using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Atelia.SessionJournal;

namespace Atelia.Galatea.Server.CharacterMemory;

/// <summary>
/// One code-owned, development-only receipt ready to be attached to a future
/// ordinary player-turn Observation. The frozen notice is the only payload
/// authority retained by the in-process queue.
/// </summary>
internal sealed class CharacterNoteRequestReceipt {
    private const string ExactTextInfoString =
        "character-note-exact-text";

    private CharacterNoteRequestReceipt(
        PlayerTurnNotice.NoteRequestReceipt notice,
        int utf8Bytes
    ) {
        Notice = notice;
        Utf8Bytes = utf8Bytes;
    }

    internal PlayerTurnNotice.NoteRequestReceipt Notice { get; }

    internal int Utf8Bytes { get; }

    /// <summary>
    /// Renders one receipt for a non-empty, already validated extraction
    /// batch. Empty or pathologically fence-heavy content that cannot fit the
    /// fixed receipt/Observation budgets produces no receipt.
    /// </summary>
    internal static bool TryCreate(
        IReadOnlyList<CharacterNoteIntent> intents,
        [NotNullWhen(true)] out CharacterNoteRequestReceipt? receipt
    ) {
        ArgumentNullException.ThrowIfNull(intents);
        receipt = null;
        if (intents.Count == 0) { return false; }
        if (intents.Count > CharacterNoteBounds.MaximumIntentCount) {
            throw new ArgumentOutOfRangeException(
                nameof(intents),
                "A Character Note receipt contains too many intents."
            );
        }

        int totalExactTextUtf8Bytes = 0;
        foreach (CharacterNoteIntent? intent in intents) {
            if (intent is null) {
                throw new ArgumentException(
                    "Character Note receipt intents must not contain null items.",
                    nameof(intents)
                );
            }
            int exactTextUtf8Bytes = RequireExactText(intent.ExactText);
            totalExactTextUtf8Bytes = checked(
                totalExactTextUtf8Bytes + exactTextUtf8Bytes
            );
            if (totalExactTextUtf8Bytes
                    > CharacterNoteBounds.MaximumTotalExactTextUtf8Bytes) {
                throw new ArgumentOutOfRangeException(
                    nameof(intents),
                    "Character Note receipt exact texts exceed their total UTF-8 byte limit."
                );
            }
        }

        string body = RenderBody(intents);
        int bodyUtf8Bytes;
        try {
            bodyUtf8Bytes = TextExtractorUtf8.GetByteCount(body);
        }
        catch (EncoderFallbackException) {
            return false;
        }
        if (bodyUtf8Bytes
                > PlayerTurnObservationEnvelope
                    .MaximumNoteRequestReceiptUtf8Bytes) {
            return false;
        }

        var notice = new PlayerTurnNotice.NoteRequestReceipt(body);
        if (!PlayerTurnObservationEnvelope.FitsEveryValidPlayerText(
                [notice])) {
            return false;
        }

        receipt = new CharacterNoteRequestReceipt(notice, bodyUtf8Bytes);
        return true;
    }

    private static string RenderBody(
        IReadOnlyList<CharacterNoteIntent> intents
    ) {
        var builder = new StringBuilder();
        _ = builder.Append("Galatea runtime 已识别到 ")
            .Append(intents.Count.ToString(CultureInfo.InvariantCulture))
            .Append(" 条 Note 请求。\n\n")
            .Append("当前仅完成请求提取与回传，Memo 持久化尚未实现；\n")
            .Append("本回执不表示这些 Note 已经保存。\n\n")
            .Append("识别到的 Note 原文如下：");

        for (int index = 0; index < intents.Count; index++) {
            _ = builder.Append("\n\n")
                .Append((index + 1).ToString(
                    CultureInfo.InvariantCulture
                ))
                .Append(".\n")
                .Append(AdaptiveMarkdownFenceRenderer.RenderBlock(
                    ExactTextInfoString,
                    intents[index].ExactText
                ));
        }
        return builder.ToString();
    }

    private static int RequireExactText(string? value) {
        if (string.IsNullOrWhiteSpace(value)) {
            throw new ArgumentException(
                "Character Note receipt exact text must not be blank.",
                "intents"
            );
        }
        try {
            int utf8Bytes = TextExtractorUtf8.GetByteCount(value);
            if (utf8Bytes
                    > CharacterNoteBounds.MaximumExactTextUtf8Bytes) {
                throw new ArgumentOutOfRangeException(
                    "intents",
                    "Character Note receipt exact text exceeds its UTF-8 byte limit."
                );
            }
            return utf8Bytes;
        }
        catch (EncoderFallbackException exception) {
            throw new ArgumentException(
                "Character Note receipt exact text must contain valid Unicode.",
                "intents",
                exception
            );
        }
    }
}

/// <summary>
/// Bounded, caller-serialized FIFO for pending development receipts. Overflow
/// returns false so the caller can drop the newest receipt without changing
/// the completed main turn's outcome.
/// </summary>
internal sealed class CharacterNoteRequestReceiptQueue {
    internal const int MaximumPendingCount = 16;
    internal const int MaximumPendingUtf8Bytes = 4 * 1024 * 1024;

    private readonly int _maximumCount;
    private readonly int _maximumUtf8Bytes;
    private readonly Queue<CharacterNoteRequestReceipt> _pending = new();
    private int _totalUtf8Bytes;

    internal CharacterNoteRequestReceiptQueue(
        int maximumCount = MaximumPendingCount,
        int maximumUtf8Bytes = MaximumPendingUtf8Bytes
    ) {
        if (maximumCount <= 0) {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }
        if (maximumUtf8Bytes <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(maximumUtf8Bytes)
            );
        }
        _maximumCount = maximumCount;
        _maximumUtf8Bytes = maximumUtf8Bytes;
    }

    internal int Count => _pending.Count;

    internal int TotalUtf8Bytes => _totalUtf8Bytes;

    internal bool TryEnqueue(CharacterNoteRequestReceipt receipt) {
        ArgumentNullException.ThrowIfNull(receipt);
        if (_pending.Count >= _maximumCount
            || receipt.Utf8Bytes
                > _maximumUtf8Bytes - _totalUtf8Bytes) {
            return false;
        }
        _pending.Enqueue(receipt);
        _totalUtf8Bytes += receipt.Utf8Bytes;
        return true;
    }

    internal bool TryDequeue(
        [NotNullWhen(true)] out CharacterNoteRequestReceipt? receipt
    ) {
        if (!_pending.TryDequeue(out receipt)) { return false; }
        _totalUtf8Bytes -= receipt.Utf8Bytes;
        return true;
    }
}
