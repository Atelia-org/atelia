// Source: Atelia.StateJournal - DiffPayload 值类型
// Spec: atelia/docs/StateJournal/mvp-design-v2.md §3.4.2

namespace Atelia.StateJournal;

/// <summary>
/// DiffPayload 中的值类型标识（KeyValuePairType 低 4 bit）。
/// </summary>
/// <remarks>
/// <para>对应条款：<c>[F-UNKNOWN-VALUETYPE-REJECT]</c></para>
/// <para>
/// 取值范围：
/// <list type="bullet">
///   <item><c>0x0</c>: Null — 无 payload</item>
///   <item><c>0x1</c>: Tombstone — 无 payload（表示删除）</item>
///   <item><c>0x2</c>: ObjRef — ObjectId（varuint）</item>
///   <item><c>0x3</c>: VarInt — varint（ZigZag）</item>
///   <item><c>0x4</c>: Ptr64 — u64 LE</item>
/// </list>
/// </para>
/// </remarks>
public enum ValueType : byte {
    /// <summary>
    /// Null 值，无 payload。
    /// </summary>
    Null = 0x0,

    /// <summary>
    /// 墓碑标记，无 payload。表示该 key 被删除。
    /// </summary>
    Tombstone = 0x1,

    /// <summary>
    /// 对象引用，payload 为 ObjectId（varuint 编码）。
    /// </summary>
    ObjRef = 0x2,

    /// <summary>
    /// 有符号整数，payload 为 varint（ZigZag 编码）。
    /// </summary>
    VarInt = 0x3,

    /// <summary>
    /// 64 位指针，payload 为 u64 LE（8 字节小端序）。
    /// </summary>
    Ptr64 = 0x4,
}

/// <summary>
/// <see cref="ValueType"/> 扩展方法。
/// </summary>
public static class ValueTypeExtensions {
    /// <summary>
    /// 已知 ValueType 的最大值。
    /// </summary>
    public const byte MaxKnownValueType = 0x4;

    /// <summary>
    /// KeyValuePairType 的高 4 bit 掩码。
    /// </summary>
    public const byte HighBitsMask = 0xF0;

    /// <summary>
    /// KeyValuePairType 的低 4 bit 掩码（ValueType 部分）。
    /// </summary>
    public const byte LowBitsMask = 0x0F;

    /// <summary>
    /// 判断该 ValueType 是否已知（MVP 范围内）。
    /// </summary>
    /// <param name="valueType">值类型。</param>
    /// <returns>如果是已知的 ValueType 返回 true；否则返回 false。</returns>
    public static bool IsKnown(this ValueType valueType) {
        return (byte)valueType <= MaxKnownValueType;
    }

    /// <summary>
    /// 判断该 ValueType 是否需要 payload。
    /// </summary>
    /// <param name="valueType">值类型。</param>
    /// <returns>如果需要 payload 返回 true；否则返回 false。</returns>
    public static bool HasPayload(this ValueType valueType) {
        return valueType switch {
            ValueType.Null => false,
            ValueType.Tombstone => false,
            ValueType.ObjRef => true,
            ValueType.VarInt => true,
            ValueType.Ptr64 => true,
            _ => false, // 未知类型假设无 payload（但会被 reject）
        };
    }

    /// <summary>
    /// 从 KeyValuePairType 字节提取 ValueType（低 4 bit）。
    /// </summary>
    /// <param name="keyValuePairType">KeyValuePairType 字节。</param>
    /// <returns>ValueType 枚举值。</returns>
    public static ValueType ExtractValueType(byte keyValuePairType) {
        return (ValueType)(keyValuePairType & LowBitsMask);
    }

    /// <summary>
    /// 检查 KeyValuePairType 字节的高 4 bit 是否为 0。
    /// </summary>
    /// <param name="keyValuePairType">KeyValuePairType 字节。</param>
    /// <returns>如果高 4 bit 为 0 返回 true；否则返回 false。</returns>
    /// <remarks>
    /// 对应条款：<c>[F-KVPAIR-HIGHBITS-RESERVED]</c>
    /// </remarks>
    public static bool AreHighBitsZero(byte keyValuePairType) {
        return (keyValuePairType & HighBitsMask) == 0;
    }

    /// <summary>
    /// 验证 KeyValuePairType 字节是否合法（高 4 bit 为 0 且 ValueType 已知）。
    /// </summary>
    /// <param name="keyValuePairType">KeyValuePairType 字节。</param>
    /// <param name="valueType">输出的 ValueType。</param>
    /// <returns>如果合法返回成功的 Result；否则返回失败的 Result。</returns>
    public static AteliaResult<ValueType> ValidateKeyValuePairType(byte keyValuePairType) {
        if (!AreHighBitsZero(keyValuePairType)) {
            return AteliaResult<ValueType>.Failure(
                new DiffPayloadFormatError(
                    $"KeyValuePairType high 4 bits must be 0, but got 0x{keyValuePairType:X2}.",
                    "The file may be corrupted or from a newer version."
                )
            );
        }

        var valueType = ExtractValueType(keyValuePairType);
        if (!valueType.IsKnown()) {
            return AteliaResult<ValueType>.Failure(
                new UnknownValueTypeError(keyValuePairType)
            );
        }

        return AteliaResult<ValueType>.Success(valueType);
    }
}
