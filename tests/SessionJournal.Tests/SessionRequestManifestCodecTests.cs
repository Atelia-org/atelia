using System.Collections.Immutable;
using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionRequestManifestCodecTests {
    private static readonly EventAddress RawStart =
        EventAddressTextCodec.Parse("ej1:00000000000000010000000100000000");
    private static readonly EventAddress RuntimeSetup =
        EventAddressTextCodec.Parse("ej1:00000000000000030000000100000000");
    private static readonly EventAddress PromptSetup =
        EventAddressTextCodec.Parse("ej1:00000000000000040000000100000000");

    [Fact]
    public void CompletionRequestPreparedV5_RoundtripsCanonicalLiteralGolden() {
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

        Assert.Equal(5, version);
        Assert.Equal(encoded, SessionEventCodec.Encode(
            SessionEventKind.CompletionRequestPrepared,
            decoded
        ));
        Assert.Equal(body.Origin, decoded.Origin);
        Assert.Equal(body.Execution, decoded.Execution);
        Assert.Equal(body.Plan.RawStartExclusive, decoded.Plan.RawStartExclusive);
        Assert.Equal(body.Plan.RawRangeSha256, decoded.Plan.RawRangeSha256);
        Assert.True(body.Plan.ExactContextInputs.SequenceEqual(
            decoded.Plan.ExactContextInputs
        ));
        Assert.Equal(body.Plan.RawStartSetups, decoded.Plan.RawStartSetups);
        Assert.Equal(body.Setups, decoded.Setups);
        Assert.Equal(body.Parameters, decoded.Parameters);
        Assert.Equal(body.ToolSet.Sha256, decoded.ToolSet.Sha256);
        Assert.Equal(body.Recipe, decoded.Recipe);
        Assert.Equal(body.Target, decoded.Target);
        Assert.Equal(body.Commitment, decoded.Commitment);
        Assert.Equal(
            """
            {"v":5,"body":{"origin":{"correlationId":"correlation-01","reason":"observation"},"execution":{"lastIssuedToolExecutionSequence":17},"plan":{"rawStartExclusive":"ej1:00000000000000010000000100000000","rawRangeSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","rawStartSetups":{"runtimeConfig":{"address":"ej1:00000000000000030000000100000000","bodySchemaVersion":1,"payloadSha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"},"systemPrompt":{"address":"ej1:00000000000000040000000100000000","bodySchemaVersion":1,"payloadSha256":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"}},"exactContextInputs":[{"contentSha256":"e6babf8c03395cef81dcfa83a6dbb4ec4a8892a9fe188a4b37d99123b79b67df","contextSnapshot":{"systemPromptFragment":"system recap","observationMessage":"","actionMessage":""}},{"contentSha256":"60b37427fabe85d010aa6c32e7b5239eda1d3cc0472fc9a02ae6027f3aba4d02","contextSnapshot":{"systemPromptFragment":"","observationMessage":"world recap","actionMessage":""}}]},"setups":{"runtimeConfig":{"address":"ej1:00000000000000030000000100000000","bodySchemaVersion":1,"payloadSha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"},"systemPrompt":{"address":"ej1:00000000000000040000000100000000","bodySchemaVersion":1,"payloadSha256":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"}},"parameters":{"modelId":"model-A","maxTokens":4096},"toolSet":{"codecId":"atelia.tool-definition.canonical-json.v1","sha256":"4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945","runtimeIdentity":null,"definitions":[]},"recipe":{"recipeId":"atelia.session-journal.coherent-artifact-tail.recipe.v1","canonicalRequestCodecId":"atelia.completion-request.canonical-json.v1"},"target":{"connection":{"connectionId":"connection-A","kind":"test","connectionFingerprint":"connection-fingerprint-A","requestAdapterFingerprint":"adapter-fingerprint-A"},"clientName":"client-A","apiSpecId":"api-A"},"commitment":{"byteLength":123,"sha256":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"}}}
            """.Trim(),
            Encoding.UTF8.GetString(encoded)
        );
    }

    [Theory]
    [InlineData("\"origin\":{\"correlationId\":", "\"origin\":{\"unknown\":true,\"correlationId\":")]
    [InlineData("\"plan\":{\"rawStartExclusive\":", "\"plan\":{\"unknown\":true,\"rawStartExclusive\":")]
    [InlineData("\"recipe\":{\"recipeId\":", "\"recipe\":{\"unknown\":true,\"recipeId\":")]
    [InlineData("\"target\":{\"connection\":", "\"target\":{\"unknown\":true,\"connection\":")]
    [InlineData("\"commitment\":{\"byteLength\":", "\"commitment\":{\"unknown\":true,\"byteLength\":")]
    [InlineData("\"correlationId\":\"correlation-01\",", "\"correlationId\":\"duplicate\",\"correlationId\":\"correlation-01\",")]
    [InlineData("\"recipeId\":\"atelia.session-journal.coherent-artifact-tail.recipe.v1\",", "\"recipeId\":\"duplicate\",\"recipeId\":\"atelia.session-journal.coherent-artifact-tail.recipe.v1\",")]
    public void CompletionRequestPreparedV5_StrictDecodeRejectsUnknownOrDuplicateProperties(
        string marker,
        string replacement
    ) {
        string canonical = EncodeManifestJson();

        AssertStrictDecodeRejected(ReplaceOnce(canonical, marker, replacement));
    }

    [Theory]
    [InlineData("\"recipe\":{", "\"removedRecipe\":{")]
    [InlineData("\"rawStartExclusive\":", "\"removedRawStart\":")]
    [InlineData("\"rawStartSetups\":", "\"removedRawStartSetups\":")]
    public void CompletionRequestPreparedV5_StrictDecodeRejectsMissingRequiredProperties(
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
    public void ManifestValidation_AllowsExactEmptyMemoryAndRequiresToolRuntimeIdentity() {
        CompletionRequestPreparedBody body = CreateManifest();
        SessionRequestContextInput first = body.Plan.ExactContextInputs[0];

        SessionRequestManifestCodec.Validate(
            body with {
                Plan = body.Plan with {
                    ExactContextInputs = []
                }
            }
        );
        SessionRequestManifestCodec.Validate(body with {
            Plan = body.Plan with { ExactContextInputs = [first] }
        });
        Assert.Throws<InvalidDataException>(() => SessionRequestManifestCodec.Validate(
            body with {
                Plan = body.Plan with {
                    ExactContextInputs = Enumerable.Repeat(first, 129).ToImmutableArray()
                }
            }
        ));
        Assert.Throws<InvalidDataException>(() => SessionRequestManifestCodec.Validate(
            body with {
                Plan = body.Plan with {
                    ExactContextInputs = [
                        first with { ContentSha256 = new string('0', 64) },
                        body.Plan.ExactContextInputs[1]
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

    [Fact]
    public void CompletionRequestPreparedV5_PreservesAbsentNullAndNumericToolDefaults() {
        CompletionRequestPreparedBody body = CreateManifest(
            CreateToolDefinitions()
        );

        byte[] encoded = SessionEventCodec.Encode(
            SessionEventKind.CompletionRequestPrepared,
            body
        );
        var decoded = Assert.IsType<CompletionRequestPreparedBody>(
            SessionEventCodec.Decode(
                SessionEventKind.CompletionRequestPrepared,
                encoded,
                out _
            )
        );
        var root = Assert.IsType<ToolSchema.Object>(
            decoded.ToolSet.Definitions[0].InputSchema
        );
        var absent = Assert.IsType<ToolSchema.Value>(root.Properties[0].Schema);
        var explicitNull =
            Assert.IsType<ToolSchema.Value>(root.Properties[1].Schema);
        var int64 = Assert.IsType<ToolSchema.Value>(root.Properties[2].Schema);
        var float32 = Assert.IsType<ToolSchema.Value>(root.Properties[3].Schema);
        var decimalValue =
            Assert.IsType<ToolSchema.Value>(root.Properties[4].Schema);

        Assert.False(absent.Default.HasValue);
        Assert.True(explicitNull.Default.HasValue);
        Assert.Null(explicitNull.Default.GetValueOrDefault().Value);
        Assert.IsType<long>(int64.Default.GetValueOrDefault().Value);
        Assert.IsType<long>(int64.Minimum);
        Assert.IsType<long>(int64.Maximum);
        Assert.IsType<float>(float32.Default.GetValueOrDefault().Value);
        Assert.IsType<decimal>(decimalValue.Default.GetValueOrDefault().Value);
        Assert.Equal(encoded, SessionEventCodec.Encode(
            SessionEventKind.CompletionRequestPrepared,
            decoded
        ));
    }

    [Fact]
    public void CompletionRequestPreparedV5_RoundtripsComprehensiveNestedToolSchemasInOrder() {
        CompletionRequestPreparedBody body = CreateManifest(
            CreateComprehensiveToolDefinitions()
        );

        byte[] encoded = SessionEventCodec.Encode(
            SessionEventKind.CompletionRequestPrepared,
            body
        );
        var decoded = Assert.IsType<CompletionRequestPreparedBody>(
            SessionEventCodec.Decode(
                SessionEventKind.CompletionRequestPrepared,
                encoded,
                out _
            )
        );
        var root = Assert.IsType<ToolSchema.Object>(
            decoded.ToolSet.Definitions[1].InputSchema
        );

        Assert.Equal(["sample", "complex"], decoded.ToolSet.Definitions
            .Select(static definition => definition.Name));
        Assert.True(root.AdditionalProperties);
        Assert.Equal("complex root", root.Description);
        Assert.Equal("root example", root.Example);
        Assert.IsType<bool>(
            Assert.IsType<ToolSchema.Value>(root.Properties[0].Schema)
                .Default.GetValueOrDefault().Value
        );
        Assert.IsType<int>(
            Assert.IsType<ToolSchema.Value>(root.Properties[1].Schema)
                .Default.GetValueOrDefault().Value
        );
        Assert.IsType<double>(
            Assert.IsType<ToolSchema.Value>(root.Properties[2].Schema)
                .Default.GetValueOrDefault().Value
        );
        var array = Assert.IsType<ToolSchema.Array>(root.Properties[3].Schema);
        var item = Assert.IsType<ToolSchema.Value>(array.ItemSchema);
        Assert.Equal(["alpha", "beta"], item.StringEnumValues.ToArray());
        Assert.Equal(1, item.MinLength);
        Assert.Equal(12, item.MaxLength);
        Assert.Equal("^[a-z]+$", item.Pattern);
        Assert.IsType<ToolSchema.Object>(root.Properties[4].Schema);
        Assert.Equal(encoded, SessionEventCodec.Encode(
            SessionEventKind.CompletionRequestPrepared,
            decoded
        ));
    }

    [Fact]
    public void CanonicalRequest_AllFiveFieldsHaveStableLiteralCommitment() {
        ImmutableArray<ToolDefinition> tools = CreateToolDefinitions();
        var request = new CompletionRequest(
            "model-α",
            "system <raw> & prompt",
            [
                new ObservationMessage("observe"),
                new ActionMessage([
                    new ActionBlock.Text("answer"),
                    new ActionBlock.TextReasoningBlock(
                        "think",
                        new CompletionDescriptor(
                            "provider",
                            "responses-v1",
                            "model-α"
                        ),
                        "debug"
                    ),
                    new ActionBlock.ToolCall(
                        new RawToolCall("sample", "call-1", "{\"x\":1}")
                    )
                ]),
                new ToolResultsMessage(
                    null,
                    [ToolResult.FromText(
                        "sample",
                        "call-1",
                        ToolExecutionStatus.Success,
                        "ok"
                    )]
                )
            ],
            tools,
            4096
        );

        byte[] canonical = SessionRequestCanonicalizer.Canonicalize(request);
        SessionRequestCommitment commitment =
            SessionRequestCanonicalizer.CreateCommitment(request);

        Assert.Equal(1921, canonical.Length);
        Assert.Equal(1921, commitment.ByteLength);
        Assert.Equal(
            "dc714068ed5e60a5213cc7c673ca4f6c65a42caae0a8d60d224d0c5ac2d0fb95",
            commitment.Sha256
        );
    }

    [Fact]
    public void CanonicalRequest_CommitmentChangesForEachOfFiveFields() {
        var baseline = new CompletionRequest(
            "model",
            "system",
            [new ObservationMessage("observation")],
            [],
            100
        );
        string hash = SessionRequestCanonicalizer.CreateCommitment(
            baseline
        ).Sha256;
        ImmutableArray<ToolDefinition> changedTools = [
            new ToolDefinition("ping", "Ping tool", new ToolSchema.Object())
        ];

        Assert.NotEqual(hash, SessionRequestCanonicalizer.CreateCommitment(
            baseline with { ModelId = "model-2" }
        ).Sha256);
        Assert.NotEqual(hash, SessionRequestCanonicalizer.CreateCommitment(
            baseline with { SystemPrompt = "system-2" }
        ).Sha256);
        Assert.NotEqual(hash, SessionRequestCanonicalizer.CreateCommitment(
            baseline with {
                Context = [new ObservationMessage("observation-2")]
            }
        ).Sha256);
        Assert.NotEqual(hash, SessionRequestCanonicalizer.CreateCommitment(
            baseline with { Tools = changedTools }
        ).Sha256);
        Assert.NotEqual(hash, SessionRequestCanonicalizer.CreateCommitment(
            baseline with { MaxTokens = 101 }
        ).Sha256);
    }

    [Fact]
    public void CanonicalRequest_RejectsContextHeaderUnknownAndDerivedHistoryMessages() {
        Assert.Throws<InvalidOperationException>(() =>
            SessionRequestCanonicalizer.Canonicalize(new CompletionRequest(
                "model",
                "system",
                [new UnsupportedHistoryMessage(HistoryMessageKind.ContextHeader)],
                [],
                null
            ))
        );
        Assert.Throws<InvalidOperationException>(() =>
            SessionRequestCanonicalizer.Canonicalize(new CompletionRequest(
                "model",
                "system",
                [new UnsupportedHistoryMessage(HistoryMessageKind.Observation)],
                [],
                null
            ))
        );
        Assert.Throws<InvalidOperationException>(() =>
            SessionRequestCanonicalizer.Canonicalize(new CompletionRequest(
                "model",
                "system",
                [new DerivedObservationMessage("derived")],
                [],
                null
            ))
        );
    }

    [Fact]
    public void ArtifactContextSnapshotHash_HasStableLiteralAndFieldSensitivity() {
        var snapshot = new SessionRequestArtifactContextSnapshot(
            "system 🌟",
            "observation\nline2",
            "action"
        );
        string hash = SessionArtifactContextSnapshotHasher.ComputeSha256(
            snapshot
        );

        Assert.Equal(
            "2e89e9acf6c1e7dbcef6874a602a51cb425f76404b2b89124d5990891832f5fc",
            hash
        );
        Assert.NotEqual(hash, SessionArtifactContextSnapshotHasher.ComputeSha256(
            snapshot with { SystemPromptFragment = "changed" }
        ));
        Assert.NotEqual(hash, SessionArtifactContextSnapshotHasher.ComputeSha256(
            snapshot with { ObservationMessage = "changed" }
        ));
        Assert.NotEqual(hash, SessionArtifactContextSnapshotHasher.ComputeSha256(
            snapshot with { ActionMessage = "changed" }
        ));
    }

    [Fact]
    public void CompletionRequestPreparedV5_StrictDecodeRejectsNestedSnapshotAndToolSchemaDrift() {
        string canonical = EncodeManifestJson(CreateToolDefinitions());
        (string Marker, string Replacement)[] mutations = [
            (
                "\"contextSnapshot\":{\"systemPromptFragment\":",
                "\"contextSnapshot\":{\"unknown\":true,\"systemPromptFragment\":"
            ),
            (
                "\"contextSnapshot\":{\"systemPromptFragment\":",
                "\"contextSnapshot\":{\"systemPromptFragment\":\"other\",\"systemPromptFragment\":"
            ),
            (
                "\"inputSchema\":{\"kind\":\"object\",",
                "\"inputSchema\":{\"kind\":\"object\",\"unknown\":true,"
            ),
            (
                "\"inputSchema\":{\"kind\":\"object\",",
                "\"inputSchema\":{\"kind\":\"array\",\"kind\":\"object\","
            ),
            (
                "{\"name\":\"withoutDefault\",\"required\":",
                "{\"name\":\"other\",\"name\":\"withoutDefault\",\"required\":"
            )
        ];

        foreach ((string marker, string replacement) in mutations) {
            AssertStrictDecodeRejected(
                ReplaceOnce(canonical, marker, replacement)
            );
        }
    }

    private static CompletionRequestPreparedBody CreateManifest(
        ImmutableArray<ToolDefinition>? requestedTools = null
    ) {
        ImmutableArray<ToolDefinition> tools = requestedTools ?? [];
        SessionRequestContextInput system = ContextInput(
            new SessionRequestArtifactContextSnapshot("system recap", "", "")
        );
        SessionRequestContextInput world = ContextInput(
            new SessionRequestArtifactContextSnapshot("", "world recap", "")
        );
        return new CompletionRequestPreparedBody(
            new SessionRequestOrigin(
                "correlation-01",
                "observation"
            ),
            new SessionExecutionCheckpoint(17),
            new SessionContextPlan(
                RawStart,
                new string('a', 64),
                new SessionGoverningSetupReferences(
                    new SessionSetupReference(RuntimeSetup, 1, new string('b', 64)),
                    new SessionSetupReference(PromptSetup, 1, new string('c', 64))
                ),
                [system, world]
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
                tools.IsEmpty
                    ? null
                    : new SessionToolRuntimeIdentity(
                        "test-host",
                        "test-implementations",
                        "test-capabilities"
                    )
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

    private static SessionRequestContextInput ContextInput(
        SessionRequestArtifactContextSnapshot snapshot
    ) => new(
        SessionArtifactContextSnapshotHasher.ComputeSha256(snapshot),
        snapshot
    );

    private static string EncodeManifestJson(
        ImmutableArray<ToolDefinition>? tools = null
    )
        => Encoding.UTF8.GetString(SessionEventCodec.Encode(
            SessionEventKind.CompletionRequestPrepared,
            CreateManifest(tools)
        ));

    private static ImmutableArray<ToolDefinition> CreateToolDefinitions() {
        var schema = new ToolSchema.Object([
            new ToolSchema.Property(
                "withoutDefault",
                new ToolSchema.Value(
                    ToolParamType.String,
                    isNullable: true
                ),
                isRequired: false
            ),
            new ToolSchema.Property(
                "explicitNull",
                new ToolSchema.Value(
                    ToolParamType.String,
                    isNullable: true,
                    defaultValue: new ParamDefault(null)
                ),
                isRequired: false
            ),
            new ToolSchema.Property(
                "count",
                new ToolSchema.Value(
                    ToolParamType.Int64,
                    defaultValue: new ParamDefault(7L),
                    minimum: -9L,
                    maximum: 20L
                ),
                isRequired: true
            ),
            new ToolSchema.Property(
                "ratio",
                new ToolSchema.Value(
                    ToolParamType.Float32,
                    defaultValue: new ParamDefault(1.25f)
                ),
                isRequired: false
            ),
            new ToolSchema.Property(
                "price",
                new ToolSchema.Value(
                    ToolParamType.Decimal,
                    defaultValue: new ParamDefault(12.50m)
                ),
                isRequired: false
            )
        ]);
        return [new ToolDefinition("sample", "Sample tool", schema)];
    }

    private static ImmutableArray<ToolDefinition>
        CreateComprehensiveToolDefinitions() {
        var nested = new ToolSchema.Object([
            new ToolSchema.Property(
                "name",
                new ToolSchema.Value(
                    ToolParamType.String,
                    description: "nested name"
                ),
                isRequired: true
            )
        ], description: "nested object", example: "nested example");
        var complex = new ToolSchema.Object(
            [
                new ToolSchema.Property(
                    "enabled",
                    new ToolSchema.Value(
                        ToolParamType.Boolean,
                        defaultValue: new ParamDefault(true)
                    ),
                    isRequired: false
                ),
                new ToolSchema.Property(
                    "limit",
                    new ToolSchema.Value(
                        ToolParamType.Int32,
                        defaultValue: new ParamDefault(3),
                        minimum: -1,
                        maximum: 10
                    ),
                    isRequired: false
                ),
                new ToolSchema.Property(
                    "score",
                    new ToolSchema.Value(
                        ToolParamType.Float64,
                        defaultValue: new ParamDefault(0.5d),
                        minimum: -2.25d,
                        maximum: 4.5d
                    ),
                    isRequired: false
                ),
                new ToolSchema.Property(
                    "tags",
                    new ToolSchema.Array(
                        new ToolSchema.Value(
                            ToolParamType.String,
                            description: "tag",
                            example: "alpha",
                            stringEnumValues: ["alpha", "beta"],
                            minLength: 1,
                            maxLength: 12,
                            pattern: "^[a-z]+$"
                        ),
                        isNullable: true,
                        description: "tag list",
                        example: "[alpha]"
                    ),
                    isRequired: false
                ),
                new ToolSchema.Property(
                    "nested",
                    nested,
                    isRequired: true
                )
            ],
            additionalProperties: true,
            description: "complex root",
            example: "root example"
        );
        return CreateToolDefinitions().Add(
            new ToolDefinition("complex", "Complex tool", complex)
        );
    }

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

    private sealed record UnsupportedHistoryMessage(
        HistoryMessageKind Kind
    ) : IHistoryMessage;

    private sealed record DerivedObservationMessage(
        string? Value
    ) : ObservationMessage(Value);
}
