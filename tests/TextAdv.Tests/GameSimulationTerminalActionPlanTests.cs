using Atelia.StateJournal;
using Xunit;

namespace Atelia.TextAdv.Tests;

public sealed class GameSimulationTerminalActionPlanTests : IDisposable {
    private readonly string _repoDir = Path.Combine(
        Path.GetTempPath(),
        "textadv-plan-tests",
        Guid.NewGuid().ToString("N")
    );

    public void Dispose() {
        if (Directory.Exists(_repoDir)) {
            Directory.Delete(_repoDir, recursive: true);
        }
    }

    [Fact]
    public void BuildTerminalInteractionPlan_WhenInteractionIsImmediate_ShouldReturnImmediatePlan() {
        using var repo = CreateRepository();
        var root = GameSimulation.CreateNewWorld(repo);
        var gmWorldEdit = new GmWorldEditService(root);

        Assert.True(gmWorldEdit.CreateItem("waterskin", "水袋", "一个还空着的旧水袋。", "beach").IsSuccess);
        Assert.True(gmWorldEdit.MoveItemToActor("waterskin", "player").IsSuccess);
        Assert.True(
            gmWorldEdit.AddInteraction(
                interactionId: "steady-breath",
                targetRef: "item:waterskin",
                actionKind: "prepare",
                visibleLabel: "把水袋口理顺",
                preconditionNote: "none",
                effectNote: "你深吸一口气，让心绪稍稍平复下来。",
                turnCost: 0,
                effectScope: GameSimulation.SelfEffectScope,
                effectSlots: GameSimulation.ImmediateEffectSlot
            ).IsSuccess
        );

        var planResult = GameSimulation.BuildTerminalInteractionPlan(
            GameSimulation.DescribeCurrentPerception(root),
            "steady-breath",
            "先稳住呼吸。"
        );

        var plan = AssertSuccess(planResult);
        Assert.Equal(TerminalActionMode.Immediate, plan.Mode);
        Assert.Equal(TerminalActionKinds.SmallInteract, plan.ActionKind);
        var interactionPlan = Assert.IsType<TerminalActionExecutionPlan.Interaction>(plan);
        Assert.Equal(InteractionExecutionKind.ImmediateSelf, interactionPlan.ExecutionKind);
    }

    [Fact]
    public void BuildExploreTerminalPlan_WhenReasonIsBlank_ShouldFailFast() {
        var planResult = GameSimulation.BuildExploreTerminalPlan(" north ", "观察海岸线", "   ");

        Assert.True(planResult.IsFailure);
        Assert.Equal("TextAdv.InvalidTerminalPlanInput", planResult.Error!.ErrorCode);
    }

    [Fact]
    public void BuildRestAWhileTerminalPlan_WhenReasonIsBlank_ShouldFailFast() {
        var planResult = GameSimulation.BuildRestAWhileTerminalPlan("   ");

        Assert.True(planResult.IsFailure);
        Assert.Equal("TextAdv.InvalidTerminalPlanInput", planResult.Error!.ErrorCode);
    }

    [Fact]
    public void BuildTerminalInteractionPlan_WhenInteractionStartsWorking_ShouldReturnLargePlan() {
        using var repo = CreateRepository();
        var root = GameSimulation.CreateNewWorld(repo);
        var gmWorldEdit = new GmWorldEditService(root);

        Assert.True(gmWorldEdit.CreateItem("wall", "薄石壁", "石壁上有一处可继续凿开的薄弱点。", "beach").IsSuccess);
        Assert.True(
            gmWorldEdit.AddInteraction(
                interactionId: "chip-wall",
                targetRef: "item:wall",
                actionKind: "work",
                visibleLabel: "继续凿石壁",
                preconditionNote: "none",
                effectNote: "石屑簌簌落下，你把这项活又往前推进了一截。",
                turnCost: 3,
                effectScope: GameSimulation.RoomEffectScope,
                effectSlots: $"{GameSimulation.PerTurnEndEffectSlot},{GameSimulation.OnCompletionEffectSlot}"
            ).IsSuccess
        );

        var planResult = GameSimulation.BuildTerminalInteractionPlan(
            GameSimulation.DescribeCurrentPerception(root),
            "chip-wall",
            "先持续处理这面薄石壁。"
        );

        var plan = AssertSuccess(planResult);
        Assert.Equal(TerminalActionMode.Large, plan.Mode);
        Assert.Equal(TerminalActionKinds.LargeInteract, plan.ActionKind);
        var interactionPlan = Assert.IsType<TerminalActionExecutionPlan.Interaction>(plan);
        Assert.Equal(InteractionExecutionKind.WorkingStart, interactionPlan.ExecutionKind);
    }

    [Fact]
    public void BuildTerminalInteractionPlan_WhenInteractionIsDeferredTurnEnd_ShouldReturnImmediatePlan() {
        using var repo = CreateRepository();
        var root = GameSimulation.CreateNewWorld(repo);
        var gmWorldEdit = new GmWorldEditService(root);

        Assert.True(gmWorldEdit.CreateItem("shell", "贝壳", "一枚带着海潮光泽的贝壳。", "beach").IsSuccess);
        Assert.True(
            gmWorldEdit.AddInteraction(
                interactionId: "nudge-shell",
                targetRef: "item:shell",
                actionKind: "inspect",
                visibleLabel: "拨弄贝壳",
                preconditionNote: "none",
                effectNote: "你先把贝壳拨到一边，打算在回合结尾再整理这点发现。",
                turnCost: 0,
                effectScope: GameSimulation.RoomEffectScope,
                effectSlots: GameSimulation.TurnEndEffectSlot
            ).IsSuccess
        );

        var planResult = GameSimulation.BuildTerminalInteractionPlan(
            GameSimulation.DescribeCurrentPerception(root),
            "nudge-shell",
            "先把贝壳拨到一边。"
        );

        var plan = AssertSuccess(planResult);
        Assert.Equal(TerminalActionMode.Immediate, plan.Mode);
        Assert.Equal(TerminalActionKinds.SmallInteract, plan.ActionKind);
        var interactionPlan = Assert.IsType<TerminalActionExecutionPlan.Interaction>(plan);
        Assert.Equal(InteractionExecutionKind.DeferredTurnEnd, interactionPlan.ExecutionKind);
    }

    [Fact]
    public void BuildTerminalInteractionPlan_WhenInteractionEndsTurn_ShouldReturnLargePlan() {
        using var repo = CreateRepository();
        var root = GameSimulation.CreateNewWorld(repo);
        var gmWorldEdit = new GmWorldEditService(root);

        Assert.True(gmWorldEdit.CreateItem("driftwood", "浮木", "一截潮湿而沉重的浮木。", "beach").IsSuccess);
        Assert.True(
            gmWorldEdit.AddInteraction(
                interactionId: "haul-driftwood",
                targetRef: "item:driftwood",
                actionKind: "move",
                visibleLabel: "把浮木拖回营地边",
                preconditionNote: "none",
                effectNote: "你费了一整回合，终于把那截浮木拖到了更顺手的位置。",
                turnCost: 1,
                effectScope: GameSimulation.RoomEffectScope,
                effectSlots: GameSimulation.TurnEndEffectSlot
            ).IsSuccess
        );

        var planResult = GameSimulation.BuildTerminalInteractionPlan(
            GameSimulation.DescribeCurrentPerception(root),
            "haul-driftwood",
            "先把这截浮木处理好。"
        );

        var plan = AssertSuccess(planResult);
        Assert.Equal(TerminalActionMode.Large, plan.Mode);
        Assert.Equal(TerminalActionKinds.LargeInteract, plan.ActionKind);
        var interactionPlan = Assert.IsType<TerminalActionExecutionPlan.Interaction>(plan);
        Assert.Equal(InteractionExecutionKind.TurnEnding, interactionPlan.ExecutionKind);
    }

    [Fact]
    public void BuildTerminalInteractionPlan_WhenInteractionIsUnsupportedZeroTurn_ShouldFailFast() {
        using var repo = CreateRepository();
        var root = GameSimulation.CreateNewWorld(repo);
        var gmWorldEdit = new GmWorldEditService(root);

        Assert.True(gmWorldEdit.CreateItem("shell", "贝壳", "一枚带着海潮光泽的贝壳。", "beach").IsSuccess);
        Assert.True(
            gmWorldEdit.AddInteraction(
                interactionId: "narrate-shell",
                targetRef: "item:shell",
                actionKind: "inspect",
                visibleLabel: "端详贝壳",
                preconditionNote: "none",
                effectNote: "你把那枚贝壳翻来覆去看了看。",
                turnCost: 0,
                effectScope: GameSimulation.SelfEffectScope,
                effectSlots: $"{GameSimulation.ImmediateEffectSlot},{GameSimulation.TurnEndEffectSlot}"
            ).IsSuccess
        );

        var planResult = GameSimulation.BuildTerminalInteractionPlan(
            GameSimulation.DescribeCurrentPerception(root),
            "narrate-shell",
            "先看看这枚贝壳。"
        );

        Assert.True(planResult.IsFailure);
        Assert.Equal("TextAdv.UnsupportedInteractionExecutionPlan", planResult.Error!.ErrorCode);
    }

    private Repository CreateRepository() {
        var createResult = Repository.Create(_repoDir);
        return AssertSuccess(createResult);
    }

    private static T AssertSuccess<T>(AteliaResult<T> result) where T : notnull {
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        return result.Value!;
    }
}
