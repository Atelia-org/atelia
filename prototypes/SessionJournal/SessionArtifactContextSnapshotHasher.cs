using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Atelia.SessionJournal;

/// <summary>
/// Computes the v1 exact commitment for a materialized artifact context header.
/// Each UTF-8 component, including the domain tag, is prefixed by its unsigned
/// 32-bit big-endian byte length.
/// </summary>
internal static class SessionArtifactContextSnapshotHasher {
    public const string CodecId = "atelia.session-journal.artifact-context-snapshot.sha256.v1";
    public const int MaxSnapshotUtf8Bytes = 4 * 1024 * 1024;

    public static string ComputeSha256(SessionRequestArtifactContextSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateSnapshot(snapshot);

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendLengthPrefixedUtf8(hash, CodecId);
        AppendLengthPrefixedUtf8(hash, snapshot.SystemPromptFragment);
        AppendLengthPrefixedUtf8(hash, snapshot.ObservationMessage);
        AppendLengthPrefixedUtf8(hash, snapshot.ActionMessage);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal static void ValidateSnapshot(SessionRequestArtifactContextSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.SystemPromptFragment);
        ArgumentNullException.ThrowIfNull(snapshot.ObservationMessage);
        ArgumentNullException.ThrowIfNull(snapshot.ActionMessage);
        long totalByteCount =
            (long)Encoding.UTF8.GetByteCount(snapshot.SystemPromptFragment) +
            Encoding.UTF8.GetByteCount(snapshot.ObservationMessage) +
            Encoding.UTF8.GetByteCount(snapshot.ActionMessage);
        if (totalByteCount > MaxSnapshotUtf8Bytes) {
            throw new ArgumentException(
                $"Artifact context snapshot exceeds the {MaxSnapshotUtf8Bytes}-byte UTF-8 limit.",
                nameof(snapshot)
            );
        }
    }

    private static void AppendLengthPrefixedUtf8(IncrementalHash hash, string value) {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        Span<byte> lengthPrefix = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(lengthPrefix, checked((uint)byteCount));
        hash.AppendData(lengthPrefix);

        if (byteCount == 0) { return; }
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(bytes);
    }
}
