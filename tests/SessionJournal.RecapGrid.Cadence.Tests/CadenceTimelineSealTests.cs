using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Cadence;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Cadence.Tests;

public sealed class CadenceTimelineSealTests : IDisposable {
    private const string EstimatorId = "test.history-load.one-per-unit.v1";
    private readonly List<string> _paths = [];
    private readonly OnePerUnitEstimator _estimator = new();

    [Theory]
    [InlineData(3, false)]
    [InlineData(2, true)]
    [InlineData(1, true)]
    public void OnlineSealEnforcesRecentReserveThresholdAndOvershoot(
        long minimumRecent,
        bool selected
    ) {
        using Fixture fixture = CreateFixture(
            "threshold",
            turns: 2,
            target: 2,
            minimumRecent);
        using RecapGridCadenceTimelineSealOperation seal = OpenSeal(fixture);

        HistoryTimelinePlanResult result = Plan(fixture, seal);

        if (!selected) {
            HistoryTimelinePlanResult.RecentReserveNotReached shortfall =
                Assert.IsType<HistoryTimelinePlanResult
                    .RecentReserveNotReached>(result);
            Assert.Equal(2, shortfall.Shortfall.Retained.Value);
            Assert.Equal(minimumRecent, shortfall.Shortfall.Required.Value);
            Assert.Null(ReadHead(fixture).HeadRowId);
            return;
        }
        HistoryRowCommitCandidate candidate = Assert.IsType<
            HistoryTimelinePlanResult.Selected>(result).Candidate;
        Assert.IsType<HistoryTimelineCommitResult.Committed>(
            seal.CommitRow(candidate));
    }

    [Fact]
    public void OneSealUsesOneSnapshotAndForeignOrDisposedSealCannotCommit() {
        using Fixture fixture = CreateFixture(
            "snapshot",
            turns: 2,
            target: 2,
            minimumRecent: 2);
        RecapGridCadenceTimelineSealOperation first = OpenSeal(fixture);
        HistoryRowCommitCandidate candidate = Assert.IsType<
            HistoryTimelinePlanResult.Selected>(Plan(fixture, first)).Candidate;

        RecapGridCadenceSnapshot prior = Assert.IsType<
            RecapGridCadenceReadResult.Available>(
                fixture.Cadence.Reader.ReadSnapshot()).Snapshot;
        Assert.IsType<RecapGridCadenceCompareExchangeResult.Updated>(
            fixture.Cadence.Coordinator.CompareExchangePolicy(
                prior.Head,
                Policy(target: 2, minimumRecent: 3)));

        using RecapGridCadenceTimelineSealOperation second = OpenSeal(fixture);
        Assert.IsType<HistoryTimelinePlanResult.RecentReserveNotReached>(
            Plan(fixture, second));
        HistoryTimelineCommitResult.Invalid foreign = Assert.IsType<
            HistoryTimelineCommitResult.Invalid>(second.CommitRow(candidate));
        Assert.Equal("CadenceSealAuthorityMismatch", foreign.Code);

        first.Dispose();
        HistoryTimelineCommitResult.Invalid disposed = Assert.IsType<
            HistoryTimelineCommitResult.Invalid>(first.CommitRow(candidate));
        Assert.Equal("CadenceSealOperationDisposed", disposed.Code);
        Assert.Null(ReadHead(fixture).HeadRowId);
    }

    [Fact]
    public void SealRejectsPartitionPolicyAndCanonicalRepositoryMismatch() {
        using Fixture mismatch = CreateFixture(
            "policy-mismatch",
            turns: 2,
            target: 2,
            minimumRecent: 2,
            cadenceTargetOverride: 3);
        RecapGridCadenceTimelineSealOpenResult.Invalid policy = Assert.IsType<
            RecapGridCadenceTimelineSealOpenResult.Invalid>(
                mismatch.Cadence.BeginTimelineSeal(mismatch.Timeline));
        Assert.Equal("CadenceTimelinePolicyMismatch", policy.Code);

        string clonePath = NewPath("clone");
        mismatch.Dispose();
        CopyDirectory(mismatch.Path, clonePath);
        using SessionJournalEngine originalOwner = SessionJournalEngine.Open(
            mismatch.Path);
        using RecapGridCadenceHandle originalCadence = Assert.IsType<
            RecapGridCadenceOpenResult.Opened>(
                RecapGridCadenceFactory.OpenMutable(originalOwner)).Handle;
        using SessionJournalEngine cloneOwner = SessionJournalEngine.Open(
            clonePath);
        using HistoryTimelineHandle cloneTimeline = Assert.IsType<
            HistoryTimelineOpenResult.Opened>(HistoryTimelineFactory.Open(
                cloneOwner.ReadView,
                _estimator)).Handle;
        RecapGridCadenceTimelineSealOpenResult.Invalid repository =
            Assert.IsType<RecapGridCadenceTimelineSealOpenResult.Invalid>(
                originalCadence.BeginTimelineSeal(cloneTimeline));
        Assert.Equal("CadenceTimelineRepositoryMismatch", repository.Code);
    }

    [Fact]
    public async Task CloneWithSameIdsCannotPlanOrCommitAcrossRepository() {
        Fixture source = CreateFixture(
            "repository-source",
            turns: 2,
            target: 2,
            minimumRecent: 2);
        string sourcePath = source.Path;
        source.Dispose();
        string clonePath = NewPath("repository-clone");
        CopyDirectory(sourcePath, clonePath);

        using SessionJournalEngine sourceOwner = SessionJournalEngine.Open(
            sourcePath);
        using SessionJournalEngine cloneOwner = SessionJournalEngine.Open(
            clonePath);
        cloneOwner.UseRuntime(new SessionRuntime(
            new EchoCompletionClient(),
            CompletionTarget: new SessionCompletionTargetIdentity(
                "cadence-clone", "test", "v1", "adapter-v1"),
            ContextCandidateSource: new EmptySource(),
            ContextLifecycle: new RawLifecycle()));
        _ = await cloneOwner.SendAsync(
            cloneOwner.ReadCurrentHead()!.Value,
            "clone-divergence");

        using HistoryTimelineHandle sourceTimeline = Assert.IsType<
            HistoryTimelineOpenResult.Opened>(HistoryTimelineFactory.Open(
                sourceOwner.ReadView,
                _estimator)).Handle;
        using HistoryTimelineHandle cloneTimeline = Assert.IsType<
            HistoryTimelineOpenResult.Opened>(HistoryTimelineFactory.Open(
                cloneOwner.ReadView,
                _estimator)).Handle;
        using RecapGridCadenceHandle sourceCadence = Assert.IsType<
            RecapGridCadenceOpenResult.Opened>(
                RecapGridCadenceFactory.OpenMutable(sourceOwner)).Handle;
        using RecapGridCadenceHandle cloneCadence = Assert.IsType<
            RecapGridCadenceOpenResult.Opened>(
                RecapGridCadenceFactory.OpenMutable(cloneOwner)).Handle;
        using RecapGridCadenceTimelineSealOperation sourceSeal =
            Assert.IsType<RecapGridCadenceTimelineSealOpenResult.Opened>(
                sourceCadence.BeginTimelineSeal(sourceTimeline)).Operation;
        using RecapGridCadenceTimelineSealOperation cloneSeal =
            Assert.IsType<RecapGridCadenceTimelineSealOpenResult.Opened>(
                cloneCadence.BeginTimelineSeal(cloneTimeline)).Operation;

        TimelineHeadRef sourceHead = Assert.IsType<
            HistoryTimelineSnapshotResult.Available>(
                sourceTimeline.Reader.ReadSnapshot()).Head;
        TimelineHeadRef cloneHead = Assert.IsType<
            HistoryTimelineSnapshotResult.Available>(
                cloneTimeline.Reader.ReadSnapshot()).Head;
        Assert.Equal(sourceHead, cloneHead);
        OnlineSelectedRawCapture cloneCapture = Assert.IsType<
            OnlineSelectedRawCaptureResult.Captured>(
                cloneTimeline.Coordinator.CaptureOnline(
                    cloneHead,
                    cloneOwner.ReadView)).Capture;

        HistoryTimelinePlanResult.Invalid plan = Assert.IsType<
            HistoryTimelinePlanResult.Invalid>(
                sourceSeal.PlanNextRow(sourceHead, cloneCapture));
        Assert.Equal("RawRepositoryMismatch", plan.Code);
        HistoryRowCommitCandidate cloneCandidate = Assert.IsType<
            HistoryTimelinePlanResult.Selected>(
                cloneSeal.PlanNextRow(cloneHead, cloneCapture)).Candidate;
        HistoryTimelineCommitResult.Invalid commit = Assert.IsType<
            HistoryTimelineCommitResult.Invalid>(
                sourceSeal.CommitRow(cloneCandidate));
        Assert.Equal("CadenceSealAuthorityMismatch", commit.Code);
        Assert.Equal(sourceHead, Assert.IsType<
            HistoryTimelineSnapshotResult.Available>(
                sourceTimeline.Reader.ReadSnapshot()).Head);
    }

    private Fixture CreateFixture(
        string suffix,
        int turns,
        long target,
        long minimumRecent,
        long? cadenceTargetOverride = null
    ) {
        string path = NewPath(suffix);
        using (SessionJournalLegacyImportWriter import =
               SessionJournalLegacyImportWriter.Create(
                   path,
                   new SessionCreateOptions("model", "system", suffix))) {
            for (int index = 0; index < turns; index++) {
                _ = import.AppendObservation($"observation-{index}");
                _ = import.AppendImportedAgentAction(
                    new ActionMessage([
                        new ActionBlock.Text($"answer-{index}")
                    ]),
                    new CompletionDescriptor("import", "v1", "model"));
            }
        }
        SessionJournalEngine journal = SessionJournalEngine.Open(path);
        Assert.IsType<HistoryTimelineCreateResult.Created>(
            HistoryTimelineFactory.Create(
                journal.ReadView,
                new HistoryTimelineInitialPolicySpec(
                    HistoryPartitionAlgorithms
                        .FirstReplaySafeBoundaryAtTargetV1,
                    EstimatorId,
                    new HistoryLoadUnit(target),
                    maxRawEvents: 64,
                    maxRenderedBytes: 1024 * 1024),
                _estimator));
        Assert.IsType<RecapGridCadenceCreateResult.Created>(
            RecapGridCadenceFactory.Create(
                journal,
                Policy(cadenceTargetOverride ?? target, minimumRecent)));
        HistoryTimelineHandle timeline = Assert.IsType<
            HistoryTimelineOpenResult.Opened>(HistoryTimelineFactory.Open(
                journal.ReadView,
                _estimator)).Handle;
        RecapGridCadenceHandle cadence = Assert.IsType<
            RecapGridCadenceOpenResult.Opened>(
                RecapGridCadenceFactory.OpenMutable(journal)).Handle;
        return new Fixture(path, journal, timeline, cadence);
    }

    private RecapGridCadenceTimelineSealOperation OpenSeal(Fixture fixture)
        => Assert.IsType<RecapGridCadenceTimelineSealOpenResult.Opened>(
            fixture.Cadence.BeginTimelineSeal(fixture.Timeline)).Operation;

    private static HistoryTimelinePlanResult Plan(
        Fixture fixture,
        RecapGridCadenceTimelineSealOperation seal
    ) {
        TimelineHeadRef head = ReadHead(fixture);
        OnlineSelectedRawCapture capture = Assert.IsType<
            OnlineSelectedRawCaptureResult.Captured>(
                fixture.Timeline.Coordinator.CaptureOnline(
                    head,
                    fixture.Journal.ReadView)).Capture;
        return seal.PlanNextRow(head, capture);
    }

    private static TimelineHeadRef ReadHead(Fixture fixture)
        => Assert.IsType<HistoryTimelineSnapshotResult.Available>(
            fixture.Timeline.Reader.ReadSnapshot()).Head;

    private static RecapGridCadencePolicySpec Policy(
        long target,
        long minimumRecent
    ) => new(
        minimumRecent,
        HistoryPartitionAlgorithms.FirstReplaySafeBoundaryAtTargetV1,
        EstimatorId,
        target,
        maxRawEvents: 64,
        maxRenderedBytes: 1024 * 1024);

    private string NewPath(string suffix) {
        string path = Path.Combine(
            Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath(),
            "atelia-cadence-a1-tests",
            suffix + "-" + Guid.NewGuid().ToString("N"));
        _paths.Add(path);
        return path;
    }

    private static void CopyDirectory(string source, string destination) {
        Directory.CreateDirectory(destination);
        if (!OperatingSystem.IsWindows()) {
            File.SetUnixFileMode(
                destination,
                File.GetUnixFileMode(source));
        }
        foreach (string directory in Directory.EnumerateDirectories(
                     source, "*", SearchOption.AllDirectories)) {
            string copiedDirectory = Path.Combine(
                destination,
                Path.GetRelativePath(source, directory));
            Directory.CreateDirectory(copiedDirectory);
            if (!OperatingSystem.IsWindows()) {
                File.SetUnixFileMode(
                    copiedDirectory,
                    File.GetUnixFileMode(directory));
            }
        }
        foreach (string file in Directory.EnumerateFiles(
                     source, "*", SearchOption.AllDirectories)) {
            File.Copy(file, Path.Combine(
                destination,
                Path.GetRelativePath(source, file)));
            if (!OperatingSystem.IsWindows()) {
                File.SetUnixFileMode(
                    Path.Combine(destination,
                        Path.GetRelativePath(source, file)),
                    File.GetUnixFileMode(file));
            }
        }
    }

    public void Dispose() {
        foreach (string path in _paths) {
            if (Directory.Exists(path)) {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    private sealed class OnePerUnitEstimator : IHistoryUnitLoadEstimator {
        public string Id => EstimatorId;

        public HistoryUnitLoadMeasurement Measure(
            SessionHistoryPlanningUnit unit,
            int maxRenderedUtf8Bytes
        ) => new(new HistoryLoadUnit(1), 1);
    }

    private sealed class EchoCompletionClient : ICompletionClient {
        public string Name => "cadence-clone";
        public string ApiSpecId => "v1";
        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(new CompletionResult(
            new ActionMessage([new ActionBlock.Text("clone-answer")]),
            new CompletionDescriptor(Name, ApiSpecId, request.ModelId)));
    }

    private sealed class EmptySource : ICoherentContextCandidateSource {
        public ValueTask<SessionContextCandidateSelection> SelectAsync(
            SessionContextSelectionRequest request,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(new SessionContextCandidateSelection(
            SessionContextCandidateSelectionStatus.EmptyLineage,
            null));

        public ValueTask<SessionContextCandidateMaterializationResult>
            MaterializeAsync(
                SessionContextCandidateDescriptor descriptor,
                CancellationToken cancellationToken
            ) => throw new InvalidOperationException();
    }

    private sealed class RawLifecycle
        : ISessionContextLifecycleCoordinator {
        public ValueTask<SessionContextLifecycleResult> PrepareAsync(
            SessionJournalReadView readView,
            SessionContextLifecycleRequest request,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(
            SessionContextLifecycleResult.RawHistoryAuthorized);
    }

    private sealed class Fixture : IDisposable {
        internal Fixture(
            string path,
            SessionJournalEngine journal,
            HistoryTimelineHandle timeline,
            RecapGridCadenceHandle cadence
        ) {
            Path = path;
            Journal = journal;
            Timeline = timeline;
            Cadence = cadence;
        }

        internal string Path { get; }
        internal SessionJournalEngine Journal { get; }
        internal HistoryTimelineHandle Timeline { get; }
        internal RecapGridCadenceHandle Cadence { get; }

        public void Dispose() {
            Cadence.Dispose();
            Timeline.Dispose();
            Journal.Dispose();
        }
    }
}
