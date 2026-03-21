using BenchmarkDotNet.Attributes;
using Atelia.Rbf;
using Atelia.StateJournal;
using Atelia.StateJournal.Internal;

namespace Atelia.RevisionCommit.Bench;

[MemoryDiagnoser]
public class CompactionCommitBenchmarks : CompactionBenchmarkBase {
    private CompactionBenchmarkScenario? _scenario;
    private IDisposable? _validationScope;

    [Benchmark(Baseline = true)]
    public CommitTicket TypedLeafObjects_NoChildRefs() => RunEndToEndBenchmark(CompactionScenarioKind.TypedLeafObjectsNoChildRefs);

    [Benchmark]
    public CommitTicket DurObjDict_SparseRefs() => RunEndToEndBenchmark(CompactionScenarioKind.DurObjDictSparseRefs);

    [Benchmark]
    public CommitTicket MixedDict_SparseRefs() => RunEndToEndBenchmark(CompactionScenarioKind.MixedDictSparseRefs);

    [Benchmark]
    public CommitTicket MixedDict_DenseRefs() => RunEndToEndBenchmark(CompactionScenarioKind.MixedDictDenseRefs);

    [IterationSetup(Target = nameof(TypedLeafObjects_NoChildRefs))]
    public void SetupTypedLeafObjects() {
        PrepareScenario(CompactionScenarioKind.TypedLeafObjectsNoChildRefs);
    }

    [IterationSetup(Target = nameof(DurObjDict_SparseRefs))]
    public void SetupDurObjDictSparseRefs() {
        PrepareScenario(CompactionScenarioKind.DurObjDictSparseRefs);
    }

    [IterationSetup(Target = nameof(MixedDict_SparseRefs))]
    public void SetupMixedDictSparseRefs() {
        PrepareScenario(CompactionScenarioKind.MixedDictSparseRefs);
    }

    [IterationSetup(Target = nameof(MixedDict_DenseRefs))]
    public void SetupMixedDictDenseRefs() {
        PrepareScenario(CompactionScenarioKind.MixedDictDenseRefs);
    }

    [IterationCleanup]
    public void CleanupIteration() {
        _validationScope?.Dispose();
        _validationScope = null;
        _scenario?.Dispose();
        _scenario = null;
    }

    private CommitTicket RunEndToEndBenchmark(CompactionScenarioKind scenarioKind) {
        var scenario = RequireScenario(scenarioKind);
        return CompactionBenchmarkUtil.RequireCompactionResult(
            scenario.Revision.Commit(scenario.Root, scenario.File),
            $"{nameof(CompactionCommitBenchmarks)}.{scenarioKind}"
        );
    }

    private void PrepareScenario(CompactionScenarioKind scenarioKind) {
        CleanupIteration();
        _validationScope = Revision.OverrideCompactionValidationModeScope(
            CompactionBenchmarkUtil.ParseValidationMode(ValidationModeName)
        );
        _scenario = CompactionScenarioFactory.CreatePendingCompactionScenario(scenarioKind, TotalChildren, RemovedChildren);
    }

    private CompactionBenchmarkScenario RequireScenario(CompactionScenarioKind expectedScenarioKind) {
        return _scenario ?? throw new InvalidOperationException($"Scenario {expectedScenarioKind} has not been prepared.");
    }
}

[MemoryDiagnoser]
public class CompactionStageBenchmarks : CompactionBenchmarkBase {
    [ParamsAllValues]
    public CompactionScenarioKind ScenarioKind { get; set; }

    private CompactionBenchmarkScenario? _scenario;
    private Revision.PrimaryCommitArtifacts _primaryArtifacts;
    private Revision.RevisionCompactionSession? _session;
    private Dictionary<uint, LocalId>? _translationTable;
    private IDisposable? _validationScope;

    [Benchmark(Baseline = true)]
    public int CompactWithUndo_Only() {
        return RequireSession().CompactPoolForBenchmark();
    }

    [Benchmark]
    public int ReferenceRewrite_Only() {
        return RequireTranslationTable().Count;
    }

    [Benchmark]
    public int Validate_Only() {
        var session = RequireSession();
        var translationTable = RequireTranslationTable();
        session.ValidateAppliedCompactionForBenchmark(_primaryArtifacts.LiveObjects, translationTable);
        return translationTable.Count;
    }

    [Benchmark]
    public CommitTicket FollowupPersist_Only() {
        var scenario = RequireScenario();
        return scenario.Revision.PersistCompactionFollowupForBenchmark(
            scenario.Root,
            _primaryArtifacts.LiveObjects,
            scenario.File
        );
    }

    [IterationSetup(Target = nameof(CompactWithUndo_Only))]
    public void SetupCompactWithUndo() {
        PrepareScenario();
    }

    [IterationSetup(Target = nameof(ReferenceRewrite_Only))]
    public void SetupReferenceRewrite() {
        PrepareScenario();
        EnsureCompactionStarted();
    }

    [IterationSetup(Target = nameof(Validate_Only))]
    public void SetupValidate() {
        PrepareScenario();
        EnsureRewriteApplied();
    }

    [IterationSetup(Target = nameof(FollowupPersist_Only))]
    public void SetupFollowupPersist() {
        PrepareScenario();
        EnsureRewriteApplied();
        RequireSession().ValidateAppliedCompactionForBenchmark(_primaryArtifacts.LiveObjects, RequireTranslationTable());
    }

    [IterationCleanup]
    public void CleanupIteration() {
        _translationTable = null;
        _session = null;
        _primaryArtifacts = default;
        _validationScope?.Dispose();
        _validationScope = null;
        _scenario?.Dispose();
        _scenario = null;
    }

    private void PrepareScenario() {
        CleanupIteration();
        _validationScope = Revision.OverrideCompactionValidationModeScope(
            CompactionBenchmarkUtil.ParseValidationMode(ValidationModeName)
        );
        _scenario = CompactionScenarioFactory.CreatePendingCompactionScenario(ScenarioKind, TotalChildren, RemovedChildren);
        _primaryArtifacts = _scenario.Revision.RunPrimaryCommitForBenchmark(_scenario.Root, _scenario.File);
        _session = Revision.RevisionCompactionSession.TryStartForBenchmark(_scenario.Revision, _primaryArtifacts.CommitTicket)
            ?? throw new InvalidOperationException($"Scenario {ScenarioKind} did not reach compaction start.");
    }

    private void EnsureCompactionStarted() {
        if (RequireSession().CompactPoolForBenchmark() == 0) { throw new InvalidOperationException($"Scenario {ScenarioKind} did not produce compaction moves."); }
    }

    private void EnsureRewriteApplied() {
        if (_translationTable is not null) { return; }
        EnsureCompactionStarted();
        _translationTable = RequireSession().ApplyMovedObjectsAndRewriteForBenchmark(_primaryArtifacts.LiveObjects);
    }

    private Dictionary<uint, LocalId> RequireTranslationTable() {
        EnsureRewriteApplied();
        return _translationTable!;
    }

    private CompactionBenchmarkScenario RequireScenario() {
        return _scenario ?? throw new InvalidOperationException("Scenario has not been prepared.");
    }

    private Revision.RevisionCompactionSession RequireSession() {
        return _session ?? throw new InvalidOperationException("Compaction session has not been prepared.");
    }
}

[MemoryDiagnoser]
public class PrimaryCommitStageBenchmarks : CompactionBenchmarkBase {
    [ParamsAllValues]
    public CompactionScenarioKind ScenarioKind { get; set; }

    private CompactionBenchmarkScenario? _scenario;
    private List<DurableObject>? _liveObjects;
    private List<PendingSave>? _pendingSaves;
    private CommitTicket _primaryCommitTicket;
    private IDisposable? _validationScope;

    [Benchmark(Baseline = true)]
    public CommitTicket PrimaryCommit_Only() {
        var scenario = RequireScenario();
        return scenario.Revision.RunPrimaryCommitForBenchmark(scenario.Root, scenario.File).CommitTicket;
    }

    [Benchmark]
    public int WalkAndMark_Only() {
        return RequireScenario().Revision.WalkAndMarkForBenchmark(RequireScenario().Root).Count;
    }

    [Benchmark]
    public CommitTicket Persist_Only() {
        var scenario = RequireScenario();
        var liveObjects = RequireLiveObjects();
        return scenario.Revision.PersistPrimaryCommitForBenchmark(scenario.Root, liveObjects, scenario.File).CommitTicket;
    }

    [Benchmark]
    public CommitTicket Finalize_Only() {
        var scenario = RequireScenario();
        var pendingSaves = RequirePendingSaves();
        scenario.Revision.FinalizePrimaryCommitForBenchmark(scenario.Root, pendingSaves, _primaryCommitTicket);
        return _primaryCommitTicket;
    }

    [IterationSetup(Target = nameof(PrimaryCommit_Only))]
    public void SetupPrimaryCommit() {
        PrepareScenario();
    }

    [IterationSetup(Target = nameof(WalkAndMark_Only))]
    public void SetupWalkAndMark() {
        PrepareScenario();
    }

    [IterationSetup(Target = nameof(Persist_Only))]
    public void SetupPersist() {
        PrepareScenario();
        _liveObjects = RequireScenario().Revision.WalkAndMarkForBenchmark(RequireScenario().Root);
    }

    [IterationSetup(Target = nameof(Finalize_Only))]
    public void SetupFinalize() {
        PrepareScenario();
        var scenario = RequireScenario();
        _liveObjects = scenario.Revision.WalkAndMarkForBenchmark(scenario.Root);
        (_pendingSaves, _primaryCommitTicket) = scenario.Revision.PersistPrimaryCommitForBenchmark(
            scenario.Root,
            _liveObjects,
            scenario.File
        );
    }

    [IterationCleanup]
    public void CleanupIteration() {
        _pendingSaves = null;
        _liveObjects = null;
        _primaryCommitTicket = default;
        _validationScope?.Dispose();
        _validationScope = null;
        _scenario?.Dispose();
        _scenario = null;
    }

    private void PrepareScenario() {
        CleanupIteration();
        _validationScope = Revision.OverrideCompactionValidationModeScope(
            CompactionBenchmarkUtil.ParseValidationMode(ValidationModeName)
        );
        _scenario = CompactionScenarioFactory.CreatePendingCompactionScenario(ScenarioKind, TotalChildren, RemovedChildren);
    }

    private CompactionBenchmarkScenario RequireScenario() {
        return _scenario ?? throw new InvalidOperationException("Primary benchmark scenario has not been prepared.");
    }

    private List<DurableObject> RequireLiveObjects() {
        return _liveObjects ?? throw new InvalidOperationException("Primary benchmark live objects are not prepared.");
    }

    private List<PendingSave> RequirePendingSaves() {
        return _pendingSaves ?? throw new InvalidOperationException("Primary benchmark pending saves are not prepared.");
    }
}

public abstract class CompactionBenchmarkBase {
    [Params(140)]
    public int TotalChildren { get; set; }

    [Params(40, 70)]
    public int RemovedChildren { get; set; }

    [Params("HotPath", "Strict")]
    public string ValidationModeName { get; set; } = "HotPath";
}

public enum CompactionScenarioKind {
    TypedLeafObjectsNoChildRefs = 0,
    DurObjDictSparseRefs = 1,
    MixedDictSparseRefs = 2,
    MixedDictDenseRefs = 3,
}

internal static class CompactionBenchmarkUtil {
    public static Revision.CompactionValidationMode ParseValidationMode(string validationModeName) {
        return validationModeName switch {
            "HotPath" => Revision.CompactionValidationMode.HotPath,
            "Strict" => Revision.CompactionValidationMode.Strict,
            _ => throw new InvalidOperationException($"Unknown validation mode '{validationModeName}'.")
        };
    }

    public static CommitTicket RequireCompactionResult(AteliaResult<CommitOutcome> result, string benchmarkName) {
        if (result.IsFailure) { throw new InvalidOperationException($"{benchmarkName} failed: {result.Error}"); }
        if (!result.Value.IsCompacted) { throw new InvalidOperationException($"{benchmarkName} did not trigger compaction."); }
        return result.Value.HeadCommitTicket;
    }
}

internal sealed class CompactionBenchmarkScenario : IDisposable {
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

internal static class CompactionScenarioFactory {
    public static CompactionBenchmarkScenario CreatePendingCompactionScenario(
        CompactionScenarioKind scenarioKind,
        int totalChildren,
        int removedChildren
    ) {
        const uint segmentNumber = 1;
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sj-bench-{Guid.NewGuid():N}.rbf");
        var file = RbfFile.CreateNew(path);
        var revision = new Revision(segmentNumber);

        DurableObject root = scenarioKind switch {
            CompactionScenarioKind.TypedLeafObjectsNoChildRefs => CreateTypedLeafScenario(revision, file, totalChildren, removedChildren),
            CompactionScenarioKind.DurObjDictSparseRefs => CreateDurObjDictSparseScenario(revision, file, totalChildren, removedChildren),
            CompactionScenarioKind.MixedDictSparseRefs => CreateMixedSparseScenario(revision, file, totalChildren, removedChildren),
            CompactionScenarioKind.MixedDictDenseRefs => CreateMixedDenseScenario(revision, file, totalChildren, removedChildren),
            _ => throw new InvalidOperationException($"Unknown scenario kind {scenarioKind}.")
        };

        return new CompactionBenchmarkScenario {
            Path = path,
            File = file,
            Revision = revision,
            Root = root,
        };
    }

    private static DurableObject CreateTypedLeafScenario(Revision revision, IRbfFile file, int totalChildren, int removedChildren) {
        var root = revision.CreateDict<int, DurableDict<int, int>>();
        for (int i = 0; i < totalChildren; i++) {
            var child = revision.CreateDict<int, int>();
            child.Upsert(i, i);
            root.Upsert(i, child);
        }

        _ = RequireCompactionResult(revision.Commit(root, file), nameof(CreateTypedLeafScenario));
        RemoveFirstEntries(root, removedChildren, key => key);
        return root;
    }

    private static DurableObject CreateDurObjDictSparseScenario(Revision revision, IRbfFile file, int totalChildren, int removedChildren) {
        var root = revision.CreateDict<int, DurableDict<int, DurableDict<int, int>>>();
        for (int i = 0; i < totalChildren; i++) {
            var container = revision.CreateDict<int, DurableDict<int, int>>();
            var leaf = revision.CreateDict<int, int>();
            leaf.Upsert(1, i);
            container.Upsert(1, leaf);
            root.Upsert(i, container);
        }

        _ = RequireCompactionResult(revision.Commit(root, file), nameof(CreateDurObjDictSparseScenario));
        RemoveFirstEntries(root, removedChildren, key => key);
        return root;
    }

    private static DurableObject CreateMixedSparseScenario(Revision revision, IRbfFile file, int totalChildren, int removedChildren) {
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

        _ = RequireCompactionResult(revision.Commit(root, file), nameof(CreateMixedSparseScenario));
        RemoveFirstEntries(root, removedChildren, key => key);
        return root;
    }

    private static DurableObject CreateMixedDenseScenario(Revision revision, IRbfFile file, int totalChildren, int removedChildren) {
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

        _ = RequireCompactionResult(revision.Commit(root, file), nameof(CreateMixedDenseScenario));
        RemoveFirstEntries(root, removedChildren, key => key);
        return root;
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

    private static CommitTicket RequireCompactionResult(AteliaResult<CommitOutcome> result, string benchmarkName) {
        if (result.IsFailure) { throw new InvalidOperationException($"{benchmarkName} failed during scenario setup: {result.Error}"); }
        return result.Value.HeadCommitTicket;
    }
}
