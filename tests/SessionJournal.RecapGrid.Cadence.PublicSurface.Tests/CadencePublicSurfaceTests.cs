using Atelia.SessionJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Cadence;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Cadence.PublicSurface.Tests;

public sealed class CadencePublicSurfaceTests : IDisposable {
    private readonly string _path = Path.Combine(
        Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath(),
        "atelia-recap-grid-cadence-public-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExternalCompositionUsesOwnerBoundViewAndNoBackendSelector() {
        using SessionJournalEngine engine = SessionJournalEngine.Create(
            _path,
            new SessionCreateOptions("model", "system", "cadence-public"));
        var policy = new RecapGridCadencePolicySpec(
            24000,
            HistoryPartitionAlgorithms.FirstReplaySafeBoundaryAtTargetV1,
            O200kBaseHistoryUnitLoadEstimator.EstimatorId,
            60000,
            4096,
            1024 * 1024);
        RecapGridCadenceSnapshot created = Assert.IsType<
            RecapGridCadenceCreateResult.Created
        >(RecapGridCadenceFactory.Create(engine, policy)).Snapshot;
        using RecapGridCadenceHandle handle = Assert.IsType<
            RecapGridCadenceOpenResult.Opened
        >(RecapGridCadenceFactory.OpenMutable(engine)).Handle;
        Assert.Equal(created.Head, Assert.IsType<
            RecapGridCadenceReadResult.Available
        >(handle.Reader.ReadSnapshot()).Snapshot.Head);
        using RecapGridCadenceReaderHandle readerHandle = Assert.IsType<
            RecapGridCadenceReaderOpenResult.Opened
        >(RecapGridCadenceFactory.OpenReader(engine.ReadView)).Handle;
        Assert.Null(typeof(RecapGridCadenceReaderHandle).GetProperty(
            "Coordinator"));
        Assert.DoesNotContain(
            typeof(RecapGridCadenceTimelineSealOperation).GetMethods(),
            static method => method.Name == "OpenOfflineBuilder"
                && method.GetParameters().Any(parameter =>
                    parameter.ParameterType
                        == typeof(SessionSelectedLineageForwardCursor)));

        Type[] exported = typeof(RecapGridCadenceFactory)
            .Assembly.GetExportedTypes();
        Assert.DoesNotContain(exported, static type =>
            type.Name.Contains("Backend", StringComparison.OrdinalIgnoreCase)
            || type.Name.Contains("FileStream", StringComparison.OrdinalIgnoreCase));
        Assert.All(typeof(RecapGridCadenceFactory).GetMethods(), method => {
            if (method.Name is "Create" or "OpenMutable") {
                Assert.Contains(method.GetParameters(), parameter =>
                    parameter.ParameterType == typeof(SessionJournalEngine));
                Assert.DoesNotContain(method.GetParameters(), parameter =>
                    parameter.ParameterType == typeof(string));
            }
        });
    }

    [Fact]
    public void CadenceLimitsAreNotExported() {
        Type[] cadenceTypes = typeof(RecapGridCadenceFactory).Assembly
            .GetExportedTypes()
            .Where(static type =>
                type.Namespace is "Atelia.SessionJournal.RecapGrid.Cadence"
            )
            .ToArray();

        Assert.NotEmpty(cadenceTypes);
        Assert.DoesNotContain(cadenceTypes, static type =>
            type.Name is "RecapGridCadenceLimits"
        );
    }

    public void Dispose() {
        if (Directory.Exists(_path)) {
            Directory.Delete(_path, recursive: true);
        }
    }
}
