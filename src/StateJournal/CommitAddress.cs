using System.Globalization;
using Atelia.Data;

namespace Atelia.StateJournal;

/// <summary>
/// commit 在物理存储中的唯一坐标：{SegmentNumber, CommitTicket}。
/// 始终有效——SegmentNumber &gt; 0 且 CommitTicket 非 null。
/// "尚无 commit"的状态（unborn）由外部用 <c>CommitAddress?</c> 表达。
/// </summary>
/// <remarks>
/// 外部格式：<c>seg:{SegmentNumber}:{ticket:x16}</c>。
/// 示例：<c>seg:1:a1b2c3d4e5f67890</c>。
/// 可通过 <see cref="ToString"/> / <see cref="Parse"/> 序列化与反序列化。
/// </remarks>
public readonly record struct CommitAddress {
    public uint SegmentNumber { get; }
    public CommitTicket CommitTicket { get; }

    public CommitAddress(uint segmentNumber, CommitTicket commitTicket) {
        if (segmentNumber == 0) { throw new InvalidOperationException("CommitAddress must have segmentNumber > 0."); }
        if (commitTicket.IsNull) { throw new InvalidOperationException("CommitAddress must have a non-null CommitTicket."); }

        SegmentNumber = segmentNumber;
        CommitTicket = commitTicket;
    }

    public static CommitAddress Create(uint segmentNumber, CommitTicket commitTicket) {
        return new CommitAddress(segmentNumber, commitTicket);
    }

    public static CommitAddress FromPersisted(uint segmentNumber, ulong ticketSerialized, string sourceDescription) {
        var commitTicket = new CommitTicket(SizedPtr.Deserialize(ticketSerialized));
        if (segmentNumber > 0 && !commitTicket.IsNull) { return Create(segmentNumber, commitTicket); }

        throw new InvalidDataException(
            $"Metadata '{sourceDescription}' contains an invalid commit address: segmentNumber={segmentNumber}, ticket={ticketSerialized}."
        );
    }

    /// <summary>
    /// 返回人类可读的坐标字符串：<c>seg:{SegmentNumber}:{ticketHex16}</c>。
    /// 此字符串可被 <see cref="Parse"/> 精确还原。
    /// </summary>
    public override string ToString() => $"seg:{SegmentNumber}:{CommitTicket.Ticket.Serialize():x16}";

    /// <summary>
    /// 从 <see cref="ToString"/> 输出的字符串还原 CommitAddress。
    /// </summary>
    /// <exception cref="FormatException">字符串格式不合法。</exception>
    public static CommitAddress Parse(string s) {
        ArgumentException.ThrowIfNullOrWhiteSpace(s);
        var parts = s.Split(':');
        if (parts.Length != 3 || !string.Equals(parts[0], "seg", StringComparison.Ordinal)) {
            throw new FormatException($"Invalid CommitAddress format: '{s}'. Expected 'seg:<segmentNumber>:<ticketHex16>'.");
        }

        if (!uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var segmentNumber) || segmentNumber == 0) {
            throw new FormatException($"Invalid segment number in CommitAddress: '{parts[1]}'.");
        }

        if (parts[2].Length != 16
            || !ulong.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var ticketSerialized)) {
            throw new FormatException($"Invalid ticket hex in CommitAddress: '{parts[2]}'.");
        }

        var commitTicket = new CommitTicket(SizedPtr.Deserialize(ticketSerialized));
        if (commitTicket.IsNull) {
            throw new FormatException($"Invalid ticket hex in CommitAddress: '{parts[2]}'.");
        }

        return Create(segmentNumber, commitTicket);
    }

    /// <summary>
    /// 安全解析，失败时返回 null 而非抛异常。
    /// </summary>
    public static CommitAddress? TryParse(string s) {
        if (string.IsNullOrWhiteSpace(s)) { return null; }
        try { return Parse(s); }
        catch (FormatException) { return null; }
        catch (ArgumentException) { return null; }
    }
}
