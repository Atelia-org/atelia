using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal.Serialization;

internal ref struct BinaryDiffWriter {
    internal const byte BareFalse = 0, BareTrue = 1;
    private IBufferWriter<byte> _downstream = null!;
    private readonly Revision? _symbolRevision;

    internal BinaryDiffWriter(IBufferWriter<byte> downstream, Revision? symbolRevision = null) {
        _downstream = downstream;
        _symbolRevision = symbolRevision;
    }

    public void BareBoolean(bool value, bool asKey) {
        _downstream.GetSpan(1)[0] = value ? BareTrue : BareFalse;
        _downstream.Advance(1);
    }

    /// <summary>
    /// <see cref="Symbol"/> 的裸写入。需要调用方提供 <see cref="Revision"/> 级编码上下文。
    /// typed <see cref="Symbol"/> wire 不会产生 <see cref="SymbolId.Null"/>；empty symbol 会被 intern 为非零 id。
    /// </summary>
    public void BareSymbol(Symbol value, bool asKey) {
        if (_symbolRevision is null) { throw new InvalidOperationException("Symbol-backed string serialization requires a bound Revision context."); }
        SymbolId id = _symbolRevision.InternReachableSymbol(value.Value);
        _symbolRevision.EnsureSymbolMirrored(value.Value, id);
        BareSymbolId(id, asKey);
    }

    /// <summary>已编码 <see cref="SymbolId"/> 的裸写入，不做任何 <see cref="Revision"/> 级转换。</summary>
    public void BareSymbolId(SymbolId value, bool asKey) {
        BareUInt32(value.Value, asKey);
    }

    /// <summary>
    /// 值语义 string 的裸写入。格式：VarUInt header，后跟 payload。
    /// header LSB=0 → UTF-16LE，header 本身就是 payloadByteCount。
    /// header LSB=1 → UTF-8，payloadByteCount = header &gt;&gt; 1。
    /// </summary>
    public void BareStringPayload(string? value, bool asKey) {
        StringPayloadCodec.WriteTo(_downstream, value ?? string.Empty);
    }

    /// <summary>
    /// 值语义 <see cref="ByteString"/> (BlobPayload) 的裸写入。格式：<c>VarUInt(byteLength)</c> 后跟原始字节。
    /// 与 <see cref="BareStringPayload"/> 不同，blob 没有 UTF-8/UTF-16 自适应：长度即字节数，payload 一字不动。
    /// </summary>
    public void BareBlobPayload(ReadOnlySpan<byte> value, bool asKey) {
        VarInt.WriteUInt32(_downstream, (uint)value.Length);
        if (value.Length > 0) {
            value.CopyTo(_downstream.GetSpan(value.Length));
            _downstream.Advance(value.Length);
        }
    }

    /// <summary>依赖于<see cref="DurableObjectKind"/>基于byte。</summary>
    public void BareDurableRef(LocalId value, bool asKey) {
        BareUInt32(value.Value, asKey);
    }

    public void BareDouble(double value, bool asKey) {
        BinaryPrimitives.WriteDoubleLittleEndian(_downstream.GetSpan(8), value);
        _downstream.Advance(8);
    }
    public void BareSingle(float value, bool asKey) {
        BinaryPrimitives.WriteSingleLittleEndian(_downstream.GetSpan(4), value);
        _downstream.Advance(4);
    }
    public void BareHalf(Half value, bool asKey) {
        BinaryPrimitives.WriteHalfLittleEndian(_downstream.GetSpan(2), value);
        _downstream.Advance(2);
    }

    public void BareUInt64(ulong value, bool asKey) => VarInt.WriteUInt64(_downstream, value);
    public void BareUInt32(uint value, bool asKey) => VarInt.WriteUInt32(_downstream, value);
    public void BareUInt16(ushort value, bool asKey) => VarInt.WriteUInt16(_downstream, value);
    public void BareInt64(long value, bool asKey) => VarInt.WriteInt64(_downstream, value);
    public void BareInt32(int value, bool asKey) => VarInt.WriteInt32(_downstream, value);
    public void BareInt16(short value, bool asKey) => VarInt.WriteInt16(_downstream, value);

    public void BareByte(byte value, bool asKey) {
        _downstream.GetSpan(1)[0] = value;
        _downstream.Advance(1);
    }
    public void BareSByte(sbyte value, bool asKey) {
        _downstream.GetSpan(1)[0] = (byte)value;
        _downstream.Advance(1);
    }

    public void WriteCount(int count) {
        Debug.Assert(count >= 0); // 内部类型，避免层层重复检查。
        VarInt.WriteUInt32(_downstream, (uint)count);
    }
    public void WriteBytes(ReadOnlySpan<byte> array) {
        VarInt.WriteUInt32(_downstream, (uint)array.Length);
        array.CopyTo(_downstream.GetSpan(array.Length));
        _downstream.Advance(array.Length);
    }

    public void TaggedBoolean(bool value) {
        _downstream.GetSpan(1)[0] = value ? ScalarRules.True : ScalarRules.False;
        _downstream.Advance(1);
    }

    public void TaggedDurableRef(DurableRef value) {
        // 内部方法，填入正确参数是调用方的责任。
        Debug.Assert(DurableRef.IsValidObjectKind(value.Kind), $"Invalid DurableRef kind '{value.Kind}'.");
        Debug.Assert(!value.IsNull, "TaggedDurableRef requires a resolved DurableRef; use TaggedNull for the no-object sentinel.");
        if (value.IsNull) {
            TaggedNull(); // Release 尽量弥补。
            return;
        }

        bool wide = value.Id.Value > ushort.MaxValue;
        byte tag = ScalarRules.TaggedRefEncoding.EncodeTag(TaggedRefKindHelper.FromDurableObjectKind(value.Kind), wide);
        if (wide) {
            var span = _downstream.GetSpan(1 + 4);
            span[0] = tag;
            BinaryPrimitives.WriteUInt32LittleEndian(span[1..], value.Id.Value);
            _downstream.Advance(1 + 4);
        }
        else {
            var span = _downstream.GetSpan(1 + 2);
            span[0] = tag;
            BinaryPrimitives.WriteUInt16LittleEndian(span[1..], (ushort)value.Id.Value);
            _downstream.Advance(1 + 2);
        }
    }

    public void TaggedFloatingPoint(double value) {
        TaggedFloat<ScalarRules.FloatingPoint>.Write(_downstream, value);
    }

    public void TaggedNegativeInteger(long value) {
        TaggedInt.WriteNegative<ScalarRules.NegativeInteger>(_downstream, value);
    }

    public void TaggedNonnegativeInteger(ulong value) {
        TaggedInt.WriteNonnegative<ScalarRules.NonnegativeInteger>(_downstream, value);
    }

    public void TaggedNull() {
        _downstream.GetSpan(1)[0] = ScalarRules.Null;
        _downstream.Advance(1);
    }

    /// <summary>
    /// Mixed payload string 的 tagged 写入。
    /// <paramref name="value"/> 为 <c>null</c> 时写 <see cref="ScalarRules.Null"/> (0xF6)；
    /// 否则写 <see cref="ScalarRules.StringPayload.Tag"/> (0xC0) 后跟 <see cref="BareStringPayload"/> 完整 payload。
    /// </summary>
    public void TaggedString(string? value) {
        if (value is null) {
            TaggedNull();
            return;
        }
        _downstream.GetSpan(1)[0] = ScalarRules.StringPayload.Tag;
        _downstream.Advance(1);
        BareStringPayload(value, asKey: false);
    }

    /// <summary>
    /// Mixed payload <see cref="ByteString"/> 的 tagged 写入。
    /// 写 <see cref="ScalarRules.BlobPayload.Tag"/> (0xC1) 后跟 <see cref="BareBlobPayload"/> 完整 payload。
    /// 空 blob 编码为 <c>0xC1 0x00</c>；上层用 <see cref="TaggedNull"/> 表达 null。
    /// </summary>
    public void TaggedBlob(ByteString value) {
        _downstream.GetSpan(1)[0] = ScalarRules.BlobPayload.Tag;
        _downstream.Advance(1);
        BareBlobPayload(value.AsSpan(), asKey: false);
    }

    /// <summary>
    /// 将 SymbolId 写为 tagged value，借用 TaggedRefEncoding 编码空间 + <see cref="TaggedRefKind.Symbol"/>。
    /// 布局：1-byte tag (0xA0 | Kind.Symbol&lt;&lt;1 | WideFlag)，后跟 2 或 4 字节 SymbolId.Value (LE)。
    /// </summary>
    public void TaggedSymbolId(SymbolId id) {
        // 内部方法，填入正确参数是调用方的责任。
        Debug.Assert(!id.IsNull, "TaggedSymbolId requires a resolved SymbolId; use TaggedNull for the no-symbol sentinel.");
        if (id.IsNull) {
            TaggedNull(); // Release 尽量弥补。
            return;
        }
        _symbolRevision?.EnsureSymbolIdMirrored(id);

        bool wide = id.Value > ushort.MaxValue;
        byte tag = ScalarRules.TaggedRefEncoding.EncodeTag(TaggedRefKind.Symbol, wide);
        if (wide) {
            var span = _downstream.GetSpan(1 + 4);
            span[0] = tag;
            BinaryPrimitives.WriteUInt32LittleEndian(span[1..], id.Value);
            _downstream.Advance(1 + 4);
        }
        else {
            var span = _downstream.GetSpan(1 + 2);
            span[0] = tag;
            BinaryPrimitives.WriteUInt16LittleEndian(span[1..], (ushort)id.Value);
            _downstream.Advance(1 + 2);
        }
    }
}
