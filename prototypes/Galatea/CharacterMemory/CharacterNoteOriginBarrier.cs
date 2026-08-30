using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.MemoPod;
using Atelia.SessionJournal;

namespace Atelia.Galatea.Server.CharacterMemory;

internal readonly record struct CharacterNoteMemoKey(
    MemoPodId PodId,
    MemoId MemoId
);

internal sealed record CharacterNoteVisibleActionIdentity {
    internal CharacterNoteVisibleActionIdentity(
        EventAddress sourceAction,
        GalateaVisibleActionFingerprint fingerprint
    ) {
        if (sourceAction == default) {
            throw new ArgumentException(
                "A visible Action identity requires a source address.",
                nameof(sourceAction)
            );
        }
        if (string.IsNullOrEmpty(fingerprint.Sha256)
            || fingerprint.Utf8Bytes < 0) {
            throw new ArgumentException(
                "A visible Action identity requires a derived fingerprint.",
                nameof(fingerprint)
            );
        }

        SourceAction = sourceAction;
        Fingerprint = fingerprint;
    }

    internal EventAddress SourceAction { get; }
    internal GalateaVisibleActionFingerprint Fingerprint { get; }
}

internal sealed record CharacterNoteOriginBarrierEntry {
    internal CharacterNoteOriginBarrierEntry(
        MemoPodId podId,
        MemoId memoId,
        CharacterNoteVisibleActionIdentity origin
    ) {
        if (string.IsNullOrEmpty(podId.Value)) {
            throw new ArgumentException(
                "A Character Note origin barrier entry requires a Pod ID.",
                nameof(podId)
            );
        }
        if (string.IsNullOrEmpty(memoId.Value)) {
            throw new ArgumentException(
                "A Character Note origin barrier entry requires a Memo ID.",
                nameof(memoId)
            );
        }
        ArgumentNullException.ThrowIfNull(origin);

        Key = new CharacterNoteMemoKey(podId, memoId);
        Origin = origin;
    }

    internal CharacterNoteMemoKey Key { get; }
    internal CharacterNoteVisibleActionIdentity Origin { get; }
}

/// <summary>
/// Blocks recall of Character Note Memos whose durable provenance resolves to
/// a source Action still visible in the current provider context. This avoids
/// zero-information reinjection and is an ephemeral visibility projection,
/// not durable Character Memory or MemoPod authority.
/// </summary>
internal sealed class CharacterNoteOriginBarrier {
    private readonly IReadOnlyDictionary<CharacterNoteMemoKey,
        CharacterNoteOriginBarrierEntry> _entriesByKey;

    internal CharacterNoteOriginBarrier(
        IEnumerable<CharacterNoteOriginBarrierEntry> entries
    ) {
        ArgumentNullException.ThrowIfNull(entries);
        var byKey = new Dictionary<CharacterNoteMemoKey,
            CharacterNoteOriginBarrierEntry>();
        var frozen = new List<CharacterNoteMemoKey>();
        foreach (CharacterNoteOriginBarrierEntry entry in entries) {
            ArgumentNullException.ThrowIfNull(entry);
            if (byKey.TryGetValue(entry.Key, out var existing)) {
                if (existing != entry) {
                    throw new InvalidDataException(
                        "One Character Note Memo key has conflicting source provenance."
                    );
                }
                continue;
            }
            byKey.Add(entry.Key, entry);
            frozen.Add(entry.Key);
        }

        Entries = Array.AsReadOnly(frozen.ToArray());
        _entriesByKey = byKey;
    }

    internal static CharacterNoteOriginBarrier Empty { get; } = new([]);

    internal IReadOnlyList<CharacterNoteMemoKey> Entries {
        get;
    }

    internal bool Contains(MemoPodId podId, MemoId memoId) =>
        _entriesByKey.ContainsKey(new CharacterNoteMemoKey(podId, memoId));
}

internal interface ICharacterNoteOriginReader {
    CharacterNoteOriginBarrier ReadOriginBarrier(
        IReadOnlyList<CharacterNoteVisibleActionIdentity> visibleActions
    );
}

internal static class GalateaCharacterNoteOriginBarrierBuilder {
    internal static CharacterNoteOriginBarrier
        BuildFromProviderVisibleRawUnits(
        IEnumerable<SessionHistoryPlanningUnit> units,
        ICharacterNoteOriginReader? originReader
    ) {
        ArgumentNullException.ThrowIfNull(units);
        if (originReader is null) {
            return CharacterNoteOriginBarrier.Empty;
        }

        var visibleActions = new List<CharacterNoteVisibleActionIdentity>();
        foreach (SessionHistoryPlanningUnit unit in units) {
            ArgumentNullException.ThrowIfNull(unit);
            if (unit.Message is not ActionMessage action) {
                continue;
            }
            if (unit.SourceStartInclusive != unit.SourceEndInclusive) {
                throw new InvalidDataException(
                    "A provider-visible Action unit must map to one exact source address."
                );
            }

            string visibleText = GalateaVisibleActionTextRenderer.Render(
                action
            );
            visibleActions.Add(new CharacterNoteVisibleActionIdentity(
                unit.SourceStartInclusive,
                GalateaVisibleActionFingerprint.Derive(visibleText)
            ));
        }
        return originReader.ReadOriginBarrier(
            Array.AsReadOnly(visibleActions.ToArray())
        );
    }
}
