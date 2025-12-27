using System.Buffers;
using Atelia.StateJournal;
using FluentAssertions;
using Xunit;
using Ws = Atelia.StateJournal.Workspace;
using static Atelia.StateJournal.Tests.TestHelper;

namespace Atelia.StateJournal.Tests.Objects;

/// <summary>
/// DurableDict 复杂场景和杂项测试。
/// </summary>
/// <remarks>
/// 综合测试混合操作、不同数据类型等场景。
/// 基础功能测试已拆分到其他专项测试文件。
/// </remarks>
public class DurableDictTests {

    #region 复杂场景测试

    /// <summary>
    /// 混合 Set/Remove 操作。
    /// </summary>
    [Fact]
    public void MixedOperations_SetAndRemove() {
        // Arrange
        var (dict, ws) = CreateCleanDurableDict((1, (object?)100), (2, (object?)200), (3, (object?)300));

        // Act
        dict.Set(1, 999);      // 覆盖
        dict.Remove(2);        // 删除 _committed 中的
        dict.Set(4, 400);      // 新增
        dict.Set(5, 500);      // 新增
        dict.Remove(5);        // 删除刚新增的

        // Assert
        dict.Count.Should().Be(3); // 1, 3, 4
        dict.Keys.Should().BeEquivalentTo(new ulong[] { 1, 3, 4 });
        dict[1].Should().Be(999);
        dict[3].Should().Be(300);
        dict[4].Should().Be(400);
        dict.ContainsKey(2).Should().BeFalse();
        dict.ContainsKey(5).Should().BeFalse();
    }

    /// <summary>
    /// Entries 枚举返回 _current 的内容。
    /// </summary>
    [Fact]
    public void Entries_MergesCommittedAndWorking() {
        // Arrange
        var (dict, ws) = CreateCleanDurableDict((1, (object?)100L), (2, (object?)200L));

        // Act
        dict.Set(1, 111L);    // 覆盖
        dict.Set(3, 300L);  // 新增

        // Assert
        var entries = dict.Entries.ToDictionary(e => e.Key, e => e.Value);
        entries.Should().HaveCount(3);
        entries[1].Should().Be(111L);
        entries[2].Should().Be(200L);
        entries[3].Should().Be(300L);
    }

    /// <summary>
    /// 空 Committed + 空 Working = 空字典。
    /// </summary>
    [Fact]
    public void EmptyDict_CountIsZero() {
        // Arrange
        var (dict, ws) = CreateDurableDict();

        // Assert
        dict.Count.Should().Be(0);
        dict.Keys.Should().BeEmpty();
        dict.Entries.Should().BeEmpty();
    }

    /// <summary>
    /// 值类型测试（int）。
    /// </summary>
    [Fact]
    public void ValueType_Int_Works() {
        var (dict, ws) = CreateDurableDict();
        dict.Set(1, 42);
        dict[1].Should().Be(42);
    }

    /// <summary>
    /// 引用类型测试（object）。
    /// </summary>
    [Fact]
    public void ReferenceType_Object_Works() {
        var (dict, ws) = CreateDurableDict();
        var obj = new object();
        dict.Set(1, obj);
        dict[1].Should().BeSameAs(obj);
    }

    /// <summary>
    /// 可空值类型测试（int?）。
    /// </summary>
    [Fact]
    public void NullableValueType_Works() {
        var (dict, ws) = CreateDurableDict();
        dict.Set(1, 42);
        dict.Set(2, null);

        dict[1].Should().Be(42);
        dict[2].Should().BeNull();
        dict.ContainsKey(2).Should().BeTrue();
    }

    #endregion

    #region ObjectDetachedException 测试

    /// <summary>
    /// ObjectDetachedException 包含正确的 ObjectId。
    /// </summary>
    [Fact]
    public void ObjectDetachedException_ContainsObjectId() {
        // Arrange & Act
        var ex = new ObjectDetachedException(12345UL);

        // Assert
        ex.ObjectId.Should().Be(12345UL);
        ex.Message.Should().Contain("12345");
        ex.Message.Should().Contain("detached");
    }

    #endregion
}
