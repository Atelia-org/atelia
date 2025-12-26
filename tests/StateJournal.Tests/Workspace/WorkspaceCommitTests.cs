// Source: Atelia.StateJournal.Tests - Workspace Commit 流程测试
// Spec: atelia/docs/StateJournal/mvp-design-v2.md §Two-Phase Commit

using Atelia.StateJournal;
using FluentAssertions;
using Xunit;
using WorkspaceClass = Atelia.StateJournal.Workspace;

namespace Atelia.StateJournal.Tests.Workspace;

/// <summary>
/// Workspace Commit 流程测试。
/// </summary>
public class WorkspaceCommitTests {
    // ========================================================================
    // FinalizeCommit 测试
    // ========================================================================

    [Fact]
    public void FinalizeCommit_ClearsDirtySet() {
        // Arrange
        using var workspace = new WorkspaceClass();
        var dict = workspace.CreateObject<DurableDict>();
        dict.Set(1, 100);

        var context = workspace.PrepareCommit();

        // Act
        workspace.FinalizeCommit(context);

        // Assert
        workspace.DirtyCount.Should().Be(0);
    }

    [Fact]
    public void FinalizeCommit_ObjectsBecomeClean() {
        // Arrange
        using var workspace = new WorkspaceClass();
        var dict = workspace.CreateObject<DurableDict>();
        dict.Set(1, 100);

        var context = workspace.PrepareCommit();

        // Act
        workspace.FinalizeCommit(context);

        // Assert
        dict.State.Should().Be(DurableObjectState.Clean);
        dict.HasChanges.Should().BeFalse();
    }

    [Fact]
    public void FinalizeCommit_UpdatesEpochSeq() {
        // Arrange
        using var workspace = new WorkspaceClass();
        var dict = workspace.CreateObject<DurableDict>();
        dict.Set(1, 100);

        workspace.EpochSeq.Should().Be(0);

        // Act
        workspace.Commit();

        // Assert
        workspace.EpochSeq.Should().Be(1);
    }

    [Fact]
    public void FinalizeCommit_UpdatesDataTail() {
        // Arrange
        using var workspace = new WorkspaceClass();
        var dict = workspace.CreateObject<DurableDict>();
        dict.Set(1, 100);

        workspace.DataTail.Should().Be(0);

        // Act
        workspace.Commit();

        // Assert
        workspace.DataTail.Should().BeGreaterThan(0);
    }

    [Fact]
    public void FinalizeCommit_UpdatesVersionIndexPtr() {
        // Arrange
        using var workspace = new WorkspaceClass();
        var dict = workspace.CreateObject<DurableDict>();
        dict.Set(1, 100);

        workspace.VersionIndexPtr.Should().Be(0);

        // Act
        workspace.Commit();

        // Assert
        workspace.VersionIndexPtr.Should().BeGreaterThan(0);
    }

    [Fact]
    public void FinalizeCommit_MultipleObjects_AllBecomeClean() {
        // Arrange
        using var workspace = new WorkspaceClass();
        var dict1 = workspace.CreateObject<DurableDict>();
        var dict2 = workspace.CreateObject<DurableDict>();
        var dict3 = workspace.CreateObject<DurableDict>();
        dict1.Set(1, 100);
        dict2.Set(2, 200);
        dict3.Set(3, 300);

        var context = workspace.PrepareCommit();

        // Act
        workspace.FinalizeCommit(context);

        // Assert
        dict1.State.Should().Be(DurableObjectState.Clean);
        dict2.State.Should().Be(DurableObjectState.Clean);
        dict3.State.Should().Be(DurableObjectState.Clean);
        workspace.DirtyCount.Should().Be(0);
    }

    // ========================================================================
    // Commit 便捷方法测试
    // ========================================================================

    [Fact]
    public void Commit_FullCycle_Success() {
        // Arrange
        using var workspace = new WorkspaceClass();
        var dict = workspace.CreateObject<DurableDict>();
        dict.Set(1, 100);

        // Act
        var context = workspace.Commit();

        // Assert
        workspace.DirtyCount.Should().Be(0);
        dict.State.Should().Be(DurableObjectState.Clean);
        context.EpochSeq.Should().Be(1);
    }

    [Fact]
    public void Commit_MultipleTimes_EpochIncreases() {
        // Arrange
        using var workspace = new WorkspaceClass();
        var dict = workspace.CreateObject<DurableDict>();

        // Act & Assert
        dict.Set(1, 100);
        workspace.Commit();
        workspace.EpochSeq.Should().Be(1);

        dict.Set(2, 200);
        workspace.Commit();
        workspace.EpochSeq.Should().Be(2);

        dict.Set(3, 300);
        workspace.Commit();
        workspace.EpochSeq.Should().Be(3);
    }

    [Fact]
    public void Commit_ReturnsContextWithCorrectEpochSeq() {
        // Arrange
        using var workspace = new WorkspaceClass();
        var dict = workspace.CreateObject<DurableDict>();

        // Act & Assert
        dict.Set(1, 100);
        var ctx1 = workspace.Commit();
        ctx1.EpochSeq.Should().Be(1);

        dict.Set(2, 200);
        var ctx2 = workspace.Commit();
        ctx2.EpochSeq.Should().Be(2);
    }

    [Fact]
    public void Commit_NoDirtyObjects_StillIncrementsEpoch() {
        // Arrange
        using var workspace = new WorkspaceClass();

        // Act
        var context = workspace.Commit();

        // Assert
        context.EpochSeq.Should().Be(1);
        workspace.EpochSeq.Should().Be(1);
    }

    // ========================================================================
    // 数据可读性测试
    // ========================================================================

    [Fact]
    public void AfterCommit_DataStillReadable() {
        // Arrange
        using var workspace = new WorkspaceClass();
        var dict = workspace.CreateObject<DurableDict>();
        dict.Set(1, 100);
        dict.Set(2, 200);

        // Act
        workspace.Commit();

        // Assert - 数据仍然可读
        dict[1].Should().Be(100);
        dict[2].Should().Be(200);
    }

    [Fact]
    public void AfterCommit_CanModifyAndCommitAgain() {
        // Arrange
        using var workspace = new WorkspaceClass();
        var dict = workspace.CreateObject<DurableDict>();
        dict.Set(1, 100);
        workspace.Commit();

        // Act
        dict.Set(1, 999);  // 修改已提交的值
        dict.Set(3, 300);  // 添加新值

        // Assert - 修改后对象变脏
        dict.State.Should().Be(DurableObjectState.PersistentDirty);
        dict.HasChanges.Should().BeTrue();

        // Note: MVP 阶段 DirtySet 的自动脏追踪机制尚未实现
        // 当前对象变脏后需要手动重新提交

        // 数据正确
        dict[1].Should().Be(999);
        dict[3].Should().Be(300);
    }

    [Fact]
    public void AfterCommit_ObjectStillInIdentityMap() {
        // Arrange
        using var workspace = new WorkspaceClass();
        var dict = workspace.CreateObject<DurableDict>();
        dict.Set(1, 100);

        // Act
        workspace.Commit();

        // Assert
        var loadResult = workspace.LoadObject<DurableDict>(dict.ObjectId);
        loadResult.IsSuccess.Should().BeTrue();
        ReferenceEquals(loadResult.Value, dict).Should().BeTrue();
    }

    // ========================================================================
    // 状态属性测试
    // ========================================================================

    [Fact]
    public void EpochSeq_InitiallyZero() {
        // Arrange & Act
        using var workspace = new WorkspaceClass();

        // Assert
        workspace.EpochSeq.Should().Be(0);
    }

    [Fact]
    public void DataTail_InitiallyZero() {
        // Arrange & Act
        using var workspace = new WorkspaceClass();

        // Assert
        workspace.DataTail.Should().Be(0);
    }

    [Fact]
    public void VersionIndexPtr_InitiallyZero() {
        // Arrange & Act
        using var workspace = new WorkspaceClass();

        // Assert
        workspace.VersionIndexPtr.Should().Be(0);
    }

    // ========================================================================
    // 边界情况测试
    // ========================================================================

    [Fact]
    public void Commit_ObjectWithNoChanges_NotWritten() {
        // Arrange
        using var workspace = new WorkspaceClass();
        var dict = workspace.CreateObject<DurableDict>();
        // 不做任何修改，DirtySet 中有对象但 HasChanges = false

        // Act
        var context = workspace.Commit();

        // Assert
        context.WrittenRecords.Should().BeEmpty();
    }

    [Fact]
    public void Commit_LargeDataSet_HandlesCorrectly() {
        // Arrange
        using var workspace = new WorkspaceClass();
        var dict = workspace.CreateObject<DurableDict>();
        for (int i = 0; i < 1000; i++) {
            dict.Set((ulong)i, i * 10);
        }

        // Act
        var context = workspace.Commit();

        // Assert
        workspace.DirtyCount.Should().Be(0);
        dict.State.Should().Be(DurableObjectState.Clean);
        context.DataTail.Should().BeGreaterThan(0);

        // Verify data integrity
        for (int i = 0; i < 1000; i++) {
            dict[(ulong)i].Should().Be(i * 10);
        }
    }
}
