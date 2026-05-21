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
            (sequence, artifact) => {
                captured = artifact;
                return new ValidateResult(true, $"accepted sequence={sequence}");
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

        var result = await wrapper.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.Success, result.Status);
        Assert.Equal("accepted sequence=0", result.Content);
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

        var result = await wrapper.ExecuteAsync(new RawToolCall("submit_artifact", "call-2", rawArguments), CancellationToken.None);

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

        var result = await wrapper.ExecuteAsync(new RawToolCall("submit_artifact", "call-3", rawArguments), CancellationToken.None);

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
            (sequence, artifact) => new ValidateResult(false, $"duplicate title '{artifact.Title}' at sequence {sequence}")
        );

        var result = await wrapper.ExecuteAsync(
            new RawToolCall("submit_artifact", "call-4", "{\"title\":\"alpha-ok\",\"tags\":[\"focus\"],\"kind\":\"Todo\"}"),
            CancellationToken.None
        );

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Contains("产物校验失败。", result.Content, StringComparison.Ordinal);
        Assert.Contains("duplicate title 'alpha-ok' at sequence 0", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_SequenceIncrementsPerWrapperInstance() {
        var sequences = new List<int>();
        var wrapper = ArtifactToolWrapper<ArtifactEnvelope>.Create(
            "submit_artifact",
            (sequence, artifact) => {
                sequences.Add(sequence);
                return new ValidateResult(true, $"accepted {artifact.Title} seq={sequence}");
            }
        );

        var first = await wrapper.ExecuteAsync(
            new RawToolCall("submit_artifact", "call-5", "{\"title\":\"alpha-ok\",\"tags\":[\"focus\"],\"kind\":\"Note\"}"),
            CancellationToken.None
        );
        var second = await wrapper.ExecuteAsync(
            new RawToolCall("submit_artifact", "call-6", "{\"title\":\"beta-ok\",\"tags\":[\"tools\"],\"kind\":\"Todo\"}"),
            CancellationToken.None
        );

        Assert.Equal(ToolExecutionStatus.Success, first.Status);
        Assert.Equal(ToolExecutionStatus.Success, second.Status);
        Assert.Equal([0, 1], sequences);
        Assert.Contains("seq=0", first.Content, StringComparison.Ordinal);
        Assert.Contains("seq=1", second.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolExecutor_WithArtifactToolWrapper_ExecutesThroughToolLoop() {
        var wrapper = ArtifactToolWrapper<ArtifactEnvelope>.Create(
            "submit_artifact",
            (sequence, artifact) => new ValidateResult(true, $"accepted {artifact.Kind} seq={sequence}")
        );
        var executor = new ToolExecutor([wrapper]);

        var result = await executor.ExecuteAsync(
            new RawToolCall("submit_artifact", "call-executor", "{\"title\":\"gamma-ok\",\"tags\":[\"loop\"],\"kind\":\"Todo\"}"),
            CancellationToken.None
        );

        Assert.Equal("submit_artifact", result.ToolName);
        Assert.Equal("call-executor", result.ToolCallId);
        Assert.Equal(ToolExecutionStatus.Success, result.ExecuteResult.Status);
        Assert.Contains("accepted Todo seq=0", result.ExecuteResult.Content, StringComparison.Ordinal);
    }

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
}
