using System.Text;
using Atelia.StateJournal;

namespace Atelia.TextAdv;

internal static partial class GameSimulation {
    internal static IReadOnlyList<ActorJournalExport> BuildActorJournalExports(DurableDict<string> root) {
        var actors = GetActors(root);
        var journals = TryGetActorJournals(root);
        return actors.Keys
            .OrderBy(static actorId => actorId, StringComparer.Ordinal)
            .Select(actorId => {
                var actor = actors.GetOrThrow<DurableDict<string>>(actorId)!;
                var actorName = actor.TryGet(NameKey, out string? rawName) && !string.IsNullOrWhiteSpace(rawName)
                    ? rawName
                    : actorId;
                var actorKind = actor.TryGet(KindKey, out string? rawKind) && !string.IsNullOrWhiteSpace(rawKind)
                    ? rawKind
                    : "npc";
                var content = GetActorJournalContent(journals, actorId);
                return new ActorJournalExport(
                    actorId,
                    actorName,
                    actorKind,
                    BuildActorJournalFileName(actorId, actorName),
                    content
                );
            })
            .ToArray();
    }

    internal static ActorJournalExport BuildActorJournalExport(DurableDict<string> root, string actorId) {
        actorId = NormalizeRequired(actorId, nameof(actorId));
        var actor = GetActor(root, actorId);
        var journals = TryGetActorJournals(root);
        var actorName = actor.TryGet(NameKey, out string? rawName) && !string.IsNullOrWhiteSpace(rawName)
            ? rawName
            : actorId;
        var actorKind = actor.TryGet(KindKey, out string? rawKind) && !string.IsNullOrWhiteSpace(rawKind)
            ? rawKind
            : "npc";
        var content = GetActorJournalContent(journals, actorId);
        return new ActorJournalExport(
            actorId,
            actorName,
            actorKind,
            BuildActorJournalFileName(actorId, actorName),
            content
        );
    }

    private static void AppendActorJournalsForCompletedTurn(DurableDict<string> root, string fallbackSummary) {
        var game = GetGame(root);
        var currentTurn = GetCurrentTurn(root);
        var startDay = currentTurn.GetOrThrow<int>(StartDayKey);
        var startSlot = currentTurn.GetOrThrow<int>(StartSlotKey);
        var endDay = game.GetOrThrow<int>(DayKey);
        var endSlot = game.GetOrThrow<int>(SlotKey);
        var slotsPerDay = game.GetOrThrow<int>(SlotsPerDayKey);
        var completedTurnCount = game.GetOrThrow<int>(CompletedTurnCountKey);
        var turnNumber = completedTurnCount + 1;
        var lastResolutionByActor = GetOrCreateLastResolutionByActor(root);

        foreach (var actorId in EnumerateActiveActorIds(root)) {
            var actor = GetActor(root, actorId);
            var actorName = actor.TryGet(NameKey, out string? rawName) && !string.IsNullOrWhiteSpace(rawName)
                ? rawName
                : actorId;
            var actorKind = actor.TryGet(KindKey, out string? rawKind) && !string.IsNullOrWhiteSpace(rawKind)
                ? rawKind
                : "npc";
            var locationId = GetActorLocationId(root, actorId);
            var location = GetLocation(root, locationId);
            var locationName = location.GetOrThrow<string>(NameKey)!;
            var actorResolution = lastResolutionByActor.TryGet(actorId, out string? rawResolution)
                && !string.IsNullOrWhiteSpace(rawResolution)
                    ? rawResolution
                    : fallbackSummary;

            var entry = BuildActorJournalEntry(
                turnNumber,
                actorId,
                actorName,
                actorKind,
                locationName,
                GameClock.FormatClock(startDay, startSlot, slotsPerDay),
                GameClock.FormatClock(endDay, endSlot, slotsPerDay),
                actorResolution
            );
            EnsureActorJournal(root, actorId).Append(entry);
        }
    }

    private static DurableText EnsureActorJournal(DurableDict<string> root, string actorId) {
        actorId = NormalizeRequired(actorId, nameof(actorId));
        _ = GetActor(root, actorId);
        var journals = GetOrCreateActorJournals(root);
        if (journals.TryGet(actorId, out DurableText? journal) && journal is not null) {
            return journal;
        }

        journal = CreateJournalText(root.Revision);
        journals.Upsert(actorId, journal);
        return journal;
    }

    private static DurableDict<string>? TryGetActorJournals(DurableDict<string> root) {
        var world = root.GetOrThrow<DurableDict<string>>(WorldKey)!;
        return world.TryGet(ActorJournalsKey, out DurableDict<string>? journals) ? journals : null;
    }

    private static DurableDict<string> GetOrCreateActorJournals(DurableDict<string> root) {
        var world = root.GetOrThrow<DurableDict<string>>(WorldKey)!;
        if (world.TryGet(ActorJournalsKey, out DurableDict<string>? journals) && journals is not null) {
            return journals;
        }

        journals = root.Revision.CreateDict<string>();
        world.Upsert(ActorJournalsKey, journals);
        return journals;
    }

    private static string GetJournalContent(DurableText journal) {
        var content = string.Join("\n\n", journal.GetAllBlocks().Select(static block => block.Content));
        return string.IsNullOrWhiteSpace(content) ? "(journal is empty)" : content;
    }

    private static string GetActorJournalContent(DurableDict<string>? journals, string actorId) {
        if (journals is not null
            && journals.TryGet(actorId, out DurableText? journal)
            && journal is not null) {
            return GetJournalContent(journal);
        }

        return "(journal is empty)";
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
