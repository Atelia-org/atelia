using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Atelia.Completion.Abstractions;

namespace Atelia.SessionJournal;

/// <summary>
/// Stable, content-only commitments for raw history contributions. These
/// hashes deliberately exclude EventAddress and execution metadata such as
/// invocation, correlation, checkpoints, operation ids, and runtime identity.
/// </summary>
public static class SessionHistorySemanticCommitment {
    public const string CodecId =
        "atelia.session-journal.history-semantic-commitment.v1";

    public static string ComputeObservationContributionSha256(
        ObservationMessage observation
    ) {
        ArgumentNullException.ThrowIfNull(observation);
        return ComputeCanonicalJsonHash(
            "history-message",
            writer =>
                SessionRequestCanonicalizer.WriteHistoryMessage(
                    writer,
                    observation
                )
        );
    }

    public static string ComputeActionContributionSha256(
        ActionMessage action
    ) {
        ArgumentNullException.ThrowIfNull(action);
        return ComputeCanonicalJsonHash(
            "history-message",
            writer =>
                SessionRequestCanonicalizer.WriteHistoryMessage(
                    writer,
                    action
                )
        );
    }

    public static string ComputeToolResultSha256(
        ToolResult result
    ) {
        ArgumentNullException.ThrowIfNull(result);
        return ComputeCanonicalJsonHash(
            "tool-result",
            writer =>
                SessionRequestCanonicalizer.WriteToolResult(
                    writer,
                    result
                )
        );
    }

    public static string ComputeToolResultsContributionSha256(
        IReadOnlyList<string> orderedResultSha256
    ) => ComputeOrderedHashSequence(
        "tool-results-contribution",
        orderedResultSha256
    );

    public static string ComputeSequenceSha256(
        IReadOnlyList<string> orderedContributionSha256
    ) => ComputeOrderedHashSequence(
        "history-contribution-sequence",
        orderedContributionSha256
    );

    private static string ComputeCanonicalJsonHash(
        string domain,
        Action<Utf8JsonWriter> writeValue
    ) {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   SessionRequestCanonicalizer.WriterOptions
               )) {
            writeValue(writer);
        }
        return ComputeDomainHash(domain, buffer.WrittenSpan);
    }

    private static string ComputeOrderedHashSequence(
        string domain,
        IReadOnlyList<string> orderedHashes
    ) {
        ArgumentNullException.ThrowIfNull(orderedHashes);
        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendDomain(hash, domain);
        Span<byte> countBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(
            countBytes,
            orderedHashes.Count
        );
        hash.AppendData(countBytes);
        foreach (string item in orderedHashes) {
            hash.AppendData(ParseCanonicalSha256(item));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string ComputeDomainHash(
        string domain,
        ReadOnlySpan<byte> payload
    ) {
        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendDomain(hash, domain);
        Span<byte> lengthBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(
            lengthBytes,
            payload.Length
        );
        hash.AppendData(lengthBytes);
        hash.AppendData(payload);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void AppendDomain(
        IncrementalHash hash,
        string domain
    ) {
        hash.AppendData(Encoding.UTF8.GetBytes(CodecId));
        hash.AppendData([0]);
        hash.AppendData(Encoding.UTF8.GetBytes(domain));
        hash.AppendData([0]);
    }

    private static byte[] ParseCanonicalSha256(string value) {
        if (value is null
            || value.Length != 64
            || value.Any(static ch =>
                ch is not (>= '0' and <= '9')
                    and not (>= 'a' and <= 'f')
            )) {
            throw new ArgumentException(
                "Semantic SHA-256 values must be canonical lowercase hex.",
                nameof(value)
            );
        }
        return Convert.FromHexString(value);
    }
}
