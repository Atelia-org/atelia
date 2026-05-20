using System.CommandLine;
using System.Reflection;
using Atelia.Completion.Tools;
using Xunit;

namespace Atelia.TextAdv.Tests;

public sealed class PlayerActionGuideCatalogTests {
    [Fact]
    public void SharedReasoningRules_ShouldStayAlignedAcrossLlmManualAndValidatorSection() {
        var sharedRules = PlayerActionGuideCatalog.GetSharedReasoningRuleLines();
        var notebookRule = PlayerActionGuideCatalog.GetNotebookRuleLine();
        var llmManual = PlayerActionGuideCatalog.BuildLlmPlayerManual();
        var validatorSection = GameActionValidator.BuildSharedReasoningSectionForTests();

        foreach (var rule in sharedRules) {
            Assert.Contains(rule, llmManual);
            Assert.Contains(rule, validatorSection);
        }

        Assert.Contains(notebookRule, llmManual);
        Assert.Contains(notebookRule, validatorSection);
    }

    [Fact]
    public void LlmManual_AndInteractToolMetadata_ShouldDescribeUnifiedInteractEntry() {
        var llmManual = PlayerActionGuideCatalog.BuildLlmPlayerManual();
        var interactMetadata = PlayerActionGuideCatalog.GetInteractToolMetadata();

        Assert.Contains("player_interact 是统一 interact 入口", llmManual);
        Assert.Contains("small interaction 立即执行且不结束回合", llmManual);
        Assert.Contains("最终仍必须提交 exactly one Large-Action", llmManual);

        Assert.Contains("统一入口", interactMetadata.Description);
        Assert.Contains("small interaction 立即执行且不结束回合", interactMetadata.Description);
        Assert.Contains("Large-Action proposal 暂存", interactMetadata.Description);
        Assert.Contains("判定这是 small 还是 large", interactMetadata.Parameters[1].Description);
    }

    [Fact]
    public void LlmPlayerRuntimePrompts_ShouldDescribeUnifiedInteractEntry() {
        var systemPrompt = InvokePrivateStringMethod(typeof(LlmPlayerAgentDriver), "BuildSystemPrompt");
        var directorObservation = InvokePrivateStringMethod(
            typeof(LlmPlayerAgentDriver),
            "BuildDirectorNotesObservation",
            "先记住眼前事实。"
        );
        var missingToolCall = InvokePrivateStringMethod(typeof(LlmPlayerAgentDriver), "BuildMissingToolCallObservation");
        var toolFailure = InvokePrivateStringMethod(typeof(LlmPlayerAgentDriver), "BuildToolFailureObservation");
        var afterSmallAction = InvokePrivateStringMethod(typeof(LlmPlayerAgentDriver), "BuildAfterSmallActionObservation");

        Assert.Contains("可以先做 small actions", systemPrompt);
        Assert.Contains("也可以用 `player_interact` 处理当前可见 interaction", systemPrompt);
        Assert.DoesNotContain("exactly one Large-Action 工具", systemPrompt);

        Assert.Contains("系统会判定它是 small 还是 large", directorObservation);
        Assert.Contains("最终落成 exactly one Large-Action", directorObservation);

        Assert.Contains("player_interact 处理当前可见 interaction", missingToolCall);
        Assert.Contains("最终落成 exactly one Large-Action", missingToolCall);
        Assert.DoesNotContain("Large-Action 工具", missingToolCall);

        Assert.Contains("player_interact 处理当前可见 interaction", toolFailure);
        Assert.Contains("最终仍必须落成 exactly one Large-Action", toolFailure);
        Assert.DoesNotContain("Large-Action 工具", toolFailure);

        Assert.Contains("player_interact 处理当前可见 interaction", afterSmallAction);
        Assert.Contains("最终仍要落成 exactly one Large-Action", afterSmallAction);
        Assert.DoesNotContain("Large-Action 工具", afterSmallAction);
    }

    [Fact]
    public void GameEntry_ReasonArgumentDescriptions_ShouldReuseSharedContract() {
        var root = GameEntry.BuildGame();

        AssertReasonArgumentDescription(
            root,
            "edit-memory-notebook",
            PlayerActionGuideCatalog.GetEditMemoryNotebookReasonArgumentDescription()
        );
        AssertReasonArgumentDescription(
            root,
            "explore",
            PlayerActionGuideCatalog.GetExploreReasonArgumentDescription()
        );
        AssertReasonArgumentDescription(
            root,
            "interact",
            PlayerActionGuideCatalog.GetInteractReasonArgumentDescription()
        );
        AssertReasonArgumentDescription(
            root,
            "rest-a-while",
            PlayerActionGuideCatalog.GetRestReasonArgumentDescription()
        );
    }

    [Fact]
    public void PlayerToolMetadata_ShouldReuseSharedTextConstants() {
        var editMetadata = PlayerActionGuideCatalog.GetEditMemoryNotebookToolMetadata();
        Assert.Equal(PlayerActionGuideText.EditMemoryNotebookToolDescription, editMetadata.Description);
        Assert.Equal(PlayerActionGuideText.EditMemoryNotebookReasonToolParamDescription, editMetadata.Parameters[0].Description);
        Assert.Equal(PlayerActionGuideText.EditScriptParameterDescription, editMetadata.Parameters[1].Description);

        var restMetadata = PlayerActionGuideCatalog.GetRestAWhileToolMetadata();
        Assert.Equal(PlayerActionGuideText.RestAWhileToolDescription, restMetadata.Description);
        Assert.Equal(PlayerActionGuideText.RestReasonToolParamDescription, restMetadata.Parameters[0].Description);

        var exploreMetadata = PlayerActionGuideCatalog.GetExploreToolMetadata();
        Assert.Equal(PlayerActionGuideText.ExploreToolDescription, exploreMetadata.Description);
        Assert.Equal(PlayerActionGuideText.ExploreReasonToolParamDescription, exploreMetadata.Parameters[0].Description);
        Assert.Equal(PlayerActionGuideText.DirectionParameterDescription, exploreMetadata.Parameters[1].Description);
        Assert.Equal(PlayerActionGuideText.FocusParameterDescription, exploreMetadata.Parameters[2].Description);

        var interactMetadata = PlayerActionGuideCatalog.GetInteractToolMetadata();
        Assert.Equal(PlayerActionGuideText.InteractToolDescription, interactMetadata.Description);
        Assert.Equal(PlayerActionGuideText.InteractReasonToolParamDescription, interactMetadata.Parameters[0].Description);
        Assert.Equal(PlayerActionGuideText.InteractionIdParameterDescription, interactMetadata.Parameters[1].Description);
    }

    [Fact]
    public void LlmPlayerToolAttributes_ShouldReuseSharedTextConstants() {
        var toolServiceType = typeof(LlmPlayerAgentDriver).GetNestedType("PlayerActionToolService", BindingFlags.NonPublic);
        Assert.NotNull(toolServiceType);

        AssertToolAndParams(
            toolServiceType!,
            "EditMemoryNotebookAsync",
            PlayerActionGuideText.EditMemoryNotebookToolDescription,
            ("reason", PlayerActionGuideText.EditMemoryNotebookReasonAttributeDescription),
            ("edit_script", PlayerActionGuideText.EditScriptParameterDescription)
        );
        AssertToolAndParams(
            toolServiceType!,
            "RestAWhileAsync",
            PlayerActionGuideText.RestAWhileToolDescription,
            ("reason", PlayerActionGuideText.RestReasonAttributeDescription)
        );
        AssertToolAndParams(
            toolServiceType!,
            "ExploreAsync",
            PlayerActionGuideText.ExploreToolDescription,
            ("reason", PlayerActionGuideText.ExploreReasonAttributeDescription),
            ("direction", PlayerActionGuideText.DirectionParameterDescription),
            ("focus", PlayerActionGuideText.FocusParameterDescription)
        );
        AssertToolAndParams(
            toolServiceType!,
            "InteractAsync",
            PlayerActionGuideText.InteractToolDescription,
            ("reason", PlayerActionGuideText.InteractReasonAttributeDescription),
            ("interaction_id", PlayerActionGuideText.InteractionIdParameterDescription)
        );
    }

    private static void AssertReasonArgumentDescription(RootCommand root, string commandName, string expectedDescription) {
        var command = Assert.Single(root.Subcommands, cmd => cmd.Name == commandName);
        var reasonArgument = Assert.Single(command.Arguments, static arg => arg.Name == "reason");
        Assert.Equal(expectedDescription, reasonArgument.Description);
    }

    private static void AssertToolAndParams(
        Type toolServiceType,
        string methodName,
        string expectedToolDescription,
        params (string ParameterName, string ExpectedDescription)[] expectedParameters
    ) {
        var method = toolServiceType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var toolAttribute = method!.GetCustomAttribute<ToolAttribute>();
        Assert.NotNull(toolAttribute);
        Assert.Equal(expectedToolDescription, toolAttribute!.Description);

        foreach (var (parameterName, expectedDescription) in expectedParameters) {
            var parameter = Assert.Single(method.GetParameters(), parameter => parameter.Name == parameterName);
            var parameterAttribute = parameter.GetCustomAttribute<ToolParamAttribute>();
            Assert.NotNull(parameterAttribute);
            Assert.Equal(expectedDescription, parameterAttribute!.Description);
        }
    }

    private static string InvokePrivateStringMethod(Type type, string methodName, params object?[] args) {
        var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<string>(method!.Invoke(null, args));
    }
}
