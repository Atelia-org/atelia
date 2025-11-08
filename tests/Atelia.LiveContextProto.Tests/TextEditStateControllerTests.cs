using Atelia.Agent.Core;
using Atelia.Agent.Text;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.LiveContextProto.Tests;

/// <summary>
/// 测试 TextEditStateController 的状态转换逻辑、工具可见性管理与 Flags 推断。
/// </summary>
public sealed class TextEditStateControllerTests {
    [Fact]
    public void Constructor_InitializesWithIdleState() {
        // Arrange & Act
        var (controller, _, _) = CreateController(PersistMode.Immediate);

        // Assert
        Assert.Equal(TextEditWorkflowState.Idle, controller.CurrentState);
        Assert.Equal(PersistMode.Immediate, controller.PersistMode);
    }

    [Theory]
    [InlineData(PersistMode.Immediate, TextEditWorkflowState.Idle)]
    [InlineData(PersistMode.Manual, TextEditWorkflowState.PersistPending)]
    [InlineData(PersistMode.Disabled, TextEditWorkflowState.Idle)]
    public void OnSingleMatchSuccess_TransitionsToCorrectState(PersistMode mode, TextEditWorkflowState expectedState) {
        // Arrange
        var (controller, _, _) = CreateController(mode);

        // Act
        controller.OnSingleMatchSuccess();

        // Assert
        Assert.Equal(expectedState, controller.CurrentState);
    }

    [Fact]
    public void OnMultiMatch_TransitionsToSelectionPending() {
        // Arrange
        var (controller, replaceTool, replaceSelectionTool) = CreateController(PersistMode.Immediate);

        // Act
        controller.OnMultiMatch();

        // Assert
        Assert.Equal(TextEditWorkflowState.SelectionPending, controller.CurrentState);
        Assert.True(replaceSelectionTool.Visible, "replace_selection 工具应该可见");
    }

    [Theory]
    [InlineData(PersistMode.Immediate, TextEditWorkflowState.Idle)]
    [InlineData(PersistMode.Manual, TextEditWorkflowState.PersistPending)]
    [InlineData(PersistMode.Disabled, TextEditWorkflowState.Idle)]
    public void OnSelectionConfirmed_TransitionsToCorrectState(PersistMode mode, TextEditWorkflowState expectedState) {
        // Arrange
        var (controller, _, replaceSelectionTool) = CreateController(mode);
        controller.OnMultiMatch(); // 先进入 SelectionPending

        // Act
        controller.OnSelectionConfirmed();

        // Assert
        Assert.Equal(expectedState, controller.CurrentState);
        Assert.False(replaceSelectionTool.Visible, "选区确认后 replace_selection 应该隐藏");
    }

    [Fact]
    public void OnExternalConflict_TransitionsToOutOfSync() {
        // Arrange
        var (controller, _, _) = CreateController(PersistMode.Immediate);

        // Act
        controller.OnExternalConflict();

        // Assert
        Assert.Equal(TextEditWorkflowState.OutOfSync, controller.CurrentState);
    }

    [Fact]
    public void OnPersistFailure_TransitionsToOutOfSync() {
        // Arrange
        var (controller, _, _) = CreateController(PersistMode.Manual);
        controller.OnSingleMatchSuccess(); // 进入 PersistPending

        // Act
        controller.OnPersistFailure();

        // Assert
        Assert.Equal(TextEditWorkflowState.OutOfSync, controller.CurrentState);
    }

    [Fact]
    public void OnClearSelection_TransitionsFromSelectionPendingToIdle() {
        // Arrange
        var (controller, _, replaceSelectionTool) = CreateController(PersistMode.Immediate);
        controller.OnMultiMatch(); // 进入 SelectionPending

        // Act
        controller.OnClearSelection();

        // Assert
        Assert.Equal(TextEditWorkflowState.Idle, controller.CurrentState);
        Assert.False(replaceSelectionTool.Visible, "清除选区后 replace_selection 应该隐藏");
    }

    [Fact]
    public void OnClearSelection_InManualMode_RestoresPersistPendingState() {
        // Arrange: Manual 模式下，先单匹配进入 PersistPending，再多匹配进入 SelectionPending
        var (controller, _, replaceSelectionTool) = CreateController(PersistMode.Manual);
        controller.OnSingleMatchSuccess(); // Idle → PersistPending
        Assert.Equal(TextEditWorkflowState.PersistPending, controller.CurrentState);

        controller.OnMultiMatch(); // PersistPending → SelectionPending
        Assert.Equal(TextEditWorkflowState.SelectionPending, controller.CurrentState);

        // Act: 清除选区
        controller.OnClearSelection();

        // Assert: 应该恢复到 PersistPending，而不是 Idle
        Assert.Equal(TextEditWorkflowState.PersistPending, controller.CurrentState);
        Assert.False(replaceSelectionTool.Visible, "清除选区后 replace_selection 应该隐藏");
    }

    [Fact]
    public void OnMultiMatch_AfterOutOfSync_PreservesOutOfSyncState() {
        // Arrange: 进入 OutOfSync 状态后触发多匹配
        var (controller, _, replaceSelectionTool) = CreateController(PersistMode.Immediate);
        controller.OnExternalConflict(); // 进入 OutOfSync
        Assert.Equal(TextEditWorkflowState.OutOfSync, controller.CurrentState);

        // Act: 触发多匹配
        controller.OnMultiMatch();
        Assert.Equal(TextEditWorkflowState.SelectionPending, controller.CurrentState);

        // 清除选区
        controller.OnClearSelection();

        // Assert: 应该恢复到 OutOfSync
        Assert.Equal(TextEditWorkflowState.OutOfSync, controller.CurrentState);
        Assert.False(replaceSelectionTool.Visible, "清除选区后 replace_selection 应该隐藏");
    }

    [Fact]
    public void OnMultiMatch_ConsecutiveCalls_PreservesCorrectState() {
        // Arrange: Manual 模式下连续触发多匹配
        var (controller, _, replaceSelectionTool) = CreateController(PersistMode.Manual);
        controller.OnSingleMatchSuccess(); // Idle → PersistPending

        // Act 1: 第一次多匹配
        controller.OnMultiMatch(); // PersistPending → SelectionPending
        controller.OnClearSelection(); // SelectionPending → PersistPending
        Assert.Equal(TextEditWorkflowState.PersistPending, controller.CurrentState);

        // Act 2: 第二次多匹配
        controller.OnMultiMatch(); // PersistPending → SelectionPending
        Assert.True(replaceSelectionTool.Visible, "多匹配后 replace_selection 应该可见");

        // Act 3: 再次清除选区
        controller.OnClearSelection();

        // Assert: 应该再次恢复到 PersistPending
        Assert.Equal(TextEditWorkflowState.PersistPending, controller.CurrentState);
        Assert.False(replaceSelectionTool.Visible, "清除选区后 replace_selection 应该隐藏");
    }

    [Theory]
    [InlineData(TextEditWorkflowState.Idle, PersistMode.Immediate, TextEditFlag.None)]
    [InlineData(TextEditWorkflowState.Idle, PersistMode.Disabled, TextEditFlag.PersistReadOnly)]
    [InlineData(TextEditWorkflowState.SelectionPending, PersistMode.Immediate, TextEditFlag.SelectionPending)]
    [InlineData(TextEditWorkflowState.PersistPending, PersistMode.Manual, TextEditFlag.PersistPending)]
    [InlineData(TextEditWorkflowState.OutOfSync, PersistMode.Immediate, TextEditFlag.OutOfSync)]
    public void DeriveFlags_ReturnsCorrectFlagsForState(
        TextEditWorkflowState state,
        PersistMode mode,
        TextEditFlag expectedFlags
    ) {
        // Arrange
        var (controller, _, _) = CreateController(mode);
        TransitionToState(controller, state);

        // Act
        var flags = controller.DeriveFlags();

        // Assert
        Assert.Equal(expectedFlags, flags);
    }

    [Theory]
    [InlineData(TextEditStatus.Success, TextEditFlag.None)]
    [InlineData(TextEditStatus.ExternalConflict, TextEditFlag.ExternalConflict | TextEditFlag.DiagnosticHint)]
    [InlineData(TextEditStatus.PersistFailure, TextEditFlag.PersistPending | TextEditFlag.DiagnosticHint)]
    [InlineData(TextEditStatus.Exception, TextEditFlag.DiagnosticHint)]
    public void DeriveStatusFlags_ReturnsCorrectFlagsForStatus(TextEditStatus status, TextEditFlag expectedFlags) {
        // Act
        var flags = TextEditStateController.DeriveStatusFlags(status);

        // Assert
        Assert.Equal(expectedFlags, flags);
    }

    [Fact]
    public void ShouldToolBeVisible_ReplaceToolHiddenInRefreshingState() {
        // Arrange
        var (controller, replaceTool, _) = CreateController(PersistMode.Immediate);

        // 模拟进入 Refreshing 状态（当前控制器未实现，暂时跳过）
        // 这个测试在实现 Refreshing 状态后补充

        // Act & Assert (当前状态下 replace 应该可见)
        Assert.True(controller.ShouldToolBeVisible(replaceTool.Name));
    }

    [Fact]
    public void ShouldToolBeVisible_ReplaceSelectionToolVisibleOnlyInSelectionPending() {
        // Arrange
        var (controller, _, replaceSelectionTool) = CreateController(PersistMode.Immediate);

        // Assert: 初始状态 (Idle) 应该隐藏
        Assert.False(controller.ShouldToolBeVisible(replaceSelectionTool.Name));

        // Act: 进入 SelectionPending
        controller.OnMultiMatch();

        // Assert: 现在应该可见
        Assert.True(controller.ShouldToolBeVisible(replaceSelectionTool.Name));

        // Act: 退出 SelectionPending
        controller.OnClearSelection();

        // Assert: 应该再次隐藏
        Assert.False(controller.ShouldToolBeVisible(replaceSelectionTool.Name));
    }

    [Fact]
    public void GetRecommendedGuidance_ReturnsCorrectGuidanceForSelectionPending() {
        // Arrange
        var (controller, replaceTool, replaceSelectionTool) = CreateController(PersistMode.Immediate);
        controller.OnMultiMatch();

        // Act
        var guidance = controller.GetRecommendedGuidance(
            TextEditStatus.MultiMatch,
            replaceTool.Name,
            replaceSelectionTool.Name
        );

        // Assert
        Assert.NotNull(guidance);
        Assert.Contains(replaceSelectionTool.Name, guidance);
        Assert.Contains("selection_id", guidance);
    }

    [Fact]
    public void GetRecommendedGuidance_IncludesReadOnlyHintInDisabledMode() {
        // Arrange
        var (controller, replaceTool, replaceSelectionTool) = CreateController(PersistMode.Disabled);

        // Act
        var guidance = controller.GetRecommendedGuidance(
            TextEditStatus.Success,
            replaceTool.Name,
            replaceSelectionTool.Name
        );

        // Assert
        Assert.NotNull(guidance);
        Assert.Contains("只读模式", guidance);
        Assert.Contains("缓存", guidance);
    }

    // 辅助方法：创建控制器和模拟工具
    private static (TextEditStateController Controller, MockTool ReplaceTool, MockTool ReplaceSelectionTool) CreateController(
        PersistMode mode
    ) {
        var replaceTool = new MockTool("test_replace");
        var replaceSelectionTool = new MockTool("test_replace_selection");
        var controller = new TextEditStateController(mode, replaceTool, replaceSelectionTool);
        return (controller, replaceTool, replaceSelectionTool);
    }

    // 辅助方法：将控制器转换到指定状态
    private static void TransitionToState(TextEditStateController controller, TextEditWorkflowState targetState) {
        switch (targetState) {
            case TextEditWorkflowState.Idle:
                // 已经是 Idle，无需操作
                break;
            case TextEditWorkflowState.SelectionPending:
                controller.OnMultiMatch();
                break;
            case TextEditWorkflowState.PersistPending:
                controller.OnSingleMatchSuccess();
                break;
            case TextEditWorkflowState.OutOfSync:
                controller.OnExternalConflict();
                break;
            case TextEditWorkflowState.Refreshing:
                // 未实现，暂时不支持
                throw new NotImplementedException("Refreshing state not yet implemented");
            default:
                throw new ArgumentOutOfRangeException(nameof(targetState), targetState, null);
        }
    }

    // 模拟 ITool 实现，用于测试
    private sealed class MockTool : ITool {
        public MockTool(string name) {
            Name = name;
        }

        public string Name { get; }
        public string Description => "Mock tool for testing";
        public IReadOnlyList<ToolParamSpec> Parameters => Array.Empty<ToolParamSpec>();
        public bool Visible { get; set; } = true;

        public System.Threading.Tasks.ValueTask<Atelia.Agent.Core.Tool.LodToolExecuteResult> ExecuteAsync(
            IReadOnlyDictionary<string, object?>? arguments,
            System.Threading.CancellationToken cancellationToken
        ) {
            throw new NotImplementedException();
        }
    }
}
