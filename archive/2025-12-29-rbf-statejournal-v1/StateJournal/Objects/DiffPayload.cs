// Source: Atelia.StateJournal - DiffPayload 编解码
// Spec: atelia/docs/StateJournal/mvp-design-v2.md §3.4.2

using System.Buffers;
using System.Buffers.Binary;

namespace Atelia.StateJournal;

/// <summary>
/// DiffPayload 写入器（ref struct，用于构建 DurableDict 的 diff payload）。
/// </summary>
/// <remarks>
/// <para>
/// DiffPayload 格式：
/// <code>
/// PairCount: varuint
/// (若 PairCount == 0，结束)
/// FirstKey: varuint
/// FirstPair:
///   KeyValuePairType: byte  // 低 4 bit = ValueType，高 4 bit 必须为 0
///   Value: (由 ValueType 决定)
/// RemainingPairs[PairCount-1]:
///   KeyValuePairType: byte
///   KeyDeltaFromPrev: varuint
///   Value: (由 ValueType 决定)
/// </code>
/// </para>
/// <para>对应条款：
/// <list type="bullet">
///   <item><c>[F-KVPAIR-HIGHBITS-RESERVED]</c></item>
///   <item><c>[S-DIFF-KEY-SORTED-UNIQUE]</c></item>
/// </list>
/// </para>
/// </remarks>
public ref struct DiffPayloadWriter {
    private readonly IBufferWriter<byte> _writer;
    private int _pairCount;
    private ulong _lastKey;
    private bool _firstPair;
    private bool _started;
    private readonly List<(ulong Key, ValueType Type, ReadOnlyMemory<byte> Payload)> _pairs;

    /// <summary>
    /// 创建新的 DiffPayload 写入器。
    /// </summary>
    /// <param name="writer">目标缓冲区写入器。</param>
    public DiffPayloadWriter(IBufferWriter<byte> writer) {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _pairCount = 0;
        _lastKey = 0;
        _firstPair = true;
        _started = false;
        _pairs = new List<(ulong, ValueType, ReadOnlyMemory<byte>)>();
    }

    /// <summary>
    /// 获取已写入的键值对数量。
    /// </summary>
    public readonly int PairCount => _pairCount;

    /// <summary>
    /// 写入 Null 值。
    /// </summary>
    /// <param name="key">键（必须按升序调用）。</param>
    /// <exception cref="ArgumentException">key 不是严格升序。</exception>
    public void WriteNull(ulong key) {
        ValidateKeyOrder(key);
        _pairs.Add((key, ValueType.Null, ReadOnlyMemory<byte>.Empty));
        _pairCount++;
    }

    /// <summary>
    /// 写入 Tombstone（删除标记）。
    /// </summary>
    /// <param name="key">键（必须按升序调用）。</param>
    /// <exception cref="ArgumentException">key 不是严格升序。</exception>
    public void WriteTombstone(ulong key) {
        ValidateKeyOrder(key);
        _pairs.Add((key, ValueType.Tombstone, ReadOnlyMemory<byte>.Empty));
        _pairCount++;
    }

    /// <summary>
    /// 写入对象引用。
    /// </summary>
    /// <param name="key">键（必须按升序调用）。</param>
    /// <param name="objectId">对象 ID（编码为 varuint）。</param>
    /// <exception cref="ArgumentException">key 不是严格升序。</exception>
    public void WriteObjRef(ulong key, ulong objectId) {
        ValidateKeyOrder(key);
        var buffer = new byte[VarInt.MaxVarUInt64Bytes];
        int len = VarInt.WriteVarUInt(buffer, objectId);
        _pairs.Add((key, ValueType.ObjRef, buffer.AsMemory(0, len)));
        _pairCount++;
    }

    /// <summary>
    /// 写入有符号整数（ZigZag 编码）。
    /// </summary>
    /// <param name="key">键（必须按升序调用）。</param>
    /// <param name="value">有符号整数值。</param>
    /// <exception cref="ArgumentException">key 不是严格升序。</exception>
    public void WriteVarInt(ulong key, long value) {
        ValidateKeyOrder(key);
        var buffer = new byte[VarInt.MaxVarUInt64Bytes];
        int len = VarInt.WriteVarInt(buffer, value);
        _pairs.Add((key, ValueType.VarInt, buffer.AsMemory(0, len)));
        _pairCount++;
    }

    /// <summary>
    /// 写入 64 位指针。
    /// </summary>
    /// <param name="key">键（必须按升序调用）。</param>
    /// <param name="ptr">64 位指针值（小端序）。</param>
    /// <exception cref="ArgumentException">key 不是严格升序。</exception>
    public void WritePtr64(ulong key, ulong ptr) {
        ValidateKeyOrder(key);
        var buffer = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, ptr);
        _pairs.Add((key, ValueType.Ptr64, buffer));
        _pairCount++;
    }

    /// <summary>
    /// 完成写入，将所有数据序列化到 writer。
    /// </summary>
    public void Complete() {
        if (_started) { throw new InvalidOperationException("Complete() has already been called."); }
        _started = true;

        // 写入 PairCount
        Span<byte> countBuffer = stackalloc byte[VarInt.MaxVarUInt64Bytes];
        int countLen = VarInt.WriteVarUInt(countBuffer, (ulong)_pairCount);
        var countSpan = _writer.GetSpan(countLen);
        countBuffer[..countLen].CopyTo(countSpan);
        _writer.Advance(countLen);

        if (_pairCount == 0) { return; }

        // 写入第一对：FirstKey + KeyValuePairType + Value
        var (firstKey, firstType, firstPayload) = _pairs[0];

        // FirstKey
        Span<byte> keyBuffer = stackalloc byte[VarInt.MaxVarUInt64Bytes];
        int keyLen = VarInt.WriteVarUInt(keyBuffer, firstKey);
        var keySpan = _writer.GetSpan(keyLen);
        keyBuffer[..keyLen].CopyTo(keySpan);
        _writer.Advance(keyLen);

        // KeyValuePairType (高 4 bit = 0)
        var typeSpan = _writer.GetSpan(1);
        typeSpan[0] = (byte)firstType;
        _writer.Advance(1);

        // Value payload
        if (firstPayload.Length > 0) {
            var valueSpan = _writer.GetSpan(firstPayload.Length);
            firstPayload.Span.CopyTo(valueSpan);
            _writer.Advance(firstPayload.Length);
        }

        // 写入剩余对：KeyValuePairType + KeyDeltaFromPrev + Value
        Span<byte> deltaBuffer = stackalloc byte[VarInt.MaxVarUInt64Bytes];
        ulong prevKey = firstKey;
        for (int i = 1; i < _pairCount; i++) {
            var (key, type, payload) = _pairs[i];

            // KeyValuePairType
            var pairTypeSpan = _writer.GetSpan(1);
            pairTypeSpan[0] = (byte)type;
            _writer.Advance(1);

            // KeyDeltaFromPrev
            ulong delta = key - prevKey;
            int deltaLen = VarInt.WriteVarUInt(deltaBuffer, delta);
            var deltaSpan = _writer.GetSpan(deltaLen);
            deltaBuffer[..deltaLen].CopyTo(deltaSpan);
            _writer.Advance(deltaLen);

            // Value payload
            if (payload.Length > 0) {
                var valSpan = _writer.GetSpan(payload.Length);
                payload.Span.CopyTo(valSpan);
                _writer.Advance(payload.Length);
            }

            prevKey = key;
        }
    }

    private void ValidateKeyOrder(ulong key) {
        if (_firstPair) {
            _firstPair = false;
            _lastKey = key;
            return;
        }

        if (key <= _lastKey) {
            throw new ArgumentException(
                $"Keys must be in strictly ascending order. Got key {key} after {_lastKey}.",
                nameof(key)
            );
        }
        _lastKey = key;
    }
}

/// <summary>
/// DiffPayload 读取器（ref struct，用于解析 DurableDict 的 diff payload）。
/// </summary>
/// <remarks>
/// <para>
/// 对应条款：
/// <list type="bullet">
///   <item><c>[F-KVPAIR-HIGHBITS-RESERVED]</c></item>
///   <item><c>[F-UNKNOWN-VALUETYPE-REJECT]</c></item>
///   <item><c>[S-DIFF-KEY-SORTED-UNIQUE]</c></item>
/// </list>
/// </para>
/// </remarks>
public ref struct DiffPayloadReader {
    private ReadOnlySpan<byte> _remaining;
    private readonly int _pairCount;
    private int _pairsRead;
    private ulong _lastKey;
    private readonly bool _parseError;
    private readonly AteliaError? _error;

    /// <summary>
    /// 创建新的 DiffPayload 读取器。
    /// </summary>
    /// <param name="payload">DiffPayload 二进制数据。</param>
    public DiffPayloadReader(ReadOnlySpan<byte> payload) {
        _remaining = payload;
        _pairCount = 0;
        _pairsRead = 0;
        _lastKey = 0;
        _parseError = false;
        _error = null;

        // 读取 PairCount
        var countResult = VarInt.TryReadVarUInt(_remaining);
        if (countResult.IsFailure) {
            _parseError = true;
            _error = new DiffPayloadEofError("reading PairCount");
            return;
        }

        _pairCount = (int)countResult.Value.Value;
        _remaining = _remaining[countResult.Value.BytesConsumed..];
    }

    /// <summary>
    /// 获取键值对总数。
    /// </summary>
    public readonly int PairCount => _pairCount;

    /// <summary>
    /// 获取已读取的键值对数量。
    /// </summary>
    public readonly int PairsRead => _pairsRead;

    /// <summary>
    /// 获取是否有解析错误。
    /// </summary>
    public readonly bool HasError => _parseError;

    /// <summary>
    /// 获取解析错误（如果有）。
    /// </summary>
    public readonly AteliaError? Error => _error;

    /// <summary>
    /// 尝试读取下一个键值对。
    /// </summary>
    /// <param name="key">输出的键。</param>
    /// <param name="valueType">输出的值类型。</param>
    /// <param name="valuePayload">输出的值 payload（切片，不复制）。</param>
    /// <returns>成功时返回包含 true 的 Result；失败时返回错误。</returns>
    public AteliaResult<bool> TryReadNext(out ulong key, out ValueType valueType, out ReadOnlySpan<byte> valuePayload) {
        key = 0;
        valueType = ValueType.Null;
        valuePayload = ReadOnlySpan<byte>.Empty;

        if (_parseError) { return AteliaResult<bool>.Failure(_error!); }

        if (_pairsRead >= _pairCount) { return AteliaResult<bool>.Success(false); }

        bool isFirstPair = _pairsRead == 0;

        if (isFirstPair) {
            // 读取 FirstKey
            var keyResult = VarInt.TryReadVarUInt(_remaining);
            if (keyResult.IsFailure) {
                return AteliaResult<bool>.Failure(
                    new DiffPayloadEofError("reading FirstKey")
                );
            }
            key = keyResult.Value.Value;
            _remaining = _remaining[keyResult.Value.BytesConsumed..];
            _lastKey = key;
        }

        // 读取 KeyValuePairType
        if (_remaining.IsEmpty) {
            return AteliaResult<bool>.Failure(
                new DiffPayloadEofError("reading KeyValuePairType")
            );
        }
        byte kvpType = _remaining[0];
        _remaining = _remaining[1..];

        // 验证 KeyValuePairType
        var typeValidation = ValueTypeExtensions.ValidateKeyValuePairType(kvpType);
        if (typeValidation.IsFailure) { return AteliaResult<bool>.Failure(typeValidation.Error!); }
        valueType = typeValidation.Value;

        if (!isFirstPair) {
            // 读取 KeyDeltaFromPrev
            var deltaResult = VarInt.TryReadVarUInt(_remaining);
            if (deltaResult.IsFailure) {
                return AteliaResult<bool>.Failure(
                    new DiffPayloadEofError("reading KeyDeltaFromPrev")
                );
            }
            ulong delta = deltaResult.Value.Value;
            _remaining = _remaining[deltaResult.Value.BytesConsumed..];

            // 验证 key 唯一性：delta 必须 > 0（否则 key 会相等或回退）
            if (delta == 0) {
                return AteliaResult<bool>.Failure(
                    new DiffKeySortingError(_lastKey, _lastKey)
                );
            }

            key = _lastKey + delta;

            // 检查溢出
            if (key < _lastKey) {
                return AteliaResult<bool>.Failure(
                    new DiffPayloadFormatError(
                        $"Key overflow: {_lastKey} + {delta} overflowed.",
                        "The payload may be corrupted."
                    )
                );
            }

            _lastKey = key;
        }

        // 读取 Value payload
        var payloadError = TryReadValuePayload(valueType, out valuePayload);
        if (payloadError is not null) { return AteliaResult<bool>.Failure(payloadError); }

        _pairsRead++;
        return AteliaResult<bool>.Success(true);
    }

    /// <summary>
    /// 从 VarInt payload 解码有符号整数。
    /// </summary>
    /// <param name="valuePayload">值 payload（来自 TryReadNext）。</param>
    /// <returns>解码后的有符号整数。</returns>
    public static AteliaResult<long> ReadVarInt(ReadOnlySpan<byte> valuePayload) {
        var result = VarInt.TryReadVarInt(valuePayload);
        if (result.IsFailure) { return AteliaResult<long>.Failure(result.Error!); }
        return AteliaResult<long>.Success(result.Value.Value);
    }

    /// <summary>
    /// 从 ObjRef payload 解码对象 ID。
    /// </summary>
    /// <param name="valuePayload">值 payload（来自 TryReadNext）。</param>
    /// <returns>解码后的对象 ID。</returns>
    public static AteliaResult<ulong> ReadObjRef(ReadOnlySpan<byte> valuePayload) {
        var result = VarInt.TryReadVarUInt(valuePayload);
        if (result.IsFailure) { return AteliaResult<ulong>.Failure(result.Error!); }
        return AteliaResult<ulong>.Success(result.Value.Value);
    }

    /// <summary>
    /// 从 Ptr64 payload 解码 64 位指针。
    /// </summary>
    /// <param name="valuePayload">值 payload（来自 TryReadNext，必须是 8 字节）。</param>
    /// <returns>解码后的 64 位指针。</returns>
    public static AteliaResult<ulong> ReadPtr64(ReadOnlySpan<byte> valuePayload) {
        if (valuePayload.Length < 8) {
            return AteliaResult<ulong>.Failure(
                new DiffPayloadEofError("Ptr64 payload must be 8 bytes")
            );
        }
        return AteliaResult<ulong>.Success(
            BinaryPrimitives.ReadUInt64LittleEndian(valuePayload)
        );
    }

    /// <summary>
    /// 尝试读取值的 payload。
    /// </summary>
    /// <param name="valueType">值类型。</param>
    /// <param name="payload">输出的 payload 切片。</param>
    /// <returns>成功时返回 null；失败时返回错误。</returns>
    private AteliaError? TryReadValuePayload(ValueType valueType, out ReadOnlySpan<byte> payload) {
        payload = ReadOnlySpan<byte>.Empty;

        switch (valueType) {
            case ValueType.Null:
            case ValueType.Tombstone:
                return null;

            case ValueType.ObjRef:
            case ValueType.VarInt:
                // varuint/varint：需要找到终止字节
                var varResult = VarInt.TryReadVarUInt(_remaining);
                if (varResult.IsFailure) { return new DiffPayloadEofError($"reading {valueType} payload"); }
                payload = _remaining[..varResult.Value.BytesConsumed];
                _remaining = _remaining[varResult.Value.BytesConsumed..];
                return null;

            case ValueType.Ptr64:
                if (_remaining.Length < 8) { return new DiffPayloadEofError("reading Ptr64 payload (need 8 bytes)"); }
                payload = _remaining[..8];
                _remaining = _remaining[8..];
                return null;

            default:
                return new UnknownValueTypeError((byte)valueType);
        }
    }
}
