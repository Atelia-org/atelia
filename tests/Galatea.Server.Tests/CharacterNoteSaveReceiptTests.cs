using System.Text;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.MemoPod;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class CharacterNoteSaveReceiptTests {
    [Fact]
    public void TryCreate_RendersTruthfulFrozenReceiptFromDurableMemos() {
        const string ExactText = "第一行\n~~~~\n最后一行\n";

        Assert.True(CharacterNoteSaveReceipt.TryCreate(
            [Memo(0, ExactText)],
            out CharacterNoteSaveReceipt? receipt
        ));

        Assert.Equal(
            Encoding.UTF8.GetByteCount(receipt.Notice.Body),
            receipt.Utf8Bytes
        );
        Assert.Contains(
            "Galatea runtime 已将以下 1 条 Note 原文成功保存到默认MemoPod。",
            receipt.Notice.Body,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "本回执只证明以下原文已保存；不承诺分类、metadata补全或召回。",
            receipt.Notice.Body,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "1.\n~~~~~character-note-exact-text\n"
                + ExactText + "~~~~~",
            receipt.Notice.Body,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "Evidence",
            receipt.Notice.Body,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void TryCreate_PreservesDurableOrderAndRejectsEmpty() {
        Assert.False(CharacterNoteSaveReceipt.TryCreate(
            Array.Empty<CharacterNoteAppliedMemo>(),
            out CharacterNoteSaveReceipt? empty
        ));
        Assert.Null(empty);

        Assert.True(CharacterNoteSaveReceipt.TryCreate(
            [
                Memo(0, "first"),
                Memo(1, "second\n"),
            ],
            out CharacterNoteSaveReceipt? receipt
        ));

        Assert.Contains(
            "以下 2 条 Note 原文成功保存到默认MemoPod",
            receipt.Notice.Body,
            StringComparison.Ordinal
        );
        int first = receipt.Notice.Body.IndexOf(
            "1.\n~~~~character-note-exact-text\nfirst\n~~~~",
            StringComparison.Ordinal
        );
        int second = receipt.Notice.Body.IndexOf(
            "2.\n~~~~character-note-exact-text\nsecond\n~~~~",
            StringComparison.Ordinal
        );
        Assert.True(first >= 0);
        Assert.True(second > first);
    }

    [Fact]
    public void TryCreate_RejectsNonDefaultOrDisorderedDurableMemos() {
        CharacterNoteAppliedMemo wrongPod = Memo(0, "first") with {
            PodId = MemoPodId.Parse("00000000000000000000000000000002")
        };
        Assert.Throws<ArgumentException>(() =>
            CharacterNoteSaveReceipt.TryCreate(
                [wrongPod],
                out CharacterNoteSaveReceipt? _
            )
        );
        Assert.Throws<ArgumentException>(() =>
            CharacterNoteSaveReceipt.TryCreate(
                [Memo(1, "first")],
                out CharacterNoteSaveReceipt? _
            )
        );
    }

    [Fact]
    public void TryCreate_RejectsFenceExpansionBeyondReceiptBudget() {
        CharacterNoteAppliedMemo[] memos = Enumerable.Range(0, 4)
            .Select(index => Memo(
                index,
                new string(
                    '~',
                    CharacterNoteBounds.MaximumExactTextUtf8Bytes
                )
            ))
            .ToArray();

        Assert.False(CharacterNoteSaveReceipt.TryCreate(
            memos,
            out CharacterNoteSaveReceipt? receipt
        ));
        Assert.Null(receipt);
    }

    private static CharacterNoteAppliedMemo Memo(
        int ordinal,
        string exactText
    ) => new(
        "0000000100000001",
        ordinal,
        CharacterNoteDefaultPodV1.PodId,
        MemoId.Parse("m1:" + (ordinal + 1).ToString("x8")),
        exactText
    );
}
