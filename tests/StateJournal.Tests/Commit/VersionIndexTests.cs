using Atelia.StateJournal;
using FluentAssertions;
using Xunit;

using WorkspaceClass = Atelia.StateJournal.Workspace;

namespace Atelia.StateJournal.Tests.Commit;

/// <summary>
/// VersionIndex 测试。
/// </summary>
/// <remarks>
/// <para>
/// VersionIndex 重构后不再是 IDurableObject，而是 DurableDict(ObjectId=0) 的类型化视图。
/// 本测试通过 Workspace 间接验证 VersionIndex 的集成行为。
/// </para>
/// <para>
/// 对应条款：<c>[F-VERSIONINDEX-REUSE-DURABLEDICT]</c>
/// </para>
/// </remarks>
public class VersionIndexTests {

    #region Well-Known ObjectId 测试

    /// <summary>
    /// VersionIndex 使用 Well-Known ObjectId 0。
    /// </summary>
    [Fact]
    public void VersionIndex_WellKnownObjectId_IsZero() {
        // Assert
        VersionIndex.WellKnownObjectId.Should().Be(0);
    }

    #endregion

    #region Workspace 集成测试：TryGetVersionPtr

    /// <summary>
    /// 新建对象后 Commit，VersionIndex 记录对象的版本指针。
    /// </summary>
    [Fact]
    public void Workspace_CreateObjectAndCommit_VersionIndexRecordsObject() {
        // Arrange
        using var workspace = new WorkspaceClass();

        // Act - 创建对象并提交
        var dict = workspace.CreateObject<DurableDict>();
        dict.Set(1, 100L);
        workspace.Commit();

        // Assert - 验证 VersionIndex 记录了对象
        // 注意：第一个对象的版本指针可能是 0（首次写入位置）
        workspace.TryGetVersionPtr(dict.ObjectId, out var ptr).Should().BeTrue(
            "VersionIndex should contain the committed object"
        );
        // 不强制要求 ptr > 0，因为 position = 0 对首个记录是合法的
    }

    /// <summary>
    /// 未提交对象时 TryGetVersionPtr 返回 false。
    /// </summary>
    [Fact]
    public void Workspace_BeforeCommit_TryGetVersionPtrReturnsFalse() {
        // Arrange
        using var workspace = new WorkspaceClass();
        var dict = workspace.CreateObject<DurableDict>();
        dict.Set(1, 100L);

        // Assert - 未提交时不在 VersionIndex 中
        workspace.TryGetVersionPtr(dict.ObjectId, out var ptr).Should().BeFalse();
        ptr.Should().Be(0);
    }

    /// <summary>
    /// 多个对象都被 VersionIndex 记录。
    /// </summary>
    [Fact]
    public void Workspace_MultipleObjects_AllRecordedInVersionIndex() {
        // Arrange
        using var workspace = new WorkspaceClass();
        var dict1 = workspace.CreateObject<DurableDict>();
        var dict2 = workspace.CreateObject<DurableDict>();
        var dict3 = workspace.CreateObject<DurableDict>();
        dict1.Set(1, 100L);
        dict2.Set(2, 200L);
        dict3.Set(3, 300L);

        // Act
        workspace.Commit();

        // Assert - 所有对象都应该在 VersionIndex 中被记录
        workspace.TryGetVersionPtr(dict1.ObjectId, out var ptr1).Should().BeTrue(
            "VersionIndex should contain dict1"
        );
        workspace.TryGetVersionPtr(dict2.ObjectId, out var ptr2).Should().BeTrue(
            "VersionIndex should contain dict2"
        );
        workspace.TryGetVersionPtr(dict3.ObjectId, out var ptr3).Should().BeTrue(
            "VersionIndex should contain dict3"
        );

        // 每个对象的版本指针应该不同（位置递增）
        // 注意：第一个对象的 ptr 可能是 0（首次写入位置），这是合法的
        ptr2.Should().BeGreaterThan(ptr1, "dict2 位置应该在 dict1 之后");
        ptr3.Should().BeGreaterThan(ptr2, "dict3 位置应该在 dict2 之后");
    }

    /// <summary>
    /// 对不存在的 ObjectId 查询返回 false。
    /// </summary>
    [Fact]
    public void Workspace_TryGetVersionPtr_NonExistentObjectId_ReturnsFalse() {
        // Arrange
        using var workspace = new WorkspaceClass();

        // Assert
        workspace.TryGetVersionPtr(999, out var ptr).Should().BeFalse();
        ptr.Should().Be(0);
    }

    #endregion

    #region Workspace 集成测试：多次 Commit

    /// <summary>
    /// 多次 Commit 更新 VersionIndex 中的版本指针。
    /// </summary>
    [Fact]
    public void Workspace_MultipleCommits_VersionPtrUpdates() {
        // Arrange
        using var workspace = new WorkspaceClass();
        var dict = workspace.CreateObject<DurableDict>();
        dict.Set(1, 100L);
        workspace.Commit();

        workspace.TryGetVersionPtr(dict.ObjectId, out var ptr1).Should().BeTrue();

        // Act - 修改并再次提交
        dict.Set(2, 200L);
        workspace.Commit();

        // Assert - 版本指针应该更新（第二次位置 > 第一次位置）
        workspace.TryGetVersionPtr(dict.ObjectId, out var ptr2).Should().BeTrue();
        ptr2.Should().BeGreaterThan(ptr1, "第二次提交的版本指针应该更大");
    }

    /// <summary>
    /// Clean 对象不参与 Commit，版本指针不变。
    /// </summary>
    [Fact]
    public void Workspace_CleanObject_NotIncludedInCommit() {
        // Arrange
        using var workspace = new WorkspaceClass();
        var dict = workspace.CreateObject<DurableDict>();
        dict.Set(1, 100L);
        workspace.Commit();

        workspace.TryGetVersionPtr(dict.ObjectId, out var ptr1).Should().BeTrue();

        // Act - 空 Commit（无脏对象）
        workspace.Commit();

        // Assert - 版本指针应该不变
        workspace.TryGetVersionPtr(dict.ObjectId, out var ptr2).Should().BeTrue();
        ptr2.Should().Be(ptr1);
    }

    #endregion

    #region Workspace 集成测试：VersionIndexPtr

    /// <summary>
    /// 首次 Commit 设置 VersionIndexPtr。
    /// </summary>
    [Fact]
    public void Workspace_FirstCommit_SetsVersionIndexPtr() {
        // Arrange
        using var workspace = new WorkspaceClass();
        workspace.VersionIndexPtr.Should().Be(0, "初始 VersionIndexPtr 应为 0");

        // Act
        var dict = workspace.CreateObject<DurableDict>();
        dict.Set(1, 100L);
        workspace.Commit();

        // Assert
        workspace.VersionIndexPtr.Should().BeGreaterThan(0);
    }

    /// <summary>
    /// 多次 Commit 更新 VersionIndexPtr。
    /// </summary>
    [Fact]
    public void Workspace_MultipleCommits_VersionIndexPtrUpdates() {
        // Arrange
        using var workspace = new WorkspaceClass();
        var dict = workspace.CreateObject<DurableDict>();
        dict.Set(1, 100L);
        workspace.Commit();

        var ptr1 = workspace.VersionIndexPtr;
        ptr1.Should().BeGreaterThan(0);

        // Act
        dict.Set(2, 200L);
        workspace.Commit();

        // Assert
        workspace.VersionIndexPtr.Should().BeGreaterThan(ptr1);
    }

    /// <summary>
    /// 无脏对象时 Commit 不更新 VersionIndexPtr。
    /// </summary>
    [Fact]
    public void Workspace_EmptyCommit_VersionIndexPtrUnchanged() {
        // Arrange
        using var workspace = new WorkspaceClass();
        var dict = workspace.CreateObject<DurableDict>();
        dict.Set(1, 100L);
        workspace.Commit();

        var ptr1 = workspace.VersionIndexPtr;

        // Act - 空 Commit
        workspace.Commit();

        // Assert
        workspace.VersionIndexPtr.Should().Be(ptr1);
    }

    #endregion

    #region ComputeNextObjectId 逻辑测试（通过 Workspace 验证）

    /// <summary>
    /// 新 Workspace 的 NextObjectId 从 16 开始。
    /// </summary>
    [Fact]
    public void Workspace_NewWorkspace_NextObjectIdIs16() {
        // Arrange & Act
        using var workspace = new WorkspaceClass();

        // Assert
        workspace.NextObjectId.Should().Be(16);
    }

    /// <summary>
    /// 创建对象后 NextObjectId 递增。
    /// </summary>
    [Fact]
    public void Workspace_CreateObject_NextObjectIdIncrements() {
        // Arrange
        using var workspace = new WorkspaceClass();

        // Act
        var dict1 = workspace.CreateObject<DurableDict>();
        var dict2 = workspace.CreateObject<DurableDict>();
        var dict3 = workspace.CreateObject<DurableDict>();

        // Assert
        dict1.ObjectId.Should().Be(16);
        dict2.ObjectId.Should().Be(17);
        dict3.ObjectId.Should().Be(18);
        workspace.NextObjectId.Should().Be(19);
    }

    /// <summary>
    /// 对象 ID 在保留区之后分配。
    /// </summary>
    [Fact]
    public void Workspace_ObjectIds_AfterReservedRange() {
        // Arrange
        using var workspace = new WorkspaceClass();

        // Act
        var dict = workspace.CreateObject<DurableDict>();

        // Assert - ObjectId 应该在保留区（0-15）之后
        dict.ObjectId.Should().BeGreaterOrEqualTo(16);
    }

    #endregion

    #region CommitContext 测试

    /// <summary>
    /// PrepareCommit 返回正确的 CommitContext。
    /// </summary>
    [Fact]
    public void Workspace_PrepareCommit_ReturnsValidContext() {
        // Arrange
        using var workspace = new WorkspaceClass();
        var dict = workspace.CreateObject<DurableDict>();
        dict.Set(1, 100L);

        // Act
        var context = workspace.PrepareCommit();

        // Assert
        context.EpochSeq.Should().Be(1);
        context.VersionIndexPtr.Should().BeGreaterThan(0);
        context.DataTail.Should().BeGreaterThan(0);
    }

    /// <summary>
    /// FinalizeCommit 更新 Workspace 状态。
    /// </summary>
    [Fact]
    public void Workspace_FinalizeCommit_UpdatesState() {
        // Arrange
        using var workspace = new WorkspaceClass();
        var dict = workspace.CreateObject<DurableDict>();
        dict.Set(1, 100L);
        var context = workspace.PrepareCommit();

        // Act
        workspace.FinalizeCommit(context);

        // Assert
        workspace.EpochSeq.Should().Be(context.EpochSeq);
        workspace.DataTail.Should().Be(context.DataTail);
        workspace.VersionIndexPtr.Should().Be(context.VersionIndexPtr);
        workspace.DirtyCount.Should().Be(0);
    }

    #endregion
}
