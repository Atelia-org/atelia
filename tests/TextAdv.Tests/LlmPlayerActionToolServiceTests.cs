using System.Reflection;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.StateJournal;
using Xunit;

namespace Atelia.TextAdv.Tests;

public sealed class LlmPlayerActionToolServiceTests : IDisposable {
    private readonly string _repoDir = Path.Combine(
        Path.GetTempPath(),
        "textadv-llm-tool-tests",
        Guid.NewGuid().ToString("N")
    );

    public LlmPlayerActionToolServiceTests() {
        GameMasterResolver.SetStubForTests(GameMasterTestStubs.CreateDeterministicLikeStub());
    }

    public void Dispose() {
        GameMasterResolver.ResetForTests();
        if (Directory.Exists(_repoDir)) {
            Directory.Delete(_repoDir, recursive: true);
        }
    }

    [Fact]
    public async Task RestAWhileAsync_ShouldStoreSharedRestPlan() {
        using var repo = CreateRepository();
        var root = GameSimulation.CreateNewWorld(repo);
        var service = CreateToolService(root, GameSimulation.TerminalPlayerActorId);

        var result = await InvokeToolAsync(service, "RestAWhileAsync", "先观察再说。");
        var plan = GetProposal(service);

        Assert.Equal(ToolExecutionStatus.Success, result.Status);
        _ = Assert.IsType<TerminalActionExecutionPlan.RestAWhile>(plan);
        Assert.Equal(TerminalActionKinds.LargeRestAWhile, plan!.ActionKind);
        Assert.Equal("原地休息一会", plan.ActionSummary);
    }

    [Fact]
    public async Task InteractAsync_WhenInteractionIsImmediate_ShouldExecuteSmallActionForInternalPlayer() {
        using var repo = CreateRepository();
        var root = GameSimulation.CreateNewWorld(repo);
        var gmWorldEdit = new GmWorldEditService(root);

        Assert.True(
            GameSimulation.CreateLlmPlayerActor(
                root,
                "ally",
                "同伴",
                "另一个由 internal LLM 驱动的玩家。",
                "beach"
            ).IsSuccess
        );
        Assert.True(gmWorldEdit.CreateItem("waterskin", "水袋", "一个还空着的旧水袋。", "beach").IsSuccess);
        Assert.True(gmWorldEdit.MoveItemToActor("waterskin", "ally").IsSuccess);
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

        var service = CreateToolService(root, "ally", AcceptAllValidationAsync);
        var result = await InvokeToolAsync(service, "InteractAsync", "先稳住呼吸。", "steady-breath");
        var perception = GetCurrentPerception(service);

        Assert.Equal(ToolExecutionStatus.Success, result.Status);
        Assert.Contains("Small-Action 已执行", result.Content);
        Assert.Contains("当前 Perception-Bundle", result.Content);
        Assert.Null(GetProposal(service));
        Assert.Equal("ally", perception.ActorId);
        var step = Assert.Single(perception.AcceptedSteps);
        Assert.Equal(TerminalActionKinds.SmallInteract, step.ActionKind);
        Assert.False(step.EndsTurn);
        Assert.Equal(GameSimulation.StepOutcomeCommittedNow, step.StepOutcomeState);
    }

    [Fact]
    public async Task InteractAsync_WhenInteractionIsDeferredTurnEnd_ShouldExecuteSmallActionForInternalPlayer() {
        using var repo = CreateRepository();
        var root = GameSimulation.CreateNewWorld(repo);
        var gmWorldEdit = new GmWorldEditService(root);

        Assert.True(
            GameSimulation.CreateLlmPlayerActor(
                root,
                "ally",
                "同伴",
                "另一个由 internal LLM 驱动的玩家。",
                "beach"
            ).IsSuccess
        );
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

        var service = CreateToolService(root, "ally", AcceptAllValidationAsync);
        var result = await InvokeToolAsync(service, "InteractAsync", "先把贝壳拨开，留到回合末再统一消化。", "nudge-shell");
        var perception = GetCurrentPerception(service);

        Assert.Equal(ToolExecutionStatus.Success, result.Status);
        Assert.Null(GetProposal(service));
        Assert.Equal("ally", perception.ActorId);
        var step = Assert.Single(perception.AcceptedSteps);
        Assert.Equal(TerminalActionKinds.SmallInteract, step.ActionKind);
        Assert.False(step.EndsTurn);
        Assert.Equal(GameSimulation.StepOutcomePendingTurnEnd, step.StepOutcomeState);
    }

    [Fact]
    public async Task InteractAsync_WhenInteractionEndsTurn_ShouldStoreLargeProposalForInternalPlayer() {
        using var repo = CreateRepository();
        var root = GameSimulation.CreateNewWorld(repo);
        var gmWorldEdit = new GmWorldEditService(root);

        Assert.True(
            GameSimulation.CreateLlmPlayerActor(
                root,
                "ally",
                "同伴",
                "另一个由 internal LLM 驱动的玩家。",
                "beach"
            ).IsSuccess
        );
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

        var service = CreateToolService(root, "ally", AcceptAllValidationAsync);
        var result = await InvokeToolAsync(service, "InteractAsync", "先把这截浮木处理好。", "haul-driftwood");
        var plan = GetProposal(service);
        var perception = GetCurrentPerception(service);

        Assert.Equal(ToolExecutionStatus.Success, result.Status);
        var interactionPlan = Assert.IsType<TerminalActionExecutionPlan.Interaction>(plan);
        Assert.Equal(InteractionExecutionKind.TurnEnding, interactionPlan.ExecutionKind);
        Assert.Empty(perception.AcceptedSteps);
        Assert.Contains("已暂存 Large-Action", result.Content);
    }

    [Fact]
    public async Task InteractAsync_WhenInteractionIsSmall_ShouldValidateUsingPlanDescriptor() {
        using var repo = CreateRepository();
        var root = GameSimulation.CreateNewWorld(repo);
        var gmWorldEdit = new GmWorldEditService(root);

        Assert.True(
            GameSimulation.CreateLlmPlayerActor(
                root,
                "ally",
                "同伴",
                "另一个由 internal LLM 驱动的玩家。",
                "beach"
            ).IsSuccess
        );
        Assert.True(gmWorldEdit.CreateItem("waterskin", "水袋", "一个还空着的旧水袋。", "beach").IsSuccess);
        Assert.True(gmWorldEdit.MoveItemToActor("waterskin", "ally").IsSuccess);
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

        var expectedPlan = AssertSuccess(
            GameSimulation.BuildTerminalInteractionPlan(
                GameSimulation.DescribePerceptionForActor(root, "ally"),
                "steady-breath",
                "先稳住呼吸。"
            )
        );

        string? validatedActionKind = null;
        string? validatedActionSummary = null;
        string? validatedActionPayload = null;
        string? validatedPreActionReason = null;
        var service = CreateToolService(
            root,
            "ally",
            (perception, actionKind, actionSummary, preActionReason, actionPayload, cancellationToken) => {
                cancellationToken.ThrowIfCancellationRequested();
                validatedActionKind = actionKind;
                validatedActionSummary = actionSummary;
                validatedActionPayload = actionPayload;
                validatedPreActionReason = preActionReason;
                return Task.FromResult(new GameActionValidator.ValidationResult(true, "通过：测试接受。"));
            }
        );

        var result = await InvokeToolAsync(service, "InteractAsync", "先稳住呼吸。", "steady-breath");

        Assert.Equal(ToolExecutionStatus.Success, result.Status);
        Assert.Equal(expectedPlan.Descriptor.ActionKind, validatedActionKind);
        Assert.Equal(expectedPlan.Descriptor.ActionSummary, validatedActionSummary);
        Assert.Equal(expectedPlan.Descriptor.ActionPayload, validatedActionPayload);
        Assert.Equal(expectedPlan.Descriptor.PreActionReason, validatedPreActionReason);
    }

    [Fact]
    public async Task EditMemoryNotebookAsync_ShouldUseInjectedValidatorDelegate() {
        using var repo = CreateRepository();
        var root = GameSimulation.CreateNewWorld(repo);
        var service = CreateToolService(root, GameSimulation.TerminalPlayerActorId, RejectNotebookValidationAsync);

        var result = await InvokeToolAsync(
            service,
            "EditMemoryNotebookAsync",
            "先记一笔。",
            "<insert side=\"after\" anchor=\"tail\">记住：这里是沙滩。</insert>"
        );

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Contains("validator 拒绝 notebook edit", result.Content);
        Assert.Null(GetProposal(service));
    }

    private Repository CreateRepository() {
        var createResult = Repository.Create(_repoDir);
        return AssertSuccess(createResult);
    }

    private static object CreateToolService(
        DurableDict<string> root,
        string actorId,
        Func<PerceptionBundle, string, string, string, string?, CancellationToken, Task<GameActionValidator.ValidationResult>>? validateActionAsync = null
    ) {
        var toolServiceType = typeof(LlmPlayerAgentDriver).GetNestedType("PlayerActionToolService", BindingFlags.NonPublic);
        Assert.NotNull(toolServiceType);

        var perception = GameSimulation.DescribePerceptionForActor(root, actorId);
        object? service = validateActionAsync is null
            ? Activator.CreateInstance(toolServiceType!, root, actorId, perception)
            : Activator.CreateInstance(toolServiceType!, root, actorId, perception, validateActionAsync);
        Assert.NotNull(service);
        return service!;
    }

    private static TerminalActionExecutionPlan? GetProposal(object service) {
        var property = service.GetType().GetProperty("Proposal", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        return property!.GetValue(service) as TerminalActionExecutionPlan;
    }

    private static PerceptionBundle GetCurrentPerception(object service) {
        var property = service.GetType().GetProperty("CurrentPerception", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        return Assert.IsType<PerceptionBundle>(property!.GetValue(service));
    }

    private static async Task<ToolExecuteResult> InvokeToolAsync(object service, string methodName, params object?[] args) {
        var method = service.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(method);

        var invokeArgs = new object?[args.Length + 1];
        Array.Copy(args, invokeArgs, args.Length);
        invokeArgs[^1] = CancellationToken.None;

        var valueTask = Assert.IsType<ValueTask<ToolExecuteResult>>(method!.Invoke(service, invokeArgs));
        return await valueTask;
    }

    private static T AssertSuccess<T>(AteliaResult<T> result) where T : notnull {
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        return result.Value!;
    }

    private static Task<GameActionValidator.ValidationResult> AcceptAllValidationAsync(
        PerceptionBundle perception,
        string actionKind,
        string actionSummary,
        string preActionReason,
        string? actionPayload,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new GameActionValidator.ValidationResult(true, "通过：测试接受。"));
    }

    private static Task<GameActionValidator.ValidationResult> RejectNotebookValidationAsync(
        PerceptionBundle perception,
        string actionKind,
        string actionSummary,
        string preActionReason,
        string? actionPayload,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new GameActionValidator.ValidationResult(false, "测试拒绝 notebook edit。"));
    }
}
