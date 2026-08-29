using Atelia.Completion.Abstractions;

namespace Atelia.Galatea.Server;

internal sealed class RecallBarrier {
    internal RecallBarrier(IEnumerable<RecallEntry> entries) {
        ArgumentNullException.ThrowIfNull(entries);
        var unique = new HashSet<RecallEntry>();
        var frozen = new List<RecallEntry>();
        foreach (RecallEntry entry in entries) {
            ArgumentNullException.ThrowIfNull(entry);
            if (unique.Add(entry)) {
                frozen.Add(entry);
            }
        }

        Entries = Array.AsReadOnly(frozen.ToArray());
        _entries = unique;
    }

    private readonly HashSet<RecallEntry> _entries;

    internal static RecallBarrier Empty { get; } = new([]);

    internal IReadOnlyList<RecallEntry> Entries { get; }

    internal bool Contains(RecallEntry entry) {
        ArgumentNullException.ThrowIfNull(entry);
        return _entries.Contains(entry);
    }

    internal bool Contains(RecallType recallType, string sourceId) =>
        Contains(new RecallEntry(recallType, sourceId));
}

internal static class GalateaRecallBarrierBuilder {
    internal static RecallBarrier Build(
        IEnumerable<PlayerTurnObservation> observations
    ) {
        ArgumentNullException.ThrowIfNull(observations);
        return new RecallBarrier(observations.SelectMany(
            static observation => {
                ArgumentNullException.ThrowIfNull(observation);
                return observation.Recalls.Select(
                    static recall => recall.Entry
                );
            }
        ));
    }

    internal static RecallBarrier BuildFromProviderVisibleObservations(
        IEnumerable<string?> observationContents
    ) {
        ArgumentNullException.ThrowIfNull(observationContents);
        var entries = new List<RecallEntry>();
        foreach (string? stored in observationContents) {
            if (!PlayerTurnObservationEnvelope.TryUnwrap(
                    stored,
                    out PlayerTurnObservation observation)) {
                continue;
            }
            entries.AddRange(observation.Recalls.Select(
                static recall => recall.Entry
            ));
        }
        return new RecallBarrier(entries);
    }

    internal static RecallBarrier BuildFromProviderVisibleMessages(
        IEnumerable<IHistoryMessage> messages
    ) {
        ArgumentNullException.ThrowIfNull(messages);
        return BuildFromProviderVisibleObservations(messages
            .OfType<ObservationMessage>()
            .Select(static message => message.Content));
    }
}
