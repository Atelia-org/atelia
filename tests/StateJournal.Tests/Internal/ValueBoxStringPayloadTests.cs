using System.Buffers;
using Xunit;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal.Tests;

// ai:impl `src/StateJournal/Internal/ValueBox.String.cs` (StringPayloadFace)
/// <summary>
/// <see cref="ValueBox.StringPayloadFace"/> + payload string 生命周期测试。
/// 核心契约：
/// - 不去重：两次 From("x") 得不同 SlotHandle 但 ValueEquals == true；
/// - StringPayload vs Symbol（intern）：即使内容相同也 ValueEquals == false；
/// - Update 释放旧 owned heap slot（包括 bits64 数值）；
/// - Frozen StringPayload fork → 深 clone 新 slot；
/// - Wire round-trip 经 dispatcher 正常工作；
/// - 不污染 SymbolPool（不进 intern 池）。
/// </summary>
[Collection("ValueBox")]
public class ValueBoxStringPayloadTests {
    private static int OwnedStringCount => ValuePools.OfOwnedString.Count;
    private static int Bits64Count => ValuePools.OfBits64.Count;

    [Fact]
    public void StringPayload_RoundTrip_PreservesContent() {
        int before = OwnedStringCount;
        var box = ValueBox.StringPayloadFace.From("hello");
        Assert.Equal(before + 1, OwnedStringCount);
        Assert.Equal(ValueKind.String, box.GetValueKind());
        Assert.True(box.IsStringPayloadRef);

        Assert.Equal(GetIssue.None, ValueBox.StringPayloadFace.Get(box, out string? value));
        Assert.Equal("hello", value);

        ValueBox.ReleaseOwnedHeapSlot(box);
        Assert.Equal(before, OwnedStringCount);
    }

    [Fact]
    public void StringPayload_Empty_RoundTrip() {
        var box = ValueBox.StringPayloadFace.From("");
        try {
            Assert.Equal(ValueKind.String, box.GetValueKind());
            Assert.Equal(GetIssue.None, ValueBox.StringPayloadFace.Get(box, out string? value));
            Assert.Equal("", value);
        }
        finally { ValueBox.ReleaseOwnedHeapSlot(box); }
    }

    [Fact]
    public void StringPayload_Null_ReturnsNullBox() {
        int before = OwnedStringCount;
        var box = ValueBox.StringPayloadFace.From(null);
        Assert.True(box.IsNull);
        Assert.Equal(before, OwnedStringCount);
        Assert.Equal(GetIssue.None, ValueBox.StringPayloadFace.Get(box, out string? v));
        Assert.Null(v);
    }

    [Fact]
    public void StringPayload_SameContent_DifferentSlots_ButValueEquals() {
        var a = ValueBox.StringPayloadFace.From("hello");
        var b = ValueBox.StringPayloadFace.From("hello");
        try {
            Assert.NotEqual(a.GetBits(), b.GetBits()); // 不同 slot
            Assert.True(ValueBox.ValueEquals(a, b));   // 内容相等
            Assert.Equal(ValueBox.ValueHashCode(a), ValueBox.ValueHashCode(b));
        }
        finally {
            ValueBox.ReleaseOwnedHeapSlot(a);
            ValueBox.ReleaseOwnedHeapSlot(b);
        }
    }

    [Fact]
    public void StringPayload_DifferentContent_NotEqual() {
        var a = ValueBox.StringPayloadFace.From("foo");
        var b = ValueBox.StringPayloadFace.From("bar");
        try {
            Assert.False(ValueBox.ValueEquals(a, b));
        }
        finally {
            ValueBox.ReleaseOwnedHeapSlot(a);
            ValueBox.ReleaseOwnedHeapSlot(b);
        }
    }

    [Fact]
    public void StringPayload_VsSymbol_NotEqual_EvenIfSameContent() {
        // payload string 不进 intern 池；与同内容 Symbol 不同 ValueKind → 不等。
        var payload = ValueBox.StringPayloadFace.From("alpha");
        var symbolBox = ValueBox.FromSymbolId(new SymbolId(123)); // 任意非 null id；位上不会等于 payload box
        try {
            Assert.False(ValueBox.ValueEquals(payload, symbolBox));
            Assert.NotEqual(ValueKind.Symbol, payload.GetValueKind());
            Assert.Equal(ValueKind.String, payload.GetValueKind());
            Assert.Equal(ValueKind.Symbol, symbolBox.GetValueKind());
        }
        finally { ValueBox.ReleaseOwnedHeapSlot(payload); }
    }

    [Fact]
    public void StringPayload_Update_ReleasesOldSlot() {
        int before = OwnedStringCount;
        var box = ValueBox.StringPayloadFace.From("first");
        Assert.Equal(before + 1, OwnedStringCount);

        // 同 exclusive slot 内容覆写：count 不变（in-place）
        bool changed = ValueBox.StringPayloadFace.UpdateOrInit(ref box, "second", out _);
        Assert.True(changed);
        Assert.Equal(before + 1, OwnedStringCount);
        Assert.Equal(GetIssue.None, ValueBox.StringPayloadFace.Get(box, out string? v));
        Assert.Equal("second", v);

        // 同内容：no-op，count 不变
        bool changed2 = ValueBox.StringPayloadFace.UpdateOrInit(ref box, "second", out _);
        Assert.False(changed2);
        Assert.Equal(before + 1, OwnedStringCount);

        ValueBox.ReleaseOwnedHeapSlot(box);
        Assert.Equal(before, OwnedStringCount);
    }

    [Fact]
    public void StringPayload_Update_FromBits64_ReleasesBits64Slot() {
        int beforeBits64 = Bits64Count;
        int beforeOwned = OwnedStringCount;
        // 先构造一个堆 bits64 整数 box
        var box = ValueBox.Int64Face.From((long)LzcConstants.NonnegIntInlineCap); // 溢出 inline，进堆
        Assert.Equal(beforeBits64 + 1, Bits64Count);

        bool changed = ValueBox.StringPayloadFace.UpdateOrInit(ref box, "switched", out _);
        Assert.True(changed);
        Assert.Equal(beforeBits64, Bits64Count); // bits64 slot 被释放
        Assert.Equal(beforeOwned + 1, OwnedStringCount); // 新 owned string slot

        ValueBox.ReleaseOwnedHeapSlot(box);
        Assert.Equal(beforeOwned, OwnedStringCount);
    }

    [Fact]
    public void StringPayload_Update_ToHeapBits64_ReleasesOldStringSlot() {
        int beforeBits64 = Bits64Count;
        int beforeOwned = OwnedStringCount;
        var box = ValueBox.StringPayloadFace.From("old string");
        Assert.Equal(beforeOwned + 1, OwnedStringCount);

        bool changed = ValueBox.UInt64Face.UpdateOrInit(ref box, LzcConstants.NonnegIntInlineCap, out _);
        Assert.True(changed);
        Assert.Equal(beforeOwned, OwnedStringCount);
        Assert.Equal(beforeBits64 + 1, Bits64Count);

        ValueBox.ReleaseOwnedHeapSlot(box);
        Assert.Equal(beforeBits64, Bits64Count);
    }

    [Fact]
    public void StringPayload_Update_FromNull_AllocatesNew() {
        int before = OwnedStringCount;
        var box = ValueBox.Null;
        bool changed = ValueBox.StringPayloadFace.UpdateOrInit(ref box, "seed", out _);
        Assert.True(changed);
        Assert.Equal(before + 1, OwnedStringCount);
        Assert.Equal(ValueKind.String, box.GetValueKind());

        // Update 到 null：释放 slot，box → Null
        bool changed2 = ValueBox.StringPayloadFace.UpdateOrInit(ref box, null, out _);
        Assert.True(changed2);
        Assert.True(box.IsNull);
        Assert.Equal(before, OwnedStringCount);
    }

    [Fact]
    public void StringPayload_Freeze_ClearsExclusiveBit() {
        var box = ValueBox.StringPayloadFace.From("frozen-me");
        try {
            var frozen = ValueBox.Freeze(box);
            // bits 应该只差一个 ExclusiveBit
            Assert.NotEqual(box.GetBits(), frozen.GetBits());
            Assert.True(ValueBox.ValueEquals(box, frozen));
            Assert.Equal(ValueKind.String, frozen.GetValueKind());
        }
        finally { ValueBox.ReleaseOwnedHeapSlot(box); }
    }

    [Fact]
    public void StringPayload_FrozenFork_DeepClonesIntoNewSlot() {
        var owner1 = ValueBox.StringPayloadFace.From("shared");
        try {
            int beforeFork = OwnedStringCount;
            var frozen = ValueBox.Freeze(owner1);

            var owner2 = ValueBox.CloneFrozenForNewOwner(frozen);
            Assert.Equal(beforeFork + 1, OwnedStringCount); // 深 clone：新 slot
            Assert.True(ValueBox.ValueEquals(frozen, owner2));
            Assert.NotEqual(frozen.GetBits(), owner2.GetBits()); // 不同 handle
            Assert.Equal(ValueKind.String, owner2.GetValueKind());

            // owner2 是 frozen 状态（CloneFrozenForNewOwner 返回 frozen）
            ValueBox.ReleaseOwnedHeapSlot(owner2);
            Assert.Equal(beforeFork, OwnedStringCount);
        }
        finally { ValueBox.ReleaseOwnedHeapSlot(owner1); }
    }

    [Fact]
    public void StringPayload_Wire_RoundTrip_ViaDispatcher() {
        var src = ValueBox.StringPayloadFace.From("wire-trip 中文 🌟");
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(buffer);
        try {
            src.Write(writer);
            byte[] bytes = buffer.WrittenSpan.ToArray();
            Assert.Equal(ScalarRules.StringPayload.Tag, bytes[0]);

            ValueBox dst = ValueBox.Null;
            var reader = new BinaryDiffReader(bytes);
            bool changed = TaggedValueDispatcher.UpdateOrInit(ref reader, ref dst);
            try {
                Assert.True(changed);
                Assert.True(ValueBox.ValueEquals(src, dst));
                Assert.Equal(GetIssue.None, ValueBox.StringPayloadFace.Get(dst, out string? v));
                Assert.Equal("wire-trip 中文 🌟", v);
            }
            finally { ValueBox.ReleaseOwnedHeapSlot(dst); }
        }
        finally { ValueBox.ReleaseOwnedHeapSlot(src); }
    }

    [Fact]
    public void StringPayload_Dispatcher_UpdateToSymbol_ReleasesOldSlot() {
        int before = OwnedStringCount;
        ValueBox box = ValueBox.StringPayloadFace.From("old payload");
        Assert.Equal(before + 1, OwnedStringCount);

        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(buffer);
        writer.TaggedSymbolId(new SymbolId(7));

        var reader = new BinaryDiffReader(buffer.WrittenSpan);
        bool changed = TaggedValueDispatcher.UpdateOrInit(ref reader, ref box);

        Assert.True(changed);
        Assert.Equal(before, OwnedStringCount);
        Assert.True(box.IsSymbolRef);
        Assert.Equal(GetIssue.None, ValueBox.GetSymbolId(box, out SymbolId id));
        Assert.Equal(7u, id.Value);
    }

    [Fact]
    public void StringPayload_Get_TypeMismatch_OnNonStringBox() {
        var intBox = ValueBox.Int64Face.From(42);
        Assert.Equal(GetIssue.TypeMismatch, ValueBox.StringPayloadFace.Get(intBox, out string? v));
        Assert.Null(v);
    }

    [Fact]
    public void StringPayload_DoesNotPolluteSymbolPool() {
        // 结构断言：payload string box 既不是 SymbolRef，也不映射到 ValueKind.Symbol。
        // 这就保证 mixed 容器的 _symbolRefCount 路径（仅识别 IsSymbolRef）不会把它算进去，
        // 从而 commit-time mark-sweep 不会把它带入 SymbolPool。
        var box = ValueBox.StringPayloadFace.From("payload-only");
        try {
            Assert.False(box.IsSymbolRef);
            Assert.True(box.IsStringPayloadRef);
            Assert.Equal(ValueKind.String, box.GetValueKind());
            Assert.NotEqual(ValueKind.Symbol, box.GetValueKind());
        }
        finally { ValueBox.ReleaseOwnedHeapSlot(box); }
    }
}
