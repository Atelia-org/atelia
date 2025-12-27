using Atelia.StateJournal;
using FluentAssertions;
using Xunit;
using static Atelia.StateJournal.Tests.TestHelper;

namespace Atelia.StateJournal.Tests.Objects;

/// <summary>
/// DurableDict 基础功能测试（构造、Set/Get/Remove、Working State）。
/// </summary>
public class DurableDictBasicTests {

    /// <summary>
    /// 辅助方法：将 Dictionary&lt;ulong, T&gt; 转换为 Dictionary&lt;ulong, object?&gt;。
    /// </summary>
    private static Dictionary<ulong, object?> ToObjectDict<T>(Dictionary<ulong, T> source)
        => source.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);

    #region 构造函数测试

    /// <summary>
    /// 新创建的 DurableDict 处于 TransientDirty 状态。
    /// </summary>
    [Fact]
    public void Constructor_New_SetsTransientDirtyState() {
        // Act
        var (dict, ws) = CreateDurableDict();

        // Assert
        dict.ObjectId.Should().BeGreaterThan(0UL);
        dict.State.Should().Be(DurableObjectState.TransientDirty);
        dict.HasChanges.Should().BeFalse(); // 新建但未修改，_dirtyKeys 为空
        dict.Count.Should().Be(0);
    }

    /// <summary>
    /// 从 Committed State 加载的 DurableDict 处于 Clean 状态。
    /// </summary>
    [Fact]
    public void Constructor_FromCommitted_SetsCleanState() {
        // Arrange & Act
        var (dict, ws) = CreateCleanDurableDict(
            (1, (object?)100L),
            (2, (object?)200L),
            (3, (object?)null)
        );

        // Assert
        dict.ObjectId.Should().BeGreaterThan(0UL);
        dict.State.Should().Be(DurableObjectState.Clean);
        dict.HasChanges.Should().BeFalse();
        dict.Count.Should().Be(3);
    }

    // NOTE: Constructor_NullCommitted_ThrowsArgumentNullException 测试已移除
    // 原因：DurableDict 现在只能通过 Workspace.CreateObject 创建，不再直接接受 committed 参数

    #endregion

    #region Set/Get 基础测试

    /// <summary>
    /// Set 后 Get 返回相同值。
    /// </summary>
    [Fact]
    public void Set_ThenGet_ReturnsSameValue() {
        // Arrange
        var (dict, ws) = CreateDurableDict();

        // Act
        dict.Set(10, "hello");

        // Assert
        dict.TryGetValue(10, out var value).Should().BeTrue();
        value.Should().Be("hello");
        dict[10].Should().Be("hello");
    }

    /// <summary>
    /// Set null 值可正常存取。
    /// </summary>
    [Fact]
    public void Set_NullValue_CanBeRetrieved() {
        // Arrange
        var (dict, ws) = CreateDurableDict();

        // Act
        dict.Set(10, null);

        // Assert
        dict.TryGetValue(10, out var value).Should().BeTrue();
        value.Should().BeNull();
        dict.ContainsKey(10).Should().BeTrue();
    }

    /// <summary>
    /// 多次 Set 覆盖之前的值。
    /// </summary>
    [Fact]
    public void Set_MultipleTimes_OverwritesPreviousValue() {
        // Arrange
        var (dict, ws) = CreateDurableDict();

        // Act
        dict.Set(10, 100);
        dict.Set(10, 200);
        dict.Set(10, 300);

        // Assert
        dict[10].Should().Be(300);
    }

    /// <summary>
    /// 使用索引器设置值。
    /// </summary>
    [Fact]
    public void Indexer_Set_WorksLikeSetMethod() {
        // Arrange
        var (dict, ws) = CreateDurableDict();

        // Act
        dict[10] = 42;

        // Assert
        dict.TryGetValue(10, out var value).Should().BeTrue();
        value.Should().Be(42);
    }

    /// <summary>
    /// 获取不存在的键抛 KeyNotFoundException。
    /// </summary>
    [Fact]
    public void Indexer_Get_NonExistentKey_ThrowsKeyNotFoundException() {
        // Arrange
        var (dict, ws) = CreateDurableDict();

        // Act
        Action act = () => _ = dict[999];

        // Assert
        act.Should().Throw<KeyNotFoundException>();
    }

    /// <summary>
    /// TryGetValue 对不存在的键返回 false。
    /// </summary>
    [Fact]
    public void TryGetValue_NonExistentKey_ReturnsFalse() {
        // Arrange
        var (dict, ws) = CreateDurableDict();

        // Act & Assert
        dict.TryGetValue(999, out var value).Should().BeFalse();
        value.Should().Be(default);
    }

    #endregion

    #region Remove 测试

    /// <summary>
    /// Remove 后 ContainsKey 返回 false。
    /// </summary>
    [Fact]
    public void Remove_ThenContainsKey_ReturnsFalse() {
        // Arrange
        var (dict, ws) = CreateDurableDict();
        dict.Set(10, "hello");

        // Act
        var removed = dict.Remove(10);

        // Assert
        removed.Should().BeTrue();
        dict.ContainsKey(10).Should().BeFalse();
        dict.TryGetValue(10, out _).Should().BeFalse();
    }

    /// <summary>
    /// Remove 不存在的键返回 false。
    /// </summary>
    [Fact]
    public void Remove_NonExistentKey_ReturnsFalse() {
        // Arrange
        var (dict, ws) = CreateDurableDict();

        // Act
        var removed = dict.Remove(999);

        // Assert
        removed.Should().BeFalse();
    }

    /// <summary>
    /// Remove 只存在于 _committed 的键返回 true。
    /// </summary>
    [Fact]
    public void Remove_KeyOnlyInCommitted_ReturnsTrue() {
        // Arrange
        var (dict, ws) = CreateCleanDurableDict((10, (object?)100));

        // Act
        var removed = dict.Remove(10);

        // Assert
        removed.Should().BeTrue();
        dict.ContainsKey(10).Should().BeFalse();
    }

    /// <summary>
    /// [S-WORKING-STATE-TOMBSTONE-FREE] Remove 后枚举不含该 key。
    /// </summary>
    [Fact]
    public void Remove_KeyNotInEnumeration() {
        // Arrange
        var (dict, ws) = CreateDurableDict();
        dict.Set(1, 100);
        dict.Set(2, 200);
        dict.Set(3, 300);

        // Act
        dict.Remove(2);

        // Assert
        dict.Keys.Should().BeEquivalentTo(new ulong[] { 1, 3 });
        dict.Entries.Select(e => e.Key).Should().BeEquivalentTo(new ulong[] { 1, 3 });
    }

    #endregion

    #region Working State 查询优先测试

    /// <summary>
    /// Set 后立即可读（Working State 优先）。
    /// </summary>
    [Fact]
    public void WorkingState_TakesPrecedence_OverCommitted() {
        // Arrange
        var (dict, ws) = CreateCleanDurableDict((10, (object?)100));

        // Act
        dict.Set(10, 999);

        // Assert - _current 的值覆盖 _committed
        dict[10].Should().Be(999);
    }

    /// <summary>
    /// 未修改的键从 _committed 读取。
    /// </summary>
    [Fact]
    public void UnmodifiedKey_ReadsFromCommitted() {
        // Arrange
        var (dict, ws) = CreateCleanDurableDict(
            (1, (object?)100L),
            (2, (object?)200L)
        );

        // Act - 只修改 key=1
        dict.Set(1, 111L);

        // Assert
        dict[1].Should().Be(111L);  // 从 _current
        dict[2].Should().Be(200L);  // 从 _current（未修改）
    }

    /// <summary>
    /// Working 新增的键被正确枚举。
    /// </summary>
    [Fact]
    public void NewKeyInWorking_IncludedInEnumeration() {
        // Arrange
        var (dict, ws) = CreateCleanDurableDict((1, (object?)100));

        // Act
        dict.Set(2, 200);
        dict.Set(3, 300);

        // Assert
        dict.Count.Should().Be(3);
        dict.Keys.Should().BeEquivalentTo(new ulong[] { 1, 2, 3 });
    }

    #endregion
}
