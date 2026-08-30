using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Atelia.SessionJournal;

namespace Atelia.Galatea.Server.CharacterMemory;

/// <summary>
/// One code-owned save receipt rendered only from a durable AppliedNow result.
/// The frozen notice is the only payload authority retained by the in-process
/// queue.
/// </summary>
internal sealed class CharacterNoteSaveReceipt {
    private const string ExactTextInfoString =
        "character-note-exact-text";

    private CharacterNoteSaveReceipt(
        PlayerTurnNotice.NoteSaveReceipt notice,
        int utf8Bytes
    ) {
        Notice = notice;
        Utf8Bytes = utf8Bytes;
    }

    internal PlayerTurnNotice.NoteSaveReceipt Notice { get; }

    internal int Utf8Bytes { get; }

    /// <summary>
    /// Renders one receipt from a non-empty batch read back from the durable
    /// Character Memory authority. Empty or pathologically fence-heavy content
    /// that cannot fit the fixed Observation budgets produces no receipt.
    /// </summary>
    internal static bool TryCreate(
        IReadOnlyList<CharacterNoteAppliedMemo> memos,
        [NotNullWhen(true)] out CharacterNoteSaveReceipt? receipt
    ) {
        ArgumentNullException.ThrowIfNull(memos);
        receipt = null;
        if (memos.Count == 0) { return false; }
        if (memos.Count > CharacterNoteBounds.MaximumIntentCount) {
            throw new ArgumentOutOfRangeException(
                nameof(memos),
                "A Character Note save receipt contains too many memos."
            );
        }

        int totalExactTextUtf8Bytes = 0;
        string? sourceAction = null;
        for (int index = 0; index < memos.Count; index++) {
            CharacterNoteAppliedMemo? memo = memos[index];
            if (memo is null) {
                throw new ArgumentException(
                    "Character Note save receipt memos must not contain null items.",
                    nameof(memos)
                );
            }
            if (memo.ArtifactOrdinal != index
                || memo.PodId != CharacterNoteDefaultPodV1.PodId
                || string.IsNullOrEmpty(memo.MemoId.Value)
                || string.IsNullOrWhiteSpace(memo.SourceActionAddress)
                || sourceAction is not null && !string.Equals(
                    sourceAction,
                    memo.SourceActionAddress,
                    StringComparison.Ordinal
                )) {
                throw new ArgumentException(
                    "Character Note save receipt memos do not form one ordered durable Default Pod batch.",
                    nameof(memos)
                );
            }
            sourceAction ??= memo.SourceActionAddress;
            int exactTextUtf8Bytes = RequireExactText(memo.ExactText);
            totalExactTextUtf8Bytes = checked(
                totalExactTextUtf8Bytes + exactTextUtf8Bytes
            );
            if (totalExactTextUtf8Bytes
                    > CharacterNoteBounds.MaximumTotalExactTextUtf8Bytes) {
                throw new ArgumentOutOfRangeException(
                    nameof(memos),
                    "Character Note save receipt exact texts exceed their total UTF-8 byte limit."
                );
            }
        }

        string body = RenderBody(memos);
        int bodyUtf8Bytes;
        try {
            bodyUtf8Bytes = TextExtractorUtf8.GetByteCount(body);
        }
        catch (EncoderFallbackException) {
            return false;
        }
        if (bodyUtf8Bytes
                > PlayerTurnObservationEnvelope
                    .MaximumNoteSaveReceiptUtf8Bytes) {
            return false;
        }

        var notice = new PlayerTurnNotice.NoteSaveReceipt(body);
        if (!PlayerTurnObservationEnvelope.FitsEveryValidPlayerText(
                [notice])) {
            return false;
        }

        receipt = new CharacterNoteSaveReceipt(notice, bodyUtf8Bytes);
        return true;
    }

    private static string RenderBody(
        IReadOnlyList<CharacterNoteAppliedMemo> memos
    ) {
        var builder = new StringBuilder();
        _ = builder.Append("Galatea runtime 已将以下 ")
            .Append(memos.Count.ToString(CultureInfo.InvariantCulture))
            .Append(" 条 Note 原文成功保存到默认MemoPod。\n\n")
            .Append("本回执只证明以下原文已保存；不承诺分类、metadata补全或召回。\n\n")
            .Append("已保存的 Note 原文：");

        for (int index = 0; index < memos.Count; index++) {
            _ = builder.Append("\n\n")
                .Append((index + 1).ToString(
                    CultureInfo.InvariantCulture
                ))
                .Append(".\n")
                .Append(AdaptiveMarkdownFenceRenderer.RenderBlock(
                    ExactTextInfoString,
                    memos[index].ExactText
                ));
        }
        return builder.ToString();
    }

    private static int RequireExactText(string? value) {
        if (string.IsNullOrWhiteSpace(value)) {
            throw new ArgumentException(
                "Character Note save receipt exact text must not be blank.",
                "memos"
            );
        }
        try {
            int utf8Bytes = TextExtractorUtf8.GetByteCount(value);
            if (utf8Bytes
                    > CharacterNoteBounds.MaximumExactTextUtf8Bytes) {
                throw new ArgumentOutOfRangeException(
                    "memos",
                    "Character Note save receipt exact text exceeds its UTF-8 byte limit."
                );
            }
            return utf8Bytes;
        }
        catch (EncoderFallbackException exception) {
            throw new ArgumentException(
                "Character Note save receipt exact text must contain valid Unicode.",
                "memos",
                exception
            );
        }
    }
}

/// <summary>
/// Bounded, caller-serialized FIFO for pending save receipts. Overflow returns
/// false so the caller can drop the newest receipt without changing the
/// completed durable Memo effect.
/// </summary>
internal sealed class CharacterNoteSaveReceiptQueue {
    internal const int MaximumPendingCount = 16;
    internal const int MaximumPendingUtf8Bytes = 4 * 1024 * 1024;

    private readonly int _maximumCount;
    private readonly int _maximumUtf8Bytes;
    private readonly Queue<CharacterNoteSaveReceipt> _pending = new();
    private int _totalUtf8Bytes;

    internal CharacterNoteSaveReceiptQueue(
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

    internal bool TryEnqueue(CharacterNoteSaveReceipt receipt) {
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
        [NotNullWhen(true)] out CharacterNoteSaveReceipt? receipt
    ) {
        if (!_pending.TryDequeue(out receipt)) { return false; }
        _totalUtf8Bytes -= receipt.Utf8Bytes;
        return true;
    }
}
