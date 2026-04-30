using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.LiveContextProto.Tests;

public sealed class CompletionAggregationTests {
    private static readonly CompletionDescriptor Descriptor = new("anthropic", "anthropic-messages-v1", "claude-3");

    private static AggregatedAction Aggregate(
        Action<CompletionAggregator> feed,
        CompletionDescriptor? descriptor = null,
        CompletionStreamObserver? observer = null
    ) {
        var aggregator = new CompletionAggregator(descriptor ?? Descriptor, observer);
        feed(aggregator);
        return aggregator.Build();
    }

    [Fact]
    public void AppendBuild_PreservesTextThinkingTextOrdering() {
        var thinkingPayload = JsonSerializer.SerializeToUtf8Bytes(new {
            type = "thinking",
            thinking = "deliberation",
            signature = "sig"
        });

        var entry = Aggregate(agg => {
            agg.AppendContent("alpha");
            agg.AppendThinking(new ThinkingChunk(thinkingPayload, "deliberation"));
            agg.AppendContent("omega");
        });

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
    public void AppendBuild_InjectsCurrentInvocationAsThinkingOrigin() {
        var payload = Encoding.UTF8.GetBytes("""{"type":"thinking","thinking":"x","signature":"s"}""");
        var customDescriptor = new CompletionDescriptor("anthropic", "anthropic-messages-v1", "claude-opus-4");

        var entry = Aggregate(
            agg => agg.AppendThinking(new ThinkingChunk(payload, null)),
            descriptor: customDescriptor
        );

        var thinking = Assert.IsType<ActionBlock.Thinking>(entry.Blocks.Single());
        Assert.Same(customDescriptor, thinking.Origin);
    }

    [Fact]
    public void AppendBuild_CollectsErrorsWithoutTurningThemIntoBlocks() {
        var entry = Aggregate(agg => {
            agg.AppendContent("alpha");
            agg.AppendError("boom-1");
            agg.AppendError("boom-2");
        });

        Assert.Collection(
            entry.Blocks,
            block => Assert.Equal("alpha", Assert.IsType<ActionBlock.Text>(block).Content)
        );
        Assert.Equal("alpha", entry.GetFlattenedText());
        Assert.Equal(new[] { "boom-1", "boom-2" }, entry.Errors);
    }

    [Fact]
    public void AppendBuild_ReturnsSingleEmptyTextBlock_ForEmptyStream() {
        var entry = Aggregate(_ => { });

        Assert.Collection(
            entry.Blocks,
            block => Assert.Equal(string.Empty, Assert.IsType<ActionBlock.Text>(block).Content)
        );
        Assert.Equal(string.Empty, entry.GetFlattenedText());
        Assert.Empty(((IActionMessage)entry).ToolCalls);
        Assert.Null(entry.Errors);
    }

    [Fact]
    public void AppendBuild_ReturnsSingleEmptyTextBlock_ForMetaOnlyStream() {
        var entry = Aggregate(agg => {
            agg.AppendError("recoverable");
        });

        Assert.Collection(
            entry.Blocks,
            block => Assert.Equal(string.Empty, Assert.IsType<ActionBlock.Text>(block).Content)
        );
        Assert.Equal(string.Empty, entry.GetFlattenedText());
        Assert.Equal(new[] { "recoverable" }, entry.Errors);
    }

    [Fact]
    public void AbortIncompleteStreamingState_BalancesThinkingLifecycleWithoutPersistingPartialBlock() {
        var observer = new CompletionStreamObserver();
        var thinkingBeginCount = 0;
        var thinkingEndCount = 0;
        observer.ReceivedThinkingBegin += () => thinkingBeginCount++;
        observer.ReceivedThinkingEnd += () => thinkingEndCount++;

        var entry = Aggregate(
            agg => {
                agg.BeginThinking();
                agg.AppendReasoningDelta("partial");
                agg.AbortIncompleteStreamingState();
            },
            observer: observer
        );

        Assert.Equal(1, thinkingBeginCount);
        Assert.Equal(1, thinkingEndCount);
        Assert.DoesNotContain(entry.Blocks, block => block.Kind == ActionBlockKind.Thinking);
        var text = Assert.Single(entry.Blocks);
        Assert.Equal(string.Empty, Assert.IsType<ActionBlock.Text>(text).Content);
    }
}
