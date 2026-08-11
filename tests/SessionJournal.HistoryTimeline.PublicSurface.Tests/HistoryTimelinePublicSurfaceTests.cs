using Atelia.SessionJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Xunit;

namespace Atelia.SessionJournal.HistoryTimeline.PublicSurface.Tests;

public sealed class HistoryTimelinePublicSurfaceTests : IDisposable {
    private readonly string _path = Path.Combine(
        Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath(),
        "atelia-history-timeline-public-surface-tests",
        Guid.NewGuid().ToString("N")
    );

    [Fact]
    public void ExternalCompositionCanCreateOpenReadAndDisposeWithoutBackendAccess() {
        using SessionJournalEngine journal = SessionJournalEngine.Create(
            _path,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        );
        var estimator = new O200kBaseHistoryUnitLoadEstimator();
        var spec = new HistoryTimelineInitialPolicySpec(
            HistoryPartitionAlgorithms
                .FirstReplaySafeBoundaryAtTargetV1,
            O200kBaseHistoryUnitLoadEstimator.EstimatorId,
            new HistoryLoadUnit(1),
            maxRawEvents: 8,
            maxRenderedBytes: 1024 * 1024
        );

        HistoryTimelineCreateResult.Created created = Assert.IsType<
            HistoryTimelineCreateResult.Created
        >(HistoryTimelineFactory.Create(
            journal.ReadView,
            spec,
            estimator
        ));
        using HistoryTimelineHandle handle = Assert.IsType<
            HistoryTimelineOpenResult.Opened
        >(HistoryTimelineFactory.Open(
            journal.ReadView,
            estimator
        )).Handle;
        TimelineHeadRef head = Assert.IsType<
            HistoryTimelineSnapshotResult.Available
        >(handle.Reader.ReadSnapshot()).Head;
        Assert.Equal(created.InitialHead, head);

        using HistoryTimelineReaderHandle readerHandle = Assert.IsType<
            HistoryTimelineReaderOpenResult.Opened
        >(HistoryTimelineMaintenance.OpenReader(
            _path,
            journal.BranchRefId
        )).Handle;
        Assert.Equal(
            head,
            Assert.IsType<HistoryTimelineSnapshotResult.Available>(
                readerHandle.Reader.ReadSnapshot()
            ).Head
        );

        handle.Dispose();
        HistoryTimelineSnapshotResult.Invalid disposed = Assert.IsType<
            HistoryTimelineSnapshotResult.Invalid
        >(handle.Reader.ReadSnapshot());
        Assert.Equal("HistoryTimelineDisposed", disposed.Code);
    }

    [Fact]
    public void PublicFactoryAndHandlesExposeNoBackendSelector() {
        Type[] publicTypes = typeof(HistoryTimelineFactory)
            .Assembly
            .GetExportedTypes();
        Assert.DoesNotContain(publicTypes, static type =>
            type.Name.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)
            || type.Name.Contains("LedgerPort", StringComparison.OrdinalIgnoreCase)
            || type.Name.Contains("InMemory", StringComparison.OrdinalIgnoreCase)
        );
        Assert.All(
            typeof(HistoryTimelineFactory).GetMethods(
                System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.DeclaredOnly
            ),
            static method => Assert.DoesNotContain(
                method.GetParameters(),
                parameter => parameter.ParameterType.Name.Contains(
                    "Backend",
                    StringComparison.OrdinalIgnoreCase
                ) || parameter.ParameterType.Name.Contains(
                    "Ledger",
                    StringComparison.OrdinalIgnoreCase
                )
            )
        );
        Assert.Empty(typeof(HistoryTimelineCoordinator).GetConstructors());
        Assert.Empty(typeof(HistoryTimelineReader).GetConstructors());
    }

    [Fact]
    public void ExternalBuildReadSessionCanOpenSelectedContentWithoutCoordinatorSurface() {
        using (SessionJournalLegacyImportWriter writer =
               SessionJournalLegacyImportWriter.Create(
            _path,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-build-reader"
            )
        )) {
            _ = writer.AppendObservation("build-reader-observation");
        }
        using SessionJournalEngine journal =
            SessionJournalEngine.OpenReadOnly(_path);
        var estimator = new O200kBaseHistoryUnitLoadEstimator();
        var policy = new HistoryTimelineInitialPolicySpec(
            HistoryPartitionAlgorithms
                .FirstReplaySafeBoundaryAtTargetV1,
            O200kBaseHistoryUnitLoadEstimator.EstimatorId,
            new HistoryLoadUnit(1),
            maxRawEvents: 8,
            maxRenderedBytes: 1024 * 1024
        );
        Assert.IsType<HistoryTimelineCreateResult.Created>(
            HistoryTimelineFactory.Create(
                journal.ReadView,
                policy,
                estimator
            )
        );
        TimelineHeadRef committed;
        using (HistoryTimelineHandle writer = Assert.IsType<
                   HistoryTimelineOpenResult.Opened
               >(HistoryTimelineFactory.Open(
                   journal.ReadView,
                   estimator
               )).Handle) {
            TimelineHeadRef before = Assert.IsType<
                HistoryTimelineSnapshotResult.Available
            >(writer.Reader.ReadSnapshot()).Head;
            OnlineSelectedRawCapture capture = Assert.IsType<
                OnlineSelectedRawCaptureResult.Captured
            >(writer.Coordinator.CaptureOnline(
                before,
                journal.ReadView
            )).Capture;
            HistoryRowCommitCandidate candidate = Assert.IsType<
                HistoryTimelinePlanResult.Selected
            >(writer.Coordinator.PlanNextRow(
                before,
                capture
            )).Candidate;
            committed = Assert.IsType<
                HistoryTimelineCommitResult.Committed
            >(writer.Coordinator.CommitRow(candidate)).Head;
        }

        using HistoryTimelineBuildReadSession session = Assert.IsType<
            HistoryTimelineBuildReadSessionOpenResult.Opened
        >(HistoryTimelineFactory.OpenBuildReadSession(
            journal.ReadView,
            estimator
        )).Session;
        HistoryTimelineSelectedRow row = Assert.IsType<
            HistoryTimelineReaderRowResult.Selected
        >(session.Reader.ReadSelectedRow(
            committed,
            committed.HeadRowId!.Value
        )).Row;
        OnlineSelectedRawCapture raw = Assert.IsType<
            OnlineSelectedRawCaptureResult.Captured
        >(session.CaptureRaw(committed)).Capture;
        HistorySegmentContent content = Assert.IsType<
            HistorySegmentOpenResult.Opened
        >(session.OpenSelectedSegment(
            committed,
            raw,
            row
        )).Content;

        Assert.Equal(row.Descriptor, content.Descriptor);
        Assert.Null(typeof(HistoryTimelineBuildReadSession).GetProperty(
            "Coordinator"
        ));
        session.Dispose();
        Assert.Equal(
            "HistoryTimelineDisposed",
            Assert.IsType<HistoryTimelineSnapshotResult.Invalid>(
                session.Reader.ReadSnapshot()
            ).Code
        );
    }

    public void Dispose() {
        if (Directory.Exists(_path)) {
            Directory.Delete(_path, recursive: true);
        }
    }
}
