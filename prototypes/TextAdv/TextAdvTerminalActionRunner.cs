using Atelia.StateJournal;

namespace Atelia.TextAdv;

internal abstract record TerminalActionRunResult {
    internal sealed record Success(string Message, string BodyText) : TerminalActionRunResult;

    internal sealed record ValidationRejected(
        string Feedback,
        string Message = "❌ 这一步没有通过检查。"
    ) : TerminalActionRunResult;

    internal sealed record Failure(
        string Message,
        AteliaError? Error = null
    ) : TerminalActionRunResult;
}

internal sealed class TextAdvTerminalActionRunner {
    private sealed record ValidationStageResult(
        GameActionValidator.ValidationResult? Validation,
        TerminalActionRunResult? TerminalResult
    );

    internal delegate Task<GameActionValidator.ValidationResult> ValidateActionAsyncDelegate(
        PerceptionBundle perception,
        string actionKind,
        string actionSummary,
        string preActionReason,
        string? actionPayload,
        CancellationToken cancellationToken
    );

    internal delegate Task<AsyncAteliaResult<ActionResolution>> ExecutePlanAsyncDelegate(
        DurableDict<string> root,
        TerminalActionExecutionPlan plan,
        string validatorFeedback,
        CancellationToken cancellationToken
    );

    private readonly ValidateActionAsyncDelegate _validateAsync;
    private readonly ExecutePlanAsyncDelegate _executePlanAsync;

    internal static TextAdvTerminalActionRunner Default { get; } = new(
        GameActionValidator.ValidateActionAsync,
        GameSimulation.ExecuteTerminalActionPlanAsync
    );

    internal TextAdvTerminalActionRunner(
        ValidateActionAsyncDelegate validateAsync,
        ExecutePlanAsyncDelegate executePlanAsync
    ) {
        _validateAsync = validateAsync ?? throw new ArgumentNullException(nameof(validateAsync));
        _executePlanAsync = executePlanAsync ?? throw new ArgumentNullException(nameof(executePlanAsync));
    }

    internal async Task<TerminalActionRunResult> RunAsync(
        TextAdvSession session,
        TerminalActionExecutionPlan plan,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(plan);

        var validationResult = await ValidateAsync(session, plan.Request, cancellationToken).ConfigureAwait(false);
        if (validationResult.TerminalResult is not null) { return validationResult.TerminalResult; }
        var validation = validationResult.Validation!;

        if (plan.Mode == TerminalActionMode.Large) {
            var collectedResult = await TryCollectLargeActionInsteadOfResolvingAsync(
                session,
                plan,
                validation.Feedback,
                cancellationToken
            ).ConfigureAwait(false);
            if (collectedResult is not null) { return collectedResult; }
        }

        var resolutionResult = await _executePlanAsync(
            session.Root,
            plan,
            validation.Feedback,
            cancellationToken
        ).ConfigureAwait(false);
        if (!resolutionResult.TryGetValue(out var resolution) || resolution is null) {
            return new TerminalActionRunResult.Failure(
                BuildResolutionFailureMessage(plan),
                resolutionResult.Error
            );
        }

        _ = session.Repo.Commit(session.Root).Value;
        return new TerminalActionRunResult.Success(
            BuildSuccessMessage(plan),
            session.RenderPerception(resolution.NextPerception)
        );
    }

    private async Task<ValidationStageResult> ValidateAsync(
        TextAdvSession session,
        TerminalActionRequest request,
        CancellationToken cancellationToken
    ) {
        try {
            var validation = await _validateAsync(
                GameSimulation.DescribeCurrentPerception(session.Root),
                request.ActionKind,
                request.ActionSummary,
                request.PreActionReason,
                request.ActionPayload,
                cancellationToken
            ).ConfigureAwait(false);

            return validation.Accepted
                ? new ValidationStageResult(validation, null)
                : new ValidationStageResult(null, new TerminalActionRunResult.ValidationRejected(validation.Feedback));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        }
        catch (Exception ex) {
            return new ValidationStageResult(
                null,
                new TerminalActionRunResult.Failure($"❌ 动作检查失败：{ex.Message}")
            );
        }
    }

    private static async Task<TerminalActionRunResult?> TryCollectLargeActionInsteadOfResolvingAsync(
        TextAdvSession session,
        TerminalActionExecutionPlan plan,
        string validatorFeedback,
        CancellationToken cancellationToken
    ) {
        var root = session.Root;
        if (!GameSimulation.RequiresMultiActorCollection(root)) { return null; }
        var request = plan.Request;

        var result = GameSimulation.SubmitLargeActionForActor(
            root,
            actorId: GameSimulation.TerminalPlayerActorId,
            request.ActionKind,
            request.ActionSummary,
            request.ActionPayload,
            request.PreActionReason,
            validatorFeedback
        );
        if (!result.TryGetValue(out var status) || status is null) {
            return new TerminalActionRunResult.Failure("❌ 多主体回合收集失败。", result.Error);
        }

        var fallbackResult = await GameSimulation.SubmitLargeActionsForPendingInternalPlayersAsync(root, cancellationToken).ConfigureAwait(false);
        if (!fallbackResult.TryGetValue(out status) || status is null) {
            return new TerminalActionRunResult.Failure("❌ LLM Player 行动提交失败。", fallbackResult.Error);
        }

        if (status.AllActiveActorsSubmittedLargeAction) {
            var resolutionResult = await GameSimulation.ApplyReadyCollectedTurnAsync(root, cancellationToken).ConfigureAwait(false);
            if (!resolutionResult.TryGetValue(out var resolution) || resolution is null) {
                return new TerminalActionRunResult.Failure("❌ 多主体统一结算失败。", resolutionResult.Error);
            }

            _ = session.Repo.Commit(root).Value;
            return new TerminalActionRunResult.Success(
                BuildSuccessMessage(plan),
                session.RenderPerception(resolution.NextPerception)
            );
        }

        _ = session.Repo.Commit(root).Value;
        return new TerminalActionRunResult.Success(
            BuildSuccessMessage(plan),
            "⏳ 其他同行还在行动，这一回合暂时还没完全结束。\n\n"
            + GamePresenter.RenderTurnCollectionStatus(status)
        );
    }

    private static string BuildSuccessMessage(TerminalActionExecutionPlan plan) {
        return plan.Mode switch {
            TerminalActionMode.Immediate => $"✅ 你顺手做了：{plan.Request.ActionSummary}",
            TerminalActionMode.Large => $"✅ 你决定了：{plan.Request.ActionSummary}",
            _ => throw new InvalidOperationException($"Unknown terminal action mode: {plan.Mode}")
        };
    }

    private static string BuildResolutionFailureMessage(TerminalActionExecutionPlan plan) {
        return plan.Mode switch {
            TerminalActionMode.Immediate => $"❌ 小动作结算失败：{plan.Request.ActionSummary}",
            TerminalActionMode.Large => $"❌ Large-Action 结算失败：{plan.Request.ActionSummary}",
            _ => throw new InvalidOperationException($"Unknown terminal action mode: {plan.Mode}")
        };
    }
}
