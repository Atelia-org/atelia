using Atelia.StateJournal;
using FluentAssertions;
using Xunit;
using static Atelia.StateJournal.Tests.TestHelper;

namespace Atelia.StateJournal.Tests.Objects;

/// <summary>
/// DurableDict Detached 状态测试。
/// </summary>
public class DurableDictDetachedTests {

    /// <summary>
    /// 创建一个 Detached 状态的 DurableDict（通过反射设置状态）。
    /// </summary>
    private static DurableDict CreateDetachedDict<TValue>() {
        var (dict, _) = CreateDurableDict();
        // 使用反射设置基类 DurableObjectBase 中的 _state 为 Detached
        var stateField = typeof(DurableObjectBase).GetField(
            "_state",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
        );
        stateField!.SetValue(dict, DurableObjectState.Detached);
        return dict;
    }

    /// <summary>
    /// Detached 状态下 TryGetValue 抛 ObjectDetachedException。
    /// </summary>
    [Fact]
    public void Detached_TryGetValue_ThrowsObjectDetachedException() {
        // Arrange
        var dict = CreateDetachedDict<int>();

        // Act
        Action act = () => dict.TryGetValue(1, out _);

        // Assert
        act.Should().Throw<ObjectDetachedException>()
            .Which.ObjectId.Should().BeGreaterThan(0UL);
    }

    /// <summary>
    /// Detached 状态下 ContainsKey 抛 ObjectDetachedException。
    /// </summary>
    [Fact]
    public void Detached_ContainsKey_ThrowsObjectDetachedException() {
        // Arrange
        var dict = CreateDetachedDict<int>();

        // Act
        Action act = () => dict.ContainsKey(1);

        // Assert
        act.Should().Throw<ObjectDetachedException>();
    }

    /// <summary>
    /// Detached 状态下 Indexer Get 抛 ObjectDetachedException。
    /// </summary>
    [Fact]
    public void Detached_IndexerGet_ThrowsObjectDetachedException() {
        // Arrange
        var dict = CreateDetachedDict<int>();

        // Act
        Action act = () => _ = dict[1];

        // Assert
        act.Should().Throw<ObjectDetachedException>();
    }

    /// <summary>
    /// Detached 状态下 Indexer Set 抛 ObjectDetachedException。
    /// </summary>
    [Fact]
    public void Detached_IndexerSet_ThrowsObjectDetachedException() {
        // Arrange
        var dict = CreateDetachedDict<int>();

        // Act
        Action act = () => dict[1] = 100;

        // Assert
        act.Should().Throw<ObjectDetachedException>();
    }

    /// <summary>
    /// Detached 状态下 Set 抛 ObjectDetachedException。
    /// </summary>
    [Fact]
    public void Detached_Set_ThrowsObjectDetachedException() {
        // Arrange
        var dict = CreateDetachedDict<int>();

        // Act
        Action act = () => dict.Set(1, 100);

        // Assert
        act.Should().Throw<ObjectDetachedException>();
    }

    /// <summary>
    /// Detached 状态下 Remove 抛 ObjectDetachedException。
    /// </summary>
    [Fact]
    public void Detached_Remove_ThrowsObjectDetachedException() {
        // Arrange
        var dict = CreateDetachedDict<int>();

        // Act
        Action act = () => dict.Remove(1);

        // Assert
        act.Should().Throw<ObjectDetachedException>();
    }

    /// <summary>
    /// Detached 状态下 Count 抛 ObjectDetachedException。
    /// </summary>
    [Fact]
    public void Detached_Count_ThrowsObjectDetachedException() {
        // Arrange
        var dict = CreateDetachedDict<int>();

        // Act
        Action act = () => _ = dict.Count;

        // Assert
        act.Should().Throw<ObjectDetachedException>();
    }

    /// <summary>
    /// Detached 状态下 Keys 抛 ObjectDetachedException。
    /// </summary>
    [Fact]
    public void Detached_Keys_ThrowsObjectDetachedException() {
        // Arrange
        var dict = CreateDetachedDict<int>();

        // Act
        Action act = () => _ = dict.Keys;

        // Assert
        act.Should().Throw<ObjectDetachedException>();
    }

    /// <summary>
    /// Detached 状态下 Entries 抛 ObjectDetachedException。
    /// </summary>
    [Fact]
    public void Detached_Entries_ThrowsObjectDetachedException() {
        // Arrange
        var dict = CreateDetachedDict<int>();

        // Act
        Action act = () => _ = dict.Entries;

        // Assert
        act.Should().Throw<ObjectDetachedException>();
    }

    /// <summary>
    /// Detached 状态下 State 属性仍可读取（不抛异常）。
    /// </summary>
    [Fact]
    public void Detached_State_CanBeRead() {
        // Arrange
        var dict = CreateDetachedDict<int>();

        // Act & Assert - State 读取不应抛异常
        dict.State.Should().Be(DurableObjectState.Detached);
    }

    /// <summary>
    /// Detached 状态下 HasChanges 抛 ObjectDetachedException。
    /// </summary>
    /// <remarks>
    /// 对应条款：[S-DETACHED-ACCESS-TIERING] - HasChanges 是语义成员，Detached 时 MUST throw。
    /// 畅谈会 #3 决议：规则优先级 R2 (Fail-Fast) &gt; R1 (属性惯例)。
    /// </remarks>
    [Fact]
    public void Detached_HasChanges_ThrowsObjectDetachedException() {
        // Arrange
        var dict = CreateDetachedDict<int>();

        // Act
        Action act = () => _ = dict.HasChanges;

        // Assert
        act.Should().Throw<ObjectDetachedException>();
    }
}
