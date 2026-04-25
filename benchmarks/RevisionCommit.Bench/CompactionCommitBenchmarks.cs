using BenchmarkDotNet.Attributes;
using Atelia.Rbf;
using Atelia.StateJournal;
using Atelia.StateJournal.Internal;

namespace Atelia.RevisionCommit.Bench;

[MemoryDiagnoser]
public class CommitBenchmarks : CommitBenchmarkBase {
    private BenchmarkScenario? _scenario;

    [Benchmark(Baseline = true)]
    public CommitTicket TypedLeafObjects_NoChildRefs() => RunEndToEndBenchmark(ScenarioKind.TypedLeafObjectsNoChildRefs);

    [Benchmark]
    public CommitTicket DurObjDict_SparseRefs() => RunEndToEndBenchmark(ScenarioKind.DurObjDictSparseRefs);

    [Benchmark]
    public CommitTicket MixedDict_SparseRefs() => RunEndToEndBenchmark(ScenarioKind.MixedDictSparseRefs);

    [Benchmark]
    public CommitTicket MixedDict_DenseRefs() => RunEndToEndBenchmark(ScenarioKind.MixedDictDenseRefs);

    [IterationSetup(Target = nameof(TypedLeafObjects_NoChildRefs))]
    public void SetupTypedLeafObjects() {
        PrepareScenario(ScenarioKind.TypedLeafObjectsNoChildRefs);
    }

    [IterationSetup(Target = nameof(DurObjDict_SparseRefs))]
    public void SetupDurObjDictSparseRefs() {
        PrepareScenario(ScenarioKind.DurObjDictSparseRefs);
    }

    [IterationSetup(Target = nameof(MixedDict_SparseRefs))]
    public void SetupMixedDictSparseRefs() {
        PrepareScenario(ScenarioKind.MixedDictSparseRefs);
    }

    [IterationSetup(Target = nameof(MixedDict_DenseRefs))]
    public void SetupMixedDictDenseRefs() {
        PrepareScenario(ScenarioKind.MixedDictDenseRefs);
    }

    [IterationCleanup]
    public void CleanupIteration() {
        _scenario?.Dispose();
        _scenario = null;
    }

    private CommitTicket RunEndToEndBenchmark(ScenarioKind scenarioKind) {
        var scenario = RequireScenario(scenarioKind);
        return BenchmarkUtil.RequireCommitResult(
            scenario.Revision.Commit(scenario.Root, scenario.File),
            $"{nameof(CommitBenchmarks)}.{scenarioKind}"
        );
    }

    private void PrepareScenario(ScenarioKind scenarioKind) {
        CleanupIteration();
        _scenario = ScenarioFactory.CreatePendingScenario(scenarioKind, TotalChildren, RemovedChildren, AddedChildren);
    }

    private BenchmarkScenario RequireScenario(ScenarioKind expectedScenarioKind) {
        return _scenario ?? throw new InvalidOperationException($"Scenario {expectedScenarioKind} has not been prepared.");
    }
}

[MemoryDiagnoser]
public class PrimaryCommitStageBenchmarks : CommitBenchmarkBase {
    private const int DefaultStageOperationCount = 256;
    private const int FinalizeStageOperationCount = 1024;

    [ParamsAllValues]
    public ScenarioKind ScenarioKind { get; set; }

    private ScenarioBatch? _scenarioBatch;
    private List<DurableObject>[]? _liveObjectsBatch;
    private List<PendingSave>[]? _pendingSavesBatch;
    private CommitTicket[]? _primaryCommitTickets;

    [Benchmark(Baseline = true, OperationsPerInvoke = DefaultStageOperationCount)]
    public ulong PrimaryCommit_Only() {
        var batch = RequireScenarioBatch();
        ulong checksum = 0;
        for (int i = 0; i < batch.Count; i++) {
            checksum += batch.Revisions[i].RunPrimaryCommitForBenchmark(batch.Roots[i], batch.File).CommitTicket.Ticket.Packed;
        }
        return checksum;
    }

    [Benchmark(OperationsPerInvoke = DefaultStageOperationCount)]
    public int WalkAndMark_Only() {
        var batch = RequireScenarioBatch();
        int totalCount = 0;
        for (int i = 0; i < batch.Count; i++) {
            totalCount += batch.Revisions[i].WalkAndMarkForBenchmark(batch.Roots[i]).Count;
        }
        return totalCount;
    }

    [Benchmark(OperationsPerInvoke = DefaultStageOperationCount)]
    public ulong Persist_Only() {
        var batch = RequireScenarioBatch();
        var liveObjectsBatch = RequireLiveObjectsBatch();
        ulong checksum = 0;
        for (int i = 0; i < batch.Count; i++) {
            checksum += batch.Revisions[i]
                .PersistPrimaryCommitForBenchmark(batch.Roots[i], liveObjectsBatch[i], batch.File)
                .CommitTicket
                .Ticket
                .Packed;
        }
        return checksum;
    }

    [Benchmark(OperationsPerInvoke = FinalizeStageOperationCount)]
    public ulong Finalize_Only() {
        var batch = RequireScenarioBatch();
        var liveObjectsBatch = RequireLiveObjectsBatch();
        var pendingSavesBatch = RequirePendingSavesBatch();
        var primaryCommitTickets = RequirePrimaryCommitTickets();
        ulong checksum = 0;
        for (int i = 0; i < batch.Count; i++) {
            batch.Revisions[i].FinalizePrimaryCommitForBenchmark(
                batch.Roots[i],
                liveObjectsBatch[i],
                pendingSavesBatch[i],
                primaryCommitTickets[i]
            );
            checksum += primaryCommitTickets[i].Ticket.Packed;
        }
        return checksum;
    }

    [IterationSetup(Target = nameof(PrimaryCommit_Only))]
    public void SetupPrimaryCommit() {
        PrepareScenario(DefaultStageOperationCount);
    }

    [IterationSetup(Target = nameof(WalkAndMark_Only))]
    public void SetupWalkAndMark() {
        PrepareScenario(DefaultStageOperationCount);
    }

    [IterationSetup(Target = nameof(Persist_Only))]
    public void SetupPersist() {
        PrepareScenario(DefaultStageOperationCount);
        var batch = RequireScenarioBatch();
        _liveObjectsBatch = new List<DurableObject>[batch.Count];
        for (int i = 0; i < batch.Count; i++) {
            _liveObjectsBatch[i] = batch.Revisions[i].WalkAndMarkForBenchmark(batch.Roots[i]);
        }
    }

    [IterationSetup(Target = nameof(Finalize_Only))]
    public void SetupFinalize() {
        PrepareScenario(FinalizeStageOperationCount);
        var batch = RequireScenarioBatch();
        _liveObjectsBatch = new List<DurableObject>[batch.Count];
        _pendingSavesBatch = new List<PendingSave>[batch.Count];
        _primaryCommitTickets = new CommitTicket[batch.Count];
        for (int i = 0; i < batch.Count; i++) {
            var liveObjects = batch.Revisions[i].WalkAndMarkForBenchmark(batch.Roots[i]);
            _liveObjectsBatch[i] = liveObjects;
            var (pendingSaves, commitTicket) = batch.Revisions[i].PersistPrimaryCommitForBenchmark(
                batch.Roots[i],
                liveObjects,
                batch.File
            );
            _pendingSavesBatch[i] = pendingSaves;
            _primaryCommitTickets[i] = commitTicket;
        }
    }

    [IterationCleanup]
    public void CleanupIteration() {
        _primaryCommitTickets = null;
        _pendingSavesBatch = null;
        _liveObjectsBatch = null;
        _scenarioBatch?.Dispose();
        _scenarioBatch = null;
    }

    private void PrepareScenario(int scenarioCount) {
        CleanupIteration();
        _scenarioBatch = ScenarioFactory.CreatePendingScenarioBatch(
            ScenarioKind,
            TotalChildren,
            RemovedChildren,
            AddedChildren,
            scenarioCount
        );
    }

    private ScenarioBatch RequireScenarioBatch() {
        return _scenarioBatch ?? throw new InvalidOperationException("Primary benchmark scenario batch has not been prepared.");
    }

    private List<DurableObject>[] RequireLiveObjectsBatch() {
        return _liveObjectsBatch ?? throw new InvalidOperationException("Primary benchmark live objects are not prepared.");
    }

    private List<PendingSave>[] RequirePendingSavesBatch() {
        return _pendingSavesBatch ?? throw new InvalidOperationException("Primary benchmark pending saves are not prepared.");
    }

    private CommitTicket[] RequirePrimaryCommitTickets() {
        return _primaryCommitTickets ?? throw new InvalidOperationException("Primary benchmark commit tickets are not prepared.");
    }
}

public abstract class CommitBenchmarkBase {
    [Params(384)]
    public int TotalChildren { get; set; }

    [Params(0, 8)]
    public int RemovedChildren { get; set; }

    [Params(8)]
    public int AddedChildren { get; set; }
}

public enum ScenarioKind {
    TypedLeafObjectsNoChildRefs = 0,
    DurObjDictSparseRefs = 1,
    MixedDictSparseRefs = 2,
    MixedDictDenseRefs = 3,
}

internal static class BenchmarkUtil {

    public static CommitTicket RequireCommitResult(AteliaResult<CommitOutcome> result, string benchmarkName) {
        if (result.IsFailure) { throw new InvalidOperationException($"{benchmarkName} failed: {result.Error}"); }
        return result.Value.HeadCommitTicket;
    }
}

internal sealed class BenchmarkScenario : IDisposable {
    public required string Path { get; init; }
    public required IRbfFile File { get; init; }
    public required Revision Revision { get; init; }
    public required DurableObject Root { get; init; }

    public void Dispose() {
        File.Dispose();
        try {
            if (System.IO.File.Exists(Path)) { System.IO.File.Delete(Path); }
        }
        catch {
        }
    }
}

internal sealed class ScenarioBatch : IDisposable {
    public required string Path { get; init; }
    public required IRbfFile File { get; init; }
    public required Revision[] Revisions { get; init; }
    public required DurableObject[] Roots { get; init; }

    public int Count => Revisions.Length;

    public void Dispose() {
        File.Dispose();
        try {
            if (System.IO.File.Exists(Path)) { System.IO.File.Delete(Path); }
        }
        catch {
        }
    }
}

internal static class ScenarioFactory {
    public static BenchmarkScenario CreatePendingScenario(
        ScenarioKind scenarioKind,
        int totalChildren,
        int removedChildren,
        int addedChildren
    ) {
        const uint segmentNumber = 1;
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sj-bench-{Guid.NewGuid():N}.rbf");
        var file = RbfFile.CreateNew(path);
        var revision = new Revision(segmentNumber);

        DurableObject root = scenarioKind switch {
            ScenarioKind.TypedLeafObjectsNoChildRefs => CreateTypedLeafScenario(revision, file, totalChildren, removedChildren, addedChildren),
            ScenarioKind.DurObjDictSparseRefs => CreateDurObjDictSparseScenario(revision, file, totalChildren, removedChildren, addedChildren),
            ScenarioKind.MixedDictSparseRefs => CreateMixedSparseScenario(revision, file, totalChildren, removedChildren, addedChildren),
            ScenarioKind.MixedDictDenseRefs => CreateMixedDenseScenario(revision, file, totalChildren, removedChildren, addedChildren),
            _ => throw new InvalidOperationException($"Unknown scenario kind {scenarioKind}.")
        };

        return new BenchmarkScenario {
            Path = path,
            File = file,
            Revision = revision,
            Root = root,
        };
    }

    public static ScenarioBatch CreatePendingScenarioBatch(
        ScenarioKind scenarioKind,
        int totalChildren,
        int removedChildren,
        int addedChildren,
        int scenarioCount
    ) {
        const uint segmentNumber = 1;
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sj-bench-{Guid.NewGuid():N}.rbf");
        var file = RbfFile.CreateNew(path);
        var revisions = new Revision[scenarioCount];
        var roots = new DurableObject[scenarioCount];

        try {
            for (int i = 0; i < scenarioCount; i++) {
                var revision = new Revision(segmentNumber);
                revisions[i] = revision;
                roots[i] = CreatePendingRoot(scenarioKind, revision, file, totalChildren, removedChildren, addedChildren);
            }

            return new ScenarioBatch {
                Path = path,
                File = file,
                Revisions = revisions,
                Roots = roots,
            };
        }
        catch {
            file.Dispose();
            try {
                if (System.IO.File.Exists(path)) { System.IO.File.Delete(path); }
            }
            catch {
            }
            throw;
        }
    }

    private static DurableObject CreatePendingRoot(
        ScenarioKind scenarioKind,
        Revision revision,
        IRbfFile file,
        int totalChildren,
        int removedChildren,
        int addedChildren
    ) {
        return scenarioKind switch {
            ScenarioKind.TypedLeafObjectsNoChildRefs => CreateTypedLeafScenario(revision, file, totalChildren, removedChildren, addedChildren),
            ScenarioKind.DurObjDictSparseRefs => CreateDurObjDictSparseScenario(revision, file, totalChildren, removedChildren, addedChildren),
            ScenarioKind.MixedDictSparseRefs => CreateMixedSparseScenario(revision, file, totalChildren, removedChildren, addedChildren),
            ScenarioKind.MixedDictDenseRefs => CreateMixedDenseScenario(revision, file, totalChildren, removedChildren, addedChildren),
            _ => throw new InvalidOperationException($"Unknown scenario kind {scenarioKind}.")
        };
    }

    private static DurableObject CreateTypedLeafScenario(Revision revision, IRbfFile file, int totalChildren, int removedChildren, int addedChildren) {
        var root = revision.CreateDict<int, DurableDict<int, int>>();
        for (int i = 0; i < totalChildren; i++) {
            var child = revision.CreateDict<int, int>();
            child.Upsert(i, i);
            root.Upsert(i, child);
        }

        _ = RequireResult(revision.Commit(root, file), nameof(CreateTypedLeafScenario));
        RemoveFirstEntries(root, removedChildren, key => key);
        AddTypedLeafEntries(root, revision, totalChildren, addedChildren);
        return root;
    }

    private static DurableObject CreateDurObjDictSparseScenario(Revision revision, IRbfFile file, int totalChildren, int removedChildren, int addedChildren) {
        var root = revision.CreateDict<int, DurableDict<int, DurableDict<int, int>>>();
        for (int i = 0; i < totalChildren; i++) {
            var container = revision.CreateDict<int, DurableDict<int, int>>();
            var leaf = revision.CreateDict<int, int>();
            leaf.Upsert(1, i);
            container.Upsert(1, leaf);
            root.Upsert(i, container);
        }

        _ = RequireResult(revision.Commit(root, file), nameof(CreateDurObjDictSparseScenario));
        RemoveFirstEntries(root, removedChildren, key => key);
        AddDurObjDictSparseEntries(root, revision, totalChildren, addedChildren);
        return root;
    }

    private static DurableObject CreateMixedSparseScenario(Revision revision, IRbfFile file, int totalChildren, int removedChildren, int addedChildren) {
        var root = revision.CreateDict<int>();
        for (int i = 0; i < totalChildren; i++) {
            var child = revision.CreateDict<int>();
            PopulateMixedScalars(child, i);
            if ((i & 3) == 0) {
                var leaf = revision.CreateDict<int, int>();
                leaf.Upsert(1, i);
                child.Upsert(10_000, leaf);
            }
            root.Upsert(i, child);
        }

        _ = RequireResult(revision.Commit(root, file), nameof(CreateMixedSparseScenario));
        RemoveFirstEntries(root, removedChildren, key => key);
        AddMixedSparseEntries(root, revision, totalChildren, addedChildren);
        return root;
    }

    private static DurableObject CreateMixedDenseScenario(Revision revision, IRbfFile file, int totalChildren, int removedChildren, int addedChildren) {
        var root = revision.CreateDict<int>();
        for (int i = 0; i < totalChildren; i++) {
            var child = revision.CreateDict<int>();
            PopulateMixedScalars(child, i);
            for (int refIndex = 0; refIndex < 4; refIndex++) {
                var leaf = revision.CreateDict<int, int>();
                leaf.Upsert(refIndex, i + refIndex);
                child.Upsert(20_000 + refIndex, leaf);
            }
            root.Upsert(i, child);
        }

        _ = RequireResult(revision.Commit(root, file), nameof(CreateMixedDenseScenario));
        RemoveFirstEntries(root, removedChildren, key => key);
        AddMixedDenseEntries(root, revision, totalChildren, addedChildren);
        return root;
    }

    private static void AddTypedLeafEntries(DurableDict<int, DurableDict<int, int>> root, Revision revision, int startKey, int count) {
        for (int i = 0; i < count; i++) {
            int key = startKey + i;
            var child = revision.CreateDict<int, int>();
            child.Upsert(key, key);
            root.Upsert(key, child);
        }
    }

    private static void AddDurObjDictSparseEntries(
        DurableDict<int, DurableDict<int, DurableDict<int, int>>> root,
        Revision revision,
        int startKey,
        int count
    ) {
        for (int i = 0; i < count; i++) {
            int key = startKey + i;
            var container = revision.CreateDict<int, DurableDict<int, int>>();
            var leaf = revision.CreateDict<int, int>();
            leaf.Upsert(1, key);
            container.Upsert(1, leaf);
            root.Upsert(key, container);
        }
    }

    private static void AddMixedSparseEntries(DurableDict<int> root, Revision revision, int startKey, int count) {
        for (int i = 0; i < count; i++) {
            int key = startKey + i;
            var child = revision.CreateDict<int>();
            PopulateMixedScalars(child, key);
            if ((key & 3) == 0) {
                var leaf = revision.CreateDict<int, int>();
                leaf.Upsert(1, key);
                child.Upsert(10_000, leaf);
            }
            root.Upsert(key, child);
        }
    }

    private static void AddMixedDenseEntries(DurableDict<int> root, Revision revision, int startKey, int count) {
        for (int i = 0; i < count; i++) {
            int key = startKey + i;
            var child = revision.CreateDict<int>();
            PopulateMixedScalars(child, key);
            for (int refIndex = 0; refIndex < 4; refIndex++) {
                var leaf = revision.CreateDict<int, int>();
                leaf.Upsert(refIndex, key + refIndex);
                child.Upsert(20_000 + refIndex, leaf);
            }
            root.Upsert(key, child);
        }
    }

    private static void PopulateMixedScalars(DurableDict<int> child, int seed) {
        for (int scalarIndex = 0; scalarIndex < 8; scalarIndex++) {
            child.Upsert(scalarIndex, seed + scalarIndex);
        }
    }

    private static void RemoveFirstEntries<TValue>(DurableDict<int, TValue> root, int count, Func<int, int> keySelector)
        where TValue : notnull {
        for (int i = 0; i < count; i++) {
            root.Remove(keySelector(i));
        }
    }

    private static void RemoveFirstEntries(DurableDict<int> root, int count, Func<int, int> keySelector) {
        for (int i = 0; i < count; i++) {
            root.Remove(keySelector(i));
        }
    }

    private static CommitTicket RequireResult(AteliaResult<CommitOutcome> result, string benchmarkName) {
        if (result.IsFailure) { throw new InvalidOperationException($"{benchmarkName} failed during scenario setup: {result.Error}"); }
        return result.Value.HeadCommitTicket;
    }
}
