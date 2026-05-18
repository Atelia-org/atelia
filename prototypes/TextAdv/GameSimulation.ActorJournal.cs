using System.Text;
using Atelia.StateJournal;

namespace Atelia.TextAdv;

internal static partial class GameSimulation {
    internal static IReadOnlyList<ActorJournalExport> BuildActorJournalExports(DurableDict<string> root) {
        var actors = GetActors(root);
        return actors.Keys
            .OrderBy(static actorId => actorId, StringComparer.Ordinal)
            .Select(actorId => BuildActorJournalExport(root, actorId))
            .ToArray();
    }

    internal static ActorJournalExport BuildActorJournalExport(DurableDict<string> root, string actorId) {
        actorId = NormalizeRequired(actorId, nameof(actorId));
        var actor = GetActor(root, actorId);
        var actorName = actor.TryGet(NameKey, out string? rawName) && !string.IsNullOrWhiteSpace(rawName)
            ? rawName
            : actorId;
        var actorKind = GetActorKind(actor);
        var content = BuildActorJournalContent(root, actorId, actorName, actorKind);
        return new ActorJournalExport(
            actorId,
            actorName,
            actorKind,
            BuildActorJournalFileName(actorId, actorName),
            content
        );
    }

    private static string BuildActorJournalContent(
        DurableDict<string> root,
        string actorId,
        string fallbackActorName,
        string fallbackActorKind
    ) {
        var archivedTurns = GetArchivedTurns(root);
        var entries = new List<string>(archivedTurns.Count);
        for (var index = 0; index < archivedTurns.Count; index++) {
            var entry = TryBuildActorJournalEntry(root, archivedTurns, index, actorId, fallbackActorName, fallbackActorKind);
            if (!string.IsNullOrWhiteSpace(entry)) {
                entries.Add(entry);
            }
        }

        return entries.Count == 0 ? "(journal is empty)" : string.Join("\n\n", entries);
    }

    private static string? TryBuildActorJournalEntry(
        DurableDict<string> root,
        IReadOnlyList<DurableDict<string>> archivedTurns,
        int turnIndex,
        string actorId,
        string fallbackActorName,
        string fallbackActorKind
    ) {
        var archivedTurn = archivedTurns[turnIndex];
        var actorResolution = ReadArchivedActorResolution(archivedTurn, actorId);
        var actorContext = ReadArchivedActorTurnContext(archivedTurn, actorId);
        if (actorResolution is null && actorContext is null) {
            return null;
        }

        var actorName = actorContext?.ActorName ?? fallbackActorName;
        var actorKind = actorContext?.ActorKind ?? fallbackActorKind;
        var locationName = actorContext?.LocationName ?? TryGetCurrentActorLocationName(root, actorId) ?? "未知地点";
        var startDay = archivedTurn.GetOrThrow<int>(StartDayKey);
        var startSlot = archivedTurn.GetOrThrow<int>(StartSlotKey);
        var (endDay, endSlot) = ResolveArchivedTurnEndClock(root, archivedTurns, turnIndex);
        var slotsPerDay = GetGame(root).GetOrThrow<int>(SlotsPerDayKey);
        return BuildActorJournalEntry(
            archivedTurn.GetOrThrow<int>(TurnNumberKey),
            actorId,
            actorName,
            actorKind,
            locationName,
            GameClock.FormatClock(startDay, startSlot, slotsPerDay),
            GameClock.FormatClock(endDay, endSlot, slotsPerDay),
            actorResolution ?? archivedTurn.GetOrThrow<string>(ResolutionSummaryKey)!
        );
    }

    private static IReadOnlyList<DurableDict<string>> GetArchivedTurns(DurableDict<string> root) {
        var game = GetGame(root);
        var turnHistory = game.GetOrThrow<DurableDict<string>>(TurnHistoryKey)!;
        return turnHistory.Keys
            .OrderBy(static turnId => turnId, StringComparer.Ordinal)
            .Select(turnId => turnHistory.GetOrThrow<DurableDict<string>>(turnId)!)
            .ToArray();
    }

    private static (int EndDay, int EndSlot) ResolveArchivedTurnEndClock(
        DurableDict<string> root,
        IReadOnlyList<DurableDict<string>> archivedTurns,
        int turnIndex
    ) {
        var archivedTurn = archivedTurns[turnIndex];
        if (archivedTurn.TryGet(EndDayKey, out int endDay) && archivedTurn.TryGet(EndSlotKey, out int endSlot)) {
            return (endDay, endSlot);
        }

        if (turnIndex + 1 < archivedTurns.Count) {
            var nextTurn = archivedTurns[turnIndex + 1];
            return (
                nextTurn.GetOrThrow<int>(StartDayKey),
                nextTurn.GetOrThrow<int>(StartSlotKey)
            );
        }

        var game = GetGame(root);
        return (
            game.GetOrThrow<int>(DayKey),
            game.GetOrThrow<int>(SlotKey)
        );
    }

    private static string? ReadArchivedActorResolution(DurableDict<string> archivedTurn, string actorId) {
        if (archivedTurn.TryGet(LastResolutionByActorKey, out DurableDict<string>? lastResolutionByActor)
            && lastResolutionByActor is not null
            && lastResolutionByActor.TryGet(actorId, out string? actorResolution)
            && !string.IsNullOrWhiteSpace(actorResolution)) {
            return actorResolution;
        }

        return null;
    }

    private static (string ActorName, string ActorKind, string LocationName)? ReadArchivedActorTurnContext(
        DurableDict<string> archivedTurn,
        string actorId
    ) {
        if (archivedTurn.TryGet(ActorTurnContextByActorKey, out DurableDict<string>? contextsByActor)
            && contextsByActor is not null
            && contextsByActor.TryGet(actorId, out DurableDict<string>? context)
            && context is not null) {
            var actorName = context.TryGet(NameKey, out string? rawName) && !string.IsNullOrWhiteSpace(rawName)
                ? rawName
                : actorId;
            var actorKind = GetActorKind(context);
            var locationName = context.TryGet(LocationNameKey, out string? rawLocationName) && !string.IsNullOrWhiteSpace(rawLocationName)
                ? rawLocationName
                : "未知地点";
            return (actorName, actorKind, locationName);
        }

        return null;
    }

    private static string? TryGetCurrentActorLocationName(DurableDict<string> root, string actorId) {
        var actors = GetActors(root);
        if (!actors.TryGet(actorId, out DurableDict<string>? actor) || actor is null) {
            return null;
        }

        var locationId = actor.GetOrThrow<string>(LocationIdKey)!;
        var location = GetLocation(root, locationId);
        return location.GetOrThrow<string>(NameKey);
    }

    private static string BuildActorJournalEntry(
        int turnNumber,
        string actorId,
        string actorName,
        string actorKind,
        string locationName,
        string startClock,
        string endClock,
        string resolution
    ) {
        var firstPerson = ConvertResolutionToFirstPerson(resolution);
        var sb = new StringBuilder();
        sb.AppendLine($"### Turn {turnNumber:D4} · {startClock} -> {endClock}");
        sb.AppendLine($"我叫 {actorName}（{actorId}, {actorKind}）。此刻我在「{locationName}」。");
        sb.AppendLine(firstPerson);
        return sb.ToString().TrimEnd();
    }

    private static string ConvertResolutionToFirstPerson(string resolution) {
        resolution = string.IsNullOrWhiteSpace(resolution) ? "这一回合没有清晰的可记录反馈。" : resolution.Trim();
        return resolution;
    }

    private static string BuildActorJournalFileName(string actorId, string actorName) {
        var safeActorId = SanitizeFileNamePart(actorId);
        var safeActorName = SanitizeFileNamePart(actorName);
        return string.Equals(safeActorId, safeActorName, StringComparison.OrdinalIgnoreCase)
            ? $"{safeActorId}.md"
            : $"{safeActorId}-{safeActorName}.md";
    }

    private static string SanitizeFileNamePart(string value) {
        value = string.IsNullOrWhiteSpace(value) ? "actor" : value.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value
            .Select(ch => invalid.Contains(ch) || char.IsWhiteSpace(ch) ? '-' : ch)
            .ToArray();
        var sanitized = new string(chars).Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "actor" : sanitized;
    }
}
