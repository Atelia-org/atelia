using Atelia.StateJournal.Internal;
using Atelia.Rbf;

namespace Atelia.StateJournal;

partial class Revision {
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

    internal void FinalizePrimaryCommitForBenchmark(
        DurableObject graphRoot,
        IReadOnlyList<DurableObject> liveObjects,
        List<PendingSave> pendingSaves,
        CommitTicket newCommitTicket
    ) {
        FinalizePrimaryCommit(graphRoot, liveObjects, pendingSaves, newCommitTicket);
    }
}
