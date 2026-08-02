using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Planner.Tests;

public sealed class RecapPlannerConfigRepositoryTests : IDisposable {
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "atelia-recap-planner-config-tests",
        Guid.NewGuid().ToString("N")
    );

    public void Dispose() {
        try {
            if (Directory.Exists(_tempRoot)) {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch {
            // Best-effort cleanup for test-owned paths.
        }
    }

    [Fact]
    public void CodecCanonicalizesExactV2DocumentAndPreservesCatalogOrder() {
        RecapPlannerConfigDocument document = CreateDocument();

        byte[] canonical =
            RecapPlannerConfigCodec.EncodeCanonical(document);

        const string expected =
            "{\"schema\":\"atelia.session-journal.recap-planner-config.v2\","
            + "\"planningPolicy\":\"bounded-maintain-all-v1\","
            + "\"cadence\":{\"historyUnitLoadEstimatorId\":"
            + "\"atelia.history-load.o200k-base.history-unit-v1\","
            + "\"minimumRecentHistoryLoad\":18000,"
            + "\"recapBuildIntervalHistoryLoad\":21000},"
            + "\"catalog\":[{\"maintainerProfile\":"
            + "\"world-understanding-rewrite\","
            + "\"maxContentUtf8Bytes\":32768},"
            + "{\"maintainerProfile\":\"autobiographical-rewrite\","
            + "\"maxContentUtf8Bytes\":16384}],"
            + "\"limits\":{\"maxRawGrowthEventCount\":512,"
            + "\"maxRouteEndpointsPerBlock\":4,"
            + "\"maxMaintainerCallsPerBuild\":8,"
            + "\"maxRawEventsPerStep\":64,"
            + "\"maxRawEventsPerBuild\":512}}";
        Assert.Equal(expected, Encoding.UTF8.GetString(canonical));
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(canonical)),
            RecapPlannerConfigCodec.ComputeSha256(canonical)
        );

        var decoded = Assert.IsType<
            RecapPlannerConfigDecodeResult.Valid
        >(RecapPlannerConfigCodec.Decode(canonical));
        Assert.Equal(
            [
                "world-understanding-rewrite",
                "autobiographical-rewrite"
            ],
            decoded.Document.Catalog
                .Select(static entry => entry.MaintainerProfile)
        );
        Assert.Equal(canonical, decoded.CanonicalBytes.ToArray());
        Assert.Equal(
            RecapPlannerConfigCodec.ComputeSha256(canonical),
            decoded.ConfigSha256
        );
    }

    [Fact]
    public void DocumentSnapshotsCatalogOnConstructionAndWithUpdate() {
        var source = new List<RecapPlannerCatalogEntryDocument> {
            new("world-understanding-rewrite", 32_768)
        };
        RecapPlannerConfigDocument document =
            CreateDocument() with { Catalog = source };

        source.Clear();

        Assert.Single(document.Catalog);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<RecapPlannerCatalogEntryDocument>)
                document.Catalog).Clear()
        );
    }

    [Theory]
    [MemberData(nameof(InvalidJsonDocuments))]
    public void CodecRejectsMalformedOrInvalidDocuments(
        string json,
        string expectedCode
    ) {
        var invalid = Assert.IsType<
            RecapPlannerConfigDecodeResult.Invalid
        >(RecapPlannerConfigCodec.Decode(Encoding.UTF8.GetBytes(json)));

        Assert.Contains(
            invalid.Defects,
            defect => defect.Code == expectedCode
        );
    }

    [Fact]
    public void CodecAllowsUnknownPolicyAndProfileForHostResolution() {
        string json = ValidJson()
            .Replace(
                "bounded-maintain-all-v1",
                "future-policy-v9",
                StringComparison.Ordinal
            )
            .Replace(
                "world-understanding-rewrite",
                "future-profile",
                StringComparison.Ordinal
            );

        var valid = Assert.IsType<
            RecapPlannerConfigDecodeResult.Valid
        >(RecapPlannerConfigCodec.Decode(Encoding.UTF8.GetBytes(json)));

        Assert.Equal("future-policy-v9", valid.Document.PlanningPolicy);
        Assert.Equal(
            "future-profile",
            valid.Document.Catalog[0].MaintainerProfile
        );
    }

    [Fact]
    public void CodecRequiresExactInt64CadenceNumbers() {
        foreach (string literal in new[] {
            "1.0",
            "1e3",
            "9223372036854775808",
            "-9223372036854775809"
        }) {
            string json = ValidJson().Replace(
                "18000",
                literal,
                StringComparison.Ordinal
            );

            var invalid = Assert.IsType<
                RecapPlannerConfigDecodeResult.Invalid
            >(RecapPlannerConfigCodec.Decode(
                Encoding.UTF8.GetBytes(json)
            ));

            Assert.Equal(
                RecapPlannerConfigDefectCodes.InvalidLimit,
                Assert.Single(invalid.Defects).Code
            );
        }
    }

    [Fact]
    public void CodecAcceptsInt64BoundaryIndependentOfRawCountLimit() {
        RecapPlannerConfigDocument document = CreateDocument() with {
            Cadence = new RecapCadenceConfigDocument(
                O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                long.MaxValue - 1,
                1
            ),
            Limits = CreateDocument().Limits with {
                MaxRawGrowthEventCount = 1
            }
        };

        byte[] canonical =
            RecapPlannerConfigCodec.EncodeCanonical(document);
        var valid = Assert.IsType<
            RecapPlannerConfigDecodeResult.Valid
        >(RecapPlannerConfigCodec.Decode(canonical));

        Assert.Equal(
            long.MaxValue - 1,
            valid.Document.Cadence.MinimumRecentHistoryLoad
        );
        Assert.Equal(
            1,
            valid.Document.Limits.MaxRawGrowthEventCount
        );
    }

    [Fact]
    public void CodecValidatesCadenceRangeAndCheckedThresholdSum() {
        AssertEncodeInvalidLimit(CreateDocument() with {
            Cadence = CreateDocument().Cadence with {
                MinimumRecentHistoryLoad = -1
            }
        });
        AssertEncodeInvalidLimit(CreateDocument() with {
            Cadence = CreateDocument().Cadence with {
                RecapBuildIntervalHistoryLoad = 0
            }
        });
        AssertEncodeInvalidLimit(CreateDocument() with {
            Cadence = CreateDocument().Cadence with {
                MinimumRecentHistoryLoad = long.MaxValue,
                RecapBuildIntervalHistoryLoad = 1
            }
        });
        AssertEncodeInvalidLimit(CreateDocument() with {
            Cadence = CreateDocument().Cadence with {
                HistoryUnitLoadEstimatorId = " "
            }
        });
    }

    [Fact]
    public void CodecRejectsLegacyV1Schema() {
        string legacy = ValidJson().Replace(
            RecapPlannerConfigCodec.SchemaV2,
            "atelia.session-journal.recap-planner-config.v1",
            StringComparison.Ordinal
        );

        var invalid = Assert.IsType<
            RecapPlannerConfigDecodeResult.Invalid
        >(RecapPlannerConfigCodec.Decode(
            Encoding.UTF8.GetBytes(legacy)
        ));

        Assert.Equal(
            RecapPlannerConfigDefectCodes.UnsupportedSchema,
            Assert.Single(invalid.Defects).Code
        );
    }

    [Fact]
    public void LoaderReturnsMissingThenOneHandleCanonicalSnapshot() {
        string repository = CreateRepositoryDirectory("load");

        Assert.IsType<RecapPlannerConfigLoadResult.Missing>(
            RecapPlannerConfigLoader.Load(repository)
        );

        string path =
            RecapPlannerConfigLoader.GetCanonicalPath(repository);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string nonCanonical =
            "{\n  \"limits\": {\"maxRawEventsPerBuild\":512,"
            + "\"maxRawEventsPerStep\":64,"
            + "\"maxMaintainerCallsPerBuild\":8,"
            + "\"maxRouteEndpointsPerBlock\":4,"
            + "\"maxRawGrowthEventCount\":512},"
            + "\"catalog\":[{\"maxContentUtf8Bytes\":32768,"
            + "\"maintainerProfile\":\"world-understanding-rewrite\"},"
            + "{\"maxContentUtf8Bytes\":16384,"
            + "\"maintainerProfile\":\"autobiographical-rewrite\"}],"
            + "\"cadence\":{\"recapBuildIntervalHistoryLoad\":21000,"
            + "\"minimumRecentHistoryLoad\":18000,"
            + "\"historyUnitLoadEstimatorId\":"
            + "\"atelia.history-load.o200k-base.history-unit-v1\"},"
            + "\"planningPolicy\":\"bounded-maintain-all-v1\","
            + "\"schema\":\"atelia.session-journal.recap-planner-config.v2\""
            + "}\n";
        File.WriteAllText(path, nonCanonical, new UTF8Encoding(false));

        var available = Assert.IsType<
            RecapPlannerConfigLoadResult.Available
        >(RecapPlannerConfigLoader.Load(repository));

        Assert.Equal(Path.GetFullPath(path), available.Path);
        Assert.Equal(
            RecapPlannerConfigCodec.EncodeCanonical(CreateDocument()),
            available.CanonicalBytes.ToArray()
        );
        Assert.Equal(
            RecapPlannerConfigCodec.ComputeSha256(
                available.CanonicalBytes.AsSpan()
            ),
            available.ConfigSha256
        );
        Assert.Equal(
            "world-understanding-rewrite",
            available.Document.Catalog[0].MaintainerProfile
        );
    }

    [Fact]
    public void LoaderRejectsOversizeUnsafeAndNonRegularInputs() {
        string oversizeRepository =
            CreateRepositoryDirectory("oversize");
        string oversizePath =
            RecapPlannerConfigLoader.GetCanonicalPath(
                oversizeRepository
            );
        Directory.CreateDirectory(
            Path.GetDirectoryName(oversizePath)!
        );
        File.WriteAllBytes(
            oversizePath,
            new byte[
                RecapPlannerConfigCodec.MaxDocumentUtf8Bytes + 1
            ]
        );
        AssertInvalidCode(
            RecapPlannerConfigLoader.Load(oversizeRepository),
            RecapPlannerConfigDefectCodes.SizeLimitExceeded
        );

        string directoryRepository =
            CreateRepositoryDirectory("directory-target");
        Directory.CreateDirectory(
            RecapPlannerConfigLoader.GetCanonicalPath(
                directoryRepository
            )
        );
        AssertInvalidCode(
            RecapPlannerConfigLoader.Load(directoryRepository),
            RecapPlannerConfigDefectCodes.UnsafePath
        );

        string targetRepository =
            CreateRepositoryDirectory("target-link");
        string targetPath =
            RecapPlannerConfigLoader.GetCanonicalPath(targetRepository);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        string external = Path.Combine(_tempRoot, "external.json");
        File.WriteAllBytes(
            external,
            RecapPlannerConfigCodec.EncodeCanonical(CreateDocument())
        );
        File.CreateSymbolicLink(targetPath, external);
        AssertInvalidCode(
            RecapPlannerConfigLoader.Load(targetRepository),
            RecapPlannerConfigDefectCodes.UnsafePath
        );

        string ancestorRepository =
            CreateRepositoryDirectory("ancestor-link");
        string realConfigDirectory =
            Path.Combine(_tempRoot, "real-config-directory");
        Directory.CreateDirectory(realConfigDirectory);
        Directory.CreateSymbolicLink(
            Path.Combine(ancestorRepository, "config"),
            realConfigDirectory
        );
        AssertInvalidCode(
            RecapPlannerConfigLoader.Load(ancestorRepository),
            RecapPlannerConfigDefectCodes.UnsafePath
        );
    }

    [Fact]
    public void InitializerPublishesCreateNewAndNeverChangesExistingFile() {
        string repository = CreateRepositoryDirectory("initialize");

        var initialized = Assert.IsType<
            RecapPlannerConfigInitializeResult.Initialized
        >(
            RecapPlannerConfigInitializer.Initialize(
                repository,
                CreateDocument()
            )
        );
        byte[] original = File.ReadAllBytes(initialized.Path);
        Assert.Equal(
            RecapPlannerConfigCodec.EncodeCanonical(CreateDocument()),
            original
        );
        Assert.DoesNotContain(
            Directory.EnumerateFiles(
                Path.GetDirectoryName(initialized.Path)!
            ),
            file => file.EndsWith(".tmp", StringComparison.Ordinal)
        );

        var exists = Assert.IsType<
            RecapPlannerConfigInitializeResult.AlreadyExists
        >(
            RecapPlannerConfigInitializer.Initialize(
                repository,
                CreateDocument() with {
                    PlanningPolicy = "another-policy"
                }
            )
        );
        Assert.Equal(initialized.Path, exists.Path);
        Assert.Equal(original, File.ReadAllBytes(initialized.Path));
    }

    [Fact]
    public void InitializerOrdersLinuxDirectoryDurabilityBarriers() {
        if (!OperatingSystem.IsLinux()) {
            return;
        }
        string repository = CreateRepositoryDirectory("durability-order");
        var observed = new List<RecapPlannerConfigInitializeIoPoint>();

        var result = RecapPlannerConfigInitializer.Initialize(
            repository,
            CreateDocument(),
            new RecapPlannerConfigInitializerTestHooks(
                (point, _) => observed.Add(point)
            )
        );

        Assert.IsType<RecapPlannerConfigInitializeResult.Initialized>(
            result
        );
        Assert.Equal(
            [
                RecapPlannerConfigInitializeIoPoint
                    .ConfigDirectoryCreated,
                RecapPlannerConfigInitializeIoPoint
                    .RepositoryRootBarrier,
                RecapPlannerConfigInitializeIoPoint
                    .TemporaryFileBarrier,
                RecapPlannerConfigInitializeIoPoint.ConfigPublished,
                RecapPlannerConfigInitializeIoPoint
                    .ConfigDirectoryBarrier
            ],
            observed
        );
    }

    [Theory]
    [InlineData(
        (int)RecapPlannerConfigInitializeIoPoint.RepositoryRootBarrier,
        false
    )]
    [InlineData(
        (int)RecapPlannerConfigInitializeIoPoint.ConfigDirectoryBarrier,
        true
    )]
    public void InitializerBarrierFailureIsUnavailable(
        int failurePointValue,
        bool configWasPublished
    ) {
        if (!OperatingSystem.IsLinux()) {
            return;
        }
        string repository = CreateRepositoryDirectory(
            $"durability-failure-{failurePointValue}"
        );
        var failurePoint =
            (RecapPlannerConfigInitializeIoPoint)failurePointValue;
        string path = RecapPlannerConfigLoader.GetCanonicalPath(
            repository
        );

        RecapPlannerConfigInitializeResult result =
            RecapPlannerConfigInitializer.Initialize(
                repository,
                CreateDocument(),
                new RecapPlannerConfigInitializerTestHooks(
                    (point, _) => {
                        if (point == failurePoint) {
                            throw new IOException("injected barrier failure");
                        }
                    }
                )
            );

        var unavailable = Assert.IsType<
            RecapPlannerConfigInitializeResult.Unavailable
        >(result);
        Assert.Contains("injected barrier failure", unavailable.Reason);
        Assert.Equal(configWasPublished, File.Exists(path));

        RecapPlannerConfigInitializeResult retry =
            RecapPlannerConfigInitializer.Initialize(
                repository,
                CreateDocument()
            );
        if (configWasPublished) {
            Assert.IsType<
                RecapPlannerConfigInitializeResult.AlreadyExists
            >(retry);
        }
        else {
            Assert.IsType<
                RecapPlannerConfigInitializeResult.Initialized
            >(retry);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InitializerCollisionRequiresWinnerDirectoryBarrier(
        bool failBarrier
    ) {
        if (!OperatingSystem.IsLinux()) {
            return;
        }
        string repository = CreateRepositoryDirectory(
            $"collision-barrier-{failBarrier}"
        );
        string path = RecapPlannerConfigLoader.GetCanonicalPath(
            repository
        );
        var observed = new List<RecapPlannerConfigInitializeIoPoint>();
        bool winnerPublished = false;

        RecapPlannerConfigInitializeResult result =
            RecapPlannerConfigInitializer.Initialize(
                repository,
                CreateDocument(),
                new RecapPlannerConfigInitializerTestHooks(
                    (point, _) => {
                        observed.Add(point);
                        if (point == RecapPlannerConfigInitializeIoPoint
                                .TemporaryFileBarrier
                            && !winnerPublished) {
                            PublishWinnerWithoutDirectoryBarrier(path);
                            winnerPublished = true;
                        }
                        if (point == RecapPlannerConfigInitializeIoPoint
                                .ConfigDirectoryBarrier
                            && failBarrier) {
                            throw new IOException(
                                "injected collision barrier failure"
                            );
                        }
                    }
                )
            );

        Assert.True(winnerPublished);
        Assert.Contains(
            RecapPlannerConfigInitializeIoPoint.ConfigDirectoryBarrier,
            observed
        );
        if (failBarrier) {
            var unavailable = Assert.IsType<
                RecapPlannerConfigInitializeResult.Unavailable
            >(result);
            Assert.Contains(
                "injected collision barrier failure",
                unavailable.Reason
            );
        }
        else {
            Assert.IsType<
                RecapPlannerConfigInitializeResult.AlreadyExists
            >(result);
        }
    }

    [Fact]
    public void InitializerCollisionRereadUnavailableIsNotAlreadyExists() {
        if (!OperatingSystem.IsLinux()) {
            return;
        }
        string repository = CreateRepositoryDirectory(
            "collision-reread-unavailable"
        );
        string path = RecapPlannerConfigLoader.GetCanonicalPath(
            repository
        );

        RecapPlannerConfigInitializeResult result =
            RecapPlannerConfigInitializer.Initialize(
                repository,
                CreateDocument(),
                new RecapPlannerConfigInitializerTestHooks(
                    (point, _) => {
                        if (point == RecapPlannerConfigInitializeIoPoint
                                .TemporaryFileBarrier) {
                            PublishWinnerWithoutDirectoryBarrier(path);
                        }
                    },
                    collisionPath => File.Delete(collisionPath)
                )
            );

        var unavailable = Assert.IsType<
            RecapPlannerConfigInitializeResult.Unavailable
        >(result);
        Assert.Contains("missing", unavailable.Reason);
    }

    [Fact]
    public void InitializerRejectsUnsafeConfigDirectoryWithoutWriting() {
        string repository =
            CreateRepositoryDirectory("initialize-link");
        string external = Path.Combine(_tempRoot, "external-config");
        Directory.CreateDirectory(external);
        Directory.CreateSymbolicLink(
            Path.Combine(repository, "config"),
            external
        );

        AssertInitializeInvalidCode(
            RecapPlannerConfigInitializer.Initialize(
                repository,
                CreateDocument()
            ),
            RecapPlannerConfigDefectCodes.UnsafePath
        );
        Assert.Empty(Directory.EnumerateFileSystemEntries(external));
    }

    public static TheoryData<string, string> InvalidJsonDocuments() {
        string valid = ValidJson();
        return new TheoryData<string, string> {
            {
                valid.Replace(
                    "\"planningPolicy\":\"bounded-maintain-all-v1\",",
                    "\"planningPolicy\":\"bounded-maintain-all-v1\","
                    + "\"planningPolicy\":\"duplicate\",",
                    StringComparison.Ordinal
                ),
                RecapPlannerConfigDefectCodes.Malformed
            },
            {
                valid.Replace(
                    "\"planningPolicy\":\"bounded-maintain-all-v1\",",
                    "\"planningPolicy\":\"bounded-maintain-all-v1\","
                    + "\"unknown\":1,",
                    StringComparison.Ordinal
                ),
                RecapPlannerConfigDefectCodes.Malformed
            },
            {
                valid.Replace(
                    "\"planningPolicy\":\"bounded-maintain-all-v1\",",
                    string.Empty,
                    StringComparison.Ordinal
                ),
                RecapPlannerConfigDefectCodes.Malformed
            },
            {
                valid.Replace(
                    "\"schema\":\"atelia.session-journal.recap-planner-config.v2\"",
                    "\"schema\":\"unsupported\"",
                    StringComparison.Ordinal
                ),
                RecapPlannerConfigDefectCodes.UnsupportedSchema
            },
            {
                valid.Replace(
                    "\"recapBuildIntervalHistoryLoad\":21000",
                    "\"recapBuildIntervalHistoryLoad\":9223372036854775808",
                    StringComparison.Ordinal
                ),
                RecapPlannerConfigDefectCodes.InvalidLimit
            },
            {
                valid.Replace(
                    "\"maxRawGrowthEventCount\":512",
                    "\"maxRawGrowthEventCount\":0",
                    StringComparison.Ordinal
                ),
                RecapPlannerConfigDefectCodes.InvalidLimit
            },
            {
                valid.Replace(
                    "\"autobiographical-rewrite\"",
                    "\"world-understanding-rewrite\"",
                    StringComparison.Ordinal
                ),
                RecapPlannerConfigDefectCodes.DuplicateProfileName
            },
            {
                valid.Replace("}", "},", StringComparison.Ordinal),
                RecapPlannerConfigDefectCodes.Malformed
            },
            {
                "/* comment */" + valid,
                RecapPlannerConfigDefectCodes.Malformed
            }
        };
    }

    private string CreateRepositoryDirectory(string name) {
        string path = Path.Combine(_tempRoot, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void PublishWinnerWithoutDirectoryBarrier(
        string path
    ) {
        byte[] canonical = RecapPlannerConfigCodec.EncodeCanonical(
            CreateDocument()
        );
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan
        );
        stream.Write(canonical);
        stream.Flush(flushToDisk: true);
    }

    private static RecapPlannerConfigDocument CreateDocument() => new(
        RecapPlannerConfigCodec.SchemaV2,
        "bounded-maintain-all-v1",
        new RecapCadenceConfigDocument(
            O200kBaseHistoryUnitLoadEstimator.EstimatorId,
            MinimumRecentHistoryLoad: 18_000,
            RecapBuildIntervalHistoryLoad: 21_000
        ),
        [
            new RecapPlannerCatalogEntryDocument(
                "world-understanding-rewrite",
                32_768
            ),
            new RecapPlannerCatalogEntryDocument(
                "autobiographical-rewrite",
                16_384
            )
        ],
        new RecapPlannerLimitsDocument(
            MaxRawGrowthEventCount: 512,
            MaxRouteEndpointsPerBlock: 4,
            MaxMaintainerCallsPerBuild: 8,
            MaxRawEventsPerStep: 64,
            MaxRawEventsPerBuild: 512
        )
    );

    private static string ValidJson() =>
        Encoding.UTF8.GetString(
            RecapPlannerConfigCodec.EncodeCanonical(CreateDocument())
        );

    private static void AssertEncodeInvalidLimit(
        RecapPlannerConfigDocument document
    ) {
        InvalidDataException failure =
            Assert.Throws<InvalidDataException>(() =>
                RecapPlannerConfigCodec.EncodeCanonical(document)
            );
        Assert.Contains(
            RecapPlannerConfigDefectCodes.InvalidLimit,
            failure.Message,
            StringComparison.Ordinal
        );
    }

    private static void AssertInvalidCode(
        RecapPlannerConfigLoadResult result,
        string expectedCode
    ) {
        var invalid =
            Assert.IsType<RecapPlannerConfigLoadResult.Invalid>(
                result
            );
        Assert.Contains(
            invalid.Defects,
            defect => defect.Code == expectedCode
        );
    }

    private static void AssertInitializeInvalidCode(
        RecapPlannerConfigInitializeResult result,
        string expectedCode
    ) {
        var invalid =
            Assert.IsType<RecapPlannerConfigInitializeResult.Invalid>(
                result
            );
        Assert.Contains(
            invalid.Defects,
            defect => defect.Code == expectedCode
        );
    }
}
