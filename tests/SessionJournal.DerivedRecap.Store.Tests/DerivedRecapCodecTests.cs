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

    private static readonly SessionContextAnchorSetupReferences S1 =
        RecapWireTestFacts.SyntheticSetups(A1);

    private static readonly SessionContextAnchorSetupReferences S2 =
        RecapWireTestFacts.SyntheticSetups(A2);

    private const string S1Json =
        "{\"runtimeConfig\":{\"address\":\"ej1:01020304050607080a0b0c0d01020304\","
        + "\"bodySchemaVersion\":1,\"payloadSha256\":\"0000000000000000000000000000000000000000000000000000000000000000\"},"
        + "\"systemPrompt\":{\"address\":\"ej1:01020304050607080a0b0c0d01020304\","
        + "\"bodySchemaVersion\":1,\"payloadSha256\":\"1111111111111111111111111111111111111111111111111111111111111111\"}}";

    private const string S2Json =
        "{\"runtimeConfig\":{\"address\":\"ej1:11121314151617181a1b1c1d00000000\","
        + "\"bodySchemaVersion\":1,\"payloadSha256\":\"0000000000000000000000000000000000000000000000000000000000000000\"},"
        + "\"systemPrompt\":{\"address\":\"ej1:11121314151617181a1b1c1d00000000\","
        + "\"bodySchemaVersion\":1,\"payloadSha256\":\"1111111111111111111111111111111111111111111111111111111111111111\"}}";

    [Fact]
    public void SchemaTokens_AreLiteralWireCommitments() {
        Assert.Equal(
            "atelia.session-journal.derived-recap-store.v4",
            DerivedRecapCodec.StoreSchema
        );
        Assert.Equal(
            "atelia.session-journal.derived-recap-manifest.v7",
            DerivedRecapCodec.ManifestSchema
        );
        Assert.Equal(
            "atelia.session-journal.derived-recap-frozen-input.v5",
            DerivedRecapCodec.FrozenInputSchema
        );
        Assert.Equal(
            "atelia.session-journal.derived-recap-block.v4",
            DerivedRecapCodec.BlockSchema
        );
        Assert.Equal(
            "atelia.session-journal.published-recap-set.v7",
            DerivedRecapCodec.PublicationSchema
        );
    }

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

    [Fact]
    public void InlinePriorIsEncodedOnceAndAllMaintainPlansBindItsDigest() {
        var prior = new InlineRecapPriorContext(
            A1,
            new ContextHeaderSnapshot(
                "shared-system",
                "shared-observation",
                "shared-action"
            )
        );
        string digest =
            DerivedRecapCodec.ComputePriorContextPayloadSha256(prior);
        RecapBlockPlan[] plans = [
            ExistingPlan("shared.one", digest),
            ExistingPlan("shared.two", digest)
        ];

        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                new RefId(0x55),
                A2,
                S2,
                prior,
                plans
            );
        string json = Encoding.UTF8.GetString(
            DerivedRecapCodec.EncodeManifest(manifest)
        );

        Assert.Equal(1, Count(json, "\"snapshot\":"));
        Assert.Equal(
            3,
            Count(json, "\"priorContextPayloadSha256\":")
        );
        Assert.Equal(
            1,
            Count(json, "\"priorContext\":")
        );
        Assert.All(
            manifest.Blocks.OfType<MaintainRecapBlockPlan>(),
            plan => Assert.Equal(
                manifest.PriorContextPayloadSha256,
                plan.PriorContextPayloadSha256
            )
        );
    }

    [Fact]
    public void MaintainDigestMismatchIsRejected() {
        var prior = new InlineRecapPriorContext(
            A1,
            ContextHeaderSnapshot.Empty
        );
        MaintainRecapBlockPlan valid = ExistingPlan(
            "digest.valid",
            DerivedRecapCodec.ComputePriorContextPayloadSha256(prior)
        );
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                new RefId(0x56),
                A2,
                S2,
                prior,
                [valid]
            );
        var mismatched = new MaintainRecapBlockPlan(
            valid.RecapBlockId,
            valid.Target,
            valid.MaintainerId,
            valid.MaintainerCapabilityFingerprint,
            valid.Source,
            valid.CatchUpBoundaries,
            new string('f', 64),
            valid.MaxContentUtf8Bytes
        );

        Assert.Throws<InvalidDataException>(() =>
            DerivedRecapCodec.ValidateManifest(manifest with {
                Blocks = [mismatched]
            })
        );
    }

    [Fact]
    public void ManifestPriorPhaseRulesRejectMixedSeedsAndNonEmptyInheritOnly() {
        var inline = new InlineRecapPriorContext(
            A1,
            ContextHeaderSnapshot.Empty
        );
        string inlineDigest =
            DerivedRecapCodec.ComputePriorContextPayloadSha256(inline);
        var empty = new MaintainRecapBlockPlan(
            new RecapBlockId("phase.empty"),
            new ContextHeaderBlockPath(
                ContextHeaderCarrier.System,
                "phase.empty"
            ),
            "phase.empty",
            RecapTestIdentity.CapabilityFingerprint,
            new EmptyRecapMaintainSource(A1, S1),
            [new RecapReplayBoundary(A2, S2)],
            inlineDigest
        );
        MaintainRecapBlockPlan existing = ExistingPlan(
            "phase.existing",
            inlineDigest
        );
        var inherit = new InheritRecapBlockPlan(
            new RecapBlockId("phase.inherit"),
            new ContextHeaderBlockPath(
                ContextHeaderCarrier.System,
                "phase.inherit"
            ),
            A1,
            S1,
            new string('a', 64),
            new string('b', 64)
        );

        InvalidDataException mixed = Assert.Throws<InvalidDataException>(
            () => DerivedRecapCodec.CreateManifest(
                new RefId(0x58),
                A2,
                S2,
                inline,
                [existing, empty]
            )
        );
        Assert.Contains("cannot mix existing and empty", mixed.Message);

        _ = DerivedRecapCodec.CreateManifest(
            new RefId(0x58),
            A2,
            S2,
            EmptyRecapPriorContext.Instance,
            [inherit]
        );
        Assert.Throws<InvalidDataException>(() =>
            DerivedRecapCodec.CreateManifest(
                new RefId(0x58),
                A2,
                S2,
                inline,
                [inherit]
            )
        );

        _ = DerivedRecapCodec.CreateManifest(
            new RefId(0x58),
            A2,
            S2,
            inline,
            [existing, inherit]
        );
    }

    [Fact]
    public void PriorMutationChangesManifestPlanAndBlockCommitments() {
        var firstPrior = new InlineRecapPriorContext(
            A1,
            new ContextHeaderSnapshot("system-a", "observation", "action")
        );
        var secondPrior = new InlineRecapPriorContext(
            A1,
            new ContextHeaderSnapshot("system-b", "observation", "action")
        );
        MaintainRecapBlockPlan firstPlan = ExistingPlan(
            "commitment.prior",
            DerivedRecapCodec.ComputePriorContextPayloadSha256(firstPrior)
        );
        MaintainRecapBlockPlan secondPlan = ExistingPlan(
            "commitment.prior",
            DerivedRecapCodec.ComputePriorContextPayloadSha256(secondPrior)
        );
        DerivedRecapSetManifest firstManifest =
            DerivedRecapCodec.CreateManifest(
                new RefId(0x57), A2, S2, firstPrior, [firstPlan]
            );
        DerivedRecapSetManifest secondManifest =
            DerivedRecapCodec.CreateManifest(
                new RefId(0x57), A2, S2, secondPrior, [secondPlan]
            );

        Assert.NotEqual(
            firstManifest.ManifestPayloadSha256,
            secondManifest.ManifestPayloadSha256
        );
        Assert.NotEqual(
            DerivedRecapCodec.ComputeBlockPlanSha256(firstPlan),
            DerivedRecapCodec.ComputeBlockPlanSha256(secondPlan)
        );
        Assert.NotEqual(
            DerivedRecapCodec.CreateBlock(firstPlan, A2, "same").PayloadSha256,
            DerivedRecapCodec.CreateBlock(secondPlan, A2, "same").PayloadSha256
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
    public void V7ManifestAndPublication_HaveLiteralCanonicalGoldens() {
        var refId = new RefId(0x1234);
        RecapBlockPlan plan = new MaintainRecapBlockPlan(
            new RecapBlockId("roleplay.self"),
            new ContextHeaderBlockPath(
                ContextHeaderCarrier.System,
                "roleplay.self"
            ),
            "roleplay.autobiographical",
            RecapTestIdentity.CapabilityFingerprint,
            new EmptyRecapMaintainSource(A1, S1),
            [new RecapReplayBoundary(A2, S2)],
            RecapWireTestFacts.PriorDigest(EmptyRecapPriorContext.Instance)
        );
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                refId,
                A2,
                S2,
                EmptyRecapPriorContext.Instance,
                [plan]
            );

        string actual = Encoding.UTF8.GetString(
            DerivedRecapCodec.EncodeManifest(manifest)
        );
        const string ExpectedManifest =
            "{\"schema\":\"atelia.session-journal.derived-recap-manifest.v7\","
            + "\"refId\":\"0000000000001234\","
            + "\"setAdmissionAnchor\":\"ej1:11121314151617181a1b1c1d00000000\","
            + "\"setAdmissionAnchorSetups\":" + S2Json + ","
            + "\"priorContext\":{\"kind\":\"empty\"},"
            + "\"priorContextPayloadSha256\":\"e35b48e84654ad51a5b1ee607e3f203550707c71e3d5017aa212ff3aa1fc578b\","
            + "\"blocks\":[{\"mode\":\"maintain\","
            + "\"recapBlockId\":\"roleplay.self\","
            + "\"target\":{\"carrier\":\"system\",\"blockKey\":\"roleplay.self\"},"
            + "\"maintainerId\":\"roleplay.autobiographical\","
            + "\"maintainerCapabilityFingerprint\":\"sha256:0000000000000000000000000000000000000000000000000000000000000000\","
            + "\"source\":{\"kind\":\"empty\",\"replayStartExclusive\":\"ej1:01020304050607080a0b0c0d01020304\","
            + "\"replayStartSetups\":" + S1Json + "},"
            + "\"catchUpBoundaries\":[{\"address\":\"ej1:11121314151617181a1b1c1d00000000\","
            + "\"setups\":" + S2Json + "}],"
            + "\"priorContextPayloadSha256\":\"e35b48e84654ad51a5b1ee607e3f203550707c71e3d5017aa212ff3aa1fc578b\","
            + "\"maxContentUtf8Bytes\":262144}],"
            + "\"manifestPayloadSha256\":\"d3672ff750ce4122e9fa5b891ad07180e72dbec1c53f2814ffba06b2facd4883\"}";

        Assert.Equal(
            "d3672ff750ce4122e9fa5b891ad07180e72dbec1c53f2814ffba06b2facd4883",
            manifest.ManifestPayloadSha256
        );
        Assert.Equal(ExpectedManifest, actual);
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

        PublishedRecapSet publication =
            DerivedRecapCodec.CreatePublication(
                manifest,
                [DerivedRecapCodec.CreateBlock(plan, A2, "recap")]
            );
        const string ExpectedPublication =
            "{\"schema\":\"atelia.session-journal.published-recap-set.v7\","
            + "\"refId\":\"0000000000001234\","
            + "\"setAdmissionAnchor\":\"ej1:11121314151617181a1b1c1d00000000\","
            + "\"frozenPlanSnapshot\":" + ExpectedManifest + ","
            + "\"blockCommitments\":[{\"recapBlockId\":\"roleplay.self\","
            + "\"target\":{\"carrier\":\"system\",\"blockKey\":\"roleplay.self\"},"
            + "\"absorbedThrough\":\"ej1:11121314151617181a1b1c1d00000000\","
            + "\"payloadSha256\":\"c340abf148dd0c03b7a8d1f4cf77801e349d2228a06e722cbe047a0baac2fec1\"}],"
            + "\"envelopeSha256\":\"d0bdee0ec99e9eb35536a4c4b5100c5535a2cece1ce74c48645b707a64ea1bc1\"}";
        Assert.Equal(
            "d0bdee0ec99e9eb35536a4c4b5100c5535a2cece1ce74c48645b707a64ea1bc1",
            publication.EnvelopeSha256
        );
        Assert.Equal(
            ExpectedPublication,
            Encoding.UTF8.GetString(
                DerivedRecapCodec.EncodePublication(publication)
            )
        );
    }

    [Fact]
    public void V5FrozenInput_HasLiteralCanonicalGolden() {
        DerivedRecapFrozenInput input =
            DerivedRecapCodec.CreateFrozenInput(
                new RecapBlockId("roleplay.action"),
                new ContextHeaderBlockPath(
                    ContextHeaderCarrier.Action,
                    "roleplay.action"
                ),
                A1,
                S1,
                "old"
            );
        string actual = Encoding.UTF8.GetString(
            DerivedRecapCodec.EncodeFrozenInput(input)
        );
        const string Expected =
            "{\"schema\":\"atelia.session-journal.derived-recap-frozen-input.v5\","
            + "\"recapBlockId\":\"roleplay.action\","
            + "\"target\":{\"carrier\":\"action\",\"blockKey\":\"roleplay.action\"},"
            + "\"absorbedThrough\":\"ej1:01020304050607080a0b0c0d01020304\","
            + "\"absorbedThroughSetups\":" + S1Json + ","
            + "\"content\":\"old\","
            + "\"payloadSha256\":\"a9986e97a2c82fd02fd8a7d71b8d9c3db0b3c3da4f98314243030a85cee50a7d\"}";

        Assert.Equal(
            "a9986e97a2c82fd02fd8a7d71b8d9c3db0b3c3da4f98314243030a85cee50a7d",
            input.PayloadSha256
        );
        Assert.Equal(Expected, actual);
        Assert.Equal(
            input,
            DerivedRecapCodec.DecodeFrozenInput(
                Encoding.UTF8.GetBytes(actual)
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
                S2,
                EmptyRecapPriorContext.Instance,
                [first]
            ).ManifestPayloadSha256,
            DerivedRecapCodec.CreateManifest(
                new RefId(8),
                A2,
                S2,
                EmptyRecapPriorContext.Instance,
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
                    S2,
                    EmptyRecapPriorContext.Instance,
                    [MaintainPlan(invalid)]
                )
            );
        }
    }

    [Theory]
    [InlineData("default-address")]
    [InlineData("non-positive-schema")]
    [InlineData("uppercase-sha")]
    public void SetupReferences_RequireNonDefaultAddressPositiveSchemaAndLowercaseSha(
        string mutation
    ) {
        SessionContextSetupReference invalidRuntime = mutation switch {
            "default-address" => S2.RuntimeConfig with {
                Address = default
            },
            "non-positive-schema" => S2.RuntimeConfig with {
                BodySchemaVersion = 0
            },
            "uppercase-sha" => S2.RuntimeConfig with {
                PayloadSha256 = new string('A', 64)
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        SessionContextAnchorSetupReferences invalidSetups = S2 with {
            RuntimeConfig = invalidRuntime
        };

        Assert.Throws<InvalidDataException>(() =>
            DerivedRecapCodec.CreateManifest(
                new RefId(8),
                A2,
                invalidSetups,
                EmptyRecapPriorContext.Instance,
                [MaintainPlan(RecapTestIdentity.CapabilityFingerprint)]
            )
        );
    }

    [Fact]
    public void MaintainFinalBoundary_MustMatchAdmissionAddressAndSetups() {
        MaintainRecapBlockPlan baseline = MaintainPlan(
            RecapTestIdentity.CapabilityFingerprint
        );
        var mismatched = new MaintainRecapBlockPlan(
            baseline.RecapBlockId,
            baseline.Target,
            baseline.MaintainerId,
            baseline.MaintainerCapabilityFingerprint,
            baseline.Source,
            [
                new RecapReplayBoundary(A2, S1)
            ],
            baseline.PriorContextPayloadSha256,
            baseline.MaxContentUtf8Bytes
        );

        Assert.Throws<InvalidDataException>(() =>
            DerivedRecapCodec.CreateManifest(
                new RefId(8),
                A2,
                S2,
                EmptyRecapPriorContext.Instance,
                [mismatched]
            )
        );
    }

    [Fact]
    public void Setups_AreHashBoundAcrossManifestFrozenInputAndPlan() {
        SessionContextAnchorSetupReferences alternateS1 = S1 with {
            SystemPrompt = S1.SystemPrompt with {
                PayloadSha256 = new string('2', 64)
            }
        };
        DerivedRecapFrozenInput firstInput =
            DerivedRecapCodec.CreateFrozenInput(
                new RecapBlockId("roleplay.action"),
                new ContextHeaderBlockPath(
                    ContextHeaderCarrier.Action,
                    "roleplay.action"
                ),
                A1,
                S1,
                "old"
            );
        DerivedRecapFrozenInput alternateInput =
            DerivedRecapCodec.CreateFrozenInput(
                firstInput.RecapBlockId,
                firstInput.Target,
                A1,
                alternateS1,
                "old"
            );
        var firstPlan = new InheritRecapBlockPlan(
            firstInput.RecapBlockId,
            firstInput.Target,
            A1,
            S1,
            new string('3', 64),
            firstInput.PayloadSha256
        );
        var crossBoundPlan = new InheritRecapBlockPlan(
            firstInput.RecapBlockId,
            firstInput.Target,
            A1,
            alternateS1,
            new string('3', 64),
            firstInput.PayloadSha256
        );

        Assert.NotEqual(
            firstInput.PayloadSha256,
            alternateInput.PayloadSha256
        );
        Assert.NotEqual(
            DerivedRecapCodec.ComputeBlockPlanSha256(firstPlan),
            DerivedRecapCodec.ComputeBlockPlanSha256(crossBoundPlan)
        );
        Assert.NotEqual(
            DerivedRecapCodec.CreateManifest(
                new RefId(8),
                A2,
                S2,
                EmptyRecapPriorContext.Instance,
                [firstPlan]
            ).ManifestPayloadSha256,
            DerivedRecapCodec.CreateManifest(
                new RefId(8),
                A2,
                S2,
                EmptyRecapPriorContext.Instance,
                [crossBoundPlan]
            ).ManifestPayloadSha256
        );
    }

    [Fact]
    public void HistoricalV5ManifestAndPublication_AreStrictlyRejected() {
        const string HistoricalManifest =
            "{\"schema\":\"atelia.session-journal.derived-recap-manifest.v5\","
            + "\"refId\":\"0000000000001234\","
            + "\"setAdmissionAnchor\":\"ej1:11121314151617181a1b1c1d00000000\","
            + "\"blocks\":[{\"mode\":\"maintain\","
            + "\"recapBlockId\":\"roleplay.self\","
            + "\"target\":{\"carrier\":\"system\",\"blockKey\":\"roleplay.self\"},"
            + "\"maintainerId\":\"roleplay.autobiographical\","
            + "\"maintainerCapabilityFingerprint\":\"sha256:0000000000000000000000000000000000000000000000000000000000000000\","
            + "\"source\":{\"kind\":\"empty\",\"replayStartExclusive\":\"ej1:01020304050607080a0b0c0d01020304\"},"
            + "\"catchUpThrough\":[\"ej1:11121314151617181a1b1c1d00000000\"],"
            + "\"priorContext\":{\"kind\":\"empty\"},"
            + "\"maxContentUtf8Bytes\":262144}],"
            + "\"manifestPayloadSha256\":\"cad7c824d9e03b0fe632852d4072d3057b5208506bf660bfa7989cf8563c24df\"}";
        const string HistoricalPublication =
            "{\"schema\":\"atelia.session-journal.published-recap-set.v5\","
            + "\"refId\":\"0000000000001234\","
            + "\"setAdmissionAnchor\":\"ej1:11121314151617181a1b1c1d00000000\","
            + "\"frozenPlanSnapshot\":{\"schema\":\"atelia.session-journal.derived-recap-manifest.v5\","
            + "\"refId\":\"0000000000001234\","
            + "\"setAdmissionAnchor\":\"ej1:11121314151617181a1b1c1d00000000\","
            + "\"blocks\":[{\"mode\":\"maintain\","
            + "\"recapBlockId\":\"roleplay.self\","
            + "\"target\":{\"carrier\":\"system\",\"blockKey\":\"roleplay.self\"},"
            + "\"maintainerId\":\"roleplay.autobiographical\","
            + "\"maintainerCapabilityFingerprint\":\"sha256:0000000000000000000000000000000000000000000000000000000000000000\","
            + "\"source\":{\"kind\":\"empty\",\"replayStartExclusive\":\"ej1:01020304050607080a0b0c0d01020304\"},"
            + "\"catchUpThrough\":[\"ej1:11121314151617181a1b1c1d00000000\"],"
            + "\"priorContext\":{\"kind\":\"empty\"},"
            + "\"maxContentUtf8Bytes\":262144}],"
            + "\"manifestPayloadSha256\":\"cad7c824d9e03b0fe632852d4072d3057b5208506bf660bfa7989cf8563c24df\"},"
            + "\"blockCommitments\":[{\"recapBlockId\":\"roleplay.self\","
            + "\"target\":{\"carrier\":\"system\",\"blockKey\":\"roleplay.self\"},"
            + "\"absorbedThrough\":\"ej1:11121314151617181a1b1c1d00000000\","
            + "\"payloadSha256\":\"aae2e3ef7c63cbc2753ef5337e600425a0670993997748b6dec552683d452324\"}],"
            + "\"envelopeSha256\":\"330273a29d4cf8ed5e2b3341f61063f76042485289eac60a2708c27f382a9f9c\"}";

        Assert.DoesNotContain(
            "setAdmissionAnchorSetups",
            HistoricalManifest,
            StringComparison.Ordinal
        );
        Assert.Throws<NotSupportedException>(() =>
            DerivedRecapCodec.DecodeManifest(
                Encoding.UTF8.GetBytes(HistoricalManifest)
            )
        );
        Assert.Throws<NotSupportedException>(() =>
            DerivedRecapCodec.DecodePublication(
                Encoding.UTF8.GetBytes(HistoricalPublication)
            )
        );
    }

    [Fact]
    public void HistoricalV4FrozenInput_IsStrictlyRejected() {
        const string HistoricalInput =
            "{\"schema\":\"atelia.session-journal.derived-recap-frozen-input.v4\","
            + "\"recapBlockId\":\"roleplay.action\","
            + "\"target\":{\"carrier\":\"action\",\"blockKey\":\"roleplay.action\"},"
            + "\"absorbedThrough\":\"ej1:01020304050607080a0b0c0d01020304\","
            + "\"content\":\"old\","
            + "\"payloadSha256\":\"0f97ad4a752167dc0c69d5c508d17c444fdb5eeecf11698c5784e8cfdaf59c14\"}";

        Assert.Throws<NotSupportedException>(() =>
            DerivedRecapCodec.DecodeFrozenInput(
                Encoding.UTF8.GetBytes(HistoricalInput)
            )
        );
    }

    [Fact]
    public void DurableV7_RejectsV6AndFingerprintFieldMutation() {
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                new RefId(9),
                A2,
                S2,
                EmptyRecapPriorContext.Instance,
                [MaintainPlan(
                    RecapTestIdentity.CapabilityFingerprint
                )]
            );
        string json = Encoding.UTF8.GetString(
            DerivedRecapCodec.EncodeManifest(manifest)
        );

        string oldSchema = json.Replace(
            DerivedRecapCodec.ManifestSchema,
            "atelia.session-journal.derived-recap-manifest.v6",
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
            "atelia.session-journal.published-recap-set.v6",
            StringComparison.Ordinal
        );
        Assert.Throws<NotSupportedException>(() =>
            DerivedRecapCodec.DecodePublication(
                Encoding.UTF8.GetBytes(oldPublicationSchema)
            )
        );
    }

    private static MaintainRecapBlockPlan ExistingPlan(
        string blockId,
        string priorDigest
    ) => new(
        new RecapBlockId(blockId),
        new ContextHeaderBlockPath(
            ContextHeaderCarrier.System,
            blockId
        ),
        "roleplay.autobiographical",
        RecapTestIdentity.CapabilityFingerprint,
        new ExistingRecapMaintainSource(
            A1,
            S1,
            new string('a', 64),
            new string('b', 64)
        ),
        [new RecapReplayBoundary(A2, S2)],
        priorDigest
    );

    private static int Count(string text, string value) {
        int count = 0;
        int start = 0;
        while ((start = text.IndexOf(
                   value,
                   start,
                   StringComparison.Ordinal
               )) >= 0) {
            count++;
            start += value.Length;
        }
        return count;
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
        new EmptyRecapMaintainSource(A1, S1),
        [new RecapReplayBoundary(A2, S2)],
        RecapWireTestFacts.PriorDigest(EmptyRecapPriorContext.Instance)
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
            new EmptyRecapMaintainSource(A1, S1),
            [new RecapReplayBoundary(A2, S2)],
            RecapWireTestFacts.PriorDigest(EmptyRecapPriorContext.Instance)
        );
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                new RefId(7),
                A2,
                S2,
                EmptyRecapPriorContext.Instance,
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
                S1,
                "old"
            );
        var plan = new InheritRecapBlockPlan(
            id,
            target,
            A1,
            S1,
            new string('1', 64),
            input.PayloadSha256
        );
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                new RefId(9),
                A2,
                S2,
                EmptyRecapPriorContext.Instance,
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
            new EmptyRecapMaintainSource(A1, S1),
            [new RecapReplayBoundary(A2, S2)],
            RecapWireTestFacts.PriorDigest(EmptyRecapPriorContext.Instance)
        );
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                new RefId(12),
                A2,
                S2,
                EmptyRecapPriorContext.Instance,
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
