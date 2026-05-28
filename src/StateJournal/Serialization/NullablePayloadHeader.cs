namespace Atelia.StateJournal.Serialization;

/// <summary>
/// 可空 bare payload 的统一头部编码：
/// <c>0</c> 表示 null；非 null 时将原始 header/length 整体加一。
/// </summary>
internal static class NullablePayloadHeader {
    public static uint EncodeNull() => 0;

    public static uint EncodePresent(uint rawHeader) => checked(rawHeader + 1u);

    public static bool TryDecode(uint encodedHeader, out uint rawHeader) {
        if (encodedHeader == 0) {
            rawHeader = 0;
            return false;
        }

        rawHeader = encodedHeader - 1u;
        return true;
    }
}
