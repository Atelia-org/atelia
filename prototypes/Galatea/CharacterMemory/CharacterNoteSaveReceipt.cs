using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Atelia.SessionJournal;

namespace Atelia.Galatea.Server.CharacterMemory;

/// <summary>
/// Code-owned saved-Note notification. The V3 outbox freezes this payload in
/// the same transaction that durably settles a newly Applied capture.
/// </summary>
internal sealed class CharacterNoteSaveReceipt {
    private static readonly PlayerTurnNotice.Reply MaximumRenderedReply = new(
        new string('~', PlayerTurnObservationEnvelope.MaximumReplyUtf8Bytes)
    );
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

    // The durable outbox cannot drop a successfully saved batch just because
    // adaptive fences make its exact-text receipt exceed an Observation budget.
    internal static CharacterNoteSaveReceipt CreateDurable(IReadOnlyList<CharacterNoteAppliedMemo> memos) {
        if (TryCreate(memos, out CharacterNoteSaveReceipt? receipt)
            && PlayerTurnObservationEnvelope.FitsEveryValidPlayerText([MaximumRenderedReply, receipt.Notice])) {
            return receipt;
        }
        if (memos.Count == 0) { throw new ArgumentException("A durable receipt requires saved memos.", nameof(memos)); }
        string body = "Galatea runtime 已将以下 Note 原文成功保存到默认MemoPod。\n"
            + "本回执只证明原文已保存；不承诺分类、metadata补全或召回。\n"
            + "原文超出回执展示预算，以下仅列出保存标识：\n"
            + "Source Action: " + memos[0].SourceActionAddress + "\n"
            + string.Join("\n", memos.Select(static memo => "Memo: " + memo.MemoId.Value));
        int bytes = TextExtractorUtf8.GetByteCount(body);
        var notice = new PlayerTurnNotice.NoteSaveReceipt(body);
        if (bytes > PlayerTurnObservationEnvelope.MaximumNoteSaveReceiptUtf8Bytes
            || !PlayerTurnObservationEnvelope.FitsEveryValidPlayerText([MaximumRenderedReply, notice])) {
            throw new InvalidDataException("Compact durable Note receipt exceeds its budget.");
        }
        return new CharacterNoteSaveReceipt(notice, bytes);
    }

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
