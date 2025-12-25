using Atelia.StateJournal;
using FluentAssertions;
using Xunit;

namespace Atelia.StateJournal.Tests.Workspace;

/// <summary>
/// DirtySet 测试。
/// </summary>
/// <remarks>
/// <para>
/// 对应条款：
/// <list type="bullet">
///   <item><c>[S-DIRTYSET-OBJECT-PINNING]</c>: Dirty Set MUST 持有对象实例的强引用</item>
///   <item><c>[S-DIRTY-OBJECT-GC-PROHIBIT]</c>: Dirty 对象不得被 GC 回收</item>
///   <item><c>[S-NEW-OBJECT-AUTO-DIRTY]</c>: 新建对象 MUST 在创建时立即加入 Dirty Set</item>
///   <item><c>[S-IDENTITY-MAP-KEY-COHERENCE]</c>: Dirty Set 的 key 必须等于对象自身 ObjectId</item>
/// </list>
/// </para>
/// </remarks>
public class DirtySetTests {
    #region 基础 Add/Contains/Remove 测试

    /// <summary>
    /// 添加后 Contains 返回 true。
    /// </summary>
    [Fact]
    public void Add_ThenContains_ReturnsTrue() {
        // Arrange
        var set = new DirtySet();
        var obj = new DurableDict<int>(42);

        // Act
        set.Add(obj);

        // Assert
        set.Contains(42).Should().BeTrue();
    }

    /// <summary>
    /// 未添加的 ObjectId Contains 返回 false。
    /// </summary>
    [Fact]
    public void Contains_NonExistent_ReturnsFalse() {
        // Arrange
        var set = new DirtySet();

        // Act & Assert
        set.Contains(999).Should().BeFalse();
    }

    /// <summary>
    /// Remove 后 Contains 返回 false。
    /// </summary>
    [Fact]
    public void Remove_ThenContains_ReturnsFalse() {
        // Arrange
        var set = new DirtySet();
        var obj = new DurableDict<int>(42);
        set.Add(obj);

        // Act
        var removed = set.Remove(42);

        // Assert
        removed.Should().BeTrue();
        set.Contains(42).Should().BeFalse();
    }

    /// <summary>
    /// Remove 不存在的 ObjectId 返回 false。
    /// </summary>
    [Fact]
    public void Remove_NonExistent_ReturnsFalse() {
        // Arrange
        var set = new DirtySet();

        // Act
        var removed = set.Remove(999);

        // Assert
        removed.Should().BeFalse();
    }

    /// <summary>
    /// 添加 null 对象抛 ArgumentNullException。
    /// </summary>
    [Fact]
    public void Add_NullObject_ThrowsArgumentNullException() {
        // Arrange
        var set = new DirtySet();

        // Act
        Action act = () => set.Add(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    /// 重复添加是幂等的（覆盖）。
    /// </summary>
    [Fact]
    public void Add_Duplicate_IsIdempotent() {
        // Arrange
        var set = new DirtySet();
        var obj = new DurableDict<int>(42);

        // Act
        set.Add(obj);
        set.Add(obj);

        // Assert
        set.Count.Should().Be(1);
        set.Contains(42).Should().BeTrue();
    }

    #endregion

    #region 强引用防 GC 测试

    /// <summary>
    /// DirtySet 持有强引用，GC 后对象仍存在。
    /// </summary>
    /// <remarks>
    /// 对应条款：<c>[S-DIRTY-OBJECT-GC-PROHIBIT]</c>
    /// </remarks>
    [Fact]
    public void Add_PreventsGC() {
        // Arrange
        var set = new DirtySet();
        AddObjectAndReleaseLocalReference(set, 42);

        // Act - 强制 GC
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Assert - 对象仍在集合中（因为 DirtySet 持有强引用）
        set.Contains(42).Should().BeTrue();
        set.Count.Should().Be(1);
    }

    /// <summary>
    /// 辅助方法：添加对象后释放局部引用。
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AddObjectAndReleaseLocalReference(DirtySet set, ulong objectId) {
        var obj = new DurableDict<int>(objectId);
        set.Add(obj);
        // obj 局部引用超出作用域，但 DirtySet 仍持有强引用
    }

    /// <summary>
    /// Remove 后对象可被 GC 回收。
    /// </summary>
    [Fact]
    public void Remove_AllowsGC() {
        // Arrange
        var set = new DirtySet();
        var weakRef = AddObjectAndGetWeakReference(set, 42);

        // Act - Remove 后 GC
        set.Remove(42);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Assert - 对象可被回收
        set.Contains(42).Should().BeFalse();
        weakRef.TryGetTarget(out _).Should().BeFalse();
    }

    /// <summary>
    /// 辅助方法：添加对象并返回 WeakReference。
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference<DurableDict<int>> AddObjectAndGetWeakReference(
        DirtySet set, ulong objectId
    ) {
        var obj = new DurableDict<int>(objectId);
        set.Add(obj);
        return new WeakReference<DurableDict<int>>(obj);
    }

    #endregion

    #region GetAll 测试

    /// <summary>
    /// GetAll 返回所有对象。
    /// </summary>
    [Fact]
    public void GetAll_ReturnsAllObjects() {
        // Arrange
        var set = new DirtySet();
        var obj1 = new DurableDict<int>(1);
        var obj2 = new DurableDict<int>(2);
        var obj3 = new DurableDict<int>(3);

        set.Add(obj1);
        set.Add(obj2);
        set.Add(obj3);

        // Act
        var all = set.GetAll().ToList();

        // Assert
        all.Should().HaveCount(3);
        all.Should().Contain(obj1);
        all.Should().Contain(obj2);
        all.Should().Contain(obj3);
    }

    /// <summary>
    /// GetAll 空集合返回空枚举。
    /// </summary>
    [Fact]
    public void GetAll_EmptySet_ReturnsEmpty() {
        // Arrange
        var set = new DirtySet();

        // Act
        var all = set.GetAll();

        // Assert
        all.Should().BeEmpty();
    }

    /// <summary>
    /// GetAll 可用于 CommitAll 场景。
    /// </summary>
    [Fact]
    public void GetAll_ForCommitAll_Scenario() {
        // Arrange
        var set = new DirtySet();
        var obj1 = new DurableDict<long>(1);
        var obj2 = new DurableDict<long>(2);
        obj1.Set(100, 1000L);
        obj2.Set(200, 2000L);

        set.Add(obj1);
        set.Add(obj2);

        // Act - 模拟 CommitAll
        foreach (var obj in set.GetAll()) {
            obj.HasChanges.Should().BeTrue();
        }

        // Assert
        set.Count.Should().Be(2);
    }

    #endregion

    #region Clear 测试

    /// <summary>
    /// Clear 清空集合。
    /// </summary>
    [Fact]
    public void Clear_EmptiesSet() {
        // Arrange
        var set = new DirtySet();
        var obj1 = new DurableDict<int>(1);
        var obj2 = new DurableDict<int>(2);
        set.Add(obj1);
        set.Add(obj2);
        set.Count.Should().Be(2);

        // Act
        set.Clear();

        // Assert
        set.Count.Should().Be(0);
        set.Contains(1).Should().BeFalse();
        set.Contains(2).Should().BeFalse();
        set.GetAll().Should().BeEmpty();
    }

    /// <summary>
    /// Clear 空集合是安全的。
    /// </summary>
    [Fact]
    public void Clear_EmptySet_IsSafe() {
        // Arrange
        var set = new DirtySet();

        // Act
        set.Clear();

        // Assert
        set.Count.Should().Be(0);
    }

    /// <summary>
    /// Clear 后对象可被 GC 回收。
    /// </summary>
    [Fact]
    public void Clear_AllowsGC() {
        // Arrange
        var set = new DirtySet();
        var weakRef = AddObjectAndGetWeakReference(set, 42);
        set.Contains(42).Should().BeTrue();

        // Act
        set.Clear();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Assert
        set.Count.Should().Be(0);
        weakRef.TryGetTarget(out _).Should().BeFalse();
    }

    #endregion

    #region Count 测试

    /// <summary>
    /// Count 反映当前对象数量。
    /// </summary>
    [Fact]
    public void Count_ReflectsObjectCount() {
        // Arrange
        var set = new DirtySet();
        var obj1 = new DurableDict<int>(1);
        var obj2 = new DurableDict<int>(2);

        // Assert
        set.Count.Should().Be(0);

        set.Add(obj1);
        set.Count.Should().Be(1);

        set.Add(obj2);
        set.Count.Should().Be(2);

        set.Remove(1);
        set.Count.Should().Be(1);

        set.Clear();
        set.Count.Should().Be(0);
    }

    #endregion

    #region Key Coherence 测试

    /// <summary>
    /// ObjectId 必须与对象的 ObjectId 属性一致。
    /// </summary>
    /// <remarks>
    /// 对应条款：<c>[S-IDENTITY-MAP-KEY-COHERENCE]</c>
    /// </remarks>
    [Fact]
    public void Add_UsesObjectIdAsKey() {
        // Arrange
        var set = new DirtySet();
        var obj = new DurableDict<int>(12345);

        // Act
        set.Add(obj);

        // Assert - 使用对象的 ObjectId 作为 key
        set.Contains(12345).Should().BeTrue();
        set.Contains(99999).Should().BeFalse();
    }

    #endregion

    #region 复杂场景测试

    /// <summary>
    /// 多次 Add/Remove 操作。
    /// </summary>
    [Fact]
    public void MultipleAddRemove_Operations() {
        // Arrange
        var set = new DirtySet();
        var obj1 = new DurableDict<int>(1);
        var obj2 = new DurableDict<int>(2);
        var obj3 = new DurableDict<int>(3);

        // Act & Assert
        set.Add(obj1);
        set.Add(obj2);
        set.Count.Should().Be(2);

        set.Remove(1);
        set.Count.Should().Be(1);
        set.Contains(1).Should().BeFalse();
        set.Contains(2).Should().BeTrue();

        set.Add(obj3);
        set.Count.Should().Be(2);

        set.Add(obj1);  // 重新添加
        set.Count.Should().Be(3);

        set.Clear();
        set.Count.Should().Be(0);
    }

    /// <summary>
    /// IdentityMap 与 DirtySet 配合使用场景。
    /// </summary>
    [Fact]
    public void IdentityMap_DirtySet_Integration() {
        // Arrange
        var identityMap = new IdentityMap();
        var dirtySet = new DirtySet();
        var obj = new DurableDict<int>(42);

        // Act - 模拟创建新对象
        identityMap.Add(obj);
        dirtySet.Add(obj);  // [S-NEW-OBJECT-AUTO-DIRTY]

        // Assert
        identityMap.TryGet(42, out var fromIdentity).Should().BeTrue();
        dirtySet.Contains(42).Should().BeTrue();
        fromIdentity.Should().BeSameAs(obj);

        // Act - 模拟 Commit 成功
        dirtySet.Remove(42);

        // Assert
        dirtySet.Contains(42).Should().BeFalse();
        identityMap.TryGet(42, out _).Should().BeTrue();  // 仍在 IdentityMap
    }

    #endregion
}
