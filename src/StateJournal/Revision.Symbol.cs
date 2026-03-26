using Atelia.StateJournal.Internal;
using Atelia.StateJournal.Pools;

namespace Atelia.StateJournal;

partial class Revision {
    internal enum SymbolMirrorUpdateMode {
        ReachableScan = 0,
        Unchanged = 1,
        RemapByJournal = 2,
    }

    internal enum SymbolMirrorValidationMode {
        None = 0,
        FullScan = 1,
    }

    internal readonly record struct SymbolMirrorUpdatePlan(
        SymbolMirrorUpdateMode Mode,
        SymbolMirrorValidationMode ValidationMode = SymbolMirrorValidationMode.None,
        InternPool<string, OrdinalStaticEqualityComparer>.CompactionJournal? SymbolJournal = null
    ) {
        public static SymbolMirrorUpdatePlan ReachableScan(bool validateFullScan = true) => new(
            SymbolMirrorUpdateMode.ReachableScan,
            validateFullScan ? SymbolMirrorValidationMode.FullScan : SymbolMirrorValidationMode.None
        );

        public static SymbolMirrorUpdatePlan Unchanged(SymbolMirrorValidationMode validationMode = SymbolMirrorValidationMode.None) => new(
            SymbolMirrorUpdateMode.Unchanged,
            validationMode
        );

        public static SymbolMirrorUpdatePlan RemapByJournal(
            InternPool<string, OrdinalStaticEqualityComparer>.CompactionJournal journal,
            SymbolMirrorValidationMode validationMode = SymbolMirrorValidationMode.None
        ) => new(SymbolMirrorUpdateMode.RemapByJournal, validationMode, journal);
    }

    private AteliaResult<bool> UpdateSymbolMirror(SymbolMirrorUpdatePlan plan) {
        AteliaResult<bool> updateResult = plan.Mode switch {
            SymbolMirrorUpdateMode.ReachableScan => UpdateSymbolMirrorByReachableScan(),

            SymbolMirrorUpdateMode.Unchanged => true,

            SymbolMirrorUpdateMode.RemapByJournal =>
                plan.SymbolJournal is not { } journal
                    ? throw new InvalidOperationException("RemapByJournal requires a non-null symbol compaction journal.")
                    : RemapSymbolMirrorByJournal(journal),

            _ => throw new InvalidOperationException($"Unknown symbol mirror update mode: {plan.Mode}.")
        };
        if (updateResult.IsFailure) { return updateResult; }

        if (plan.ValidationMode == SymbolMirrorValidationMode.FullScan) {
            return ValidateSymbolMirrorConsistency(reachableOnly: plan.Mode == SymbolMirrorUpdateMode.ReachableScan);
        }

        return true;
    }

    private AteliaResult<bool> UpdateSymbolMirrorByReachableScan() {
        ReconcileSymbolTableFromPool(reachableOnly: true);
        return true;
    }

    private AteliaResult<bool> RemapSymbolMirrorByJournal(
        InternPool<string, OrdinalStaticEqualityComparer>.CompactionJournal journal
    ) {
        if (journal.Records.Count == 0) { return true; }

        int countBefore = _symbolTable.Count;
        var remaps = new List<(uint OldPacked, uint NewPacked, InlineString Value)>(journal.Records.Count);

        foreach (var record in journal.Records) {
            uint oldPacked = record.OldHandle.Packed;
            uint newPacked = record.NewHandle.Packed;

            if (_symbolTable.Get(oldPacked, out InlineString existing) != GetIssue.None) {
                return new SjCorruptionError(
                    $"SymbolTable is missing remap source symbol entry {oldPacked}.",
                    RecoveryHint: "Compaction follow-up symbol mirror is inconsistent with the primary snapshot."
                );
            }

            remaps.Add((oldPacked, newPacked, existing));
        }

        foreach (var remap in remaps) {
            _symbolTable.Remove(remap.OldPacked);
        }

        foreach (var remap in remaps) {
            _symbolTable.Upsert(remap.NewPacked, remap.Value);
        }

        if (_symbolTable.Count != countBefore) {
            return new SjCorruptionError(
                $"SymbolTable count changed from {countBefore} to {_symbolTable.Count} during journal remap.",
                RecoveryHint: "Compaction follow-up symbol mirror remap corrupted the durable mirror."
            );
        }

        foreach (var remap in remaps) {
            if (_symbolTable.Get(remap.OldPacked, out _) == GetIssue.None) {
                return new SjCorruptionError(
                    $"SymbolTable old symbol entry {remap.OldPacked} still exists after journal remap.",
                    RecoveryHint: "Compaction follow-up symbol mirror remap left stale keys behind."
                );
            }

            if (_symbolTable.Get(remap.NewPacked, out InlineString remapped) != GetIssue.None) {
                return new SjCorruptionError(
                    $"SymbolTable is missing remapped symbol entry {remap.NewPacked}.",
                    RecoveryHint: "Compaction follow-up symbol mirror remap failed to materialize new keys."
                );
            }

            if (!_symbolPool.TryGetValue(new SlotHandle(remap.NewPacked), out string pooledValue)) {
                return new SjCorruptionError(
                    $"Symbol pool is missing remapped symbol entry {remap.NewPacked}.",
                    RecoveryHint: "Compaction follow-up symbol mirror remap observed stale journal data."
                );
            }

            if (remapped.Value != pooledValue) {
                return new SjCorruptionError(
                    $"SymbolTable value mismatch for remapped symbol {remap.NewPacked}.",
                    RecoveryHint: "Compaction follow-up symbol mirror remap produced inconsistent values."
                );
            }
        }

        return true;
    }

    private bool ShouldSymbolCompact() {
        int liveCount = _symbolPool.Count;
        int capacity = _symbolPool.Capacity;
        if (liveCount < SymbolCompactionMinThreshold || capacity == 0) { return false; }

        int holeCount = capacity - liveCount;
        return holeCount * 100 > capacity * CompactionTriggerPercent;
    }

    private int GetSymbolCompactionMaxMoves() {
        int liveCount = _symbolPool.Count;
        return Math.Max(1, (int)((long)liveCount * CompactionMovePercent / 100));
    }
    #region Visitors
    /// <summary>Symbol 池的存活条目数低于此值时不触发压缩。</summary>
    private const int SymbolCompactionMinThreshold = 64;

    partial struct CompactRewriter {
        public SymbolId Rewrite(SymbolId oldId) {
            return symbolTable is not null && symbolTable.TryGetValue(oldId.Value, out var newId) ? newId : oldId;
        }
    }

    partial struct WalkMarkVisitor {
        public void Visit(SymbolId symbolId) {
            if (symbolId.IsNull) { return; }
            if (!symbolPool.TryMarkReachable(symbolId.ToSlotHandle())) {
                Error = new SjCorruptionError(
                    $"Dangling symbol reference detected during commit: Graph contains missing SymbolId {symbolId.Value}.",
                    RecoveryHint: "Fix string/symbol references before commit."
                );
            }
        }
    }

    partial struct ReferenceValidationVisitor {
        public void Visit(SymbolId symbolId) {
            if (Error is not null || symbolId.IsNull) { return; }
            if (!symbolPool.Validate(symbolId.ToSlotHandle())) {
                Error = new SjCorruptionError(
                    $"Dangling symbol reference detected: parent LocalId {parentId.Value} points to missing SymbolId {symbolId.Value}.",
                    RecoveryHint: "Data corruption detected in persisted symbol references."
                );
            }
        }
    }
    #endregion
}
