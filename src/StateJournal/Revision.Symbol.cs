using Atelia.StateJournal.Internal;
using Atelia.StateJournal.Pools;

namespace Atelia.StateJournal;

partial class Revision {
    internal enum SymbolMirrorValidationMode {
        None = 0,
        FullScan = 1,
    }

    internal readonly record struct SymbolMirrorUpdatePlan(
        SymbolMirrorValidationMode ValidationMode = SymbolMirrorValidationMode.None
    ) {
        public static SymbolMirrorUpdatePlan ReachableScan(bool validateFullScan = true) => new(
            validateFullScan ? SymbolMirrorValidationMode.FullScan : SymbolMirrorValidationMode.None
        );
    }

    private AteliaResult<bool> UpdateSymbolMirror(SymbolMirrorUpdatePlan plan) {
        AteliaResult<bool> updateResult = UpdateSymbolMirrorByReachableScan();
        if (updateResult.IsFailure) { return updateResult; }

        if (plan.ValidationMode == SymbolMirrorValidationMode.FullScan) {
            return ValidateSymbolMirrorConsistency(reachableOnly: true);
        }

        return true;
    }

    private AteliaResult<bool> UpdateSymbolMirrorByReachableScan() {
        ReconcileSymbolTableFromPool(reachableOnly: true);
        return true;
    }
    #region Visitors
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
