using System.Reflection;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Store;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Store.PublicSurface.Tests;

public sealed class StorePublicSurfaceTests : IDisposable {
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "atelia-recap-grid-store-public-tests",
        Guid.NewGuid().ToString("N")
    );

    [Fact]
    public void ExternalCompositionCanCreateOpenReadAndDispose() {
        Directory.CreateDirectory(_root);
        RecapGridStoreCreateResult.Created created = Assert.IsType<
            RecapGridStoreCreateResult.Created
        >(RecapGridStoreFactory.Create(_root));
        RecapGridStoreHandle handle = Assert.IsType<
            RecapGridStoreOpenResult.Opened
        >(RecapGridStoreFactory.Open(_root)).Handle;
        Assert.Equal(created.Identity, handle.Identity);
        var missingDigest = new CellDigest(new string('a', 64));
        Assert.IsType<RecapGridStoreReadResult<RecapCellArtifact>.Missing>(
            handle.Reader.ReadCell(missingDigest)
        );
        handle.Dispose();
        Assert.IsType<RecapGridStoreReadResult<RecapCellArtifact>.Disposed>(
            handle.Reader.ReadCell(missingDigest)
        );

        using RecapGridStoreReaderHandle reader = Assert.IsType<
            RecapGridStoreReaderOpenResult.Opened
        >(RecapGridStoreFactory.OpenReader(_root)).Handle;
        Assert.Equal(created.Identity, reader.Identity);
    }

    [Fact]
    public void PublicFactoryHasNoBackendSelector() {
        Assembly assembly = typeof(RecapGridStoreFactory).Assembly;
        Assert.DoesNotContain(
            assembly.GetExportedTypes(),
            static type => type.Name.Contains(
                "Sqlite",
                StringComparison.OrdinalIgnoreCase
            ) || type.Name.Contains(
                "BackendSelector",
                StringComparison.OrdinalIgnoreCase
            )
        );
        Assert.All(
            typeof(RecapGridStoreFactory).GetMethods(
                BindingFlags.Public | BindingFlags.Static
            ),
            static method => Assert.DoesNotContain(
                method.GetParameters(),
                parameter => parameter.ParameterType.Name.Contains(
                    "Sqlite",
                    StringComparison.OrdinalIgnoreCase
                )
            )
        );
    }

    public void Dispose() {
        if (Directory.Exists(_root)) {
            Directory.Delete(_root, recursive: true);
        }
    }
}
