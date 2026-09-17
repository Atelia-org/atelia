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
    public void RowWorkCursorIsReadableWithoutExportingWriterOrImportFactories() {
        var bytes = new byte[66];
        bytes[0] = 2;
        bytes[1] = 4;
        System.Text.Encoding.ASCII.GetBytes(new string('a', 64))
            .CopyTo(bytes, 2);
        string value = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        Assert.Equal(value, RecapGridStoreExportCursor.Parse(value).Value);
        string[] publicFactories = typeof(RecapGridStoreExportCursor).GetMethods(
                BindingFlags.Public
                | BindingFlags.Static
                | BindingFlags.DeclaredOnly
            ).Where(static method => !method.IsSpecialName)
            .Select(static method => method.Name).ToArray();
        Assert.Equal(["Parse"], publicFactories);
        Assert.DoesNotContain(
            typeof(RecapGridStoreMaintenance).GetMethods(
                BindingFlags.Public
                | BindingFlags.Static
                | BindingFlags.DeclaredOnly
            ),
            static method => method.Name.Contains(
                "Import",
                StringComparison.Ordinal
            )
        );
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
        var missingDigest = new CellId(new string('a', 32));
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

    [Fact]
    public void PartialProofFailureContractIsClosedAndBounded() {
        foreach (RecapGridStorePartialProofFailure failure in
                 Enum.GetValues<RecapGridStorePartialProofFailure>()) {
            var exception = new RecapGridStorePartialProofException(
                failure,
                "exact proof failed"
            );
            Assert.Equal(failure, exception.Failure);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RecapGridStorePartialProofException(
                (RecapGridStorePartialProofFailure)int.MaxValue,
                "invalid enum"
            ));
        Assert.Throws<ArgumentException>(() =>
            new RecapGridStorePartialProofException(
                RecapGridStorePartialProofFailure.Unprovable,
                string.Empty
            ));
        Assert.Throws<ArgumentException>(() =>
            new RecapGridStorePartialProofException(
                RecapGridStorePartialProofFailure.Unprovable,
                new string('x', 1025)
            ));
    }

    [Fact]
    public void V4PartialProofBoundaryExposesFactsWithoutSqliteAuthority() {
        MethodInfo[] methods = typeof(RecapGridStoreMaintenance).GetMethods(
            BindingFlags.Public | BindingFlags.Static);
        foreach ((string name, int count) in new[] {
                     (nameof(RecapGridStoreMaintenance.UpgradeV4), 3),
                     (nameof(RecapGridStoreMaintenance.PrepareRestoreV4), 3),
                     (nameof(RecapGridStoreMaintenance.RestoreV4), 5)
                 }) {
            MethodInfo factsAware = Assert.Single(methods,
                method => method.Name == name
                    && method.GetParameters().Length == count
                    && IsFactsResolver(method.GetParameters()[^1]
                        .ParameterType));
            Assert.DoesNotContain("Sqlite", factsAware.ToString(),
                StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(
            typeof(IReadOnlyList<RecapGridStoreV4PartialCellFact>),
            typeof(RecapGridStoreV4PartialProofFacts)
                .GetProperty(nameof(
                    RecapGridStoreV4PartialProofFacts.PartialCells))!
                .PropertyType);
        Assert.Equal(
            typeof(IReadOnlyList<RecapGridStoreV4RowMemberFact>),
            typeof(RecapGridStoreV4RowFact)
                .GetProperty(nameof(RecapGridStoreV4RowFact.Members))!
                .PropertyType);

        static bool IsFactsResolver(Type type) => type.IsGenericType
            && type.GenericTypeArguments.Length == 2
            && type.GenericTypeArguments[0]
                == typeof(RecapGridStoreV4PartialProofFacts);
    }

    public void Dispose() {
        if (Directory.Exists(_root)) {
            Directory.Delete(_root, recursive: true);
        }
    }
}
