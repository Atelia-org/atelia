using System.Diagnostics;
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
        PruneUnreachableSymbolsFromMirror(reachableOnly: true);

        Debug.Assert(ValidateSymbolMirrorConsistency(reachableOnly: true).Unwrap() == true);

        return true;
    }

    /// <summary>
    /// 写时把 symbol durable mirror 补齐到至少包含本次实际落盘用到的 symbol。
    /// 这是一个 write-through 侧效应：typed string / mixed string 在写出时都会触达此入口。
    /// </summary>
    internal void EnsureSymbolMirrored(string value, SymbolId id) {
        Debug.Assert(_symbolPool.TryGetValue(id.ToSlotHandle(), out string? existed) && string.Equals(existed, value, StringComparison.Ordinal));
        _symbolMirror.Upsert(id.Value, new InlineString(value));
    }

    /// <summary>
    /// mixed string 路径在写出时只有 SymbolId；此处从当前 runtime symbol pool 反查 string，
    /// 再将 durable mirror 补齐。
    /// </summary>
    internal void EnsureSymbolIdMirrored(SymbolId id) {
        if (!_symbolPool.TryGetValue(id.ToSlotHandle(), out string value)) { throw new InvalidDataException(); }
        EnsureSymbolMirrored(value, id);
    }

    private void PruneUnreachableSymbolsFromMirror(bool reachableOnly) {
        foreach (uint key in _symbolMirror.CommittedKeys) {
            SlotHandle handle = new(key);
            if (!_symbolPool.Validate(handle) || (reachableOnly && !_symbolPool.IsMarkedReachable(handle))) {
                _symbolMirror.Remove(key);
            }
        }
    }

    private AteliaResult<bool> ValidateSymbolMirrorConsistency(bool reachableOnly) {
        var checker = new SymbolMirrorValidator(_symbolMirror, _symbolPool, reachableOnly);
        _symbolPool.VisitEntries(ref checker);
        if (checker.Error is not null) { return checker.Error; }

        if (checker.ObservedCount != _symbolMirror.Count) {
            return new SjCorruptionError(
                $"SymbolTable count {_symbolMirror.Count} does not match symbol pool live count {checker.ObservedCount}.",
                RecoveryHint: "Symbol mirror is inconsistent with the runtime symbol pool."
            );
        }

        return true;
    }

    private ref struct SymbolMirrorValidator(
        DurableDict<uint, InlineString> symbolTable,
        StringPool symbolPool,
        bool reachableOnly) : StringPool.IEntryVisitor {
        public AteliaError? Error { get; private set; }
        public int ObservedCount { get; private set; }

        public void Visit(SlotHandle handle, string value) {
            if (Error is not null) { return; }
            if (reachableOnly && !symbolPool.IsMarkedReachable(handle)) { return; }

            ObservedCount++;
            uint packed = handle.Packed;
            if (symbolTable.Get(packed, out InlineString inline) != GetIssue.None) {
                Error = new SjCorruptionError(
                    $"SymbolTable is missing runtime symbol entry {packed}.",
                    RecoveryHint: "Symbol mirror is inconsistent with the runtime symbol pool."
                );
                return;
            }

            if (inline.Value != value) {
                Error = new SjCorruptionError(
                    $"SymbolTable value mismatch for symbol {packed}.",
                    RecoveryHint: "Symbol mirror is inconsistent with the runtime symbol pool."
                );
            }
        }
    }

    #region Visitors
    partial struct WalkMarkVisitor {
        public void Visit(string? value) {
            if (value is null) { return; }
            symbolPool.MarkReachable(symbolPool.Store(value));
        }

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
        /// typed string facade 在 load 完成后由 ValidateReconstructed 统一校验；
        /// 这里不做 symbol 级验证，避免把“校验”与“补齐/解析”职责重新耦合。
        public void Visit(string? value) { }

        // mixed 容器里的直接 SymbolId 同样交由各自的 ValidateReconstructed 负责。
        public void Visit(SymbolId symbolId) { }
    }
    #endregion
}
