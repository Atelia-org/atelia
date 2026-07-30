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
    public void CodecCanonicalizesExactV1DocumentAndPreservesCatalogOrder() {
        RecapPlannerConfigDocument document = CreateDocument();

        byte[] canonical =
            RecapPlannerConfigCodec.EncodeCanonical(document);

        const string expected =
            "{\"schema\":\"atelia.session-journal.recap-planner-config.v1\","
            + "\"planningPolicy\":\"bounded-maintain-all-v1\","
            + "\"cadence\":{\"minimumRecentHistoryUnitCount\":20,"
            + "\"recapBuildIntervalUnitCount\":24},"
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
            + "\"cadence\":{\"recapBuildIntervalUnitCount\":24,"
            + "\"minimumRecentHistoryUnitCount\":20},"
            + "\"planningPolicy\":\"bounded-maintain-all-v1\","
            + "\"schema\":\"atelia.session-journal.recap-planner-config.v1\""
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
                    "\"schema\":\"atelia.session-journal.recap-planner-config.v1\"",
                    "\"schema\":\"unsupported\"",
                    StringComparison.Ordinal
                ),
                RecapPlannerConfigDefectCodes.UnsupportedSchema
            },
            {
                valid.Replace(
                    "\"recapBuildIntervalUnitCount\":24",
                    "\"recapBuildIntervalUnitCount\":2147483648",
                    StringComparison.Ordinal
                ),
                RecapPlannerConfigDefectCodes.InvalidLimit
            },
            {
                valid.Replace(
                    "\"maxRawGrowthEventCount\":512",
                    "\"maxRawGrowthEventCount\":43",
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

    private static RecapPlannerConfigDocument CreateDocument() => new(
        RecapPlannerConfigCodec.SchemaV1,
        "bounded-maintain-all-v1",
        new RecapCadenceConfigDocument(20, 24),
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
