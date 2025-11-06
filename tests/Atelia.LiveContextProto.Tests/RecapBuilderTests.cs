using System;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Atelia.Agent.Core.History;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.LiveContextProto.Tests;

public class RecapBuilderTests {
    private sealed class PassthroughTokenEstimator : ITokenEstimator {
        public uint Estimate(string? contents) => (uint)(contents?.Length ?? 0);
    }

    [Fact]
    public void UpdateRecap_ShouldRefreshTokenEstimate() {
        using var estimatorScope = TokenEstimateHelper.GetDefault().BeginScopedOverride(new PassthroughTokenEstimator());

        var builder = CreateBuilderWithTwoPairs("AAA");

        Assert.Equal((uint)53, builder.CurrentTokenEstimate);
        Assert.Equal((uint)3, builder.RecapTokenEstimate);

        builder.UpdateRecap("AAAA");

        Assert.Equal((uint)54, builder.CurrentTokenEstimate);
        Assert.Equal((uint)4, builder.RecapTokenEstimate);
    }

    [Fact]
    public void Dequeue_ShouldReduceTokenEstimate() {
        using var estimatorScope = TokenEstimateHelper.GetDefault().BeginScopedOverride(new PassthroughTokenEstimator());

        var builder = CreateBuilderWithTwoPairs("AAA");

        var initialTotal = builder.CurrentTokenEstimate;

        Assert.True(builder.TryDequeueNextPair(out var dequeued));
        Assert.True(dequeued.HasValue);
        Assert.Equal((uint)30, dequeued.Value.TokenEstimate);
        Assert.Equal(initialTotal - dequeued.Value.TokenEstimate, builder.CurrentTokenEstimate);
        Assert.Equal(builder.PendingPairs[0].Action.Serial, builder.FirstPendingSerial);

        Assert.False(builder.TryDequeueNextPair(out _));
        Assert.True(builder.HasPendingPairs);
        Assert.Equal(1, builder.PendingPairCount);

        var remaining = builder.PendingPairs[0];
        Assert.Equal(builder.RecapTokenEstimate + remaining.TokenEstimate, builder.CurrentTokenEstimate);
        Assert.Equal(remaining.Action.Serial, builder.FirstPendingSerial);
    }

    [Fact]
    public void TryDequeueNextPair_ShouldNotRemoveLastPair() {
        using var estimatorScope = TokenEstimateHelper.GetDefault().BeginScopedOverride(new PassthroughTokenEstimator());

        var builder = CreateBuilderWithSinglePair("Recap");

        Assert.False(builder.TryDequeueNextPair(out _));
        Assert.True(builder.HasPendingPairs);
        Assert.Equal(1, builder.PendingPairCount);
        Assert.Equal(builder.PendingPairs[0].Action.Serial, builder.FirstPendingSerial);
    }

    [Fact]
    public void CreateSnapshot_WithRecapOnlyEntry_ShouldThrow() {
        const string recapText = "ABCD";

        var recapEntry = new RecapEntry(recapText, 0);
        recapEntry.AssignSerial(1);
        recapEntry.AssignTokenEstimate((uint)recapText.Length);

        var entries = Array.AsReadOnly(new HistoryEntry[] { recapEntry });

        Assert.Throws<InvalidOperationException>(() => RecapBuilder.CreateSnapshot(entries));
    }

    [Fact]
    public void CreateSnapshot_WithRecapAndActionOnly_ShouldThrow() {
        var descriptor = new CompletionDescriptor("Provider", "Spec", "Model");

        var recapEntry = new RecapEntry("Recap", 0);
        recapEntry.AssignSerial(1);
        recapEntry.AssignTokenEstimate(5);

        var actionEntry = new ActionEntry("Action", ImmutableArray<ParsedToolCall>.Empty, descriptor);
        actionEntry.AssignSerial(2);
        actionEntry.AssignTokenEstimate(10);

        var entries = Array.AsReadOnly(new HistoryEntry[] { recapEntry, actionEntry });

        Assert.Throws<InvalidOperationException>(() => RecapBuilder.CreateSnapshot(entries));
    }

    private static RecapBuilder CreateBuilderWithTwoPairs(string recapText) {
        var descriptor = new CompletionDescriptor("Provider", "Spec", "Model");

        var recapEntry = new RecapEntry(recapText, 0);
        recapEntry.AssignSerial(1);
        recapEntry.AssignTokenEstimate((uint)recapText.Length);

        var action1 = new ActionEntry("Action1", ImmutableArray<ParsedToolCall>.Empty, descriptor);
        action1.AssignSerial(2);
        action1.AssignTokenEstimate(10);

        var observation1 = new ObservationEntry();
        observation1.AssignSerial(3);
        observation1.AssignTokenEstimate(20);

        var action2 = new ActionEntry("Action2", ImmutableArray<ParsedToolCall>.Empty, descriptor);
        action2.AssignSerial(4);
        action2.AssignTokenEstimate(5);

        var observation2 = new ObservationEntry();
        observation2.AssignSerial(5);
        observation2.AssignTokenEstimate(15);

        ReadOnlyCollection<HistoryEntry> entries = Array.AsReadOnly(
            new HistoryEntry[] {
                recapEntry,
                action1,
                observation1,
                action2,
                observation2
        }
        );

        return RecapBuilder.CreateSnapshot(entries);
    }

    private static RecapBuilder CreateBuilderWithSinglePair(string recapText) {
        var descriptor = new CompletionDescriptor("Provider", "Spec", "Model");

        var recapEntry = new RecapEntry(recapText, 0);
        recapEntry.AssignSerial(1);
        recapEntry.AssignTokenEstimate((uint)recapText.Length);

        var action1 = new ActionEntry("Action", ImmutableArray<ParsedToolCall>.Empty, descriptor);
        action1.AssignSerial(2);
        action1.AssignTokenEstimate(10);

        var observation1 = new ObservationEntry();
        observation1.AssignSerial(3);
        observation1.AssignTokenEstimate(20);

        ReadOnlyCollection<HistoryEntry> entries = Array.AsReadOnly(
            new HistoryEntry[] {
                recapEntry,
                action1,
                observation1
            }
        );

        return RecapBuilder.CreateSnapshot(entries);
    }
}
