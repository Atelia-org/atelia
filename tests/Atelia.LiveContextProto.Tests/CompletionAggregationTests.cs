using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.LiveContextProto.Tests;

public sealed class CompletionChunkAggregationTests {
    private static readonly CompletionDescriptor Descriptor = new("anthropic", "anthropic-messages-v1", "claude-3");

    private static async IAsyncEnumerable<CompletionChunk> ToAsync(IEnumerable<CompletionChunk> chunks) {
        foreach (var chunk in chunks) {
            yield return chunk;
            await Task.Yield();
        }
    }

    [Fact]
    public async Task AggregateAsync_PreservesTextThinkingTextOrdering() {
        var thinkingPayload = JsonSerializer.SerializeToUtf8Bytes(new {
            type = "thinking",
            thinking = "deliberation",
            signature = "sig"
        });

        var chunks = new[] {
            CompletionChunk.FromContent("alpha"),
            CompletionChunk.FromThinking(new ThinkingChunk(thinkingPayload, "deliberation")),
            CompletionChunk.FromContent("omega")
        };

        var entry = await ToAsync(chunks).AggregateAsync(Descriptor, CancellationToken.None);

        Assert.Collection(
            entry.Blocks,
            block => Assert.Equal("alpha", Assert.IsType<ActionBlock.Text>(block).Content),
            block => {
                var thinking = Assert.IsType<ActionBlock.Thinking>(block);
                Assert.Same(Descriptor, thinking.Origin);
                Assert.Equal("deliberation", thinking.PlainTextForDebug);
                using var doc = JsonDocument.Parse(thinking.OpaquePayload);
                Assert.Equal("thinking", doc.RootElement.GetProperty("type").GetString());
            },
            block => Assert.Equal("omega", Assert.IsType<ActionBlock.Text>(block).Content)
        );

        // Compatibility view: GetFlattenedText() concatenates only Text blocks, Thinking is excluded
        Assert.Equal("alphaomega", entry.GetFlattenedText());
    }

    [Fact]
    public async Task AggregateAsync_InjectsCurrentInvocationAsThinkingOrigin() {
        var payload = Encoding.UTF8.GetBytes("""{"type":"thinking","thinking":"x","signature":"s"}""");
        var customDescriptor = new CompletionDescriptor("anthropic", "anthropic-messages-v1", "claude-opus-4");

        var entry = await ToAsync(new[] {
                CompletionChunk.FromThinking(new ThinkingChunk(payload, null))
            }).AggregateAsync(
                customDescriptor,
                CancellationToken.None
            );

        var thinking = Assert.IsType<ActionBlock.Thinking>(entry.Blocks.Single());
        Assert.Same(customDescriptor, thinking.Origin);
    }

    [Fact]
    public async Task AggregateAsync_CollectsErrorsAndUsageWithoutTurningThemIntoBlocks() {
        var usage = new TokenUsage(11, 7, 3);
        var entry = await ToAsync(new[] {
                CompletionChunk.FromContent("alpha"),
                CompletionChunk.FromError("boom-1"),
                CompletionChunk.FromError("boom-2"),
                CompletionChunk.FromTokenUsage(usage),
            }).AggregateAsync(
                Descriptor,
                CancellationToken.None
            );

        Assert.Collection(
            entry.Blocks,
            block => Assert.Equal("alpha", Assert.IsType<ActionBlock.Text>(block).Content)
        );
        Assert.Equal("alpha", entry.GetFlattenedText());
        Assert.Equal(usage, entry.Usage);
        Assert.Equal(new[] { "boom-1", "boom-2" }, entry.Errors);
    }

    [Fact]
    public async Task AggregateAsync_ReturnsSingleEmptyTextBlock_ForEmptyStream() {
        var entry = await ToAsync(Array.Empty<CompletionChunk>()).AggregateAsync(Descriptor, CancellationToken.None);

        Assert.Collection(
            entry.Blocks,
            block => Assert.Equal(string.Empty, Assert.IsType<ActionBlock.Text>(block).Content)
        );
        Assert.Equal(string.Empty, entry.GetFlattenedText());
        Assert.Empty(((IActionMessage)entry).ToolCalls);
        Assert.Null(entry.Errors);
        Assert.Null(entry.Usage);
    }

    [Fact]
    public async Task AggregateAsync_ReturnsSingleEmptyTextBlock_ForMetaOnlyStream() {
        var usage = new TokenUsage(5, 2);
        var entry = await ToAsync(new[] {
                CompletionChunk.FromError("recoverable"),
                CompletionChunk.FromTokenUsage(usage),
            }).AggregateAsync(
                Descriptor,
                CancellationToken.None
            );

        Assert.Collection(
            entry.Blocks,
            block => Assert.Equal(string.Empty, Assert.IsType<ActionBlock.Text>(block).Content)
        );
        Assert.Equal(string.Empty, entry.GetFlattenedText());
        Assert.Equal(new[] { "recoverable" }, entry.Errors);
        Assert.Equal(usage, entry.Usage);
    }
}
