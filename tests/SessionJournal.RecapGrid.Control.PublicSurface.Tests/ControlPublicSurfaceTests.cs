using Atelia.SessionJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Control;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Control.PublicSurface.Tests;

public sealed class ControlPublicSurfaceTests : IDisposable {
    private readonly string _path = Path.Combine(
        Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath(),
        "atelia-recap-grid-control-public-surface-tests",
        Guid.NewGuid().ToString("N")
    );

    [Fact]
    public void ExternalCompositionCanCreateOpenReadAndDispose() {
        using SessionJournalEngine journal = SessionJournalEngine.Create(
            _path,
            new SessionCreateOptions("model", "system", "surface")
        );
        var estimator = new O200kBaseHistoryUnitLoadEstimator();
        Assert.IsType<HistoryTimelineCreateResult.Created>(
            HistoryTimelineFactory.Create(
                journal.ReadView,
                new HistoryTimelineInitialPolicySpec(
                    HistoryPartitionAlgorithms
                        .FirstReplaySafeBoundaryAtTargetV1,
                    O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                    new HistoryLoadUnit(1),
                    maxRawEvents: 8,
                    maxRenderedBytes: 1024 * 1024
                ),
                estimator
            )
        );
        FamilyDefinition family = FamilyDefinition.Create(
            "Public fixture family.",
            [],
            new FamilyOutputProtocol(
                "output-v1",
                FamilyOutputMode.FullReplacementText
            ),
            new FamilyInputRenderingProtocol(
                "input-v1",
                "prior-v1",
                "history-v1"
            )
        );
        var admission = new RecapGridControlAdmission(
            RecapGridControlPermission.Create
                | RecapGridControlPermission.RegisterFamily,
            [family.Digest],
            Array.Empty<string>(),
            Array.Empty<ContextHeaderCarrier>(),
            ["case."],
            maximumBootstrapRows: 0,
            maximumProjectedCalls: 0
        );
        ControlHeadRef created = Assert.IsType<
            RecapGridControlCreateResult.Created
        >(RecapGridControlFactory.Create(
            _path,
            journal.BranchRefId,
            admission
        )).Head;
        using (RecapGridControlHandle handle = Assert.IsType<
               RecapGridControlOpenResult.Opened
               >(RecapGridControlFactory.Open(
                   _path,
                   journal.BranchRefId,
                   admission
               )).Handle) {
            Assert.Equal(
                created,
                Assert.IsType<RecapGridControlSnapshotResult.Available>(
                    handle.Reader.ReadSnapshot()
                ).Snapshot.Head
            );
        }
        RecapGridControlReaderHandle readerHandle = Assert.IsType<
            RecapGridControlReaderOpenResult.Opened
        >(RecapGridControlFactory.OpenReader(
            _path,
            journal.BranchRefId
        )).Handle;
        Assert.IsType<RecapGridControlSnapshotResult.Available>(
            readerHandle.Reader.ReadSnapshot()
        );
        readerHandle.Dispose();
        Assert.IsType<RecapGridControlSnapshotResult.Disposed>(
            readerHandle.Reader.ReadSnapshot()
        );
    }

    [Fact]
    public void PublicFactoryAndHandlesExposeNoBackendSelector() {
        Type[] exported = typeof(RecapGridControlFactory)
            .Assembly.GetExportedTypes();
        Assert.DoesNotContain(exported, static type =>
            type.Name.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)
            || type.Name.Contains(
                "Backend",
                StringComparison.OrdinalIgnoreCase
            )
            || type.Name.Contains(
                "DurableFiles",
                StringComparison.OrdinalIgnoreCase
            )
        );
        Assert.All(
            typeof(RecapGridControlFactory).GetMethods(
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
                    "Store",
                    StringComparison.OrdinalIgnoreCase
                )
            )
        );
        Assert.Empty(typeof(RecapGridControlReader).GetConstructors());
        Assert.Empty(typeof(RecapGridControlCoordinator).GetConstructors());
    }

    public void Dispose() {
        if (Directory.Exists(_path)) {
            Directory.Delete(_path, recursive: true);
        }
    }
}
