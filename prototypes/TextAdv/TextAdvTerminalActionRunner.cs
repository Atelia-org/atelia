using Atelia.StateJournal;

namespace Atelia.TextAdv;

internal sealed record TerminalActionRequest(
    string ActionKind,
    string ActionSummary,
    string? ActionPayload,
    string PreActionReason
);

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

    private readonly ValidateActionAsyncDelegate _validateAsync;

    internal static TextAdvTerminalActionRunner Default { get; } = new(GameActionValidator.ValidateActionAsync);

    internal TextAdvTerminalActionRunner(ValidateActionAsyncDelegate validateAsync) {
        _validateAsync = validateAsync ?? throw new ArgumentNullException(nameof(validateAsync));
    }

    internal async Task<TerminalActionRunResult> RunLargeActionAsync(
        TextAdvSession session,
        TerminalActionRequest request,
        Func<DurableDict<string>, GameActionValidator.ValidationResult, CancellationToken, Task<AsyncAteliaResult<TurnResolution>>> resolveAsync,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(resolveAsync);

        var validationResult = await ValidateAsync(session, request, cancellationToken).ConfigureAwait(false);
        if (validationResult.TerminalResult is not null) { return validationResult.TerminalResult; }
        var validation = validationResult.Validation!;

        var collectedResult = await TryCollectLargeActionInsteadOfResolvingAsync(
            session,
            request,
            validation.Feedback,
            cancellationToken
        ).ConfigureAwait(false);
        if (collectedResult is not null) { return collectedResult; }

        var resolutionResult = await resolveAsync(session.Root, validation, cancellationToken).ConfigureAwait(false);
        if (!resolutionResult.TryGetValue(out var resolution) || resolution is null) {
            return new TerminalActionRunResult.Failure(
                $"❌ Large-Action 结算失败：{request.ActionSummary}",
                resolutionResult.Error
            );
        }

        _ = session.Repo.Commit(session.Root).Value;
        return new TerminalActionRunResult.Success(
            $"✅ 你决定了：{request.ActionSummary}",
            session.RenderPerception(resolution.NextPerception)
        );
    }

    internal async Task<TerminalActionRunResult> RunImmediateActionAsync(
        TextAdvSession session,
        TerminalActionRequest request,
        Func<DurableDict<string>, GameActionValidator.ValidationResult, CancellationToken, Task<AsyncAteliaResult<SmallActionResolution>>> resolveAsync,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(resolveAsync);

        var validationResult = await ValidateAsync(session, request, cancellationToken).ConfigureAwait(false);
        if (validationResult.TerminalResult is not null) { return validationResult.TerminalResult; }
        var validation = validationResult.Validation!;

        var resolutionResult = await resolveAsync(session.Root, validation, cancellationToken).ConfigureAwait(false);
        if (!resolutionResult.TryGetValue(out var resolution) || resolution is null) {
            return new TerminalActionRunResult.Failure(
                $"❌ 小动作结算失败：{request.ActionSummary}",
                resolutionResult.Error
            );
        }

        _ = session.Repo.Commit(session.Root).Value;
        return new TerminalActionRunResult.Success(
            $"✅ 你顺手做了：{request.ActionSummary}",
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
        TerminalActionRequest request,
        string validatorFeedback,
        CancellationToken cancellationToken
    ) {
        var root = session.Root;
        if (!GameSimulation.RequiresMultiActorCollection(root)) { return null; }

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
                $"✅ 你决定了：{request.ActionSummary}",
                session.RenderPerception(resolution.NextPerception)
            );
        }

        _ = session.Repo.Commit(root).Value;
        return new TerminalActionRunResult.Success(
            $"✅ 你决定了：{request.ActionSummary}",
            "⏳ 其他同行还在行动，这一回合暂时还没完全结束。\n\n"
            + GamePresenter.RenderTurnCollectionStatus(status)
        );
    }
}
