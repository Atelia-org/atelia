using System.Collections.Immutable;
using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionRequestManifestCodecTests {
    private static readonly EventAddress RawStart =
        EventAddressTextCodec.Parse("ej1:00000000000000010000000100000000");
    private static readonly EventAddress Activation =
        EventAddressTextCodec.Parse("ej1:00000000000000020000000100000000");
    private static readonly EventAddress RuntimeSetup =
        EventAddressTextCodec.Parse("ej1:00000000000000030000000100000000");
    private static readonly EventAddress PromptSetup =
        EventAddressTextCodec.Parse("ej1:00000000000000040000000100000000");

    [Fact]
    public void CompletionRequestPreparedV2_RoundtripsCanonicalLiteralGolden() {
        CompletionRequestPreparedBody body = CreateManifest();

        byte[] encoded = SessionEventCodec.Encode(
            SessionEventKind.CompletionRequestPrepared,
            body
        );
        var decoded = Assert.IsType<CompletionRequestPreparedBody>(
            SessionEventCodec.Decode(
                SessionEventKind.CompletionRequestPrepared,
                encoded,
                out int version
            )
        );

        Assert.Equal(2, version);
        Assert.Equal(encoded, SessionEventCodec.Encode(
            SessionEventKind.CompletionRequestPrepared,
            decoded
        ));
        Assert.Equal(body.Attempt, decoded.Attempt);
        Assert.Equal(body.Execution, decoded.Execution);
        Assert.Equal(body.Plan.RawStartExclusive, decoded.Plan.RawStartExclusive);
        Assert.Equal(body.Plan.RawRangeSha256, decoded.Plan.RawRangeSha256);
        Assert.True(body.Plan.ArtifactInputs.SequenceEqual(
            decoded.Plan.ArtifactInputs
        ));
        Assert.Equal(body.Plan.ActiveArtifactSet, decoded.Plan.ActiveArtifactSet);
        Assert.Equal(body.Setups, decoded.Setups);
        Assert.Equal(body.Parameters, decoded.Parameters);
        Assert.Equal(body.ToolSet.Sha256, decoded.ToolSet.Sha256);
        Assert.Equal(body.Recipe, decoded.Recipe);
        Assert.Equal(body.Target, decoded.Target);
        Assert.Equal(body.Commitment, decoded.Commitment);
        Assert.Equal(
            """
            {"v":2,"body":{"attempt":{"attemptId":"attempt-01","correlationId":"correlation-01","reason":"observation"},"execution":{"lastIssuedToolExecutionSequence":17},"plan":{"rawStartExclusive":"ej1:00000000000000010000000100000000","rawRangeSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","artifactInputs":[{"artifactId":"artifact-system","artifactKind":"rolling-summary","contentSha256":"e6babf8c03395cef81dcfa83a6dbb4ec4a8892a9fe188a4b37d99123b79b67df","contextSnapshot":{"systemPromptFragment":"system recap","observationMessage":"","actionMessage":""}},{"artifactId":"artifact-world","artifactKind":"world-understanding","contentSha256":"60b37427fabe85d010aa6c32e7b5239eda1d3cc0472fc9a02ae6027f3aba4d02","contextSnapshot":{"systemPromptFragment":"","observationMessage":"world recap","actionMessage":""}}],"activeArtifactSet":{"address":"ej1:00000000000000020000000100000000","bodySchemaVersion":1,"payloadSha256":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"}},"setups":{"runtimeConfig":{"address":"ej1:00000000000000030000000100000000","bodySchemaVersion":1,"payloadSha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"},"systemPrompt":{"address":"ej1:00000000000000040000000100000000","bodySchemaVersion":1,"payloadSha256":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"}},"parameters":{"modelId":"model-A","maxTokens":4096},"toolSet":{"codecId":"atelia.tool-definition.canonical-json.v1","sha256":"4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945","runtimeIdentity":null,"definitions":[]},"recipe":{"recipeId":"atelia.session-journal.coherent-artifact-tail.recipe.v1","canonicalRequestCodecId":"atelia.completion-request.canonical-json.v1"},"target":{"connection":{"connectionId":"connection-A","kind":"test","connectionFingerprint":"connection-fingerprint-A","requestAdapterFingerprint":"adapter-fingerprint-A"},"clientName":"client-A","apiSpecId":"api-A"},"commitment":{"byteLength":123,"sha256":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"}}}
            """.Trim(),
            Encoding.UTF8.GetString(encoded)
        );
    }

    [Theory]
    [InlineData("\"attempt\":{\"attemptId\":", "\"attempt\":{\"unknown\":true,\"attemptId\":")]
    [InlineData("\"plan\":{\"rawStartExclusive\":", "\"plan\":{\"unknown\":true,\"rawStartExclusive\":")]
    [InlineData("\"recipe\":{\"recipeId\":", "\"recipe\":{\"unknown\":true,\"recipeId\":")]
    [InlineData("\"target\":{\"connection\":", "\"target\":{\"unknown\":true,\"connection\":")]
    [InlineData("\"commitment\":{\"byteLength\":", "\"commitment\":{\"unknown\":true,\"byteLength\":")]
    [InlineData("\"attemptId\":\"attempt-01\",", "\"attemptId\":\"duplicate\",\"attemptId\":\"attempt-01\",")]
    [InlineData("\"recipeId\":\"atelia.session-journal.coherent-artifact-tail.recipe.v1\",", "\"recipeId\":\"duplicate\",\"recipeId\":\"atelia.session-journal.coherent-artifact-tail.recipe.v1\",")]
    public void CompletionRequestPreparedV2_StrictDecodeRejectsUnknownOrDuplicateProperties(
        string marker,
        string replacement
    ) {
        string canonical = EncodeManifestJson();

        AssertStrictDecodeRejected(ReplaceOnce(canonical, marker, replacement));
    }

    [Theory]
    [InlineData("\"recipe\":{", "\"removedRecipe\":{")]
    [InlineData("\"rawStartExclusive\":", "\"removedRawStart\":")]
    [InlineData("\"activeArtifactSet\":", "\"removedArtifactSet\":")]
    public void CompletionRequestPreparedV2_StrictDecodeRejectsMissingRequiredProperties(
        string marker,
        string replacement
    ) {
        string canonical = EncodeManifestJson();

        AssertStrictDecodeRejected(ReplaceOnce(canonical, marker, replacement));
    }

    [Fact]
    public void ManifestValidation_RejectsUnsupportedRecipeAndRequestCodec() {
        CompletionRequestPreparedBody body = CreateManifest();

        Assert.Throws<NotSupportedException>(() => SessionRequestManifestCodec.Validate(
            body with {
                Recipe = body.Recipe with { RecipeId = "unsupported-recipe" }
            }
        ));
        Assert.Throws<NotSupportedException>(() => SessionRequestManifestCodec.Validate(
            body with {
                Recipe = body.Recipe with {
                    CanonicalRequestCodecId = "unsupported-codec"
                }
            }
        ));
    }

    [Fact]
    public void ManifestValidation_RequiresExactArtifactsAndToolRuntimeIdentity() {
        CompletionRequestPreparedBody body = CreateManifest();
        SessionRequestArtifactInput first = body.Plan.ArtifactInputs[0];

        Assert.Throws<InvalidDataException>(() => SessionRequestManifestCodec.Validate(
            body with { Plan = body.Plan with { ArtifactInputs = [first] } }
        ));
        Assert.Throws<InvalidDataException>(() => SessionRequestManifestCodec.Validate(
            body with {
                Plan = body.Plan with {
                    ArtifactInputs = [first, first]
                }
            }
        ));
        Assert.Throws<InvalidDataException>(() => SessionRequestManifestCodec.Validate(
            body with {
                Plan = body.Plan with {
                    ArtifactInputs = [
                        first with { ContentSha256 = new string('0', 64) },
                        body.Plan.ArtifactInputs[1]
                    ]
                }
            }
        ));

        ImmutableArray<ToolDefinition> tools = [
            new ToolDefinition("sample", "sample tool", new ToolSchema.Object())
        ];
        SessionRequestToolSet toolSet = new(
            SessionRequestManifestDefaults.ToolCodecId,
            SessionRequestCanonicalizer.ComputeToolSetSha256(tools),
            tools,
            RuntimeIdentity: null
        );
        Assert.Throws<InvalidDataException>(() => SessionRequestManifestCodec.Validate(
            body with { ToolSet = toolSet }
        ));
    }

    [Fact]
    public void CanonicalRequest_PreservesOpaqueReasoningToolCallAndToolResult() {
        var descriptor = new CompletionDescriptor("provider", "api", "model-A");
        var request = new CompletionRequest(
            "model-A",
            "system",
            [
                new ObservationMessage("observe"),
                new ActionMessage([
                    new ActionBlock.TextReasoningBlock("opaque", descriptor, "debug"),
                    new ActionBlock.ToolCall(
                        new RawToolCall("sample", "call-1", """{"value":1}""")
                    )
                ]),
                new ToolResultsMessage(
                    null,
                    [ToolResult.FromText(
                        "sample",
                        "call-1",
                        ToolExecutionStatus.Success,
                        "result"
                    )]
                )
            ],
            [new ToolDefinition("sample", "sample tool", new ToolSchema.Object())],
            512
        );

        byte[] canonical = SessionRequestCanonicalizer.Canonicalize(request);
        string json = Encoding.UTF8.GetString(canonical);

        Assert.Contains("\"payload\":\"b3BhcXVl\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"tool-call\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"tool-results\"", json, StringComparison.Ordinal);
        Assert.Equal(
            SessionRequestCanonicalizer.CreateCommitment(request),
            new SessionRequestCommitment(
                canonical.Length,
                SessionRequestCanonicalizer.Sha256Hex(canonical)
            )
        );
    }

    private static CompletionRequestPreparedBody CreateManifest() {
        ImmutableArray<ToolDefinition> tools = [];
        SessionRequestArtifactInput system = Artifact(
            "artifact-system",
            "rolling-summary",
            new SessionRequestArtifactContextSnapshot("system recap", "", "")
        );
        SessionRequestArtifactInput world = Artifact(
            "artifact-world",
            "world-understanding",
            new SessionRequestArtifactContextSnapshot("", "world recap", "")
        );
        return new CompletionRequestPreparedBody(
            new SessionRequestAttempt(
                "attempt-01",
                "correlation-01",
                "observation"
            ),
            new SessionExecutionCheckpoint(17),
            new SessionContextPlan(
                RawStart,
                new string('a', 64),
                [system, world],
                new SessionArtifactSetReference(
                    Activation,
                    1,
                    new string('e', 64)
                )
            ),
            new SessionGoverningSetupReferences(
                new SessionSetupReference(
                    RuntimeSetup,
                    1,
                    new string('b', 64)
                ),
                new SessionSetupReference(
                    PromptSetup,
                    1,
                    new string('c', 64)
                )
            ),
            new SessionRequestParameters("model-A", 4096),
            new SessionRequestToolSet(
                SessionRequestManifestDefaults.ToolCodecId,
                SessionRequestCanonicalizer.ComputeToolSetSha256(tools),
                tools,
                RuntimeIdentity: null
            ),
            new SessionRequestRecipe(
                SessionRequestManifestDefaults.RecipeId,
                SessionRequestManifestDefaults.CanonicalRequestCodecId
            ),
            new SessionRequestTarget(
                new SessionCompletionTargetIdentity(
                    "connection-A",
                    "test",
                    "connection-fingerprint-A",
                    "adapter-fingerprint-A"
                ),
                "client-A",
                "api-A"
            ),
            new SessionRequestCommitment(123, new string('d', 64))
        );
    }

    private static SessionRequestArtifactInput Artifact(
        string id,
        string kind,
        SessionRequestArtifactContextSnapshot snapshot
    ) => new(
        id,
        kind,
        SessionArtifactContextSnapshotHasher.ComputeSha256(snapshot),
        snapshot
    );

    private static string EncodeManifestJson()
        => Encoding.UTF8.GetString(SessionEventCodec.Encode(
            SessionEventKind.CompletionRequestPrepared,
            CreateManifest()
        ));

    private static void AssertStrictDecodeRejected(string json) {
        Assert.Throws<InvalidDataException>(() => SessionEventCodec.Decode(
            SessionEventKind.CompletionRequestPrepared,
            Encoding.UTF8.GetBytes(json),
            out _
        ));
    }

    private static string ReplaceOnce(
        string source,
        string marker,
        string replacement
    ) {
        int index = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Mutation marker was not found: {marker}");
        return string.Concat(
            source.AsSpan(0, index),
            replacement,
            source.AsSpan(index + marker.Length)
        );
    }
}
