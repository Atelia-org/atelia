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

        Assert.Throws<InvalidOperationException>(() => SessionRequestCanonicalizer.Canonicalize(
            new CompletionRequest("model", "system", [contextHeader], [], null)
        ));
        Assert.Throws<InvalidOperationException>(() => SessionRequestCanonicalizer.Canonicalize(
            new CompletionRequest("model", "system", [unknown], [], null)
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

    private CompletionRequestPreparedBody CreateManifestBody(
        ImmutableArray<ToolDefinition> tools,
        out CompletionRequest request
    ) {
        (EventAddress runtime, EventAddress prompt, EventAddress rawStart) = CreateAddresses();
        request = new CompletionRequest(
            "model-α",
            "system <raw> & prompt",
            [new ObservationMessage("observe")],
            tools,
            4096
        );
        return new CompletionRequestPreparedBody(
            new SessionRequestAttempt("attempt-01", "correlation-01", "new observation", null),
            new SessionContextPlan(
                "full-raw-v1",
                "sha256:planner",
                rawStart,
                new string('a', 64),
                [],
                [],
                "session-context-v1",
                "model-profile-v1",
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
                "session-context-v1",
                "sha256:renderer",
                SessionRequestManifestDefaults.CanonicalRequestCodecId,
                SessionRequestManifestDefaults.ToolCodecId,
                "sha256:reasoning-codecs"
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
                "openai-responses-v1",
                "sha256:request-adapter"
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

    private sealed record UnsupportedHistoryMessage(HistoryMessageKind Kind) : IHistoryMessage;
}
