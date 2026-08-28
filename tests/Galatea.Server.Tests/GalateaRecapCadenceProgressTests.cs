using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Cadence;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaRecapCadenceProgressTests : IDisposable {
    private const string EstimatorId =
        "test.galatea.recap-cadence-progress.one-per-unit.v1";

    private readonly string _root = Path.Combine(
        Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath(),
        "atelia-galatea-recap-cadence-progress-tests",
        Guid.NewGuid().ToString("N"));
    private readonly OnePerUnitEstimator _estimator = new();

    [Fact]
    public void EmptyTimelineIsExactBelowTargetAndInspectionIsReadOnly() {
        using SessionJournalEngine engine = CreateFixture(
            "empty",
            target: 2,
            minimumRecent: 1,
            turns: 0);
        EventAddress capturedHead = Assert.IsType<EventAddress>(
            engine.ReadCurrentHead());
        EventJournalPhysicalAppendFrontier physicalBefore =
            engine.ReadView.ReadPhysicalAppendFrontier();
        TimelineHeadRef timelineBefore = ReadTimelineHead(engine);
        RecapGridCadenceSnapshot cadenceBefore = ReadCadence(engine);

        RecapCadenceProgressSnapshotDto result =
            GalateaRecapCadenceProgress.Inspect(
                engine.ReadView,
                capturedHead,
                _estimator,
                CancellationToken.None);

        Assert.Equal("exact", result.Freshness);
        Assert.Equal("below-target", result.State);
        Assert.Equal(Address(capturedHead), result.ObservedRawHead);
        Assert.Equal(Address(capturedHead), result.CadenceBaseline);
        Assert.Equal(0, result.RecentHistoryPlanningUnitCount);
        Assert.Equal("0", result.RecentHistoryLoad);
        Assert.Equal("2", result.RecapIntervalHistoryLoad);
        Assert.Equal("1", result.MinimumRecentHistoryLoad);
        Assert.Equal("3", result.BuildThresholdHistoryLoad);
        Assert.Equal("3", result.RemainingHistoryLoad);
        Assert.Equal(EstimatorId, result.HistoryLoadEstimatorId);
        Assert.Null(result.Code);

        Assert.Equal(capturedHead, engine.ReadCurrentHead());
        Assert.Equal(
            physicalBefore,
            engine.ReadView.ReadPhysicalAppendFrontier());
        Assert.Equal(timelineBefore, ReadTimelineHead(engine));
        Assert.Equal(cadenceBefore.Head, ReadCadence(engine).Head);
    }

    [Fact]
    public void ReplaySafeTargetReachedButRecentReserveIsShort() {
        using SessionJournalEngine engine = CreateFixture(
            "reserve-short",
            target: 1,
            minimumRecent: 2,
            turns: 1);
        EventAddress capturedHead = Assert.IsType<EventAddress>(
            engine.ReadCurrentHead());

        RecapCadenceProgressSnapshotDto result =
            GalateaRecapCadenceProgress.Inspect(
                engine.ReadView,
                capturedHead,
                _estimator,
                CancellationToken.None);

        Assert.Equal("exact", result.Freshness);
        Assert.Equal("awaiting-recent-reserve", result.State);
        Assert.Equal("2", result.RecentHistoryLoad);
        Assert.Equal(2, result.RecentHistoryPlanningUnitCount);
        Assert.Equal("1", result.RecapIntervalHistoryLoad);
        Assert.Equal("2", result.MinimumRecentHistoryLoad);
        Assert.Equal("3", result.BuildThresholdHistoryLoad);
        Assert.Equal("1", result.RemainingHistoryLoad);
        Assert.Equal("recent-reserve-short", result.Code);
        Assert.Equal("retained=1", result.Detail);
    }

    [Fact]
    public void ReplaySafeTargetAndRecentReserveMakeCadenceReady() {
        using SessionJournalEngine engine = CreateFixture(
            "ready",
            target: 1,
            minimumRecent: 1,
            turns: 1);
        EventAddress capturedHead = Assert.IsType<EventAddress>(
            engine.ReadCurrentHead());

        RecapCadenceProgressSnapshotDto result =
            GalateaRecapCadenceProgress.Inspect(
                engine.ReadView,
                capturedHead,
                _estimator,
                CancellationToken.None);

        Assert.Equal("exact", result.Freshness);
        Assert.Equal("cadence-ready", result.State);
        Assert.Equal("2", result.RecentHistoryLoad);
        Assert.Equal(2, result.RecentHistoryPlanningUnitCount);
        Assert.Equal("2", result.BuildThresholdHistoryLoad);
        Assert.Equal("0", result.RemainingHistoryLoad);
        Assert.Null(result.Code);
    }

    [Fact]
    public void TimelineHeadRowEndIsTheNextCadenceBaseline() {
        using SessionJournalEngine engine = CreateFixture(
            "row-baseline",
            target: 1,
            minimumRecent: 1,
            turns: 2);
        HistorySegmentDescriptor committed = CommitOneTimelineRow(engine);
        EventAddress capturedHead = Assert.IsType<EventAddress>(
            engine.ReadCurrentHead());

        RecapCadenceProgressSnapshotDto result =
            GalateaRecapCadenceProgress.Inspect(
                engine.ReadView,
                capturedHead,
                _estimator,
                CancellationToken.None);

        Assert.Equal("exact", result.Freshness);
        Assert.Equal(Address(committed.EndInclusive), result.CadenceBaseline);
        Assert.Equal(3, result.RecentHistoryPlanningUnitCount);
        Assert.Equal("3", result.RecentHistoryLoad);
        Assert.Equal("cadence-ready", result.State);
    }

    [Fact]
    public void RawHeadChangeAtFinalFenceReturnsTypedStale() {
        using SessionJournalEngine engine = CreateFixture(
            "raw-drift",
            target: 2,
            minimumRecent: 1,
            turns: 0);
        EventAddress capturedHead = Assert.IsType<EventAddress>(
            engine.ReadCurrentHead());
        GalateaRecapCadenceProgress.BeforeFinalAuthorityFenceForTest.Value =
            () => engine.AppendObservation("drift during progress inspection");
        try {
            RecapCadenceProgressSnapshotDto result =
                GalateaRecapCadenceProgress.Inspect(
                    engine.ReadView,
                    capturedHead,
                    _estimator,
                    CancellationToken.None);

            Assert.Equal("stale", result.Freshness);
            Assert.Equal("stale", result.State);
            Assert.Equal(Address(capturedHead), result.ObservedRawHead);
            Assert.Equal("raw-head-changed", result.Code);
        }
        finally {
            GalateaRecapCadenceProgress
                .BeforeFinalAuthorityFenceForTest.Value = null;
        }
    }

    [Fact]
    public void PolicyRawBoundWinsEvenWhenAnEarlyBoundaryCouldBeSelected() {
        using SessionJournalEngine engine = CreateFixture(
            "policy-raw-bound",
            target: 1,
            minimumRecent: 1,
            turns: 1,
            maxRawEvents: 2);
        HistorySegmentDescriptor committed = CommitOneTimelineRow(engine);
        _ = engine.AppendObservation("tail-observation");
        _ = engine.AppendImportedAgentAction(
            new ActionMessage([
                new ActionBlock.Text("tail-answer")
            ]),
            new CompletionDescriptor("import", "v1", "model"));
        EventAddress capturedHead = Assert.IsType<EventAddress>(
            engine.ReadCurrentHead());

        RecapCadenceProgressSnapshotDto result =
            GalateaRecapCadenceProgress.Inspect(
                engine.ReadView,
                capturedHead,
                _estimator,
                CancellationToken.None);

        Assert.Equal("exact", result.Freshness);
        Assert.Equal("limited", result.State);
        Assert.Equal(Address(committed.EndInclusive), result.CadenceBaseline);
        Assert.Equal("recent-history-beyond-prefix", result.Code);
        Assert.Null(result.RecentHistoryLoad);
        Assert.Null(result.BuildThresholdHistoryLoad);
        Assert.Null(result.RemainingHistoryLoad);
    }

    [Fact]
    public void DisposedReadViewAtFinalFenceDoesNotLeakOrClaimExact() {
        using SessionJournalEngine engine = CreateFixture(
            "disposed-final-fence",
            target: 2,
            minimumRecent: 1,
            turns: 0);
        EventAddress capturedHead = Assert.IsType<EventAddress>(
            engine.ReadCurrentHead());
        GalateaRecapCadenceProgress.BeforeFinalAuthorityFenceForTest.Value =
            engine.Dispose;
        try {
            RecapCadenceProgressSnapshotDto? result = null;
            Exception? exception = Record.Exception(() =>
                result = GalateaRecapCadenceProgress.Inspect(
                    engine.ReadView,
                    capturedHead,
                    _estimator,
                    CancellationToken.None));

            Assert.Null(exception);
            Assert.NotNull(result);
            Assert.Equal("stale", result.Freshness);
            Assert.Equal("unavailable", result.State);
            Assert.Equal("raw-head-observation-failed", result.Code);
            Assert.Contains("ObjectDisposedException", result.Detail);
        }
        finally {
            GalateaRecapCadenceProgress
                .BeforeFinalAuthorityFenceForTest.Value = null;
        }
    }

    [Fact]
    public void ReplaySafeOvershootRaisesTheEffectiveBuildThreshold() {
        var estimator = new FixedPerUnitEstimator(
            "test.galatea.recap-cadence-progress.two-per-unit.v1",
            load: 2);
        using SessionJournalEngine engine = CreateFixture(
            "overshoot",
            target: 3,
            minimumRecent: 1,
            turns: 2,
            estimator: estimator);
        EventAddress capturedHead = Assert.IsType<EventAddress>(
            engine.ReadCurrentHead());

        RecapCadenceProgressSnapshotDto result =
            GalateaRecapCadenceProgress.Inspect(
                engine.ReadView,
                capturedHead,
                estimator,
                CancellationToken.None);

        Assert.Equal("cadence-ready", result.State);
        Assert.Equal("8", result.RecentHistoryLoad);
        Assert.Equal("3", result.RecapIntervalHistoryLoad);
        Assert.Equal("1", result.MinimumRecentHistoryLoad);
        Assert.Equal("5", result.BuildThresholdHistoryLoad);
        Assert.Equal("0", result.RemainingHistoryLoad);
    }

    private SessionJournalEngine CreateFixture(
        string suffix,
        long target,
        long minimumRecent,
        int turns,
        int maxRawEvents = 64,
        IHistoryUnitLoadEstimator? estimator = null
    ) {
        estimator ??= _estimator;
        string path = Path.Combine(_root, suffix);
        var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model",
                "system",
                $"recap cadence progress {suffix}"));
        Assert.IsType<HistoryTimelineCreateResult.Created>(
            HistoryTimelineFactory.Create(
                engine.ReadView,
                new HistoryTimelineInitialPolicySpec(
                    HistoryPartitionAlgorithms
                        .FirstReplaySafeBoundaryAtTargetV1,
                    estimator.Id,
                    new HistoryLoadUnit(target),
                    maxRawEvents,
                    maxRenderedBytes: 1024 * 1024),
                estimator));
        Assert.IsType<RecapGridCadenceCreateResult.Created>(
            RecapGridCadenceFactory.Create(
                engine,
                new RecapGridCadencePolicySpec(
                    minimumRecent,
                    HistoryPartitionAlgorithms
                        .FirstReplaySafeBoundaryAtTargetV1,
                    estimator.Id,
                    target,
                    maxRawEvents,
                    maxRenderedBytes: 1024 * 1024)));
        for (int index = 0; index < turns; index++) {
            _ = engine.AppendObservation($"observation-{index}");
            _ = engine.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text($"answer-{index}")
                ]),
                new CompletionDescriptor("import", "v1", "model"));
        }
        return engine;
    }

    private HistorySegmentDescriptor CommitOneTimelineRow(
        SessionJournalEngine engine
    ) {
        using HistoryTimelineHandle timeline = Assert.IsType<
            HistoryTimelineOpenResult.Opened>(
                HistoryTimelineFactory.Open(
                    engine.ReadView,
                    _estimator)).Handle;
        using RecapGridCadenceHandle cadence = Assert.IsType<
            RecapGridCadenceOpenResult.Opened>(
                RecapGridCadenceFactory.OpenMutable(engine)).Handle;
        using RecapGridCadenceTimelineSealOperation seal = Assert.IsType<
            RecapGridCadenceTimelineSealOpenResult.Opened>(
                cadence.BeginTimelineSeal(timeline)).Operation;
        TimelineHeadRef head = Assert.IsType<
            HistoryTimelineSnapshotResult.Available>(
                timeline.Reader.ReadSnapshot()).Head;
        OnlineSelectedRawCapture capture = Assert.IsType<
            OnlineSelectedRawCaptureResult.Captured>(
                timeline.Coordinator.CaptureOnline(
                    head,
                    engine.ReadView)).Capture;
        HistoryRowCommitCandidate candidate = Assert.IsType<
            HistoryTimelinePlanResult.Selected>(
                seal.PlanNextRow(head, capture)).Candidate;
        Assert.IsType<HistoryTimelineCommitResult.Committed>(
            seal.CommitRow(candidate));
        return candidate.Proposal.Descriptor;
    }

    private TimelineHeadRef ReadTimelineHead(SessionJournalEngine engine) {
        using HistoryTimelineBuildReadSession session = Assert.IsType<
            HistoryTimelineBuildReadSessionOpenResult.Opened>(
                HistoryTimelineFactory.OpenBuildReadSession(
                    engine.ReadView,
                    _estimator)).Session;
        return Assert.IsType<HistoryTimelineSnapshotResult.Available>(
            session.Reader.ReadSnapshot()).Head;
    }

    private static RecapGridCadenceSnapshot ReadCadence(
        SessionJournalEngine engine
    ) {
        using RecapGridCadenceReaderHandle handle = Assert.IsType<
            RecapGridCadenceReaderOpenResult.Opened>(
                RecapGridCadenceFactory.OpenReader(engine.ReadView)).Handle;
        return Assert.IsType<RecapGridCadenceReadResult.Available>(
            handle.Reader.ReadSnapshot()).Snapshot;
    }

    private static string Address(EventAddress value)
        => EventAddressTextCodec.Format(value);

    public void Dispose() {
        if (Directory.Exists(_root)) {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class OnePerUnitEstimator : IHistoryUnitLoadEstimator {
        public string Id => EstimatorId;

        public HistoryUnitLoadMeasurement Measure(
            SessionHistoryPlanningUnit unit,
            int maxRenderedUtf8Bytes
        ) => new(new HistoryLoadUnit(1), 1);
    }

    private sealed class FixedPerUnitEstimator(string id, long load)
        : IHistoryUnitLoadEstimator {
        public string Id { get; } = id;

        public HistoryUnitLoadMeasurement Measure(
            SessionHistoryPlanningUnit unit,
            int maxRenderedUtf8Bytes
        ) => new(new HistoryLoadUnit(load), 1);
    }
}
