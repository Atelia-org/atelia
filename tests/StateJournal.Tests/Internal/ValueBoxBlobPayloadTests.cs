using System.Buffers;
using Xunit;
using Atelia.StateJournal.Pools;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal.Tests;

// ai:impl `src/StateJournal/Internal/ValueBox.Blob.cs` (BlobPayloadFace)
/// <summary>
/// <see cref="ValueBox.BlobPayloadFace"/> + payload blob 生命周期测试。
/// 完全镜像 <c>ValueBoxStringPayloadTests</c>，验证：
/// - 不去重：两次 From 同内容得不同 SlotHandle 但 ValueEquals == true；
/// - Equality 走 SequenceEqual 慢路径；
/// - UpdateOrInit 五条路径（新建 / no-op / inplace / cross-kind / freeze-fork 后再写）；
/// - EstimateBareSize == 1 + VarUInt(len) + len（完全 exact）；
/// - Freeze + Fork 行为；
/// - Wire round-trip via dispatcher；
/// - 大 blob 端到端不出错。
/// </summary>
[Collection("ValueBox")]
public class ValueBoxBlobPayloadTests {
    private static int OwnedBlobCount => ValuePools.OfOwnedBlob.Count;
    private static int OwnedStringCount => ValuePools.OfOwnedString.Count;
    private static int Bits64Count => ValuePools.OfBits64.Count;

    private static ByteString Bs(params byte[] data) => new(data);
    private static uint BlobTaggedSize(int length) => checked(1u + CostEstimateUtil.VarIntSize((uint)length) + (uint)length);

    // ─────────────────── 基础 From / Get / Equals ───────────────────

    [Fact]
    public void BlobPayload_RoundTrip_PreservesContent() {
        int before = OwnedBlobCount;
        var box = ValueBox.BlobPayloadFace.From(Bs(1, 2, 3, 4));
        Assert.Equal(before + 1, OwnedBlobCount);
        Assert.Equal(ValueKind.Blob, box.GetValueKind());
        Assert.True(box.IsBlobPayloadRef);

        Assert.Equal(GetIssue.None, ValueBox.BlobPayloadFace.Get(box, out ByteString value));
        Assert.Equal(Bs(1, 2, 3, 4), value);

        ValueBox.ReleaseOwnedHeapSlot(box);
        Assert.Equal(before, OwnedBlobCount);
    }

    [Fact]
    public void BlobPayload_Empty_RoundTrip() {
        int before = OwnedBlobCount;
        var box = ValueBox.BlobPayloadFace.From(ByteString.Empty);
        try {
            // 空 blob 也走 pool（与 StringPayloadFace 处理空 string 一致）。
            Assert.Equal(before + 1, OwnedBlobCount);
            Assert.Equal(ValueKind.Blob, box.GetValueKind());
            Assert.Equal(GetIssue.None, ValueBox.BlobPayloadFace.Get(box, out ByteString value));
            Assert.True(value.IsEmpty);
        }
        finally { ValueBox.ReleaseOwnedHeapSlot(box); }
    }

    [Fact]
    public void BlobPayload_Default_BehavesAsEmpty() {
        // ByteString default == Empty；From(default) 应正常分配 slot。
        var box = ValueBox.BlobPayloadFace.From(default);
        try {
            Assert.Equal(GetIssue.None, ValueBox.BlobPayloadFace.Get(box, out ByteString v));
            Assert.True(v.IsEmpty);
        }
        finally { ValueBox.ReleaseOwnedHeapSlot(box); }
    }

    [Fact]
    public void BlobPayload_SameContent_DifferentSlots_ButValueEquals() {
        var a = ValueBox.BlobPayloadFace.From(Bs(0xAA, 0xBB, 0xCC));
        var b = ValueBox.BlobPayloadFace.From(Bs(0xAA, 0xBB, 0xCC));
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
    public void BlobPayload_DifferentContent_NotEqual() {
        var a = ValueBox.BlobPayloadFace.From(Bs(1, 2, 3));
        var b = ValueBox.BlobPayloadFace.From(Bs(1, 2, 4));
        try {
            Assert.False(ValueBox.ValueEquals(a, b));
        }
        finally {
            ValueBox.ReleaseOwnedHeapSlot(a);
            ValueBox.ReleaseOwnedHeapSlot(b);
        }
    }

    [Fact]
    public void BlobPayload_DifferentLength_NotEqual() {
        var a = ValueBox.BlobPayloadFace.From(Bs(1, 2, 3));
        var b = ValueBox.BlobPayloadFace.From(Bs(1, 2, 3, 4));
        try {
            Assert.False(ValueBox.ValueEquals(a, b));
        }
        finally {
            ValueBox.ReleaseOwnedHeapSlot(a);
            ValueBox.ReleaseOwnedHeapSlot(b);
        }
    }

    [Fact]
    public void BlobPayload_VsStringPayload_NotEqual_EvenIfBytesMatch() {
        // payload blob 与 string payload 走不同 ValueKind，即使字节匹配也不等。
        var blob = ValueBox.BlobPayloadFace.From(Bs(0x68, 0x69)); // "hi"
        var str = ValueBox.StringPayloadFace.From("hi");
        try {
            Assert.False(ValueBox.ValueEquals(blob, str));
            Assert.Equal(ValueKind.Blob, blob.GetValueKind());
            Assert.Equal(ValueKind.String, str.GetValueKind());
        }
        finally {
            ValueBox.ReleaseOwnedHeapSlot(blob);
            ValueBox.ReleaseOwnedHeapSlot(str);
        }
    }

    [Fact]
    public void BlobPayload_VsSymbol_NotEqual() {
        var blob = ValueBox.BlobPayloadFace.From(Bs(1, 2, 3));
        var sym = ValueBox.FromSymbolId(new SymbolId(42));
        try {
            Assert.False(ValueBox.ValueEquals(blob, sym));
        }
        finally { ValueBox.ReleaseOwnedHeapSlot(blob); }
    }

    [Fact]
    public void BlobPayload_Get_TypeMismatch_OnNonBlobBox() {
        var intBox = ValueBox.Int64Face.From(42);
        Assert.Equal(GetIssue.TypeMismatch, ValueBox.BlobPayloadFace.Get(intBox, out ByteString v));
        Assert.True(v.IsEmpty);
    }

    [Fact]
    public void BlobPayload_Get_TypeMismatch_OnStringBox() {
        var str = ValueBox.StringPayloadFace.From("hello");
        try {
            Assert.Equal(GetIssue.TypeMismatch, ValueBox.BlobPayloadFace.Get(str, out ByteString v));
            Assert.True(v.IsEmpty);
        }
        finally { ValueBox.ReleaseOwnedHeapSlot(str); }
    }

    [Fact]
    public void BlobPayload_Get_TypeMismatch_OnNullBox() {
        // ByteString 无 null 概念（与 BooleanFace 一致），Null box 视为 TypeMismatch。
        Assert.Equal(GetIssue.TypeMismatch, ValueBox.BlobPayloadFace.Get(ValueBox.Null, out ByteString v));
        Assert.True(v.IsEmpty);
    }

    [Fact]
    public void BlobPayload_From_DefensiveClone_ExternalMutationDoesNotAffectPool() {
        // CMS Step 3b 决策 B（GPT5 review）：BlobPayloadFace.From 入池时 defensive clone byte[]，
        // 即便 ByteString(byte[]) ctor 不 clone（trusts immutable convention），
        // pool 内 byte[] 仍是 face 独占副本，外部 mutate 源数组不应污染 dict / wire。
        // 本测试锁定该契约，防 future 误改回零拷贝。
        byte[] external = [1, 2, 3, 4];
        ByteString src = new(external);
        ValueBox box = ValueBox.BlobPayloadFace.From(src);
        try {
            external[1] = 0xFF; // 外部 mutation
            ValueBox.BlobPayloadFace.Get(box, out ByteString got);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, got.AsSpan().ToArray());
        }
        finally {
            ValueBox.ReleaseOwnedHeapSlot(box);
        }
    }

    [Fact]
    public void BlobPayload_UpdateOrInit_DefensiveClone_ExternalMutationDoesNotAffectPool() {
        // 同上，但走 UpdateOrInit 路径（含 inplace 优化与 cross-kind/new-slot 分支共用的 CloneForPool）。
        byte[] external = [10, 20, 30];
        ValueBox box = ValueBox.BlobPayloadFace.From(Bs(1)); // 先建一个 exclusive blob 占 slot
        try {
            ValueBox.BlobPayloadFace.UpdateOrInit(ref box, new ByteString(external), out _);
            external[0] = 0xFF;
            ValueBox.BlobPayloadFace.Get(box, out ByteString got);
            Assert.Equal(new byte[] { 10, 20, 30 }, got.AsSpan().ToArray());
        }
        finally {
            ValueBox.ReleaseOwnedHeapSlot(box);
        }
    }

    // ─────────────────── UpdateOrInit 五条路径 ───────────────────

    [Fact]
    public void BlobPayload_UpdateOrInit_NewFromUninit_Allocates() {
        int before = OwnedBlobCount;
        ValueBox box = default; // Uninitialized
        bool changed = ValueBox.BlobPayloadFace.UpdateOrInit(ref box, Bs(7, 8, 9), out uint oldBytes);
        try {
            Assert.True(changed);
            Assert.Equal(0u, oldBytes);
            Assert.Equal(before + 1, OwnedBlobCount);
            Assert.Equal(ValueKind.Blob, box.GetValueKind());
        }
        finally { ValueBox.ReleaseOwnedHeapSlot(box); }
    }

    [Fact]
    public void BlobPayload_UpdateOrInit_NoOp_SameContent_ReturnsFalse() {
        var box = ValueBox.BlobPayloadFace.From(Bs(1, 2, 3));
        try {
            int beforeCount = OwnedBlobCount;
            uint expectedBytes = box.IsUninitialized ? 0u : (uint)(1 + 1 + 3); // 1B tag + VarUInt(3) + 3B
            bool changed = ValueBox.BlobPayloadFace.UpdateOrInit(ref box, Bs(1, 2, 3), out uint oldBytes);
            Assert.False(changed);
            Assert.Equal(expectedBytes, oldBytes);
            Assert.Equal(beforeCount, OwnedBlobCount);
        }
        finally { ValueBox.ReleaseOwnedHeapSlot(box); }
    }

    [Fact]
    public void BlobPayload_UpdateOrInit_InplaceOverwrite_ExclusiveSlot_ReusesSlot() {
        int before = OwnedBlobCount;
        var box = ValueBox.BlobPayloadFace.From(Bs(1, 2, 3));
        Assert.Equal(before + 1, OwnedBlobCount);
        ulong oldBits = box.GetBits();

        bool changed = ValueBox.BlobPayloadFace.UpdateOrInit(ref box, Bs(9, 9, 9, 9, 9), out uint oldBytes);
        try {
            Assert.True(changed);
            Assert.Equal((uint)(1 + 1 + 3), oldBytes); // 旧 estimate
            Assert.Equal(before + 1, OwnedBlobCount);  // inplace，slot 数不变
            Assert.Equal(oldBits, box.GetBits());      // bits 不变
            Assert.Equal(GetIssue.None, ValueBox.BlobPayloadFace.Get(box, out ByteString v));
            Assert.Equal(Bs(9, 9, 9, 9, 9), v);
        }
        finally { ValueBox.ReleaseOwnedHeapSlot(box); }
    }

    [Fact]
    public void BlobPayload_UpdateOrInit_OnFrozenSlot_AllocatesNew_DoesNotFreeOld() {
        var owner = ValueBox.BlobPayloadFace.From(Bs(1, 2));
        try {
            // Freeze: 清 ExclusiveBit。模拟 commit 后场景。
            var frozen = ValueBox.Freeze(owner);
            int beforeCount = OwnedBlobCount;

            // 在 frozen box 上 UpdateOrInit：因为 !IsExclusive 不能 inplace，必须分配新 slot；
            // FreeOldOwnedHeapIfNeeded 跳过 frozen → 旧 slot 仍保留（owner 持有）。
            bool changed = ValueBox.BlobPayloadFace.UpdateOrInit(ref frozen, Bs(7, 7), out _);
            try {
                Assert.True(changed);
                Assert.Equal(beforeCount + 1, OwnedBlobCount); // 新分配
                Assert.Equal(GetIssue.None, ValueBox.BlobPayloadFace.Get(owner, out ByteString origOwnerValue));
                Assert.Equal(Bs(1, 2), origOwnerValue); // owner 旧值未受影响
            }
            finally { ValueBox.ReleaseOwnedHeapSlot(frozen); }
        }
        finally { ValueBox.ReleaseOwnedHeapSlot(owner); }
    }

    [Fact]
    public void BlobPayload_UpdateOrInit_CrossKind_FromBits64_ReleasesBits64() {
        int beforeBits64 = Bits64Count;
        int beforeBlob = OwnedBlobCount;
        var box = ValueBox.Int64Face.From((long)LzcConstants.NonnegIntInlineCap); // 进堆
        Assert.Equal(beforeBits64 + 1, Bits64Count);

        bool changed = ValueBox.BlobPayloadFace.UpdateOrInit(ref box, Bs(0xDE, 0xAD, 0xBE, 0xEF), out _);
        try {
            Assert.True(changed);
            Assert.Equal(beforeBits64, Bits64Count);     // bits64 slot 被释放
            Assert.Equal(beforeBlob + 1, OwnedBlobCount); // 新 blob slot
            Assert.Equal(ValueKind.Blob, box.GetValueKind());
        }
        finally { ValueBox.ReleaseOwnedHeapSlot(box); }
    }

    [Fact]
    public void BlobPayload_UpdateOrInit_CrossKind_FromString_ReleasesStringSlot() {
        int beforeStr = OwnedStringCount;
        int beforeBlob = OwnedBlobCount;
        var box = ValueBox.StringPayloadFace.From("payload");
        Assert.Equal(beforeStr + 1, OwnedStringCount);

        bool changed = ValueBox.BlobPayloadFace.UpdateOrInit(ref box, Bs(1, 2, 3), out uint oldBytes);
        try {
            Assert.True(changed);
            Assert.Equal(16u, oldBytes); // "payload" length=7 → 1B tag + VarUInt(14) + 14B UTF-16 upper-bound
            Assert.Equal(beforeStr, OwnedStringCount);
            Assert.Equal(beforeBlob + 1, OwnedBlobCount);
        }
        finally { ValueBox.ReleaseOwnedHeapSlot(box); }
    }

    [Fact]
    public void BlobPayload_UpdateOrInit_CrossKind_FromSymbol_DoesNotFreeSymbol() {
        int beforeBlob = OwnedBlobCount;
        var box = ValueBox.FromSymbolId(new SymbolId(99));
        bool changed = ValueBox.BlobPayloadFace.UpdateOrInit(ref box, Bs(1, 2), out _);
        try {
            Assert.True(changed);
            Assert.Equal(beforeBlob + 1, OwnedBlobCount);
            // Symbol 由 mark-sweep 管，不参与 manual release；FreeOldOwnedHeapIfNeeded 不应触碰它，
            // 这里仅断言切换成功且未抛异常。
        }
        finally { ValueBox.ReleaseOwnedHeapSlot(box); }
    }

    [Fact]
    public void BlobPayload_UpdateOrInit_CrossKind_FromNull_AllocatesNew() {
        int beforeBlob = OwnedBlobCount;
        var box = ValueBox.Null;
        bool changed = ValueBox.BlobPayloadFace.UpdateOrInit(ref box, Bs(1, 2, 3), out uint oldBytes);
        try {
            Assert.True(changed);
            Assert.Equal(1u, oldBytes); // EstimateBareSize(Null) == 1
            Assert.Equal(beforeBlob + 1, OwnedBlobCount);
            Assert.Equal(ValueKind.Blob, box.GetValueKind());
        }
        finally { ValueBox.ReleaseOwnedHeapSlot(box); }
    }

    [Fact]
    public void BlobPayload_UpdateToNull_ViaUpdateToNull_ReleasesSlot() {
        int before = OwnedBlobCount;
        var box = ValueBox.BlobPayloadFace.From(Bs(1, 2, 3));
        Assert.Equal(before + 1, OwnedBlobCount);

        bool changed = ValueBox.UpdateToNull(ref box);
        Assert.True(changed);
        Assert.True(box.IsNull);
        Assert.Equal(before, OwnedBlobCount);
    }

    [Fact]
    public void BlobPayload_OldBareBytes_CapturedBeforeCrossKind_ToString() {
        int beforeBlob = OwnedBlobCount;
        int beforeStr = OwnedStringCount;
        var box = ValueBox.BlobPayloadFace.From(new ByteString(new byte[128]));
        Assert.Equal(beforeBlob + 1, OwnedBlobCount);

        bool changed = ValueBox.StringPayloadFace.UpdateOrInit(ref box, "s", out uint oldBytes);
        try {
            Assert.True(changed);
            Assert.Equal(BlobTaggedSize(128), oldBytes);
            Assert.Equal(beforeBlob, OwnedBlobCount);
            Assert.Equal(beforeStr + 1, OwnedStringCount);
            Assert.Equal(ValueKind.String, box.GetValueKind());
        }
        finally { ValueBox.ReleaseOwnedHeapSlot(box); }
    }

    [Fact]
    public void BlobPayload_OldBareBytes_CapturedBeforeCrossKind_ToSymbol() {
        int beforeBlob = OwnedBlobCount;
        var box = ValueBox.BlobPayloadFace.From(Bs(1, 2, 3, 4, 5));
        Assert.Equal(beforeBlob + 1, OwnedBlobCount);

        bool changed = ValueBox.SymbolIdFace.UpdateOrInit(ref box, new SymbolId(123), out uint oldBytes);

        Assert.True(changed);
        Assert.Equal(BlobTaggedSize(5), oldBytes);
        Assert.Equal(beforeBlob, OwnedBlobCount);
        Assert.Equal(ValueKind.Symbol, box.GetValueKind());
    }

    [Fact]
    public void BlobPayload_OldBareBytes_CapturedBeforeCrossKind_ToHeapBits64() {
        int beforeBlob = OwnedBlobCount;
        int beforeBits64 = Bits64Count;
        var box = ValueBox.BlobPayloadFace.From(Bs(9, 8, 7, 6));
        Assert.Equal(beforeBlob + 1, OwnedBlobCount);

        bool changed = ValueBox.Int64Face.UpdateOrInit(ref box, (long)LzcConstants.NonnegIntInlineCap, out uint oldBytes);
        try {
            Assert.True(changed);
            Assert.Equal(BlobTaggedSize(4), oldBytes);
            Assert.Equal(beforeBlob, OwnedBlobCount);
            Assert.Equal(beforeBits64 + 1, Bits64Count);
            Assert.Equal(ValueKind.NonnegativeInteger, box.GetValueKind());
        }
        finally { ValueBox.ReleaseOwnedHeapSlot(box); }
    }

    // ─────────────────── EstimateBareSize ───────────────────

    [Theory]
    [InlineData(0, 2)]      // 空 blob: 1B tag + VarUInt(0)=1B = 2
    [InlineData(1, 3)]      // 1B: 1B tag + VarUInt(1)=1B + 1B = 3
    [InlineData(63, 65)]    // VarUInt 单字节边界（含），1+1+63
    [InlineData(127, 129)]  // VarUInt 单字节最大
    [InlineData(128, 131)]  // VarUInt 进 2 字节：1+2+128
    [InlineData(16383, 16386)] // VarUInt 2 字节边界（含），1+2+16383
    [InlineData(16384, 16388)] // VarUInt 进 3 字节：1+3+16384
    public void BlobPayload_EstimateBareSize_IsExact(int length, uint expected) {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++) { bytes[i] = (byte)(i & 0xFF); }
        var box = ValueBox.BlobPayloadFace.From(new ByteString(bytes));
        try {
            // ai:trick 通过 Write 实测 wire 长度，再断言 estimate == wire（exact）。
            Assert.Equal(expected, box.EstimateBareSize());
            var buffer = new ArrayBufferWriter<byte>();
            var writer = new BinaryDiffWriter(buffer);
            box.Write(writer);
            Assert.Equal(expected, (uint)buffer.WrittenCount);
            Assert.Equal(box.EstimateBareSize(), (uint)buffer.WrittenCount);
        }
        finally { ValueBox.ReleaseOwnedHeapSlot(box); }
    }

    // ─────────────────── Freeze + CloneFrozenForNewOwner ───────────────────

    [Fact]
    public void BlobPayload_Freeze_ClearsExclusiveBit() {
        var box = ValueBox.BlobPayloadFace.From(Bs(0xAB, 0xCD));
        try {
            var frozen = ValueBox.Freeze(box);
            Assert.NotEqual(box.GetBits(), frozen.GetBits()); // 差一个 ExclusiveBit
            Assert.True(ValueBox.ValueEquals(box, frozen));
            Assert.Equal(ValueKind.Blob, frozen.GetValueKind());
        }
        finally { ValueBox.ReleaseOwnedHeapSlot(box); }
    }

    [Fact]
    public void BlobPayload_FrozenFork_AllocatesNewSlot() {
        var owner1 = ValueBox.BlobPayloadFace.From(Bs(1, 2, 3));
        try {
            int beforeFork = OwnedBlobCount;
            var frozen = ValueBox.Freeze(owner1);

            var owner2 = ValueBox.CloneFrozenForNewOwner(frozen);
            Assert.Equal(beforeFork + 1, OwnedBlobCount); // 新 pool slot
            Assert.True(ValueBox.ValueEquals(frozen, owner2));
            Assert.NotEqual(frozen.GetBits(), owner2.GetBits()); // 不同 handle
            Assert.Equal(ValueKind.Blob, owner2.GetValueKind());

            ValueBox.ReleaseOwnedHeapSlot(owner2);
            Assert.Equal(beforeFork, OwnedBlobCount);
        }
        finally { ValueBox.ReleaseOwnedHeapSlot(owner1); }
    }

    // ─────────────────── Wire round-trip via dispatcher ───────────────────

    [Fact]
    public void BlobPayload_Wire_RoundTrip_ViaDispatcher() {
        var src = ValueBox.BlobPayloadFace.From(Bs(0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0xFF));
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(buffer);
        try {
            src.Write(writer);
            byte[] bytes = buffer.WrittenSpan.ToArray();
            Assert.Equal(ScalarRules.BlobPayload.Tag, bytes[0]);

            ValueBox dst = ValueBox.Null;
            var reader = new BinaryDiffReader(bytes);
            bool changed = TaggedValueDispatcher.UpdateOrInit(ref reader, ref dst);
            try {
                Assert.True(changed);
                Assert.True(ValueBox.ValueEquals(src, dst));
                Assert.Equal(GetIssue.None, ValueBox.BlobPayloadFace.Get(dst, out ByteString v));
                Assert.Equal(Bs(0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0xFF), v);
            }
            finally { ValueBox.ReleaseOwnedHeapSlot(dst); }
        }
        finally { ValueBox.ReleaseOwnedHeapSlot(src); }
    }

    [Fact]
    public void BlobPayload_Wire_EmptyBlob_RoundTrip() {
        var src = ValueBox.BlobPayloadFace.From(ByteString.Empty);
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(buffer);
        try {
            src.Write(writer);
            byte[] bytes = buffer.WrittenSpan.ToArray();
            // 0xC1 0x00 — tag + VarUInt(0)
            Assert.Equal(2, bytes.Length);
            Assert.Equal(ScalarRules.BlobPayload.Tag, bytes[0]);
            Assert.Equal(0, bytes[1]);

            ValueBox dst = ValueBox.Null;
            var reader = new BinaryDiffReader(bytes);
            TaggedValueDispatcher.UpdateOrInit(ref reader, ref dst);
            try {
                Assert.True(ValueBox.ValueEquals(src, dst));
            }
            finally { ValueBox.ReleaseOwnedHeapSlot(dst); }
        }
        finally { ValueBox.ReleaseOwnedHeapSlot(src); }
    }

    [Fact]
    public void BlobPayload_Dispatcher_UpdateExistingBlob_ReleasesOldSlot() {
        int before = OwnedBlobCount;
        ValueBox box = ValueBox.BlobPayloadFace.From(Bs(1, 2));
        Assert.Equal(before + 1, OwnedBlobCount);

        // 写入新 blob 数据，dispatcher 会触发 inplace 路径（exclusive blob → 同 slot 覆写）。
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(buffer);
        writer.TaggedBlob(Bs(9, 8, 7, 6, 5));

        var reader = new BinaryDiffReader(buffer.WrittenSpan);
        bool changed = TaggedValueDispatcher.UpdateOrInit(ref reader, ref box);
        try {
            Assert.True(changed);
            Assert.Equal(before + 1, OwnedBlobCount); // inplace
            Assert.Equal(GetIssue.None, ValueBox.BlobPayloadFace.Get(box, out ByteString v));
            Assert.Equal(Bs(9, 8, 7, 6, 5), v);
        }
        finally { ValueBox.ReleaseOwnedHeapSlot(box); }
    }

    [Fact]
    public void BlobPayload_Dispatcher_UpdateToString_ReleasesBlobSlot() {
        int beforeBlob = OwnedBlobCount;
        int beforeStr = OwnedStringCount;
        ValueBox box = ValueBox.BlobPayloadFace.From(Bs(1, 2, 3));
        Assert.Equal(beforeBlob + 1, OwnedBlobCount);

        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(buffer);
        writer.TaggedString("switched");

        var reader = new BinaryDiffReader(buffer.WrittenSpan);
        bool changed = TaggedValueDispatcher.UpdateOrInit(ref reader, ref box);
        try {
            Assert.True(changed);
            Assert.Equal(beforeBlob, OwnedBlobCount);
            Assert.Equal(beforeStr + 1, OwnedStringCount);
            Assert.Equal(ValueKind.String, box.GetValueKind());
        }
        finally { ValueBox.ReleaseOwnedHeapSlot(box); }
    }

    // ─────────────────── 大 blob ───────────────────

    [Fact]
    public void BlobPayload_LargeBlob_1MB_RoundTrip() {
        const int Size = 1024 * 1024;
        var data = new byte[Size];
        var rng = new Random(20260427);
        rng.NextBytes(data);

        var src = ValueBox.BlobPayloadFace.From(new ByteString(data));
        try {
            // Estimate: 1 + VarUInt(1MB) + 1MB. VarUInt(1048576) = 4 bytes (since 2^20 needs 4-byte VarUInt with this scheme).
            uint estimate = src.EstimateBareSize();
            Assert.True(estimate >= 1 + 1 + Size, $"estimate {estimate} should >= 1+1+{Size}");
            Assert.True(estimate <= 1 + 5 + Size, $"estimate {estimate} should <= 1+5+{Size}");

            var buffer = new ArrayBufferWriter<byte>();
            var writer = new BinaryDiffWriter(buffer);
            src.Write(writer);
            Assert.Equal(estimate, (uint)buffer.WrittenCount); // exact: estimate == wire

            ValueBox dst = ValueBox.Null;
            var reader = new BinaryDiffReader(buffer.WrittenSpan);
            TaggedValueDispatcher.UpdateOrInit(ref reader, ref dst);
            try {
                Assert.True(ValueBox.ValueEquals(src, dst));
                Assert.Equal(GetIssue.None, ValueBox.BlobPayloadFace.Get(dst, out ByteString v));
                Assert.Equal(Size, v.Length);
                Assert.True(v.AsSpan().SequenceEqual(data));
            }
            finally { ValueBox.ReleaseOwnedHeapSlot(dst); }
        }
        finally { ValueBox.ReleaseOwnedHeapSlot(src); }
    }

    [Fact]
    public void BlobPayload_VeryLargeBlob_4MB_EstimateExact() {
        const int Size = 4 * 1024 * 1024;
        var src = ValueBox.BlobPayloadFace.From(new ByteString(new byte[Size]));
        try {
            uint estimate = src.EstimateBareSize();
            var buffer = new ArrayBufferWriter<byte>();
            var writer = new BinaryDiffWriter(buffer);
            src.Write(writer);
            Assert.Equal(estimate, (uint)buffer.WrittenCount);
        }
        finally { ValueBox.ReleaseOwnedHeapSlot(src); }
    }

    // ─────────────────── 防止污染其他 pool ───────────────────

    [Fact]
    public void BlobPayload_DoesNotPolluteOtherPools() {
        int beforeStr = OwnedStringCount;
        int beforeBits64 = Bits64Count;
        var box = ValueBox.BlobPayloadFace.From(Bs(1, 2, 3));
        try {
            Assert.False(box.IsSymbolRef);
            Assert.False(box.IsStringPayloadRef);
            Assert.True(box.IsBlobPayloadRef);
            Assert.Equal(beforeStr, OwnedStringCount);
            Assert.Equal(beforeBits64, Bits64Count);
        }
        finally { ValueBox.ReleaseOwnedHeapSlot(box); }
    }

    [Fact]
    public void BlobPayload_HashCode_ConsistentWithEquality() {
        // 三组同值不同 slot；hash 必须相等。
        var a = ValueBox.BlobPayloadFace.From(Bs(0x10, 0x20, 0x30, 0x40));
        var b = ValueBox.BlobPayloadFace.From(Bs(0x10, 0x20, 0x30, 0x40));
        try {
            Assert.True(ValueBox.ValueEquals(a, b));
            Assert.Equal(ValueBox.ValueHashCode(a), ValueBox.ValueHashCode(b));

            // 不同内容 hash 大概率不等（这里不强制，只断言不抛）。
            int _ = ValueBox.ValueHashCode(a);
        }
        finally {
            ValueBox.ReleaseOwnedHeapSlot(a);
            ValueBox.ReleaseOwnedHeapSlot(b);
        }
    }
}
