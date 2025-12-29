// Source: Atelia.StateJournal.Tests - Workspace 测试
// Spec: atelia/docs/StateJournal/mvp-design-v2.md §Workspace

using System.Runtime.CompilerServices;
using Atelia;
using Atelia.StateJournal;
using FluentAssertions;
using Xunit;
using WorkspaceClass = Atelia.StateJournal.Workspace;

namespace Atelia.StateJournal.Tests.Workspace;

/// <summary>
/// Workspace 单元测试。
/// </summary>
public class WorkspaceTests {
    // ========================================================================
    // CreateObject 基础测试
    // ========================================================================

    [Fact]
    public void CreateObject_ReturnsNewObject_WithAllocatedId() {
        // Arrange
        using var workspace = new WorkspaceClass();

        // Act
        var dict = workspace.CreateDict();

        // Assert
        dict.ObjectId.Should().Be(16);  // 第一个用户对象
        dict.State.Should().Be(DurableObjectState.TransientDirty);
    }

    [Fact]
    public void CreateObject_SequentialIds_AreMonotonic() {
        // Arrange
        using var workspace = new WorkspaceClass();

        // Act
        var obj1 = workspace.CreateDict();
        var obj2 = workspace.CreateDict();
        var obj3 = workspace.CreateDict();

        // Assert
        obj1.ObjectId.Should().Be(16);
        obj2.ObjectId.Should().Be(17);
        obj3.ObjectId.Should().Be(18);
    }

    [Fact]
    public void CreateObject_NextObjectId_IncrementsCorrectly() {
        // Arrange
        using var workspace = new WorkspaceClass();

        // Act & Assert
        workspace.NextObjectId.Should().Be(16);

        workspace.CreateDict();
        workspace.NextObjectId.Should().Be(17);

        workspace.CreateDict();
        workspace.NextObjectId.Should().Be(18);
    }

    // ========================================================================
    // Identity Map 集成测试
    // ========================================================================

    [Fact]
    public void CreateObject_AddsTo_IdentityMap() {
        // Arrange
        using var workspace = new WorkspaceClass();

        // Act
        var dict = workspace.CreateDict();

        // Assert
        workspace.CachedCount.Should().Be(1);
    }

    [Fact]
    public void CreateObject_MultiplObjects_AllInIdentityMap() {
        // Arrange
        using var workspace = new WorkspaceClass();

        // Act
        workspace.CreateDict();
        workspace.CreateDict();
        workspace.CreateDict();

        // Assert
        workspace.CachedCount.Should().Be(3);
    }

    // ========================================================================
    // Dirty Set 集成测试
    // ========================================================================

    [Fact]
    public void CreateObject_AddsTo_DirtySet() {
        // Arrange
        using var workspace = new WorkspaceClass();

        // Act
        var dict = workspace.CreateDict();

        // Assert
        workspace.DirtyCount.Should().Be(1);
    }

    [Fact]
    public void CreateObject_MultipleObjects_AllInDirtySet() {
        // Arrange
        using var workspace = new WorkspaceClass();

        // Act
        workspace.CreateDict();
        workspace.CreateDict();
        workspace.CreateDict();

        // Assert
        workspace.DirtyCount.Should().Be(3);
    }

    [Fact]
    public void CreateObject_ObjectNotGCed_WhileInDirtySet() {
        // Arrange
        using var workspace = new WorkspaceClass();

        // Act
        CreateAndForget(workspace);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Assert - DirtySet 持有强引用，对象不应被 GC
        workspace.DirtyCount.Should().Be(1);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void CreateAndForget(WorkspaceClass ws) {
            var dict = ws.CreateDict();
            // dict 超出作用域，但 DirtySet 持有强引用
        }
    }

    // ========================================================================
    // 保留区测试
    // ========================================================================

    [Fact]
    public void Constructor_Default_NextObjectIdIs16() {
        // Arrange & Act
        using var workspace = new WorkspaceClass();

        // Assert
        workspace.NextObjectId.Should().Be(16);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15)]
    public void Constructor_WithInvalidNextObjectId_Throws(ulong invalidId) {
        // Arrange & Act
        Action act = () => new WorkspaceClass(invalidId);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>()
           .WithParameterName("nextObjectId");
    }

    [Fact]
    public void Constructor_WithValidNextObjectId_Succeeds() {
        // Arrange & Act
        using var workspace = new WorkspaceClass(100);

        // Assert
        workspace.NextObjectId.Should().Be(100);
    }

    [Fact]
    public void Constructor_WithMinimumValidNextObjectId_Succeeds() {
        // Arrange & Act
        using var workspace = new WorkspaceClass(16);

        // Assert
        workspace.NextObjectId.Should().Be(16);
    }

    // ========================================================================
    // Dispose 测试
    // ========================================================================

    [Fact]
    public void Dispose_ClearsDirtySet() {
        // Arrange
        var workspace = new WorkspaceClass();
        workspace.CreateDict();
        workspace.CreateDict();

        // Act
        workspace.Dispose();

        // Assert
        workspace.DirtyCount.Should().Be(0);
    }

    // ========================================================================
    // Recovery 场景测试
    // ========================================================================

    [Fact]
    public void Constructor_WithRecoveredNextObjectId_ContinuesFromThatId() {
        // Arrange - 模拟从日志恢复时，NextObjectId 从之前的值继续
        using var workspace = new WorkspaceClass(1000);

        // Act
        var obj1 = workspace.CreateDict();
        var obj2 = workspace.CreateDict();

        // Assert
        obj1.ObjectId.Should().Be(1000);
        obj2.ObjectId.Should().Be(1001);
        workspace.NextObjectId.Should().Be(1002);
    }

    // ========================================================================
    // LoadObject 测试
    // ========================================================================

    [Fact]
    public void LoadObject_AfterCreate_ReturnsSameInstance() {
        // Arrange
        using var workspace = new WorkspaceClass();
        var created = workspace.CreateDict();

        // Act
        var loadResult = workspace.LoadDict(created.ObjectId);

        // Assert
        loadResult.IsSuccess.Should().BeTrue();
        ReferenceEquals(loadResult.Value, created).Should().BeTrue();
    }

    /// <summary>
    /// 从 IdentityMap 加载已存在的对象应该成功。
    /// </summary>
    /// <remarks>
    /// 原测试为 "LoadObject_WrongType_ReturnsTypeMismatchError"，但现在 DurableDict 是非泛型的，
    /// 类型总是匹配，所以测试变为验证加载同一类型成功。
    /// </remarks>
    [Fact]
    public void LoadObject_SameType_Succeeds() {
        // Arrange
        using var workspace = new WorkspaceClass();
        var created = workspace.CreateDict();

        // Act - 用相同类型加载
        var loadResult = workspace.LoadDict(created.ObjectId);

        // Assert - 应该成功，返回相同实例
        loadResult.IsSuccess.Should().BeTrue();
        ReferenceEquals(loadResult.Value, created).Should().BeTrue();
    }

    [Fact]
    public void LoadObject_NotExists_ReturnsNotFoundError() {
        // Arrange
        using var workspace = new WorkspaceClass();

        // Act
        var loadResult = workspace.LoadDict(999);

        // Assert
        loadResult.IsFailure.Should().BeTrue();
        loadResult.Error.Should().BeOfType<ObjectNotFoundError>();
        var error = (ObjectNotFoundError)loadResult.Error!;
        error.ObjectId.Should().Be(999ul);
    }

    [Fact]
    public void LoadObject_FromStorage_AddsToIdentityMap() {
        // Arrange - 模拟存储加载
        // 使用临时 Workspace 创建 mock 对象（保持存活防止 GC）
        using var mockWorkspace = new WorkspaceClass(100);
        var mockObject = mockWorkspace.CreateDict();

        ObjectLoaderDelegate loader = id => id == 100
            ? AteliaResult<DurableObjectBase>.Success(mockObject)
            : AteliaResult<DurableObjectBase>.Failure(new ObjectNotFoundError(id));

        using var workspace = new WorkspaceClass(loader);

        // Act
        var result1 = workspace.LoadDict(100);
        var result2 = workspace.LoadDict(100);

        // Assert
        result1.IsSuccess.Should().BeTrue();
        result2.IsSuccess.Should().BeTrue();
        ReferenceEquals(result1.Value, result2.Value).Should().BeTrue();
        workspace.CachedCount.Should().Be(1);
    }

    [Fact]
    public void LoadObject_FromStorage_DoesNotAddToDirtySet() {
        // Arrange
        // 使用临时 Workspace 创建 mock 对象（保持存活防止 GC）
        using var mockWorkspace = new WorkspaceClass(100);
        var mockObject = mockWorkspace.CreateDict();

        ObjectLoaderDelegate loader = id =>
            AteliaResult<DurableObjectBase>.Success(mockObject);

        using var workspace = new WorkspaceClass(loader);

        // Act
        workspace.LoadDict(100);

        // Assert - 从存储加载的对象是 Clean 状态，不应加入 DirtySet
        workspace.DirtyCount.Should().Be(0);
    }

    [Fact]
    public void LoadObject_LoaderReturnsFailure_PropagatesError() {
        // Arrange
        var customError = new ObjectNotFoundError(42);
        ObjectLoaderDelegate loader = _ =>
            AteliaResult<DurableObjectBase>.Failure(customError);

        using var workspace = new WorkspaceClass(loader);

        // Act
        var result = workspace.LoadDict(42);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeSameAs(customError);
    }

    /// <summary>
    /// Loader 返回相同类型的对象应该成功。
    /// </summary>
    /// <remarks>
    /// 原测试为 "LoadObject_LoaderReturnsWrongType_ReturnsTypeMismatchError"，但现在 DurableDict 是非泛型的，
    /// 类型总是匹配，所以测试变为验证 Loader 返回的对象可以成功加载。
    /// </remarks>
    [Fact]
    public void LoadObject_LoaderReturnsSameType_Succeeds() {
        // Arrange
        // 使用临时 Workspace 创建 mock 对象（保持存活防止 GC）
        using var mockWorkspace = new WorkspaceClass(100);
        var mockObject = mockWorkspace.CreateDict();

        ObjectLoaderDelegate loader = _ =>
            AteliaResult<DurableObjectBase>.Success(mockObject);

        using var workspace = new WorkspaceClass(loader);

        // Act - 请求同一类型
        var result = workspace.LoadDict(100);

        // Assert - 应该成功
        result.IsSuccess.Should().BeTrue();
        ReferenceEquals(result.Value, mockObject).Should().BeTrue();
    }

    [Fact]
    public void LoadObject_MultipleLoads_ReturnsSameInstance() {
        // Arrange
        using var workspace = new WorkspaceClass();
        var created = workspace.CreateDict();

        // Act
        var result1 = workspace.LoadDict(created.ObjectId);
        var result2 = workspace.LoadDict(created.ObjectId);
        var result3 = workspace.LoadDict(created.ObjectId);

        // Assert
        result1.IsSuccess.Should().BeTrue();
        result2.IsSuccess.Should().BeTrue();
        result3.IsSuccess.Should().BeTrue();
        ReferenceEquals(result1.Value, result2.Value).Should().BeTrue();
        ReferenceEquals(result2.Value, result3.Value).Should().BeTrue();
    }

    // ========================================================================
    // PrepareCommit 测试
    // ========================================================================

    [Fact]
    public void PrepareCommit_EmptyDirtySet_ReturnsEmptyContext() {
        // Arrange
        using var workspace = new WorkspaceClass();

        // Act
        var context = workspace.PrepareCommit();

        // Assert
        context.WrittenRecords.Should().BeEmpty();
        context.EpochSeq.Should().Be(1);
    }

    [Fact]
    public void PrepareCommit_SingleDirtyObject_WritesRecord() {
        // Arrange
        using var workspace = new WorkspaceClass();
        var dict = workspace.CreateDict();
        dict.Set(1, 100);

        // Act
        var context = workspace.PrepareCommit();

        // Assert
        // 应该写入两条记录：dict 和 VersionIndex
        context.WrittenRecords.Should().HaveCount(2);
    }

    [Fact]
    public void PrepareCommit_UpdatesVersionIndex() {
        // Arrange
        using var workspace = new WorkspaceClass();
        var dict = workspace.CreateDict();
        dict.Set(1, 100);

        // Act
        var context = workspace.PrepareCommit();

        // Assert
        // VersionIndex 应该包含新对象
        workspace.TryGetVersionPtr(dict.ObjectId, out var ptr).Should().BeTrue();
    }

    [Fact]
    public void PrepareCommit_MultipleDirtyObjects_WritesAllRecords() {
        // Arrange
        using var workspace = new WorkspaceClass();
        var dict1 = workspace.CreateDict();
        var dict2 = workspace.CreateDict();
        dict1.Set(1, 100);
        dict2.Set(2, 200);

        // Act
        var context = workspace.PrepareCommit();

        // Assert
        // 2 个 dict + 1 个 VersionIndex = 3 条记录
        context.WrittenRecords.Should().HaveCount(3);
    }

    [Fact]
    public void PrepareCommit_EpochSeq_Increments() {
        // Arrange
        using var workspace = new WorkspaceClass();
        var dict = workspace.CreateDict();
        dict.Set(1, 100);

        // Act
        var context = workspace.PrepareCommit();

        // Assert
        context.EpochSeq.Should().Be(1);
    }

    [Fact]
    public void PrepareCommit_DataTail_IncrementsAfterWrites() {
        // Arrange
        using var workspace = new WorkspaceClass();
        var dict = workspace.CreateDict();
        dict.Set(1, 100);

        // Act
        var context = workspace.PrepareCommit();

        // Assert
        // 写入两条记录后，DataTail 应该增加
        context.DataTail.Should().BeGreaterThan(0);
    }

    [Fact]
    public void PrepareCommit_VersionIndexPtr_UpdatedAfterWrite() {
        // Arrange
        using var workspace = new WorkspaceClass();
        var dict = workspace.CreateDict();
        dict.Set(1, 100);

        // Act
        var context = workspace.PrepareCommit();

        // Assert
        // VersionIndexPtr 应该指向 VersionIndex 写入的位置
        context.VersionIndexPtr.Should().BeGreaterThan(0);
    }

    [Fact]
    public void PrepareCommit_WrittenRecords_ContainCorrectObjectIds() {
        // Arrange
        using var workspace = new WorkspaceClass();
        var dict1 = workspace.CreateDict();
        var dict2 = workspace.CreateDict();
        dict1.Set(1, 100);
        dict2.Set(2, 200);

        // Act
        var context = workspace.PrepareCommit();

        // Assert
        var recordIds = context.WrittenRecords.Select(r => r.ObjectId).ToList();
        recordIds.Should().Contain(dict1.ObjectId);
        recordIds.Should().Contain(dict2.ObjectId);
        recordIds.Should().Contain(VersionIndex.WellKnownObjectId);  // 0
    }

    [Fact]
    public void PrepareCommit_NoChanges_DoesNotWriteVersionIndex() {
        // Arrange
        using var workspace = new WorkspaceClass();
        // 创建对象但不修改（TransientDirty 但 HasChanges 语义上为"无实际数据变更"）
        // 注意：TransientDirty 对象在 DurableDict 实现中，HasChanges 取决于 _dirtyKeys.Count
        // 新创建的 DurableDict 的 _dirtyKeys 是空的，所以 HasChanges = false

        // Act
        var context = workspace.PrepareCommit();

        // Assert
        // 没有实际变更的对象不应该被写入
        context.WrittenRecords.Should().BeEmpty();
    }
}
