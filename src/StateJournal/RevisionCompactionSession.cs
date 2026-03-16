using System.Diagnostics;
using Atelia.Diagnostics;
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
        private readonly CommitId _primaryCommitId;
        private readonly HashSet<DurableObject> _touchedObjects = new();
        /// <summary>
        /// Apply() 入口处即赋值（即使 records 为空也会赋值）。
        /// 仅在 follow-up persist 外部失败后，才会用它驱动回滚。
        /// </summary>
        private GcPool<DurableObject>.CompactionJournal _undoToken;

        private RevisionCompactionSession(Revision revision, CommitId primaryCommitId) {
            _revision = revision;
            _primaryCommitId = primaryCommitId;
        }

        public static RevisionCompactionSession? TryApply(
            Revision revision,
            CommitId primaryCommitId,
            IReadOnlyList<DurableObject> liveObjects
        ) {
            if (!revision.ShouldCompact()) { return null; }

            var session = new RevisionCompactionSession(revision, primaryCommitId);
            return session.ApplyCore(liveObjects) ? session : null;
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
        private bool ApplyCore(IReadOnlyList<DurableObject> liveObjects) {
            if (!TryCompactPool()) { return false; }
            var translationTable = ApplyMovedObjectsAndRewrite(liveObjects);
            ValidateAppliedCompaction(liveObjects, translationTable);

            DebugUtil.Info(
                "StateJournal.Compaction",
                $"Apply succeeded: primary={_primaryCommitId.Ticket.Serialize()}, moves={_undoToken.Records.Count}, touched={_touchedObjects.Count}",
                eventKind: DebugEventKind.Success
            );
            return true;
        }

        private bool TryCompactPool() {
            _undoToken = _revision._pool.CompactWithUndo(_revision.GetCompactionMaxMoves());
            var records = _undoToken.Records;
            if (records.Count == 0) {
                DebugUtil.Trace(
                    "StateJournal.Compaction",
                    $"Skipped after planning: no movable records. primary={_primaryCommitId.Ticket.Serialize()}",
                    eventKind: DebugEventKind.Skip
                );
                return false;
            }

            DebugUtil.Trace(
                "StateJournal.Compaction",
                $"Apply start: primary={_primaryCommitId.Ticket.Serialize()}, moves={records.Count}",
                eventKind: DebugEventKind.Start
            );
            return true;
        }

        private Dictionary<uint, LocalId> ApplyMovedObjectsAndRewrite(IReadOnlyList<DurableObject> liveObjects) {
            var records = _undoToken.Records;
            if (records.Count == 0) { throw new InvalidOperationException("Compaction moves are not initialized. Call CompactPool first."); }

            var translationTable = new Dictionary<uint, LocalId>(records.Count);
            bool isFirstMove = true;
            foreach (var record in records) {
                var oldId = LocalId.FromSlotHandle(record.OldHandle);
                var newId = LocalId.FromSlotHandle(record.NewHandle);

                var obj = _revision._pool[record.NewHandle];
                obj.Rebind(newId);
                _touchedObjects.Add(obj); // track for DiscardChanges on external-failure rollback

                if (_revision._objectMap.Get(oldId.Value, out ulong ticket) == GetIssue.None) {
                    _revision._objectMap.Remove(oldId.Value);
                    _revision._objectMap.Upsert(newId.Value, ticket);
                }

                translationTable.Add(oldId.Value, newId);

                if (isFirstMove) {
                    ThrowIfCompactionFaultInjected(CompactionFaultPoint.AfterFirstMoveApplied);
                    isFirstMove = false;
                }
            }

            var rewriter = new CompactRewriter(translationTable);
            foreach (var obj in liveObjects) {
                if (obj.AcceptChildRefRewrite(ref rewriter)) {
                    _touchedObjects.Add(obj); // rewritten child refs → needs DiscardChanges on external-failure rollback
                }
            }

            return translationTable;
        }

        private void ValidateAppliedCompaction(
            IReadOnlyList<DurableObject> liveObjects,
            IReadOnlyDictionary<uint, LocalId> translationTable
        ) {
            var validateResult = _revision.ValidateCompactionApply(liveObjects, _touchedObjects, translationTable);
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
            Debug.Assert(_undoToken.Records is not null, "RollbackAfterFollowupPersistFailure called before _undoToken was initialized by Apply().");

            var issue = BuildCompactionFollowupPersistFailureError(_primaryCommitId, cause);
            _revision.RollbackCompactionChanges(_undoToken, _touchedObjects);
            DebugUtil.Warning(
                "StateJournal.Compaction",
                $"Rollback succeeded: primary={_primaryCommitId.Ticket.Serialize()}, stage=FollowupPersist, issue={issue.ErrorCode}, touched={_touchedObjects.Count}",
                eventKind: DebugEventKind.Failure
            );
            return CommitOutcome.CompactionRolledBack(_primaryCommitId, issue);
        }
    }
}
