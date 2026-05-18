using Atelia.StateJournal;
using Xunit;

namespace Atelia.TextAdv.Tests;

public sealed class GameSimulationImmediateInteractionTests : IDisposable {
    private readonly string _repoDir = Path.Combine(
        Path.GetTempPath(),
        "textadv-tests",
        Guid.NewGuid().ToString("N")
    );

    public GameSimulationImmediateInteractionTests() {
        GameMasterResolver.SetStubForTests(GameMasterTestStubs.CreateDeterministicLikeStub());
    }

    public void Dispose() {
        GameMasterResolver.ResetForTests();
        if (Directory.Exists(_repoDir)) {
            Directory.Delete(_repoDir, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyImmediateSelfInteractionAsync_ShouldKeepTurnOpen_AndRecordCommittedNowStep() {
        using var repo = CreateRepository();
        var root = GameSimulation.CreateNewWorld(repo);
        var gmWorldEdit = new GmWorldEditService(root);
        Assert.True(
            gmWorldEdit.CreateItem(
                itemId: "waterskin",
                name: "水袋",
                description: "一个还空着的旧水袋。",
                locationId: "beach"
            ).IsSuccess
        );
        Assert.True(gmWorldEdit.MoveItemToActor("waterskin", "player").IsSuccess);

        var addResult = gmWorldEdit.AddInteraction(
            interactionId: "steady-breath",
            targetRef: "item:waterskin",
            actionKind: "prepare",
            visibleLabel: "把水袋口理顺",
            preconditionNote: "none",
            effectNote: "你深吸一口气，让心绪稍稍平复下来。",
            turnCost: 0,
            effectScope: GameSimulation.SelfEffectScope,
            effectSlots: GameSimulation.ImmediateEffectSlot
        );
        Assert.True(addResult.IsSuccess, addResult.Error?.Message);

        var resolutionResult = await GameSimulation.ApplyImmediateSelfInteractionAsync(
            root,
            "steady-breath",
            "先稳住呼吸，再决定接下来要做什么。",
            "通过：这一步 grounded。",
            CancellationToken.None
        );
        Assert.True(resolutionResult.IsSuccess, resolutionResult.Error?.Message);

        var resolution = resolutionResult.Value!;
        Assert.False(string.IsNullOrWhiteSpace(resolution.Summary));
        Assert.Equal(1, resolution.NextPerception.Day);
        Assert.Equal(1, resolution.NextPerception.Slot);

        var step = Assert.Single(resolution.NextPerception.AcceptedSteps);
        Assert.Equal("small/interact", step.ActionKind);
        Assert.False(step.EndsTurn);
        Assert.Equal(GameSimulation.StepOutcomeCommittedNow, step.StepOutcomeState);
        Assert.Equal(resolution.Summary, step.StepOutcomeSummary);

        var turnStatus = GameSimulation.DescribeCurrentTurnStatus(root);
        Assert.False(turnStatus.AllActiveActorsSubmittedLargeAction);
        Assert.Null(resolution.NextPerception.LastResolution);
    }

    [Fact]
    public async Task ApplyImmediateSelfInteractionAsync_TakeItem_ShouldUpdateInventoryAndHideConsumedInteraction() {
        using var repo = CreateRepository();
        var root = GameSimulation.CreateNewWorld(repo);
        var gmWorldEdit = new GmWorldEditService(root);
        Assert.True(
            gmWorldEdit.CreateItem(
                itemId: "rusty-key",
                name: "生锈的铜钥匙",
                description: "一枚锈迹斑斑的旧钥匙。",
                locationId: "beach"
            ).IsSuccess
        );

        var addResult = gmWorldEdit.AddInteraction(
            interactionId: "take-rusty-key",
            targetRef: "item:rusty-key",
            actionKind: "take",
            visibleLabel: "取走生锈的铜钥匙",
            preconditionNote: "none",
            effectNote: "你把那枚冰凉的铜钥匙收进了手里。",
            turnCost: 0,
            effectScope: GameSimulation.SelfEffectScope,
            effectSlots: GameSimulation.ImmediateEffectSlot
        );
        Assert.True(addResult.IsSuccess, addResult.Error?.Message);

        var resolutionResult = await GameSimulation.ApplyImmediateSelfInteractionAsync(
            root,
            "take-rusty-key",
            "钥匙已经露出来了，先拿在手里。",
            "通过：这一步 grounded。",
            CancellationToken.None
        );
        Assert.True(resolutionResult.IsSuccess, resolutionResult.Error?.Message);

        var perception = resolutionResult.Value!.NextPerception;
        Assert.Contains(perception.InventoryItems, static item => item.ItemId == "rusty-key");
        Assert.DoesNotContain(perception.Location.Items, static item => item.ItemId == "rusty-key");
        Assert.DoesNotContain(
            PerceptionEvidenceRenderer.EnumerateVisibleInteractions(perception),
            static interaction => interaction.InteractionId == "take-rusty-key"
        );

        var step = Assert.Single(perception.AcceptedSteps);
        Assert.Equal(GameSimulation.StepOutcomeCommittedNow, step.StepOutcomeState);
        Assert.False(string.IsNullOrWhiteSpace(step.StepOutcomeSummary));
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
