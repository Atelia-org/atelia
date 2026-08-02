using System.Text;
using Atelia.Data;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Store.Tests;

public sealed class DerivedRecapCodecTests {
    private static readonly EventAddress A1 = new(
        SizedPtr.FromPacked(0x0102_0304_0506_0708),
        0x0a0b_0c0d,
        new AddressHint(0x0102_0304)
    );

    private static readonly EventAddress A2 = new(
        SizedPtr.FromPacked(0x1112_1314_1516_1718),
        0x1a1b_1c1d,
        AddressHint.None
    );

    [Fact]
    public void EventAddressFileNameCodec_UsesBinaryLittleEndianHex() {
        const string expected =
            "08070605040302010d0c0b0a04030201";

        string encoded = EventAddressFileNameCodec.Format(A1);

        Assert.Equal(expected, encoded);
        Assert.Equal(A1, EventAddressFileNameCodec.Parse(encoded));
        Assert.False(
            EventAddressFileNameCodec.TryParse(
                encoded.ToUpperInvariant(),
                out _
            )
        );
    }

    [Theory]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("RolePlay")]
    [InlineData(".hidden")]
    [InlineData("slash/value")]
    public void RecapBlockId_RejectsUnsafeTokens(string value) {
        Assert.Throws<ArgumentException>(() => new RecapBlockId(value));
    }

    [Fact]
    public void ManifestCodec_HasStableCanonicalPropertyOrderAndRoundTrips() {
        var refId = new RefId(0x1234);
        RecapBlockPlan plan = new MaintainRecapBlockPlan(
            new RecapBlockId("roleplay.self"),
            new ContextHeaderBlockPath(
                ContextHeaderCarrier.System,
                "roleplay.self"
            ),
            "roleplay.autobiographical",
            RecapTestIdentity.CapabilityFingerprint,
            new EmptyRecapMaintainSource(A1),
            [A2],
            EmptyRecapPriorContext.Instance
        );
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(refId, A2, [plan]);

        string actual = Encoding.UTF8.GetString(
            DerivedRecapCodec.EncodeManifest(manifest)
        );
        string expected =
            "{\"schema\":\""
            + DerivedRecapCodec.ManifestSchema
            + "\",\"refId\":\"0000000000001234\","
            + "\"setAdmissionAnchor\":\""
            + EventAddressTextCodec.Format(A2)
            + "\",\"blocks\":[{\"mode\":\"maintain\","
            + "\"recapBlockId\":\"roleplay.self\","
            + "\"target\":{\"carrier\":\"system\","
            + "\"blockKey\":\"roleplay.self\"},"
            + "\"maintainerId\":\"roleplay.autobiographical\","
            + "\"maintainerCapabilityFingerprint\":\""
            + RecapTestIdentity.CapabilityFingerprint
            + "\","
            + "\"source\":{\"kind\":\"empty\","
            + "\"replayStartExclusive\":\""
            + EventAddressTextCodec.Format(A1)
            + "\"},\"catchUpThrough\":[\""
            + EventAddressTextCodec.Format(A2)
            + "\"],\"priorContext\":{\"kind\":\"empty\"},"
            + "\"maxContentUtf8Bytes\":262144}],"
            + "\"manifestPayloadSha256\":\""
            + manifest.ManifestPayloadSha256
            + "\"}";

        Assert.Equal(expected, actual);
        DerivedRecapSetManifest decoded =
            DerivedRecapCodec.DecodeManifest(
                Encoding.UTF8.GetBytes(actual)
            );
        Assert.Equal(
            actual,
            Encoding.UTF8.GetString(
                DerivedRecapCodec.EncodeManifest(decoded)
            )
        );
    }

    [Fact]
    public void MaintainCapabilityFingerprint_IsRequiredAndHashBound() {
        MaintainRecapBlockPlan first = MaintainPlan(
            RecapTestIdentity.CapabilityFingerprint
        );
        MaintainRecapBlockPlan second = MaintainPlan(
            "sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"
        );

        Assert.NotEqual(
            DerivedRecapCodec.ComputeBlockPlanSha256(first),
            DerivedRecapCodec.ComputeBlockPlanSha256(second)
        );
        Assert.NotEqual(
            DerivedRecapCodec.CreateManifest(
                new RefId(8),
                A2,
                [first]
            ).ManifestPayloadSha256,
            DerivedRecapCodec.CreateManifest(
                new RefId(8),
                A2,
                [second]
            ).ManifestPayloadSha256
        );

        foreach (string invalid in new[] {
            "",
            new string('0', 64),
            "sha256:" + new string('A', 64),
            "sha256:" + new string('0', 63)
        }) {
            Assert.Throws<InvalidDataException>(() =>
                DerivedRecapCodec.CreateManifest(
                    new RefId(8),
                    A2,
                    [MaintainPlan(invalid)]
                )
            );
        }
    }

    [Fact]
    public void DurableV5_RejectsV4AndFingerprintFieldMutation() {
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                new RefId(9),
                A2,
                [MaintainPlan(
                    RecapTestIdentity.CapabilityFingerprint
                )]
            );
        string json = Encoding.UTF8.GetString(
            DerivedRecapCodec.EncodeManifest(manifest)
        );

        string oldSchema = json.Replace(
            DerivedRecapCodec.ManifestSchema,
            "atelia.session-journal.derived-recap-manifest.v4",
            StringComparison.Ordinal
        );
        Assert.Throws<NotSupportedException>(() =>
            DerivedRecapCodec.DecodeManifest(
                Encoding.UTF8.GetBytes(oldSchema)
            )
        );

        string missing = json.Replace(
            "\"maintainerCapabilityFingerprint\":\""
            + RecapTestIdentity.CapabilityFingerprint
            + "\",",
            string.Empty,
            StringComparison.Ordinal
        );
        Assert.Throws<InvalidDataException>(() =>
            DerivedRecapCodec.DecodeManifest(
                Encoding.UTF8.GetBytes(missing)
            )
        );

        string duplicate = json.Replace(
            "\"maintainerCapabilityFingerprint\":",
            "\"maintainerCapabilityFingerprint\":\""
            + RecapTestIdentity.CapabilityFingerprint
            + "\",\"maintainerCapabilityFingerprint\":",
            StringComparison.Ordinal
        );
        Assert.Throws<InvalidDataException>(() =>
            DerivedRecapCodec.DecodeManifest(
                Encoding.UTF8.GetBytes(duplicate)
            )
        );

        RecapBlockPlan plan = manifest.Blocks[0];
        PublishedRecapSet publication =
            DerivedRecapCodec.CreatePublication(
                manifest,
                [DerivedRecapCodec.CreateBlock(plan, A2, "recap")]
            );
        string publicationJson = Encoding.UTF8.GetString(
            DerivedRecapCodec.EncodePublication(publication)
        );
        string oldPublicationSchema = publicationJson.Replace(
            DerivedRecapCodec.PublicationSchema,
            "atelia.session-journal.published-recap-set.v4",
            StringComparison.Ordinal
        );
        Assert.Throws<NotSupportedException>(() =>
            DerivedRecapCodec.DecodePublication(
                Encoding.UTF8.GetBytes(oldPublicationSchema)
            )
        );
    }

    private static MaintainRecapBlockPlan MaintainPlan(
        string capabilityFingerprint
    ) => new(
        new RecapBlockId("roleplay.fingerprint"),
        new ContextHeaderBlockPath(
            ContextHeaderCarrier.System,
            "roleplay.fingerprint"
        ),
        "roleplay.fingerprint",
        capabilityFingerprint,
        new EmptyRecapMaintainSource(A1),
        [A2],
        EmptyRecapPriorContext.Instance
    );

    [Fact]
    public void ManifestCodec_RejectsSemanticMutationUnknownAndDuplicate() {
        RecapBlockPlan plan = new MaintainRecapBlockPlan(
            new RecapBlockId("roleplay.world"),
            new ContextHeaderBlockPath(
                ContextHeaderCarrier.Observation,
                "roleplay.world"
            ),
            "roleplay.world",
            RecapTestIdentity.CapabilityFingerprint,
            new EmptyRecapMaintainSource(A1),
            [A2],
            EmptyRecapPriorContext.Instance
        );
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                new RefId(7),
                A2,
                [plan]
            );
        string json = Encoding.UTF8.GetString(
            DerivedRecapCodec.EncodeManifest(manifest)
        );

        string mutated = json.Replace(
            EventAddressTextCodec.Format(A1),
            EventAddressTextCodec.Format(A2),
            StringComparison.Ordinal
        );
        Assert.Throws<InvalidDataException>(() =>
            DerivedRecapCodec.DecodeManifest(
                Encoding.UTF8.GetBytes(mutated)
            )
        );

        string unknown = json.Replace(
            "\"blocks\":",
            "\"unknown\":0,\"blocks\":",
            StringComparison.Ordinal
        );
        Assert.Throws<InvalidDataException>(() =>
            DerivedRecapCodec.DecodeManifest(
                Encoding.UTF8.GetBytes(unknown)
            )
        );

        string duplicate = json.Replace(
            "{\"schema\":",
            "{\"schema\":\"duplicate\",\"schema\":",
            StringComparison.Ordinal
        );
        Assert.Throws<InvalidDataException>(() =>
            DerivedRecapCodec.DecodeManifest(
                Encoding.UTF8.GetBytes(duplicate)
            )
        );
    }

    [Fact]
    public void AllPayloadCodecsRejectValidJsonHashMutation() {
        var target = new ContextHeaderBlockPath(
            ContextHeaderCarrier.Action,
            "roleplay.action"
        );
        var id = new RecapBlockId("roleplay.action");
        DerivedRecapFrozenInput input =
            DerivedRecapCodec.CreateFrozenInput(
                id,
                target,
                A1,
                "old"
            );
        var plan = new InheritRecapBlockPlan(
            id,
            target,
            A1,
            new string('1', 64),
            input.PayloadSha256
        );
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                new RefId(9),
                A2,
                [plan]
            );
        DerivedRecapBlock block =
            DerivedRecapCodec.CreateBlock(plan, A1, "old");
        PublishedRecapSet publication =
            DerivedRecapCodec.CreatePublication(manifest, [block]);

        AssertHashMutationRejected(
            DerivedRecapCodec.EncodeFrozenInput(input),
            "\"old\"",
            "\"new\"",
            static bytes =>
                DerivedRecapCodec.DecodeFrozenInput(bytes)
        );
        AssertHashMutationRejected(
            DerivedRecapCodec.EncodeBlock(block),
            "\"old\"",
            "\"new\"",
            static bytes =>
                DerivedRecapCodec.DecodeBlock(bytes)
        );
        AssertHashMutationRejected(
            DerivedRecapCodec.EncodePublication(publication),
            "\"old-never-present\"",
            "\"new\"",
            static bytes =>
                DerivedRecapCodec.DecodePublication(bytes),
            replaceFallback: (
                EventAddressTextCodec.Format(A1),
                EventAddressTextCodec.Format(A2)
            )
        );
    }

    [Theory]
    [InlineData("leading-whitespace")]
    [InlineData("trailing-whitespace")]
    [InlineData("inter-property-whitespace")]
    [InlineData("escaped-string")]
    [InlineData("utf8-bom")]
    public void PublicationCodecRejectsEquivalentNonCanonicalBytes(
        string mutation
    ) {
        var target = new ContextHeaderBlockPath(
            ContextHeaderCarrier.System,
            "roleplay.self"
        );
        var id = new RecapBlockId("roleplay.self");
        RecapBlockPlan plan = new MaintainRecapBlockPlan(
            id,
            target,
            "roleplay.autobiographical",
            RecapTestIdentity.CapabilityFingerprint,
            new EmptyRecapMaintainSource(A1),
            [A2],
            EmptyRecapPriorContext.Instance
        );
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                new RefId(12),
                A2,
                [plan]
            );
        PublishedRecapSet publication =
            DerivedRecapCodec.CreatePublication(
                manifest,
                [DerivedRecapCodec.CreateBlock(plan, A2, "recap")]
            );
        byte[] canonical =
            DerivedRecapCodec.EncodePublication(publication);
        string json = Encoding.UTF8.GetString(canonical);
        byte[] mutated = mutation switch {
            "leading-whitespace" => Encoding.UTF8.GetBytes(" " + json),
            "trailing-whitespace" => Encoding.UTF8.GetBytes(json + "\n"),
            "inter-property-whitespace" => Encoding.UTF8.GetBytes(
                json.Replace(
                    ",\"refId\"",
                    ", \"refId\"",
                    StringComparison.Ordinal
                )
            ),
            "escaped-string" => Encoding.UTF8.GetBytes(
                json.Replace(
                    "roleplay.self",
                    "roleplay\\u002eself",
                    StringComparison.Ordinal
                )
            ),
            "utf8-bom" => [0xef, 0xbb, 0xbf, .. canonical],
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };

        Assert.NotEqual(canonical, mutated);
        Assert.Throws<InvalidDataException>(() =>
            DerivedRecapCodec.DecodePublication(mutated)
        );
    }

    private static void AssertHashMutationRejected<T>(
        byte[] bytes,
        string oldValue,
        string newValue,
        Func<byte[], T> decode,
        (string Old, string New)? replaceFallback = null
    ) {
        string json = Encoding.UTF8.GetString(bytes);
        string mutated = json.Replace(
            oldValue,
            newValue,
            StringComparison.Ordinal
        );
        if (string.Equals(json, mutated, StringComparison.Ordinal)
            && replaceFallback is { } fallback) {
            mutated = json.Replace(
                fallback.Old,
                fallback.New,
                StringComparison.Ordinal
            );
        }
        Assert.NotEqual(json, mutated);
        Assert.Throws<InvalidDataException>(() =>
            decode(Encoding.UTF8.GetBytes(mutated))
        );
    }
}
