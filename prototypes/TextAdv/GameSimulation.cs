using Atelia.StateJournal;
using Atelia.TextEditScript;

namespace Atelia.TextAdv;

internal static class GameSimulation {
    private const string WorldKey = "world";
    private const string GameKey = "game";
    private const string LocationsKey = "locations";
    private const string InitialLocationKey = "initialLocation";
    private const string PlayerKey = "player";
    private const string PlayerLocationKey = "location";
    private const string MemoryNotebookKey = "memoryNotebook";
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
    private const string DescriptionKey = "description";
    private const string ExitsKey = "exits";
    private const int DefaultSlotsPerDay = 4;

    internal static DurableDict<string> CreateNewWorld(Repository repo) {
        var revResult = repo.GetOrCreateBranch("main");
        var rev = revResult.Value!;

        var root = rev.CreateDict<string>();
        var world = rev.CreateDict<string>();
        var game = rev.CreateDict<string>();
        var locations = rev.CreateDict<string>();
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

        world.Upsert(LocationsKey, locations);
        world.Upsert(InitialLocationKey, beachId);

        player.Upsert(PlayerLocationKey, beachId);
        player.Upsert(MemoryNotebookKey, notebook);

        game.Upsert(DayKey, 1);
        game.Upsert(SlotKey, 1);
        game.Upsert(SlotsPerDayKey, DefaultSlotsPerDay);
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
        return DescribeCurrentPerception(root);
    }

    internal static async Task<TurnResolutionApplyResult> ApplyExploreAsync(
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

            return TurnResolutionApplyResult.Success(new TurnResolution(llmResolutionSummary, DescribeCurrentPerception(root)));
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
            if (!createResult.IsSuccess) { return TurnResolutionApplyResult.Failure(createResult.Error!); }

            var linkResult = gmTools.LinkLocations(
                currentLocationId,
                direction,
                targetLocationId,
                TryGetReverseDirection(direction)
            );
            if (!linkResult.IsSuccess) { return TurnResolutionApplyResult.Failure(linkResult.Error!); }

            createdNewLocation = true;
        }

        var moveResult = gmTools.MovePlayerTo(targetLocationId);
        if (!moveResult.IsSuccess) { return TurnResolutionApplyResult.Failure(moveResult.Error!); }

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

        return TurnResolutionApplyResult.Success(new TurnResolution(resolutionSummary, DescribeCurrentPerception(root)));
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
        return new LocationPerception(locationId, name, description, exits);
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

    private static void AddExit(DurableDict<string> from, string direction, string targetLocationId) {
        var exits = from.GetOrThrow<DurableDict<string>>(ExitsKey)!;
        exits.Upsert(direction, targetLocationId);
    }

    private static string BuildExplorePayload(string direction, string? focus)
        => focus is null
            ? $"direction={direction}"
            : $"direction={direction}\nfocus={focus}";

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
