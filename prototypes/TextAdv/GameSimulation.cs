using Atelia.StateJournal;

namespace Atelia.TextAdv;

internal static class GameSimulation
{
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
    private const string ReasonTraceKey = "reasonTrace";
    private const string ValidatorFeedbackKey = "validatorFeedback";
    private const string EndsTurnKey = "endsTurn";
    private const string NameKey = "name";
    private const string DescriptionKey = "description";
    private const string ExitsKey = "exits";
    private const int DefaultSlotsPerDay = 4;

    private sealed record GameError(string ErrorCode, string Message)
        : AteliaError(ErrorCode, Message);

    internal sealed record LocationPerception(
        string LocationId,
        string Name,
        string Description,
        IReadOnlyList<LocationExitPerception> Exits);

    internal sealed record LocationExitPerception(
        string Direction,
        string TargetLocationId,
        string TargetName);

    internal sealed record TurnStep(
        int StepNumber,
        string ActionKind,
        string ActionSummary,
        string? ActionPayload,
        string ReasonTrace,
        string ValidatorFeedback,
        bool EndsTurn);

    internal sealed record PerceptionBundle(
        int Day,
        int Slot,
        int SlotsPerDay,
        LocationPerception Location,
        string NotebookContent,
        IReadOnlyList<TurnStep> AcceptedSteps,
        string? LastResolution);

    internal sealed record TurnResolution(
        string Summary,
        PerceptionBundle NextPerception);

    internal static DurableDict<string> CreateNewWorld(Repository repo)
    {
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
            + "细白的沙子在阳光下闪闪发光。远处可以看到茂密的树林。");
        var forest = CreateLocation(rev, "密林",
            "茂密的树林遮天蔽日，空气中弥漫着泥土和树叶的气味。"
            + "树影间隐约能听到鸟鸣声。南边透过树缝可以看到沙滩的亮光。");

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

    internal static PerceptionBundle DescribeCurrentPerception(DurableDict<string> root)
    {
        var game = GetGame(root);
        var day = game.GetOrThrow<int>(DayKey);
        var slot = game.GetOrThrow<int>(SlotKey);
        var slotsPerDay = game.GetOrThrow<int>(SlotsPerDayKey);
        var lastResolution = TryGetOptionalString(game, LastResolutionKey);

        return new PerceptionBundle(
            day,
            slot,
            slotsPerDay,
            DescribeCurrentLocation(root),
            GetNotebookContent(root),
            ReadAcceptedSteps(root),
            lastResolution);
    }

    internal static LocationPerception DescribeCurrentLocation(DurableDict<string> root)
        => DescribeLocation(root, GetPlayerLocationId(root));

    internal static AteliaResult<PerceptionBundle> MovePlayer(DurableDict<string> root, string direction)
    {
        var player = root.GetOrThrow<DurableDict<string>>(PlayerKey)!;
        var currentLocationId = GetPlayerLocationId(root);
        var currentLocation = GetLocation(root, currentLocationId);
        var exits = currentLocation.GetOrThrow<DurableDict<string>>(ExitsKey)!;

        if (!exits.TryGet(direction, out string? targetLocationId)
            || string.IsNullOrWhiteSpace(targetLocationId))
        {
            var available = string.Join(", ",
                EnumerateExits(root, currentLocationId)
                    .Select(exit => $"{exit.Direction} → {exit.TargetName}"));
            var currentName = currentLocation.GetOrThrow<string>(NameKey)!;
            return AteliaResult<PerceptionBundle>.Failure(
                new GameError("TextAdv.InvalidDirection",
                    $"「{currentName}」没有通往「{direction}」的出口。可用的出口: {available}"));
        }

        _ = GetLocation(root, targetLocationId);
        player.Upsert(PlayerLocationKey, targetLocationId);
        return DescribeCurrentPerception(root);
    }

    internal static PerceptionBundle ApplyNotebookEdit(
        DurableDict<string> root,
        string newContent,
        string reasonTrace,
        string validatorFeedback)
    {
        var player = GetPlayer(root);
        var oldContent = GetNotebookContent(root);
        var newNotebook = CreateNotebookText(root.Revision, newContent);
        player.Upsert(MemoryNotebookKey, newNotebook);

        AppendAcceptedStep(
            root,
            actionKind: "small/edit-memory-notebook",
            actionSummary: $"replace notebook ({oldContent.Length} -> {newContent.Length} chars)",
            actionPayload: newContent,
            reasonTrace,
            validatorFeedback,
            endsTurn: false);

        return DescribeCurrentPerception(root);
    }

    internal static TurnResolution ApplyRestAWhile(
        DurableDict<string> root,
        string reasonTrace,
        string validatorFeedback)
    {
        const string actionSummary = "原地休息一会";

        AppendAcceptedStep(
            root,
            actionKind: "large/rest-a-while",
            actionSummary,
            actionPayload: null,
            reasonTrace,
            validatorFeedback,
            endsTurn: true);

        var game = GetGame(root);
        var previousDay = game.GetOrThrow<int>(DayKey);
        var previousSlot = game.GetOrThrow<int>(SlotKey);
        var nextClock = AdvanceClock(root);
        var resolutionSummary =
            $"你原地休息了一会。当前原型只推进时钟，不结算更复杂的世界后果。"
            + $" 时间从 Day {previousDay} Slot {previousSlot} 前进到 Day {nextClock.Day} Slot {nextClock.Slot}。";

        ArchiveCompletedTurn(root, resolutionSummary);
        game.Upsert(LastResolutionKey, resolutionSummary);
        ResetCurrentTurn(root);

        return new TurnResolution(resolutionSummary, DescribeCurrentPerception(root));
    }

    private static string GetPlayerLocationId(DurableDict<string> root)
    {
        var player = root.GetOrThrow<DurableDict<string>>(PlayerKey)!;
        return player.GetOrThrow<string>(PlayerLocationKey)!;
    }

    private static LocationPerception DescribeLocation(DurableDict<string> root, string locationId)
    {
        var location = GetLocation(root, locationId);
        var name = location.GetOrThrow<string>(NameKey)!;
        var description = location.GetOrThrow<string>(DescriptionKey)!;
        var exits = EnumerateExits(root, locationId).ToArray();
        return new LocationPerception(locationId, name, description, exits);
    }

    private static IEnumerable<LocationExitPerception> EnumerateExits(
        DurableDict<string> root,
        string locationId)
    {
        var location = GetLocation(root, locationId);
        var exits = location.GetOrThrow<DurableDict<string>>(ExitsKey)!;

        foreach (var direction in exits.Keys)
        {
            var targetLocationId = exits.GetOrThrow<string>(direction)!;
            var targetLocation = GetLocation(root, targetLocationId);
            var targetName = targetLocation.GetOrThrow<string>(NameKey)!;
            yield return new LocationExitPerception(direction, targetLocationId, targetName);
        }
    }

    private static DurableDict<string> GetLocation(DurableDict<string> root, string locationId)
    {
        var world = root.GetOrThrow<DurableDict<string>>(WorldKey)!;
        var locations = world.GetOrThrow<DurableDict<string>>(LocationsKey)!;
        return locations.GetOrThrow<DurableDict<string>>(locationId)!;
    }

    private static DurableDict<string> GetGame(DurableDict<string> root)
        => root.GetOrThrow<DurableDict<string>>(GameKey)!;

    private static DurableDict<string> GetPlayer(DurableDict<string> root)
        => root.GetOrThrow<DurableDict<string>>(PlayerKey)!;

    private static DurableDict<string> GetCurrentTurn(DurableDict<string> root)
    {
        var game = GetGame(root);
        return game.GetOrThrow<DurableDict<string>>(CurrentTurnKey)!;
    }

    private static string GetNotebookContent(DurableDict<string> root)
    {
        var player = GetPlayer(root);
        var notebook = player.GetOrThrow<DurableText>(MemoryNotebookKey)!;
        var blocks = notebook.GetAllBlocks();
        return string.Join("\n", blocks.Select(static block => block.Content));
    }

    private static DurableText CreateNotebookText(Revision rev, string content)
    {
        var notebook = rev.CreateText();
        if (!string.IsNullOrEmpty(content))
        {
            notebook.LoadText(content);
        }

        return notebook;
    }

    private static DurableDict<string> CreateCurrentTurnState(
        Revision rev,
        int day,
        int slot,
        string locationId,
        string notebookSnapshot)
    {
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

    private static IReadOnlyList<TurnStep> ReadAcceptedSteps(DurableDict<string> root)
    {
        var currentTurn = GetCurrentTurn(root);
        var acceptedSteps = currentTurn.GetOrThrow<DurableDict<string>>(AcceptedStepsKey)!;
        return acceptedSteps.Keys
            .OrderBy(static key => key, StringComparer.Ordinal)
            .Select(key =>
            {
                var step = acceptedSteps.GetOrThrow<DurableDict<string>>(key)!;
                var numberPart = key.Split('-').Last();
                var stepNumber = int.Parse(numberPart);
                _ = step.TryGet(ActionPayloadKey, out string? actionPayload);
                return new TurnStep(
                    stepNumber,
                    step.GetOrThrow<string>(ActionKindKey)!,
                    step.GetOrThrow<string>(ActionSummaryKey)!,
                    actionPayload,
                    step.GetOrThrow<string>(ReasonTraceKey)!,
                    step.GetOrThrow<string>(ValidatorFeedbackKey)!,
                    step.GetOrThrow<bool>(EndsTurnKey));
            })
            .ToArray();
    }

    private static TurnStep AppendAcceptedStep(
        DurableDict<string> root,
        string actionKind,
        string actionSummary,
        string? actionPayload,
        string reasonTrace,
        string validatorFeedback,
        bool endsTurn)
    {
        var currentTurn = GetCurrentTurn(root);
        var acceptedSteps = currentTurn.GetOrThrow<DurableDict<string>>(AcceptedStepsKey)!;
        var stepNumber = currentTurn.GetOrThrow<int>(NextStepNumberKey);
        var stepId = $"step-{stepNumber:D4}";
        var step = root.Revision.CreateDict<string>();

        step.Upsert(ActionKindKey, actionKind);
        step.Upsert(ActionSummaryKey, actionSummary);
        if (actionPayload is not null)
        {
            step.Upsert(ActionPayloadKey, actionPayload);
        }

        step.Upsert(ReasonTraceKey, reasonTrace);
        step.Upsert(ValidatorFeedbackKey, validatorFeedback);
        step.Upsert(EndsTurnKey, endsTurn);

        acceptedSteps.Upsert(stepId, step);
        currentTurn.Upsert(NextStepNumberKey, stepNumber + 1);

        return new TurnStep(stepNumber, actionKind, actionSummary, actionPayload, reasonTrace, validatorFeedback, endsTurn);
    }

    private static void ArchiveCompletedTurn(DurableDict<string> root, string resolutionSummary)
    {
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
        archivedTurn.Upsert(EndingNotebookKey, GetNotebookContent(root));

        turnHistory.Upsert($"turn-{turnNumber:D4}", archivedTurn);
        game.Upsert(CompletedTurnCountKey, turnNumber);
    }

    private static (int Day, int Slot) AdvanceClock(DurableDict<string> root)
    {
        var game = GetGame(root);
        var day = game.GetOrThrow<int>(DayKey);
        var slot = game.GetOrThrow<int>(SlotKey);
        var slotsPerDay = game.GetOrThrow<int>(SlotsPerDayKey);
        var nextSlot = slot + 1;
        var nextDay = day;

        if (nextSlot > slotsPerDay)
        {
            nextDay++;
            nextSlot = 1;
        }

        game.Upsert(DayKey, nextDay);
        game.Upsert(SlotKey, nextSlot);
        return (nextDay, nextSlot);
    }

    private static void ResetCurrentTurn(DurableDict<string> root)
    {
        var game = GetGame(root);
        var day = game.GetOrThrow<int>(DayKey);
        var slot = game.GetOrThrow<int>(SlotKey);
        game.Upsert(
            CurrentTurnKey,
            CreateCurrentTurnState(
                root.Revision,
                day,
                slot,
                GetPlayerLocationId(root),
                GetNotebookContent(root)));
    }

    private static string? TryGetOptionalString(DurableDict<string> dict, string key)
    {
        if (!dict.TryGet(key, out string? value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value;
    }

    private static DurableDict<string> CreateLocation(Revision rev, string name, string description)
    {
        var location = rev.CreateDict<string>();
        location.Upsert(NameKey, name);
        location.Upsert(DescriptionKey, description);
        location.Upsert(ExitsKey, rev.CreateDict<string>());
        return location;
    }

    private static void AddExit(DurableDict<string> from, string direction, string targetLocationId)
    {
        var exits = from.GetOrThrow<DurableDict<string>>(ExitsKey)!;
        exits.Upsert(direction, targetLocationId);
    }
}
