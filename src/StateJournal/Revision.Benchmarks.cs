using Atelia.StateJournal.Internal;
using Atelia.Rbf;

namespace Atelia.StateJournal;

partial class Revision {
    internal sealed partial class RevisionCompactionSession {
        /// <summary>
        /// benchmark/诊断专用：只完成“应该开始 compaction 吗”的入口判定，不执行 apply。
        /// </summary>
        internal static RevisionCompactionSession? TryStartForBenchmark(Revision revision, CommitTicket primaryCommitTicket) {
            return revision.ShouldCompact()
                ? new RevisionCompactionSession(revision, primaryCommitTicket)
                : null;
        }

        internal int CompactPoolForBenchmark() {
            return TryCompactPool() ? _objectUndoToken?.Records.Count ?? 0 : 0;
        }

        internal Dictionary<uint, LocalId> ApplyMovedObjectsAndRewriteForBenchmark(IReadOnlyList<DurableObject> liveObjects) {
            var (objectTable, _) = ApplyMovedObjectsAndRewrite(liveObjects, objectMoved: true, symbolMoved: false);
            return objectTable!;
        }

        internal void ValidateAppliedCompactionForBenchmark(
            IReadOnlyList<DurableObject> liveObjects,
            IReadOnlyDictionary<uint, LocalId> translationTable
        ) {
            ValidateAppliedCompaction(liveObjects, translationTable);
        }
    }

    internal PrimaryCommitArtifacts RunPrimaryCommitForBenchmark(DurableObject graphRoot, IRbfFile targetFile) {
        var result = RunPrimaryCommit(graphRoot, targetFile);
        if (result.IsFailure) { throw new InvalidOperationException($"Primary benchmark setup failed: {result.Error}"); }
        return result.Value;
    }

    internal List<DurableObject> WalkAndMarkForBenchmark(DurableObject graphRoot) {
        var result = WalkAndMark(graphRoot);
        if (result.IsFailure) { throw new InvalidOperationException($"WalkAndMark benchmark failed: {result.Error}"); }
        return result.Value!;
    }

    internal CommitTicket PersistCompactionFollowupForBenchmark(
        DurableObject graphRoot,
        IReadOnlyList<DurableObject> liveObjects,
        IRbfFile targetFile
    ) {
        var result = PersistCompactionFollowup(
            graphRoot,
            liveObjects,
            SymbolMirrorUpdatePlan.Unchanged(),
            targetFile
        );
        if (result.IsFailure) { throw new InvalidOperationException($"Follow-up persist benchmark failed: {result.Error}"); }
        return result.Value;
    }

    internal (List<PendingSave> PendingSaves, CommitTicket CommitTicket) PersistPrimaryCommitForBenchmark(
        DurableObject graphRoot,
        IReadOnlyList<DurableObject> liveObjects,
        IRbfFile targetFile
    ) {
        var result = PersistPrimarySnapshot(graphRoot, liveObjects, targetFile);
        if (result.IsFailure) { throw new InvalidOperationException($"Primary persist benchmark failed: {result.Error}"); }
        var (pendingSaves, commitTicket) = result.Value;
        return (pendingSaves, commitTicket);
    }

    internal void FinalizePrimaryCommitForBenchmark(DurableObject graphRoot, List<PendingSave> pendingSaves, CommitTicket newCommitTicket) {
        FinalizePrimaryCommit(graphRoot, pendingSaves, newCommitTicket);
    }
}
