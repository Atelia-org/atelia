using System.Diagnostics;
using Atelia.Diagnostics;
using Atelia.StateJournal.Internal;
using Atelia.StateJournal.Pools;

namespace Atelia.StateJournal;

partial class Revision {
    /// <summary>
    /// 封装一次 primary commit 之后的 compaction apply / rollback / follow-up 持久化故障处理。
    /// </summary>
    /// <remarks>
    /// 目标语义：
    /// - 纯内存 compaction apply / rollback 中的内部不变量破坏属于 bug，应 fail-fast；
    /// - 只有 follow-up persist 遭遇外部不可控因素时，才构造可诊断的 <see cref="AteliaError"/> 并回到 <see cref="CommitOutcome"/>。
    /// </remarks>
    internal sealed partial class RevisionCompactionSession {
        private readonly Revision _revision;
        private readonly CommitTicket _primaryCommitTicket;
        private readonly HashSet<DurableObject> _touchedObjects = new();
        private GcPool<DurableObject>.CompactionJournal? _objectUndoToken;
        private InternPool<string, OrdinalStaticEqualityComparer>.CompactionJournal? _symbolUndoToken;

        private RevisionCompactionSession(Revision revision, CommitTicket primaryCommitTicket) {
            _revision = revision;
            _primaryCommitTicket = primaryCommitTicket;
        }

        internal SymbolMirrorUpdatePlan SymbolMirrorUpdatePlan =>
            _symbolUndoToken is { } journal
                ? SymbolMirrorUpdatePlan.RemapByJournal(journal, GetFollowupSymbolMirrorValidationMode())
                : SymbolMirrorUpdatePlan.Unchanged(GetFollowupSymbolMirrorValidationMode());

        private static SymbolMirrorValidationMode GetFollowupSymbolMirrorValidationMode() {
            return GetCompactionValidationMode() == CompactionValidationMode.Strict
                ? SymbolMirrorValidationMode.FullScan
                : SymbolMirrorValidationMode.None;
        }

        public static RevisionCompactionSession? TryApply(
            Revision revision,
            CommitTicket primaryCommitTicket,
            IReadOnlyList<DurableObject> liveObjects
        ) {
            bool shouldObjectCompact = revision.ShouldCompact();
            bool shouldSymbolCompact = revision.ShouldSymbolCompact();
            if (!shouldObjectCompact && !shouldSymbolCompact) { return null; }

            var session = new RevisionCompactionSession(revision, primaryCommitTicket);
            return session.ApplyCore(liveObjects, shouldObjectCompact, shouldSymbolCompact) ? session : null;
        }

        /// <summary>
        /// 若 compaction 真正发生，则执行 apply 并返回可用于后续 rollback 的 session；否则返回 null。
        /// </summary>
        /// <remarks>
        /// 这会把“是否触发 compact”与“是否真的发生移动”合并为单次判定。
        ///
        /// 目标语义下，内部 apply 过程应在前置条件满足时保持 no-throw except bug：
        /// 若发生异常，优先解释为内部实现 bug 或不变量破坏，而非普通业务失败。
        /// </remarks>
        private bool ApplyCore(IReadOnlyList<DurableObject> liveObjects, bool shouldObjectCompact, bool shouldSymbolCompact) {
            bool objectMoved = shouldObjectCompact && TryCompactPool();
            bool symbolMoved = shouldSymbolCompact && TryCompactSymbolPool();
            if (!objectMoved && !symbolMoved) { return false; }

            var (objectTranslationTable, symbolTranslationTable) = ApplyMovedObjectsAndRewrite(liveObjects, objectMoved, symbolMoved);
            ValidateAppliedCompaction(liveObjects, objectTranslationTable);

            DebugUtil.Info(
                "StateJournal.Compaction",
                $"Apply succeeded: primary={_primaryCommitTicket.Ticket.Serialize()}, objectMoves={_objectUndoToken?.Records.Count ?? 0}, symbolMoves={_symbolUndoToken?.Records.Count ?? 0}, touched={_touchedObjects.Count}",
                eventKind: DebugEventKind.Success
            );
            return true;
        }

        private bool TryCompactPool() {
            var journal = _revision._pool.CompactWithUndo(_revision.GetCompactionMaxMoves());
            var records = journal.Records;
            if (records.Count == 0) {
                _objectUndoToken = null;
                DebugUtil.Trace(
                    "StateJournal.Compaction",
                    $"Object pool: skipped, no movable records. primary={_primaryCommitTicket.Ticket.Serialize()}",
                    eventKind: DebugEventKind.Skip
                );
                return false;
            }

            _objectUndoToken = journal;
            DebugUtil.Trace(
                "StateJournal.Compaction",
                $"Object pool: start, moves={records.Count}. primary={_primaryCommitTicket.Ticket.Serialize()}",
                eventKind: DebugEventKind.Start
            );
            return true;
        }

        private bool TryCompactSymbolPool() {
            var journal = _revision._symbolPool.CompactWithUndo(_revision.GetSymbolCompactionMaxMoves());
            if (journal.Records.Count == 0) {
                DebugUtil.Trace(
                    "StateJournal.Compaction",
                    $"Symbol pool: skipped, no movable records. primary={_primaryCommitTicket.Ticket.Serialize()}",
                    eventKind: DebugEventKind.Skip
                );
                return false;
            }
            _symbolUndoToken = journal;
            DebugUtil.Trace(
                "StateJournal.Compaction",
                $"Symbol pool: start, moves={journal.Records.Count}. primary={_primaryCommitTicket.Ticket.Serialize()}",
                eventKind: DebugEventKind.Start
            );
            return true;
        }

        private (Dictionary<uint, LocalId>? objectTable, Dictionary<uint, SymbolId>? symbolTable) ApplyMovedObjectsAndRewrite(
            IReadOnlyList<DurableObject> liveObjects, bool objectMoved, bool symbolMoved
        ) {
            Dictionary<uint, LocalId>? objectTranslationTable = null;
            Dictionary<uint, SymbolId>? symbolTranslationTable = null;

            // ── Object Compaction: Rebind + ObjectMap key update ──
            if (objectMoved) {
                var records = _objectUndoToken!.Value.Records;
                objectTranslationTable = new Dictionary<uint, LocalId>(records.Count);
                bool isFirstMove = true;
                foreach (var record in records) {
                    var oldId = LocalId.FromSlotHandle(record.OldHandle);
                    var newId = LocalId.FromSlotHandle(record.NewHandle);

                    var obj = _revision._pool[record.NewHandle];
                    obj.Rebind(newId);
                    _touchedObjects.Add(obj);

                    if (_revision._objectMap.Get(oldId.Value, out ulong ticket) == GetIssue.None) {
                        _revision._objectMap.Remove(oldId.Value);
                        _revision._objectMap.Upsert(newId.Value, ticket);
                    }

                    objectTranslationTable.Add(oldId.Value, newId);

                    if (isFirstMove) {
                        ThrowIfCompactionFaultInjected(CompactionFaultPoint.AfterFirstMoveApplied);
                        isFirstMove = false;
                    }
                }
            }

            // ── Symbol Compaction: 只记录 SymbolId 翻译表，durable mirror 留待持久化前统一 reconcile ──
            if (symbolMoved) {
                var records = _symbolUndoToken!.Value.Records;
                symbolTranslationTable = new Dictionary<uint, SymbolId>(records.Count);
                foreach (var record in records) {
                    var oldSymbolId = new SymbolId(record.OldHandle.Packed);
                    var newSymbolId = new SymbolId(record.NewHandle.Packed);
                    symbolTranslationTable.Add(oldSymbolId.Value, newSymbolId);
                }
            }

            // ── 统一遍历：同时重写 LocalId 和 SymbolId 引用 ──
            var rewriter = new CompactRewriter(
                objectTranslationTable ?? new Dictionary<uint, LocalId>(),
                symbolTranslationTable
            );
            foreach (var obj in liveObjects) {
                if (obj.AcceptChildRefRewrite(ref rewriter)) {
                    _touchedObjects.Add(obj);
                }
            }

            return (objectTranslationTable, symbolTranslationTable);
        }

        private void ValidateAppliedCompaction(
            IReadOnlyList<DurableObject> liveObjects,
            IReadOnlyDictionary<uint, LocalId>? objectTranslationTable
        ) {
            var validateResult = _revision.ValidateCompactionApply(
                liveObjects,
                _touchedObjects,
                objectTranslationTable ?? EmptyObjectTranslationTable.Instance
            );
            if (validateResult.IsFailure) {
                throw new InvalidOperationException(
                    $"Compaction produced invalid references: {validateResult.Error!.Message}"
                );
            }
        }

        /// <summary>
        /// 在 follow-up persist 返回 <see cref="AteliaError"/> 后尝试回滚内存 compaction。
        /// </summary>
        /// <remarks>
        /// 若 rollback 过程中违反内部不变量，会直接抛异常 fail-fast。
        /// </remarks>
        public CommitOutcome RollbackAfterFollowupPersistFailure(AteliaError cause) {
            Debug.Assert(_objectUndoToken is not null || _symbolUndoToken is not null,
                "RollbackAfterFollowupPersistFailure called before any undo token was initialized by Apply().");

            var issue = BuildCompactionFollowupPersistFailureError(_primaryCommitTicket, cause);
            _revision.RollbackCompactionChanges(_objectUndoToken, _symbolUndoToken, _touchedObjects);
            DebugUtil.Warning(
                "StateJournal.Compaction",
                $"Rollback succeeded: primary={_primaryCommitTicket.Ticket.Serialize()}, stage=FollowupPersist, issue={issue.ErrorCode}, touched={_touchedObjects.Count}",
                eventKind: DebugEventKind.Failure
            );
            return CommitOutcome.CompactionRolledBack(_primaryCommitTicket, issue);
        }

        private static class EmptyObjectTranslationTable {
            public static readonly IReadOnlyDictionary<uint, LocalId> Instance = new Dictionary<uint, LocalId>(0);
        }
    }
}
