using Atelia.StateJournal;
using Atelia.TextEditScript;

namespace Atelia.TextAdv;

internal static class GameSimulation {
    private const string WorldKey = "world";
    private const string GameKey = "game";
    private const string LocationsKey = "locations";
    private const string ItemsKey = "items";
    private const string ActorsKey = "actors";
    private const string InteractionsKey = "interactions";
    private const string InitialLocationKey = "initialLocation";
    private const string ActiveActorIdsKey = "activeActorIds";
    private const string PlayerKey = "player";
    private const string PlayerLocationKey = "location";
    private const string MemoryNotebookKey = "memoryNotebook";
    private const string TerminalPlayerActorId = "player";
    private const string DayKey = "day";
    private const string SlotKey = "slot";
    private const string SlotsPerDayKey = "slotsPerDay";
    private const string CurrentTurnKey = "currentTurn";
    private const string TurnHistoryKey = "turnHistory";
    private const string CompletedTurnCountKey = "completedTurnCount";
    private const string LastResolutionKey = "lastResolution";
    private const string LastResolutionByActorKey = "lastResolutionByActor";
    private const string StartDayKey = "startDay";
    private const string StartSlotKey = "startSlot";
    private const string StartLocationIdKey = "startLocationId";
    private const string NotebookSnapshotKey = "notebookSnapshot";
    private const string NextStepNumberKey = "nextStepNumber";
    private const string AcceptedStepsKey = "acceptedSteps";
    private const string TurnOwnerActorIdKey = "turnOwnerActorId";
    private const string AcceptedStepsByActorKey = "acceptedStepsByActor";
    private const string LargeActionByActorKey = "largeActionByActor";
    private const string BarrierStateKey = "barrierState";
    private const string TurnNumberKey = "turnNumber";
    private const string ResolutionSummaryKey = "resolutionSummary";
    private const string EndingNotebookKey = "endingNotebook";
    private const string CollectingTerminalBarrierState = "collecting-terminal";
    private const string CollectingLlmBarrierState = "collecting-llm";
    private const string ReadyForGmBarrierState = "ready-for-gm";
    private const string ActionKindKey = "actionKind";
    private const string ActionSummaryKey = "actionSummary";
    private const string ActionPayloadKey = "actionPayload";
    private const string PreActionReasonKey = "preActionReason";
    private const string ValidatorFeedbackKey = "validatorFeedback";
    private const string EndsTurnKey = "endsTurn";
    private const string SourceStepNumberKey = "sourceStepNumber";
    private const string NameKey = "name";
    private const string KindKey = "kind";
    private const string DescriptionKey = "description";
    private const string ProfileNoteKey = "profileNote";
    private const string ActiveKey = "active";
    private const string ExitsKey = "exits";
    private const string LocationIdKey = "locationId";
    private const string OwnerActorIdKey = "ownerActorId";
    private const string VisibilityKey = "visibility";
    private const string TargetKindKey = "targetKind";
    private const string TargetIdKey = "targetId";
    private const string ActionKindLedgerKey = "actionKind";
    private const string VisibleLabelKey = "visibleLabel";
    private const string PreconditionNoteKey = "preconditionNote";
    private const string EffectNoteKey = "effectNote";
    private const string VisibleValue = "visible";
    private const string DiscoveredValue = "discovered";
    private const int DefaultSlotsPerDay = 4;

    private sealed record LargeActionIntent(
        string ActorId,
        string ActorName,
        string ActorKind,
        string ActionKind,
        string ActionSummary,
        string? ActionPayload,
        string PreActionReason,
        string ValidatorFeedback
    );

    private enum ActorResolutionCommitMode {
        ReplaceAllWithSummary,
        PreserveExistingAndFillMissing
    }

    internal static DurableDict<string> CreateNewWorld(Repository repo) {
        var revResult = repo.GetOrCreateBranch("main");
        var rev = revResult.Value!;

        var root = rev.CreateDict<string>();
        var world = rev.CreateDict<string>();
        var game = rev.CreateDict<string>();
        var locations = rev.CreateDict<string>();
        var items = rev.CreateDict<string>();
        var actors = rev.CreateDict<string>();
        var interactions = rev.CreateDict<string>();
        var activeActorIds = rev.CreateDict<string>();
        var player = rev.CreateDict<string>();
        var notebook = CreateNotebookText(rev, string.Empty);
        var turnHistory = rev.CreateDict<string>();
        var lastResolutionByActor = rev.CreateDict<string>();

        var beachId = "beach";
        var forestId = "forest";

        var beach = CreateLocation(rev, "沙滩",
            "一片开阔的沙滩，海浪轻拍着海岸线。"
            + "细白的沙子在阳光下闪闪发光。远处可以看到茂密的树林。"
        );
        var forest = CreateLocation(rev, "密林",
            "茂密的树林遮天蔽日，空气中弥漫着泥土和树叶的气味。"
            + "树影间隐约能听到鸟鸣声。南边透过树缝可以看到沙滩的亮光。"
        );

        AddExit(beach, "north", forestId);
        AddExit(forest, "south", beachId);

        locations.Upsert(beachId, beach);
        locations.Upsert(forestId, forest);

        var terminalActor = CreateActor(
            rev,
            kind: "terminal-player",
            name: "你",
            profileNote: "通过终端命令操作的玩家角色。",
            locationId: beachId,
            active: true
        );
        terminalActor.Upsert(MemoryNotebookKey, notebook);
        actors.Upsert(TerminalPlayerActorId, terminalActor);
        activeActorIds.Upsert(TerminalPlayerActorId, TerminalPlayerActorId);

        world.Upsert(LocationsKey, locations);
        world.Upsert(ItemsKey, items);
        world.Upsert(ActorsKey, actors);
        world.Upsert(InteractionsKey, interactions);
        world.Upsert(InitialLocationKey, beachId);

        player.Upsert(PlayerLocationKey, beachId);
        player.Upsert(MemoryNotebookKey, notebook);

        game.Upsert(DayKey, 1);
        game.Upsert(SlotKey, 1);
        game.Upsert(SlotsPerDayKey, DefaultSlotsPerDay);
        game.Upsert(ActiveActorIdsKey, activeActorIds);
        game.Upsert(CompletedTurnCountKey, 0);
        game.Upsert(TurnHistoryKey, turnHistory);
        game.Upsert(LastResolutionByActorKey, lastResolutionByActor);
        game.Upsert(CurrentTurnKey, CreateCurrentTurnState(rev, day: 1, slot: 1, beachId, string.Empty));

        root.Upsert(WorldKey, world);
        root.Upsert(GameKey, game);
        root.Upsert(PlayerKey, player);

        _ = repo.Commit(root).Value;
        return root;
    }

    internal static PerceptionBundle DescribeCurrentPerception(DurableDict<string> root)
        => DescribePerceptionForActor(root, TerminalPlayerActorId);

    internal static PerceptionBundle DescribePerceptionForActor(DurableDict<string> root, string actorId) {
        actorId = NormalizeRequired(actorId, nameof(actorId));
        var actor = GetActor(root, actorId);
        var game = GetGame(root);
        var day = game.GetOrThrow<int>(DayKey);
        var slot = game.GetOrThrow<int>(SlotKey);
        var slotsPerDay = game.GetOrThrow<int>(SlotsPerDayKey);
        var lastResolution = ReadLastResolutionForActor(game, actorId);
        var notebookSnapshot = GetNotebookSnapshot(root, actorId);
        var acceptedSteps = ReadAcceptedSteps(root, actorId);
        var locationId = actor.GetOrThrow<string>(LocationIdKey)!;
        var actorKind = actor.TryGet(KindKey, out string? rawKind) && !string.IsNullOrWhiteSpace(rawKind)
            ? rawKind
            : "npc";
        var actorName = actor.TryGet(NameKey, out string? rawName) && !string.IsNullOrWhiteSpace(rawName)
            ? rawName
            : actorId;
        var actorProfileNote = actor.TryGet(ProfileNoteKey, out string? rawProfileNote) && rawProfileNote is not null
            ? rawProfileNote
            : string.Empty;

        return new PerceptionBundle(
            actorId,
            actorKind,
            actorName,
            actorProfileNote,
            day,
            slot,
            slotsPerDay,
            DescribeLocation(root, locationId, actorId),
            EnumerateVisibleItemsOwnedByActor(root, actorId).ToArray(),
            notebookSnapshot,
            acceptedSteps,
            lastResolution
        );
    }

    internal static LocationPerception DescribeCurrentLocation(DurableDict<string> root)
        => DescribeLocation(root, GetPlayerLocationId(root), TerminalPlayerActorId);

    internal static TurnCollectionStatus DescribeCurrentTurnStatus(DurableDict<string> root) {
        var game = GetGame(root);
        var currentTurn = EnsureCurrentTurnPhase4Fields(root);
        var largeActionByActor = currentTurn.GetOrThrow<DurableDict<string>>(LargeActionByActorKey)!;
        var actorStatuses = EnumerateActiveActorIds(root)
            .Select(actorId => {
                var actor = GetActor(root, actorId);
                var kind = actor.TryGet(KindKey, out string? rawKind) && !string.IsNullOrWhiteSpace(rawKind)
                    ? rawKind
                    : "npc";
                var name = actor.TryGet(NameKey, out string? rawName) && !string.IsNullOrWhiteSpace(rawName)
                    ? rawName
                    : actorId;
                var active = !actor.TryGet(ActiveKey, out bool rawActive) || rawActive;
                var hasLargeAction = largeActionByActor.TryGet(actorId, out DurableDict<string>? action)
                    && action is not null;
                string? actionKind = null;
                string? actionSummary = null;
                if (hasLargeAction && action is not null) {
                    _ = action.TryGet(ActionKindKey, out actionKind);
                    _ = action.TryGet(ActionSummaryKey, out actionSummary);
                }

                return new TurnActorStatus(
                    actorId,
                    kind,
                    name,
                    active,
                    hasLargeAction,
                    actionKind,
                    actionSummary
                );
            })
            .ToArray();

        var allSubmitted = actorStatuses.Length > 0
            && actorStatuses.All(static actor => actor.HasSubmittedLargeAction);
        return new TurnCollectionStatus(
            game.GetOrThrow<int>(DayKey),
            game.GetOrThrow<int>(SlotKey),
            game.GetOrThrow<int>(SlotsPerDayKey),
            currentTurn.GetOrThrow<string>(TurnOwnerActorIdKey)!,
            currentTurn.GetOrThrow<string>(BarrierStateKey)!,
            allSubmitted,
            actorStatuses
        );
    }

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
        locationId = string.IsNullOrWhiteSpace(locationId) ? GetPlayerLocationId(root) : locationId.Trim();

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

    internal static AteliaResult<InteractionPerception> TryGetVisibleInteraction(
        PerceptionBundle perception,
        string interactionId
    ) {
        interactionId = NormalizeRequired(interactionId, nameof(interactionId));
        var interactions = EnumerateVisibleInteractions(perception)
            .Where(interaction => string.Equals(interaction.InteractionId, interactionId, StringComparison.Ordinal))
            .ToArray();

        if (interactions.Length == 1) { return interactions[0]; }

        if (interactions.Length > 1) {
            return AteliaResult<InteractionPerception>.Failure(
                new TextAdvError(
                    "TextAdv.AmbiguousInteraction",
                    $"InteractionId '{interactionId}' 在当前感知中出现了多次；请先修复世界账本。"
                )
            );
        }

        var available = string.Join(", ", EnumerateVisibleInteractions(perception).Select(static interaction => interaction.InteractionId));
        if (string.IsNullOrWhiteSpace(available)) { available = "(none)"; }
        return AteliaResult<InteractionPerception>.Failure(
            new TextAdvError(
                "TextAdv.InteractionNotVisible",
                $"当前看不到 Interaction '{interactionId}'。可见 interaction: {available}"
            )
        );
    }

    internal static string BuildInteractionPayload(InteractionPerception interaction) {
        var lines = new List<string>
        {
            $"interactionId={interaction.InteractionId}",
            $"target={interaction.TargetKind}:{interaction.TargetId}",
            $"actionKind={interaction.ActionKind}",
            $"visibleLabel={interaction.VisibleLabel}",
        };
        if (!string.IsNullOrWhiteSpace(interaction.PreconditionNote)) {
            lines.Add($"preconditionNote={interaction.PreconditionNote}");
        }

        return string.Join("\n", lines);
    }

    internal static AteliaResult<PerceptionBundle> MovePlayer(DurableDict<string> root, string direction) {
        var player = root.GetOrThrow<DurableDict<string>>(PlayerKey)!;
        var currentLocationId = GetPlayerLocationId(root);
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
        player.Upsert(PlayerLocationKey, targetLocationId);
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

        var currentLocationId = GetPlayerLocationId(root);
        var currentLocation = GetLocation(root, currentLocationId);
        var currentLocationName = currentLocation.GetOrThrow<string>(NameKey)!;
        var currentExits = currentLocation.GetOrThrow<DurableDict<string>>(ExitsKey)!;
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

        var currentLocationId = GetPlayerLocationId(root);
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
            && !string.Equals(GetPlayerLocationId(root), currentLocationId, StringComparison.Ordinal)) {
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

        var moveResult = gmTools.MovePlayerTo(targetLocationId);
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
                GetPlayerLocationId(root),
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

    private static string GetPlayerLocationId(DurableDict<string> root) {
        var actors = GetActors(root);
        if (actors.TryGet(TerminalPlayerActorId, out DurableDict<string>? actor)
            && actor is not null
            && actor.TryGet(LocationIdKey, out string? actorLocationId)
            && !string.IsNullOrWhiteSpace(actorLocationId)) {
            return actorLocationId;
        }

        var player = root.GetOrThrow<DurableDict<string>>(PlayerKey)!;
        return player.GetOrThrow<string>(PlayerLocationKey)!;
    }

    private static LocationPerception DescribeLocation(DurableDict<string> root, string locationId, string observerActorId) {
        var location = GetLocation(root, locationId);
        var name = location.GetOrThrow<string>(NameKey)!;
        var description = location.GetOrThrow<string>(DescriptionKey)!;
        var exits = EnumerateExits(root, locationId).ToArray();
        var items = EnumerateVisibleItemsAtLocation(root, locationId).ToArray();
        var actors = EnumerateVisibleActorsAtLocation(root, locationId, observerActorId).ToArray();
        var interactions = EnumerateVisibleInteractions(root, "location", locationId).ToArray();
        return new LocationPerception(locationId, name, description, exits, items, actors, interactions);
    }

    private static IEnumerable<LocationExitPerception> EnumerateExits(
        DurableDict<string> root,
        string locationId
    ) {
        var location = GetLocation(root, locationId);
        var exits = location.GetOrThrow<DurableDict<string>>(ExitsKey)!;

        foreach (var direction in exits.Keys) {
            var targetLocationId = exits.GetOrThrow<string>(direction)!;
            var targetLocation = GetLocation(root, targetLocationId);
            var targetName = targetLocation.GetOrThrow<string>(NameKey)!;
            yield return new LocationExitPerception(direction, targetLocationId, targetName);
        }
    }

    private static DurableDict<string> GetLocation(DurableDict<string> root, string locationId) {
        var world = root.GetOrThrow<DurableDict<string>>(WorldKey)!;
        var locations = world.GetOrThrow<DurableDict<string>>(LocationsKey)!;
        return locations.GetOrThrow<DurableDict<string>>(locationId)!;
    }

    private static IEnumerable<ItemPerception> EnumerateVisibleItemsAtLocation(
        DurableDict<string> root,
        string locationId
    ) {
        var world = root.GetOrThrow<DurableDict<string>>(WorldKey)!;
        if (!world.TryGet(ItemsKey, out DurableDict<string>? items) || items is null) { yield break; }

        foreach (var itemId in items.Keys.OrderBy(static key => key, StringComparer.Ordinal)) {
            var item = items.GetOrThrow<DurableDict<string>>(itemId)!;
            if (item.TryGet(OwnerActorIdKey, out string? ownerActorId)
                && !string.IsNullOrWhiteSpace(ownerActorId)) {
                continue;
            }

            if (!item.TryGet(LocationIdKey, out string? itemLocationId)
                || !string.Equals(itemLocationId, locationId, StringComparison.Ordinal)) {
                continue;
            }

            var visibility = item.TryGet(VisibilityKey, out string? rawVisibility)
                ? rawVisibility
                : VisibleValue;
            if (!IsVisibleToPlayer(visibility)) { continue; }

            yield return new ItemPerception(
                itemId,
                item.GetOrThrow<string>(NameKey)!,
                item.GetOrThrow<string>(DescriptionKey)!,
                EnumerateVisibleInteractions(root, "item", itemId).ToArray()
            );
        }
    }

    private static IEnumerable<ItemPerception> EnumerateVisibleItemsOwnedByActor(
        DurableDict<string> root,
        string actorId
    ) {
        var world = root.GetOrThrow<DurableDict<string>>(WorldKey)!;
        if (!world.TryGet(ItemsKey, out DurableDict<string>? items) || items is null) { yield break; }

        foreach (var itemId in items.Keys.OrderBy(static key => key, StringComparer.Ordinal)) {
            var item = items.GetOrThrow<DurableDict<string>>(itemId)!;
            if (!item.TryGet(OwnerActorIdKey, out string? ownerActorId)
                || !string.Equals(ownerActorId, actorId, StringComparison.Ordinal)) {
                continue;
            }

            var visibility = item.TryGet(VisibilityKey, out string? rawVisibility)
                ? rawVisibility
                : VisibleValue;
            if (!IsVisibleToPlayer(visibility)) { continue; }

            yield return new ItemPerception(
                itemId,
                item.GetOrThrow<string>(NameKey)!,
                item.GetOrThrow<string>(DescriptionKey)!,
                EnumerateVisibleInteractions(root, "item", itemId).ToArray()
            );
        }
    }

    private static IEnumerable<ActorPerception> EnumerateVisibleActorsAtLocation(
        DurableDict<string> root,
        string locationId,
        string observerActorId
    ) {
        var world = root.GetOrThrow<DurableDict<string>>(WorldKey)!;
        if (!world.TryGet(ActorsKey, out DurableDict<string>? actors) || actors is null) { yield break; }

        foreach (var actorId in actors.Keys.OrderBy(static key => key, StringComparer.Ordinal)) {
            if (string.Equals(actorId, observerActorId, StringComparison.Ordinal)) { continue; }

            var actor = actors.GetOrThrow<DurableDict<string>>(actorId)!;
            if (!actor.TryGet(LocationIdKey, out string? actorLocationId)
                || !string.Equals(actorLocationId, locationId, StringComparison.Ordinal)) {
                continue;
            }

            var visibility = actor.TryGet(VisibilityKey, out string? rawVisibility)
                ? rawVisibility
                : VisibleValue;
            if (!IsVisibleToPlayer(visibility)) { continue; }

            var kind = actor.TryGet(KindKey, out string? rawKind) && !string.IsNullOrWhiteSpace(rawKind)
                ? rawKind
                : "npc";
            var profileNote = actor.TryGet(ProfileNoteKey, out string? rawProfileNote)
                ? rawProfileNote ?? string.Empty
                : string.Empty;

            yield return new ActorPerception(
                actorId,
                kind,
                actor.GetOrThrow<string>(NameKey)!,
                profileNote,
                EnumerateVisibleInteractions(root, "actor", actorId).ToArray()
            );
        }
    }

    private static IEnumerable<InteractionPerception> EnumerateVisibleInteractions(
        DurableDict<string> root,
        string targetKind,
        string targetId
    ) {
        var world = root.GetOrThrow<DurableDict<string>>(WorldKey)!;
        if (!world.TryGet(InteractionsKey, out DurableDict<string>? interactions) || interactions is null) { yield break; }

        foreach (var interactionId in interactions.Keys.OrderBy(static key => key, StringComparer.Ordinal)) {
            var interaction = interactions.GetOrThrow<DurableDict<string>>(interactionId)!;
            var actualTargetKind = interaction.GetOrThrow<string>(TargetKindKey)!;
            var actualTargetId = interaction.GetOrThrow<string>(TargetIdKey)!;
            if (!string.Equals(actualTargetKind, targetKind, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(actualTargetId, targetId, StringComparison.Ordinal)) {
                continue;
            }

            var visibility = interaction.TryGet(VisibilityKey, out string? rawVisibility)
                ? rawVisibility
                : VisibleValue;
            if (!IsVisibleToPlayer(visibility)) { continue; }

            _ = interaction.TryGet(PreconditionNoteKey, out string? preconditionNote);
            _ = interaction.TryGet(EffectNoteKey, out string? effectNote);
            yield return new InteractionPerception(
                interactionId,
                actualTargetKind,
                actualTargetId,
                interaction.GetOrThrow<string>(ActionKindLedgerKey)!,
                interaction.GetOrThrow<string>(VisibleLabelKey)!,
                preconditionNote,
                effectNote
            );
        }
    }

    private static IEnumerable<InteractionPerception> EnumerateVisibleInteractions(PerceptionBundle perception) {
        foreach (var interaction in perception.Location.Interactions) {
            yield return interaction;
        }

        foreach (var item in perception.Location.Items) {
            foreach (var interaction in item.Interactions) {
                yield return interaction;
            }
        }

        foreach (var item in perception.InventoryItems) {
            foreach (var interaction in item.Interactions) {
                yield return interaction;
            }
        }

        foreach (var actor in perception.Location.Actors) {
            foreach (var interaction in actor.Interactions) {
                yield return interaction;
            }
        }
    }

    private static bool IsVisibleToPlayer(string? visibility)
        => string.IsNullOrWhiteSpace(visibility)
            || string.Equals(visibility, VisibleValue, StringComparison.OrdinalIgnoreCase)
            || string.Equals(visibility, DiscoveredValue, StringComparison.OrdinalIgnoreCase);

    private static string? ReadLastResolutionForActor(DurableDict<string> game, string actorId) {
        if (game.TryGet(LastResolutionByActorKey, out DurableDict<string>? lastResolutionByActor)
            && lastResolutionByActor is not null
            && lastResolutionByActor.TryGet(actorId, out string? actorResolution)
            && !string.IsNullOrWhiteSpace(actorResolution)) {
            return actorResolution;
        }

        return TryGetOptionalString(game, LastResolutionKey);
    }

    private static void SetLastResolutionForActiveActors(DurableDict<string> root, string summary) {
        summary = NormalizeRequired(summary, nameof(summary));
        var game = GetGame(root);
        var lastResolutionByActor = root.Revision.CreateDict<string>();
        foreach (var actorId in EnumerateActiveActorIds(root)) {
            lastResolutionByActor.Upsert(actorId, summary);
        }

        game.Upsert(LastResolutionByActorKey, lastResolutionByActor);
        game.Upsert(LastResolutionKey, summary);
    }

    private static void SetLastResolutionForMissingActiveActors(DurableDict<string> root, string fallbackSummary) {
        fallbackSummary = NormalizeRequired(fallbackSummary, nameof(fallbackSummary));
        var game = GetGame(root);
        var lastResolutionByActor = GetOrCreateLastResolutionByActor(root);
        foreach (var actorId in EnumerateActiveActorIds(root)) {
            if (!lastResolutionByActor.TryGet(actorId, out string? actorResolution)
                || string.IsNullOrWhiteSpace(actorResolution)) {
                lastResolutionByActor.Upsert(actorId, fallbackSummary);
            }
        }

        game.Upsert(LastResolutionKey, fallbackSummary);
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

    private static void ClearLastResolutionByActor(DurableDict<string> root) {
        var game = GetGame(root);
        game.Upsert(LastResolutionByActorKey, root.Revision.CreateDict<string>());
    }

    private static DurableDict<string> GetOrCreateLastResolutionByActor(DurableDict<string> root) {
        var game = GetGame(root);
        if (game.TryGet(LastResolutionByActorKey, out DurableDict<string>? lastResolutionByActor)
            && lastResolutionByActor is not null) {
            return lastResolutionByActor;
        }

        lastResolutionByActor = root.Revision.CreateDict<string>();
        game.Upsert(LastResolutionByActorKey, lastResolutionByActor);
        return lastResolutionByActor;
    }

    private static DurableDict<string> GetGame(DurableDict<string> root)
        => root.GetOrThrow<DurableDict<string>>(GameKey)!;

    private static DurableDict<string> GetPlayer(DurableDict<string> root)
        => root.GetOrThrow<DurableDict<string>>(PlayerKey)!;

    private static DurableText GetNotebook(DurableDict<string> root)
        => GetNotebook(root, TerminalPlayerActorId);

    private static DurableText GetNotebook(DurableDict<string> root, string actorId) {
        var actor = GetActor(root, actorId);
        if (actor.TryGet(MemoryNotebookKey, out DurableText? notebook) && notebook is not null) {
            return notebook;
        }

        if (string.Equals(actorId, TerminalPlayerActorId, StringComparison.Ordinal)) {
            var playerNotebook = GetPlayer(root).GetOrThrow<DurableText>(MemoryNotebookKey)!;
            actor.Upsert(MemoryNotebookKey, playerNotebook);
            return playerNotebook;
        }

        notebook = CreateNotebookText(root.Revision, string.Empty);
        actor.Upsert(MemoryNotebookKey, notebook);
        return notebook;
    }

    private static DurableDict<string> GetActors(DurableDict<string> root) {
        var world = root.GetOrThrow<DurableDict<string>>(WorldKey)!;
        return world.GetOrThrow<DurableDict<string>>(ActorsKey)!;
    }

    private static DurableDict<string> GetActor(DurableDict<string> root, string actorId) {
        var actors = GetActors(root);
        return actors.GetOrThrow<DurableDict<string>>(actorId)!;
    }

    private static DurableDict<string> GetCurrentTurn(DurableDict<string> root) {
        var game = GetGame(root);
        return game.GetOrThrow<DurableDict<string>>(CurrentTurnKey)!;
    }

    private static TextBlockSnapshotDocument GetNotebookSnapshot(DurableDict<string> root)
        => GetNotebookSnapshot(root, TerminalPlayerActorId);

    private static TextBlockSnapshotDocument GetNotebookSnapshot(DurableDict<string> root, string actorId) {
        var notebook = GetNotebook(root, actorId);
        return new TextBlockSnapshotDocument(
            notebook.GetAllBlocks()
                .Select(static block => new TextBlockSnapshot(block.Id, block.Content))
                .ToArray()
        );
    }

    private static string GetNotebookContent(TextBlockSnapshotDocument snapshot) {
        return string.Join("\n", snapshot.Blocks.Select(static block => block.Content));
    }

    private static DurableText CreateNotebookText(Revision rev, string content) {
        var notebook = rev.CreateText();
        if (!string.IsNullOrEmpty(content)) {
            notebook.LoadText(content);
        }

        return notebook;
    }

    private static DurableDict<string> CreateCurrentTurnState(
        Revision rev,
        int day,
        int slot,
        string locationId,
        string notebookSnapshot
    ) {
        var currentTurn = rev.CreateDict<string>();
        var acceptedSteps = rev.CreateDict<string>();
        var acceptedStepsByActor = rev.CreateDict<string>();
        var largeActionByActor = rev.CreateDict<string>();

        currentTurn.Upsert(StartDayKey, day);
        currentTurn.Upsert(StartSlotKey, slot);
        currentTurn.Upsert(StartLocationIdKey, locationId);
        currentTurn.Upsert(NotebookSnapshotKey, notebookSnapshot);
        currentTurn.Upsert(NextStepNumberKey, 1);
        currentTurn.Upsert(AcceptedStepsKey, acceptedSteps);
        currentTurn.Upsert(TurnOwnerActorIdKey, TerminalPlayerActorId);
        currentTurn.Upsert(AcceptedStepsByActorKey, acceptedStepsByActor);
        currentTurn.Upsert(LargeActionByActorKey, largeActionByActor);
        currentTurn.Upsert(BarrierStateKey, CollectingTerminalBarrierState);
        acceptedStepsByActor.Upsert(TerminalPlayerActorId, acceptedSteps);

        return currentTurn;
    }

    private static IReadOnlyList<TurnStep> ReadAcceptedSteps(DurableDict<string> root) {
        return ReadAcceptedSteps(root, TerminalPlayerActorId);
    }

    private static IReadOnlyList<TurnStep> ReadAcceptedSteps(DurableDict<string> root, string actorId) {
        var acceptedSteps = GetAcceptedStepsForActor(root, actorId, createIfMissing: false);
        if (acceptedSteps is null) { return Array.Empty<TurnStep>(); }

        return ReadAcceptedStepList(acceptedSteps);
    }

    private static IReadOnlyList<TurnStep> ReadAcceptedStepList(DurableDict<string> acceptedSteps) {
        return acceptedSteps.Keys
            .OrderBy(static key => key, StringComparer.Ordinal)
            .Select(
            key => {
                var step = acceptedSteps.GetOrThrow<DurableDict<string>>(key)!;
                var numberPart = key.Split('-').Last();
                var stepNumber = int.Parse(numberPart);
                _ = step.TryGet(ActionPayloadKey, out string? actionPayload);
                return new TurnStep(
                    stepNumber,
                    step.GetOrThrow<string>(ActionKindKey)!,
                    step.GetOrThrow<string>(ActionSummaryKey)!,
                    actionPayload,
                    step.GetOrThrow<string>(PreActionReasonKey)!,
                    step.GetOrThrow<string>(ValidatorFeedbackKey)!,
                    step.GetOrThrow<bool>(EndsTurnKey)
                );
            }
        )
            .ToArray();
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
        EnsureCurrentTurnPhase4Fields(root);
        var turnHistory = game.GetOrThrow<DurableDict<string>>(TurnHistoryKey)!;
        var completedTurnCount = game.GetOrThrow<int>(CompletedTurnCountKey);
        var turnNumber = completedTurnCount + 1;
        var archivedTurn = rev.CreateDict<string>();

        archivedTurn.Upsert(TurnNumberKey, turnNumber);
        archivedTurn.Upsert(StartDayKey, currentTurn.GetOrThrow<int>(StartDayKey));
        archivedTurn.Upsert(StartSlotKey, currentTurn.GetOrThrow<int>(StartSlotKey));
        archivedTurn.Upsert(StartLocationIdKey, currentTurn.GetOrThrow<string>(StartLocationIdKey)!);
        archivedTurn.Upsert(NotebookSnapshotKey, currentTurn.GetOrThrow<string>(NotebookSnapshotKey)!);
        archivedTurn.Upsert(AcceptedStepsKey, currentTurn.GetOrThrow<DurableDict<string>>(AcceptedStepsKey)!);
        archivedTurn.Upsert(AcceptedStepsByActorKey, currentTurn.GetOrThrow<DurableDict<string>>(AcceptedStepsByActorKey)!);
        archivedTurn.Upsert(LargeActionByActorKey, currentTurn.GetOrThrow<DurableDict<string>>(LargeActionByActorKey)!);
        archivedTurn.Upsert(BarrierStateKey, currentTurn.GetOrThrow<string>(BarrierStateKey)!);
        archivedTurn.Upsert(TurnOwnerActorIdKey, currentTurn.GetOrThrow<string>(TurnOwnerActorIdKey)!);
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
                GetPlayerLocationId(root),
                GetNotebookContent(notebookSnapshot)
            )
        );
    }

    private static void RecordLargeActionForActor(DurableDict<string> root, string actorId, TurnStep step) {
        var currentTurn = EnsureCurrentTurnPhase4Fields(root);
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

        currentTurn.Upsert(
            BarrierStateKey,
            HaveAllActiveActorsSubmittedLargeAction(root, largeActionByActor)
                ? ReadyForGmBarrierState
                : CollectingLlmBarrierState
        );
    }

    private static bool HasSubmittedLargeAction(DurableDict<string> root, string actorId) {
        var currentTurn = EnsureCurrentTurnPhase4Fields(root);
        var largeActionByActor = currentTurn.GetOrThrow<DurableDict<string>>(LargeActionByActorKey)!;
        return largeActionByActor.TryGet(actorId, out DurableDict<string>? action) && action is not null;
    }

    private static IReadOnlyList<LargeActionIntent> ReadLargeActionIntents(DurableDict<string> root) {
        var currentTurn = EnsureCurrentTurnPhase4Fields(root);
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

    private static IEnumerable<string> EnumerateActiveActorIds(DurableDict<string> root) {
        var game = GetGame(root);
        var activeActorIds = game.GetOrThrow<DurableDict<string>>(ActiveActorIdsKey)!;
        foreach (var actorId in activeActorIds.Keys.OrderBy(static key => key, StringComparer.Ordinal)) {
            var actor = GetActor(root, actorId);
            if (actor.TryGet(ActiveKey, out bool active) && !active) { continue; }
            yield return actorId;
        }
    }

    private static DurableDict<string>? GetAcceptedStepsForActor(
        DurableDict<string> root,
        string actorId,
        bool createIfMissing
    ) {
        actorId = NormalizeRequired(actorId, nameof(actorId));
        var currentTurn = EnsureCurrentTurnPhase4Fields(root);
        var acceptedStepsByActor = currentTurn.GetOrThrow<DurableDict<string>>(AcceptedStepsByActorKey)!;
        if (acceptedStepsByActor.TryGet(actorId, out DurableDict<string>? acceptedSteps)
            && acceptedSteps is not null) {
            return acceptedSteps;
        }

        if (!createIfMissing) { return null; }

        acceptedSteps = root.Revision.CreateDict<string>();
        acceptedStepsByActor.Upsert(actorId, acceptedSteps);

        if (string.Equals(actorId, TerminalPlayerActorId, StringComparison.Ordinal)) {
            currentTurn.Upsert(AcceptedStepsKey, acceptedSteps);
        }

        return acceptedSteps;
    }

    private static DurableDict<string> EnsureCurrentTurnPhase4Fields(DurableDict<string> root) {
        var currentTurn = GetCurrentTurn(root);

        if (!currentTurn.TryGet(AcceptedStepsByActorKey, out DurableDict<string>? acceptedStepsByActor)
            || acceptedStepsByActor is null) {
            acceptedStepsByActor = root.Revision.CreateDict<string>();
            currentTurn.Upsert(AcceptedStepsByActorKey, acceptedStepsByActor);
        }

        if (!acceptedStepsByActor.TryGet(TerminalPlayerActorId, out DurableDict<string>? terminalSteps)
            || terminalSteps is null) {
            terminalSteps = currentTurn.TryGet(AcceptedStepsKey, out DurableDict<string>? legacySteps)
                && legacySteps is not null
                    ? legacySteps
                    : root.Revision.CreateDict<string>();
            acceptedStepsByActor.Upsert(TerminalPlayerActorId, terminalSteps);
            currentTurn.Upsert(AcceptedStepsKey, terminalSteps);
        }

        if (!currentTurn.TryGet(LargeActionByActorKey, out DurableDict<string>? largeActionByActor)
            || largeActionByActor is null) {
            currentTurn.Upsert(LargeActionByActorKey, root.Revision.CreateDict<string>());
        }

        if (!currentTurn.TryGet(TurnOwnerActorIdKey, out string? turnOwnerActorId)
            || string.IsNullOrWhiteSpace(turnOwnerActorId)) {
            currentTurn.Upsert(TurnOwnerActorIdKey, TerminalPlayerActorId);
        }

        if (!currentTurn.TryGet(BarrierStateKey, out string? barrierState)
            || string.IsNullOrWhiteSpace(barrierState)) {
            currentTurn.Upsert(BarrierStateKey, CollectingTerminalBarrierState);
        }

        return currentTurn;
    }

    private static string? TryGetOptionalString(DurableDict<string> dict, string key) {
        if (!dict.TryGet(key, out string? value) || string.IsNullOrWhiteSpace(value)) { return null; }

        return value;
    }

    private static DurableDict<string> CreateLocation(Revision rev, string name, string description) {
        var location = rev.CreateDict<string>();
        location.Upsert(NameKey, name);
        location.Upsert(DescriptionKey, description);
        location.Upsert(ExitsKey, rev.CreateDict<string>());
        return location;
    }

    private static DurableDict<string> CreateActor(
        Revision rev,
        string kind,
        string name,
        string profileNote,
        string locationId,
        bool active
    ) {
        var actor = rev.CreateDict<string>();
        actor.Upsert(KindKey, kind);
        actor.Upsert(NameKey, name);
        actor.Upsert(ProfileNoteKey, profileNote);
        actor.Upsert(LocationIdKey, locationId);
        actor.Upsert(VisibilityKey, VisibleValue);
        actor.Upsert(ActiveKey, active);
        return actor;
    }

    private static void UpsertActorLocation(DurableDict<string> root, string actorId, string locationId) {
        var world = root.GetOrThrow<DurableDict<string>>(WorldKey)!;
        if (!world.TryGet(ActorsKey, out DurableDict<string>? actors) || actors is null) { return; }
        if (!actors.TryGet(actorId, out DurableDict<string>? actor) || actor is null) { return; }

        actor.Upsert(LocationIdKey, locationId);
    }

    private static void AddExit(DurableDict<string> from, string direction, string targetLocationId) {
        var exits = from.GetOrThrow<DurableDict<string>>(ExitsKey)!;
        exits.Upsert(direction, targetLocationId);
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

    private static bool LocationExists(DurableDict<string> root, string locationId) {
        var world = root.GetOrThrow<DurableDict<string>>(WorldKey)!;
        var locations = world.GetOrThrow<DurableDict<string>>(LocationsKey)!;
        return locations.TryGet(locationId, out DurableDict<string>? _);
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

    private static string NormalizeRequired(string value, string parameterName) {
        if (string.IsNullOrWhiteSpace(value)) { throw new ArgumentException("Value cannot be null or whitespace.", parameterName); }
        return value.Trim();
    }
}
