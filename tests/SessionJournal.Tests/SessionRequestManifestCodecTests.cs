using System.Collections.Immutable;
using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionRequestManifestCodecTests : IDisposable {
    private readonly string _tempPath = Path.Combine(
        Path.GetTempPath(),
        "atelia-session-request-manifest-tests",
        Guid.NewGuid().ToString("N")
    );

    public void Dispose() {
        try {
            if (Directory.Exists(_tempPath)) {
                Directory.Delete(_tempPath, recursive: true);
            }
        }
        catch {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public void CompletionRequestPrepared_RoundtripsCanonicalEnvelopeWithoutRepeatingHeaderParent() {
        var body = CreateManifestBody(CreateToolDefinitions(), out _);

        byte[] encoded = SessionEventCodec.Encode(SessionEventKind.CompletionRequestPrepared, body);
        var decoded = Assert.IsType<CompletionRequestPreparedBody>(
            SessionEventCodec.Decode(SessionEventKind.CompletionRequestPrepared, encoded, out int bodySchemaVersion)
        );
        byte[] reencoded = SessionEventCodec.Encode(SessionEventKind.CompletionRequestPrepared, decoded);
        string json = Encoding.UTF8.GetString(encoded);

        Assert.Equal(1, bodySchemaVersion);
        Assert.Equal(encoded, reencoded);
        Assert.Equal(body.Attempt, decoded.Attempt);
        Assert.Equal(body.Plan.RawStartExclusive, decoded.Plan.RawStartExclusive);
        Assert.Equal(body.Setups, decoded.Setups);
        Assert.Equal(body.Parameters, decoded.Parameters);
        Assert.Equal(body.ToolSet.Sha256, decoded.ToolSet.Sha256);
        Assert.Equal(body.Target, decoded.Target);
        Assert.Equal(body.Commitment, decoded.Commitment);
        Assert.DoesNotContain("basedOnRawHead", json, StringComparison.Ordinal);
        Assert.DoesNotContain("rawEndInclusive", json, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletionRequestPrepared_PreservesAbsentVsNullDefaultsAndNumericKinds() {
        ImmutableArray<ToolDefinition> tools = CreateToolDefinitions();
        var body = CreateManifestBody(tools, out _);

        byte[] encoded = SessionEventCodec.Encode(SessionEventKind.CompletionRequestPrepared, body);
        var decoded = Assert.IsType<CompletionRequestPreparedBody>(
            SessionEventCodec.Decode(SessionEventKind.CompletionRequestPrepared, encoded, out _)
        );

        var root = Assert.IsType<ToolSchema.Object>(decoded.ToolSet.Definitions[0].InputSchema);
        var absentDefault = Assert.IsType<ToolSchema.Value>(root.Properties[0].Schema);
        var nullDefault = Assert.IsType<ToolSchema.Value>(root.Properties[1].Schema);
        var int64Default = Assert.IsType<ToolSchema.Value>(root.Properties[2].Schema);
        var float32Default = Assert.IsType<ToolSchema.Value>(root.Properties[3].Schema);
        var decimalDefault = Assert.IsType<ToolSchema.Value>(root.Properties[4].Schema);

        Assert.False(absentDefault.Default.HasValue);
        Assert.True(nullDefault.Default.HasValue);
        Assert.Null(nullDefault.Default.GetValueOrDefault().Value);
        Assert.Equal(ToolParamType.Int64, int64Default.ValueKind);
        Assert.IsType<long>(int64Default.Default.GetValueOrDefault().Value);
        Assert.IsType<long>(int64Default.Minimum);
        Assert.IsType<long>(int64Default.Maximum);
        Assert.Equal(ToolParamType.Float32, float32Default.ValueKind);
        Assert.IsType<float>(float32Default.Default.GetValueOrDefault().Value);
        Assert.Equal(ToolParamType.Decimal, decimalDefault.ValueKind);
        Assert.IsType<decimal>(decimalDefault.Default.GetValueOrDefault().Value);
        Assert.Equal(body.ToolSet.Sha256, SessionRequestCanonicalizer.ComputeToolSetSha256(decoded.ToolSet.Definitions));
    }

    [Fact]
    public void CompletionRequestPrepared_RoundtripsComprehensiveNestedToolSchemasInOrder() {
        ImmutableArray<ToolDefinition> tools = CreateComprehensiveToolDefinitions();
        var body = CreateManifestBody(tools, out _);

        byte[] encoded = SessionEventCodec.Encode(SessionEventKind.CompletionRequestPrepared, body);
        var decoded = Assert.IsType<CompletionRequestPreparedBody>(
            SessionEventCodec.Decode(SessionEventKind.CompletionRequestPrepared, encoded, out _)
        );
        byte[] reencoded = SessionEventCodec.Encode(SessionEventKind.CompletionRequestPrepared, decoded);

        Assert.Equal(encoded, reencoded);
        Assert.Equal(["sample", "complex"], decoded.ToolSet.Definitions.Select(static tool => tool.Name));
        var root = Assert.IsType<ToolSchema.Object>(decoded.ToolSet.Definitions[1].InputSchema);
        Assert.True(root.AdditionalProperties);
        Assert.Equal("complex root", root.Description);
        Assert.Equal("root example", root.Example);
        Assert.IsType<bool>(Assert.IsType<ToolSchema.Value>(root.Properties[0].Schema).Default.GetValueOrDefault().Value);
        Assert.IsType<int>(Assert.IsType<ToolSchema.Value>(root.Properties[1].Schema).Default.GetValueOrDefault().Value);
        Assert.IsType<double>(Assert.IsType<ToolSchema.Value>(root.Properties[2].Schema).Default.GetValueOrDefault().Value);
        var array = Assert.IsType<ToolSchema.Array>(root.Properties[3].Schema);
        Assert.True(array.IsNullable);
        var item = Assert.IsType<ToolSchema.Value>(array.ItemSchema);
        Assert.Collection(
            item.StringEnumValues,
            value => Assert.Equal("alpha", value),
            value => Assert.Equal("beta", value)
        );
        Assert.Equal(1, item.MinLength);
        Assert.Equal(12, item.MaxLength);
        Assert.Equal("^[a-z]+$", item.Pattern);
        Assert.IsType<ToolSchema.Object>(root.Properties[4].Schema);
    }

    [Fact]
    public void CanonicalRequest_CoversAllFiveFieldsAndProviderNeutralHistory_GoldenCommitment() {
        ImmutableArray<ToolDefinition> tools = CreateToolDefinitions();
        var request = new CompletionRequest(
            "model-α",
            "system <raw> & prompt",
            new IHistoryMessage[] {
                new ObservationMessage("observe"),
                new ActionMessage([
                    new ActionBlock.Text("answer"),
                    new ActionBlock.TextReasoningBlock(
                        "think",
                        new CompletionDescriptor("provider", "responses-v1", "model-α"),
                        "debug"
                    ),
                    new ActionBlock.ToolCall(new RawToolCall("sample", "call-1", "{\"x\":1}"))
                ]),
                new ToolResultsMessage(
                    null,
                    [
                        ToolResult.FromText("sample", "call-1", ToolExecutionStatus.Success, "ok")
                    ]
                )
            },
            tools,
            MaxTokens: 4096
        );

        byte[] canonical = SessionRequestCanonicalizer.Canonicalize(request);
        SessionRequestCommitment commitment = SessionRequestCanonicalizer.CreateCommitment(request);

        Assert.Equal(SessionRequestManifestDefaults.CommitmentAlgorithm, commitment.Algorithm);
        Assert.Equal(1921, commitment.ByteLength);
        Assert.Equal(canonical.Length, commitment.ByteLength);
        Assert.Equal("dc714068ed5e60a5213cc7c673ca4f6c65a42caae0a8d60d224d0c5ac2d0fb95", commitment.Sha256);
        Assert.Equal(canonical, SessionRequestCanonicalizer.Canonicalize(request));
        Assert.Contains("\"modelId\":\"model-α\"", Encoding.UTF8.GetString(canonical), StringComparison.Ordinal);
        Assert.Contains("\"maxTokens\":4096", Encoding.UTF8.GetString(canonical), StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"reasoning\"", Encoding.UTF8.GetString(canonical), StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"tool-results\"", Encoding.UTF8.GetString(canonical), StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalRequest_RejectsContextHeaderAndUnknownMessageTypes() {
        var contextHeader = new UnsupportedHistoryMessage(HistoryMessageKind.ContextHeader);
        var unknown = new UnsupportedHistoryMessage(HistoryMessageKind.Observation);
        var derivedObservation = new DerivedObservationMessage("derived");

        Assert.Throws<InvalidOperationException>(() => SessionRequestCanonicalizer.Canonicalize(
            new CompletionRequest("model", "system", [contextHeader], [], null)
        ));
        Assert.Throws<InvalidOperationException>(() => SessionRequestCanonicalizer.Canonicalize(
            new CompletionRequest("model", "system", [unknown], [], null)
        ));
        Assert.Throws<InvalidOperationException>(() => SessionRequestCanonicalizer.Canonicalize(
            new CompletionRequest("model", "system", [derivedObservation], [], null)
        ));
    }

    [Fact]
    public void CanonicalRequestCommitment_ChangesForEachOfTheFiveRequestFields() {
        var baseline = new CompletionRequest(
            "model",
            "system",
            [new ObservationMessage("observation")],
            [],
            100
        );
        string baselineHash = SessionRequestCanonicalizer.CreateCommitment(baseline).Sha256;
        var changedTools = ImmutableArray.Create(
            new ToolDefinition("ping", "Ping tool", new ToolSchema.Object())
        );

        Assert.NotEqual(baselineHash, SessionRequestCanonicalizer.CreateCommitment(baseline with { ModelId = "model-2" }).Sha256);
        Assert.NotEqual(baselineHash, SessionRequestCanonicalizer.CreateCommitment(baseline with { SystemPrompt = "system-2" }).Sha256);
        Assert.NotEqual(
            baselineHash,
            SessionRequestCanonicalizer.CreateCommitment(baseline with {
                Context = [new ObservationMessage("observation-2")]
            }).Sha256
        );
        Assert.NotEqual(baselineHash, SessionRequestCanonicalizer.CreateCommitment(baseline with { Tools = changedTools }).Sha256);
        Assert.NotEqual(baselineHash, SessionRequestCanonicalizer.CreateCommitment(baseline with { MaxTokens = 101 }).Sha256);
    }

    [Fact]
    public void CanonicalRequestCommitment_IsSensitiveToReasoningAndToolResultFields() {
        var origin = new CompletionDescriptor("provider", "api-v1", "model");
        CompletionRequest CreateRequest(string reasoning, ToolExecutionStatus status, string resultText)
            => new(
                "model",
                "system",
                [
                    new ActionMessage([
                        new ActionBlock.TextReasoningBlock(reasoning, origin, "debug")
                    ]),
                    new ToolResultsMessage(
                        "tool observation",
                        [ToolResult.FromText("tool", "call-1", status, resultText)]
                    )
                ],
                [],
                null
            );

        string baseline = SessionRequestCanonicalizer.CreateCommitment(
            CreateRequest("reasoning-a", ToolExecutionStatus.Success, "result-a")
        ).Sha256;

        Assert.NotEqual(
            baseline,
            SessionRequestCanonicalizer.CreateCommitment(
                CreateRequest("reasoning-b", ToolExecutionStatus.Success, "result-a")
            ).Sha256
        );
        Assert.NotEqual(
            baseline,
            SessionRequestCanonicalizer.CreateCommitment(
                CreateRequest("reasoning-a", ToolExecutionStatus.Failed, "result-a")
            ).Sha256
        );
        Assert.NotEqual(
            baseline,
            SessionRequestCanonicalizer.CreateCommitment(
                CreateRequest("reasoning-a", ToolExecutionStatus.Success, "result-b")
            ).Sha256
        );
    }

    [Fact]
    public void ManifestValidation_RejectsToolSnapshotHashMismatchAndNonEmptyArtifactInputs() {
        var body = CreateManifestBody(CreateToolDefinitions(), out _);

        Assert.Throws<InvalidDataException>(() => SessionEventCodec.Encode(
            SessionEventKind.CompletionRequestPrepared,
            body with { ToolSet = body.ToolSet with { Sha256 = new string('0', 64) } }
        ));
        Assert.Throws<NotSupportedException>(() => SessionEventCodec.Encode(
            SessionEventKind.CompletionRequestPrepared,
            body with {
                Plan = body.Plan with {
                    ArtifactInputs = [
                        new SessionRequestArtifactInput("artifact-1", "rolling-summary", new string('1', 64))
                    ]
                }
            }
        ));
    }

    [Fact]
    public void ManifestValidation_RejectsInvalidSetupAndNonNullFullRawStartBeforeEncoding() {
        var body = CreateManifestBody(CreateToolDefinitions(), out _);

        Assert.Throws<ArgumentException>(() => SessionEventCodec.Encode(
            SessionEventKind.CompletionRequestPrepared,
            body with {
                Setups = body.Setups with {
                    RuntimeConfig = body.Setups.RuntimeConfig with { Address = default }
                }
            }
        ));
        var invalidRawStart = body with {
            Plan = body.Plan with { RawStartExclusive = body.Setups.SystemPrompt.Address }
        };
        Assert.Throws<InvalidDataException>(() => SessionRequestManifestCodec.Validate(invalidRawStart));
        Assert.Throws<InvalidDataException>(() => SessionEventCodec.Encode(
            SessionEventKind.CompletionRequestPrepared,
            invalidRawStart
        ));
        Assert.Throws<ArgumentException>(() => EventAddressTextCodec.Format(default));
    }

    [Fact]
    public void CompletionRequestPrepared_StrictDecodeRejectsUnknownPropertiesAtEveryManifestLayer() {
        string canonical = EncodeManifestJson();
        (string Marker, string Replacement)[] mutations = [
            ("{\"v\":1,", "{\"v\":1,\"unknownEnvelope\":true,"),
            ("\"body\":{\"attempt\":", "\"body\":{\"unknownBody\":true,\"attempt\":"),
            ("\"attempt\":{\"attemptId\":", "\"attempt\":{\"unknownAttempt\":true,\"attemptId\":"),
            ("\"plan\":{\"selectionPolicyId\":", "\"plan\":{\"unknownPlan\":true,\"selectionPolicyId\":"),
            ("\"setups\":{\"runtimeConfig\":", "\"setups\":{\"unknownSetups\":true,\"runtimeConfig\":"),
            ("\"runtimeConfig\":{\"address\":", "\"runtimeConfig\":{\"unknownSetup\":true,\"address\":"),
            ("\"parameters\":{\"modelId\":", "\"parameters\":{\"unknownParameters\":true,\"modelId\":"),
            ("\"toolSet\":{\"codecId\":", "\"toolSet\":{\"unknownToolSet\":true,\"codecId\":"),
            ("{\"name\":\"sample\",\"description\":", "{\"unknownDefinition\":true,\"name\":\"sample\",\"description\":"),
            ("\"inputSchema\":{\"kind\":\"object\",", "\"inputSchema\":{\"kind\":\"object\",\"unknownSchema\":true,"),
            ("{\"name\":\"withoutDefault\",\"required\":", "{\"unknownProperty\":true,\"name\":\"withoutDefault\",\"required\":"),
            ("\"rendering\":{\"contextRendererId\":", "\"rendering\":{\"unknownRendering\":true,\"contextRendererId\":"),
            ("\"target\":{\"connection\":", "\"target\":{\"unknownTarget\":true,\"connection\":"),
            ("\"connection\":{\"connectionId\":", "\"connection\":{\"unknownConnection\":true,\"connectionId\":"),
            ("\"commitment\":{\"algorithm\":", "\"commitment\":{\"unknownCommitment\":true,\"algorithm\":")
        ];

        foreach ((string marker, string replacement) in mutations) {
            AssertStrictDecodeRejected(ReplaceOnce(canonical, marker, replacement));
        }
    }

    [Fact]
    public void CompletionRequestPrepared_StrictDecodeRejectsDuplicatePropertiesIncludingToolSchema() {
        string canonical = EncodeManifestJson();
        (string Marker, string Replacement)[] mutations = [
            ("{\"v\":1,", "{\"v\":1,\"v\":1,"),
            ("\"body\":{\"attempt\":", "\"body\":{\"attempt\":{},\"attempt\":"),
            ("\"attempt\":{\"attemptId\":\"attempt-01\",", "\"attempt\":{\"attemptId\":\"other\",\"attemptId\":\"attempt-01\","),
            ("\"runtimeConfig\":{\"address\":", "\"runtimeConfig\":{\"address\":\"ej1:00000000000000010000000100000000\",\"address\":"),
            ("{\"name\":\"sample\",\"description\":", "{\"name\":\"other\",\"name\":\"sample\",\"description\":"),
            ("\"inputSchema\":{\"kind\":\"object\",", "\"inputSchema\":{\"kind\":\"array\",\"kind\":\"object\","),
            ("{\"name\":\"withoutDefault\",\"required\":", "{\"name\":\"duplicate\",\"name\":\"withoutDefault\",\"required\":"),
            ("\"connection\":{\"connectionId\":", "\"connection\":{\"connectionId\":\"other\",\"connectionId\":"),
            ("\"commitment\":{\"algorithm\":", "\"commitment\":{\"algorithm\":\"other\",\"algorithm\":")
        ];

        foreach ((string marker, string replacement) in mutations) {
            AssertStrictDecodeRejected(ReplaceOnce(canonical, marker, replacement));
        }
    }

    private string EncodeManifestJson() {
        var body = CreateManifestBody(CreateToolDefinitions(), out _);
        return Encoding.UTF8.GetString(
            SessionEventCodec.Encode(SessionEventKind.CompletionRequestPrepared, body)
        );
    }

    private static void AssertStrictDecodeRejected(string json) {
        Assert.Throws<InvalidDataException>(() => SessionEventCodec.Decode(
            SessionEventKind.CompletionRequestPrepared,
            Encoding.UTF8.GetBytes(json),
            out _
        ));
    }

    [Fact]
    public void ManifestValidation_RejectsCrossFieldSemanticDrift() {
        CompletionRequestPreparedBody body = CreateManifestBody(CreateToolDefinitions(), out _);

        Assert.Throws<InvalidDataException>(() => SessionRequestManifestCodec.Validate(
            body with { Attempt = body.Attempt with { Reason = "different" } }
        ));
        Assert.Throws<InvalidDataException>(() => SessionRequestManifestCodec.Validate(
            body with { Plan = body.Plan with { ModelProfileId = "different-model" } }
        ));
        Assert.Throws<NotSupportedException>(() => SessionRequestManifestCodec.Validate(
            body with { Plan = body.Plan with { SelectionPolicyId = "unknown-policy" } }
        ));
        Assert.Throws<NotSupportedException>(() => SessionRequestManifestCodec.Validate(
            body with { Plan = body.Plan with { PlannerFingerprint = "unknown-planner" } }
        ));
        Assert.Throws<NotSupportedException>(() => SessionRequestManifestCodec.Validate(
            body with {
                Rendering = body.Rendering with { ContextRendererFingerprint = "unknown-renderer" }
            }
        ));
    }

    private static string ReplaceOnce(string source, string marker, string replacement) {
        int index = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Mutation marker was not found: {marker}");
        return string.Concat(source.AsSpan(0, index), replacement, source.AsSpan(index + marker.Length));
    }

    private CompletionRequestPreparedBody CreateManifestBody(
        ImmutableArray<ToolDefinition> tools,
        out CompletionRequest request
    ) {
        (EventAddress runtime, EventAddress prompt, _) = CreateAddresses();
        request = new CompletionRequest(
            "model-α",
            "system <raw> & prompt",
            [new ObservationMessage("observe")],
            tools,
            4096
        );
        return new CompletionRequestPreparedBody(
            new SessionRequestAttempt("attempt-01", "correlation-01", "full raw fallback", null),
            new SessionContextPlan(
                SessionRequestManifestDefaults.SelectionPolicyId,
                SessionRequestManifestDefaults.PlannerFingerprint,
                null,
                new string('a', 64),
                [],
                [],
                SessionRequestManifestDefaults.RenderingProfileId,
                request.ModelId,
                123,
                "full raw fallback"
            ),
            new SessionGoverningSetupReferences(
                new SessionSetupReference(runtime, 1, new string('b', 64)),
                new SessionSetupReference(prompt, 1, new string('c', 64))
            ),
            new SessionRequestParameters(request.ModelId, request.MaxTokens),
            new SessionRequestToolSet(
                SessionRequestManifestDefaults.ToolCodecId,
                SessionRequestCanonicalizer.ComputeToolSetSha256(tools),
                tools
            ),
            new SessionRequestRendering(
                SessionRequestManifestDefaults.ContextRendererId,
                SessionRequestManifestDefaults.ContextRendererFingerprint,
                SessionRequestManifestDefaults.CanonicalRequestCodecId,
                SessionRequestManifestDefaults.ToolCodecId,
                SessionRequestManifestDefaults.ReasoningCodecSetFingerprint
            ),
            new SessionRequestTarget(
                new SessionCompletionTargetIdentity(
                    "dsv4p",
                    "openai-compatible",
                    "sha256:connection",
                    "sha256:connection-adapter"
                ),
                "responses",
                "OpenAIResponses",
                "openai-responses-v1"
            ),
            SessionRequestCanonicalizer.CreateCommitment(request)
        );
    }

    private (EventAddress Runtime, EventAddress Prompt, EventAddress RawStart) CreateAddresses() {
        using var journal = EventJournal.EventJournal.CreateNew(_tempPath);
        journal.CreateBranch("main", startPoint: null).Unwrap();
        EventAddress runtime = journal.CommitToRef("main", null, [1], opaqueEventKind: 1).Unwrap().EventAddress;
        EventAddress prompt = journal.CommitToRef("main", runtime, [2], opaqueEventKind: 2).Unwrap().EventAddress;
        EventAddress rawStart = journal.CommitToRef("main", prompt, [3], opaqueEventKind: 3).Unwrap().EventAddress;
        return (runtime, prompt, rawStart);
    }

    private static ImmutableArray<ToolDefinition> CreateToolDefinitions() {
        var schema = new ToolSchema.Object([
            new ToolSchema.Property(
                "withoutDefault",
                new ToolSchema.Value(ToolParamType.String, isNullable: true),
                isRequired: false
            ),
            new ToolSchema.Property(
                "explicitNull",
                new ToolSchema.Value(ToolParamType.String, isNullable: true, defaultValue: new ParamDefault(null)),
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
                new ToolSchema.Value(ToolParamType.Float32, defaultValue: new ParamDefault(1.25f)),
                isRequired: false
            ),
            new ToolSchema.Property(
                "price",
                new ToolSchema.Value(ToolParamType.Decimal, defaultValue: new ParamDefault(12.50m)),
                isRequired: false
            )
        ]);
        return [new ToolDefinition("sample", "Sample tool", schema)];
    }

    private static ImmutableArray<ToolDefinition> CreateComprehensiveToolDefinitions() {
        var nested = new ToolSchema.Object([
            new ToolSchema.Property(
                "name",
                new ToolSchema.Value(ToolParamType.String, description: "nested name"),
                isRequired: true
            )
        ], description: "nested object", example: "nested example");
        var complex = new ToolSchema.Object(
            [
                new ToolSchema.Property(
                    "enabled",
                    new ToolSchema.Value(ToolParamType.Boolean, defaultValue: new ParamDefault(true)),
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
                new ToolSchema.Property("nested", nested, isRequired: true)
            ],
            additionalProperties: true,
            description: "complex root",
            example: "root example"
        );
        return CreateToolDefinitions().Add(new ToolDefinition("complex", "Complex tool", complex));
    }

    private sealed record UnsupportedHistoryMessage(HistoryMessageKind Kind) : IHistoryMessage;

    private sealed record DerivedObservationMessage(string? Value) : ObservationMessage(Value);
}
