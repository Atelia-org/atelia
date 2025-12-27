using Atelia.StateJournal;
using FluentAssertions;
using Xunit;
using static Atelia.StateJournal.Tests.TestHelper;

namespace Atelia.StateJournal.Tests.Objects;

/// <summary>
/// DurableDict 状态转换测试。
/// </summary>
public class DurableDictStateTests {

    /// <summary>
    /// Clean 状态修改后变为 PersistentDirty。
    /// </summary>
    [Fact]
    public void Clean_AfterSet_BecomesPersistentDirty() {
        // Arrange
        var (dict, ws) = CreateCleanDurableDict((1, (object?)100));
        dict.State.Should().Be(DurableObjectState.Clean);

        // Act
        dict.Set(1, 999);

        // Assert
        dict.State.Should().Be(DurableObjectState.PersistentDirty);
        dict.HasChanges.Should().BeTrue();
    }

    /// <summary>
    /// TransientDirty 状态修改后保持 TransientDirty。
    /// </summary>
    [Fact]
    public void TransientDirty_AfterSet_RemainsTransientDirty() {
        // Arrange
        var (dict, ws) = CreateDurableDict();
        dict.State.Should().Be(DurableObjectState.TransientDirty);

        // Act
        dict.Set(1, 100);

        // Assert
        dict.State.Should().Be(DurableObjectState.TransientDirty);
        dict.HasChanges.Should().BeTrue();
    }

    /// <summary>
    /// HasChanges 反映 _dirtyKeys 状态。
    /// </summary>
    [Fact]
    public void HasChanges_ReflectsDirtyKeys() {
        // Arrange
        var (dict, ws) = CreateDurableDict();
        dict.HasChanges.Should().BeFalse();

        // Act & Assert
        dict.Set(1, 100);
        dict.HasChanges.Should().BeTrue();

        dict.Set(2, 200);
        dict.HasChanges.Should().BeTrue();
    }

    /// <summary>
    /// Clean 状态 Remove 后变为 PersistentDirty。
    /// </summary>
    [Fact]
    public void Clean_AfterRemove_BecomesPersistentDirty() {
        // Arrange
        var (dict, ws) = CreateCleanDurableDict((1, (object?)100));

        // Act
        dict.Remove(1);

        // Assert
        dict.State.Should().Be(DurableObjectState.PersistentDirty);
        dict.HasChanges.Should().BeTrue();
    }
}
