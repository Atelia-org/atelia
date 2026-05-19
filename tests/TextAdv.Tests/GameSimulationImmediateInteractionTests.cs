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

    [Fact]
    public async Task ApplyImmediateSelfInteractionAsync_CanRefreshExistingItemMetadataAfterInspection() {
        GameMasterResolver.SetStubForTests(
            new GameMasterStub(
                ImmediateSelfInteractionResolver: (root, context, cancellationToken) => {
                    cancellationToken.ThrowIfCancellationRequested();
                    var gmTools = new GmWorldEditService(root);
                    var updateResult = gmTools.UpdateItem(
                        context.Interaction.TargetId,
                        name: "铜钥匙",
                        description: "一枚刚从泉底捞出的铜钥匙，齿纹间还沾着湿润细沙。"
                    );
                    Assert.True(updateResult.IsSuccess, updateResult.Error?.Message);
                    return Task.FromResult(
                        new GmExploreResolution(
                            "你把那件闪光的小东西捞到手边，认出它其实是一枚铜钥匙。",
                            UsedLlm: false,
                            FallbackReason: null
                        )
                    );
                }
            )
        );

        using var repo = CreateRepository();
        var root = GameSimulation.CreateNewWorld(repo);
        var gmWorldEdit = new GmWorldEditService(root);
        Assert.True(
            gmWorldEdit.CreateItem(
                itemId: "spring-glint",
                name: "泉底闪光的物件",
                description: "半埋在泉底细沙中，只露出一点模糊的金属反光。",
                locationId: "beach"
            ).IsSuccess
        );
        Assert.True(
            gmWorldEdit.AddInteraction(
                interactionId: "inspect-spring-glint",
                targetRef: "item:spring-glint",
                actionKind: "inspect",
                visibleLabel: "俯身查看泉底那件闪光的物件",
                preconditionNote: "none",
                effectNote: "你把那件闪光的小东西捞到手边，认出它其实是一枚铜钥匙。",
                turnCost: 0,
                effectScope: GameSimulation.SelfEffectScope,
                effectSlots: GameSimulation.ImmediateEffectSlot
            ).IsSuccess
        );

        var resolutionResult = await GameSimulation.ApplyImmediateSelfInteractionAsync(
            root,
            "inspect-spring-glint",
            "先确认那点反光到底是什么，再决定要不要拿走。",
            "通过：这一步 grounded。",
            CancellationToken.None
        );
        Assert.True(resolutionResult.IsSuccess, resolutionResult.Error?.Message);

        var item = Assert.Single(resolutionResult.Value!.NextPerception.Location.Items);
        Assert.Equal("spring-glint", item.ItemId);
        Assert.Equal("铜钥匙", item.Name);
        Assert.Equal("一枚刚从泉底捞出的铜钥匙，齿纹间还沾着湿润细沙。", item.Description);
    }

    [Fact]
    public void DescribeCurrentPerception_ShouldHidePickupInteractionForOwnedItemEvenIfStillVisibleInLedger() {
        using var repo = CreateRepository();
        var root = GameSimulation.CreateNewWorld(repo);
        var gmWorldEdit = new GmWorldEditService(root);
        Assert.True(
            gmWorldEdit.CreateItem(
                itemId: "flint",
                name: "燧石",
                description: "一块边缘锋利的燧石。",
                locationId: "beach"
            ).IsSuccess
        );
        Assert.True(gmWorldEdit.MoveItemToActor("flint", "player").IsSuccess);
        Assert.True(
            gmWorldEdit.AddInteraction(
                interactionId: "take-flint",
                targetRef: "item:flint",
                actionKind: "take",
                visibleLabel: "捡起燧石",
                preconditionNote: "none",
                effectNote: "你把燧石拿了起来。",
                turnCost: 0,
                effectScope: GameSimulation.SelfEffectScope,
                effectSlots: GameSimulation.ImmediateEffectSlot
            ).IsSuccess
        );

        var perception = GameSimulation.DescribeCurrentPerception(root);
        var flint = Assert.Single(perception.InventoryItems);
        Assert.DoesNotContain(flint.Interactions, static interaction => interaction.InteractionId == "take-flint");
        Assert.DoesNotContain(
            PerceptionEvidenceRenderer.EnumerateVisibleInteractions(perception),
            static interaction => interaction.InteractionId == "take-flint"
        );
    }

    [Fact]
    public void PlaceItemAtLocation_ShouldNotSilentlyConsumeExistingPickupInteraction() {
        using var repo = CreateRepository();
        var root = GameSimulation.CreateNewWorld(repo);
        var gmWorldEdit = new GmWorldEditService(root);
        Assert.True(
            gmWorldEdit.CreateItem(
                itemId: "shell-token",
                name: "贝壳片",
                description: "一枚边缘磨圆的白色贝壳片。",
                locationId: "beach"
            ).IsSuccess
        );
        Assert.True(
            gmWorldEdit.AddInteraction(
                interactionId: "take-shell-token",
                targetRef: "item:shell-token",
                actionKind: "take",
                visibleLabel: "捡起贝壳片",
                preconditionNote: "none",
                effectNote: "你把贝壳片拾了起来。",
                turnCost: 0,
                effectScope: GameSimulation.SelfEffectScope,
                effectSlots: GameSimulation.ImmediateEffectSlot
            ).IsSuccess
        );

        Assert.True(gmWorldEdit.MoveItemToActor("shell-token", "player").IsSuccess);
        Assert.True(gmWorldEdit.PlaceItemAtLocation("shell-token", "beach").IsSuccess);

        var item = Assert.Single(GameSimulation.DescribeCurrentPerception(root).Location.Items);
        Assert.Contains(item.Interactions, static interaction => interaction.InteractionId == "take-shell-token");
    }

    [Fact]
    public void AddInteraction_ShouldCanonicalizeLegacyPickupAliasToTake() {
        using var repo = CreateRepository();
        var root = GameSimulation.CreateNewWorld(repo);
        var gmWorldEdit = new GmWorldEditService(root);
        Assert.True(
            gmWorldEdit.CreateItem(
                itemId: "hook",
                name: "弯钩",
                description: "一枚小小的金属弯钩。",
                locationId: "beach"
            ).IsSuccess
        );
        Assert.True(
            gmWorldEdit.AddInteraction(
                interactionId: "pick-up-hook",
                targetRef: "item:hook",
                actionKind: "pick-up",
                visibleLabel: "捡起弯钩",
                preconditionNote: "none",
                effectNote: "你把弯钩捡了起来。",
                turnCost: 0,
                effectScope: GameSimulation.SelfEffectScope,
                effectSlots: GameSimulation.ImmediateEffectSlot
            ).IsSuccess
        );

        var item = Assert.Single(GameSimulation.DescribeCurrentPerception(root).Location.Items);
        var interaction = Assert.Single(item.Interactions);
        Assert.Equal("take", interaction.ActionKind);
    }

    [Fact]
    public void GmToolCatalog_ToolSetMetadataShouldMatchWrappedToolDefinitions() {
        using var repo = CreateRepository();
        var root = GameSimulation.CreateNewWorld(repo);

        foreach (var toolSet in GmToolCatalog.AllToolSets) {
            var declaredToolNames = GmToolCatalog.GetVisibleToolNames(toolSet);
            var wrappedToolNames = GmToolCatalog.CreateExecutor(root, toolSet)
                .GetVisibleToolDefinitions()
                .Select(static definition => definition.Name)
                .ToArray();

            Assert.Equal(declaredToolNames, wrappedToolNames);
            Assert.Equal(declaredToolNames.Count, declaredToolNames.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public void GmToolCatalog_ExploreAuditToolSetShouldExposeExploreAuditTools() {
        using var repo = CreateRepository();
        var root = GameSimulation.CreateNewWorld(repo);

        Assert.Equal(
            [
                GmToolCatalog.CreateItemToolName,
                GmToolCatalog.CreateNpcToolName,
                GmToolCatalog.UpdateItemToolName,
                GmToolCatalog.AddInteractionToolName,
                GmToolCatalog.SetVisibilityToolName,
                GmToolCatalog.SetInteractionVisibilityToolName,
            ],
            GmToolCatalog.GetVisibleToolNames(GmToolCatalog.ToolSets.ExploreAudit)
        );
        Assert.Equal(
            GmToolCatalog.GetVisibleToolNames(GmToolCatalog.ToolSets.ExploreAudit),
            GmToolCatalog.CreateExecutor(root, GmToolCatalog.ToolSets.ExploreAudit)
                .GetVisibleToolDefinitions()
                .Select(static definition => definition.Name)
                .ToArray()
        );
    }

    [Fact]
    public void GmToolCatalog_ExploreMapToolSetShouldExposeOnlyMapMovementTools() {
        Assert.Equal(
            [
                GmToolCatalog.CreateLocationToolName,
                GmToolCatalog.LinkLocationsToolName,
                GmToolCatalog.MoveActorToolName,
            ],
            GmToolCatalog.GetVisibleToolNames(GmToolCatalog.ToolSets.ExploreMap)
        );
    }

    [Fact]
    public void GmToolCatalog_CollectedTurnSummaryToolSetShouldAddActorResolutionTool() {
        var coreTools = GmToolCatalog.GetVisibleToolNames(GmToolCatalog.ToolSets.CollectedTurnCore);
        var summaryTools = GmToolCatalog.GetVisibleToolNames(GmToolCatalog.ToolSets.CollectedTurnSummary);

        Assert.Contains(GmToolCatalog.SetActorResolutionToolName, summaryTools);
        Assert.DoesNotContain(GmToolCatalog.SetActorResolutionToolName, coreTools);
        Assert.Equal(coreTools.Count + 1, summaryTools.Count);
    }

    [Fact]
    public void GmToolCatalog_ImmediateSelfSummaryToolSetShouldExposeSummaryRepairTools() {
        Assert.Equal(
            [
                GmToolCatalog.CreateItemToolName,
                GmToolCatalog.UpdateItemToolName,
                GmToolCatalog.MoveItemToActorToolName,
                GmToolCatalog.PlaceItemAtLocationToolName,
                GmToolCatalog.AddInteractionToolName,
                GmToolCatalog.SetVisibilityToolName,
                GmToolCatalog.SetInteractionVisibilityToolName,
            ],
            GmToolCatalog.GetVisibleToolNames(GmToolCatalog.ToolSets.ImmediateSelfSummary)
        );
    }

    [Fact]
    public void GmToolCatalog_InteractionAuditToolSetShouldBeRestrictedSubsetOfInteractionConsequence() {
        var auditTools = GmToolCatalog.GetVisibleToolNames(GmToolCatalog.ToolSets.InteractionAudit);
        var consequenceTools = GmToolCatalog.GetVisibleToolNames(GmToolCatalog.ToolSets.InteractionConsequence);

        Assert.All(auditTools, toolName => Assert.Contains(toolName, consequenceTools));
        Assert.True(auditTools.Count < consequenceTools.Count);
    }

    [Fact]
    public void GmToolCatalog_ImmediateSelfConsequenceToolSetShouldExcludeNpcAndActorMovement() {
        var consequenceTools = GmToolCatalog.GetVisibleToolNames(GmToolCatalog.ToolSets.ImmediateSelfConsequence);

        Assert.DoesNotContain(GmToolCatalog.CreateNpcToolName, consequenceTools);
        Assert.DoesNotContain(GmToolCatalog.MoveActorToolName, consequenceTools);
    }

    [Fact]
    public void GmToolCatalog_AllToolSetNamesShouldBeUnique() {
        var names = GmToolCatalog.AllToolSets
            .Select(static toolSet => toolSet.Name)
            .ToArray();

        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ValidateExploreMapStageCompletion_ShouldRejectUnknownDirectionWithoutLinkedExit() {
        using var repo = CreateRepository();
        var root = GameSimulation.CreateNewWorld(repo);
        var context = new GmExploreContext(
            Perception: GameSimulation.DescribeCurrentPerception(root),
            CurrentLocationId: "beach",
            Direction: "east",
            Focus: "岩缝",
            PreActionReason: "我想看看东边是否有能遮风的地方。",
            SuggestedReverseDirection: "west"
        );

        var gmWorldEdit = new GmWorldEditService(root);
        Assert.True(gmWorldEdit.MoveActorTo("player", "forest").IsSuccess);

        var error = GameMasterResolver.ValidateExploreMapStageCompletion(root, context);
        Assert.Contains("出口 'east' 正确连接", error);
    }

    [Fact]
    public void ValidateExploreMapStageCompletion_ShouldRejectMissingSuggestedReverseExit() {
        using var repo = CreateRepository();
        var root = GameSimulation.CreateNewWorld(repo);
        var context = new GmExploreContext(
            Perception: GameSimulation.DescribeCurrentPerception(root),
            CurrentLocationId: "beach",
            Direction: "east",
            Focus: "岩缝",
            PreActionReason: "我想看看东边是否有能遮风的地方。",
            SuggestedReverseDirection: "west"
        );

        var gmWorldEdit = new GmWorldEditService(root);
        Assert.True(gmWorldEdit.CreateLocation("beach-east-cove", "东侧岩湾", "一片紧贴礁石的浅湾。").IsSuccess);
        Assert.True(gmWorldEdit.LinkLocations("beach", "east", "beach-east-cove", reverseDirection: null).IsSuccess);
        Assert.True(gmWorldEdit.MoveActorTo("player", "beach-east-cove").IsSuccess);

        var error = GameMasterResolver.ValidateExploreMapStageCompletion(root, context);
        Assert.Contains("建议反向出口 'west'", error);
    }

    [Fact]
    public void ValidateExploreMapStageCompletion_ShouldAcceptUnknownDirectionWhenExitAndReverseExitExist() {
        using var repo = CreateRepository();
        var root = GameSimulation.CreateNewWorld(repo);
        var context = new GmExploreContext(
            Perception: GameSimulation.DescribeCurrentPerception(root),
            CurrentLocationId: "beach",
            Direction: "east",
            Focus: "岩缝",
            PreActionReason: "我想看看东边是否有能遮风的地方。",
            SuggestedReverseDirection: "west"
        );

        var gmWorldEdit = new GmWorldEditService(root);
        Assert.True(gmWorldEdit.CreateLocation("beach-east-cove", "东侧岩湾", "一片紧贴礁石的浅湾。").IsSuccess);
        Assert.True(gmWorldEdit.LinkLocations("beach", "east", "beach-east-cove", reverseDirection: "west").IsSuccess);
        Assert.True(gmWorldEdit.MoveActorTo("player", "beach-east-cove").IsSuccess);

        Assert.Null(GameMasterResolver.ValidateExploreMapStageCompletion(root, context));
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
