using Atelia.StateJournal;

namespace Atelia.TextAdv;

internal static partial class GameSimulation {
    internal static AteliaResult<TurnCollectionStatus> SubmitDevLargeActionForActor(
        DurableDict<string> root,
        string actorId,
        string actionKind,
        string actionSummary,
        string? actionPayload,
        string preActionReason
    ) {
        return SubmitLargeActionForActor(
            root,
            actorId,
            actionKind,
            actionSummary,
            actionPayload,
            preActionReason,
            validatorFeedback: "dev-submit-large-action bypassed validator"
        );
    }

    internal static bool RequiresMultiActorCollection(DurableDict<string> root)
        => EnumerateActiveActorIds(root).Count() > 1;

    internal static AteliaResult<TurnCollectionStatus> SubmitLargeActionForActor(
        DurableDict<string> root,
        string actorId,
        string actionKind,
        string actionSummary,
        string? actionPayload,
        string preActionReason,
        string validatorFeedback
    ) {
        actorId = NormalizeRequired(actorId, nameof(actorId));
        actionKind = NormalizeRequired(actionKind, nameof(actionKind));
        actionSummary = NormalizeRequired(actionSummary, nameof(actionSummary));
        preActionReason = NormalizeRequired(preActionReason, nameof(preActionReason));
        validatorFeedback = NormalizeRequired(validatorFeedback, nameof(validatorFeedback));
        actionPayload = string.IsNullOrWhiteSpace(actionPayload) ? null : actionPayload.Trim();

        var actors = GetActors(root);
        if (!actors.TryGet(actorId, out DurableDict<string>? actor) || actor is null) {
            return AteliaResult<TurnCollectionStatus>.Failure(
                new TextAdvError(
                    "TextAdv.ActorNotFound",
                    $"Actor '{actorId}' 不存在。"
                )
            );
        }

        if (actor.TryGet(ActiveKey, out bool active) && !active) {
            return AteliaResult<TurnCollectionStatus>.Failure(
                new TextAdvError(
                    "TextAdv.ActorInactive",
                    $"Actor '{actorId}' 不是 active actor，不能参与当前回合提交。"
                )
            );
        }

        _ = AppendAcceptedStepForActor(
            root,
            actorId,
            actionKind,
            actionSummary,
            actionPayload,
            preActionReason,
            validatorFeedback,
            endsTurn: true
        );

        return DescribeCurrentTurnStatus(root);
    }

    internal static async Task<AsyncAteliaResult<TurnCollectionStatus>> SubmitLargeActionsForPendingLlmPlayersAsync(
        DurableDict<string> root,
        CancellationToken cancellationToken
    ) {
        foreach (var actorId in EnumerateActiveActorIds(root).ToArray()) {
            if (string.Equals(actorId, TerminalPlayerActorId, StringComparison.Ordinal)) { continue; }
            if (HasSubmittedLargeAction(root, actorId)) { continue; }

            var actor = GetActor(root, actorId);
            var kind = actor.TryGet(KindKey, out string? rawKind) && !string.IsNullOrWhiteSpace(rawKind)
                ? rawKind
                : "npc";
            if (!string.Equals(kind, "llm-player", StringComparison.Ordinal)) { continue; }

            var result = await LlmPlayerAgentDriver.TrySubmitLargeActionAsync(root, actorId, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess) {
                return result;
            }
        }

        return AsyncAteliaResult<TurnCollectionStatus>.Success(DescribeCurrentTurnStatus(root));
    }

    internal static async Task<AsyncAteliaResult<TurnResolution>> ApplyReadyCollectedTurnAsync(
        DurableDict<string> root,
        CancellationToken cancellationToken
    ) {
        var status = DescribeCurrentTurnStatus(root);
        if (!status.AllActiveActorsSubmittedLargeAction) {
            return AsyncAteliaResult<TurnResolution>.Failure(
                new TextAdvError(
                    "TextAdv.TurnNotReadyForGm",
                    "当前回合还没有收齐所有 active actor 的 Large-Action。"
                )
            );
        }

        var intents = ReadLargeActionIntents(root);
        var terminalIntent = intents.FirstOrDefault(static intent => string.Equals(intent.ActorId, TerminalPlayerActorId, StringComparison.Ordinal));
        if (terminalIntent is null) {
            return AsyncAteliaResult<TurnResolution>.Failure(
                new TextAdvError(
                    "TextAdv.TerminalActionMissing",
                    "当前回合缺少终端玩家的 Large-Action，不能进入统一结算。"
                )
            );
        }

        try {
            var game = GetGame(root);
            var previousDay = game.GetOrThrow<int>(DayKey);
            var previousSlot = game.GetOrThrow<int>(SlotKey);
            var slotsPerDay = game.GetOrThrow<int>(SlotsPerDayKey);
            var gmContext = BuildGmCollectedTurnContext(root, intents);
            ClearLastResolutionByActor(root);
            var gmResolution = await GameMasterResolver.TryResolveCollectedTurnAsync(
                root,
                gmContext,
                cancellationToken
            ).ConfigureAwait(false);

            if (gmResolution is { UsedLlm: true }) {
                var nextClock = AdvanceClock(root);
                var resolutionSummary = AppendClockAdvance(
                    gmResolution.Summary,
                    previousDay,
                    previousSlot,
                    nextClock.Day,
                    nextClock.Slot,
                    slotsPerDay
                );

                AppendClockAdvanceToExistingActorResolutions(
                    root,
                    previousDay,
                    previousSlot,
                    nextClock.Day,
                    nextClock.Slot,
                    slotsPerDay
                );
                return AsyncAteliaResult<TurnResolution>.Success(
                    CompleteTurn(root, resolutionSummary, ActorResolutionCommitMode.PreserveExistingAndFillMissing)
                );
            }

            var lead = BuildCollectedTurnLead(intents, gmResolution?.FallbackReason);
            return terminalIntent.ActionKind switch {
                "large/rest-a-while" => AsyncAteliaResult<TurnResolution>.Success(ResolveRestAccepted(root, collectedTurnLead: lead)),
                "large/explore" => await ResolveExploreAcceptedAsync(
                    root,
                    ParseRequiredPayloadValue(terminalIntent.ActionPayload, "direction"),
                    ParseOptionalPayloadValue(terminalIntent.ActionPayload, "focus"),
                    terminalIntent.PreActionReason,
                    collectedTurnLead: lead,
                    cancellationToken
                ).ConfigureAwait(false),
                "large/interact" => await ResolveInteractionAcceptedAsync(
                    root,
                    ParseRequiredPayloadValue(terminalIntent.ActionPayload, "interactionId"),
                    terminalIntent.PreActionReason,
                    collectedTurnLead: lead,
                    cancellationToken
                ).ConfigureAwait(false),
                _ => AsyncAteliaResult<TurnResolution>.Failure(
                    new TextAdvError(
                        "TextAdv.UnsupportedCollectedAction",
                        $"当前 MVP 尚不支持统一结算 Large-Action '{terminalIntent.ActionKind}'。"
                    )
                )
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) {
            return AsyncAteliaResult<TurnResolution>.Failure(
                new TextAdvError(
                    "TextAdv.InvalidCollectedActionPayload",
                    ex.Message
                )
            );
        }
    }

    internal static AteliaResult<string> CreateLlmPlayerActor(
        DurableDict<string> root,
        string actorId,
        string name,
        string profileNote,
        string? locationId
    ) {
        actorId = NormalizeRequired(actorId, nameof(actorId));
        name = NormalizeRequired(name, nameof(name));
        profileNote = NormalizeRequired(profileNote, nameof(profileNote));
        locationId = string.IsNullOrWhiteSpace(locationId) ? GetActorLocationId(root, TerminalPlayerActorId) : locationId.Trim();

        _ = GetLocation(root, locationId);
        var actors = GetActors(root);
        if (actors.TryGet(actorId, out DurableDict<string>? _)) {
            return AteliaResult<string>.Failure(
                new TextAdvError(
                    "TextAdv.ActorAlreadyExists",
                    $"Actor '{actorId}' 已存在。"
                )
            );
        }

        var actor = CreateActor(
            root.Revision,
            kind: "llm-player",
            name,
            profileNote,
            locationId,
            active: true
        );
        actor.Upsert(MemoryNotebookKey, CreateNotebookText(root.Revision, string.Empty));
        actors.Upsert(actorId, actor);

        var game = GetGame(root);
        var activeActorIds = game.GetOrThrow<DurableDict<string>>(ActiveActorIdsKey)!;
        activeActorIds.Upsert(actorId, actorId);
        return actorId;
    }

    internal static AteliaResult<PerceptionBundle> MovePlayer(DurableDict<string> root, string direction) {
        var currentLocationId = GetActorLocationId(root, TerminalPlayerActorId);
        var currentLocation = GetLocation(root, currentLocationId);
        var exits = currentLocation.GetOrThrow<DurableDict<string>>(ExitsKey)!;

        if (!exits.TryGet(direction, out string? targetLocationId)
            || string.IsNullOrWhiteSpace(targetLocationId)) {
            var available = string.Join(", ",
                EnumerateExits(root, currentLocationId)
                    .Select(exit => $"{exit.Direction} → {exit.TargetName}")
            );
            var currentName = currentLocation.GetOrThrow<string>(NameKey)!;
            return AteliaResult<PerceptionBundle>.Failure(
                new TextAdvError("TextAdv.InvalidDirection",
                    $"「{currentName}」没有通往「{direction}」的出口。可用的出口: {available}"
                )
            );
        }

        _ = GetLocation(root, targetLocationId);
        UpsertActorLocation(root, TerminalPlayerActorId, targetLocationId);
        return DescribeCurrentPerception(root);
    }

    internal static async Task<AsyncAteliaResult<TurnResolution>> ApplyExploreAsync(
        DurableDict<string> root,
        string direction,
        string? focus,
        string preActionReason,
        string validatorFeedback,
        CancellationToken cancellationToken
    ) {
        direction = NormalizeRequired(direction, nameof(direction));
        focus = string.IsNullOrWhiteSpace(focus) ? null : focus.Trim();

        var actionSummary = focus is null
            ? $"向 {direction} 探索"
            : $"向 {direction} 探索：{focus}";
        var actionPayload = BuildExplorePayload(direction, focus);

        AppendAcceptedStep(
            root,
            actionKind: "large/explore",
            actionSummary,
            actionPayload,
            preActionReason,
            validatorFeedback,
            endsTurn: true
        );

        return await ResolveExploreAcceptedAsync(
            root,
            direction,
            focus,
            preActionReason,
            collectedTurnLead: null,
            cancellationToken
        ).ConfigureAwait(false);
    }

    internal static async Task<AsyncAteliaResult<TurnResolution>> ApplyInteractionAsync(
        DurableDict<string> root,
        string interactionId,
        string preActionReason,
        string validatorFeedback,
        CancellationToken cancellationToken
    ) {
        var interactionResult = TryGetVisibleInteraction(DescribeCurrentPerception(root), interactionId);
        if (!interactionResult.TryGetValue(out var interaction) || interaction is null) {
            return AsyncAteliaResult<TurnResolution>.Failure(interactionResult.Error!);
        }

        AppendAcceptedStep(
            root,
            actionKind: "large/interact",
            actionSummary: $"{interaction.VisibleLabel} ({interaction.ActionKind})",
            actionPayload: BuildInteractionPayload(interaction),
            preActionReason,
            validatorFeedback,
            endsTurn: true
        );

        return await ResolveInteractionAcceptedAsync(
            root,
            interactionId,
            preActionReason,
            collectedTurnLead: null,
            cancellationToken
        ).ConfigureAwait(false);
    }

    internal static PerceptionBundle ApplyNotebookEdit(
        DurableDict<string> root,
        NotebookEditProposal proposal,
        string preActionReason,
        string validatorFeedback
    ) {
        return ApplyNotebookEditForActor(
            root,
            TerminalPlayerActorId,
            proposal,
            preActionReason,
            validatorFeedback
        );
    }

    internal static PerceptionBundle ApplyNotebookEditForActor(
        DurableDict<string> root,
        string actorId,
        NotebookEditProposal proposal,
        string preActionReason,
        string validatorFeedback
    ) {
        actorId = NormalizeRequired(actorId, nameof(actorId));
        var notebook = GetNotebook(root, actorId);
        GameNotebookEditService.ApplyOrThrow(notebook, proposal);

        AppendAcceptedStepForActor(
            root,
            actorId,
            actionKind: "small/edit-memory-notebook",
            actionSummary: proposal.ActionSummary,
            actionPayload: proposal.CanonicalScriptXml,
            preActionReason,
            validatorFeedback,
            endsTurn: false
        );

        return DescribePerceptionForActor(root, actorId);
    }

    internal static TurnResolution ApplyRestAWhile(
        DurableDict<string> root,
        string preActionReason,
        string validatorFeedback
    ) {
        const string actionSummary = "原地休息一会";

        AppendAcceptedStep(
            root,
            actionKind: "large/rest-a-while",
            actionSummary,
            actionPayload: null,
            preActionReason,
            validatorFeedback,
            endsTurn: true
        );

        return ResolveRestAccepted(root, collectedTurnLead: null);
    }

    private static async Task<AsyncAteliaResult<TurnResolution>> ResolveExploreAcceptedAsync(
        DurableDict<string> root,
        string direction,
        string? focus,
        string preActionReason,
        string? collectedTurnLead,
        CancellationToken cancellationToken
    ) {
        direction = NormalizeRequired(direction, nameof(direction));
        focus = string.IsNullOrWhiteSpace(focus) ? null : focus.Trim();

        var currentLocationId = GetActorLocationId(root, TerminalPlayerActorId);
        var currentLocation = GetLocation(root, currentLocationId);
        var currentLocationName = currentLocation.GetOrThrow<string>(NameKey)!;
        var currentExits = currentLocation.GetOrThrow<DurableDict<string>>(ExitsKey)!;
        var game = GetGame(root);
        var previousDay = game.GetOrThrow<int>(DayKey);
        var previousSlot = game.GetOrThrow<int>(SlotKey);
        var slotsPerDay = game.GetOrThrow<int>(SlotsPerDayKey);
        var gmResolution = await GameMasterResolver.TryResolveExploreAsync(
            root,
            new GmExploreContext(
                DescribeCurrentPerception(root),
                currentLocationId,
                direction,
                focus,
                preActionReason,
                TryGetReverseDirection(direction)
            ),
            cancellationToken
        ).ConfigureAwait(false);

        if (gmResolution is { UsedLlm: true }
            && !string.Equals(GetActorLocationId(root, TerminalPlayerActorId), currentLocationId, StringComparison.Ordinal)) {
            var llmNextClock = AdvanceClock(root);
            var llmResolutionSummary = AppendClockAdvance(
                gmResolution.Summary,
                previousDay,
                previousSlot,
                llmNextClock.Day,
                llmNextClock.Slot,
                slotsPerDay
            );
            llmResolutionSummary = PrefixCollectedTurnLead(collectedTurnLead, llmResolutionSummary);

            return AsyncAteliaResult<TurnResolution>.Success(
                CompleteTurn(root, llmResolutionSummary, ActorResolutionCommitMode.ReplaceAllWithSummary)
            );
        }

        var gmTools = new GmWorldEditService(root);
        var createdNewLocation = false;
        string targetLocationId;

        if (currentExits.TryGet(direction, out string? existingTargetLocationId)
            && !string.IsNullOrWhiteSpace(existingTargetLocationId)) {
            targetLocationId = existingTargetLocationId;
        }
        else {
            targetLocationId = CreateExplorationLocationId(root, currentLocationId, direction);
            var targetName = CreateExplorationLocationName(direction, focus);
            var targetDescription = CreateExplorationLocationDescription(currentLocationName, direction, focus);

            var createResult = gmTools.CreateLocation(targetLocationId, targetName, targetDescription);
            if (!createResult.IsSuccess) { return AsyncAteliaResult<TurnResolution>.Failure(createResult.Error!); }

            var linkResult = gmTools.LinkLocations(
                currentLocationId,
                direction,
                targetLocationId,
                TryGetReverseDirection(direction)
            );
            if (!linkResult.IsSuccess) { return AsyncAteliaResult<TurnResolution>.Failure(linkResult.Error!); }

            createdNewLocation = true;
        }

        var moveResult = gmTools.MoveActorTo(TerminalPlayerActorId, targetLocationId);
        if (!moveResult.IsSuccess) { return AsyncAteliaResult<TurnResolution>.Failure(moveResult.Error!); }

        var targetLocation = GetLocation(root, targetLocationId);
        var targetLocationName = targetLocation.GetOrThrow<string>(NameKey)!;
        var nextClock = AdvanceClock(root);
        var discoveryText = createdNewLocation
            ? $"GM 账本新增了地点「{targetLocationName}」，并记录了从「{currentLocationName}」向 {direction} 的出口。"
            : $"你沿着已知出口从「{currentLocationName}」向 {direction} 前进，来到「{targetLocationName}」。";
        var resolutionSummary = AppendClockAdvance(
            discoveryText,
            previousDay,
            previousSlot,
            nextClock.Day,
            nextClock.Slot,
            slotsPerDay
        );
        resolutionSummary = PrefixCollectedTurnLead(collectedTurnLead, resolutionSummary);

        return AsyncAteliaResult<TurnResolution>.Success(
            CompleteTurn(root, resolutionSummary, ActorResolutionCommitMode.ReplaceAllWithSummary)
        );
    }

    private static async Task<AsyncAteliaResult<TurnResolution>> ResolveInteractionAcceptedAsync(
        DurableDict<string> root,
        string interactionId,
        string preActionReason,
        string? collectedTurnLead,
        CancellationToken cancellationToken
    ) {
        var startingPerception = DescribeCurrentPerception(root);
        var interactionResult = TryGetVisibleInteraction(startingPerception, interactionId);
        if (!interactionResult.TryGetValue(out var interaction) || interaction is null) {
            return AsyncAteliaResult<TurnResolution>.Failure(interactionResult.Error!);
        }

        var game = GetGame(root);
        var previousDay = game.GetOrThrow<int>(DayKey);
        var previousSlot = game.GetOrThrow<int>(SlotKey);
        var slotsPerDay = game.GetOrThrow<int>(SlotsPerDayKey);
        var gmResolution = await GameMasterResolver.TryResolveInteractionAsync(
            root,
            new GmInteractionContext(
                DescribeCurrentPerception(root),
                GetActorLocationId(root, TerminalPlayerActorId),
                interaction,
                preActionReason
            ),
            cancellationToken
        ).ConfigureAwait(false);

        var nextClock = AdvanceClock(root);
        var summary = gmResolution is { UsedLlm: true }
            ? gmResolution.Summary
            : BuildDeterministicInteractionSummary(interaction);
        var resolutionSummary = AppendClockAdvance(
            summary,
            previousDay,
            previousSlot,
            nextClock.Day,
            nextClock.Slot,
            slotsPerDay
        );
        resolutionSummary = PrefixCollectedTurnLead(collectedTurnLead, resolutionSummary);

        return AsyncAteliaResult<TurnResolution>.Success(
            CompleteTurn(root, resolutionSummary, ActorResolutionCommitMode.ReplaceAllWithSummary)
        );
    }

    private static TurnResolution ResolveRestAccepted(DurableDict<string> root, string? collectedTurnLead) {
        var game = GetGame(root);
        var previousDay = game.GetOrThrow<int>(DayKey);
        var previousSlot = game.GetOrThrow<int>(SlotKey);
        var slotsPerDay = game.GetOrThrow<int>(SlotsPerDayKey);
        var nextClock = AdvanceClock(root);
        var resolutionSummary =
            $"你原地休息了一会。当前原型只推进时钟，不结算更复杂的世界后果。"
            + $" 时间从 {GameClock.FormatClock(previousDay, previousSlot, slotsPerDay)} 前进到 {GameClock.FormatClock(nextClock.Day, nextClock.Slot, slotsPerDay)}。";
        resolutionSummary = PrefixCollectedTurnLead(collectedTurnLead, resolutionSummary);

        return CompleteTurn(root, resolutionSummary, ActorResolutionCommitMode.ReplaceAllWithSummary);
    }

    private static void SetLastResolutionForActiveActors(DurableDict<string> root, string summary) {
        summary = NormalizeRequired(summary, nameof(summary));
        var game = GetGame(root);
        var lastResolutionByActor = root.Revision.CreateDict<string>();
        foreach (var actorId in EnumerateActiveActorIds(root)) {
            lastResolutionByActor.Upsert(actorId, summary);
        }

        game.Upsert(LastResolutionByActorKey, lastResolutionByActor);
    }

    private static void SetLastResolutionForMissingActiveActors(DurableDict<string> root, string fallbackSummary) {
        fallbackSummary = NormalizeRequired(fallbackSummary, nameof(fallbackSummary));
        var lastResolutionByActor = GetOrCreateLastResolutionByActor(root);
        foreach (var actorId in EnumerateActiveActorIds(root)) {
            if (!lastResolutionByActor.TryGet(actorId, out string? actorResolution)
                || string.IsNullOrWhiteSpace(actorResolution)) {
                lastResolutionByActor.Upsert(actorId, fallbackSummary);
            }
        }
    }

    private static void AppendClockAdvanceToExistingActorResolutions(
        DurableDict<string> root,
        int previousDay,
        int previousSlot,
        int nextDay,
        int nextSlot,
        int slotsPerDay
    ) {
        var lastResolutionByActor = GetOrCreateLastResolutionByActor(root);
        foreach (var actorId in EnumerateActiveActorIds(root)) {
            if (!lastResolutionByActor.TryGet(actorId, out string? actorResolution)
                || string.IsNullOrWhiteSpace(actorResolution)) {
                continue;
            }

            lastResolutionByActor.Upsert(
                actorId,
                AppendClockAdvance(
                    actorResolution,
                    previousDay,
                    previousSlot,
                    nextDay,
                    nextSlot,
                    slotsPerDay
                )
            );
        }
    }

    private static TurnStep AppendAcceptedStep(
        DurableDict<string> root,
        string actionKind,
        string actionSummary,
        string? actionPayload,
        string preActionReason,
        string validatorFeedback,
        bool endsTurn
    ) {
        return AppendAcceptedStepForActor(
            root,
            TerminalPlayerActorId,
            actionKind,
            actionSummary,
            actionPayload,
            preActionReason,
            validatorFeedback,
            endsTurn
        );
    }

    private static TurnStep AppendAcceptedStepForActor(
        DurableDict<string> root,
        string actorId,
        string actionKind,
        string actionSummary,
        string? actionPayload,
        string preActionReason,
        string validatorFeedback,
        bool endsTurn
    ) {
        var currentTurn = GetCurrentTurn(root);
        var acceptedSteps = GetAcceptedStepsForActor(root, actorId, createIfMissing: true)!;
        var stepNumber = currentTurn.GetOrThrow<int>(NextStepNumberKey);
        var stepId = $"step-{stepNumber:D4}";
        var step = root.Revision.CreateDict<string>();

        step.Upsert(ActionKindKey, actionKind);
        step.Upsert(ActionSummaryKey, actionSummary);
        if (actionPayload is not null) {
            step.Upsert(ActionPayloadKey, actionPayload);
        }

        step.Upsert(PreActionReasonKey, preActionReason);
        step.Upsert(ValidatorFeedbackKey, validatorFeedback);
        step.Upsert(EndsTurnKey, endsTurn);

        acceptedSteps.Upsert(stepId, step);
        currentTurn.Upsert(NextStepNumberKey, stepNumber + 1);

        var turnStep = new TurnStep(stepNumber, actionKind, actionSummary, actionPayload, preActionReason, validatorFeedback, endsTurn);
        if (endsTurn) {
            RecordLargeActionForActor(root, actorId, turnStep);
        }

        return turnStep;
    }

    private static void ArchiveCompletedTurn(DurableDict<string> root, string resolutionSummary) {
        var rev = root.Revision;
        var game = GetGame(root);
        var currentTurn = GetCurrentTurn(root);
        var turnHistory = game.GetOrThrow<DurableDict<string>>(TurnHistoryKey)!;
        var completedTurnCount = game.GetOrThrow<int>(CompletedTurnCountKey);
        var turnNumber = completedTurnCount + 1;
        var archivedTurn = rev.CreateDict<string>();

        archivedTurn.Upsert(TurnNumberKey, turnNumber);
        archivedTurn.Upsert(StartDayKey, currentTurn.GetOrThrow<int>(StartDayKey));
        archivedTurn.Upsert(StartSlotKey, currentTurn.GetOrThrow<int>(StartSlotKey));
        archivedTurn.Upsert(StartLocationIdKey, currentTurn.GetOrThrow<string>(StartLocationIdKey)!);
        archivedTurn.Upsert(NotebookSnapshotKey, currentTurn.GetOrThrow<string>(NotebookSnapshotKey)!);
        archivedTurn.Upsert(AcceptedStepsByActorKey, currentTurn.GetOrThrow<DurableDict<string>>(AcceptedStepsByActorKey)!);
        archivedTurn.Upsert(LargeActionByActorKey, currentTurn.GetOrThrow<DurableDict<string>>(LargeActionByActorKey)!);
        archivedTurn.Upsert(ResolutionSummaryKey, resolutionSummary);
        archivedTurn.Upsert(LastResolutionByActorKey, GetOrCreateLastResolutionByActor(root));
        archivedTurn.Upsert(EndingNotebookKey, GetNotebookContent(GetNotebookSnapshot(root)));

        turnHistory.Upsert($"turn-{turnNumber:D4}", archivedTurn);
        game.Upsert(CompletedTurnCountKey, turnNumber);
    }

    private static TurnResolution CompleteTurn(
        DurableDict<string> root,
        string resolutionSummary,
        ActorResolutionCommitMode actorResolutionMode
    ) {
        switch (actorResolutionMode) {
            case ActorResolutionCommitMode.ReplaceAllWithSummary:
                SetLastResolutionForActiveActors(root, resolutionSummary);
                break;
            case ActorResolutionCommitMode.PreserveExistingAndFillMissing:
                SetLastResolutionForMissingActiveActors(root, resolutionSummary);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(actorResolutionMode), actorResolutionMode, "Unknown actor resolution commit mode.");
        }

        ArchiveCompletedTurn(root, resolutionSummary);
        ResetCurrentTurn(root);
        return new TurnResolution(resolutionSummary, DescribeCurrentPerception(root));
    }

    private static (int Day, int Slot) AdvanceClock(DurableDict<string> root) {
        var game = GetGame(root);
        var day = game.GetOrThrow<int>(DayKey);
        var slot = game.GetOrThrow<int>(SlotKey);
        var slotsPerDay = game.GetOrThrow<int>(SlotsPerDayKey);
        var nextClock = GameClock.PreviewNextClock(day, slot, slotsPerDay);

        game.Upsert(DayKey, nextClock.Day);
        game.Upsert(SlotKey, nextClock.Slot);
        return nextClock;
    }

    private static void ResetCurrentTurn(DurableDict<string> root) {
        var game = GetGame(root);
        var day = game.GetOrThrow<int>(DayKey);
        var slot = game.GetOrThrow<int>(SlotKey);
        var notebookSnapshot = GetNotebookSnapshot(root);
        game.Upsert(
            CurrentTurnKey,
            CreateCurrentTurnState(
                root.Revision,
                day,
                slot,
                GetActorLocationId(root, TerminalPlayerActorId),
                GetNotebookContent(notebookSnapshot)
            )
        );
    }

    private static void RecordLargeActionForActor(DurableDict<string> root, string actorId, TurnStep step) {
        var currentTurn = GetCurrentTurn(root);
        var largeActionByActor = currentTurn.GetOrThrow<DurableDict<string>>(LargeActionByActorKey)!;
        var action = root.Revision.CreateDict<string>();

        action.Upsert(SourceStepNumberKey, step.StepNumber);
        action.Upsert(ActionKindKey, step.ActionKind);
        action.Upsert(ActionSummaryKey, step.ActionSummary);
        if (step.ActionPayload is not null) {
            action.Upsert(ActionPayloadKey, step.ActionPayload);
        }

        action.Upsert(PreActionReasonKey, step.PreActionReason);
        action.Upsert(ValidatorFeedbackKey, step.ValidatorFeedback);
        largeActionByActor.Upsert(actorId, action);
    }

    private static bool HasSubmittedLargeAction(DurableDict<string> root, string actorId) {
        var currentTurn = GetCurrentTurn(root);
        var largeActionByActor = currentTurn.GetOrThrow<DurableDict<string>>(LargeActionByActorKey)!;
        return largeActionByActor.TryGet(actorId, out DurableDict<string>? action) && action is not null;
    }

    private static IReadOnlyList<LargeActionIntent> ReadLargeActionIntents(DurableDict<string> root) {
        var currentTurn = GetCurrentTurn(root);
        var largeActionByActor = currentTurn.GetOrThrow<DurableDict<string>>(LargeActionByActorKey)!;
        return largeActionByActor.Keys
            .OrderBy(static key => key, StringComparer.Ordinal)
            .Select(actorId => {
                var actor = GetActor(root, actorId);
                var action = largeActionByActor.GetOrThrow<DurableDict<string>>(actorId)!;
                _ = action.TryGet(ActionPayloadKey, out string? actionPayload);
                var actorName = actor.TryGet(NameKey, out string? rawName) && !string.IsNullOrWhiteSpace(rawName)
                    ? rawName
                    : actorId;
                var actorKind = actor.TryGet(KindKey, out string? rawKind) && !string.IsNullOrWhiteSpace(rawKind)
                    ? rawKind
                    : "npc";

                return new LargeActionIntent(
                    actorId,
                    actorName,
                    actorKind,
                    action.GetOrThrow<string>(ActionKindKey)!,
                    action.GetOrThrow<string>(ActionSummaryKey)!,
                    actionPayload,
                    action.GetOrThrow<string>(PreActionReasonKey)!,
                    action.GetOrThrow<string>(ValidatorFeedbackKey)!
                );
            })
            .ToArray();
    }

    private static GmCollectedTurnContext BuildGmCollectedTurnContext(
        DurableDict<string> root,
        IReadOnlyList<LargeActionIntent> intents
    ) {
        return new GmCollectedTurnContext(
            TerminalPlayerActorId,
            intents
                .Select(intent => new GmCollectedTurnIntent(
                    intent.ActorId,
                    intent.ActorName,
                    intent.ActorKind,
                    intent.ActionKind,
                    intent.ActionSummary,
                    intent.ActionPayload,
                    intent.PreActionReason,
                    intent.ValidatorFeedback,
                    DescribePerceptionForActor(root, intent.ActorId)
                ))
                .ToArray()
        );
    }

    private static string BuildCollectedTurnLead(IReadOnlyList<LargeActionIntent> intents, string? fallbackReason) {
        var lines = new List<string>
        {
            "本回合采用多主体同步收集："
        };

        foreach (var intent in intents) {
            lines.Add($"- {intent.ActorName} [{intent.ActorId}, {intent.ActorKind}]：{intent.ActionSummary} ({intent.ActionKind})");
        }

        lines.Add(
            string.IsNullOrWhiteSpace(fallbackReason)
                ? "当前 deterministic fallback 先按终端玩家的大型动作推进世界；真实 GM Agent 启用时会尝试统一裁决所有意图。"
                : $"真实多主体 GM Agent 未完成，本回合回退到终端玩家主导的 deterministic 结算。原因：{fallbackReason}"
        );
        return string.Join("\n", lines);
    }

    private static string PrefixCollectedTurnLead(string? collectedTurnLead, string summary) {
        if (string.IsNullOrWhiteSpace(collectedTurnLead)) { return summary; }
        return $"{collectedTurnLead.Trim()}\n\n{summary}";
    }

    private static string ParseRequiredPayloadValue(string? payload, string key) {
        var value = ParseOptionalPayloadValue(payload, key);
        if (string.IsNullOrWhiteSpace(value)) {
            throw new InvalidOperationException($"Large-Action payload 缺少必填字段 '{key}'。");
        }

        return value;
    }

    private static string? ParseOptionalPayloadValue(string? payload, string key) {
        if (string.IsNullOrWhiteSpace(payload)) { return null; }

        foreach (var line in payload.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')) {
            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0) { continue; }
            var actualKey = line[..separatorIndex].Trim();
            if (!string.Equals(actualKey, key, StringComparison.Ordinal)) { continue; }

            var value = line[(separatorIndex + 1)..].Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    private static bool HaveAllActiveActorsSubmittedLargeAction(
        DurableDict<string> root,
        DurableDict<string> largeActionByActor
    ) {
        var activeActorIds = EnumerateActiveActorIds(root).ToArray();
        return activeActorIds.Length > 0
            && activeActorIds.All(actorId => largeActionByActor.TryGet(actorId, out DurableDict<string>? action) && action is not null);
    }

    private static string BuildExplorePayload(string direction, string? focus)
        => focus is null
            ? $"direction={direction}"
            : $"direction={direction}\nfocus={focus}";

    private static string BuildDeterministicInteractionSummary(InteractionPerception interaction) {
        if (!string.IsNullOrWhiteSpace(interaction.EffectNote)) {
            return interaction.EffectNote!;
        }

        return $"你执行了「{interaction.VisibleLabel}」。当前原型只推进时钟；更具体的后果需要真实 GM Agent 或后续规则工具结算。";
    }

    private static string AppendClockAdvance(
        string summary,
        int previousDay,
        int previousSlot,
        int nextDay,
        int nextSlot,
        int slotsPerDay
    ) {
        var trimmed = string.IsNullOrWhiteSpace(summary) ? "本回合探索已经完成。" : summary.Trim();
        return $"{trimmed} 时间从 {GameClock.FormatClock(previousDay, previousSlot, slotsPerDay)}"
            + $" 前进到 {GameClock.FormatClock(nextDay, nextSlot, slotsPerDay)}。";
    }

    private static string CreateExplorationLocationId(DurableDict<string> root, string currentLocationId, string direction) {
        var baseId = $"{Slugify(currentLocationId)}-{Slugify(direction)}";
        var candidate = baseId;
        var index = 1;
        while (LocationExists(root, candidate)) {
            candidate = $"{baseId}-{index:D2}";
            index++;
        }

        return candidate;
    }

    private static string CreateExplorationLocationName(string direction, string? focus) {
        if (!string.IsNullOrWhiteSpace(focus)) { return focus.Trim(); }

        return TryGetDirectionDisplayName(direction) is { } display
            ? $"{display}的未知区域"
            : $"{direction} 方向的未知区域";
    }

    private static string CreateExplorationLocationDescription(string fromLocationName, string direction, string? focus) {
        var targetText = string.IsNullOrWhiteSpace(focus)
            ? "一处刚被确认的新区域"
            : $"你原本想寻找的「{focus.Trim()}」";
        return $"这是你从「{fromLocationName}」向 {direction} 探索时确认的地点。"
            + $"{targetText}已经被记录进世界账本；当前只掌握入口附近的轮廓，更多细节仍需要后续观察。";
    }

    private static string? TryGetReverseDirection(string direction) {
        return direction.Trim().ToLowerInvariant() switch {
            "north" or "n" => "south",
            "south" or "s" => "north",
            "east" or "e" => "west",
            "west" or "w" => "east",
            "up" => "down",
            "down" => "up",
            "inside" or "in" => "outside",
            "outside" or "out" => "inside",
            "northeast" or "ne" => "southwest",
            "northwest" or "nw" => "southeast",
            "southeast" or "se" => "northwest",
            "southwest" or "sw" => "northeast",
            _ => null
        };
    }

    private static string? TryGetDirectionDisplayName(string direction) {
        return direction.Trim().ToLowerInvariant() switch {
            "north" or "n" => "北侧",
            "south" or "s" => "南侧",
            "east" or "e" => "东侧",
            "west" or "w" => "西侧",
            "up" => "上方",
            "down" => "下方",
            "inside" or "in" => "内部",
            "outside" or "out" => "外侧",
            "northeast" or "ne" => "东北侧",
            "northwest" or "nw" => "西北侧",
            "southeast" or "se" => "东南侧",
            "southwest" or "sw" => "西南侧",
            _ => null
        };
    }

    private static string Slugify(string text) {
        var normalized = text.Trim().ToLowerInvariant();
        var chars = normalized
            .Select(static ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();
        var slug = string.Join(
            "-",
            new string(chars)
                .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        );

        return string.IsNullOrWhiteSpace(slug) ? "location" : slug;
    }
}
