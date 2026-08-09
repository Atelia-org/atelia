using System.Text;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Planner.Tests;

public sealed class RecapEpochConfigCodecTests {
    [Fact]
    public void V3RoundTripsCanonicalAndRejectsOldSchemaAndFields() {
        RecapEpochConfigDocument document = CreateDocument();
        byte[] canonical = RecapEpochConfigCodec.Encode(document);
        Assert.Equal(
            canonical,
            RecapEpochConfigCodec.Encode(
                RecapEpochConfigCodec.Decode(canonical)
            )
        );

        string json = Encoding.UTF8.GetString(canonical);
        Assert.Throws<InvalidDataException>(() =>
            RecapEpochConfigCodec.Decode(Encoding.UTF8.GetBytes(
                json.Replace(
                    RecapEpochConfigCodec.SchemaV3,
                    "atelia.session-journal.recap-planner-config.v2",
                    StringComparison.Ordinal
                )
            ))
        );
        Assert.Throws<InvalidDataException>(() =>
            RecapEpochConfigCodec.Decode(Encoding.UTF8.GetBytes(
                json.Replace(
                    "\"maxMaintainerCallsPerEpoch\":2",
                    "\"maxMaintainerCallsPerBuild\":2",
                    StringComparison.Ordinal
                )
            ))
        );
        Assert.Throws<InvalidDataException>(() =>
            RecapEpochConfigCodec.Decode(Encoding.UTF8.GetBytes(
                json.Replace(
                    "\"maxRawEventsPerEpoch\":512",
                    "\"maxRouteEndpointsPerBlock\":4",
                    StringComparison.Ordinal
                )
            ))
        );
    }

    [Fact]
    public void DecodeRejectsNonCanonicalPropertyOrder() {
        string json = Encoding.UTF8.GetString(
            RecapEpochConfigCodec.Encode(CreateDocument())
        );
        string reordered = json.Replace(
            "\"schema\":\"atelia.session-journal.recap-epoch-config.v3\",\"planningPolicy\":\"maintain-complete-roster-epoch-v1\"",
            "\"planningPolicy\":\"maintain-complete-roster-epoch-v1\",\"schema\":\"atelia.session-journal.recap-epoch-config.v3\"",
            StringComparison.Ordinal
        );
        Assert.NotEqual(json, reordered);
        Assert.Throws<InvalidDataException>(() =>
            RecapEpochConfigCodec.Decode(Encoding.UTF8.GetBytes(reordered))
        );
    }

    [Fact]
    public void MissingRepositoryConfigDoesNotCreateAnyPath() {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"atelia-recap-config-missing-{Guid.NewGuid():N}"
        );

        Assert.False(RecapEpochConfigLoader.TryLoad(root, out _));
        Assert.False(Directory.Exists(root));
    }

    private static RecapEpochConfigDocument CreateDocument() => new(
        RecapEpochConfigCodec.SchemaV3,
        MaintainCompleteRosterEpochPolicy.PolicyId,
        new RecapEpochCadenceConfigDocument("estimator", 18_000, 21_000),
        [new RecapEpochCatalogEntryDocument("profile", 32_768)],
        new RecapEpochLimitsDocument(
            512,
            512,
            2,
            4,
            8,
            2,
            65_536,
            2 * 1024 * 1024,
            5 * 1024 * 1024,
            8 * 1024 * 1024,
            2 * 1024 * 1024,
            512 * 1024,
            3 * 1024 * 1024
        )
    );
}
