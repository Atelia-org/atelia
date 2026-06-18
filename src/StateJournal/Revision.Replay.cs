using Atelia.Rbf;
using Atelia.StateJournal.Internal;
using Atelia.StateJournal.Pools;

namespace Atelia.StateJournal;

partial class Revision {
    internal AteliaResult<TDurable> ReplayCommittedCore<TDurable>(
        TDurable source,
        IRbfFile sourceFile,
        LoadMaterializationMode materializationMode
    )
        where TDurable : DurableObject {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceFile);

        if (!ReferenceEquals(source.BoundRevision, this)) {
            return new SjStateError(
                "Cannot replay committed state from a DurableObject bound to a different Revision.",
                RecoveryHint: "Pass a source object that belongs to the same loaded Revision."
            );
        }

        if (source.IsDetached) {
            return new SjStateError(
                $"Cannot replay committed state from detached LocalId={source.LocalId.Value}.",
                RecoveryHint: "Keep the source object reachable or reload it before replaying."
            );
        }

        SlotHandle sourceHandle = source.LocalId.ToSlotHandle();
        if (!_pool.Validate(sourceHandle)) {
            return new SjStateError(
                $"Cannot replay committed state from LocalId={source.LocalId.Value}: object is no longer tracked by this Revision.",
                RecoveryHint: "Use a live object from the currently loaded Revision."
            );
        }

        if (!source.IsTracked) {
            return new SjStateError(
                $"Cannot replay committed state from LocalId={source.LocalId.Value}: source has no committed version chain yet.",
                RecoveryHint: "Commit the source object first, or use a tracked object loaded from an existing commit."
            );
        }

        AteliaResult<DurableObject> loadResult;
        try {
            loadResult = VersionChain.Load(
                sourceFile,
                source.HeadTicket,
                expectObject: source.Kind,
                symbolPool: _symbolPool,
                materializationMode: materializationMode
            );
        }
        catch (NotSupportedException ex) {
            return new SjStateError(
                $"ReplayCommitted does not support materializing {source.GetType().Name} with mode {materializationMode}: {ex.Message}",
                RecoveryHint: "Choose a different LoadMaterializationMode, or use a durable type that supports this materialization."
            );
        }
        if (loadResult.IsFailure) { return loadResult.Error!; }
        if (loadResult.Value is not TDurable replayed) {
            return new SjCorruptionError(
                $"ReplayCommitted loaded unexpected type {loadResult.Value!.GetType().Name}; expected {typeof(TDurable).Name}.",
                RecoveryHint: "The stored version chain does not match the requested durable type."
            );
        }

        BindForkedObject(replayed);
        return replayed;
    }
}
