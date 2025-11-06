using System.Collections.Immutable;
using Atelia.Agent.Core.History;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.LiveContextProto.Tests;

public class AgentStateRecapTests {
    private static readonly CompletionDescriptor Descriptor = new("provider", "spec", "model");

    [Fact]
    public void CommitRecapBuilder_RemovesDequeuedPairsAndInsertsRecap() {
        var state = AgentState.CreateDefault();

        state.AppendObservation(new ObservationEntry());

        var action1 = new ActionEntry("action-1", ImmutableArray<ParsedToolCall>.Empty, Descriptor);
        state.AppendAction(action1);

        var observation1 = new ObservationEntry();
        state.AppendObservation(observation1);

        var action2 = new ActionEntry("action-2", ImmutableArray<ParsedToolCall>.Empty, Descriptor);
        state.AppendAction(action2);

        var observation2 = new ObservationEntry();
        state.AppendObservation(observation2);

        var builder = state.GetRecapBuilder();

        Assert.True(builder.TryDequeueNextPair(out var dequeuedPair));
        Assert.True(dequeuedPair.HasValue);

        builder.UpdateRecap("Recap Summary");

        var result = state.CommitRecapBuilder(builder);

        Assert.Null(result.ErrorMessage);
        Assert.Equal(3, result.RemovedEntryCount);
        Assert.True(result.RecapEntrySerial > 0);

        var history = state.RecentHistory;
        var recapEntry = Assert.IsType<RecapEntry>(history[0]);
        Assert.Equal("Recap Summary", recapEntry.Content);
        Assert.Equal(dequeuedPair.Value.Observation.Serial, recapEntry.InsteadSerial);

        Assert.Same(action2, history[1]);
        Assert.Same(observation2, history[2]);

        var context = state.RenderLiveContext();
        var recapMessage = Assert.IsType<ObservationMessage>(context[0]);
        Assert.Equal("Recap Summary", recapMessage.Content);
    }

    [Fact]
    public void CommitRecapBuilder_WithTextOnlyUpdate_ReplacesHeadEntry() {
        var state = AgentState.CreateDefault();

        var observation0 = new ObservationEntry();
        state.AppendObservation(observation0);
        var anchorSerial = observation0.Serial;

        var action1 = new ActionEntry("action-1", ImmutableArray<ParsedToolCall>.Empty, Descriptor);
        state.AppendAction(action1);

        var observation1 = new ObservationEntry();
        state.AppendObservation(observation1);

        var builder = state.GetRecapBuilder();
        builder.UpdateRecap("Initial Recap");

        var result = state.CommitRecapBuilder(builder);

        Assert.Null(result.ErrorMessage);
        Assert.Equal(1, result.RemovedEntryCount);

        var history = state.RecentHistory;
        var recapEntry = Assert.IsType<RecapEntry>(history[0]);
        Assert.Equal("Initial Recap", recapEntry.Content);
        Assert.Equal(anchorSerial, recapEntry.InsteadSerial);

        Assert.Same(action1, history[1]);
        Assert.Same(observation1, history[2]);
    }
}
