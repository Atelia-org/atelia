using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Atelia.EventJournal;
using Atelia.SessionJournal;

namespace Atelia.Galatea.Server;

/// <summary>
/// Code-owned durable identities and bounds shared by the current-state store
/// and the legacy in-memory coordinator during the hard-cut transition.
/// </summary>
internal static class GalateaDelegationDurableContract {
    internal const int MaximumCandidateCount = 4_096;
    internal const int MaximumCandidateUtf8Bytes = 64 * 1024 * 1024;
    internal const int MaximumActionHeadTombstones = 4_096;

    internal const string RouteProtocolVersion =
        "galatea-codex-sidecar-jsonrpc-v2";
    internal const string RouteProfileVersion =
        "fixed-thread-single-active-mail-v1";
    internal const string TaskTooLargeStage = "preflight";
    internal const string TaskTooLargeCode = "TASK_INVALID_OR_TOO_LARGE";
    internal const string TaskTooLargeNotice =
        "外界代行者 Codex 未能处理这封信（阶段：preflight；错误代码：TASK_INVALID_OR_TOO_LARGE）。";

    private const string DispatchPrefix = "gd1-";
    private const string RoutePolicyPrefix = "gdrp1-";
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

    internal static string CreateRoutePolicyFingerprint(
        GalateaDelegateRouteConfig route
    ) {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(route.Tools);
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256
        );
        AppendLengthPrefixed(hash, RouteProtocolVersion);
        AppendLengthPrefixed(hash, RouteProfileVersion);
        AppendLengthPrefixed(hash, route.Recipient);
        AppendLengthPrefixed(hash, route.Kind);
        AppendLengthPrefixed(hash, route.Cwd);
        AppendLengthPrefixed(hash, RouteModeText(route.Mode));
        AppendLengthPrefixed(hash, BooleanText(route.LocalCommandNetwork));
        AppendLengthPrefixed(hash, WebSearchText(route.Tools.WebSearch));
        AppendLengthPrefixed(hash, BooleanText(route.Tools.ImageGeneration));
        AppendLengthPrefixed(hash, BooleanText(route.Tools.ViewImage));
        AppendLengthPrefixed(hash, IntegerText(route.MaximumQueuedMails));
        AppendLengthPrefixed(hash, IntegerText(route.MaximumTaskUtf8Bytes));
        AppendLengthPrefixed(hash, IntegerText(route.MaximumReplyUtf8Bytes));
        AppendLengthPrefixed(hash, IntegerText(route.MaximumInboxReplies));
        AppendLengthPrefixed(hash, IntegerText(route.MaximumInboxUtf8Bytes));
        return RoutePolicyPrefix
            + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static string CreateDeliveryFailureNotice(
        string? stage,
        string? code
    ) {
        string safeStage = NormalizeFailureToken(stage, "delegate");
        string safeCode = NormalizeFailureToken(code, "DELEGATE_FAILURE");
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

    private static string RouteModeText(GalateaDelegateMode value) => value switch {
        GalateaDelegateMode.Research => "research",
        GalateaDelegateMode.Work => "work",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string WebSearchText(
        GalateaDelegateWebSearchMode value
    ) => value switch {
        GalateaDelegateWebSearchMode.Disabled => "disabled",
        GalateaDelegateWebSearchMode.Cached => "cached",
        GalateaDelegateWebSearchMode.Indexed => "indexed",
        GalateaDelegateWebSearchMode.Live => "live",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string BooleanText(bool value) => value ? "true" : "false";

    private static string IntegerText(int value) =>
        value.ToString(CultureInfo.InvariantCulture);

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
