using Atelia.StateJournal;

namespace Atelia.TextAdv;

internal static partial class GameSimulation {
    internal static AteliaResult<TurnCollectionStatus> SubmitDevLargeActionForActor(
        DurableDict<string> root,
        string actorId,
        ActionDescriptor descriptor
    ) {
        return SubmitLargeActionForActor(
            root,
            actorId,
            descriptor,
            validatorFeedback: "dev-submit-large-action bypassed validator"
        );
    }

    internal static bool RequiresMultiActorCollection(DurableDict<string> root)
        => EnumerateActiveActorIds(root).Count() > 1;

    internal static AteliaResult<TurnCollectionStatus> SubmitLargeActionForActor(
        DurableDict<string> root,
        string actorId,
        ActionDescriptor descriptor,
        string validatorFeedback
    ) {
        actorId = NormalizeRequired(actorId, nameof(actorId));
        descriptor = NormalizeActionDescriptor(descriptor);
        validatorFeedback = NormalizeRequired(validatorFeedback, nameof(validatorFeedback));

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
            descriptor,
            validatorFeedback,
            endsTurn: true
        );

        return DescribeCurrentTurnStatus(root);
    }

    internal static async Task<AsyncAteliaResult<TurnCollectionStatus>> SubmitLargeActionsForPendingInternalPlayersAsync(
        DurableDict<string> root,
        CancellationToken cancellationToken
    ) {
        foreach (var actorId in EnumerateActiveActorIds(root).ToArray()) {
            if (HasSubmittedLargeAction(root, actorId)) { continue; }
            if (!IsInternallyDrivenPlayerActor(root, actorId)) { continue; }

            var result = await LlmPlayerAgentDriver.TrySubmitLargeActionAsync(root, actorId, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess) { return result; }
        }

        return AsyncAteliaResult<TurnCollectionStatus>.Success(DescribeCurrentTurnStatus(root));
    }

    internal static async Task<AsyncAteliaResult<ActionResolution>> ApplyReadyCollectedTurnAsync(
        DurableDict<string> root,
        CancellationToken cancellationToken
    ) {
        var status = DescribeCurrentTurnStatus(root);
        if (!status.AllActiveActorsSubmittedLargeAction) {
            return AsyncAteliaResult<ActionResolution>.Failure(
                new TextAdvError(
                    "TextAdv.TurnNotReadyForGm",
                    "当前回合还没有收齐所有 active actor 的 Large-Action。"
                )
            );
        }

        var preludeResult = await PrepareTurnResolutionPreludeAsync(root, cancellationToken).ConfigureAwait(false);
        if (!preludeResult.TryGetValue(out var prelude) || prelude is null) { return AsyncAteliaResult<ActionResolution>.Failure(preludeResult.Error!); }

        var intents = ReadLargeActionIntents(root);
        if (intents.Count == 1) {
            try {
                return await ResolveCollectedTurnFromIntentAsync(
                    root,
                    intents[0],
                    prelude,
                    cancellationToken
                ).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) {
                return AsyncAteliaResult<ActionResolution>.Failure(
                    new TextAdvError(
                        "TextAdv.CollectedTurnSingleIntentFailed",
                        $"单意图 collected-turn 结算失败：{ex.Message}"
                    )
                );
            }
        }

        try {
            var gmContext = BuildGmCollectedTurnContext(root, intents);
            ClearLastResolutionByActor(root);
            var gmResolution = await GameMasterResolver.ResolveCollectedTurnAsync(
                root,
                gmContext,
                cancellationToken
            ).ConfigureAwait(false);

            _ = AdvanceClock(root);
            var resolutionSummary = CombineNonEmptySummaries(
                prelude.PendingTurnEndEffects.TerminalVisibleSummary,
                prelude.BackgroundWorkingEffects.TerminalVisibleSummary,
                gmResolution.Summary
            );

            MergeActorFacingSummariesIntoExistingResolutions(
                root,
                prelude.PendingTurnEndEffects,
                prelude.BackgroundWorkingEffects
            );
            return AsyncAteliaResult<ActionResolution>.Success(
                CompleteTurn(root, resolutionSummary, ActorResolutionCommitMode.PreserveExistingAndFillMissing)
            );
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) {
            return AsyncAteliaResult<ActionResolution>.Failure(
                new TextAdvError(
                    "TextAdv.CollectedTurnGmFailed",
                    $"多主体统一结算依赖 GM Agent，但本次调用失败：{ex.Message}"
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
            kind: PlayerActorKind,
            name,
            profileNote,
            locationId,
            active: true,
            controllerKind: InternalLlmControllerKind
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

    internal static async Task<AsyncAteliaResult<ActionResolution>> ApplyExploreAsync(
        DurableDict<string> root,
        string direction,
        string? focus,
        string preActionReason,
        string validatorFeedback,
        CancellationToken cancellationToken
    ) {
        var plan = new TerminalActionExecutionPlan.Explore(
            NormalizeRequired(direction, nameof(direction)),
            string.IsNullOrWhiteSpace(focus) ? null : focus.Trim(),
            preActionReason
        );
        return await AppendAcceptedLargeActionAndResolveAsync(
            root,
            plan.Descriptor,
            validatorFeedback,
            () => ResolveExploreAcceptedAsync(
                root,
                plan.Direction,
                plan.Focus,
                plan.PreActionReason,
                collectedTurnLead: null,
                cancellationToken
            )
        ).ConfigureAwait(false);
    }

    internal static async Task<AsyncAteliaResult<ActionResolution>> ApplyInteractionAsync(
        DurableDict<string> root,
        string interactionId,
        string preActionReason,
        string validatorFeedback,
        CancellationToken cancellationToken
    ) {
        var interactionResult = TryGetVisibleInteraction(DescribeCurrentPerception(root), interactionId);
        if (!interactionResult.TryGetValue(out var interaction) || interaction is null) { return AsyncAteliaResult<ActionResolution>.Failure(interactionResult.Error!); }
        var executionKindResult = ClassifyInteractionExecutionKind(interaction);
        if (!executionKindResult.TryGetValue(out var executionKind) || executionKind != InteractionExecutionKind.TurnEnding) {
            return AsyncAteliaResult<ActionResolution>.Failure(
                new TextAdvError(
                    "TextAdv.UnsupportedTurnEndingInteraction",
                    $"Interaction '{interaction.InteractionId}' 不是当前可按单回合 Large-Action 结算的交互。"
                )
            );
        }

        var plan = CreateInteractionPlan(interaction, executionKind, preActionReason);

        return await AppendAcceptedLargeActionAndResolveAsync(
            root,
            plan.Descriptor,
            validatorFeedback,
            () => ResolveInteractionAcceptedAsync(
                root,
                plan.InteractionId,
                plan.PreActionReason,
                collectedTurnLead: null,
                cancellationToken
            )
        ).ConfigureAwait(false);
    }

    internal static async Task<AsyncAteliaResult<ActionResolution>> ApplyImmediateSelfInteractionAsync(
        DurableDict<string> root,
        string interactionId,
        string preActionReason,
        string validatorFeedback,
        CancellationToken cancellationToken
    ) {
        return await ApplyImmediateSelfInteractionForActorAsync(
            root,
            TerminalPlayerActorId,
            interactionId,
            preActionReason,
            validatorFeedback,
            cancellationToken
        ).ConfigureAwait(false);
    }

    internal static async Task<AsyncAteliaResult<ActionResolution>> ApplyImmediateSelfInteractionForActorAsync(
        DurableDict<string> root,
        string actorId,
        string interactionId,
        string preActionReason,
        string validatorFeedback,
        CancellationToken cancellationToken
    ) {
        actorId = NormalizeRequired(actorId, nameof(actorId));
        var actorPerception = DescribePerceptionForActor(root, actorId);
        var interactionResult = TryGetVisibleInteraction(actorPerception, interactionId);
        if (!interactionResult.TryGetValue(out var interaction) || interaction is null) { return AsyncAteliaResult<ActionResolution>.Failure(interactionResult.Error!); }

        var executionKindResult = ClassifyInteractionExecutionKind(interaction);
        if (!executionKindResult.TryGetValue(out var executionKind) || executionKind != InteractionExecutionKind.ImmediateSelf) {
            return AsyncAteliaResult<ActionResolution>.Failure(
                new TextAdvError(
                    "TextAdv.UnsupportedImmediateInteraction",
                    $"Interaction '{interaction.InteractionId}' 不是当前可直接即时结算的 self/immediate 小动作。"
                )
            );
        }

        GmExploreResolution gmResolution;
        try {
            gmResolution = await GameMasterResolver.ResolveImmediateSelfInteractionAsync(
                root,
                new GmInteractionContext(
                    actorPerception,
                    GetActorLocationId(root, actorId),
                    interaction,
                    preActionReason
                ),
                cancellationToken
            ).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) {
            return AsyncAteliaResult<ActionResolution>.Failure(
                new TextAdvError(
                    "TextAdv.ImmediateInteractionGmFailed",
                    $"即时小动作结算依赖 GM Agent，但本次调用失败：{ex.Message}"
                )
            );
        }

        var summary = gmResolution.Summary;
        var plan = CreateInteractionPlan(interaction, InteractionExecutionKind.ImmediateSelf, preActionReason);

        AppendAcceptedStepForActor(
            root,
            actorId,
            plan.Descriptor,
            validatorFeedback,
            endsTurn: false,
            stepOutcomeSummary: summary,
            stepOutcomeState: StepOutcomeCommittedNow
        );

        return AsyncAteliaResult<ActionResolution>.Success(
            new ActionResolution(summary, DescribePerceptionForActor(root, actorId))
        );
    }

    internal static AsyncAteliaResult<ActionResolution> ApplyDeferredTurnEndInteraction(
        DurableDict<string> root,
        string interactionId,
        string preActionReason,
        string validatorFeedback
    ) {
        return ApplyDeferredTurnEndInteractionForActor(
            root,
            TerminalPlayerActorId,
            interactionId,
            preActionReason,
            validatorFeedback
        );
    }

    internal static AsyncAteliaResult<ActionResolution> ApplyDeferredTurnEndInteractionForActor(
        DurableDict<string> root,
        string actorId,
        string interactionId,
        string preActionReason,
        string validatorFeedback
    ) {
        actorId = NormalizeRequired(actorId, nameof(actorId));
        var actorPerception = DescribePerceptionForActor(root, actorId);
        var interactionResult = TryGetVisibleInteraction(actorPerception, interactionId);
        if (!interactionResult.TryGetValue(out var interaction) || interaction is null) { return AsyncAteliaResult<ActionResolution>.Failure(interactionResult.Error!); }

        var executionKindResult = ClassifyInteractionExecutionKind(interaction);
        if (!executionKindResult.TryGetValue(out var executionKind) || executionKind != InteractionExecutionKind.DeferredTurnEnd) {
            return AsyncAteliaResult<ActionResolution>.Failure(
                new TextAdvError(
                    "TextAdv.UnsupportedDeferredInteraction",
                    $"Interaction '{interaction.InteractionId}' 不是当前可走 turn-end 延迟结算的小动作。"
                )
            );
        }

        var pendingSummary = BuildDeferredTurnEndInteractionSummary(interaction);
        var plan = CreateInteractionPlan(interaction, InteractionExecutionKind.DeferredTurnEnd, preActionReason);
        var step = AppendAcceptedStepForActor(
            root,
            actorId,
            plan.Descriptor,
            validatorFeedback,
            endsTurn: false,
            stepOutcomeSummary: pendingSummary,
            stepOutcomeState: StepOutcomePendingTurnEnd
        );
        EnqueuePendingTurnEndEffect(
            root,
            actorId,
            step,
            effectSlot: TurnEndEffectSlot
        );

        return AsyncAteliaResult<ActionResolution>.Success(
            new ActionResolution(pendingSummary, DescribePerceptionForActor(root, actorId))
        );
    }

    internal static async Task<AsyncAteliaResult<ActionResolution>> ApplyWorkingInteractionAsync(
        DurableDict<string> root,
        string interactionId,
        string preActionReason,
        string validatorFeedback,
        CancellationToken cancellationToken
    ) {
        var interactionResult = TryGetVisibleInteraction(DescribeCurrentPerception(root), interactionId);
        if (!interactionResult.TryGetValue(out var interaction) || interaction is null) { return AsyncAteliaResult<ActionResolution>.Failure(interactionResult.Error!); }

        var executionKindResult = ClassifyInteractionExecutionKind(interaction);
        if (!executionKindResult.TryGetValue(out var executionKind) || executionKind != InteractionExecutionKind.WorkingStart) {
            return AsyncAteliaResult<ActionResolution>.Failure(
                new TextAdvError(
                    "TextAdv.UnsupportedWorkingInteraction",
                    $"Interaction '{interaction.InteractionId}' 不是当前可进入 Working 的交互。"
                )
            );
        }

        var plan = CreateInteractionPlan(interaction, InteractionExecutionKind.WorkingStart, preActionReason);
        var workingStartSummary = BuildWorkingStartSummary(interaction);

        AppendAcceptedStep(
            root,
            plan.Descriptor,
            validatorFeedback,
            endsTurn: true,
            stepOutcomeSummary: workingStartSummary,
            stepOutcomeState: StepOutcomeWorking
        );

        StartWorkingForActor(
            root,
            TerminalPlayerActorId,
            plan.Descriptor,
            validatorFeedback,
            interaction.TurnCost - 1
        );

        var turnSummary = await ResolveWorkingTurnAndCompleteAsync(
            root,
            TerminalPlayerActorId,
            workingStartSummary,
            collectedTurnLead: null,
            consumeFutureTurn: false,
            cancellationToken
        ).ConfigureAwait(false);
        return await CompleteWorkingActionWithAutoAdvanceAsync(root, turnSummary, cancellationToken).ConfigureAwait(false);
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
            new ActionDescriptor(
                TerminalActionKinds.SmallEditMemoryNotebook,
                proposal.ActionSummary,
                proposal.CanonicalScriptXml,
                preActionReason
            ),
            validatorFeedback,
            endsTurn: false,
            stepOutcomeSummary: "你的记事本已按这一步修改更新。",
            stepOutcomeState: StepOutcomeCommittedNow
        );

        return DescribePerceptionForActor(root, actorId);
    }

    internal static ActionResolution ApplyRestAWhile(
        DurableDict<string> root,
        string preActionReason,
        string validatorFeedback
    ) {
        var descriptor = new ActionDescriptor(
            TerminalActionKinds.LargeRestAWhile,
            "原地休息一会",
            null,
            preActionReason
        );
        return AppendAcceptedLargeActionAndResolve(
            root,
            descriptor,
            validatorFeedback,
            () => ResolveRestAccepted(root, collectedTurnLead: null)
        );
    }

    internal static Task<AsyncAteliaResult<ActionResolution>> ExecuteTerminalActionPlanAsync(
        DurableDict<string> root,
        TerminalActionExecutionPlan plan,
        string validatorFeedback,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(plan);
        validatorFeedback = NormalizeRequired(validatorFeedback, nameof(validatorFeedback));

        return plan switch {
            TerminalActionExecutionPlan.Explore explore => ApplyExploreAsync(
                root,
                explore.Direction,
                explore.Focus,
                plan.PreActionReason,
                validatorFeedback,
                cancellationToken
            ),
            TerminalActionExecutionPlan.RestAWhile => Task.FromResult(
                AsyncAteliaResult<ActionResolution>.Success(
                    ApplyRestAWhile(root, plan.PreActionReason, validatorFeedback)
                )
            ),
            TerminalActionExecutionPlan.Interaction interaction => ExecuteTerminalInteractionPlanAsync(
                root,
                interaction,
                validatorFeedback,
                cancellationToken
            ),
            _ => throw new InvalidOperationException($"Unknown terminal action plan type: {plan.GetType().FullName}")
        };
    }

    private static Task<AsyncAteliaResult<ActionResolution>> ExecuteTerminalInteractionPlanAsync(
        DurableDict<string> root,
        TerminalActionExecutionPlan.Interaction interaction,
        string validatorFeedback,
        CancellationToken cancellationToken
    ) {
        return interaction.ExecutionKind switch {
            InteractionExecutionKind.ImmediateSelf => ApplyImmediateSelfInteractionAsync(
                root,
                interaction.InteractionId,
                interaction.PreActionReason,
                validatorFeedback,
                cancellationToken
            ),
            InteractionExecutionKind.DeferredTurnEnd => Task.FromResult(
                ApplyDeferredTurnEndInteraction(
                    root,
                    interaction.InteractionId,
                    interaction.PreActionReason,
                    validatorFeedback
                )
            ),
            InteractionExecutionKind.WorkingStart => ApplyWorkingInteractionAsync(
                root,
                interaction.InteractionId,
                interaction.PreActionReason,
                validatorFeedback,
                cancellationToken
            ),
            InteractionExecutionKind.TurnEnding => ApplyInteractionAsync(
                root,
                interaction.InteractionId,
                interaction.PreActionReason,
                validatorFeedback,
                cancellationToken
            ),
            _ => throw new InvalidOperationException($"Unknown interaction execution kind: {interaction.ExecutionKind}")
        };
    }

    private static async Task<AsyncAteliaResult<TurnResolutionPrelude>> PrepareTurnResolutionPreludeAsync(
        DurableDict<string> root,
        CancellationToken cancellationToken,
        string? excludeWorkingActorId = null
    ) {
        var pendingTurnEndSummaryResult = await ResolvePendingTurnEndEffectsAsync(root, cancellationToken).ConfigureAwait(false);
        if (!pendingTurnEndSummaryResult.TryGetValue(out var pendingTurnEndSummary)) { return AsyncAteliaResult<TurnResolutionPrelude>.Failure(pendingTurnEndSummaryResult.Error!); }

        var backgroundWorkingSummaryResult = await ResolveBackgroundWorkingEffectsAsync(
            root,
            cancellationToken,
            excludeWorkingActorId
        ).ConfigureAwait(false);
        if (!backgroundWorkingSummaryResult.TryGetValue(out var backgroundWorkingSummary)) { return AsyncAteliaResult<TurnResolutionPrelude>.Failure(backgroundWorkingSummaryResult.Error!); }

        var game = GetGame(root);
        return AsyncAteliaResult<TurnResolutionPrelude>.Success(
            new TurnResolutionPrelude(
                pendingTurnEndSummary!,
                backgroundWorkingSummary!,
                game.GetOrThrow<int>(DayKey),
                game.GetOrThrow<int>(SlotKey),
                game.GetOrThrow<int>(SlotsPerDayKey)
            )
        );
    }

    private static async Task<AsyncAteliaResult<ActionResolution>> ResolveExploreAcceptedAsync(
        DurableDict<string> root,
        string direction,
        string? focus,
        string preActionReason,
        string? collectedTurnLead,
        CancellationToken cancellationToken
    ) {
        var preludeResult = await PrepareTurnResolutionPreludeAsync(root, cancellationToken).ConfigureAwait(false);
        if (!preludeResult.TryGetValue(out var prelude) || prelude is null) { return AsyncAteliaResult<ActionResolution>.Failure(preludeResult.Error!); }

        return await ResolveExploreAcceptedForActorAsync(
            root,
            TerminalPlayerActorId,
            direction,
            focus,
            preActionReason,
            prelude,
            collectedTurnLead,
            cancellationToken
        ).ConfigureAwait(false);
    }

    private static async Task<AsyncAteliaResult<ActionResolution>> ResolveInteractionAcceptedAsync(
        DurableDict<string> root,
        string interactionId,
        string preActionReason,
        string? collectedTurnLead,
        CancellationToken cancellationToken
    ) {
        var preludeResult = await PrepareTurnResolutionPreludeAsync(root, cancellationToken).ConfigureAwait(false);
        if (!preludeResult.TryGetValue(out var prelude) || prelude is null) { return AsyncAteliaResult<ActionResolution>.Failure(preludeResult.Error!); }

        return await ResolveInteractionAcceptedForActorAsync(
            root,
            TerminalPlayerActorId,
            interactionId,
            preActionReason,
            prelude,
            collectedTurnLead,
            cancellationToken
        ).ConfigureAwait(false);
    }

    private static ActionResolution ResolveRestAccepted(DurableDict<string> root, string? collectedTurnLead) {
        var preludeResult = PrepareTurnResolutionPreludeAsync(root, CancellationToken.None).GetAwaiter().GetResult();
        if (!preludeResult.TryGetValue(out var prelude) || prelude is null) { throw new InvalidOperationException(preludeResult.Error?.Message ?? "无法准备本回合的 turn resolution prelude。"); }

        return ResolveRestAcceptedForActor(
            root,
            TerminalPlayerActorId,
            prelude,
            collectedTurnLead
        );
    }

    private static async Task<AsyncAteliaResult<ActionResolution>> ResolveExploreAcceptedForActorAsync(
        DurableDict<string> root,
        string actorId,
        string direction,
        string? focus,
        string preActionReason,
        TurnResolutionPrelude prelude,
        string? collectedTurnLead,
        CancellationToken cancellationToken
    ) {
        actorId = NormalizeRequired(actorId, nameof(actorId));
        direction = NormalizeRequired(direction, nameof(direction));
        focus = string.IsNullOrWhiteSpace(focus) ? null : focus.Trim();

        var actorPerception = DescribePerceptionForActor(root, actorId);
        var currentLocationId = GetActorLocationId(root, actorId);
        var currentLocationName = actorPerception.Location.Name;
        GmExploreResolution gmResolution;
        try {
            gmResolution = await GameMasterResolver.ResolveExploreAsync(
                root,
                new GmExploreContext(
                    actorPerception,
                    currentLocationId,
                    direction,
                    focus,
                    preActionReason,
                    TryGetReverseDirection(direction)
                ),
                cancellationToken
            );
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) {
            return AsyncAteliaResult<ActionResolution>.Failure(
                new TextAdvError(
                    "TextAdv.ExploreGmFailed",
                    $"探索结算依赖 GM Agent，但本次调用失败：{ex.Message}"
                )
            );
        }
        var nextLocationId = GetActorLocationId(root, actorId);
        var nextLocationName = DescribePerceptionForActor(root, actorId).Location.Name;
        _ = AdvanceClock(root);
        var actorSummaryText = gmResolution.Summary;
        ApplyActorFacingSummariesForTurn(
            root,
            new[] { prelude.PendingTurnEndEffects, prelude.BackgroundWorkingEffects },
            new ActorEffectSummary(actorId, actorSummaryText)
        );

        var resolutionSummary = CombineNonEmptySummaries(
            prelude.PendingTurnEndEffects.TerminalVisibleSummary,
            prelude.BackgroundWorkingEffects.TerminalVisibleSummary,
            actorId == TerminalPlayerActorId
                ? actorSummaryText
                : BuildTerminalObservationForExplore(
                    root,
                    actorId,
                    currentLocationId,
                    currentLocationName,
                    nextLocationId,
                    nextLocationName,
                    direction,
                    !string.Equals(currentLocationId, nextLocationId, StringComparison.Ordinal)
                        && !actorPerception.Location.Exits.Any(exit => string.Equals(exit.Direction, direction, StringComparison.Ordinal))
                )
        );
        resolutionSummary = PrefixCollectedTurnLead(collectedTurnLead, resolutionSummary);

        return AsyncAteliaResult<ActionResolution>.Success(
            CompleteTurn(root, resolutionSummary, ActorResolutionCommitMode.PreserveExistingAndFillMissing)
        );
    }

    private static async Task<AsyncAteliaResult<ActionResolution>> ResolveInteractionAcceptedForActorAsync(
        DurableDict<string> root,
        string actorId,
        string interactionId,
        string preActionReason,
        TurnResolutionPrelude prelude,
        string? collectedTurnLead,
        CancellationToken cancellationToken
    ) {
        actorId = NormalizeRequired(actorId, nameof(actorId));
        var actorPerception = DescribePerceptionForActor(root, actorId);
        var interactionResult = TryGetVisibleInteraction(actorPerception, interactionId);
        if (!interactionResult.TryGetValue(out var interaction) || interaction is null) { return AsyncAteliaResult<ActionResolution>.Failure(interactionResult.Error!); }

        var executionKindResult = ClassifyInteractionExecutionKind(interaction);
        if (!executionKindResult.TryGetValue(out var executionKind)) {
            return AsyncAteliaResult<ActionResolution>.Failure(executionKindResult.Error!);
        }

        switch (executionKind) {
            case InteractionExecutionKind.WorkingStart: {
                var plan = CreateInteractionPlan(interaction, executionKind, preActionReason);
                var workingStartSummary = BuildWorkingStartSummary(interaction);

                StartWorkingForActor(
                    root,
                    actorId,
                    plan.Descriptor,
                    validatorFeedback: "collected-turn working start",
                    interaction.TurnCost - 1
                );

                var workingResolution = await ResolveWorkingTurnAndCompleteWithPreludeAsync(
                    root,
                    actorId,
                    workingStartSummary,
                    prelude,
                    collectedTurnLead,
                    consumeFutureTurn: false,
                    cancellationToken
                ).ConfigureAwait(false);
                return await CompleteWorkingActionWithAutoAdvanceAsync(root, workingResolution, cancellationToken).ConfigureAwait(false);
            }
            case InteractionExecutionKind.TurnEnding:
                break;
            default:
                return AsyncAteliaResult<ActionResolution>.Failure(
                    new TextAdvError(
                        "TextAdv.UnsupportedCollectedInteractionExecutionKind",
                        $"当前 collected-turn 不支持 executionKind='{executionKind}' 的 interaction。"
                    )
                );
        }

        GmExploreResolution gmResolution;
        try {
            gmResolution = await GameMasterResolver.ResolveInteractionAsync(
                root,
                new GmInteractionContext(
                    actorPerception,
                    GetActorLocationId(root, actorId),
                    interaction,
                    preActionReason
                ),
                cancellationToken
            ).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) {
            return AsyncAteliaResult<ActionResolution>.Failure(
                new TextAdvError(
                    "TextAdv.InteractionGmFailed",
                    $"交互结算依赖 GM Agent，但本次调用失败：{ex.Message}"
                )
            );
        }

        _ = AdvanceClock(root);
        var actorSummaryText = gmResolution.Summary;
        ApplyActorFacingSummariesForTurn(
            root,
            new[] { prelude.PendingTurnEndEffects, prelude.BackgroundWorkingEffects },
            new ActorEffectSummary(actorId, actorSummaryText)
        );

        var resolutionSummary = CombineNonEmptySummaries(
            prelude.PendingTurnEndEffects.TerminalVisibleSummary,
            prelude.BackgroundWorkingEffects.TerminalVisibleSummary,
            actorId == TerminalPlayerActorId
                ? actorSummaryText
                : BuildTerminalObservationForInteraction(root, actorId, interaction)
        );
        resolutionSummary = PrefixCollectedTurnLead(collectedTurnLead, resolutionSummary);

        return AsyncAteliaResult<ActionResolution>.Success(
            CompleteTurn(root, resolutionSummary, ActorResolutionCommitMode.PreserveExistingAndFillMissing)
        );
    }

    private static ActionResolution ResolveRestAcceptedForActor(
        DurableDict<string> root,
        string actorId,
        TurnResolutionPrelude prelude,
        string? collectedTurnLead
    ) {
        actorId = NormalizeRequired(actorId, nameof(actorId));
        _ = AdvanceClock(root);
        var actorSummaryText = "你原地休息了一会。当前原型只推进时钟，不结算更复杂的世界后果。";
        ApplyActorFacingSummariesForTurn(
            root,
            new[] { prelude.PendingTurnEndEffects, prelude.BackgroundWorkingEffects },
            new ActorEffectSummary(actorId, actorSummaryText)
        );

        var terminalObservation = actorId == TerminalPlayerActorId
            ? actorSummaryText
            : BuildTerminalObservationForRest(root, actorId);
        var resolutionSummary = CombineNonEmptySummaries(
            prelude.PendingTurnEndEffects.TerminalVisibleSummary,
            prelude.BackgroundWorkingEffects.TerminalVisibleSummary,
            terminalObservation
        );
        resolutionSummary = PrefixCollectedTurnLead(collectedTurnLead, resolutionSummary);

        return CompleteTurn(root, resolutionSummary, ActorResolutionCommitMode.PreserveExistingAndFillMissing);
    }

    private static string BuildInteractionActionSummary(InteractionPerception interaction) {
        ArgumentNullException.ThrowIfNull(interaction);
        return $"{interaction.VisibleLabel} ({interaction.ActionKind})";
    }

    private static ActionResolution AppendAcceptedLargeActionAndResolve(
        DurableDict<string> root,
        ActionDescriptor descriptor,
        string validatorFeedback,
        Func<ActionResolution> resolve
    ) {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(resolve);

        AppendAcceptedStep(
            root,
            descriptor,
            validatorFeedback,
            endsTurn: true
        );

        return resolve();
    }

    private static Task<AsyncAteliaResult<ActionResolution>> AppendAcceptedLargeActionAndResolveAsync(
        DurableDict<string> root,
        ActionDescriptor descriptor,
        string validatorFeedback,
        Func<Task<AsyncAteliaResult<ActionResolution>>> resolveAsync
    ) {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(resolveAsync);

        AppendAcceptedStep(
            root,
            descriptor,
            validatorFeedback,
            endsTurn: true
        );

        return resolveAsync();
    }

    private static void SetLastResolutionForActiveActors(DurableDict<string> root, string summary) {
        summary = NormalizeRequired(summary, nameof(summary));
        var game = GetGame(root);
        var lastResolutionByActor = root.Revision.CreateDict<string>();
        foreach (var actorId in EnumerateTrackedActorIds(root)) {
            lastResolutionByActor.Upsert(actorId, summary);
        }

        game.Upsert(LastResolutionByActorKey, lastResolutionByActor);
    }

    private static void SetLastResolutionForMissingActiveActors(DurableDict<string> root, string fallbackSummary) {
        fallbackSummary = NormalizeRequired(fallbackSummary, nameof(fallbackSummary));
        var lastResolutionByActor = GetOrCreateLastResolutionByActor(root);
        foreach (var actorId in EnumerateTrackedActorIds(root)) {
            if (!lastResolutionByActor.TryGet(actorId, out string? actorResolution)
                || string.IsNullOrWhiteSpace(actorResolution)) {
                lastResolutionByActor.Upsert(actorId, fallbackSummary);
            }
        }
    }

    private static TurnStep AppendAcceptedStep(
        DurableDict<string> root,
        ActionDescriptor descriptor,
        string validatorFeedback,
        bool endsTurn,
        string? stepOutcomeSummary = null,
        string? stepOutcomeState = null
    ) {
        return AppendAcceptedStepForActor(
            root,
            TerminalPlayerActorId,
            descriptor,
            validatorFeedback,
            endsTurn,
            stepOutcomeSummary,
            stepOutcomeState
        );
    }

    private static TurnStep AppendAcceptedStepForActor(
        DurableDict<string> root,
        string actorId,
        ActionDescriptor descriptor,
        string validatorFeedback,
        bool endsTurn,
        string? stepOutcomeSummary = null,
        string? stepOutcomeState = null
    ) {
        descriptor = NormalizeActionDescriptor(descriptor);
        var currentTurn = GetCurrentTurn(root);
        var acceptedSteps = GetAcceptedStepsForActor(root, actorId, createIfMissing: true)!;
        var stepNumber = currentTurn.GetOrThrow<int>(NextStepNumberKey);
        var stepId = $"step-{stepNumber:D4}";
        var step = root.Revision.CreateDict<string>();

        step.Upsert(ActionKindKey, descriptor.ActionKind);
        step.Upsert(ActionSummaryKey, descriptor.ActionSummary);
        if (descriptor.ActionPayload is not null) {
            step.Upsert(ActionPayloadKey, descriptor.ActionPayload);
        }

        step.Upsert(PreActionReasonKey, descriptor.PreActionReason);
        step.Upsert(ValidatorFeedbackKey, validatorFeedback);
        step.Upsert(EndsTurnKey, endsTurn);
        if (!string.IsNullOrWhiteSpace(stepOutcomeSummary)) {
            step.Upsert(StepOutcomeSummaryKey, stepOutcomeSummary);
        }

        var normalizedStepOutcomeState = string.IsNullOrWhiteSpace(stepOutcomeState)
            ? (endsTurn ? StepOutcomeCompleted : StepOutcomeCommittedNow)
            : stepOutcomeState.Trim();
        step.Upsert(StepOutcomeStateKey, normalizedStepOutcomeState);

        acceptedSteps.Upsert(stepId, step);
        currentTurn.Upsert(NextStepNumberKey, stepNumber + 1);

        var turnStep = new TurnStep(
            stepNumber,
            descriptor.ActionKind,
            descriptor.ActionSummary,
            descriptor.ActionPayload,
            descriptor.PreActionReason,
            validatorFeedback,
            endsTurn,
            stepOutcomeSummary,
            normalizedStepOutcomeState
        );
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
        var endDay = game.GetOrThrow<int>(DayKey);
        var endSlot = game.GetOrThrow<int>(SlotKey);
        var turnNumber = completedTurnCount + 1;
        var archivedTurn = rev.CreateDict<string>();

        archivedTurn.Upsert(TurnNumberKey, turnNumber);
        archivedTurn.Upsert(StartDayKey, currentTurn.GetOrThrow<int>(StartDayKey));
        archivedTurn.Upsert(StartSlotKey, currentTurn.GetOrThrow<int>(StartSlotKey));
        archivedTurn.Upsert(StartLocationIdKey, currentTurn.GetOrThrow<string>(StartLocationIdKey)!);
        archivedTurn.Upsert(NotebookSnapshotKey, currentTurn.GetOrThrow<string>(NotebookSnapshotKey)!);
        archivedTurn.Upsert(AcceptedStepsByActorKey, currentTurn.GetOrThrow<DurableDict<string>>(AcceptedStepsByActorKey)!);
        archivedTurn.Upsert(LargeActionByActorKey, currentTurn.GetOrThrow<DurableDict<string>>(LargeActionByActorKey)!);
        archivedTurn.Upsert(PendingTurnEndEffectsByActorKey, currentTurn.GetOrThrow<DurableDict<string>>(PendingTurnEndEffectsByActorKey)!);
        archivedTurn.Upsert(ResolutionSummaryKey, resolutionSummary);
        archivedTurn.Upsert(LastResolutionByActorKey, GetOrCreateLastResolutionByActor(root));
        archivedTurn.Upsert(EndDayKey, endDay);
        archivedTurn.Upsert(EndSlotKey, endSlot);
        archivedTurn.Upsert(ActorTurnContextByActorKey, CreateArchivedActorTurnContextByActor(root));
        archivedTurn.Upsert(EndingNotebookKey, GetNotebookContent(GetNotebookSnapshot(root)));

        turnHistory.Upsert($"turn-{turnNumber:D4}", archivedTurn);
        game.Upsert(CompletedTurnCountKey, turnNumber);
    }

    private static ActionResolution CompleteTurn(
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
        return new ActionResolution(resolutionSummary, DescribeCurrentPerception(root));
    }

    private static async Task<AsyncAteliaResult<ActionResolution>> ResolveCollectedTurnFromIntentAsync(
        DurableDict<string> root,
        LargeActionIntent intent,
        TurnResolutionPrelude prelude,
        CancellationToken cancellationToken
    ) {
        return intent.ActionKind switch {
            TerminalActionKinds.LargeRestAWhile => AsyncAteliaResult<ActionResolution>.Success(
                ResolveRestAcceptedForActor(root, intent.ActorId, prelude, collectedTurnLead: null)
            ),
            TerminalActionKinds.LargeExplore => await ResolveExploreAcceptedForActorAsync(
                root,
                intent.ActorId,
                ParseRequiredPayloadValue(intent.ActionPayload, "direction"),
                ParseOptionalPayloadValue(intent.ActionPayload, "focus"),
                intent.PreActionReason,
                prelude,
                collectedTurnLead: null,
                cancellationToken
            ).ConfigureAwait(false),
            TerminalActionKinds.LargeInteract => await ResolveInteractionAcceptedForActorAsync(
                root,
                intent.ActorId,
                ParseRequiredPayloadValue(intent.ActionPayload, "interactionId"),
                intent.PreActionReason,
                prelude,
                collectedTurnLead: null,
                cancellationToken
            ).ConfigureAwait(false),
            _ => AsyncAteliaResult<ActionResolution>.Failure(
                new TextAdvError(
                    "TextAdv.UnsupportedCollectedAction",
                    $"当前尚不支持 collected-turn 中的 Large-Action '{intent.ActionKind}'。"
                )
            )
        };
    }

    private static DurableDict<string> CreateArchivedActorTurnContextByActor(DurableDict<string> root) {
        var contexts = root.Revision.CreateDict<string>();
        foreach (var actorId in EnumerateTrackedActorIds(root)) {
            contexts.Upsert(actorId, CreateArchivedActorTurnContext(root, actorId));
        }

        return contexts;
    }

    private static DurableDict<string> CreateArchivedActorTurnContext(DurableDict<string> root, string actorId) {
        var actor = GetActor(root, actorId);
        var location = GetLocation(root, GetActorLocationId(root, actorId));
        var context = root.Revision.CreateDict<string>();
        var actorName = actor.TryGet(NameKey, out string? rawName) && !string.IsNullOrWhiteSpace(rawName)
            ? rawName
            : actorId;
        var actorKind = GetActorKind(actor);

        context.Upsert(NameKey, actorName);
        context.Upsert(KindKey, actorKind);
        context.Upsert(LocationIdKey, GetActorLocationId(root, actorId));
        context.Upsert(LocationNameKey, location.GetOrThrow<string>(NameKey)!);
        return context;
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
            .Select(
            actorId => {
                var actor = GetActor(root, actorId);
                var action = largeActionByActor.GetOrThrow<DurableDict<string>>(actorId)!;
                _ = action.TryGet(ActionPayloadKey, out string? actionPayload);
                var actorName = actor.TryGet(NameKey, out string? rawName) && !string.IsNullOrWhiteSpace(rawName)
                    ? rawName
                    : actorId;
                var actorKind = GetActorKind(actor);

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
            }
        )
            .ToArray();
    }

    private static DurableDict<string> GetOrCreatePendingTurnEndEffectsForActor(
        DurableDict<string> root,
        string actorId
    ) {
        actorId = NormalizeRequired(actorId, nameof(actorId));
        var currentTurn = GetCurrentTurn(root);
        var pendingByActor = currentTurn.GetOrThrow<DurableDict<string>>(PendingTurnEndEffectsByActorKey)!;
        if (pendingByActor.TryGet(actorId, out DurableDict<string>? pendingEffects)
            && pendingEffects is not null) { return pendingEffects; }

        pendingEffects = root.Revision.CreateDict<string>();
        pendingByActor.Upsert(actorId, pendingEffects);
        return pendingEffects;
    }

    private static IReadOnlyList<PendingTurnEndEffect> ReadPendingTurnEndEffects(DurableDict<string> root) {
        var currentTurn = GetCurrentTurn(root);
        var pendingByActor = currentTurn.GetOrThrow<DurableDict<string>>(PendingTurnEndEffectsByActorKey)!;
        return pendingByActor.Keys
            .OrderBy(static key => key, StringComparer.Ordinal)
            .SelectMany(
            actorId => {
                var pendingEffects = pendingByActor.GetOrThrow<DurableDict<string>>(actorId)!;
                return pendingEffects.Keys
                    .OrderBy(static key => key, StringComparer.Ordinal)
                    .Select(
                    effectId => {
                        var effect = pendingEffects.GetOrThrow<DurableDict<string>>(effectId)!;
                        _ = effect.TryGet(ActionPayloadKey, out string? actionPayload);
                        return new PendingTurnEndEffect(
                            effectId,
                            effect.GetOrThrow<string>(ActorIdKey)!,
                            effect.GetOrThrow<string>(ActionKindKey)!,
                            effect.GetOrThrow<string>(ActionSummaryKey)!,
                            actionPayload,
                            effect.GetOrThrow<string>(PreActionReasonKey)!,
                            effect.GetOrThrow<string>(ValidatorFeedbackKey)!,
                            effect.GetOrThrow<string>(EffectSlotKey)!,
                            effect.GetOrThrow<int>(SourceStepNumberKey)
                        );
                    }
                );
            }
        )
            .OrderBy(static effect => effect.SourceStepNumber)
            .ToArray();
    }

    private static void ClearPendingTurnEndEffects(DurableDict<string> root) {
        var currentTurn = GetCurrentTurn(root);
        currentTurn.Upsert(PendingTurnEndEffectsByActorKey, root.Revision.CreateDict<string>());
    }

    private static void EnqueuePendingTurnEndEffect(
        DurableDict<string> root,
        string actorId,
        TurnStep step,
        string effectSlot
    ) {
        var pendingEffects = GetOrCreatePendingTurnEndEffectsForActor(root, actorId);
        var effect = root.Revision.CreateDict<string>();
        var effectId = $"pending-{step.StepNumber:D4}";

        effect.Upsert(ActorIdKey, actorId);
        effect.Upsert(ActionKindKey, step.ActionKind);
        effect.Upsert(ActionSummaryKey, step.ActionSummary);
        if (step.ActionPayload is not null) {
            effect.Upsert(ActionPayloadKey, step.ActionPayload);
        }

        effect.Upsert(PreActionReasonKey, step.PreActionReason);
        effect.Upsert(ValidatorFeedbackKey, step.ValidatorFeedback);
        effect.Upsert(EffectSlotKey, effectSlot);
        effect.Upsert(SourceStepNumberKey, step.StepNumber);
        pendingEffects.Upsert(effectId, effect);
    }

    private static async Task<AsyncAteliaResult<EffectBatchResolution>> ResolvePendingTurnEndEffectsAsync(
        DurableDict<string> root,
        CancellationToken cancellationToken
    ) {
        var pendingEffects = ReadPendingTurnEndEffects(root);
        if (pendingEffects.Count == 0) {
            return AsyncAteliaResult<EffectBatchResolution>.Success(
                new EffectBatchResolution(string.Empty, [])
            );
        }

        var terminalSummaries = new List<string>();
        var actorSummaries = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var effect in pendingEffects) {
            if (string.IsNullOrWhiteSpace(effect.ActionPayload)) {
                return AsyncAteliaResult<EffectBatchResolution>.Failure(
                    new TextAdvError(
                        "TextAdv.PendingTurnEndPayloadMissing",
                        $"Pending turn-end effect '{effect.EffectId}' 缺少 action payload。"
                    )
                );
            }

            InteractionPerception interaction;
            try {
                interaction = BuildInteractionSnapshotFromPayload(effect.ActionPayload!);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) {
                return AsyncAteliaResult<EffectBatchResolution>.Failure(
                    new TextAdvError(
                        "TextAdv.PendingTurnEndPayloadInvalid",
                        ex.Message
                    )
                );
            }

            var effectSummary = await ResolveInteractionEffectAsync(
                root,
                effect.ActorId,
                interaction,
                effect.PreActionReason,
                effect.EffectSlot,
                cancellationToken
            ).ConfigureAwait(false);
            if (!effectSummary.TryGetValue(out var narration) || narration is null) { return AsyncAteliaResult<EffectBatchResolution>.Failure(effectSummary.Error!); }

            AppendActorFacingSummary(actorSummaries, effect.ActorId, narration.ActorFacingSummary);
            if (!string.IsNullOrWhiteSpace(narration.TerminalVisibleSummary)) {
                terminalSummaries.Add($"此前的顺手动作：{narration.TerminalVisibleSummary!.Trim()}");
            }
        }

        ClearPendingTurnEndEffects(root);
        return AsyncAteliaResult<EffectBatchResolution>.Success(
            new EffectBatchResolution(
                terminalSummaries.Count == 0 ? string.Empty : string.Join("\n", terminalSummaries),
                BuildActorEffectSummaries(actorSummaries)
            )
        );
    }

    private static void StartWorkingForActor(
        DurableDict<string> root,
        string actorId,
        ActionDescriptor descriptor,
        string validatorFeedback,
        int remainingTurns
    ) {
        descriptor = NormalizeActionDescriptor(descriptor);
        var workingByActor = GetOrCreateWorkingByActor(root);
        var workOrder = root.Revision.CreateDict<string>();
        workOrder.Upsert(ActorIdKey, actorId);
        workOrder.Upsert(ActionKindKey, descriptor.ActionKind);
        workOrder.Upsert(ActionSummaryKey, descriptor.ActionSummary);
        if (descriptor.ActionPayload is not null) {
            workOrder.Upsert(ActionPayloadKey, descriptor.ActionPayload);
        }

        workOrder.Upsert(PreActionReasonKey, descriptor.PreActionReason);
        workOrder.Upsert(ValidatorFeedbackKey, validatorFeedback);
        workOrder.Upsert(RemainingTurnsKey, remainingTurns);
        workingByActor.Upsert(actorId, workOrder);
        SetActorActiveState(root, actorId, active: false);
    }

    private static ActionDescriptor NormalizeActionDescriptor(ActionDescriptor descriptor) {
        ArgumentNullException.ThrowIfNull(descriptor);
        return new ActionDescriptor(
            NormalizeRequired(descriptor.ActionKind, nameof(descriptor.ActionKind)),
            NormalizeRequired(descriptor.ActionSummary, nameof(descriptor.ActionSummary)),
            string.IsNullOrWhiteSpace(descriptor.ActionPayload) ? null : descriptor.ActionPayload.Trim(),
            NormalizeRequired(descriptor.PreActionReason, nameof(descriptor.PreActionReason))
        );
    }

    private static WorkOrder? TryReadWorkOrder(DurableDict<string> root, string actorId) {
        var workingByActor = GetOrCreateWorkingByActor(root);
        if (!workingByActor.TryGet(actorId, out DurableDict<string>? workOrder)
            || workOrder is null) { return null; }

        _ = workOrder.TryGet(ActionPayloadKey, out string? actionPayload);
        return new WorkOrder(
            workOrder.GetOrThrow<string>(ActorIdKey)!,
            workOrder.GetOrThrow<string>(ActionKindKey)!,
            workOrder.GetOrThrow<string>(ActionSummaryKey)!,
            actionPayload,
            workOrder.GetOrThrow<string>(PreActionReasonKey)!,
            workOrder.GetOrThrow<string>(ValidatorFeedbackKey)!,
            workOrder.GetOrThrow<int>(RemainingTurnsKey)
        );
    }

    private static void UpdateWorkOrderRemainingTurns(DurableDict<string> root, string actorId, int remainingTurns) {
        var workingByActor = GetOrCreateWorkingByActor(root);
        if (!workingByActor.TryGet(actorId, out DurableDict<string>? workOrder)
            || workOrder is null) { return; }

        workOrder.Upsert(RemainingTurnsKey, remainingTurns);
    }

    private static void RemoveWorkOrder(DurableDict<string> root, string actorId) {
        var workingByActor = GetOrCreateWorkingByActor(root);
        _ = workingByActor.Remove(actorId);
    }

    private static async Task<AsyncAteliaResult<EffectBatchResolution>> ResolveBackgroundWorkingEffectsAsync(
        DurableDict<string> root,
        CancellationToken cancellationToken,
        string? excludeActorId = null
    ) {
        var workingByActor = GetOrCreateWorkingByActor(root);
        if (!workingByActor.Keys.Any()) {
            return AsyncAteliaResult<EffectBatchResolution>.Success(
                new EffectBatchResolution(string.Empty, [])
            );
        }

        var terminalSummaries = new List<string>();
        var actorSummaries = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var actorId in workingByActor.Keys.OrderBy(static key => key, StringComparer.Ordinal).ToArray()) {
            if (!string.IsNullOrWhiteSpace(excludeActorId)
                && string.Equals(actorId, excludeActorId, StringComparison.Ordinal)) { continue; }

            var effectResult = await ResolveWorkingEffectForTurnAsync(
                root,
                actorId,
                consumeFutureTurn: true,
                cancellationToken
            ).ConfigureAwait(false);
            if (!effectResult.TryGetValue(out var batch) || batch is null) { return AsyncAteliaResult<EffectBatchResolution>.Failure(effectResult.Error!); }

            MergeActorFacingSummaries(actorSummaries, batch.ActorFacingSummaries);
            if (!string.IsNullOrWhiteSpace(batch.TerminalVisibleSummary)) {
                terminalSummaries.Add(batch.TerminalVisibleSummary);
            }
        }

        return AsyncAteliaResult<EffectBatchResolution>.Success(
            new EffectBatchResolution(
                CombineNonEmptySummaries(terminalSummaries.ToArray()),
                BuildActorEffectSummaries(actorSummaries)
            )
        );
    }

    private static async Task<AsyncAteliaResult<ActionResolution>> ResolveWorkingTurnAndCompleteAsync(
        DurableDict<string> root,
        string actorId,
        string baseSummary,
        string? collectedTurnLead,
        bool consumeFutureTurn,
        CancellationToken cancellationToken
    ) {
        var preludeResult = await PrepareTurnResolutionPreludeAsync(
            root,
            cancellationToken,
            excludeWorkingActorId: actorId
        ).ConfigureAwait(false);
        if (!preludeResult.TryGetValue(out var prelude) || prelude is null) { return AsyncAteliaResult<ActionResolution>.Failure(preludeResult.Error!); }

        return await ResolveWorkingTurnAndCompleteWithPreludeAsync(
            root,
            actorId,
            baseSummary,
            prelude,
            collectedTurnLead,
            consumeFutureTurn,
            cancellationToken
        ).ConfigureAwait(false);
    }

    private static async Task<AsyncAteliaResult<ActionResolution>> ResolveWorkingTurnAndCompleteWithPreludeAsync(
        DurableDict<string> root,
        string actorId,
        string baseSummary,
        TurnResolutionPrelude prelude,
        string? collectedTurnLead,
        bool consumeFutureTurn,
        CancellationToken cancellationToken
    ) {
        var workEffectSummaryResult = await ResolveWorkingEffectForTurnAsync(
            root,
            actorId,
            consumeFutureTurn,
            cancellationToken
        ).ConfigureAwait(false);
        if (!workEffectSummaryResult.TryGetValue(out var workEffectBatch) || workEffectBatch is null) { return AsyncAteliaResult<ActionResolution>.Failure(workEffectSummaryResult.Error!); }

        _ = AdvanceClock(root);
        ApplyActorFacingSummariesForTurn(
            root,
            new[] { prelude.PendingTurnEndEffects, prelude.BackgroundWorkingEffects, workEffectBatch },
            extraActorSummary: new ActorEffectSummary(actorId, baseSummary)
        );

        var combinedSummary = CombineNonEmptySummaries(
            prelude.PendingTurnEndEffects.TerminalVisibleSummary,
            prelude.BackgroundWorkingEffects.TerminalVisibleSummary,
            baseSummary,
            workEffectBatch.TerminalVisibleSummary
        );
        var resolutionSummary = combinedSummary;
        resolutionSummary = PrefixCollectedTurnLead(collectedTurnLead, resolutionSummary);
        return AsyncAteliaResult<ActionResolution>.Success(
            CompleteTurn(root, resolutionSummary, ActorResolutionCommitMode.PreserveExistingAndFillMissing)
        );
    }

    private static async Task<AsyncAteliaResult<EffectBatchResolution>> ResolveWorkingEffectForTurnAsync(
        DurableDict<string> root,
        string actorId,
        bool consumeFutureTurn,
        CancellationToken cancellationToken
    ) {
        var workOrder = TryReadWorkOrder(root, actorId);
        if (workOrder is null) { return AsyncAteliaResult<EffectBatchResolution>.Success(new EffectBatchResolution(string.Empty, [])); }
        if (string.IsNullOrWhiteSpace(workOrder.ActionPayload)) { return AsyncAteliaResult<EffectBatchResolution>.Success(new EffectBatchResolution(string.Empty, [])); }

        InteractionPerception interaction;
        try {
            interaction = BuildInteractionSnapshotFromPayload(workOrder.ActionPayload!);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) {
            return AsyncAteliaResult<EffectBatchResolution>.Failure(
                new TextAdvError(
                    "TextAdv.WorkPayloadInvalid",
                    ex.Message
                )
            );
        }

        var terminalSummaries = new List<string>();
        var actorSummaries = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var remainingTurnsAfterThisTurn = workOrder.RemainingTurns;
        if (consumeFutureTurn) {
            remainingTurnsAfterThisTurn = Math.Max(0, workOrder.RemainingTurns - 1);
            UpdateWorkOrderRemainingTurns(root, actorId, remainingTurnsAfterThisTurn);
        }

        if (interaction.EffectSlots.Contains(PerTurnEndEffectSlot, StringComparer.Ordinal)) {
            var perTurnSummaryResult = await ResolveInteractionEffectAsync(
                root,
                actorId,
                interaction,
                workOrder.PreActionReason,
                PerTurnEndEffectSlot,
                cancellationToken
            ).ConfigureAwait(false);
            if (!perTurnSummaryResult.TryGetValue(out var perTurnNarration) || perTurnNarration is null) { return AsyncAteliaResult<EffectBatchResolution>.Failure(perTurnSummaryResult.Error!); }

            AppendActorFacingSummary(actorSummaries, actorId, perTurnNarration.ActorFacingSummary);
            if (!string.IsNullOrWhiteSpace(perTurnNarration.TerminalVisibleSummary)) {
                terminalSummaries.Add(perTurnNarration.TerminalVisibleSummary!);
            }
        }

        if (consumeFutureTurn && remainingTurnsAfterThisTurn == 0) {
            if (interaction.EffectSlots.Contains(OnCompletionEffectSlot, StringComparer.Ordinal)) {
                var completionSummaryResult = await ResolveInteractionEffectAsync(
                    root,
                    actorId,
                    interaction,
                    workOrder.PreActionReason,
                    OnCompletionEffectSlot,
                    cancellationToken
                ).ConfigureAwait(false);
                if (!completionSummaryResult.TryGetValue(out var completionNarration) || completionNarration is null) { return AsyncAteliaResult<EffectBatchResolution>.Failure(completionSummaryResult.Error!); }

                AppendActorFacingSummary(actorSummaries, actorId, completionNarration.ActorFacingSummary);
                if (!string.IsNullOrWhiteSpace(completionNarration.TerminalVisibleSummary)) {
                    terminalSummaries.Add(completionNarration.TerminalVisibleSummary!);
                }
            }

            RemoveWorkOrder(root, actorId);
            SetActorActiveState(root, actorId, active: true);
        }

        return AsyncAteliaResult<EffectBatchResolution>.Success(
            new EffectBatchResolution(
                CombineNonEmptySummaries(terminalSummaries.ToArray()),
                BuildActorEffectSummaries(actorSummaries)
            )
        );
    }

    private static async Task<AsyncAteliaResult<ActionResolution>> CompleteWorkingActionWithAutoAdvanceAsync(
        DurableDict<string> root,
        AsyncAteliaResult<ActionResolution> resolutionResult,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(root);

        if (!resolutionResult.TryGetValue(out var resolution) || resolution is null) { return AsyncAteliaResult<ActionResolution>.Failure(resolutionResult.Error!); }

        if (HasAnyActiveActor(root)) { return AsyncAteliaResult<ActionResolution>.Success(resolution); }

        var autoSummary = await AutoAdvanceBackgroundWorkUntilAnyActorActiveAsync(root, cancellationToken).ConfigureAwait(false);
        if (!autoSummary.TryGetValue(out var combinedSummary) || string.IsNullOrWhiteSpace(combinedSummary)) { return AsyncAteliaResult<ActionResolution>.Success(resolution); }

        var fullSummary = CombineNonEmptySummaries(resolution.Summary, combinedSummary);
        SetLastResolutionForActiveActors(root, fullSummary);
        return AsyncAteliaResult<ActionResolution>.Success(
            new ActionResolution(fullSummary, DescribeCurrentPerception(root))
        );
    }

    private static async Task<AsyncAteliaResult<string>> AutoAdvanceBackgroundWorkUntilAnyActorActiveAsync(
        DurableDict<string> root,
        CancellationToken cancellationToken
    ) {
        if (HasAnyActiveActor(root)) { return AsyncAteliaResult<string>.Success(string.Empty); }

        var summaries = new List<string>();
        while (!HasAnyActiveActor(root) && GetOrCreateWorkingByActor(root).Keys.Any()) {
            var preludeResult = await PrepareTurnResolutionPreludeAsync(root, cancellationToken).ConfigureAwait(false);
            if (!preludeResult.TryGetValue(out var prelude) || prelude is null) { return AsyncAteliaResult<string>.Failure(preludeResult.Error!); }

            _ = AdvanceClock(root);
            ApplyActorFacingSummariesForTurn(
                root,
                new[] { prelude.PendingTurnEndEffects, prelude.BackgroundWorkingEffects }
            );

            var resolutionSummary = CombineNonEmptySummaries(
                prelude.PendingTurnEndEffects.TerminalVisibleSummary,
                prelude.BackgroundWorkingEffects.TerminalVisibleSummary
            );
            var resolution = CompleteTurn(root, resolutionSummary, ActorResolutionCommitMode.PreserveExistingAndFillMissing);
            summaries.Add(resolution.Summary);
        }

        return AsyncAteliaResult<string>.Success(summaries.Count == 0 ? string.Empty : string.Join("\n\n", summaries));
    }

    private static GmCollectedTurnContext BuildGmCollectedTurnContext(
        DurableDict<string> root,
        IReadOnlyList<LargeActionIntent> intents
    ) {
        return new GmCollectedTurnContext(
            TerminalPlayerActorId,
            intents
                .Select(
                intent => new GmCollectedTurnIntent(
                    intent.ActorId,
                    intent.ActorName,
                    intent.ActorKind,
                    intent.ActionKind,
                    intent.ActionSummary,
                    intent.ActionPayload,
                    intent.PreActionReason,
                    intent.ValidatorFeedback,
                    DescribePerceptionForActor(root, intent.ActorId)
                )
            )
                .ToArray()
        );
    }

    private static string PrefixCollectedTurnLead(string? collectedTurnLead, string summary) {
        if (string.IsNullOrWhiteSpace(collectedTurnLead)) { return summary; }
        return $"{collectedTurnLead.Trim()}\n\n{summary}";
    }

    private static string PrefixDeferredEffectSummary(string? deferredEffectSummary, string summary)
        => string.IsNullOrWhiteSpace(deferredEffectSummary)
            ? summary
            : $"{deferredEffectSummary!.Trim()}\n\n{summary}";

    private static string CombineNonEmptySummaries(params string?[] parts)
        => string.Join(
            "\n",
            parts.Where(static part => !string.IsNullOrWhiteSpace(part)).Select(static part => part!.Trim())
        );

    private static void AppendActorFacingSummary(
        Dictionary<string, List<string>> actorSummaries,
        string actorId,
        string? summary
    ) {
        if (string.IsNullOrWhiteSpace(summary)) { return; }
        actorId = NormalizeRequired(actorId, nameof(actorId));
        if (!actorSummaries.TryGetValue(actorId, out var list)) {
            list = [];
            actorSummaries.Add(actorId, list);
        }

        list.Add(summary.Trim());
    }

    private static void MergeActorFacingSummaries(
        Dictionary<string, List<string>> actorSummaries,
        IReadOnlyList<ActorEffectSummary> summaries
    ) {
        foreach (var summary in summaries) {
            AppendActorFacingSummary(actorSummaries, summary.ActorId, summary.Summary);
        }
    }

    private static IReadOnlyList<ActorEffectSummary> BuildActorEffectSummaries(
        Dictionary<string, List<string>> actorSummaries
    ) {
        return actorSummaries
            .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
            .Select(static entry => new ActorEffectSummary(entry.Key, CombineNonEmptySummaries(entry.Value.ToArray())))
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.Summary))
            .ToArray();
    }

    private static void ApplyActorFacingSummariesForTurn(
        DurableDict<string> root,
        IReadOnlyList<EffectBatchResolution> batches,
        ActorEffectSummary? extraActorSummary = null
    ) {
        var actorSummaries = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var batch in batches) {
            MergeActorFacingSummaries(actorSummaries, batch.ActorFacingSummaries);
        }

        if (extraActorSummary is not null) {
            AppendActorFacingSummary(actorSummaries, extraActorSummary.ActorId, extraActorSummary.Summary);
        }

        foreach (var summary in BuildActorEffectSummaries(actorSummaries)) {
            SetLastResolutionForActor(root, summary.ActorId, summary.Summary);
        }
    }

    private static void MergeActorFacingSummariesIntoExistingResolutions(
        DurableDict<string> root,
        params EffectBatchResolution[] batches
    ) {
        var actorSummaries = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var batch in batches) {
            MergeActorFacingSummaries(actorSummaries, batch.ActorFacingSummaries);
        }

        foreach (var summary in BuildActorEffectSummaries(actorSummaries)) {
            MergeSummaryIntoActorResolution(root, summary.ActorId, summary.Summary);
        }
    }

    private static void MergeSummaryIntoActorResolution(
        DurableDict<string> root,
        string actorId,
        string? summary
    ) {
        if (string.IsNullOrWhiteSpace(summary)) { return; }

        var lastResolutionByActor = GetOrCreateLastResolutionByActor(root);
        if (lastResolutionByActor.TryGet(actorId, out string? existing)
            && !string.IsNullOrWhiteSpace(existing)) {
            lastResolutionByActor.Upsert(
                actorId,
                CombineNonEmptySummaries(summary, existing)
            );
            return;
        }

        lastResolutionByActor.Upsert(actorId, summary.Trim());
    }

    private static InteractionPerception BuildInteractionSnapshotFromPayload(string payload) {
        var interactionId = ParseRequiredPayloadValue(payload, "interactionId");
        var target = ParseRequiredPayloadValue(payload, "target");
        var separatorIndex = target.IndexOf(':', StringComparison.Ordinal);
        if (separatorIndex <= 0 || separatorIndex >= target.Length - 1) { throw new InvalidOperationException($"interaction payload 中的 target 无效：'{target}'。"); }

        var effectSlots = ParseRequiredPayloadValue(payload, "effectSlots")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(static slot => slot.Trim())
            .Where(static slot => !string.IsNullOrWhiteSpace(slot))
            .ToArray();
        return new InteractionPerception(
            interactionId,
            target[..separatorIndex],
            target[(separatorIndex + 1)..],
            ParseRequiredPayloadValue(payload, "actionKind"),
            ParseRequiredPayloadValue(payload, "visibleLabel"),
            ParseOptionalPayloadValue(payload, "preconditionNote"),
            ParseOptionalPayloadValue(payload, "effectNote"),
            int.Parse(ParseRequiredPayloadValue(payload, "turnCost")),
            ParseRequiredPayloadValue(payload, "effectScope"),
            effectSlots.Length == 0 ? [TurnEndEffectSlot] : effectSlots
        );
    }

    private static async Task<AsyncAteliaResult<EffectResolution>> ResolveInteractionEffectAsync(
        DurableDict<string> root,
        string actorId,
        InteractionPerception interaction,
        string preActionReason,
        string effectSlot,
        CancellationToken cancellationToken
    ) {
        var terminalCanObserveActor = string.Equals(actorId, TerminalPlayerActorId, StringComparison.Ordinal)
            || CanTerminalObserveActorAtLocation(root, actorId, GetActorLocationId(root, actorId));
        GmInteractionEffectResolution gmResolution;
        try {
            gmResolution = await GameMasterResolver.ResolveInteractionEffectAsync(
                root,
                new GmInteractionEffectContext(
                    DescribePerceptionForActor(root, actorId),
                    GetActorLocationId(root, actorId),
                    interaction,
                    preActionReason,
                    effectSlot,
                    DescribeCurrentPerception(root),
                    terminalCanObserveActor
                ),
                cancellationToken
            ).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) {
            return AsyncAteliaResult<EffectResolution>.Failure(
                new TextAdvError(
                    "TextAdv.InteractionEffectGmFailed",
                    $"effect-slot 结算依赖 GM Agent，但本次调用失败：{ex.Message}"
                )
            );
        }

        var actorFacingSummary = gmResolution.ActorFacingSummary;
        var terminalVisibleSummary = string.Equals(actorId, TerminalPlayerActorId, StringComparison.Ordinal)
            ? gmResolution.TerminalVisibleSummary ?? actorFacingSummary
            : gmResolution.TerminalVisibleSummary;
        return AsyncAteliaResult<EffectResolution>.Success(
            new EffectResolution(actorFacingSummary, terminalVisibleSummary)
        );
    }

    private static string ParseRequiredPayloadValue(string? payload, string key) {
        var value = ParseOptionalPayloadValue(payload, key);
        if (string.IsNullOrWhiteSpace(value)) { throw new InvalidOperationException($"Large-Action payload 缺少必填字段 '{key}'。"); }

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

    internal static string BuildExplorePayload(string direction, string? focus)
        => focus is null
            ? $"direction={direction}"
            : $"direction={direction}\nfocus={focus}";

    private static bool CanTerminalObserveActorAtLocation(
        DurableDict<string> root,
        string actorId,
        string locationId
    ) {
        if (string.Equals(actorId, TerminalPlayerActorId, StringComparison.Ordinal)) { return true; }
        if (!string.Equals(GetActorLocationId(root, TerminalPlayerActorId), locationId, StringComparison.Ordinal)) { return false; }

        var actor = GetActor(root, actorId);
        var visibility = actor.TryGet(VisibilityKey, out string? rawVisibility)
            ? rawVisibility
            : VisibleValue;
        return IsVisibleToPlayer(visibility);
    }

    private static string? BuildTerminalObservationForRest(DurableDict<string> root, string actorId) {
        var locationId = GetActorLocationId(root, actorId);
        if (!CanTerminalObserveActorAtLocation(root, actorId, locationId)) { return null; }
        return $"你看见{GetActorName(root, actorId)}原地歇了一会。";
    }

    private static string? BuildTerminalObservationForExplore(
        DurableDict<string> root,
        string actorId,
        string sourceLocationId,
        string sourceLocationName,
        string targetLocationId,
        string targetLocationName,
        string direction,
        bool createdNewLocation
    ) {
        if (!CanTerminalObserveActorAtLocation(root, actorId, sourceLocationId)) { return null; }

        if (createdNewLocation) { return $"你看见{GetActorName(root, actorId)}朝 {direction} 方向试探前行，离开了「{sourceLocationName}」的视线范围。"; }

        return string.Equals(sourceLocationId, targetLocationId, StringComparison.Ordinal)
            ? $"你看见{GetActorName(root, actorId)}在「{sourceLocationName}」附近来回试探了一阵。"
            : $"你看见{GetActorName(root, actorId)}沿着通往「{targetLocationName}」的 {direction} 出口离开了「{sourceLocationName}」。";
    }

    private static string? BuildTerminalObservationForInteraction(
        DurableDict<string> root,
        string actorId,
        InteractionPerception interaction
    ) {
        var actorLocationId = GetActorLocationId(root, actorId);
        if (!CanTerminalObserveActorAtLocation(root, actorId, actorLocationId)) { return null; }

        return interaction.TargetKind switch {
            "location" when string.Equals(interaction.TargetId, actorLocationId, StringComparison.Ordinal)
                => $"你看见{GetActorName(root, actorId)}试着去做「{interaction.VisibleLabel}」。",
            "item" when IsObservableLocationItem(root, interaction.TargetId, actorLocationId)
                => $"你看见{GetActorName(root, actorId)}对眼前的物件试了试「{interaction.VisibleLabel}」。",
            "actor" when IsObservableActor(root, interaction.TargetId, actorLocationId)
                => $"你看见{GetActorName(root, actorId)}对{GetActorName(root, interaction.TargetId)}试了试「{interaction.VisibleLabel}」。",
            _ => null
        };
    }

    private static string? BuildTerminalObservationForEffect(
        DurableDict<string> root,
        string actorId,
        InteractionPerception interaction,
        string effectSlot
    ) {
        var actorLocationId = GetActorLocationId(root, actorId);
        if (!CanTerminalObserveActorAtLocation(root, actorId, actorLocationId)) { return null; }

        return effectSlot switch {
            TurnEndEffectSlot => $"你注意到{GetActorName(root, actorId)}刚才着手的「{interaction.VisibleLabel}」这时见了分晓。",
            PerTurnEndEffectSlot => $"你看见{GetActorName(root, actorId)}手头的「{interaction.VisibleLabel}」又往前推进了一点。",
            OnCompletionEffectSlot => $"你看见{GetActorName(root, actorId)}手头的「{interaction.VisibleLabel}」终于做完了。",
            _ => $"你注意到{GetActorName(root, actorId)}那边的「{interaction.VisibleLabel}」有了新的结果。"
        };
    }

    private static bool IsObservableLocationItem(DurableDict<string> root, string itemId, string locationId) {
        var world = root.GetOrThrow<DurableDict<string>>(WorldKey)!;
        if (!world.TryGet(ItemsKey, out DurableDict<string>? items)
            || items is null
            || !items.TryGet(itemId, out DurableDict<string>? item)
            || item is null) { return false; }

        if (item.TryGet(OwnerActorIdKey, out string? ownerActorId) && !string.IsNullOrWhiteSpace(ownerActorId)) { return false; }

        if (!item.TryGet(LocationIdKey, out string? itemLocationId)
            || !string.Equals(itemLocationId, locationId, StringComparison.Ordinal)) { return false; }

        var visibility = item.TryGet(VisibilityKey, out string? rawVisibility)
            ? rawVisibility
            : VisibleValue;
        return IsVisibleToPlayer(visibility);
    }

    private static bool IsObservableActor(DurableDict<string> root, string actorId, string locationId) {
        var actor = GetActor(root, actorId);
        if (!actor.TryGet(LocationIdKey, out string? actorLocationId)
            || !string.Equals(actorLocationId, locationId, StringComparison.Ordinal)) { return false; }

        var visibility = actor.TryGet(VisibilityKey, out string? rawVisibility)
            ? rawVisibility
            : VisibleValue;
        return IsVisibleToPlayer(visibility);
    }

    private static string BuildDeterministicInteractionSummary(InteractionPerception interaction) {
        if (!string.IsNullOrWhiteSpace(interaction.EffectNote)) { return interaction.EffectNote!; }

        return $"你执行了「{interaction.VisibleLabel}」。当前原型只推进时钟；更具体的后果需要真实 GM Agent 或后续规则工具结算。";
    }

    private static string BuildDeterministicImmediateInteractionSummary(InteractionPerception interaction) {
        if (!string.IsNullOrWhiteSpace(interaction.EffectNote)) { return interaction.EffectNote!; }

        return $"你顺手试了试「{interaction.VisibleLabel}」。当前原型暂时只记录这次即时尝试，并把它留在本回合步骤里。";
    }

    private static string BuildDeterministicDeferredInteractionSummary(InteractionPerception interaction, string effectSlot) {
        if (!string.IsNullOrWhiteSpace(interaction.EffectNote)) { return interaction.EffectNote!; }

        return effectSlot switch {
            TurnEndEffectSlot => $"到本回合结束时，你此前试着进行的「{interaction.VisibleLabel}」终于见了分晓。",
            PerTurnEndEffectSlot => $"这回合末，你持续进行的「{interaction.VisibleLabel}」又推进了一点。",
            OnCompletionEffectSlot => $"经过持续忙碌，「{interaction.VisibleLabel}」终于做完了。",
            _ => $"「{interaction.VisibleLabel}」在 {effectSlot} 阶段产生了结果。"
        };
    }

    private static string BuildDeferredTurnEndInteractionSummary(InteractionPerception interaction)
        => $"你着手去做「{interaction.VisibleLabel}」，它会在本回合结束时统一见分晓。";

    private static string BuildWorkingStartSummary(InteractionPerception interaction)
        => $"你开始持续处理「{interaction.VisibleLabel}」，若不被打断，接下来几个回合会一直忙于这件事。";

    private static string BuildWorkingContinueLead(InteractionPerception interaction)
        => $"你继续处理「{interaction.VisibleLabel}」。";

    private static string BuildWorkingCompletionLead(InteractionPerception interaction)
        => $"你把「{interaction.VisibleLabel}」继续做到了收尾阶段。";

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
