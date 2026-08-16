using System.Reflection;
using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;
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
    public void PublicExportPageLimitsAreExact() {
        Assert.Equal(128, RecapGridStoreLimits.MaximumPageItems);
        Assert.Equal(2 * 1024 * 1024, RecapGridStoreLimits.MaximumPageBytes);
    }

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
        Assert.IsType<RecapGridStoreReadResult<RecapRowView>.Missing>(
            reader.Reader.ReadViewAt(new RowViewAssignmentKey(
                new RefId(1),
                new TimelineId(new string('1', 32)),
                new GridBuildRecipeDigest(new string('2', 64)),
                new HistoryRowId(new string('3', 64))
            ))
        );
    }

    [Fact]
    public void PublicCountersAndPhysicalWitnessUseLongWithoutLifetimeCap() {
        foreach (string property in new[] {
                     nameof(RecapGridStoreInfo.DatabaseBytes),
                     nameof(RecapGridStoreInfo.CellCount),
                     nameof(RecapGridStoreInfo.RowViewCount),
                     nameof(RecapGridStoreInfo.RowViewMemberCount),
                     nameof(RecapGridStoreInfo.FulfilledViewCount)
                 }) {
            Assert.Equal(
                typeof(long),
                typeof(RecapGridStoreInfo).GetProperty(property)!.PropertyType
            );
        }
        var witness = new RecapGridStorePhysicalWitness(
            16L * 1024 * 1024 * 1024,
            new string('a', 64)
        );
        Assert.Equal(16L * 1024 * 1024 * 1024, witness.Length);
        Assert.DoesNotContain(
            typeof(RecapGridStoreLimits).GetFields(
                BindingFlags.Public | BindingFlags.Static
            ),
            static field => field.Name.Contains("Database",
                StringComparison.Ordinal)
                || field.Name.Contains("Count", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void WriterAndMutationResultsAreNotExported() {
        string[] hiddenTypeNames = [
            "RecapGridStoreWriter",
            "RecapGridCellPutResult",
            "RecapGridRowViewPutResult",
            "RecapGridFulfilledPutResult",
            "RecapGridMissingResult",
            "RecapGridFulfilledView"
        ];
        string[] leakedTypeNames = typeof(RecapGridStoreFactory).Assembly
            .GetExportedTypes()
            .Where(static type =>
                type.Namespace is "Atelia.SessionJournal.RecapGrid.Store"
            )
            .Select(static type => type.Name)
            .Intersect(hiddenTypeNames, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(leakedTypeNames);
        Assert.Null(typeof(RecapGridStoreHandle).GetProperty(
            "Writer",
            BindingFlags.Instance | BindingFlags.Public
        ));

        string[] publicReaderMethods = typeof(RecapGridStoreReader)
            .GetMethods(
                BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.DeclaredOnly
            )
            .Select(static method => method.Name)
            .ToArray();
        Assert.DoesNotContain(
            "FindMissingAssignments",
            publicReaderMethods
        );
        Assert.DoesNotContain("ReadFulfilled", publicReaderMethods);
    }

    [Fact]
    public void PublicFactoryHasNoBackendSelector() {
        Assembly assembly = typeof(RecapGridStoreFactory).Assembly;
        Assert.DoesNotContain(
            assembly.GetExportedTypes().Where(static type =>
                type.Namespace is "Atelia.SessionJournal.RecapGrid.Store"
                || type.Namespace?.StartsWith(
                    "Atelia.SessionJournal.RecapGrid.Store.",
                    StringComparison.Ordinal
                ) is true
            ),
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
