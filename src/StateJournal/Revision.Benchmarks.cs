using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Diagnostics;
using Atelia.Rbf;
using Atelia.StateJournal.Internal;
using Atelia.StateJournal.Pools;

namespace Atelia.StateJournal;

partial class Revision {
    internal sealed partial class RevisionCompactionSession {
        /// <summary>
        /// benchmark/诊断专用：只完成“应该开始 compaction 吗”的入口判定，不执行 apply。
        /// </summary>
        internal static RevisionCompactionSession? TryStartForBenchmark(Revision revision, CommitId primaryCommitId) {
            return revision.ShouldCompact()
                ? new RevisionCompactionSession(revision, primaryCommitId)
                : null;
        }

        internal int CompactPoolForBenchmark() {
            return TryCompactPool() ? _undoToken.Records.Count : 0;
        }

        internal Dictionary<uint, LocalId> ApplyMovedObjectsAndRewriteForBenchmark(IReadOnlyList<DurableObject> liveObjects) {
            return ApplyMovedObjectsAndRewrite(liveObjects);
        }

        internal void ValidateAppliedCompactionForBenchmark(
            IReadOnlyList<DurableObject> liveObjects,
            IReadOnlyDictionary<uint, LocalId> translationTable
        ) {
            ValidateAppliedCompaction(liveObjects, translationTable);
        }
    }

    internal PrimaryCommitArtifacts RunPrimaryCommitForBenchmark(DurableObject graphRoot) {
        var result = RunPrimaryCommit(graphRoot);
        if (result.IsFailure) { throw new InvalidOperationException($"Primary benchmark setup failed: {result.Error}"); }
        return result.Value;
    }

    internal List<DurableObject> WalkAndMarkForBenchmark(DurableObject graphRoot) {
        var result = WalkAndMark(graphRoot);
        if (result.IsFailure) { throw new InvalidOperationException($"WalkAndMark benchmark failed: {result.Error}"); }
        return result.Value!;
    }

    internal CommitId PersistCompactionFollowupForBenchmark(DurableObject graphRoot, IReadOnlyList<DurableObject> liveObjects) {
        var result = PersistCompactionFollowup(graphRoot, liveObjects);
        if (result.IsFailure) { throw new InvalidOperationException($"Follow-up persist benchmark failed: {result.Error}"); }
        return result.Value;
    }

    internal (List<PendingSave> PendingSaves, CommitId CommitId) PersistPrimaryCommitForBenchmark(
        DurableObject graphRoot,
        IReadOnlyList<DurableObject> liveObjects
    ) {
        var result = PersistCurrentSnapshot(graphRoot, liveObjects, removeUnreachableObjectMapKeys: true, FrameSource.PrimaryCommit);
        if (result.IsFailure) { throw new InvalidOperationException($"Primary persist benchmark failed: {result.Error}"); }
        var (pendingSaves, commitId) = result.Value;
        return (pendingSaves, commitId);
    }

    internal void FinalizePrimaryCommitForBenchmark(DurableObject graphRoot, List<PendingSave> pendingSaves, CommitId newCommitId) {
        FinalizePrimaryCommit(graphRoot, pendingSaves, newCommitId);
    }
}
