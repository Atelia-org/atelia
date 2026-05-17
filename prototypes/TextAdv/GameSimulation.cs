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
    private const string TerminalPlayerActorId = "player";
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

        _ = repo.Commit(root).Value;
        return root;
    }

    private static string? ReadLastResolutionForActor(DurableDict<string> game, string actorId) {
        if (game.TryGet(LastResolutionByActorKey, out DurableDict<string>? lastResolutionByActor)
            && lastResolutionByActor is not null
            && lastResolutionByActor.TryGet(actorId, out string? actorResolution)
            && !string.IsNullOrWhiteSpace(actorResolution)) {
            return actorResolution;
        }

        return null;
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

    private static DurableText GetNotebook(DurableDict<string> root)
        => GetNotebook(root, TerminalPlayerActorId);

    private static DurableText GetNotebook(DurableDict<string> root, string actorId) {
        var actor = GetActor(root, actorId);
        if (actor.TryGet(MemoryNotebookKey, out DurableText? notebook) && notebook is not null) {
            return notebook;
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

        currentTurn.Upsert(StartDayKey, day);
        currentTurn.Upsert(StartSlotKey, slot);
        currentTurn.Upsert(StartLocationIdKey, locationId);
        currentTurn.Upsert(NotebookSnapshotKey, notebookSnapshot);
        currentTurn.Upsert(NextStepNumberKey, 1);
        currentTurn.Upsert(AcceptedStepsByActorKey, acceptedStepsByActor);
        currentTurn.Upsert(LargeActionByActorKey, largeActionByActor);
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

    private static DurableDict<string>? GetAcceptedStepsForActor(
        DurableDict<string> root,
        string actorId,
        bool createIfMissing
    ) {
        actorId = NormalizeRequired(actorId, nameof(actorId));
        var currentTurn = GetCurrentTurn(root);
        var acceptedStepsByActor = currentTurn.GetOrThrow<DurableDict<string>>(AcceptedStepsByActorKey)!;
        if (acceptedStepsByActor.TryGet(actorId, out DurableDict<string>? acceptedSteps)
            && acceptedSteps is not null) {
            return acceptedSteps;
        }

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

    private static bool LocationExists(DurableDict<string> root, string locationId) {
        var world = root.GetOrThrow<DurableDict<string>>(WorldKey)!;
        var locations = world.GetOrThrow<DurableDict<string>>(LocationsKey)!;
        return locations.TryGet(locationId, out DurableDict<string>? _);
    }

    private static string NormalizeRequired(string value, string parameterName) {
        if (string.IsNullOrWhiteSpace(value)) { throw new ArgumentException("Value cannot be null or whitespace.", parameterName); }
        return value.Trim();
    }
}
