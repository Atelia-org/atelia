using Atelia.Agent.Core.History;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Agent.Core.Tests;

public sealed class AgentStateInjectionProjectionTests {
    [Fact]
    public void ProjectInvocationContext_RebindsInjectedThinkingToCurrentInvocation() {
        var state = AgentState.CreateDefault("test");
        var previousInvocation = new CompletionDescriptor("provider", "api", "model-a");
        var currentInvocation = new CompletionDescriptor("provider", "api", "model-b");

        state.AppendObservation(new ObservationEntry());
        state.AppendAction(
            new ActionEntry(
                new ActionMessage([
                    new ActionBlock.TextReasoningBlock("old thought", previousInvocation)
                ]),
                previousInvocation
            )
        );
        state.AppendObservation(new ObservationEntry());
        state.InjectActionContent(
            new ActionInjectionRequest(
                "new thought",
                new InjectionSource(InjectionSourceKind.Wizard),
                InjectedActionContentMode.Thinking
            )
        );

        var projection = state.ProjectInvocationContext(
            new ContextProjectionOptions(TargetInvocation: currentInvocation)
        );

        Assert.Equal(2, projection.ActiveTurnTail.Count);
        Assert.IsType<ObservationMessage>(projection.ActiveTurnTail[0]);
        var action = Assert.IsType<ActionMessage>(projection.ActiveTurnTail[1]);
        var reasoning = Assert.IsType<ActionBlock.TextReasoningBlock>(Assert.Single(action.Blocks));
        Assert.Equal("new thought", reasoning.Content);
        Assert.Equal(currentInvocation, reasoning.Origin);
    }

    [Fact]
    public void ProjectInvocationContext_PreservesInjectedTextPrefixVerbatim() {
        var state = AgentState.CreateDefault("test");
        var invocation = new CompletionDescriptor("provider", "api", "model-a");
        const string injectedPrefix = "\n  partial-json:{";

        state.AppendObservation(new ObservationEntry());
        state.AppendAction(
            new ActionEntry(
                new ActionMessage([
                    new ActionBlock.Text("assistant")
                ]),
                invocation
            )
        );
        state.InjectActionContent(
            new ActionInjectionRequest(
                injectedPrefix,
                new InjectionSource(InjectionSourceKind.Wizard),
                InjectedActionContentMode.Text
            )
        );

        var projection = state.ProjectInvocationContext(
            new ContextProjectionOptions(TargetInvocation: invocation)
        );

        Assert.Equal(2, projection.ActiveTurnTail.Count);
        Assert.IsType<ObservationMessage>(projection.ActiveTurnTail[0]);
        var action = Assert.IsType<ActionMessage>(projection.ActiveTurnTail[1]);
        Assert.Equal("assistant", Assert.IsType<ActionBlock.Text>(action.Blocks[0]).Content);
        Assert.Equal(injectedPrefix, Assert.IsType<ActionBlock.Text>(action.Blocks[1]).Content);
    }
}
