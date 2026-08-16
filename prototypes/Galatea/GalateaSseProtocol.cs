using System.Text;
using System.Text.Json;

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

    internal static GalateaSseFrame ReasoningDelta(string delta) =>
        Delta("reasoning-delta", delta);

    internal static GalateaSseFrame TextDelta(string delta) =>
        Delta("text-delta", delta);

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

    private static GalateaSseFrame Delta(
        string eventName,
        string delta
    ) {
        if (string.IsNullOrEmpty(delta)) {
            throw new ArgumentException(
                "SSE delta must be nonempty.",
                nameof(delta)
            );
        }
        return Encode(
            eventName,
            new GalateaSseDeltaPayload(delta),
            terminal: false
        );
    }

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

    private sealed record GalateaSseDeltaPayload(string Delta);

    private sealed record GalateaSseDonePayload(
        RecentTurnsResponseDto? Recent
    );

    private sealed record GalateaSseErrorPayload(
        string Code,
        string Message
    );
}
