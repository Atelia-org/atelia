using Atelia.StateJournal.Internal;
using Xunit;

namespace Atelia.StateJournal.Tests;

/// <summary>
/// DictChangeTracker.FreezeFromClean 的防御性冻结验证。
/// 这些是 white-box 测试，直接操作 internal 类型以确保 FreezeFromClean 履行其冻结契约。
/// </summary>
public class DictChangeTrackerFreezeTests {
    [Fact]
    public void FreezeFromClean_DefensivelyFreezesHeapValueBoxSlots() {
        var tracker = new DictChangeTracker<byte, ValueBox>();
        const byte key = 1;

        // 通过 StringPayloadFace 创建 heap Exclusive ValueBox。
        // EncodeHeapSlot 总是设置 ExclusiveBit，因此该 ValueBox 初始为 Exclusive。
        ValueBox exclusiveValue = ValueBox.StringPayloadFace.From("test");
        // 预先计算 frozen 版本的 bits 作为期望值。
        ValueBox frozenValue = ValueBox.Freeze(exclusiveValue);

        // 初始状态下，Exclusive 版本与 Frozen 版本的 bits 应不同（Exclusive bit 差异）。
        Assert.NotEqual(exclusiveValue.GetBits(), frozenValue.GetBits());

        // 将 Exclusive ValueBox 直接放入 _current，绕过正常公共 API 的冻结点。
        tracker.Current[key] = exclusiveValue;

        // Act：调用 FreezeFromClean。修复后应防御性冻结 _current 中的所有 ValueBox。
        tracker.FreezeFromClean<ValueBoxHelper>();

        // Assert：_current 中的 ValueBox 应与 frozen 版本 bits 完全一致。
        Assert.Equal(frozenValue.GetBits(), tracker.Current[key].GetBits());
    }
}
