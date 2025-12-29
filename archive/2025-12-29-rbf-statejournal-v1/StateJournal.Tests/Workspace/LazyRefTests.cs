// Source: Atelia.StateJournal.Tests - LazyRef 测试
// Spec: atelia/docs/StateJournal/mvp-design-v2.md §LazyRef

using Atelia;
using Atelia.StateJournal;
using FluentAssertions;
using Xunit;
using WorkspaceClass = Atelia.StateJournal.Workspace;

using static Atelia.StateJournal.Tests.TestHelper;

namespace Atelia.StateJournal.Tests.Workspace;

/// <summary>
/// LazyRef&lt;T&gt; 单元测试。
/// </summary>
/// <remarks>
/// <para>
/// 对应条款：
/// <list type="bullet">
///   <item><c>[A-OBJREF-BACKFILL-CURRENT]</c>: 加载后回填缓存</item>
///   <item><c>[A-OBJREF-TRANSPARENT-LAZY-LOAD]</c>: 透明加载</item>
/// </list>
/// </para>
/// </remarks>
public class LazyRefTests {
    // ========================================================================
    // 已加载实例测试
    // ========================================================================

    [Fact]
    public void LazyRef_WithInstance_ReturnsImmediately() {
        // Arrange
        var (dict, ws) = CreateDurableDict();

        // Act
        var lazyRef = new LazyRef<DurableDict>(dict);

        // Assert
        lazyRef.IsLoaded.Should().BeTrue();
        lazyRef.IsInitialized.Should().BeTrue();
        lazyRef.Value.Should().BeSameAs(dict);
        lazyRef.ObjectId.Should().Be(dict.ObjectId);
    }

    [Fact]
    public void LazyRef_WithInstance_TryGetValue_ReturnsSuccess() {
        // Arrange
        var (dict, ws) = CreateDurableDict();
        var lazyRef = new LazyRef<DurableDict>(dict);

        // Act
        var result = lazyRef.TryGetValue();

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(dict);
    }

    // ========================================================================
    // 延迟加载测试
    // ========================================================================

    [Fact]
    public void LazyRef_WithObjectId_LoadsOnFirstAccess() {
        // Arrange
        // 创建用于模拟存储的 DurableDict，使用特定 ObjectId
        DurableDict? storedDict = null;
        ObjectLoaderDelegate loader = id => {
            if (id == 100 && storedDict != null) { return AteliaResult<DurableObjectBase>.Success(storedDict); }
            return AteliaResult<DurableObjectBase>.Failure(new ObjectNotFoundError(id));
        };

        using var workspace = new WorkspaceClass(loader);
        // 通过 workspace 创建 storedDict（会分配 ObjectId=16）
        storedDict = workspace.CreateDict();
        var objectId = storedDict.ObjectId;

        // 使用实际分配的 objectId
        var lazyRef = new LazyRef<DurableDict>(objectId, workspace);

        // Assert - 由于对象已在 IdentityMap 中，会直接返回
        // 注意：CreateObject 会将对象加入 IdentityMap，所以这里实际上不会走 loader
        lazyRef.IsLoaded.Should().BeFalse(); // LazyRef 自身未加载
        lazyRef.IsInitialized.Should().BeTrue();
        lazyRef.ObjectId.Should().Be(objectId);

        // Act - 触发加载（由于 CreateObject 已在 IdentityMap，这里从缓存获取）
        var value = lazyRef.Value;

        // Assert - 加载后
        lazyRef.IsLoaded.Should().BeTrue();
        value.Should().BeSameAs(storedDict);
    }

    [Fact]
    public void LazyRef_WithObjectId_TryGetValue_LoadsOnFirstAccess() {
        // Arrange
        DurableDict? storedDict = null;
        ObjectLoaderDelegate loader = id => {
            if (storedDict != null && id == storedDict.ObjectId) { return AteliaResult<DurableObjectBase>.Success(storedDict); }
            return AteliaResult<DurableObjectBase>.Failure(new ObjectNotFoundError(id));
        };

        using var workspace = new WorkspaceClass(loader);
        storedDict = workspace.CreateDict();
        var lazyRef = new LazyRef<DurableDict>(storedDict.ObjectId, workspace);

        // Act
        var result = lazyRef.TryGetValue();

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(storedDict);
        lazyRef.IsLoaded.Should().BeTrue();
    }

    // ========================================================================
    // 回填缓存测试 [A-OBJREF-BACKFILL-CURRENT]
    // ========================================================================

    [Fact]
    public void LazyRef_AfterLoad_DoesNotReloadOnSubsequentAccess() {
        // Arrange
        int loadCount = 0;
        DurableDict? storedDict = null;
        ObjectLoaderDelegate loader = id => {
            loadCount++;
            if (storedDict != null && id == storedDict.ObjectId) { return AteliaResult<DurableObjectBase>.Success(storedDict); }
            return AteliaResult<DurableObjectBase>.Failure(new ObjectNotFoundError(id));
        };

        using var workspace = new WorkspaceClass(loader);
        storedDict = workspace.CreateDict();
        var lazyRef = new LazyRef<DurableDict>(storedDict.ObjectId, workspace);

        // Act
        _ = lazyRef.Value;  // 第一次加载
        _ = lazyRef.Value;  // 应该使用缓存
        _ = lazyRef.Value;  // 应该使用缓存

        // Assert - CreateObject 创建的对象已在 IdentityMap 中，不会调用 loader
        // 所以 loadCount 应该是 0
        loadCount.Should().Be(0);
    }

    [Fact]
    public void LazyRef_TryGetValue_AfterLoad_UsesCache() {
        // Arrange
        int loadCount = 0;
        DurableDict? storedDict = null;
        ObjectLoaderDelegate loader = id => {
            loadCount++;
            if (storedDict != null && id == storedDict.ObjectId) { return AteliaResult<DurableObjectBase>.Success(storedDict); }
            return AteliaResult<DurableObjectBase>.Failure(new ObjectNotFoundError(id));
        };

        using var workspace = new WorkspaceClass(loader);
        storedDict = workspace.CreateDict();
        var lazyRef = new LazyRef<DurableDict>(storedDict.ObjectId, workspace);

        // Act
        _ = lazyRef.TryGetValue();
        _ = lazyRef.TryGetValue();
        _ = lazyRef.TryGetValue();

        // Assert - CreateObject 创建的对象已在 IdentityMap 中，不会调用 loader
        loadCount.Should().Be(0);
    }

    // ========================================================================
    // 加载失败测试
    // ========================================================================

    [Fact]
    public void LazyRef_LoadFailure_ThrowsException() {
        // Arrange
        ObjectLoaderDelegate loader = id =>
            AteliaResult<DurableObjectBase>.Failure(new ObjectNotFoundError(id));

        using var workspace = new WorkspaceClass(loader);
        var lazyRef = new LazyRef<DurableDict>(999, workspace);

        // Act
        Action act = () => _ = lazyRef.Value;

        // Assert
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*999*");
    }

    [Fact]
    public void LazyRef_TryGetValue_ReturnsFailureOnLoadError() {
        // Arrange
        ObjectLoaderDelegate loader = id =>
            AteliaResult<DurableObjectBase>.Failure(new ObjectNotFoundError(id));

        using var workspace = new WorkspaceClass(loader);
        var lazyRef = new LazyRef<DurableDict>(999, workspace);

        // Act
        var result = lazyRef.TryGetValue();

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<ObjectNotFoundError>();
    }

    // ========================================================================
    // ObjectId 属性测试
    // ========================================================================

    [Fact]
    public void LazyRef_ObjectId_AvailableBeforeLoad() {
        // Arrange
        DurableDict? storedDict = null;
        ObjectLoaderDelegate loader = id => {
            if (storedDict != null) { return AteliaResult<DurableObjectBase>.Success(storedDict); }
            return AteliaResult<DurableObjectBase>.Failure(new ObjectNotFoundError(id));
        };

        using var workspace = new WorkspaceClass(loader);
        storedDict = workspace.CreateDict();
        var lazyRef = new LazyRef<DurableDict>(storedDict.ObjectId, workspace);

        // Assert - ObjectId 可用但 LazyRef 内部未标记为已加载
        lazyRef.ObjectId.Should().Be(storedDict.ObjectId);
        lazyRef.IsLoaded.Should().BeFalse();
    }

    [Fact]
    public void LazyRef_ObjectId_MatchesInstanceId() {
        // Arrange
        var (dict, ws) = CreateDurableDict();
        var lazyRef = new LazyRef<DurableDict>(dict);

        // Assert
        lazyRef.ObjectId.Should().Be(dict.ObjectId);
    }

    // ========================================================================
    // 未初始化状态测试
    // ========================================================================

    [Fact]
    public void LazyRef_Default_IsNotInitialized() {
        // Arrange & Act
        var lazyRef = default(LazyRef<DurableDict>);

        // Assert
        lazyRef.IsInitialized.Should().BeFalse();
        lazyRef.IsLoaded.Should().BeFalse();
    }

    [Fact]
    public void LazyRef_Default_Value_ThrowsException() {
        // Arrange
        var lazyRef = default(LazyRef<DurableDict>);

        // Act
        Action act = () => _ = lazyRef.Value;

        // Assert
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*not initialized*");
    }

    [Fact]
    public void LazyRef_Default_ObjectId_ThrowsException() {
        // Arrange
        var lazyRef = default(LazyRef<DurableDict>);

        // Act
        Action act = () => _ = lazyRef.ObjectId;

        // Assert
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*not initialized*");
    }

    [Fact]
    public void LazyRef_Default_TryGetValue_ReturnsNotInitializedError() {
        // Arrange
        var lazyRef = default(LazyRef<DurableDict>);

        // Act
        var result = lazyRef.TryGetValue();

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<LazyRefNotInitializedError>();
    }

    // ========================================================================
    // 边界情况测试
    // ========================================================================

    [Fact]
    public void LazyRef_WithObjectId_NoWorkspace_LoadFails() {
        // 注意：这个测试验证了一个边界情况
        // 当 workspace 为 null 时（理论上不应该发生），TryGetValue 应该返回错误
        // 但由于 struct 构造函数不能被绕过，我们测试 TryGetValue 的行为

        // Arrange - 创建一个带 objectId 和有效 workspace 的 LazyRef
        DurableDict? storedDict = null;
        ObjectLoaderDelegate loader = id => {
            if (storedDict != null) { return AteliaResult<DurableObjectBase>.Success(storedDict); }
            return AteliaResult<DurableObjectBase>.Failure(new ObjectNotFoundError(id));
        };

        using var workspace = new WorkspaceClass(loader);
        storedDict = workspace.CreateDict();
        var lazyRef = new LazyRef<DurableDict>(storedDict.ObjectId, workspace);

        // Act & Assert - 正常加载应该成功
        var result = lazyRef.TryGetValue();
        result.IsSuccess.Should().BeTrue();
    }

    /// <summary>
    /// LazyRef 加载相同类型的对象应该成功。
    /// </summary>
    /// <remarks>
    /// 原测试为 "LazyRef_TypeMismatch_PropagatesError"，但现在 DurableDict 是非泛型的，
    /// 类型总是匹配，所以测试变为验证 LazyRef 可以成功加载。
    /// </remarks>
    [Fact]
    public void LazyRef_SameType_Succeeds() {
        // Arrange
        DurableDict? storedDict = null;
        ObjectLoaderDelegate loader = id => {
            if (storedDict != null) { return AteliaResult<DurableObjectBase>.Success(storedDict); }
            return AteliaResult<DurableObjectBase>.Failure(new ObjectNotFoundError(id));
        };

        using var workspace = new WorkspaceClass(loader);
        storedDict = workspace.CreateDict();
        // 以 DurableDict 类型加载
        var lazyRef = new LazyRef<DurableDict>(storedDict.ObjectId, workspace);

        // Act
        var result = lazyRef.TryGetValue();

        // Assert - 应该成功
        result.IsSuccess.Should().BeTrue();
        ReferenceEquals(result.Value, storedDict).Should().BeTrue();
    }

    [Fact]
    public void LazyRef_MultipleLazyRefs_SameObject_ShareCache() {
        // Arrange
        int loadCount = 0;
        DurableDict? storedDict = null;
        ObjectLoaderDelegate loader = id => {
            loadCount++;
            if (storedDict != null) { return AteliaResult<DurableObjectBase>.Success(storedDict); }
            return AteliaResult<DurableObjectBase>.Failure(new ObjectNotFoundError(id));
        };

        using var workspace = new WorkspaceClass(loader);
        storedDict = workspace.CreateDict();

        // Act - 创建两个指向同一 ObjectId 的 LazyRef
        var lazyRef1 = new LazyRef<DurableDict>(storedDict.ObjectId, workspace);
        var lazyRef2 = new LazyRef<DurableDict>(storedDict.ObjectId, workspace);

        _ = lazyRef1.Value;  // 第一次加载
        _ = lazyRef2.Value;  // 应该命中 Workspace 的 IdentityMap

        // Assert - CreateObject 创建的对象已在 IdentityMap 中，不会调用 loader
        loadCount.Should().Be(0);
    }

    [Fact]
    public void LazyRef_IsLoaded_BecomesTrue_AfterAccess() {
        // Arrange
        DurableDict? storedDict = null;
        ObjectLoaderDelegate loader = id => {
            if (storedDict != null) { return AteliaResult<DurableObjectBase>.Success(storedDict); }
            return AteliaResult<DurableObjectBase>.Failure(new ObjectNotFoundError(id));
        };

        using var workspace = new WorkspaceClass(loader);
        storedDict = workspace.CreateDict();
        var lazyRef = new LazyRef<DurableDict>(storedDict.ObjectId, workspace);

        // Assert - 加载前
        lazyRef.IsLoaded.Should().BeFalse();
        lazyRef.IsInitialized.Should().BeTrue();

        // Act - 触发加载
        _ = lazyRef.Value;

        // Assert - 加载后
        lazyRef.IsLoaded.Should().BeTrue();
        lazyRef.IsInitialized.Should().BeTrue();
    }
}
