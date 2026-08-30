using System.Text;
using Atelia.Galatea.Server.CharacterMemory;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class CharacterNoteRequestReceiptTests {
    [Fact]
    public void TryCreate_RendersOneHonestFrozenReceiptWithoutEvidence() {
        const string ExactText = "第一行\n~~~~\n最后一行\n";
        const string Evidence = "她完成提交development Note保存请求。";

        Assert.True(CharacterNoteRequestReceipt.TryCreate(
            [new CharacterNoteIntent(ExactText, Evidence)],
            out CharacterNoteRequestReceipt? receipt
        ));

        Assert.Equal(
            Encoding.UTF8.GetByteCount(receipt.Notice.Body),
            receipt.Utf8Bytes
        );
        Assert.Contains(
            "Galatea runtime 已识别到 1 条 Note 请求。",
            receipt.Notice.Body,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "Memo 持久化尚未实现",
            receipt.Notice.Body,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "本回执不表示这些 Note 已经保存。",
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
            Evidence,
            receipt.Notice.Body,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void TryCreate_PreservesMultipleIntentOrderAndRejectsEmpty() {
        Assert.False(CharacterNoteRequestReceipt.TryCreate(
            Array.Empty<CharacterNoteIntent>(),
            out CharacterNoteRequestReceipt? empty
        ));
        Assert.Null(empty);

        Assert.True(CharacterNoteRequestReceipt.TryCreate(
            [
                new CharacterNoteIntent("first", "evidence first"),
                new CharacterNoteIntent("second\n", "evidence second")
            ],
            out CharacterNoteRequestReceipt? receipt
        ));

        Assert.Contains(
            "Galatea runtime 已识别到 2 条 Note 请求。",
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
    public void TryCreate_RejectsFenceExpansionBeyondReceiptBudget() {
        CharacterNoteIntent[] intents = Enumerable.Range(0, 4)
            .Select(static index => new CharacterNoteIntent(
                new string(
                    '~',
                    CharacterNoteBounds.MaximumExactTextUtf8Bytes
                ),
                $"evidence-{index}"
            ))
            .ToArray();

        Assert.False(CharacterNoteRequestReceipt.TryCreate(
            intents,
            out CharacterNoteRequestReceipt? receipt
        ));
        Assert.Null(receipt);
    }

    [Fact]
    public void Queue_IsFifoAndDropsNewestAtCountBound() {
        CharacterNoteRequestReceipt first = Create("first");
        CharacterNoteRequestReceipt second = Create("second");
        CharacterNoteRequestReceipt dropped = Create("third");
        var queue = new CharacterNoteRequestReceiptQueue(
            maximumCount: 2,
            maximumUtf8Bytes: 1024 * 1024
        );

        Assert.True(queue.TryEnqueue(first));
        Assert.True(queue.TryEnqueue(second));
        Assert.False(queue.TryEnqueue(dropped));
        Assert.Equal(2, queue.Count);
        Assert.Equal(
            first.Utf8Bytes + second.Utf8Bytes,
            queue.TotalUtf8Bytes
        );

        Assert.True(queue.TryDequeue(out CharacterNoteRequestReceipt? one));
        Assert.Same(first, one);
        Assert.True(queue.TryDequeue(out CharacterNoteRequestReceipt? two));
        Assert.Same(second, two);
        Assert.False(queue.TryDequeue(out CharacterNoteRequestReceipt? none));
        Assert.Null(none);
        Assert.Equal(0, queue.Count);
        Assert.Equal(0, queue.TotalUtf8Bytes);
    }

    [Fact]
    public void Queue_DropsNewestAtTotalByteBoundWithoutMutation() {
        CharacterNoteRequestReceipt first = Create("first");
        CharacterNoteRequestReceipt second = Create("second");
        var queue = new CharacterNoteRequestReceiptQueue(
            maximumCount: CharacterNoteRequestReceiptQueue
                .MaximumPendingCount,
            maximumUtf8Bytes:
                first.Utf8Bytes + second.Utf8Bytes - 1
        );

        Assert.True(queue.TryEnqueue(first));
        Assert.False(queue.TryEnqueue(second));
        Assert.Equal(1, queue.Count);
        Assert.Equal(first.Utf8Bytes, queue.TotalUtf8Bytes);

        Assert.True(queue.TryDequeue(out CharacterNoteRequestReceipt? item));
        Assert.Same(first, item);
        Assert.True(queue.TryEnqueue(second));
        Assert.Equal(second.Utf8Bytes, queue.TotalUtf8Bytes);
        Assert.Equal(16,
            CharacterNoteRequestReceiptQueue.MaximumPendingCount);
        Assert.Equal(4 * 1024 * 1024,
            CharacterNoteRequestReceiptQueue.MaximumPendingUtf8Bytes);
    }

    private static CharacterNoteRequestReceipt Create(string exactText) {
        Assert.True(CharacterNoteRequestReceipt.TryCreate(
            [new CharacterNoteIntent(exactText, "completed request submission")],
            out CharacterNoteRequestReceipt? receipt
        ));
        return receipt;
    }
}
