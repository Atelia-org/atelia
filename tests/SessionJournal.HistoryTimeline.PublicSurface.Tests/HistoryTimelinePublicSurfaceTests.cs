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
    public void O200kExtensionAssemblyExportsOnlyEstimator() {
        var timelineAssembly = typeof(HistoryTimelineFactory).Assembly;
        var o200kAssembly = typeof(O200kBaseHistoryUnitLoadEstimator).Assembly;

        Assert.NotEqual(timelineAssembly, o200kAssembly);
        Assert.Equal(
            [typeof(O200kBaseHistoryUnitLoadEstimator)],
            o200kAssembly.GetExportedTypes()
        );
        Assert.Null(timelineAssembly.GetType(
            typeof(O200kBaseHistoryUnitLoadEstimator).FullName!,
            throwOnError: false
        ));
    }

    [Fact]
    public void HistoryLoadSafetyLimitsAreNotExported() {
        Type[] timelineTypes = typeof(HistoryTimelineFactory).Assembly
            .GetExportedTypes()
            .Where(static type =>
                type.Namespace is "Atelia.SessionJournal.HistoryTimeline"
            )
            .ToArray();
        Assert.Contains(typeof(HistoryTimelineFactory), timelineTypes);

        string[] leakedSafetyTypes = timelineTypes
            .Select(static type => type.FullName)
            .Where(static fullName => fullName is
                "Atelia.SessionJournal.HistoryTimeline.HistoryLoadMeasurementSafety"
                or "Atelia.SessionJournal.HistoryTimeline.HistoryLoadMeasurementSafety+V1"
            )
            .Cast<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Empty(leakedSafetyTypes);
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
    public void ExternalBuildReadSessionExposesNoTimelineMutationSurface() {
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
        using HistoryTimelineBuildReadSession session = Assert.IsType<
            HistoryTimelineBuildReadSessionOpenResult.Opened
        >(HistoryTimelineFactory.OpenBuildReadSession(
            journal.ReadView,
            estimator
        )).Session;
        TimelineHeadRef head = Assert.IsType<
            HistoryTimelineSnapshotResult.Available
        >(session.Reader.ReadSnapshot()).Head;
        _ = Assert.IsType<
            OnlineSelectedRawCaptureResult.Captured
        >(session.CaptureRaw(head));
        Assert.IsType<HistoryTimelineRawHeadObservationResult.Available>(
            session.ObserveRawHead()
        );
        Assert.Null(typeof(HistoryTimelineBuildReadSession).GetProperty(
            "Coordinator"
        ));
        string[] forbiddenMutationMethods = [
            "PlanNextRow",
            "CommitRow",
            "OpenOfflineBuilder"
        ];
        Assert.DoesNotContain(
            typeof(HistoryTimelineCoordinator).GetMethods(
                System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly),
            method => forbiddenMutationMethods.Contains(
                method.Name,
                StringComparer.Ordinal));
        session.Dispose();
        Assert.IsType<HistoryTimelineRawHeadObservationResult.Disposed>(
            session.ObserveRawHead()
        );
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
