using System.Text;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Planner.Tests;

public sealed class RecapPlannerConfigV2CodecTests {
    [Fact]
    public void CanonicalRoundTripPreservesOrderAndHash() {
        RecapPlannerConfigV2Document document = CreateDocument();

        byte[] canonical =
            RecapPlannerConfigV2Codec.EncodeCanonical(document);
        string expected =
            "{\"schema\":\"atelia.session-journal.recap-planner-config.v2\","
            + "\"planningPolicy\":\"bounded-maintain-all-v1\","
            + "\"cadence\":{"
            + "\"historyUnitLoadEstimatorId\":"
            + "\"atelia.history-load.o200k-base.history-unit-v1\","
            + "\"minimumRecentHistoryLoad\":100000,"
            + "\"recapBuildIntervalHistoryLoad\":120000},"
            + "\"catalog\":["
            + "{\"maintainerProfile\":\"world\","
            + "\"maxContentUtf8Bytes\":32768},"
            + "{\"maintainerProfile\":\"self\","
            + "\"maxContentUtf8Bytes\":16384}],"
            + "\"limits\":{\"maxRawGrowthEventCount\":512,"
            + "\"maxRouteEndpointsPerBlock\":4,"
            + "\"maxMaintainerCallsPerBuild\":8,"
            + "\"maxRawEventsPerStep\":64,"
            + "\"maxRawEventsPerBuild\":512}}";

        Assert.Equal(expected, Encoding.UTF8.GetString(canonical));
        var valid = Assert.IsType<
            RecapPlannerConfigV2DecodeResult.Valid
        >(RecapPlannerConfigV2Codec.Decode(canonical));
        Assert.Equal(document.Schema, valid.Document.Schema);
        Assert.Equal(
            document.PlanningPolicy,
            valid.Document.PlanningPolicy
        );
        Assert.Equal(document.Cadence, valid.Document.Cadence);
        Assert.Equal(document.Catalog, valid.Document.Catalog);
        Assert.Equal(document.Limits, valid.Document.Limits);
        Assert.Equal(canonical, valid.CanonicalBytes);
        Assert.Equal(
            RecapPlannerConfigV2Codec.ComputeSha256(canonical),
            valid.ConfigSha256
        );

        string reordered =
            "{\"limits\":{\"maxRawEventsPerBuild\":512,"
            + "\"maxRawEventsPerStep\":64,"
            + "\"maxMaintainerCallsPerBuild\":8,"
            + "\"maxRouteEndpointsPerBlock\":4,"
            + "\"maxRawGrowthEventCount\":512},"
            + "\"catalog\":[{\"maxContentUtf8Bytes\":32768,"
            + "\"maintainerProfile\":\"world\"},"
            + "{\"maxContentUtf8Bytes\":16384,"
            + "\"maintainerProfile\":\"self\"}],"
            + "\"cadence\":{\"recapBuildIntervalHistoryLoad\":120000,"
            + "\"minimumRecentHistoryLoad\":100000,"
            + "\"historyUnitLoadEstimatorId\":"
            + "\"atelia.history-load.o200k-base.history-unit-v1\"},"
            + "\"planningPolicy\":\"bounded-maintain-all-v1\","
            + "\"schema\":\"atelia.session-journal.recap-planner-config.v2\"}";
        var normalized = Assert.IsType<
            RecapPlannerConfigV2DecodeResult.Valid
        >(Decode(reordered));
        Assert.Equal(canonical, normalized.CanonicalBytes);
        Assert.Equal(valid.ConfigSha256, normalized.ConfigSha256);
    }

    [Theory]
    [MemberData(nameof(StrictShapeDefects))]
    public void UnknownMissingAndDuplicatePropertiesFailStrict(
        string json
    ) {
        RecapPlannerConfigV2DecodeResult.Invalid invalid =
            Assert.IsType<RecapPlannerConfigV2DecodeResult.Invalid>(
                Decode(json)
            );

        Assert.Equal(
            RecapPlannerConfigDefectCodes.Malformed,
            Assert.Single(invalid.Defects).Code
        );
    }

    public static TheoryData<string> StrictShapeDefects => new() {
        CanonicalJson().Replace(
            "\"planningPolicy\":\"bounded-maintain-all-v1\",",
            string.Empty,
            StringComparison.Ordinal
        ),
        CanonicalJson().Replace(
            "\"planningPolicy\":\"bounded-maintain-all-v1\",",
            "\"planningPolicy\":\"bounded-maintain-all-v1\","
            + "\"unknown\":true,",
            StringComparison.Ordinal
        ),
        CanonicalJson().Replace(
            "\"schema\":\"atelia.session-journal.recap-planner-config.v2\",",
            "\"schema\":\"atelia.session-journal.recap-planner-config.v2\","
            + "\"schema\":\"atelia.session-journal.recap-planner-config.v2\",",
            StringComparison.Ordinal
        ),
        CanonicalJson().Replace(
            "\"minimumRecentHistoryLoad\":100000,",
            string.Empty,
            StringComparison.Ordinal
        ),
        CanonicalJson().Replace(
            "\"minimumRecentHistoryLoad\":100000,",
            "\"minimumRecentHistoryLoad\":100000,"
            + "\"unexpected\":0,",
            StringComparison.Ordinal
        ),
        CanonicalJson().Replace(
            "\"minimumRecentHistoryLoad\":100000,",
            "\"minimumRecentHistoryLoad\":100000,"
            + "\"minimumRecentHistoryLoad\":100000,",
            StringComparison.Ordinal
        )
    };

    [Theory]
    [InlineData("1.0")]
    [InlineData("1e3")]
    [InlineData("9223372036854775808")]
    [InlineData("-9223372036854775809")]
    public void CadenceNumbersRequireExactInt64(string literal) {
        string json = CanonicalJson().Replace(
            "100000",
            literal,
            StringComparison.Ordinal
        );

        RecapPlannerConfigV2DecodeResult.Invalid invalid =
            Assert.IsType<RecapPlannerConfigV2DecodeResult.Invalid>(
                Decode(json)
            );

        Assert.Equal(
            RecapPlannerConfigDefectCodes.InvalidLimit,
            Assert.Single(invalid.Defects).Code
        );
    }

    [Fact]
    public void Int64BoundaryAndCrossUnitSeparationAreAccepted() {
        RecapPlannerConfigV2Document document = CreateDocument() with {
            Cadence = new RecapCadenceConfigV2Document(
                O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                long.MaxValue - 1,
                1
            ),
            Limits = CreateDocument().Limits with {
                MaxRawGrowthEventCount = 1
            }
        };

        byte[] canonical =
            RecapPlannerConfigV2Codec.EncodeCanonical(document);
        var valid = Assert.IsType<
            RecapPlannerConfigV2DecodeResult.Valid
        >(RecapPlannerConfigV2Codec.Decode(canonical));

        Assert.Equal(long.MaxValue - 1, valid.Document.Cadence
            .MinimumRecentHistoryLoad);
        Assert.Equal(1, valid.Document.Limits
            .MaxRawGrowthEventCount);
    }

    [Fact]
    public void CadenceRangeAndCheckedSumAreValidated() {
        AssertInvalidLimit(CreateDocument() with {
            Cadence = CreateDocument().Cadence with {
                MinimumRecentHistoryLoad = -1
            }
        });
        AssertInvalidLimit(CreateDocument() with {
            Cadence = CreateDocument().Cadence with {
                RecapBuildIntervalHistoryLoad = 0
            }
        });
        AssertInvalidLimit(CreateDocument() with {
            Cadence = CreateDocument().Cadence with {
                MinimumRecentHistoryLoad = long.MaxValue,
                RecapBuildIntervalHistoryLoad = 1
            }
        });
        AssertInvalidLimit(CreateDocument() with {
            Cadence = CreateDocument().Cadence with {
                HistoryUnitLoadEstimatorId = " "
            }
        });
    }

    [Fact]
    public void CatalogAndRawLimitsRetainV1ShapeValidation() {
        RecapPlannerConfigV2Document source = CreateDocument();
        AssertInvalid(
            source with {
                Catalog = Array.AsReadOnly([
                    source.Catalog[0],
                    source.Catalog[0]
                ])
            },
            RecapPlannerConfigDefectCodes.DuplicateProfileName
        );
        AssertInvalidLimit(source with {
            Limits = source.Limits with {
                MaxRawGrowthEventCount = 0
            }
        });
    }

    [Fact]
    public void DocumentSizeCapIsCheckedBeforeJsonParsing() {
        byte[] oversized = new byte[
            RecapPlannerConfigV2Codec.MaxDocumentUtf8Bytes + 1
        ];

        RecapPlannerConfigV2DecodeResult.Invalid invalid =
            Assert.IsType<RecapPlannerConfigV2DecodeResult.Invalid>(
                RecapPlannerConfigV2Codec.Decode(oversized)
            );

        Assert.Equal(
            RecapPlannerConfigDefectCodes.SizeLimitExceeded,
            Assert.Single(invalid.Defects).Code
        );
    }

    [Fact]
    public void ProductionV1LoaderStillRejectsCanonicalV2() {
        string repository = Path.Combine(
            Path.GetTempPath(),
            "atelia-recap-config-v2-inactive-tests",
            Guid.NewGuid().ToString("N")
        );
        try {
            Directory.CreateDirectory(repository);
            string path =
                RecapPlannerConfigLoader.GetCanonicalPath(repository);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(
                path,
                RecapPlannerConfigV2Codec.EncodeCanonical(
                    CreateDocument()
                )
            );

            var invalid = Assert.IsType<
                RecapPlannerConfigLoadResult.Invalid
            >(RecapPlannerConfigLoader.Load(repository));
            Assert.Equal(
                RecapPlannerConfigDefectCodes.UnsupportedSchema,
                Assert.Single(invalid.Defects).Code
            );
        }
        finally {
            if (Directory.Exists(repository)) {
                Directory.Delete(repository, recursive: true);
            }
        }
    }

    private static RecapPlannerConfigV2Document CreateDocument()
        => new(
            RecapPlannerConfigV2Codec.SchemaV2,
            "bounded-maintain-all-v1",
            new RecapCadenceConfigV2Document(
                O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                MinimumRecentHistoryLoad: 100_000,
                RecapBuildIntervalHistoryLoad: 120_000
            ),
            Array.AsReadOnly([
                new RecapPlannerCatalogEntryDocument("world", 32_768),
                new RecapPlannerCatalogEntryDocument("self", 16_384)
            ]),
            new RecapPlannerLimitsDocument(
                MaxRawGrowthEventCount: 512,
                MaxRouteEndpointsPerBlock: 4,
                MaxMaintainerCallsPerBuild: 8,
                MaxRawEventsPerStep: 64,
                MaxRawEventsPerBuild: 512
            )
        );

    private static string CanonicalJson() => Encoding.UTF8.GetString(
        RecapPlannerConfigV2Codec.EncodeCanonical(CreateDocument())
    );

    private static RecapPlannerConfigV2DecodeResult Decode(
        string json
    ) => RecapPlannerConfigV2Codec.Decode(
        Encoding.UTF8.GetBytes(json)
    );

    private static void AssertInvalidLimit(
        RecapPlannerConfigV2Document document
    ) => AssertInvalid(
        document,
        RecapPlannerConfigDefectCodes.InvalidLimit
    );

    private static void AssertInvalid(
        RecapPlannerConfigV2Document document,
        string expectedCode
    ) {
        InvalidDataException failure =
            Assert.Throws<InvalidDataException>(() =>
                RecapPlannerConfigV2Codec.EncodeCanonical(document)
            );
        Assert.Contains(
            expectedCode,
            failure.Message,
            StringComparison.Ordinal
        );
    }
}
