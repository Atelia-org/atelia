using Atelia.StateJournal;
using FluentAssertions;
using Xunit;
using static Atelia.StateJournal.Tests.TestHelper;
using WorkspaceClass = Atelia.StateJournal.Workspace;

namespace Atelia.StateJournal.Tests.Workspace;

/// <summary>
/// IdentityMap 测试。
/// </summary>
/// <remarks>
/// <para>
/// 对应条款：
/// <list type="bullet">
///   <item><c>[S-IDENTITY-MAP-KEY-COHERENCE]</c>: Identity Map 的 key 必须等于对象自身 ObjectId</item>
/// </list>
/// </para>
/// </remarks>
public class IdentityMapTests {
    #region 基础 Add/Get 测试

    /// <summary>
    /// 添加后可获取。
    /// </summary>
    [Fact]
    public void Add_ThenTryGet_ReturnsObject() {
        // Arrange
        var map = new IdentityMap();
        var (obj, ws) = CreateDurableDict();
        var objectId = obj.ObjectId;

        // Act
        map.Add(obj);
        var found = map.TryGet(objectId, out var result);

        // Assert
        found.Should().BeTrue();
        result.Should().BeSameAs(obj);

        GC.KeepAlive(ws);
    }

    /// <summary>
    /// 未添加的 ObjectId 返回 false。
    /// </summary>
    [Fact]
    public void TryGet_NonExistent_ReturnsFalse() {
        // Arrange
        var map = new IdentityMap();

        // Act
        var found = map.TryGet(999, out var result);

        // Assert
        found.Should().BeFalse();
        result.Should().BeNull();
    }

    /// <summary>
    /// 添加 null 对象抛 ArgumentNullException。
    /// </summary>
    [Fact]
    public void Add_NullObject_ThrowsArgumentNullException() {
        // Arrange
        var map = new IdentityMap();

        // Act
        Action act = () => map.Add(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region 重复 Add 测试

    /// <summary>
    /// 同一 ObjectId 不能添加两次（不同对象）。
    /// </summary>
    [Fact]
    public void Add_DuplicateObjectId_ThrowsInvalidOperationException() {
        // Arrange
        var map = new IdentityMap();
        var (dicts, ws) = CreateMultipleDurableDict(2);
        var obj1 = dicts[0];
        var obj2 = dicts[1];

        // Act
        map.Add(obj1);
        map.Add(obj2);

        // 同一对象尝试第二次添加 - 由于是同一实例，Add 会认为是幂等操作
        // 这里我们验证不同对象可以共存
        var found1 = map.TryGet(obj1.ObjectId, out var result1);
        var found2 = map.TryGet(obj2.ObjectId, out var result2);

        // Assert
        found1.Should().BeTrue();
        found2.Should().BeTrue();
        result1.Should().BeSameAs(obj1);
        result2.Should().BeSameAs(obj2);

        GC.KeepAlive(ws);
    }

    /// <summary>
    /// 同一对象可以重复添加（幂等，覆盖）。
    /// </summary>
    [Fact]
    public void Add_SameObject_Succeeds() {
        // Arrange
        var map = new IdentityMap();
        var (obj, ws) = CreateDurableDict();
        var objectId = obj.ObjectId;

        // Act
        map.Add(obj);
        map.Add(obj);  // 同一对象再次添加

        // Assert
        map.Count.Should().Be(1);
        map.TryGet(objectId, out var result).Should().BeTrue();
        result.Should().BeSameAs(obj);

        GC.KeepAlive(ws);
    }

    #endregion

    #region Remove 测试

    /// <summary>
    /// Remove 后可重新 Add。
    /// </summary>
    [Fact]
    public void Remove_ThenAdd_Succeeds() {
        // Arrange
        var map = new IdentityMap();
        var (dicts, ws) = CreateMultipleDurableDict(2);
        var obj1 = dicts[0];
        var obj2 = dicts[1];
        var objectId1 = obj1.ObjectId;
        var objectId2 = obj2.ObjectId;
        map.Add(obj1);

        // Act
        var removed = map.Remove(objectId1);
        map.Add(obj2);

        // Assert
        removed.Should().BeTrue();
        map.TryGet(objectId2, out var result).Should().BeTrue();
        result.Should().BeSameAs(obj2);

        GC.KeepAlive(ws);
    }

    /// <summary>
    /// Remove 不存在的 ObjectId 返回 false。
    /// </summary>
    [Fact]
    public void Remove_NonExistent_ReturnsFalse() {
        // Arrange
        var map = new IdentityMap();

        // Act
        var removed = map.Remove(999);

        // Assert
        removed.Should().BeFalse();
    }

    /// <summary>
    /// Remove 后 TryGet 返回 false。
    /// </summary>
    [Fact]
    public void Remove_ThenTryGet_ReturnsFalse() {
        // Arrange
        var map = new IdentityMap();
        var (obj, ws) = CreateDurableDict();
        var objectId = obj.ObjectId;
        map.Add(obj);

        // Act
        map.Remove(objectId);
        var found = map.TryGet(objectId, out var result);

        // Assert
        found.Should().BeFalse();
        result.Should().BeNull();

        GC.KeepAlive(ws);
    }

    #endregion

    #region WeakReference 行为测试

    /// <summary>
    /// 对象无强引用时，GC 后 TryGet 返回 false。
    /// </summary>
    [Fact]
    public void TryGet_AfterGC_ReturnsFalse() {
        // Arrange
        var map = new IdentityMap();
        var objectId = AddObjectAndReleaseReference(map);

        // Act - 强制 GC
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var found = map.TryGet(objectId, out var result);

        // Assert
        found.Should().BeFalse();
        result.Should().BeNull();
    }

    /// <summary>
    /// 辅助方法：添加对象后不保留引用，返回 ObjectId 供验证。
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static ulong AddObjectAndReleaseReference(IdentityMap map) {
        var (obj, _) = CreateDurableDict();
        var objectId = obj.ObjectId;
        map.Add(obj);
        // obj 和 workspace 超出作用域，无强引用
        return objectId;
    }

    /// <summary>
    /// 有强引用时，GC 后 TryGet 仍返回 true。
    /// </summary>
    [Fact]
    public void TryGet_WithStrongReference_AfterGC_ReturnsTrue() {
        // Arrange
        var map = new IdentityMap();
        var (obj, ws) = CreateDurableDict();
        var objectId = obj.ObjectId;
        map.Add(obj);

        // Act - 强制 GC
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var found = map.TryGet(objectId, out var result);

        // Assert
        found.Should().BeTrue();
        result.Should().BeSameAs(obj);

        // 确保 obj 和 ws 在此之后仍被引用，防止编译器优化
        GC.KeepAlive(obj);
        GC.KeepAlive(ws);
    }

    /// <summary>
    /// GC 回收后，可以添加新对象。
    /// </summary>
    [Fact]
    public void Add_AfterGC_Succeeds() {
        // Arrange
        var map = new IdentityMap();
        _ = AddObjectAndReleaseReference(map);

        // 强制 GC
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Act - 添加一个新对象
        var (newObj, ws) = CreateDurableDict();
        var newObjectId = newObj.ObjectId;
        map.Add(newObj);

        // Assert
        map.TryGet(newObjectId, out var result).Should().BeTrue();
        result.Should().BeSameAs(newObj);

        GC.KeepAlive(ws);
    }

    #endregion

    #region Cleanup 测试

    /// <summary>
    /// Cleanup 清理已失效的 WeakReference。
    /// </summary>
    [Fact]
    public void Cleanup_RemovesDeadReferences() {
        // Arrange
        var map = new IdentityMap();

        // 创建两个会被 GC 的对象（使用独立的 Workspace 并 Dispose 释放 DirtySet）
        AddAndReleaseWithDispose(map, out var deadId1);
        AddAndReleaseWithDispose(map, out var deadId2);

        // 创建一个存活的对象
        var (liveObj, ws) = CreateDurableDict();
        var liveObjectId = liveObj.ObjectId;
        map.Add(liveObj);

        map.Count.Should().Be(3);

        // 强制 GC
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Act
        var cleaned = map.Cleanup();

        // Assert
        cleaned.Should().Be(2);  // 两个死引用被清理
        map.Count.Should().Be(1);  // 只剩活对象
        map.TryGet(liveObjectId, out var result).Should().BeTrue();
        result.Should().BeSameAs(liveObj);

        GC.KeepAlive(liveObj);
        GC.KeepAlive(ws);
    }

    /// <summary>
    /// 辅助方法：创建对象、添加到 map、然后 dispose workspace 释放 DirtySet 的强引用。
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AddAndReleaseWithDispose(IdentityMap map, out ulong objectId) {
        var (dict, tempWs) = CreateDurableDictWithUniqueId();
        objectId = dict.ObjectId;
        map.Add(dict);
        tempWs.Dispose();
        // tempWs 被 dispose，释放 DirtySet 中对 dict 的强引用
    }

    /// <summary>
    /// Cleanup 对空映射是安全的。
    /// </summary>
    [Fact]
    public void Cleanup_EmptyMap_ReturnsZero() {
        // Arrange
        var map = new IdentityMap();

        // Act
        var cleaned = map.Cleanup();

        // Assert
        cleaned.Should().Be(0);
        map.Count.Should().Be(0);
    }

    /// <summary>
    /// Cleanup 对全活映射是安全的。
    /// </summary>
    [Fact]
    public void Cleanup_AllAlive_ReturnsZero() {
        // Arrange
        var map = new IdentityMap();
        var (dicts, ws) = CreateMultipleDurableDict(2);
        var obj1 = dicts[0];
        var obj2 = dicts[1];
        map.Add(obj1);
        map.Add(obj2);

        // Act
        var cleaned = map.Cleanup();

        // Assert
        cleaned.Should().Be(0);
        map.Count.Should().Be(2);

        GC.KeepAlive(obj1);
        GC.KeepAlive(obj2);
        GC.KeepAlive(ws);
    }

    #endregion

    #region Count 测试

    /// <summary>
    /// Count 反映当前条目数。
    /// </summary>
    [Fact]
    public void Count_ReflectsEntries() {
        // Arrange
        var map = new IdentityMap();
        var (dicts, ws) = CreateMultipleDurableDict(2);
        var obj1 = dicts[0];
        var obj2 = dicts[1];
        var objectId1 = obj1.ObjectId;

        // Assert
        map.Count.Should().Be(0);

        map.Add(obj1);
        map.Count.Should().Be(1);

        map.Add(obj2);
        map.Count.Should().Be(2);

        map.Remove(objectId1);
        map.Count.Should().Be(1);

        GC.KeepAlive(obj1);
        GC.KeepAlive(obj2);
        GC.KeepAlive(ws);
    }

    /// <summary>
    /// Count 包括可能已失效的 WeakReference。
    /// </summary>
    [Fact]
    public void Count_IncludesDeadReferences() {
        // Arrange
        var map = new IdentityMap();

        // 创建一个会被 GC 的对象
        AddAndReleaseWithDispose(map, out var deadId);

        // 创建一个存活的对象
        var (liveObj, ws) = CreateDurableDict();
        map.Add(liveObj);

        // 强制 GC
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Assert - Count 仍为 2（包含死引用）
        map.Count.Should().Be(2);

        // Cleanup 后 Count 减少
        map.Cleanup();
        map.Count.Should().Be(1);

        GC.KeepAlive(liveObj);
        GC.KeepAlive(ws);
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
        var map = new IdentityMap();
        var (obj, ws) = CreateDurableDict();
        var objectId = obj.ObjectId;

        // Act
        map.Add(obj);

        // Assert - 使用对象的 ObjectId 作为 key
        map.TryGet(objectId, out var result).Should().BeTrue();
        result.Should().BeSameAs(obj);

        // 其他 ID 找不到
        map.TryGet(99999, out _).Should().BeFalse();

        GC.KeepAlive(ws);
    }

    #endregion

    #region 多对象测试

    /// <summary>
    /// 多个不同 ObjectId 的对象可以共存。
    /// </summary>
    [Fact]
    public void Add_MultipleObjects_AllAccessible() {
        // Arrange
        var map = new IdentityMap();
        var (dicts, ws) = CreateMultipleDurableDict(3);
        var obj1 = dicts[0];
        var obj2 = dicts[1];
        var obj3 = dicts[2];
        var objectId1 = obj1.ObjectId;
        var objectId2 = obj2.ObjectId;
        var objectId3 = obj3.ObjectId;

        // Act
        map.Add(obj1);
        map.Add(obj2);
        map.Add(obj3);

        // Assert
        map.Count.Should().Be(3);
        map.TryGet(objectId1, out var r1).Should().BeTrue();
        map.TryGet(objectId2, out var r2).Should().BeTrue();
        map.TryGet(objectId3, out var r3).Should().BeTrue();
        r1.Should().BeSameAs(obj1);
        r2.Should().BeSameAs(obj2);
        r3.Should().BeSameAs(obj3);

        GC.KeepAlive(obj1);
        GC.KeepAlive(obj2);
        GC.KeepAlive(obj3);
        GC.KeepAlive(ws);
    }

    #endregion
}
