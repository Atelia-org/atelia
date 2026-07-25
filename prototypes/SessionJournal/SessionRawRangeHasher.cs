using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Atelia.EventJournal;

namespace Atelia.SessionJournal;

internal readonly record struct SessionRawRangeHashEntry(
    EventAddress Address,
    EventAddress? Parent,
    uint EventKind,
    int BodySchemaVersion,
    string PayloadSha256
);

internal static class SessionRawRangeHasher {
    private const string CodecId = "atelia.session-journal.raw-range.v1";

    public static string Compute(
        EventAddress? rawStartExclusive,
        EventAddress rawEndInclusive,
        IReadOnlyList<SessionRawRangeHashEntry> entries
    ) {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0 || entries[^1].Address != rawEndInclusive) {
            throw new ArgumentException("Raw range entries must be non-empty and end at rawEndInclusive.", nameof(entries));
        }
        EventAddress? expectedParent = rawStartExclusive;
        foreach (SessionRawRangeHashEntry entry in entries) {
            if (entry.Parent != expectedParent) {
                throw new ArgumentException(
                    $"Raw range is not parent-contiguous at {entry.Address}; expected parent '{expectedParent}', got '{entry.Parent}'.",
                    nameof(entries)
                );
            }
            expectedParent = entry.Address;
        }

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendField(hash, Encoding.UTF8.GetBytes(CodecId));
        AppendOptionalAddress(hash, rawStartExclusive);
        AppendField(hash, Encoding.UTF8.GetBytes(EventAddressTextCodec.Format(rawEndInclusive)));

        Span<byte> number = stackalloc byte[4];
        foreach (SessionRawRangeHashEntry entry in entries) {
            AppendField(hash, Encoding.UTF8.GetBytes(EventAddressTextCodec.Format(entry.Address)));
            AppendOptionalAddress(hash, entry.Parent);
            BinaryPrimitives.WriteUInt32BigEndian(number, entry.EventKind);
            hash.AppendData(number);
            BinaryPrimitives.WriteInt32BigEndian(number, entry.BodySchemaVersion);
            hash.AppendData(number);
            if (entry.PayloadSha256.Length != 64
                || entry.PayloadSha256.Any(static ch => !((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f')))) {
                throw new ArgumentException("Raw range payload hash must be lowercase SHA-256 hex.", nameof(entries));
            }
            byte[] payloadHash;
            try {
                payloadHash = Convert.FromHexString(entry.PayloadSha256);
            }
            catch (FormatException ex) {
                throw new ArgumentException("Raw range payload hash must be SHA-256 hex.", nameof(entries), ex);
            }
            AppendField(hash, payloadHash);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void AppendOptionalAddress(IncrementalHash hash, EventAddress? address) {
        if (address is { } value) {
            hash.AppendData([1]);
            AppendField(hash, Encoding.UTF8.GetBytes(EventAddressTextCodec.Format(value)));
        }
        else {
            hash.AppendData([0]);
        }
    }

    private static void AppendField(IncrementalHash hash, ReadOnlySpan<byte> bytes) {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
