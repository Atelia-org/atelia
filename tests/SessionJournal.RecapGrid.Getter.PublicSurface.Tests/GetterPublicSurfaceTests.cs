using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Cadence;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Getter;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Getter.PublicSurface.Tests;

public sealed class GetterPublicSurfaceTests : IDisposable {
    private readonly string _path = Path.Combine(
        Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath(),
        "atelia-recap-grid-getter-public-tests",
        Guid.NewGuid().ToString("N")
    );

    [Fact]
    public async Task ExternalCompositionOpensRawWithoutStoreAndDisposes() {
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
        Assert.IsType<RecapGridCadenceCreateResult.Created>(
            RecapGridCadenceFactory.Create(
                journal,
                new RecapGridCadencePolicySpec(
                    minimumRecentHistoryLoad: 1,
                    HistoryPartitionAlgorithms
                        .FirstReplaySafeBoundaryAtTargetV1,
                    O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                    targetHistoryLoad: 1,
                    maxRawEvents: 8,
                    maxRenderedBytes: 1024 * 1024
                )
            )
        );
        var admission = new RecapGridControlAdmission(
            RecapGridControlPermission.Create,
            [],
            [],
            [],
            ["case."],
            maximumBootstrapRows: 0,
            maximumProjectedCalls: 0
        );
        Assert.IsType<RecapGridControlCreateResult.Created>(
            RecapGridControlFactory.Create(
                _path,
                journal.BranchRefId,
                admission
            )
        );

        RecapGridContextHandle getter = Assert.IsType<
            RecapGridContextOpenResult.Opened>(
            RecapGridContextFactory.Open(journal.ReadView, estimator)
        ).Handle;
        string storePath = Path.Combine(
            _path,
            "derived",
            "recap-grid",
            "v1",
            "grid.sqlite"
        );
        Assert.IsType<RecapGridContextResolveResult.RawHistoryAuthorized>(
            getter.Resolve(journal.ReadCurrentHead()!.Value, 0)
        );
        Assert.False(File.Exists(storePath));
        Assert.Equal(
            SessionContextCandidateSelectionStatus.EmptyLineage,
            (await getter.SelectAsync(
                new SessionContextSelectionRequest(
                    journal.ReadCurrentHead()!.Value,
                    0
                ),
                CancellationToken.None
            )).Status
        );
        getter.Dispose();
        Assert.IsType<RecapGridContextResolveResult.Disposed>(
            getter.Resolve(journal.ReadCurrentHead()!.Value, 0)
        );
    }

    [Fact]
    public void PublicSurfaceHasNoBackendOrMutationSelector() {
        Type[] exported = typeof(RecapGridContextFactory)
            .Assembly.GetExportedTypes();
        Assert.DoesNotContain(exported, static type =>
            type.Name.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)
            || type.Name.Contains("Backend", StringComparison.OrdinalIgnoreCase)
            || type.Name.Contains("Coordinator", StringComparison.OrdinalIgnoreCase)
        );
        Assert.Empty(typeof(RecapGridContextHandle).GetConstructors());
        Assert.Empty(typeof(RecapGridContextSelection).GetConstructors());
        Assert.Single(typeof(RecapGridContextFactory).GetMethods(
            System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.DeclaredOnly
        ));
    }

    public void Dispose() {
        if (Directory.Exists(_path)) {
            Directory.Delete(_path, recursive: true);
        }
    }
}
