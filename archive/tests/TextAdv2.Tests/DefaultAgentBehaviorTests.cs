using Atelia.TextAdv2.DefaultAgent;
using Atelia.TextAdv2.DevSupport;
using Atelia.TextAdv2.Gym;
using Atelia.TextAdv2.Runtime;
using Atelia.TextAdv2.WorldTruth;
using Xunit;

namespace Atelia.TextAdv2.Tests;

public class DefaultAgentBehaviorTests {
    [Fact]
    public async Task PassthroughDefaultAgentContextBuilder_ComposesGoalMemoryAndBudgetAsync() {
        var input = CreateTurnInput();
        var builder = new PassthroughDefaultAgentContextBuilder(
            goalSource: new FixedGoalSource(new DefaultAgentGoalState("reach-aerie", "Reach the aerie.")),
            memorySource: new FixedMemorySource(
                new DefaultAgentMemorySlice(
                    "Recent route-relevant memory.",
                    [
                        new DefaultAgentMemoryEntry("m1", "The ridge winch leads to the aerie.", "test"),
                    ]
                )
            ),
            budgetSource: new FixedBudgetSource(new DefaultAgentTurnBudget(2, 1, 4))
        );

        var context = await builder.BuildAsync(input);

        Assert.Equal("scout", context.TurnInput.SelfObservation.ActorId);
        Assert.NotNull(context.Goal);
        Assert.Equal("reach-aerie", context.Goal!.GoalId);
        Assert.Equal("Recent route-relevant memory.", context.Memory.Summary);
        Assert.Single(context.Memory.Entries);
        Assert.Equal(2, context.Budget.MaxAdditionalObservations);
        Assert.Equal(1, context.Budget.MaxPlannedActions);
        Assert.Equal(4, context.Budget.MaxThinkingSteps);
    }

    [Fact]
    public async Task FirstAvailableMoveDefaultAgentPolicy_PicksFirstAvailableMoveAsync() {
        var policy = new FirstAvailableMoveDefaultAgentPolicy();
        var context = CreateTurnContext();

        var decision = await policy.DecideAsync(context);

        var move = Assert.IsType<MoveAgentActionIntent>(decision.Action);
        Assert.Equal(TestWorldBuilder.PassageIds.SquareRidgeTrail, move.PassageId);
        Assert.Equal("first-available-move", decision.Reasoning);
    }

    [Fact]
    public async Task KeepDefaultAgentPolicy_AlwaysReturnsKeepAsync() {
        var policy = new KeepDefaultAgentPolicy();
        var context = CreateTurnContext();

        var decision = await policy.DecideAsync(context);

        Assert.IsType<KeepAgentActionIntent>(decision.Action);
        Assert.Equal("default-keep", decision.Reasoning);
    }

    [Fact]
    public async Task DefaultAgentTurnPolicyAdapter_BuildsContextBeforeDelegatingAsync() {
        var adapter = new DefaultAgentTurnPolicyAdapter(
            new RecordingDefaultAgentPolicy(),
            new PassthroughDefaultAgentContextBuilder(
                goalSource: new FixedGoalSource(new DefaultAgentGoalState("reach-aerie", "Reach the aerie.")),
                memorySource: new FixedMemorySource(
                    new DefaultAgentMemorySlice(
                        "Remember the cliff lift.",
                        [
                            new DefaultAgentMemoryEntry("m1", "The ridge winch leads upward.", "test"),
                        ]
                    )
                ),
                budgetSource: new FixedBudgetSource(new DefaultAgentTurnBudget(2, 1, 4))
            )
        );

        var decision = await adapter.DecideAsync(CreateTurnInput());

        Assert.IsType<KeepAgentActionIntent>(decision.Action);
        Assert.Equal("goal=reach-aerie;memory=1;budget=4", decision.Reasoning);
    }

    [Fact]
    public void ActorEmbodiedStateDraftShapes_PreserveWorldTruthBoundary() {
        ActorEmbodiedState idle = ActorEmbodiedState.Idle;
        var route = new RouteFollowingActorProcessState(
            destinationLocationId: "aerie",
            remainingPassageIds: [TestWorldBuilder.PassageIds.RidgeAerieWinch],
            remainingTravelTicksOnCurrentLeg: 2
        );
        var mining = new MiningActorProcessState(
            worksiteId: "iron-vein-1",
            progressTicksInCurrentCycle: 2,
            ticksPerYield: 3,
            yieldItemId: "iron-ore"
        );

        Assert.IsType<IdleActorEmbodiedState>(idle);
        Assert.Equal("route-following", route.ProcessKind);
        Assert.True(route.IsInterruptible);
        Assert.Equal("aerie", route.DestinationLocationId);
        Assert.Equal("mining", mining.ProcessKind);
        Assert.Equal(3, mining.TicksPerYield);
        Assert.Equal("iron-ore", mining.YieldItemId);
    }

    private static AgentTurnInput CreateTurnInput() {
        using var session = SampleWorldBootstrap.CreateTemporarySession();
        var observation = session.ObserveActorContext(TestWorldBuilder.ActorIds.Scout);
        return new AgentTurnInput(1, observation);
    }

    private static DefaultAgentTurnContext CreateTurnContext() {
        var input = CreateTurnInput();
        return new DefaultAgentTurnContext(
            input,
            goal: null,
            DefaultAgentMemorySlice.Empty,
            DefaultAgentTurnBudget.Default
        );
    }

    private sealed class FixedGoalSource(DefaultAgentGoalState goal) : IDefaultAgentGoalSource {
        public ValueTask<DefaultAgentGoalState?> GetGoalAsync(AgentTurnInput input, CancellationToken ct = default) {
            ArgumentNullException.ThrowIfNull(input);
            return ValueTask.FromResult<DefaultAgentGoalState?>(goal);
        }
    }

    private sealed class FixedMemorySource(DefaultAgentMemorySlice memory) : IDefaultAgentMemorySource {
        public ValueTask<DefaultAgentMemorySlice> GetMemoryAsync(AgentTurnInput input, CancellationToken ct = default) {
            ArgumentNullException.ThrowIfNull(input);
            return ValueTask.FromResult(memory);
        }
    }

    private sealed class FixedBudgetSource(DefaultAgentTurnBudget budget) : IDefaultAgentBudgetSource {
        public ValueTask<DefaultAgentTurnBudget> GetBudgetAsync(AgentTurnInput input, CancellationToken ct = default) {
            ArgumentNullException.ThrowIfNull(input);
            return ValueTask.FromResult(budget);
        }
    }

    private sealed class RecordingDefaultAgentPolicy : IDefaultAgentTurnPolicy {
        public ValueTask<AgentTurnDecision> DecideAsync(DefaultAgentTurnContext context, CancellationToken ct = default) {
            ArgumentNullException.ThrowIfNull(context);

            string reasoning = $"goal={context.Goal?.GoalId};memory={context.Memory.Entries.Length};budget={context.Budget.MaxThinkingSteps}";
            return ValueTask.FromResult(AgentTurnDecision.Keep(reasoning));
        }
    }
}
