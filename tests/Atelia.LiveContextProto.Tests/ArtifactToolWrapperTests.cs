using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Xunit;

namespace Atelia.LiveContextProto.Tests;

public sealed class ArtifactToolWrapperTests {
    [Fact]
    public async Task Create_ExecuteAsync_BindsDefinitionAndInvokesHandler() {
        ArtifactEnvelope? captured = null;
        var wrapper = ArtifactToolWrapper<ArtifactEnvelope>.Create(
            "submit_artifact",
            (artifact, executionSequence) => {
                captured = artifact;
                return new ValidateResult(true, $"accepted sequence={executionSequence}");
            }
        );

        var definition = wrapper.Definition;
        Assert.Equal("submit_artifact", definition.Name);
        var schema = Assert.IsType<ToolSchema.Object>(definition.InputSchema);
        Assert.Contains(schema.Properties, static property => property.Name == "title" && property.IsRequired);
        Assert.Contains(schema.Properties, static property => property.Name == "tags" && property.IsRequired);
        Assert.Contains(schema.Properties, static property => property.Name == "kind" && property.IsRequired);
        Assert.DoesNotContain(schema.Properties, static property => property.Name == "Hidden");

        var request = new RawToolCall(
            "submit_artifact",
            "call-1",
            "{\"title\":\"alpha\",\"tags\":[\"focus\",\"tools\"],\"kind\":\"Note\"}"
        );

        var result = await wrapper.ExecuteAsync(CreateExecutionRequest(request), CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.Success, result.Status);
        Assert.Equal("accepted sequence=1", result.Content);
        Assert.NotNull(captured);
        Assert.Equal("alpha", captured!.Title);
        Assert.Equal(["focus", "tools"], captured.Tags);
        Assert.Equal(ArtifactKind.Note, captured.Kind);
        Assert.Equal("server", captured.Hidden);
    }

    [Fact]
    public async Task ExecuteAsync_SchemaParseFailure_ReturnsFailedResultWithoutInvokingHandler() {
        var invoked = false;
        var wrapper = ArtifactToolWrapper<ArtifactEnvelope>.Create(
            "submit_artifact",
            (_, _) => {
                invoked = true;
                return new ValidateResult(true, "should not be reached");
            }
        );
        const string rawArguments = "{\"title\":123,\"tags\":[\"focus\"],\"kind\":\"Note\"}";

        var result = await wrapper.ExecuteAsync(CreateExecutionRequest(new RawToolCall("submit_artifact", "call-2", rawArguments)), CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.False(invoked);
        Assert.Contains("工具参数解析失败。", result.Content, StringComparison.Ordinal);
        Assert.Contains("title:expected_string", result.Content, StringComparison.Ordinal);
        Assert.Contains($"raw_arguments_json: {rawArguments}", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_DataAnnotationsValidationFailure_ReturnsFailedResult() {
        var invoked = false;
        var wrapper = ArtifactToolWrapper<ArtifactEnvelope>.Create(
            "submit_artifact",
            (_, _) => {
                invoked = true;
                return new ValidateResult(true, "should not be reached");
            }
        );
        const string rawArguments = "{\"title\":\"abc\",\"tags\":[],\"kind\":\"Note\"}";

        var result = await wrapper.ExecuteAsync(CreateExecutionRequest(new RawToolCall("submit_artifact", "call-3", rawArguments)), CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.False(invoked);
        Assert.Contains("工具参数验证失败。", result.Content, StringComparison.Ordinal);
        Assert.Contains("title:", result.Content, StringComparison.Ordinal);
        Assert.Contains("tags:", result.Content, StringComparison.Ordinal);
        Assert.Contains($"raw_arguments_json: {rawArguments}", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_HandlerReturnsInvalid_MapsToFailedResult() {
        var wrapper = ArtifactToolWrapper<ArtifactEnvelope>.Create(
            "submit_artifact",
            (artifact, executionSequence) => new ValidateResult(false, $"duplicate title '{artifact.Title}' at sequence {executionSequence}")
        );

        var result = await wrapper.ExecuteAsync(
            CreateExecutionRequest(new RawToolCall("submit_artifact", "call-4", "{\"title\":\"alpha-ok\",\"tags\":[\"focus\"],\"kind\":\"Todo\"}")),
            CancellationToken.None
        );

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Contains("产物校验失败。", result.Content, StringComparison.Ordinal);
        Assert.Contains("duplicate title 'alpha-ok' at sequence 1", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_UsesExecutionSequenceProvidedByRequest() {
        var sequences = new List<long>();
        var wrapper = ArtifactToolWrapper<ArtifactEnvelope>.Create(
            "submit_artifact",
            (artifact, executionSequence) => {
                sequences.Add(executionSequence);
                return new ValidateResult(true, $"accepted {artifact.Title} seq={executionSequence}");
            }
        );

        var first = await wrapper.ExecuteAsync(
            CreateExecutionRequest(new RawToolCall("submit_artifact", "call-5", "{\"title\":\"alpha-ok\",\"tags\":[\"focus\"],\"kind\":\"Note\"}"), executionSequence: 7),
            CancellationToken.None
        );
        var second = await wrapper.ExecuteAsync(
            CreateExecutionRequest(new RawToolCall("submit_artifact", "call-6", "{\"title\":\"beta-ok\",\"tags\":[\"tools\"],\"kind\":\"Todo\"}"), executionSequence: 42),
            CancellationToken.None
        );

        Assert.Equal(ToolExecutionStatus.Success, first.Status);
        Assert.Equal(ToolExecutionStatus.Success, second.Status);
        Assert.Equal([7L, 42L], sequences);
        Assert.Contains("seq=7", first.Content, StringComparison.Ordinal);
        Assert.Contains("seq=42", second.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_HandlerValidationFailure_PreservesProvidedSequence() {
        var sequences = new List<long>();
        var wrapper = ArtifactToolWrapper<ArtifactEnvelope>.Create(
            "submit_artifact",
            (_, executionSequence) => {
                sequences.Add(executionSequence);
                return executionSequence == 11
                    ? new ValidateResult(false, $"reject seq={executionSequence}")
                    : new ValidateResult(true, $"accept seq={executionSequence}");
            }
        );

        var failed = await wrapper.ExecuteAsync(
            CreateExecutionRequest(new RawToolCall("submit_artifact", "call-5", "{\"title\":\"alpha-ok\",\"tags\":[\"focus\"],\"kind\":\"Note\"}"), executionSequence: 11),
            CancellationToken.None
        );
        var succeeded = await wrapper.ExecuteAsync(
            CreateExecutionRequest(new RawToolCall("submit_artifact", "call-6", "{\"title\":\"beta-ok\",\"tags\":[\"tools\"],\"kind\":\"Todo\"}"), executionSequence: 12),
            CancellationToken.None
        );

        Assert.Equal(ToolExecutionStatus.Failed, failed.Status);
        Assert.Equal(ToolExecutionStatus.Success, succeeded.Status);
        Assert.Equal([11L, 12L], sequences);
        Assert.Contains("reject seq=11", failed.Content, StringComparison.Ordinal);
        Assert.Contains("accept seq=12", succeeded.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_RecursivelyValidatesNestedObjectGraph() {
        var invoked = false;
        var wrapper = ArtifactToolWrapper<NestedArtifactEnvelope>.Create(
            "submit_nested_artifact",
            (_, _) => {
                invoked = true;
                return new ValidateResult(true, "should not be reached");
            }
        );
        const string rawArguments = "{\"title\":\"alpha-ok\",\"metadata\":{\"owner\":\"xy\"}}";

        var result = await wrapper.ExecuteAsync(CreateExecutionRequest(new RawToolCall("submit_nested_artifact", "call-nested", rawArguments)), CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.False(invoked);
        Assert.Contains("工具参数验证失败。", result.Content, StringComparison.Ordinal);
        Assert.Contains("metadata.owner:", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_IgnoresValidationOnJsonIgnoredMembers() {
        var invoked = false;
        var wrapper = ArtifactToolWrapper<IgnoredValidationArtifact>.Create(
            "submit_ignored_validation_artifact",
            (_, _) => {
                invoked = true;
                return new ValidateResult(true, "accepted");
            }
        );

        var result = await wrapper.ExecuteAsync(
            CreateExecutionRequest(new RawToolCall("submit_ignored_validation_artifact", "call-ignored", "{\"title\":\"alpha-ok\"}")),
            CancellationToken.None
        );

        Assert.Equal(ToolExecutionStatus.Success, result.Status);
        Assert.True(invoked);
        Assert.Equal("accepted", result.Content);
    }

    [Fact]
    public async Task ToolExecutor_WithArtifactToolWrapper_ExecutesThroughToolLoop() {
        var wrapper = ArtifactToolWrapper<ArtifactEnvelope>.Create(
            "submit_artifact",
            (artifact, executionSequence) => new ValidateResult(true, $"accepted {artifact.Kind} seq={executionSequence}")
        );
        var executor = new ToolExecutor([wrapper]);

        var result = await executor.ExecuteAsync(
            new RawToolCall("submit_artifact", "call-executor", "{\"title\":\"gamma-ok\",\"tags\":[\"loop\"],\"kind\":\"Todo\"}"),
            CancellationToken.None
        );

        Assert.Equal("submit_artifact", result.ToolName);
        Assert.Equal("call-executor", result.ToolCallId);
        Assert.Equal(ToolExecutionStatus.Success, result.ExecuteResult.Status);
        Assert.Contains("accepted Todo seq=1", result.ExecuteResult.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolExecutor_MissingToolAttempt_ConsumesSequenceBeforeArtifactToolExecutes() {
        var wrapper = ArtifactToolWrapper<ArtifactEnvelope>.Create(
            "submit_artifact",
            (_, executionSequence) => new ValidateResult(true, $"accepted seq={executionSequence}")
        );
        var executor = new ToolExecutor([wrapper]);

        var missingResult = await executor.ExecuteAsync(
            new RawToolCall("missing_tool", "call-missing", "{}"),
            CancellationToken.None
        );
        var artifactResult = await executor.ExecuteAsync(
            new RawToolCall("submit_artifact", "call-executor-2", "{\"title\":\"delta-ok\",\"tags\":[\"loop\"],\"kind\":\"Note\"}"),
            CancellationToken.None
        );

        Assert.Equal(ToolExecutionStatus.Failed, missingResult.ExecuteResult.Status);
        Assert.Equal("missing_tool", missingResult.ToolName);
        Assert.Contains("未找到工具: missing_tool", missingResult.ExecuteResult.Content, StringComparison.Ordinal);
        Assert.Equal(ToolExecutionStatus.Success, artifactResult.ExecuteResult.Status);
        Assert.Contains("accepted seq=2", artifactResult.ExecuteResult.Content, StringComparison.Ordinal);
    }

    private static ToolExecutionRequest CreateExecutionRequest(RawToolCall rawToolCall, long executionSequence = 1)
        => new(rawToolCall, executionSequence);

    [Description("Accept a structured artifact from the model.")]
    private sealed record class ArtifactEnvelope(
        [property: Description("Artifact title.")]
        [property: JsonPropertyName("title")]
        [property: MinLength(5)]
        string Title,
        [property: Description("Artifact tags.")]
        [property: JsonPropertyName("tags")]
        [property: MinLength(1)]
        IReadOnlyList<string> Tags,
        [property: Description("Artifact kind.")]
        [property: JsonPropertyName("kind")]
        ArtifactKind Kind,
        [property: JsonIgnore]
        string Hidden = "server"
    );

    private enum ArtifactKind {
        Note,
        Todo
    }

    [Description("Accept a structured artifact with nested metadata.")]
    private sealed record class NestedArtifactEnvelope(
        [property: Description("Artifact title.")]
        [property: JsonPropertyName("title")]
        [property: MinLength(5)]
        string Title,
        [property: Description("Artifact metadata.")]
        [property: JsonPropertyName("metadata")]
        NestedArtifactMetadata Metadata
    );

    private sealed class NestedArtifactMetadata {
        [Description("Owner handle.")]
        [JsonPropertyName("owner")]
        [MinLength(3)]
        public string Owner { get; init; } = string.Empty;
    }

    [Description("Artifact with ignored internal validation members.")]
    private sealed record class IgnoredValidationArtifact(
        [property: Description("Artifact title.")]
        [property: JsonPropertyName("title")]
        [property: MinLength(5)]
        string Title,
        [property: JsonIgnore]
        [property: Required]
        string? InternalOnly = null
    );
}
