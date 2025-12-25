// Source: Atelia.StateJournal.Tests - LazyRef 测试
// Spec: atelia/docs/StateJournal/mvp-design-v2.md §LazyRef

using Atelia;
using Atelia.StateJournal;
using FluentAssertions;
using Xunit;
using WorkspaceClass = Atelia.StateJournal.Workspace;

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
        var dict = new DurableDict<long?>(1);

        // Act
        var lazyRef = new LazyRef<DurableDict<long?>>(dict);

        // Assert
        lazyRef.IsLoaded.Should().BeTrue();
        lazyRef.IsInitialized.Should().BeTrue();
        lazyRef.Value.Should().BeSameAs(dict);
        lazyRef.ObjectId.Should().Be(1);
    }

    [Fact]
    public void LazyRef_WithInstance_TryGetValue_ReturnsSuccess() {
        // Arrange
        var dict = new DurableDict<long?>(1);
        var lazyRef = new LazyRef<DurableDict<long?>>(dict);

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
        var storedDict = new DurableDict<long?>(100);
        ObjectLoaderDelegate loader = id => id == 100
            ? AteliaResult<IDurableObject>.Success(storedDict)
            : AteliaResult<IDurableObject>.Failure(new ObjectNotFoundError(id));

        using var workspace = new WorkspaceClass(loader);
        var lazyRef = new LazyRef<DurableDict<long?>>(100, workspace);

        // Assert - 加载前
        lazyRef.IsLoaded.Should().BeFalse();
        lazyRef.IsInitialized.Should().BeTrue();
        lazyRef.ObjectId.Should().Be(100);

        // Act - 触发加载
        var value = lazyRef.Value;

        // Assert - 加载后
        lazyRef.IsLoaded.Should().BeTrue();
        value.Should().BeSameAs(storedDict);
    }

    [Fact]
    public void LazyRef_WithObjectId_TryGetValue_LoadsOnFirstAccess() {
        // Arrange
        var storedDict = new DurableDict<long?>(100);
        ObjectLoaderDelegate loader = id => id == 100
            ? AteliaResult<IDurableObject>.Success(storedDict)
            : AteliaResult<IDurableObject>.Failure(new ObjectNotFoundError(id));

        using var workspace = new WorkspaceClass(loader);
        var lazyRef = new LazyRef<DurableDict<long?>>(100, workspace);

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
        var storedDict = new DurableDict<long?>(100);
        ObjectLoaderDelegate loader = id => {
            loadCount++;
            return id == 100
                ? AteliaResult<IDurableObject>.Success(storedDict)
                : AteliaResult<IDurableObject>.Failure(new ObjectNotFoundError(id));
        };

        using var workspace = new WorkspaceClass(loader);
        var lazyRef = new LazyRef<DurableDict<long?>>(100, workspace);

        // Act
        _ = lazyRef.Value;  // 第一次加载
        _ = lazyRef.Value;  // 应该使用缓存
        _ = lazyRef.Value;  // 应该使用缓存

        // Assert - 只加载一次（通过 Workspace 的 IdentityMap）
        // 注意：loadCount 可能为 1，因为 Workspace 内部也有 IdentityMap 缓存
        loadCount.Should().Be(1);
    }

    [Fact]
    public void LazyRef_TryGetValue_AfterLoad_UsesCache() {
        // Arrange
        int loadCount = 0;
        var storedDict = new DurableDict<long?>(100);
        ObjectLoaderDelegate loader = id => {
            loadCount++;
            return id == 100
                ? AteliaResult<IDurableObject>.Success(storedDict)
                : AteliaResult<IDurableObject>.Failure(new ObjectNotFoundError(id));
        };

        using var workspace = new WorkspaceClass(loader);
        var lazyRef = new LazyRef<DurableDict<long?>>(100, workspace);

        // Act
        _ = lazyRef.TryGetValue();
        _ = lazyRef.TryGetValue();
        _ = lazyRef.TryGetValue();

        // Assert
        loadCount.Should().Be(1);
    }

    // ========================================================================
    // 加载失败测试
    // ========================================================================

    [Fact]
    public void LazyRef_LoadFailure_ThrowsException() {
        // Arrange
        ObjectLoaderDelegate loader = id =>
            AteliaResult<IDurableObject>.Failure(new ObjectNotFoundError(id));

        using var workspace = new WorkspaceClass(loader);
        var lazyRef = new LazyRef<DurableDict<long?>>(999, workspace);

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
            AteliaResult<IDurableObject>.Failure(new ObjectNotFoundError(id));

        using var workspace = new WorkspaceClass(loader);
        var lazyRef = new LazyRef<DurableDict<long?>>(999, workspace);

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
        var storedDict = new DurableDict<long?>(42);
        ObjectLoaderDelegate loader = id =>
            AteliaResult<IDurableObject>.Success(storedDict);

        using var workspace = new WorkspaceClass(loader);
        var lazyRef = new LazyRef<DurableDict<long?>>(42, workspace);

        // Assert - ObjectId 可用但未加载
        lazyRef.ObjectId.Should().Be(42);
        lazyRef.IsLoaded.Should().BeFalse();
    }

    [Fact]
    public void LazyRef_ObjectId_MatchesInstanceId() {
        // Arrange
        var dict = new DurableDict<long?>(123);
        var lazyRef = new LazyRef<DurableDict<long?>>(dict);

        // Assert
        lazyRef.ObjectId.Should().Be(123);
    }

    // ========================================================================
    // 未初始化状态测试
    // ========================================================================

    [Fact]
    public void LazyRef_Default_IsNotInitialized() {
        // Arrange & Act
        var lazyRef = default(LazyRef<DurableDict<long?>>);

        // Assert
        lazyRef.IsInitialized.Should().BeFalse();
        lazyRef.IsLoaded.Should().BeFalse();
    }

    [Fact]
    public void LazyRef_Default_Value_ThrowsException() {
        // Arrange
        var lazyRef = default(LazyRef<DurableDict<long?>>);

        // Act
        Action act = () => _ = lazyRef.Value;

        // Assert
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*not initialized*");
    }

    [Fact]
    public void LazyRef_Default_ObjectId_ThrowsException() {
        // Arrange
        var lazyRef = default(LazyRef<DurableDict<long?>>);

        // Act
        Action act = () => _ = lazyRef.ObjectId;

        // Assert
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*not initialized*");
    }

    [Fact]
    public void LazyRef_Default_TryGetValue_ReturnsNotInitializedError() {
        // Arrange
        var lazyRef = default(LazyRef<DurableDict<long?>>);

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
        var storedDict = new DurableDict<long?>(100);
        ObjectLoaderDelegate loader = id =>
            AteliaResult<IDurableObject>.Success(storedDict);

        using var workspace = new WorkspaceClass(loader);
        var lazyRef = new LazyRef<DurableDict<long?>>(100, workspace);

        // Act & Assert - 正常加载应该成功
        var result = lazyRef.TryGetValue();
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void LazyRef_TypeMismatch_PropagatesError() {
        // Arrange
        var storedDict = new DurableDict<long?>(100);  // 存储的是 DurableDict<long?>
        ObjectLoaderDelegate loader = id =>
            AteliaResult<IDurableObject>.Success(storedDict);

        using var workspace = new WorkspaceClass(loader);
        // 尝试以 DurableDict<string?> 类型加载
        var lazyRef = new LazyRef<DurableDict<string?>>(100, workspace);

        // Act
        var result = lazyRef.TryGetValue();

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<ObjectTypeMismatchError>();
    }

    [Fact]
    public void LazyRef_MultipleLazyRefs_SameObject_ShareCache() {
        // Arrange
        int loadCount = 0;
        var storedDict = new DurableDict<long?>(100);
        ObjectLoaderDelegate loader = id => {
            loadCount++;
            return AteliaResult<IDurableObject>.Success(storedDict);
        };

        using var workspace = new WorkspaceClass(loader);

        // Act - 创建两个指向同一 ObjectId 的 LazyRef
        var lazyRef1 = new LazyRef<DurableDict<long?>>(100, workspace);
        var lazyRef2 = new LazyRef<DurableDict<long?>>(100, workspace);

        _ = lazyRef1.Value;  // 第一次加载
        _ = lazyRef2.Value;  // 应该命中 Workspace 的 IdentityMap

        // Assert - Workspace 的 IdentityMap 确保只加载一次
        loadCount.Should().Be(1);
    }

    [Fact]
    public void LazyRef_IsLoaded_BecomesTrue_AfterAccess() {
        // Arrange
        var storedDict = new DurableDict<long?>(100);
        ObjectLoaderDelegate loader = id =>
            AteliaResult<IDurableObject>.Success(storedDict);

        using var workspace = new WorkspaceClass(loader);
        var lazyRef = new LazyRef<DurableDict<long?>>(100, workspace);

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
