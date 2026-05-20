using Atelia.StateJournal;

namespace Atelia.TextAdv;

internal static partial class GameSimulation {
    private static readonly (string ActorId, string Name, string ProfileNote)[] DiagnosticLlmPlayerTemplates =
    [
        (
            "diagnostic-llm-01",
            "林边观察者",
            "一个谨慎的内部玩家，倾向于观察树林、边界和可疑变化；会把不确定性写进私人记忆。"
        ),
        (
            "diagnostic-llm-02",
            "潮汐记录者",
            "一个关注海岸、时间和资源节律的内部玩家；会优先寻找可复核的生存线索。"
        ),
        (
            "diagnostic-llm-03",
            "火种守望者",
            "一个重视庇护、火源和安全边界的内部玩家；会在冒险和保守之间权衡。"
        ),
        (
            "diagnostic-llm-04",
            "沉默采集者",
            "一个少言但有行动欲的内部玩家；会尝试寻找可带走、可使用或可交换的对象。"
        ),
    ];

    private static readonly string[] DiagnosticExploreDirections =
    [
        "north",
        "east",
        "south",
        "west",
        "inside",
        "up",
        "down",
    ];

    internal static AteliaResult<IReadOnlyList<string>> EnsureDiagnosticLlmPlayers(
        DurableDict<string> root,
        int targetCount
    ) {
        if (targetCount < 0) {
            return AteliaResult<IReadOnlyList<string>>.Failure(
                new TextAdvError(
                    "TextAdv.InvalidDiagnosticActorCount",
                    "diagnostic LLM player 数量不能小于 0。"
                )
            );
        }

        var ensured = new List<string>();
        var activeDiagnosticLlmPlayerCount = CountActiveDiagnosticLlmPlayers(root);
        for (var templateIndex = 0;
             activeDiagnosticLlmPlayerCount < targetCount && templateIndex < DiagnosticLlmPlayerTemplates.Length;
             templateIndex++) {
            var template = DiagnosticLlmPlayerTemplates[templateIndex];
            if (ActorExists(root, template.ActorId)) { continue; }

            var createResult = CreateLlmPlayerActor(
                root,
                template.ActorId,
                template.Name,
                template.ProfileNote,
                locationId: null
            );
            if (!createResult.TryGetValue(out var actorId) || string.IsNullOrWhiteSpace(actorId)) { return AteliaResult<IReadOnlyList<string>>.Failure(createResult.Error!); }

            ensured.Add(actorId);
            activeDiagnosticLlmPlayerCount++;
        }

        if (activeDiagnosticLlmPlayerCount < targetCount) {
            return AteliaResult<IReadOnlyList<string>>.Failure(
                new TextAdvError(
                    "TextAdv.NotEnoughDiagnosticActorTemplates",
                    $"当前内置 diagnostic LLM player 模板最多支持 {DiagnosticLlmPlayerTemplates.Length} 个，无法补足到 {targetCount} 个。"
                )
            );
        }

        return ensured;
    }

    internal static async Task<AsyncAteliaResult<AutonomousRoundReport>> RunDiagnosticAutonomousRoundAsync(
        DurableDict<string> root,
        int roundNumber,
        bool useRealAgents,
        CancellationToken cancellationToken
    ) {
        if (roundNumber <= 0) {
            return AsyncAteliaResult<AutonomousRoundReport>.Failure(
                new TextAdvError(
                    "TextAdv.InvalidDiagnosticRoundNumber",
                    "诊断回合编号必须大于 0。"
                )
            );
        }

        var startingStatus = DescribeCurrentTurnStatus(root);
        if (startingStatus.Actors.Any(static actor => actor.HasSubmittedLargeAction)) {
            return AsyncAteliaResult<AutonomousRoundReport>.Failure(
                new TextAdvError(
                    "TextAdv.DiagnosticTurnAlreadyInProgress",
                    "当前回合已经有 actor 提交了 Large-Action；请先完成或重建世界后再运行自动诊断回合。"
                )
            );
        }

        var terminalAction = BuildDiagnosticTerminalAction(root, roundNumber);
        var submitTerminalResult = SubmitLargeActionForActor(
            root,
            TerminalPlayerActorId,
            terminalAction,
            validatorFeedback: "diagnostic autonomous runner bypassed validator"
        );
        if (!submitTerminalResult.TryGetValue(out var status) || status is null) { return AsyncAteliaResult<AutonomousRoundReport>.Failure(submitTerminalResult.Error!); }

        if (useRealAgents) {
            var submitLlmResult = await SubmitLargeActionsForPendingInternalPlayersAsync(root, cancellationToken)
                .ConfigureAwait(false);
            if (!submitLlmResult.TryGetValue(out status) || status is null) { return AsyncAteliaResult<AutonomousRoundReport>.Failure(submitLlmResult.Error!); }
        }
        else {
            var submitFallbackResult = SubmitDiagnosticFallbackActionsForPendingLlmPlayers(root);
            if (!submitFallbackResult.TryGetValue(out status) || status is null) { return AsyncAteliaResult<AutonomousRoundReport>.Failure(submitFallbackResult.Error!); }
        }

        if (!status.AllActiveActorsSubmittedLargeAction) {
            return AsyncAteliaResult<AutonomousRoundReport>.Failure(
                new TextAdvError(
                    "TextAdv.DiagnosticTurnNotReady",
                    "自动诊断回合未能收齐所有 active actor 的 Large-Action。"
                )
            );
        }

        var resolutionResult = await ApplyReadyCollectedTurnAsync(root, cancellationToken)
            .ConfigureAwait(false);
        if (!resolutionResult.TryGetValue(out var resolution) || resolution is null) { return AsyncAteliaResult<AutonomousRoundReport>.Failure(resolutionResult.Error!); }

        return AsyncAteliaResult<AutonomousRoundReport>.Success(
            new AutonomousRoundReport(
                roundNumber,
                terminalAction.ActionKind,
                terminalAction.ActionSummary,
                terminalAction.ActionPayload,
                resolution.Summary,
                DescribeCurrentTurnStatus(root)
            )
        );
    }

    private static AteliaResult<TurnCollectionStatus> SubmitDiagnosticFallbackActionsForPendingLlmPlayers(
        DurableDict<string> root
    ) {
        foreach (var actorId in EnumerateActiveActorIds(root).ToArray()) {
            if (HasSubmittedLargeAction(root, actorId)) { continue; }
            if (!IsInternallyDrivenPlayerActor(root, actorId)) { continue; }

            var perception = DescribePerceptionForActor(root, actorId);
            var submitResult = SubmitLargeActionForActor(
                root,
                actorId,
                new ActionDescriptor(
                    TerminalActionKinds.LargeRestAWhile,
                    "诊断模式：谨慎观察并暂不移动",
                    null,
                    $"deterministic diagnostic harness：当前先在「{perception.Location.Name}」保持观察，等待 GM 结算托管玩家的探索结果。"
                ),
                validatorFeedback: "diagnostic deterministic fallback bypassed validator"
            );
            if (!submitResult.IsSuccess) { return AteliaResult<TurnCollectionStatus>.Failure(submitResult.Error!); }
        }

        return DescribeCurrentTurnStatus(root);
    }

    private static ActionDescriptor BuildDiagnosticTerminalAction(DurableDict<string> root, int roundNumber) {
        var perception = DescribeCurrentPerception(root);
        var direction = DiagnosticExploreDirections[(roundNumber - 1) % DiagnosticExploreDirections.Length];
        var focus = $"诊断探索点 {roundNumber:D2}";
        var actionSummary = $"诊断托管：向 {direction} 探索 {focus}";
        var actionPayload = $"direction={direction}\nfocus={focus}";
        var preActionReason =
            $"自动诊断第 {roundNumber} 回合：我当前位于「{perception.Location.Name}」，"
            + $"为了让世界账本、GM 结算、LLM Player 行动和 actor journal 产生可观察变化，"
            + $"选择向 {direction} 探索并寻找「{focus}」。";

        return new ActionDescriptor(
            TerminalActionKinds.LargeExplore,
            actionSummary,
            actionPayload,
            preActionReason
        );
    }

    private static int CountActiveDiagnosticLlmPlayers(DurableDict<string> root) {
        return DiagnosticLlmPlayerTemplates.Count(
            template => {
                if (!IsActiveActor(root, template.ActorId)) { return false; }

                var actor = GetActor(root, template.ActorId);
                return IsInternallyDrivenPlayerActor(root, template.ActorId);
            }
        );
    }

    private static bool ActorExists(DurableDict<string> root, string actorId) {
        var actors = GetActors(root);
        return actors.TryGet(actorId, out DurableDict<string>? actor) && actor is not null;
    }

    private static bool IsActiveActor(DurableDict<string> root, string actorId) {
        var game = GetGame(root);
        var activeActorIds = game.GetOrThrow<DurableDict<string>>(ActiveActorIdsKey)!;
        return activeActorIds.TryGet(actorId, out string? activeId) && !string.IsNullOrWhiteSpace(activeId);
    }
}
