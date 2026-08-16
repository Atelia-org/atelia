using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Atelia.Completion;

namespace Atelia.Galatea.Server;

internal static class GalateaSseLimits {
    internal const int MaximumPreviewUtf8Bytes = 4 * 1024 * 1024;
    internal const int MaximumTerminalFrameUtf8Bytes = 5 * 1024 * 1024;
    internal const int MaximumWholeReplayUtf8Bytes = 9 * 1024 * 1024;
    internal const int MaximumPreviewEventCount = 16_383;
    internal const int MaximumReplayEventCount = 16_384;
    internal const int SubscriberChannelCapacity = 256;
    internal const int BrowserMaximumConnectionBytes =
        MaximumWholeReplayUtf8Bytes;
    internal const int BrowserMaximumFrameBytes =
        MaximumTerminalFrameUtf8Bytes;
}

internal enum GalateaSseStatusCode {
    Generating,
    NormalizingInput,
    InputNormalizationFinished,
    UsingTools
}

internal enum GalateaSseErrorCode {
    OperatorStop,
    ServerShutdown,
    CompletionFailed,
    TurnUnavailable,
    InternalFailure
}

internal sealed class GalateaSseFrame {
    private readonly byte[] _utf8;

    internal GalateaSseFrame(
        string eventName,
        byte[] utf8,
        bool terminal
    ) {
        EventName = eventName;
        _utf8 = utf8;
        IsTerminal = terminal;
    }

    internal string EventName { get; }

    internal bool IsTerminal { get; }

    internal int Utf8Length => _utf8.Length;

    internal ReadOnlyMemory<byte> Utf8 => _utf8;
}

internal static class GalateaSseFrames {
    internal static GalateaSseFrame Status(
        GalateaSseStatusCode code,
        bool? changed = null
    ) {
        string wireCode = StatusCode(code);
        object payload;
        if (code == GalateaSseStatusCode.InputNormalizationFinished) {
            payload = new GalateaSseFinishedStatusPayload(
                wireCode,
                changed ?? throw new ArgumentNullException(nameof(changed))
            );
        }
        else {
            if (changed is not null) {
                throw new ArgumentException(
                    "Only input-normalization-finished carries changed.",
                    nameof(changed)
                );
            }
            payload = new GalateaSseStatusPayload(wireCode);
        }
        return Encode("status", payload, terminal: false);
    }

    internal static GalateaSseFrame? TryStatus(
        GalateaSseStatusCode code,
        bool? changed,
        int maximumFrameUtf8Bytes
    ) {
        if (maximumFrameUtf8Bytes < 0) {
            throw new ArgumentOutOfRangeException(
                nameof(maximumFrameUtf8Bytes)
            );
        }
        GalateaSseFrame frame = Status(code, changed);
        return frame.Utf8Length <= maximumFrameUtf8Bytes
            ? frame
            : null;
    }

    internal static GalateaSseFrame ReasoningDelta(string delta) =>
        TryReasoningDelta(delta, int.MaxValue)
        ?? throw new InvalidOperationException(
            "Galatea SSE reasoning delta exceeded Int32 capacity."
        );

    internal static GalateaSseFrame TextDelta(string delta) =>
        TryTextDelta(delta, int.MaxValue)
        ?? throw new InvalidOperationException(
            "Galatea SSE text delta exceeded Int32 capacity."
        );

    internal static GalateaSseFrame? TryReasoningDelta(
        string delta,
        int maximumFrameUtf8Bytes
    ) => TryDelta(
        "reasoning-delta",
        delta,
        maximumFrameUtf8Bytes
    );

    internal static GalateaSseFrame? TryTextDelta(
        string delta,
        int maximumFrameUtf8Bytes
    ) => TryDelta(
        "text-delta",
        delta,
        maximumFrameUtf8Bytes
    );

    internal static GalateaSseFrame Done(
        RecentTurnsResponseDto? recent
    ) => Encode(
        "done",
        new GalateaSseDonePayload(recent),
        terminal: true
    );

    internal static GalateaSseFrame Error(
        GalateaSseErrorCode code
    ) => Encode(
        "error",
        new GalateaSseErrorPayload(
            ErrorCode(code),
            ErrorMessage(code)
        ),
        terminal: true
    );

    internal static string StatusCode(GalateaSseStatusCode code) =>
        code switch {
            GalateaSseStatusCode.Generating => "generating",
            GalateaSseStatusCode.NormalizingInput => "normalizing-input",
            GalateaSseStatusCode.InputNormalizationFinished =>
                "input-normalization-finished",
            GalateaSseStatusCode.UsingTools => "using-tools",
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, null)
        };

    internal static string ErrorCode(GalateaSseErrorCode code) =>
        code switch {
            GalateaSseErrorCode.OperatorStop => "operator-stop",
            GalateaSseErrorCode.ServerShutdown => "server-shutdown",
            GalateaSseErrorCode.CompletionFailed => "completion-failed",
            GalateaSseErrorCode.TurnUnavailable => "turn-unavailable",
            GalateaSseErrorCode.InternalFailure => "internal-failure",
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, null)
        };

    internal static string ErrorMessage(GalateaSseErrorCode code) =>
        code switch {
            GalateaSseErrorCode.OperatorStop =>
                "已停止生成，本轮结果未写入历史。",
            GalateaSseErrorCode.ServerShutdown =>
                "服务器正在关闭，当前生成已终止。",
            GalateaSseErrorCode.CompletionFailed =>
                "模型本次输出未正常结束，本轮结果未写入历史。",
            GalateaSseErrorCode.TurnUnavailable =>
                "当前会话边界无法继续生成，请刷新后处理。",
            GalateaSseErrorCode.InternalFailure =>
                "生成过程中发生内部错误，请刷新后重试。",
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, null)
        };

    private static GalateaSseFrame? TryDelta(
        string eventName,
        string delta,
        int maximumFrameUtf8Bytes
    ) {
        if (string.IsNullOrEmpty(delta)) {
            throw new ArgumentException(
                "SSE delta must be nonempty.",
                nameof(delta)
            );
        }
        return TryEncodeDelta(
            eventName,
            delta,
            maximumFrameUtf8Bytes
        );
    }

    private static GalateaSseFrame? TryEncodeDelta(
        string eventName,
        string delta,
        int maximumFrameUtf8Bytes
    ) {
        if (maximumFrameUtf8Bytes < 0) {
            throw new ArgumentOutOfRangeException(
                nameof(maximumFrameUtf8Bytes)
            );
        }
        byte[] prefix = Encoding.UTF8.GetBytes(
            $"event: {eventName}\ndata: {{\"delta\":\""
        );
        const int SuffixLength = 4;
        int maximumEscapedBytes = maximumFrameUtf8Bytes
            - prefix.Length
            - SuffixLength;
        if (maximumEscapedBytes < 0
            || !TryCountEscapedUtf8(
                delta,
                maximumEscapedBytes,
                out int escapedBytes
            )) {
            return null;
        }

        int frameLength = checked(
            prefix.Length + escapedBytes + SuffixLength
        );
        byte[] frame = GC.AllocateUninitializedArray<byte>(frameLength);
        prefix.CopyTo(frame, 0);
        int destinationOffset = prefix.Length;
        ForEachEscapedUtf8Chunk(delta, encoded => {
            encoded.CopyTo(frame.AsSpan(destinationOffset));
            destinationOffset += encoded.Length;
        });
        frame[destinationOffset++] = (byte)'"';
        frame[destinationOffset++] = (byte)'}';
        frame[destinationOffset++] = (byte)'\n';
        frame[destinationOffset++] = (byte)'\n';
        if (destinationOffset != frame.Length) {
            throw new InvalidOperationException(
                "Galatea SSE delta length changed while encoding."
            );
        }
        return new GalateaSseFrame(eventName, frame, terminal: false);
    }

    private static bool TryCountEscapedUtf8(
        string value,
        int maximumBytes,
        out int escapedBytes
    ) {
        int total = 0;
        bool withinLimit = true;
        ForEachEscapedUtf8Chunk(value, encoded => {
            if (encoded.Length > maximumBytes - total) {
                withinLimit = false;
                return false;
            }
            total += encoded.Length;
            return true;
        });
        escapedBytes = total;
        return withinLimit;
    }

    private static void ForEachEscapedUtf8Chunk(
        string value,
        EscapedChunkConsumer consume
    ) => ForEachEscapedUtf8Chunk(
        value,
        encoded => {
            consume(encoded);
            return true;
        }
    );

    private static void ForEachEscapedUtf8Chunk(
        string value,
        EscapedChunkPredicate consume
    ) {
        const int EncodedCharacterBufferLength = 1024;
        Span<char> encodedCharacters = stackalloc char[
            EncodedCharacterBufferLength
        ];
        Span<byte> encodedUtf8 = stackalloc byte[
            Encoding.UTF8.GetMaxByteCount(
                EncodedCharacterBufferLength
            )
        ];
        JavaScriptEncoder encoder = GalateaJson.Options.Encoder
            ?? JavaScriptEncoder.Default;
        ReadOnlySpan<char> remaining = value;
        while (!remaining.IsEmpty) {
            OperationStatus status = encoder.Encode(
                remaining,
                encodedCharacters,
                out int consumedCharacters,
                out int writtenCharacters,
                isFinalBlock: true
            );
            if (consumedCharacters == 0 && writtenCharacters == 0) {
                throw new InvalidOperationException(
                    "Galatea JSON encoder made no progress."
                );
            }
            int writtenUtf8 = Encoding.UTF8.GetBytes(
                encodedCharacters[..writtenCharacters],
                encodedUtf8
            );
            if (!consume(encodedUtf8[..writtenUtf8])) {
                return;
            }
            remaining = remaining[consumedCharacters..];
            if (status == OperationStatus.Done) {
                if (!remaining.IsEmpty) {
                    throw new InvalidOperationException(
                        "Galatea JSON encoder finished before consuming input."
                    );
                }
                return;
            }
            if (status != OperationStatus.DestinationTooSmall) {
                throw new InvalidOperationException(
                    $"Galatea JSON encoder returned {status}."
                );
            }
        }
    }

    private delegate void EscapedChunkConsumer(
        ReadOnlySpan<byte> encoded
    );

    private delegate bool EscapedChunkPredicate(
        ReadOnlySpan<byte> encoded
    );

    private static GalateaSseFrame Encode(
        string eventName,
        object payload,
        bool terminal
    ) {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            payload,
            GalateaJson.Options
        );
        if (json.AsSpan().IndexOfAny((byte)'\r', (byte)'\n') >= 0) {
            throw new InvalidOperationException(
                "Galatea SSE payload JSON must be one physical line."
            );
        }
        byte[] prefix = Encoding.UTF8.GetBytes(
            $"event: {eventName}\ndata: "
        );
        byte[] frame = GC.AllocateUninitializedArray<byte>(
            checked(prefix.Length + json.Length + 2)
        );
        prefix.CopyTo(frame, 0);
        json.CopyTo(frame, prefix.Length);
        frame[^2] = (byte)'\n';
        frame[^1] = (byte)'\n';
        return new GalateaSseFrame(eventName, frame, terminal);
    }

    private sealed record GalateaSseStatusPayload(string Code);

    private sealed record GalateaSseFinishedStatusPayload(
        string Code,
        bool Changed
    );

    private sealed record GalateaSseDonePayload(
        RecentTurnsResponseDto? Recent
    );

    private sealed record GalateaSseErrorPayload(
        string Code,
        string Message
    );
}

internal static class GalateaSseErrorClassifier {
    internal static GalateaSseErrorCode Classify(
        GalateaTurnException exception
    ) {
        ArgumentNullException.ThrowIfNull(exception);
        string? reason = exception.FailureReason;
        if (reason is "stopped-by-user"
            or "stopped-before-dispatch"
            or "recovery-stopped-before-dispatch") {
            return GalateaSseErrorCode.OperatorStop;
        }
        if (reason is "input-limit-exceeded"
            or "uncertain-completion-restart-required"
            or "stale-session-head"
            or "tool-set-fingerprint-mismatch"
            or nameof(CompletionDispatchBindingUnavailableReason
                .ConnectionMissing)
            or nameof(CompletionDispatchBindingUnavailableReason
                .ConnectionKindMismatch)
            or nameof(CompletionDispatchBindingUnavailableReason
                .ConnectionFingerprintMismatch)
            or nameof(CompletionDispatchBindingUnavailableReason
                .ClientNameMismatch)
            or nameof(CompletionDispatchBindingUnavailableReason
                .ClientApiSpecIdMismatch)
            or nameof(CompletionDispatchBindingUnavailableReason
                .RequestAdapterFingerprintMismatch)
            || reason?.StartsWith(
                "recovery-",
                StringComparison.Ordinal
            ) == true
            || reason?.StartsWith(
                "failed-turn-",
                StringComparison.Ordinal
            ) == true
            || reason?.StartsWith(
                "recap-grid-",
                StringComparison.Ordinal
            ) == true
            || reason?.StartsWith(
                "agent-control-",
                StringComparison.Ordinal
            ) == true
            || reason?.StartsWith(
                "tool-runtime-",
                StringComparison.Ordinal
            ) == true) {
            return GalateaSseErrorCode.TurnUnavailable;
        }
        return GalateaSseErrorCode.CompletionFailed;
    }
}
