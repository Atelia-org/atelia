using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Atelia.EventJournal;

namespace Atelia.SessionJournal.HistoryTimeline;

internal static class HistorySelectedPathCommitment {
    private static readonly byte[] EmptyPreimage =
        Encoding.UTF8.GetBytes(
            "atelia.history-timeline.selected-path.mmr.v1\0empty");
    private static readonly byte[] LeafDomain =
        Encoding.UTF8.GetBytes(
            "atelia.history-timeline.selected-path.mmr.v1\0leaf\0");
    private static readonly byte[] NodeDomain =
        Encoding.UTF8.GetBytes(
            "atelia.history-timeline.selected-path.mmr.v1\0node\0");
    private static readonly byte[] RootDomain =
        Encoding.UTF8.GetBytes(
            "atelia.history-timeline.selected-path.mmr.v1\0root\0");

    internal static string EmptyDigest { get; } = HashHex(EmptyPreimage);

    internal static string ComputeLeaf(
        long ordinal,
        HistoryRowId rowId,
        HistoryRowId? previousRowId,
        EventAddress endInclusive
    ) {
        byte[] row = Encoding.UTF8.GetBytes(rowId.Value);
        byte[] previous = previousRowId is null
            ? []
            : Encoding.UTF8.GetBytes(previousRowId.Value.Value);
        byte[] end = new byte[EventAddressCodec.EventAddressLength];
        EventAddressCodec.Encode(endInclusive, end);
        byte[] preimage = new byte[
            LeafDomain.Length + 8 + 4 + row.Length + 4
            + previous.Length + end.Length];
        int offset = 0;
        LeafDomain.CopyTo(preimage, offset);
        offset += LeafDomain.Length;
        BinaryPrimitives.WriteInt64BigEndian(
            preimage.AsSpan(offset, 8), ordinal);
        offset += 8;
        BinaryPrimitives.WriteInt32BigEndian(
            preimage.AsSpan(offset, 4), row.Length);
        offset += 4;
        row.CopyTo(preimage, offset);
        offset += row.Length;
        BinaryPrimitives.WriteInt32BigEndian(
            preimage.AsSpan(offset, 4), previous.Length);
        offset += 4;
        previous.CopyTo(preimage, offset);
        offset += previous.Length;
        end.CopyTo(preimage, offset);
        return HashHex(preimage);
    }

    internal static string Combine(
        int level,
        string left,
        string right
    ) {
        byte[] leftBytes = Convert.FromHexString(left);
        byte[] rightBytes = Convert.FromHexString(right);
        byte[] preimage = new byte[
            NodeDomain.Length + 4 + leftBytes.Length + rightBytes.Length];
        NodeDomain.CopyTo(preimage, 0);
        BinaryPrimitives.WriteInt32BigEndian(
            preimage.AsSpan(NodeDomain.Length, 4), level);
        leftBytes.CopyTo(preimage, NodeDomain.Length + 4);
        rightBytes.CopyTo(
            preimage,
            NodeDomain.Length + 4 + leftBytes.Length);
        return HashHex(preimage);
    }

    internal static string ComputeRoot(
        long count,
        IReadOnlyList<HistorySelectedPathPeak> peaks
    ) {
        if (count == 0) {
            if (peaks.Count != 0) {
                throw new InvalidDataException(
                    "An empty selected path cannot have Merkle peaks.");
            }
            return EmptyDigest;
        }
        using var stream = new MemoryStream();
        stream.Write(RootDomain);
        Span<byte> number = stackalloc byte[8];
        Span<byte> levelBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt64BigEndian(number, count);
        stream.Write(number);
        foreach (HistorySelectedPathPeak peak in peaks) {
            BinaryPrimitives.WriteInt32BigEndian(levelBytes, peak.Level);
            stream.Write(levelBytes);
            BinaryPrimitives.WriteInt64BigEndian(number, peak.NodeIndex);
            stream.Write(number);
            stream.Write(Convert.FromHexString(peak.Digest));
        }
        return HashHex(stream.ToArray());
    }

    internal static IReadOnlyList<(int Level, long NodeIndex)> PeakKeys(
        long count
    ) {
        if (count < 0) {
            throw new InvalidDataException(
                "Selected path count cannot be negative.");
        }
        var result = new List<(int, long)>();
        long offset = 0;
        for (int level = 62; level >= 0; level--) {
            long size = 1L << level;
            if ((count & size) == 0) {
                continue;
            }
            result.Add((level, offset >> level));
            offset = checked(offset + size);
        }
        return result;
    }

    private static string HashHex(ReadOnlySpan<byte> bytes)
        => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

internal sealed record HistorySelectedPathPeak(
    int Level,
    long NodeIndex,
    string Digest
);
