using Atelia.StateJournal;
using FluentAssertions;
using Xunit;

namespace Atelia.StateJournal.Tests.Core;

/// <summary>
/// DurableObjectState 枚举测试。
/// </summary>
/// <remarks>
/// 对应条款：<c>[A-OBJECT-STATE-CLOSED-SET]</c>
/// </remarks>
public class DurableObjectStateTests {
    /// <summary>
    /// 测试枚举包含且仅包含 4 个值。
    /// </summary>
    [Fact]
    public void DurableObjectState_HasExactlyFourValues() {
        var values = Enum.GetValues<DurableObjectState>();

        values.Should().HaveCount(4);
    }

    /// <summary>
    /// 测试 Clean 状态存在且值为 0。
    /// </summary>
    [Fact]
    public void Clean_HasValue0() {
        ((int)DurableObjectState.Clean).Should().Be(0);
    }

    /// <summary>
    /// 测试 PersistentDirty 状态存在且值为 1。
    /// </summary>
    [Fact]
    public void PersistentDirty_HasValue1() {
        ((int)DurableObjectState.PersistentDirty).Should().Be(1);
    }

    /// <summary>
    /// 测试 TransientDirty 状态存在且值为 2。
    /// </summary>
    [Fact]
    public void TransientDirty_HasValue2() {
        ((int)DurableObjectState.TransientDirty).Should().Be(2);
    }

    /// <summary>
    /// 测试 Detached 状态存在且值为 3。
    /// </summary>
    [Fact]
    public void Detached_HasValue3() {
        ((int)DurableObjectState.Detached).Should().Be(3);
    }

    /// <summary>
    /// 测试所有枚举值可被解析为字符串。
    /// </summary>
    [Theory]
    [InlineData(DurableObjectState.Clean, "Clean")]
    [InlineData(DurableObjectState.PersistentDirty, "PersistentDirty")]
    [InlineData(DurableObjectState.TransientDirty, "TransientDirty")]
    [InlineData(DurableObjectState.Detached, "Detached")]
    public void AllValues_HaveCorrectNames(DurableObjectState state, string expectedName) {
        state.ToString().Should().Be(expectedName);
    }

    /// <summary>
    /// 测试枚举值可以从整数转换。
    /// </summary>
    [Theory]
    [InlineData(0, DurableObjectState.Clean)]
    [InlineData(1, DurableObjectState.PersistentDirty)]
    [InlineData(2, DurableObjectState.TransientDirty)]
    [InlineData(3, DurableObjectState.Detached)]
    public void IntegerConversion_Works(int value, DurableObjectState expectedState) {
        var state = (DurableObjectState)value;
        state.Should().Be(expectedState);
    }
}
