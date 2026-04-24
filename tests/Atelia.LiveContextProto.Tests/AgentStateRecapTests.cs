using System.Collections.Immutable;
using System.Text.Json;
using System.Reflection;
using Atelia.Agent.Core.History;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.LiveContextProto.Tests;

public class AgentStateRecapTests {
    private static readonly CompletionDescriptor Descriptor = new("provider", "spec", "model");
    private static readonly CompletionDescriptor OtherDescriptor = new("other-provider", "other-spec", "other-model");

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

        var context = state.ProjectInvocationContext(new ContextProjectionOptions()).ToFlat();
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

    [Fact]
    public void ProjectInvocationContext_SplitsStablePrefixAndActiveTurnTailAtTurnBoundary() {
        var state = AgentState.CreateDefault();

        var observation1 = new ObservationEntry();
        state.AppendObservation(observation1);

        var action1 = new ActionEntry("action-1", ImmutableArray<ParsedToolCall>.Empty, Descriptor);
        state.AppendAction(action1);

        var observation2 = new ObservationEntry();
        state.AppendObservation(observation2);

        var action2 = new ActionEntry("action-2", ImmutableArray<ParsedToolCall>.Empty, Descriptor);
        state.AppendAction(action2);

        var projection = state.ProjectInvocationContext(new ContextProjectionOptions(TargetInvocation: Descriptor));

        Assert.Collection(
            projection.StablePrefix,
            message => Assert.IsType<ObservationMessage>(message),
            message => {
                var action = Assert.IsType<ProjectedActionMessage>(message);
                Assert.Equal("action-1", action.Content);
            }
        );

        Assert.Collection(
            projection.ActiveTurnTail,
            message => Assert.IsType<ObservationMessage>(message),
            message => {
                var action = Assert.IsType<ProjectedActionMessage>(message);
                Assert.Equal("action-2", action.Content);
            }
        );
    }

    [Fact]
    public void ProjectInvocationContext_WithEmptyHistory_ReturnsTwoEmptySegments() {
        var state = AgentState.CreateDefault();

        var projection = state.ProjectInvocationContext(new ContextProjectionOptions());

        Assert.Empty(projection.StablePrefix);
        Assert.Empty(projection.ActiveTurnTail);
        Assert.Empty(projection.ToFlat());
    }

    [Fact]
    public void ProjectInvocationContext_WithNullTargetInvocation_IsAFirstClassFlatProjectionScenario() {
        var state = AgentState.CreateDefault();

        state.AppendObservation(new ObservationEntry());
        state.AppendAction(CreateActionWithThinking("action-1", Descriptor));
        state.AppendObservation(new ObservationEntry());

        var projection = state.ProjectInvocationContext(new ContextProjectionOptions(TargetInvocation: null));

        Assert.Collection(
            projection.ToFlat(),
            message => Assert.IsType<ObservationMessage>(message),
            message => Assert.Empty(Assert.IsType<ProjectedActionMessage>(message).Blocks.OfType<ActionBlock.Thinking>()),
            message => Assert.IsType<ObservationMessage>(message)
        );
    }

    [Fact]
    public void ProjectInvocationContext_StripsOldTurnThinkingFromStablePrefix() {
        var state = AgentState.CreateDefault();

        state.AppendObservation(new ObservationEntry());
        state.AppendAction(CreateActionWithThinking("old-turn", Descriptor));
        state.AppendObservation(new ObservationEntry());
        state.AppendAction(CreateActionWithThinking("current-turn", Descriptor));

        var projection = state.ProjectInvocationContext(new ContextProjectionOptions(TargetInvocation: Descriptor));

        var stableAction = Assert.IsType<ProjectedActionMessage>(projection.StablePrefix[1]);
        Assert.DoesNotContain(stableAction.Blocks, static block => block is ActionBlock.Thinking);

        var activeAction = Assert.IsType<ProjectedActionMessage>(projection.ActiveTurnTail[1]);
        Assert.Contains(activeAction.Blocks, static block => block is ActionBlock.Thinking);
    }

    [Fact]
    public void ProjectInvocationContext_StripsCurrentTurnThinkingWhenOriginDoesNotMatchTarget() {
        var state = AgentState.CreateDefault();

        state.AppendObservation(new ObservationEntry());
        state.AppendAction(CreateActionWithThinking("action-1", Descriptor));

        var projection = state.ProjectInvocationContext(new ContextProjectionOptions(TargetInvocation: OtherDescriptor));
        var projectedAction = Assert.IsType<ProjectedActionMessage>(projection.ActiveTurnTail[1]);

        Assert.DoesNotContain(projectedAction.Blocks, static block => block is ActionBlock.Thinking);
        Assert.Equal("action-1", projectedAction.Content);
    }

    [Fact]
    public void ProjectInvocationContext_StripsCurrentTurnThinkingWhenModeIsNone() {
        var state = AgentState.CreateDefault();

        state.AppendObservation(new ObservationEntry());
        state.AppendAction(CreateActionWithThinking("action-1", Descriptor));

        var projection = state.ProjectInvocationContext(
            new ContextProjectionOptions(TargetInvocation: Descriptor, ThinkingMode: ThinkingProjectionMode.None)
        );
        var projectedAction = Assert.IsType<ProjectedActionMessage>(projection.ActiveTurnTail[1]);

        Assert.DoesNotContain(projectedAction.Blocks, static block => block is ActionBlock.Thinking);
    }

    [Fact]
    public void ProjectInvocationContext_StripsCurrentTurnThinkingWhenExplicitBoundaryIsMissing() {
        var state = CreateStateWithRecappedCurrentTurn(CreateActionWithThinking("action-1", Descriptor));

        var projection = state.ProjectInvocationContext(new ContextProjectionOptions(TargetInvocation: Descriptor));
        var projectedAction = Assert.IsType<ProjectedActionMessage>(Assert.Single(projection.ActiveTurnTail));

        Assert.DoesNotContain(projectedAction.Blocks, static block => block is ActionBlock.Thinking);
        Assert.Equal("action-1", projectedAction.Content);
    }

    [Fact]
    public void ProjectInvocationContext_RetainsCurrentTurnThinkingWhenAllConditionsMatch() {
        var state = AgentState.CreateDefault();

        state.AppendObservation(new ObservationEntry());
        state.AppendAction(CreateActionWithThinking("action-1", Descriptor));

        var projection = state.ProjectInvocationContext(new ContextProjectionOptions(TargetInvocation: Descriptor));
        var projectedAction = Assert.IsType<ProjectedActionMessage>(projection.ActiveTurnTail[1]);
        var thinking = Assert.IsType<ActionBlock.Thinking>(Assert.Single(projectedAction.Blocks.OfType<ActionBlock.Thinking>()));

        using var doc = JsonDocument.Parse(thinking.OpaquePayload);
        Assert.Equal("thinking", doc.RootElement.GetProperty("type").GetString());
    }

    private static ActionEntry CreateActionWithThinking(string text, CompletionDescriptor invocation, CompletionDescriptor? thinkingOrigin = null) {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new {
            type = "thinking",
            thinking = $"debug:{text}",
            signature = "sig"
        });

        return new ActionEntry(
            Blocks: new ActionBlock[] {
                new ActionBlock.Text(text),
                new ActionBlock.Thinking(thinkingOrigin ?? invocation, payload, $"debug:{text}")
            },
            Invocation: invocation
        );
    }

    private static AgentState CreateStateWithRecappedCurrentTurn(ActionEntry actionEntry) {
        var state = AgentState.CreateDefault();
        ForceAppendEntry(state, new RecapEntry("Synthetic recap", InsteadSerial: 1));
        ForceAppendEntry(state, actionEntry);
        return state;
    }

    private static void ForceAppendEntry(AgentState state, HistoryEntry entry) {
        var tokenEstimate = TokenEstimateHelper.GetDefault().Estimate(entry);
        entry.AssignTokenEstimate(tokenEstimate);

        var historyField = typeof(AgentState).GetField("_recentHistory", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Cannot access AgentState._recentHistory for test setup.");
        var serialField = typeof(AgentState).GetField("_lastSerial", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Cannot access AgentState._lastSerial for test setup.");

        var recentHistory = (List<HistoryEntry>?)historyField.GetValue(state)
            ?? throw new InvalidOperationException("AgentState._recentHistory was null.");
        var lastSerial = (ulong)(serialField.GetValue(state) ?? 0UL);

        entry.AssignSerial(++lastSerial);
        recentHistory.Add(entry);
        serialField.SetValue(state, lastSerial);
    }
}
