using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Cadence;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Manager;
using Atelia.SessionJournal.RecapGrid.Online;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Online.PublicSurface.Tests;

public sealed class PublicSurfaceTests {
    [Fact]
    public async Task ExternalCompositionCanOpenUseAndDisposeOnlineHandle() {
        string path = Path.Combine(
            Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath(),
            "atelia-recap-grid-online-public-tests",
            Guid.NewGuid().ToString("N"));
        try {
            var estimator = new O200kBaseHistoryUnitLoadEstimator();
            using SessionJournalEngine engine = SessionJournalEngine.Create(
                path,
                new SessionCreateOptions("model", "system", "surface"));
            Assert.IsType<HistoryTimelineCreateResult.Created>(
                HistoryTimelineFactory.Create(
                    engine.ReadView,
                    new HistoryTimelineInitialPolicySpec(
                        HistoryPartitionAlgorithms
                            .FirstReplaySafeBoundaryAtTargetV1,
                        O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                        new HistoryLoadUnit(1),
                        maxRawEvents: 64,
                        maxRenderedBytes: 1024 * 1024),
                    estimator));
            Assert.IsType<RecapGridCadenceCreateResult.Created>(
                RecapGridCadenceFactory.Create(
                    engine,
                    new RecapGridCadencePolicySpec(
                        minimumRecentHistoryLoad: 1,
                        HistoryPartitionAlgorithms
                            .FirstReplaySafeBoundaryAtTargetV1,
                        O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                        targetHistoryLoad: 1,
                        maxRawEvents: 64,
                        maxRenderedBytes: 1024 * 1024)));
            Assert.IsType<RecapGridControlCreateResult.Created>(
                RecapGridControlFactory.Create(
                    path,
                    engine.BranchRefId,
                    new RecapGridControlAdmission(
                        RecapGridControlPermission.Create,
                        Array.Empty<FamilyDefinitionDigest>(),
                        Array.Empty<string>(),
                        Array.Empty<ContextHeaderCarrier>(),
                        ["surface."],
                        maximumBootstrapRows: 64,
                        maximumProjectedCalls: 64)));

            RecapGridOnlineOpenResult.Opened opened = Assert.IsType<
                RecapGridOnlineOpenResult.Opened>(
                RecapGridOnlineFactory.Open(
                    engine,
                    new RejectingExecutor(),
                    RecapGridOnlineLimits.Production,
                    estimator));
            await using RecapGridOnlineContextHandle handle = opened.Handle;
            EventAddress head = engine.ReadCurrentHead()!.Value;
            RecapGridOnlinePassResult result = await handle.PreparePassAsync(
                engine.ReadView,
                new SessionContextLifecycleRequest(
                    new SessionContextSelectionRequest(head, 0),
                    SessionExecutionPhase.Idle,
                    SessionContextLifecycleTrigger.PreObservation,
                    "pending"));

            Assert.IsType<RecapGridOnlinePassResult.RawHistoryAuthorized>(
                result);
            Assert.NotNull(handle.CandidateSource);
            Assert.Same(handle, handle.Lifecycle);
        }
        finally {
            if (Directory.Exists(path)) {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    [Fact]
    public void CatchUpLimitsAreNotExported() {
        Assert.DoesNotContain(
            typeof(RecapGridOnlineFactory).Assembly.GetExportedTypes()
                .Where(static type => type.Namespace
                    is "Atelia.SessionJournal.RecapGrid.Online"),
            static type => type.Name is "RecapGridOnlineCatchUpLimits"
        );
    }

    private sealed class RejectingExecutor : IRecapCellBatchExecutor {
        public ValueTask<RecapCellBatchExecutionResult> ExecuteAsync(
            FrozenRowBatch batch,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException(
            "A raw-only public-surface fixture must not execute work.");
    }
}
