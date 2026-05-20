using Atelia.StateJournal;
using Xunit;

namespace Atelia.TextAdv.Tests;

public sealed class GameSimulationDeferredAndWorkingTests : IDisposable {
    private readonly string _repoDir = Path.Combine(
        Path.GetTempPath(),
        "textadv-tests",
        Guid.NewGuid().ToString("N")
    );

    public GameSimulationDeferredAndWorkingTests() {
        GameMasterResolver.SetStubForTests(GameMasterTestStubs.CreateDeterministicLikeStub());
    }

    public void Dispose() {
        GameMasterResolver.ResetForTests();
        if (Directory.Exists(_repoDir)) {
            Directory.Delete(_repoDir, recursive: true);
        }
    }

    [Fact]
    public void ApplyDeferredTurnEndInteraction_ThenRest_ShouldResolvePendingEffectAtTurnEnd() {
        using var repo = CreateRepository();
        var root = GameSimulation.CreateNewWorld(repo);
        var gmWorldEdit = new GmWorldEditService(root);

        Assert.True(gmWorldEdit.CreateItem("berries", "野果", "一小丛刚熟的野果。", "beach").IsSuccess);
        Assert.True(
            gmWorldEdit.AddInteraction(
                interactionId: "pick-berries",
                targetRef: "item:berries",
                actionKind: "take",
                visibleLabel: "摘下一颗野果",
                preconditionNote: "none",
                effectNote: "到本回合结束时，你顺手摘下了一颗野果。",
                turnCost: 0,
                effectScope: GameSimulation.RoomEffectScope,
                effectSlots: GameSimulation.TurnEndEffectSlot
            ).IsSuccess
        );

        var smallResult = GameSimulation.ApplyDeferredTurnEndInteraction(
            root,
            "pick-berries",
            "眼前就有一丛成熟野果，先顺手摘下一颗。",
            "通过"
        );
        Assert.True(smallResult.IsSuccess, smallResult.Error?.Message);

        var pendingStep = Assert.Single(GameSimulation.DescribeCurrentPerception(root).AcceptedSteps);
        Assert.Equal(GameSimulation.StepOutcomePendingTurnEnd, pendingStep.StepOutcomeState);

        var resolution = GameSimulation.ApplyRestAWhile(root, "先缓一缓，看看回合末会发生什么。", "通过");
        Assert.Contains("此前的顺手动作：到本回合结束时，你顺手摘下了一颗野果。", resolution.Summary);
        Assert.Contains("你原地休息了一会", resolution.Summary);
        Assert.Equal(1, resolution.NextPerception.Day);
        Assert.Equal(2, resolution.NextPerception.Slot);
    }

    [Fact]
    public async Task ApplyWorkingInteractionAsync_ShouldAutoAdvanceUntilCompletion_ForSingleActor() {
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

        var resolutionResult = await GameSimulation.ApplyWorkingInteractionAsync(
            root,
            "chip-wall",
            "眼下没有别的更急的事，先把这面薄石壁持续凿开。",
            "通过",
            CancellationToken.None
        );
        Assert.True(resolutionResult.IsSuccess, resolutionResult.Error?.Message);

        var resolution = resolutionResult.Value!;
        Assert.Contains("继续凿石壁", resolution.Summary);
        Assert.Equal(1, resolution.NextPerception.Day);
        Assert.Equal(4, resolution.NextPerception.Slot);
        Assert.Empty(resolution.NextPerception.AcceptedSteps);
        Assert.False(string.IsNullOrWhiteSpace(resolution.NextPerception.LastResolution));
    }

    [Fact]
    public async Task ApplyInteractionAsync_WhenInteractionIsWorking_ShouldRejectInsteadOfTreatingItAsTurnEnding() {
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

        var resolutionResult = await GameSimulation.ApplyInteractionAsync(
            root,
            "chip-wall",
            "这里需要持续投入多个回合，不该走单回合 interaction 入口。",
            "通过",
            CancellationToken.None
        );

        Assert.False(resolutionResult.IsSuccess);
        Assert.Equal("TextAdv.UnsupportedTurnEndingInteraction", resolutionResult.Error?.ErrorCode);
        Assert.Empty(GameSimulation.DescribeCurrentPerception(root).AcceptedSteps);
        Assert.Equal(1, GameSimulation.DescribeCurrentPerception(root).Slot);
    }

    [Fact]
    public async Task WorkingActor_ShouldStayInactiveWhileOtherActorTurnsAdvanceUntilCompletion() {
        using var repo = CreateRepository();
        var root = GameSimulation.CreateNewWorld(repo);
        var gmWorldEdit = new GmWorldEditService(root);

        Assert.True(
            GameSimulation.CreateLlmPlayerActor(
                root,
                "ally",
                "同伴",
                "另一个活跃中的同行者。",
                "beach"
            ).IsSuccess
        );
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

        var startResult = await GameSimulation.ApplyWorkingInteractionAsync(
            root,
            "chip-wall",
            "先持续处理这面薄石壁，让同伴去做别的事。",
            "通过",
            CancellationToken.None
        );
        Assert.True(startResult.IsSuccess, startResult.Error?.Message);
        Assert.Equal(1, startResult.Value!.NextPerception.Day);
        Assert.Equal(2, startResult.Value.NextPerception.Slot);

        var firstStatus = GameSimulation.DescribeCurrentTurnStatus(root);
        Assert.Single(firstStatus.Actors);
        Assert.Equal("ally", firstStatus.TurnOwnerActorId);

        var firstSubmit = GameSimulation.SubmitDevLargeActionForActor(
            root,
            "ally",
            new ActionDescriptor(
                TerminalActionKinds.LargeRestAWhile,
                "同伴原地休息一会",
                null,
                "让同伴先稳住节奏。"
            )
        );
        Assert.True(firstSubmit.IsSuccess, firstSubmit.Error?.Message);

        var firstResolution = await GameSimulation.ApplyReadyCollectedTurnAsync(root, CancellationToken.None);
        Assert.True(firstResolution.IsSuccess, firstResolution.Error?.Message);
        Assert.Equal(1, firstResolution.Value!.NextPerception.Day);
        Assert.Equal(3, firstResolution.Value.NextPerception.Slot);
        Assert.Contains("同伴原地歇了一会", firstResolution.Value.Summary);
        Assert.Contains("原地休息了一会", GameSimulation.DescribePerceptionForActor(root, "ally").LastResolution);

        var secondStatus = GameSimulation.DescribeCurrentTurnStatus(root);
        Assert.Single(secondStatus.Actors);
        Assert.Equal("ally", secondStatus.TurnOwnerActorId);

        var secondSubmit = GameSimulation.SubmitDevLargeActionForActor(
            root,
            "ally",
            new ActionDescriptor(
                TerminalActionKinds.LargeRestAWhile,
                "同伴再次原地休息一会",
                null,
                "继续等待石壁那边完工。"
            )
        );
        Assert.True(secondSubmit.IsSuccess, secondSubmit.Error?.Message);

        var secondResolution = await GameSimulation.ApplyReadyCollectedTurnAsync(root, CancellationToken.None);
        Assert.True(secondResolution.IsSuccess, secondResolution.Error?.Message);
        Assert.Equal(1, secondResolution.Value!.NextPerception.Day);
        Assert.Equal(4, secondResolution.Value.NextPerception.Slot);

        var finalStatus = GameSimulation.DescribeCurrentTurnStatus(root);
        Assert.Equal(2, finalStatus.Actors.Count);
        Assert.Contains(finalStatus.Actors, static actor => actor.ActorId == "player");
        Assert.Contains(finalStatus.Actors, static actor => actor.ActorId == "ally");
    }

    [Fact]
    public async Task CollectedTurn_ShouldResolveSoleNonTerminalExplore_WhenPlayerIsWorking() {
        using var repo = CreateRepository();
        var root = GameSimulation.CreateNewWorld(repo);
        var gmWorldEdit = new GmWorldEditService(root);

        Assert.True(GameSimulation.CreateLlmPlayerActor(root, "ally", "同伴", "另一个活跃中的同行者。", "beach").IsSuccess);
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

        var startResult = await GameSimulation.ApplyWorkingInteractionAsync(
            root,
            "chip-wall",
            "先持续处理这面薄石壁，让同伴去探路。",
            "通过",
            CancellationToken.None
        );
        Assert.True(startResult.IsSuccess, startResult.Error?.Message);

        var submit = GameSimulation.SubmitDevLargeActionForActor(
            root,
            "ally",
            new ActionDescriptor(
                TerminalActionKinds.LargeExplore,
                "向 north 探索",
                "direction=north",
                "先去北边的密林看看。"
            )
        );
        Assert.True(submit.IsSuccess, submit.Error?.Message);

        var resolution = await GameSimulation.ApplyReadyCollectedTurnAsync(root, CancellationToken.None);
        Assert.True(resolution.IsSuccess, resolution.Error?.Message);
        Assert.Equal("forest", GameSimulation.DescribePerceptionForActor(root, "ally").Location.LocationId);
        Assert.Contains("离开了「沙滩」", resolution.Value!.Summary);
        Assert.Contains("来到「密林」", GameSimulation.DescribePerceptionForActor(root, "ally").LastResolution);
    }

    [Fact]
    public async Task CollectedTurn_ShouldResolveSoleNonTerminalInteraction_WhenPlayerIsWorking() {
        using var repo = CreateRepository();
        var root = GameSimulation.CreateNewWorld(repo);
        var gmWorldEdit = new GmWorldEditService(root);

        Assert.True(GameSimulation.CreateLlmPlayerActor(root, "ally", "同伴", "另一个活跃中的同行者。", "beach").IsSuccess);
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
        Assert.True(gmWorldEdit.CreateItem("shell", "贝壳", "一枚带着海潮光泽的贝壳。", "beach").IsSuccess);
        Assert.True(
            gmWorldEdit.AddInteraction(
                interactionId: "inspect-shell",
                targetRef: "item:shell",
                actionKind: "inspect",
                visibleLabel: "端详贝壳",
                preconditionNote: "none",
                effectNote: "你把那枚贝壳翻来覆去看了看，确认它只是普通海边拾到的东西。",
                turnCost: 1,
                effectScope: GameSimulation.RoomEffectScope,
                effectSlots: GameSimulation.TurnEndEffectSlot
            ).IsSuccess
        );

        var startResult = await GameSimulation.ApplyWorkingInteractionAsync(
            root,
            "chip-wall",
            "我继续凿石壁，让同伴顺手检查一下地上的东西。",
            "通过",
            CancellationToken.None
        );
        Assert.True(startResult.IsSuccess, startResult.Error?.Message);

        var payload = GameSimulation.BuildInteractionPayload(
            GameSimulation.TryGetVisibleInteraction(
                GameSimulation.DescribePerceptionForActor(root, "ally"),
                "inspect-shell"
            ).Value!
        );
        var submit = GameSimulation.SubmitDevLargeActionForActor(
            root,
            "ally",
            new ActionDescriptor(
                TerminalActionKinds.LargeInteract,
                "端详贝壳 (inspect)",
                payload,
                "先看看这枚贝壳值不值得带走。"
            )
        );
        Assert.True(submit.IsSuccess, submit.Error?.Message);

        var resolution = await GameSimulation.ApplyReadyCollectedTurnAsync(root, CancellationToken.None);
        Assert.True(resolution.IsSuccess, resolution.Error?.Message);
        Assert.Contains("端详贝壳", resolution.Value!.Summary);
        Assert.Contains("普通海边拾到的东西", GameSimulation.DescribePerceptionForActor(root, "ally").LastResolution);
    }

    [Fact]
    public async Task ActivePlayer_ShouldObserveCoLocatedOtherActorWorkingProgress() {
        using var repo = CreateRepository();
        var root = GameSimulation.CreateNewWorld(repo);
        var gmWorldEdit = new GmWorldEditService(root);

        Assert.True(GameSimulation.CreateLlmPlayerActor(root, "ally", "同伴", "另一个活跃中的同行者。", "beach").IsSuccess);
        Assert.True(gmWorldEdit.CreateItem("wall", "薄石壁", "石壁上有一处可继续凿开的薄弱点。", "beach").IsSuccess);
        Assert.True(
            gmWorldEdit.AddInteraction(
                interactionId: "chip-wall",
                targetRef: "item:wall",
                actionKind: "work",
                visibleLabel: "继续凿石壁",
                preconditionNote: "none",
                effectNote: "石屑簌簌落下，你把这项活又往前推进了一截。",
                turnCost: 2,
                effectScope: GameSimulation.RoomEffectScope,
                effectSlots: $"{GameSimulation.PerTurnEndEffectSlot},{GameSimulation.OnCompletionEffectSlot}"
            ).IsSuccess
        );
        Assert.True(gmWorldEdit.CreateItem("rubble", "碎石堆", "一小堆需要慢慢整理的碎石。", "beach").IsSuccess);
        Assert.True(
            gmWorldEdit.AddInteraction(
                interactionId: "sort-rubble",
                targetRef: "item:rubble",
                actionKind: "work",
                visibleLabel: "整理碎石",
                preconditionNote: "none",
                effectNote: "碎石被一点点归拢起来，手上的进度又往前挪了一截。",
                turnCost: 3,
                effectScope: GameSimulation.RoomEffectScope,
                effectSlots: $"{GameSimulation.PerTurnEndEffectSlot},{GameSimulation.OnCompletionEffectSlot}"
            ).IsSuccess
        );

        var playerStart = await GameSimulation.ApplyWorkingInteractionAsync(
            root,
            "chip-wall",
            "我先把石壁这边收个尾，让同伴接着整理碎石。",
            "通过",
            CancellationToken.None
        );
        Assert.True(playerStart.IsSuccess, playerStart.Error?.Message);

        var allyPayload = GameSimulation.BuildInteractionPayload(
            GameSimulation.TryGetVisibleInteraction(
                GameSimulation.DescribePerceptionForActor(root, "ally"),
                "sort-rubble"
            ).Value!
        );
        var allySubmit = GameSimulation.SubmitDevLargeActionForActor(
            root,
            "ally",
            new ActionDescriptor(
                TerminalActionKinds.LargeInteract,
                "整理碎石 (work)",
                allyPayload,
                "我收尾石壁时，让同伴继续在旁边整理碎石。"
            )
        );
        Assert.True(allySubmit.IsSuccess, allySubmit.Error?.Message);

        var allyStartResolution = await GameSimulation.ApplyReadyCollectedTurnAsync(root, CancellationToken.None);
        Assert.True(allyStartResolution.IsSuccess, allyStartResolution.Error?.Message);
        Assert.Contains("整理碎石", allyStartResolution.Value!.Summary);

        var turnStatus = GameSimulation.DescribeCurrentTurnStatus(root);
        Assert.Single(turnStatus.Actors);
        Assert.Equal("player", turnStatus.TurnOwnerActorId);

        var restResolution = GameSimulation.ApplyRestAWhile(root, "先歇口气，同时留意同伴那边整理碎石的进展。", "通过");
        Assert.Contains("整理碎石", restResolution.Summary);
        Assert.Contains("又往前推进了一点", restResolution.Summary);
    }

    private Repository CreateRepository() {
        var createResult = Repository.Create(_repoDir);
        return AssertSuccess(createResult);
    }

    private static T AssertSuccess<T>(AteliaResult<T> result)
        where T : notnull {
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        return result.Value!;
    }
}
