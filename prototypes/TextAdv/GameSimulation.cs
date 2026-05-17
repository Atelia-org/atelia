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
    private const string StartDayKey = "startDay";
    private const string StartSlotKey = "startSlot";
    private const string StartLocationIdKey = "startLocationId";
    private const string NotebookSnapshotKey = "notebookSnapshot";
    private const string NextStepNumberKey = "nextStepNumber";
    private const string AcceptedStepsKey = "acceptedSteps";
    private const string TurnNumberKey = "turnNumber";
    private const string ResolutionSummaryKey = "resolutionSummary";
    private const string EndingNotebookKey = "endingNotebook";
    private const string ActionKindKey = "actionKind";
    private const string ActionSummaryKey = "actionSummary";
    private const string ActionPayloadKey = "actionPayload";
    private const string PreActionReasonKey = "preActionReason";
    private const string ValidatorFeedbackKey = "validatorFeedback";
    private const string EndsTurnKey = "endsTurn";
    private const string NameKey = "name";
    private const string KindKey = "kind";
    private const string DescriptionKey = "description";
    private const string ProfileNoteKey = "profileNote";
    private const string ActiveKey = "active";
    private const string ExitsKey = "exits";
    private const string LocationIdKey = "locationId";
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

        actors.Upsert(
            TerminalPlayerActorId,
            CreateActor(
                rev,
                kind: "terminal-player",
                name: "你",
                profileNote: "通过终端命令操作的玩家角色。",
                locationId: beachId,
                active: true
            )
        );
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
        game.Upsert(CurrentTurnKey, CreateCurrentTurnState(rev, day: 1, slot: 1, beachId, string.Empty));

        root.Upsert(WorldKey, world);
        root.Upsert(GameKey, game);
        root.Upsert(PlayerKey, player);

        _ = repo.Commit(root).Value;
        return root;
    }

    internal static PerceptionBundle DescribeCurrentPerception(DurableDict<string> root) {
        var game = GetGame(root);
        var day = game.GetOrThrow<int>(DayKey);
        var slot = game.GetOrThrow<int>(SlotKey);
        var slotsPerDay = game.GetOrThrow<int>(SlotsPerDayKey);
        var lastResolution = TryGetOptionalString(game, LastResolutionKey);
        var notebookSnapshot = GetNotebookSnapshot(root);
        var acceptedSteps = ReadAcceptedSteps(root);

        return new PerceptionBundle(
            day,
            slot,
            slotsPerDay,
            DescribeCurrentLocation(root),
            notebookSnapshot,
            acceptedSteps,
            lastResolution
        );
    }

    internal static LocationPerception DescribeCurrentLocation(DurableDict<string> root)
        => DescribeLocation(root, GetPlayerLocationId(root));

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

            ArchiveCompletedTurn(root, llmResolutionSummary);
            game.Upsert(LastResolutionKey, llmResolutionSummary);
            ResetCurrentTurn(root);

            return AsyncAteliaResult<TurnResolution>.Success(new TurnResolution(llmResolutionSummary, DescribeCurrentPerception(root)));
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

        ArchiveCompletedTurn(root, resolutionSummary);
        game.Upsert(LastResolutionKey, resolutionSummary);
        ResetCurrentTurn(root);

        return AsyncAteliaResult<TurnResolution>.Success(new TurnResolution(resolutionSummary, DescribeCurrentPerception(root)));
    }

    internal static async Task<AsyncAteliaResult<TurnResolution>> ApplyInteractionAsync(
        DurableDict<string> root,
        string interactionId,
        string preActionReason,
        string validatorFeedback,
        CancellationToken cancellationToken
    ) {
        var startingPerception = DescribeCurrentPerception(root);
        var interactionResult = TryGetVisibleInteraction(startingPerception, interactionId);
        if (!interactionResult.TryGetValue(out var interaction) || interaction is null) {
            return AsyncAteliaResult<TurnResolution>.Failure(interactionResult.Error!);
        }

        var actionSummary = $"{interaction.VisibleLabel} ({interaction.ActionKind})";
        AppendAcceptedStep(
            root,
            actionKind: "large/interact",
            actionSummary,
            actionPayload: BuildInteractionPayload(interaction),
            preActionReason,
            validatorFeedback,
            endsTurn: true
        );

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

        ArchiveCompletedTurn(root, resolutionSummary);
        game.Upsert(LastResolutionKey, resolutionSummary);
        ResetCurrentTurn(root);

        return AsyncAteliaResult<TurnResolution>.Success(new TurnResolution(resolutionSummary, DescribeCurrentPerception(root)));
    }

    internal static PerceptionBundle ApplyNotebookEdit(
        DurableDict<string> root,
        NotebookEditProposal proposal,
        string preActionReason,
        string validatorFeedback
    ) {
        var notebook = GetNotebook(root);
        GameNotebookEditService.ApplyOrThrow(notebook, proposal);

        AppendAcceptedStep(
            root,
            actionKind: "small/edit-memory-notebook",
            actionSummary: proposal.ActionSummary,
            actionPayload: proposal.CanonicalScriptXml,
            preActionReason,
            validatorFeedback,
            endsTurn: false
        );

        return DescribeCurrentPerception(root);
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

        var game = GetGame(root);
        var previousDay = game.GetOrThrow<int>(DayKey);
        var previousSlot = game.GetOrThrow<int>(SlotKey);
        var slotsPerDay = game.GetOrThrow<int>(SlotsPerDayKey);
        var nextClock = AdvanceClock(root);
        var resolutionSummary =
            $"你原地休息了一会。当前原型只推进时钟，不结算更复杂的世界后果。"
            + $" 时间从 {GameClock.FormatClock(previousDay, previousSlot, slotsPerDay)} 前进到 {GameClock.FormatClock(nextClock.Day, nextClock.Slot, slotsPerDay)}。";

        ArchiveCompletedTurn(root, resolutionSummary);
        game.Upsert(LastResolutionKey, resolutionSummary);
        ResetCurrentTurn(root);

        return new TurnResolution(resolutionSummary, DescribeCurrentPerception(root));
    }

    private static string GetPlayerLocationId(DurableDict<string> root) {
        var player = root.GetOrThrow<DurableDict<string>>(PlayerKey)!;
        return player.GetOrThrow<string>(PlayerLocationKey)!;
    }

    private static LocationPerception DescribeLocation(DurableDict<string> root, string locationId) {
        var location = GetLocation(root, locationId);
        var name = location.GetOrThrow<string>(NameKey)!;
        var description = location.GetOrThrow<string>(DescriptionKey)!;
        var exits = EnumerateExits(root, locationId).ToArray();
        var items = EnumerateVisibleItemsAtLocation(root, locationId).ToArray();
        var actors = EnumerateVisibleActorsAtLocation(root, locationId).ToArray();
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

    private static IEnumerable<ActorPerception> EnumerateVisibleActorsAtLocation(
        DurableDict<string> root,
        string locationId
    ) {
        var world = root.GetOrThrow<DurableDict<string>>(WorldKey)!;
        if (!world.TryGet(ActorsKey, out DurableDict<string>? actors) || actors is null) { yield break; }

        foreach (var actorId in actors.Keys.OrderBy(static key => key, StringComparer.Ordinal)) {
            if (string.Equals(actorId, TerminalPlayerActorId, StringComparison.Ordinal)) { continue; }

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

    private static DurableDict<string> GetGame(DurableDict<string> root)
        => root.GetOrThrow<DurableDict<string>>(GameKey)!;

    private static DurableDict<string> GetPlayer(DurableDict<string> root)
        => root.GetOrThrow<DurableDict<string>>(PlayerKey)!;

    private static DurableText GetNotebook(DurableDict<string> root)
        => GetPlayer(root).GetOrThrow<DurableText>(MemoryNotebookKey)!;

    private static DurableDict<string> GetCurrentTurn(DurableDict<string> root) {
        var game = GetGame(root);
        return game.GetOrThrow<DurableDict<string>>(CurrentTurnKey)!;
    }

    private static TextBlockSnapshotDocument GetNotebookSnapshot(DurableDict<string> root) {
        var notebook = GetNotebook(root);
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

        currentTurn.Upsert(StartDayKey, day);
        currentTurn.Upsert(StartSlotKey, slot);
        currentTurn.Upsert(StartLocationIdKey, locationId);
        currentTurn.Upsert(NotebookSnapshotKey, notebookSnapshot);
        currentTurn.Upsert(NextStepNumberKey, 1);
        currentTurn.Upsert(AcceptedStepsKey, acceptedSteps);

        return currentTurn;
    }

    private static IReadOnlyList<TurnStep> ReadAcceptedSteps(DurableDict<string> root) {
        var currentTurn = GetCurrentTurn(root);
        var acceptedSteps = currentTurn.GetOrThrow<DurableDict<string>>(AcceptedStepsKey)!;
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
        var currentTurn = GetCurrentTurn(root);
        var acceptedSteps = currentTurn.GetOrThrow<DurableDict<string>>(AcceptedStepsKey)!;
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

        return new TurnStep(stepNumber, actionKind, actionSummary, actionPayload, preActionReason, validatorFeedback, endsTurn);
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
        archivedTurn.Upsert(AcceptedStepsKey, currentTurn.GetOrThrow<DurableDict<string>>(AcceptedStepsKey)!);
        archivedTurn.Upsert(ResolutionSummaryKey, resolutionSummary);
        archivedTurn.Upsert(EndingNotebookKey, GetNotebookContent(GetNotebookSnapshot(root)));

        turnHistory.Upsert($"turn-{turnNumber:D4}", archivedTurn);
        game.Upsert(CompletedTurnCountKey, turnNumber);
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
