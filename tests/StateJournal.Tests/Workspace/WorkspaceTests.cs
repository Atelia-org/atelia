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
        var dict = workspace.CreateObject<DurableDict<long?>>();

        // Assert
        dict.ObjectId.Should().Be(16);  // 第一个用户对象
        dict.State.Should().Be(DurableObjectState.TransientDirty);
    }

    [Fact]
    public void CreateObject_SequentialIds_AreMonotonic() {
        // Arrange
        using var workspace = new WorkspaceClass();

        // Act
        var obj1 = workspace.CreateObject<DurableDict<long?>>();
        var obj2 = workspace.CreateObject<DurableDict<long?>>();
        var obj3 = workspace.CreateObject<DurableDict<long?>>();

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

        workspace.CreateObject<DurableDict<long?>>();
        workspace.NextObjectId.Should().Be(17);

        workspace.CreateObject<DurableDict<long?>>();
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
        var dict = workspace.CreateObject<DurableDict<long?>>();

        // Assert
        workspace.CachedCount.Should().Be(1);
    }

    [Fact]
    public void CreateObject_MultiplObjects_AllInIdentityMap() {
        // Arrange
        using var workspace = new WorkspaceClass();

        // Act
        workspace.CreateObject<DurableDict<long?>>();
        workspace.CreateObject<DurableDict<long?>>();
        workspace.CreateObject<DurableDict<long?>>();

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
        var dict = workspace.CreateObject<DurableDict<long?>>();

        // Assert
        workspace.DirtyCount.Should().Be(1);
    }

    [Fact]
    public void CreateObject_MultipleObjects_AllInDirtySet() {
        // Arrange
        using var workspace = new WorkspaceClass();

        // Act
        workspace.CreateObject<DurableDict<long?>>();
        workspace.CreateObject<DurableDict<long?>>();
        workspace.CreateObject<DurableDict<long?>>();

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
            var dict = ws.CreateObject<DurableDict<long?>>();
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
        workspace.CreateObject<DurableDict<long?>>();
        workspace.CreateObject<DurableDict<long?>>();

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
        var obj1 = workspace.CreateObject<DurableDict<long?>>();
        var obj2 = workspace.CreateObject<DurableDict<long?>>();

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
        var created = workspace.CreateObject<DurableDict<long?>>();

        // Act
        var loadResult = workspace.LoadObject<DurableDict<long?>>(created.ObjectId);

        // Assert
        loadResult.IsSuccess.Should().BeTrue();
        ReferenceEquals(loadResult.Value, created).Should().BeTrue();
    }

    [Fact]
    public void LoadObject_WrongType_ReturnsTypeMismatchError() {
        // Arrange
        using var workspace = new WorkspaceClass();
        var created = workspace.CreateObject<DurableDict<long?>>();

        // Act - 尝试用错误的类型加载
        var loadResult = workspace.LoadObject<DurableDict<string?>>(created.ObjectId);

        // Assert
        loadResult.IsFailure.Should().BeTrue();
        loadResult.Error.Should().BeOfType<ObjectTypeMismatchError>();
        var error = (ObjectTypeMismatchError)loadResult.Error!;
        error.ObjectId.Should().Be(created.ObjectId);
        error.ExpectedType.Should().Be(typeof(DurableDict<string?>));
        error.ActualType.Should().Be(typeof(DurableDict<long?>));
    }

    [Fact]
    public void LoadObject_NotExists_ReturnsNotFoundError() {
        // Arrange
        using var workspace = new WorkspaceClass();

        // Act
        var loadResult = workspace.LoadObject<DurableDict<long?>>(999);

        // Assert
        loadResult.IsFailure.Should().BeTrue();
        loadResult.Error.Should().BeOfType<ObjectNotFoundError>();
        var error = (ObjectNotFoundError)loadResult.Error!;
        error.ObjectId.Should().Be(999ul);
    }

    [Fact]
    public void LoadObject_FromStorage_AddsToIdentityMap() {
        // Arrange - 模拟存储加载
        var mockObject = new DurableDict<long?>(100);

        ObjectLoaderDelegate loader = id => id == 100
            ? AteliaResult<IDurableObject>.Success(mockObject)
            : AteliaResult<IDurableObject>.Failure(new ObjectNotFoundError(id));

        using var workspace = new WorkspaceClass(loader);

        // Act
        var result1 = workspace.LoadObject<DurableDict<long?>>(100);
        var result2 = workspace.LoadObject<DurableDict<long?>>(100);

        // Assert
        result1.IsSuccess.Should().BeTrue();
        result2.IsSuccess.Should().BeTrue();
        ReferenceEquals(result1.Value, result2.Value).Should().BeTrue();
        workspace.CachedCount.Should().Be(1);
    }

    [Fact]
    public void LoadObject_FromStorage_DoesNotAddToDirtySet() {
        // Arrange
        var mockObject = new DurableDict<long?>(100);

        ObjectLoaderDelegate loader = id =>
            AteliaResult<IDurableObject>.Success(mockObject);

        using var workspace = new WorkspaceClass(loader);

        // Act
        workspace.LoadObject<DurableDict<long?>>(100);

        // Assert - 从存储加载的对象是 Clean 状态，不应加入 DirtySet
        workspace.DirtyCount.Should().Be(0);
    }

    [Fact]
    public void LoadObject_LoaderReturnsFailure_PropagatesError() {
        // Arrange
        var customError = new ObjectNotFoundError(42);
        ObjectLoaderDelegate loader = _ =>
            AteliaResult<IDurableObject>.Failure(customError);

        using var workspace = new WorkspaceClass(loader);

        // Act
        var result = workspace.LoadObject<DurableDict<long?>>(42);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeSameAs(customError);
    }

    [Fact]
    public void LoadObject_LoaderReturnsWrongType_ReturnsTypeMismatchError() {
        // Arrange
        var mockObject = new DurableDict<long?>(100);

        ObjectLoaderDelegate loader = _ =>
            AteliaResult<IDurableObject>.Success(mockObject);

        using var workspace = new WorkspaceClass(loader);

        // Act - 请求不同的类型
        var result = workspace.LoadObject<DurableDict<string?>>(100);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<ObjectTypeMismatchError>();
    }

    [Fact]
    public void LoadObject_MultipleLoads_ReturnsSameInstance() {
        // Arrange
        using var workspace = new WorkspaceClass();
        var created = workspace.CreateObject<DurableDict<long?>>();

        // Act
        var result1 = workspace.LoadObject<DurableDict<long?>>(created.ObjectId);
        var result2 = workspace.LoadObject<DurableDict<long?>>(created.ObjectId);
        var result3 = workspace.LoadObject<DurableDict<long?>>(created.ObjectId);

        // Assert
        result1.IsSuccess.Should().BeTrue();
        result2.IsSuccess.Should().BeTrue();
        result3.IsSuccess.Should().BeTrue();
        ReferenceEquals(result1.Value, result2.Value).Should().BeTrue();
        ReferenceEquals(result2.Value, result3.Value).Should().BeTrue();
    }
}
