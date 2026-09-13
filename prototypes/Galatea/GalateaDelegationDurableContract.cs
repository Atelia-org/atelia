using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Atelia.EventJournal;
using Atelia.SessionJournal;

namespace Atelia.Galatea.Server;

/// <summary>
/// Code-owned durable identities and bounds for the delegation current-state
/// store and durable transport.
/// </summary>
internal static class GalateaDelegationDurableContract {
    internal const int MaximumRecoveryFailures = 8;
    internal const long MaximumRecoveryBackoffMilliseconds = 60_000;
    internal const string LocalRecoveryStage = "local-recovery";
    internal const string ResultUnconfirmedCode = "RESULT_UNCONFIRMED";
    internal const string NotDispatchedRetriesExhaustedCode = "NOT_DISPATCHED_RETRIES_EXHAUSTED";

    internal const int MaximumCandidateCount = 4_096;
    internal const int MaximumCandidateUtf8Bytes = 64 * 1024 * 1024;
    internal const int MaximumActionHeadTombstones = 4_096;

    internal const string TaskTooLargeStage = "preflight";
    internal const string TaskTooLargeCode = "TASK_INVALID_OR_TOO_LARGE";
    internal const string TaskTooLargeNotice =
        "外界代行者 Codex 未能处理这封信（阶段：preflight；错误代码：TASK_INVALID_OR_TOO_LARGE）。";

    private const string DispatchPrefix = "gd1-";
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    internal static string CreateDispatchId(
        string userId,
        EventAddress sourceActionHead,
        int artifactOrdinal
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentOutOfRangeException.ThrowIfNegative(artifactOrdinal);
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256
        );
        AppendLengthPrefixed(hash, userId);
        AppendLengthPrefixed(
            hash,
            GalateaDelegateConfigReader.CanonicalRecipient
        );
        AppendLengthPrefixed(
            hash,
            EventAddressTextCodec.Format(sourceActionHead)
        );
        AppendLengthPrefixed(
            hash,
            artifactOrdinal.ToString(CultureInfo.InvariantCulture)
        );
        return DispatchPrefix
            + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static string CreateDeliveryFailureNotice(
        string? stage,
        string? code
    ) {
        string safeStage = NormalizeFailureToken(stage, "delegate");
        string safeCode = NormalizeFailureToken(code, "DELEGATE_FAILURE");
        if (safeStage == LocalRecoveryStage) {
            return safeCode == ResultUnconfirmedCode
                ? "Codex 未能确认本次任务结果。后台恢复已结束；此前可能产生部分工作，旧工作也可能仍在运行，请先核查现状。后续任务可以继续。"
                : $"Codex 本次任务尚未开始，后台恢复已结束（错误代码：{safeCode}）。后续任务可以继续。";
        }
        return $"外界代行者 Codex 未能处理这封信（阶段：{safeStage}；错误代码：{safeCode}）。";
    }

    internal static string NormalizeFailureToken(
        string? value,
        string fallback
    ) {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 64
            || value.Any(static character =>
                !(character is >= 'A' and <= 'Z'
                    or >= 'a' and <= 'z'
                    or >= '0' and <= '9'
                    or '_' or '-' or '.'))) {
            return fallback;
        }
        return value;
    }

    private static void AppendLengthPrefixed(
        IncrementalHash hash,
        string value
    ) {
        ArgumentNullException.ThrowIfNull(value);
        byte[] utf8 = StrictUtf8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, utf8.Length);
        hash.AppendData(length);
        hash.AppendData(utf8);
    }
}
