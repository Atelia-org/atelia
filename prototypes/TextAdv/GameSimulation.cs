using Atelia.StateJournal;
using Atelia.TextEditScript;

namespace Atelia.TextAdv;

internal static partial class GameSimulation {
    private const string WorldKey = "world";
    private const string GameKey = "game";
    private const string LocationsKey = "locations";
    private const string ItemsKey = "items";
    private const string ActorsKey = "actors";
    private const string InteractionsKey = "interactions";
    private const string InitialLocationKey = "initialLocation";
    private const string ActiveActorIdsKey = "activeActorIds";
    private const string MemoryNotebookKey = "memoryNotebook";
    internal const string TerminalPlayerActorId = "player";
    private const string DayKey = "day";
    private const string SlotKey = "slot";
    private const string SlotsPerDayKey = "slotsPerDay";
    private const string CurrentTurnKey = "currentTurn";
    private const string TurnHistoryKey = "turnHistory";
    private const string CompletedTurnCountKey = "completedTurnCount";
    private const string LastResolutionByActorKey = "lastResolutionByActor";
    private const string StartDayKey = "startDay";
    private const string StartSlotKey = "startSlot";
    private const string StartLocationIdKey = "startLocationId";
    private const string NotebookSnapshotKey = "notebookSnapshot";
    private const string NextStepNumberKey = "nextStepNumber";
    private const string AcceptedStepsByActorKey = "acceptedStepsByActor";
    private const string LargeActionByActorKey = "largeActionByActor";
    private const string PendingTurnEndEffectsByActorKey = "pendingTurnEndEffectsByActor";
    private const string TurnNumberKey = "turnNumber";
    private const string ResolutionSummaryKey = "resolutionSummary";
    private const string EndingNotebookKey = "endingNotebook";
    private const string EndDayKey = "endDay";
    private const string EndSlotKey = "endSlot";
    private const string ActorTurnContextByActorKey = "actorTurnContextByActor";
    private const string AwaitingActionsBarrierState = "awaiting-actions";
    private const string CollectingActionsBarrierState = "collecting-actions";
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
    private const string ControllerKindKey = "controllerKind";
    private const string DescriptionKey = "description";
    private const string ProfileNoteKey = "profileNote";
    private const string ActiveKey = "active";
    private const string ExitsKey = "exits";
    private const string LocationIdKey = "locationId";
    private const string LocationNameKey = "locationName";
    private const string OwnerActorIdKey = "ownerActorId";
    private const string VisibilityKey = "visibility";
    private const string TargetKindKey = "targetKind";
    private const string TargetIdKey = "targetId";
    private const string ActionKindLedgerKey = "actionKind";
    private const string VisibleLabelKey = "visibleLabel";
    private const string PreconditionNoteKey = "preconditionNote";
    private const string EffectNoteKey = "effectNote";
    private const string TurnCostLedgerKey = "turnCost";
    private const string EffectScopeKey = "effectScope";
    private const string EffectSlotsKey = "effectSlots";
    private const string StepOutcomeSummaryKey = "stepOutcomeSummary";
    private const string StepOutcomeStateKey = "stepOutcomeState";
    private const string WorkingByActorKey = "workingByActor";
    private const string ActorIdKey = "actorId";
    private const string EffectSlotKey = "effectSlot";
    private const string RemainingTurnsKey = "remainingTurns";
    private const string VisibleValue = "visible";
    private const string DiscoveredValue = "discovered";
    private const int DefaultSlotsPerDay = 4;

    internal const string ImmediateEffectSlot = "immediate";
    internal const string TurnEndEffectSlot = "turn-end";
    internal const string PerTurnEndEffectSlot = "per-turn-end";
    internal const string OnCompletionEffectSlot = "on-completion";
    internal const string SelfEffectScope = "self";
    internal const string RoomEffectScope = "room";
    internal const string AdjacentRoomEffectScope = "adjacent-room";
    internal const string SceneEffectScope = "scene";
    internal const string StepOutcomeCommittedNow = "committed-now";
    internal const string StepOutcomePendingTurnEnd = "pending-turn-end";
    internal const string StepOutcomeWorking = "working";
    internal const string StepOutcomeCompleted = "completed";
    internal const string PlayerActorKind = "player";
    internal const string NpcActorKind = "npc";
    internal const string ExternalTerminalControllerKind = "external-terminal";
    internal const string InternalLlmControllerKind = "internal-llm";

    private sealed record PendingTurnEndEffect(
        string EffectId,
        string ActorId,
        string ActionKind,
        string ActionSummary,
        string? ActionPayload,
        string PreActionReason,
        string ValidatorFeedback,
        string EffectSlot,
        int SourceStepNumber
    );

    private sealed record WorkOrder(
        string ActorId,
        string ActionKind,
        string ActionSummary,
        string? ActionPayload,
        string PreActionReason,
        string ValidatorFeedback,
        int RemainingTurns
    );

    private sealed record TurnResolutionPrelude(
        EffectBatchResolution PendingTurnEndEffects,
        EffectBatchResolution BackgroundWorkingEffects,
        int PreviousDay,
        int PreviousSlot,
        int SlotsPerDay
    );

    private sealed record ActorEffectSummary(
        string ActorId,
        string Summary
    );

    private sealed record EffectResolution(
        string ActorFacingSummary,
        string? TerminalVisibleSummary
    );

    private sealed record EffectBatchResolution(
        string TerminalVisibleSummary,
        IReadOnlyList<ActorEffectSummary> ActorFacingSummaries
    );

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
        var notebook = CreateNotebookText(rev, string.Empty);
        var turnHistory = rev.CreateDict<string>();
        var lastResolutionByActor = rev.CreateDict<string>();
        var workingByActor = rev.CreateDict<string>();

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
            kind: PlayerActorKind,
            name: "你",
            profileNote: "通过终端命令操作的玩家角色。",
            locationId: beachId,
            active: true,
            controllerKind: ExternalTerminalControllerKind
        );
        terminalActor.Upsert(MemoryNotebookKey, notebook);
        actors.Upsert(TerminalPlayerActorId, terminalActor);
        activeActorIds.Upsert(TerminalPlayerActorId, TerminalPlayerActorId);

        world.Upsert(LocationsKey, locations);
        world.Upsert(ItemsKey, items);
        world.Upsert(ActorsKey, actors);
        world.Upsert(InteractionsKey, interactions);
        world.Upsert(InitialLocationKey, beachId);

        game.Upsert(DayKey, 1);
        game.Upsert(SlotKey, 1);
        game.Upsert(SlotsPerDayKey, DefaultSlotsPerDay);
        game.Upsert(ActiveActorIdsKey, activeActorIds);
        game.Upsert(CompletedTurnCountKey, 0);
        game.Upsert(TurnHistoryKey, turnHistory);
        game.Upsert(LastResolutionByActorKey, lastResolutionByActor);
        game.Upsert(WorkingByActorKey, workingByActor);
        game.Upsert(CurrentTurnKey, CreateCurrentTurnState(rev, day: 1, slot: 1, beachId, string.Empty));

        root.Upsert(WorldKey, world);
        root.Upsert(GameKey, game);

        _ = repo.Commit(root).Value;
        return root;
    }

    private static string? ReadLastResolutionForActor(DurableDict<string> game, string actorId) {
        if (game.TryGet(LastResolutionByActorKey, out DurableDict<string>? lastResolutionByActor)
            && lastResolutionByActor is not null
            && lastResolutionByActor.TryGet(actorId, out string? actorResolution)
            && !string.IsNullOrWhiteSpace(actorResolution)) { return actorResolution; }

        return null;
    }

    private static void ClearLastResolutionByActor(DurableDict<string> root) {
        var game = GetGame(root);
        game.Upsert(LastResolutionByActorKey, root.Revision.CreateDict<string>());
    }

    private static DurableDict<string> GetOrCreateLastResolutionByActor(DurableDict<string> root) {
        var game = GetGame(root);
        if (game.TryGet(LastResolutionByActorKey, out DurableDict<string>? lastResolutionByActor)
            && lastResolutionByActor is not null) { return lastResolutionByActor; }

        lastResolutionByActor = root.Revision.CreateDict<string>();
        game.Upsert(LastResolutionByActorKey, lastResolutionByActor);
        return lastResolutionByActor;
    }

    private static void SetLastResolutionForActor(DurableDict<string> root, string actorId, string summary) {
        actorId = NormalizeRequired(actorId, nameof(actorId));
        summary = NormalizeRequired(summary, nameof(summary));
        var lastResolutionByActor = GetOrCreateLastResolutionByActor(root);
        lastResolutionByActor.Upsert(actorId, summary);
    }

    private static DurableDict<string> GetGame(DurableDict<string> root)
        => root.GetOrThrow<DurableDict<string>>(GameKey)!;

    private static DurableText GetNotebook(DurableDict<string> root)
        => GetNotebook(root, TerminalPlayerActorId);

    private static DurableText GetNotebook(DurableDict<string> root, string actorId) {
        var actor = GetActor(root, actorId);
        if (actor.TryGet(MemoryNotebookKey, out DurableText? notebook) && notebook is not null) { return notebook; }

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

    private static string GetActorName(DurableDict<string> root, string actorId) {
        var actor = GetActor(root, actorId);
        return actor.TryGet(NameKey, out string? rawName) && !string.IsNullOrWhiteSpace(rawName)
            ? rawName
            : actorId;
    }

    private static string GetActorKind(DurableDict<string> actor) {
        return actor.TryGet(KindKey, out string? rawKind) && !string.IsNullOrWhiteSpace(rawKind)
            ? rawKind
            : NpcActorKind;
    }

    private static string GetActorKind(DurableDict<string> root, string actorId)
        => GetActorKind(GetActor(root, actorId));

    private static string? GetActorControllerKind(DurableDict<string> actor) {
        if (!actor.TryGet(ControllerKindKey, out string? rawControllerKind)
            || string.IsNullOrWhiteSpace(rawControllerKind)) { return null; }

        return rawControllerKind;
    }

    private static bool IsInternallyDrivenPlayerActor(DurableDict<string> root, string actorId) {
        var actor = GetActor(root, actorId);
        return string.Equals(GetActorKind(actor), PlayerActorKind, StringComparison.Ordinal)
            && string.Equals(GetActorControllerKind(actor), InternalLlmControllerKind, StringComparison.Ordinal);
    }

    private static DurableDict<string> GetCurrentTurn(DurableDict<string> root) {
        var game = GetGame(root);
        return game.GetOrThrow<DurableDict<string>>(CurrentTurnKey)!;
    }

    private static DurableDict<string> GetOrCreateWorkingByActor(DurableDict<string> root) {
        var game = GetGame(root);
        if (game.TryGet(WorkingByActorKey, out DurableDict<string>? workingByActor)
            && workingByActor is not null) { return workingByActor; }

        workingByActor = root.Revision.CreateDict<string>();
        game.Upsert(WorkingByActorKey, workingByActor);
        return workingByActor;
    }

    private static DurableDict<string> GetLocation(DurableDict<string> root, string locationId) {
        var world = root.GetOrThrow<DurableDict<string>>(WorldKey)!;
        var locations = world.GetOrThrow<DurableDict<string>>(LocationsKey)!;
        return locations.GetOrThrow<DurableDict<string>>(locationId)!;
    }

    private static string GetActorLocationId(DurableDict<string> root, string actorId)
        => GetActor(root, actorId).GetOrThrow<string>(LocationIdKey)!;

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
        var acceptedStepsByActor = rev.CreateDict<string>();
        var largeActionByActor = rev.CreateDict<string>();
        var pendingTurnEndEffectsByActor = rev.CreateDict<string>();

        currentTurn.Upsert(StartDayKey, day);
        currentTurn.Upsert(StartSlotKey, slot);
        currentTurn.Upsert(StartLocationIdKey, locationId);
        currentTurn.Upsert(NotebookSnapshotKey, notebookSnapshot);
        currentTurn.Upsert(NextStepNumberKey, 1);
        currentTurn.Upsert(AcceptedStepsByActorKey, acceptedStepsByActor);
        currentTurn.Upsert(LargeActionByActorKey, largeActionByActor);
        currentTurn.Upsert(PendingTurnEndEffectsByActorKey, pendingTurnEndEffectsByActor);
        acceptedStepsByActor.Upsert(TerminalPlayerActorId, rev.CreateDict<string>());

        return currentTurn;
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

    private static IEnumerable<string> EnumerateTrackedActorIds(DurableDict<string> root) {
        var game = GetGame(root);
        var activeActorIds = game.GetOrThrow<DurableDict<string>>(ActiveActorIdsKey)!;
        foreach (var actorId in activeActorIds.Keys.OrderBy(static key => key, StringComparer.Ordinal)) {
            yield return actorId;
        }
    }

    private static bool HasAnyActiveActor(DurableDict<string> root)
        => EnumerateActiveActorIds(root).Any();

    private static void SetActorActiveState(DurableDict<string> root, string actorId, bool active) {
        actorId = NormalizeRequired(actorId, nameof(actorId));
        var actor = GetActor(root, actorId);
        actor.Upsert(ActiveKey, active);
    }

    private static DurableDict<string>? GetAcceptedStepsForActor(
        DurableDict<string> root,
        string actorId,
        bool createIfMissing
    ) {
        actorId = NormalizeRequired(actorId, nameof(actorId));
        var currentTurn = GetCurrentTurn(root);
        var acceptedStepsByActor = currentTurn.GetOrThrow<DurableDict<string>>(AcceptedStepsByActorKey)!;
        if (acceptedStepsByActor.TryGet(actorId, out DurableDict<string>? acceptedSteps)
            && acceptedSteps is not null) { return acceptedSteps; }

        if (!createIfMissing) { return null; }

        acceptedSteps = root.Revision.CreateDict<string>();
        acceptedStepsByActor.Upsert(actorId, acceptedSteps);
        return acceptedSteps;
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
        bool active,
        string? controllerKind = null
    ) {
        var actor = rev.CreateDict<string>();
        actor.Upsert(KindKey, kind);
        if (!string.IsNullOrWhiteSpace(controllerKind)) {
            actor.Upsert(ControllerKindKey, controllerKind!.Trim());
        }

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

    private static bool LocationExists(DurableDict<string> root, string locationId) {
        var world = root.GetOrThrow<DurableDict<string>>(WorldKey)!;
        var locations = world.GetOrThrow<DurableDict<string>>(LocationsKey)!;
        return locations.TryGet(locationId, out DurableDict<string>? _);
    }

    private static string NormalizeRequired(string value, string parameterName) {
        if (string.IsNullOrWhiteSpace(value)) { throw new ArgumentException("Value cannot be null or whitespace.", parameterName); }
        return value.Trim();
    }

    internal static bool SupportsImmediateSelfInteraction(InteractionPerception interaction) {
        ArgumentNullException.ThrowIfNull(interaction);
        return interaction.TurnCost == 0
            && string.Equals(interaction.EffectScope, SelfEffectScope, StringComparison.Ordinal)
            && interaction.EffectSlots.Count > 0
            && interaction.EffectSlots.All(static slot => string.Equals(slot, ImmediateEffectSlot, StringComparison.Ordinal));
    }

    internal static bool SupportsDeferredTurnEndInteraction(InteractionPerception interaction) {
        ArgumentNullException.ThrowIfNull(interaction);
        return interaction.TurnCost == 0
            && interaction.EffectSlots.Count > 0
            && interaction.EffectSlots.All(static slot => string.Equals(slot, TurnEndEffectSlot, StringComparison.Ordinal));
    }

    internal static bool SupportsWorkingInteraction(InteractionPerception interaction) {
        ArgumentNullException.ThrowIfNull(interaction);
        return interaction.TurnCost > 1;
    }

    internal static bool SupportsTurnEndingInteraction(InteractionPerception interaction) {
        ArgumentNullException.ThrowIfNull(interaction);
        return interaction.TurnCost == 1;
    }

    internal static string DescribeInteractionTurnCostForPlayer(InteractionPerception interaction) {
        ArgumentNullException.ThrowIfNull(interaction);

        if (interaction.TurnCost <= 0) { return "顺手可做"; }
        if (interaction.TurnCost == 1) { return "会占用这一回合"; }
        return $"会持续忙碌 {interaction.TurnCost} 个回合";
    }

    internal static AteliaResult<TerminalActionExecutionPlan> BuildExploreTerminalPlan(
        string direction,
        string? focus,
        string preActionReason
    ) {
        try {
            direction = NormalizeRequired(direction, nameof(direction));
            preActionReason = NormalizeRequired(preActionReason, nameof(preActionReason));
            focus = string.IsNullOrWhiteSpace(focus) ? null : focus.Trim();
            return new TerminalActionExecutionPlan.Explore(direction, focus, preActionReason);
        }
        catch (ArgumentException ex) {
            return BuildInvalidTerminalPlanInputError(ex, "explore");
        }
    }

    internal static AteliaResult<TerminalActionExecutionPlan> BuildRestAWhileTerminalPlan(string preActionReason) {
        try {
            preActionReason = NormalizeRequired(preActionReason, nameof(preActionReason));
            return new TerminalActionExecutionPlan.RestAWhile(preActionReason);
        }
        catch (ArgumentException ex) {
            return BuildInvalidTerminalPlanInputError(ex, "rest-a-while");
        }
    }

    internal static AteliaResult<TerminalActionExecutionPlan> BuildTerminalInteractionPlan(
        PerceptionBundle perception,
        string interactionId,
        string preActionReason
    ) {
        ArgumentNullException.ThrowIfNull(perception);
        try {
            interactionId = NormalizeRequired(interactionId, nameof(interactionId));
            preActionReason = NormalizeRequired(preActionReason, nameof(preActionReason));
        }
        catch (ArgumentException ex) {
            return BuildInvalidTerminalPlanInputError(ex, "interact");
        }

        var interactionResult = TryGetVisibleInteraction(perception, interactionId);
        if (!interactionResult.TryGetValue(out var interaction) || interaction is null) { return interactionResult.Error!; }

        var executionKindResult = ClassifyInteractionExecutionKind(interaction);
        if (!executionKindResult.TryGetValue(out var executionKind)) { return executionKindResult.Error!; }

        return CreateInteractionPlan(interaction, executionKind, preActionReason);
    }

    internal static AteliaResult<InteractionExecutionKind> ClassifyInteractionExecutionKind(InteractionPerception interaction) {
        ArgumentNullException.ThrowIfNull(interaction);

        if (SupportsImmediateSelfInteraction(interaction)) { return InteractionExecutionKind.ImmediateSelf; }
        if (SupportsDeferredTurnEndInteraction(interaction)) { return InteractionExecutionKind.DeferredTurnEnd; }
        if (SupportsWorkingInteraction(interaction)) { return InteractionExecutionKind.WorkingStart; }
        if (SupportsTurnEndingInteraction(interaction)) { return InteractionExecutionKind.TurnEnding; }

        if (interaction.TurnCost == 0) {
            return new TextAdvError(
                "TextAdv.UnsupportedInteractionExecutionPlan",
                "这个 interaction 目前属于零回合但非即时私有效果的动作类型。",
                "当前实现还不能安全结算它；请先改用会结束回合的动作，或补完 turn-end / working 流程。"
            );
        }

        return new TextAdvError(
            "TextAdv.UnsupportedInteractionExecutionPlan",
            $"这个 interaction 目前具有不受支持的 turnCost={interaction.TurnCost}。",
            "当前实现只支持 immediate self、turn-end deferred、turn-ending 和 multi-turn working 这几类交互。"
        );
    }

    private static TerminalActionExecutionPlan.Interaction CreateInteractionPlan(
        InteractionPerception interaction,
        InteractionExecutionKind executionKind,
        string preActionReason
    ) {
        ArgumentNullException.ThrowIfNull(interaction);
        preActionReason = NormalizeRequired(preActionReason, nameof(preActionReason));
        return new TerminalActionExecutionPlan.Interaction(
            interaction.InteractionId,
            interaction.VisibleLabel,
            interaction.ActionKind,
            BuildInteractionPayload(interaction),
            executionKind,
            preActionReason
        );
    }

    private static TextAdvError BuildInvalidTerminalPlanInputError(ArgumentException ex, string actionName) {
        ArgumentNullException.ThrowIfNull(ex);
        actionName = NormalizeRequired(actionName, nameof(actionName));
        var parameterName = string.IsNullOrWhiteSpace(ex.ParamName) ? "unknown" : ex.ParamName;
        return new TextAdvError(
            "TextAdv.InvalidTerminalPlanInput",
            $"无法构造 '{actionName}' 的终端动作计划：参数 '{parameterName}' 不能为空。",
            "请提供非空白的 reason、direction 或 interaction-id。"
        );
    }
}
