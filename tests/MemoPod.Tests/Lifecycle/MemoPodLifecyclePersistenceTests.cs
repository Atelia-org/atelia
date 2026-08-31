using System.Text;
using Atelia.MemoPod;

namespace Atelia.MemoPod.Tests.Lifecycle;

public sealed class MemoPodLifecyclePersistenceTests : IDisposable {
    private static readonly MemoPodId PodId = MemoPodId.Parse(
        "99999999999999999999999999999999"
    );

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "atelia-memo-pod-lifecycle-persistence-tests",
        Guid.NewGuid().ToString("N")
    );

    public MemoPodLifecyclePersistenceTests() {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void PublicPersistenceFailuresUseCoarseStableKinds() {
        string absentRoot = Path.Combine(_root, "absent");
        MemoPodPersistenceException rootAbsent =
            Assert.Throws<MemoPodPersistenceException>(() =>
                MemoPod.Create(absentRoot, PodId, "topic"));
        Assert.Equal(
            MemoPodPersistenceFailureKind.NotFound,
            rootAbsent.FailureKind
        );

        MemoPodPersistenceException documentAbsent =
            Assert.Throws<MemoPodPersistenceException>(() =>
                MemoPod.Open(_root, PodId));
        Assert.Equal(
            MemoPodPersistenceFailureKind.NotFound,
            documentAbsent.FailureKind
        );

        string target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        string linkedRoot = Path.Combine(_root, "linked-root");
        Directory.CreateSymbolicLink(linkedRoot, target);
        MemoPodPersistenceException unsafePath =
            Assert.Throws<MemoPodPersistenceException>(() =>
                MemoPod.Create(linkedRoot, PodId, "topic"));
        Assert.Equal(
            MemoPodPersistenceFailureKind.UnsafePath,
            unsafePath.FailureKind
        );
    }

    [Fact]
    public async Task StrictOpenMapsMalformedDocumentToInvalidDocument() {
        MemoPod pod = MemoPod.Create(_root, PodId, "topic");
        pod.Append("memo");
        await pod.FreezeAsync();
        MemoPodStorePaths paths = MemoPodStoreLayout.Resolve(_root, PodId);
        File.WriteAllText(paths.DocumentPath, "{}");

        MemoPodPersistenceException failure =
            Assert.Throws<MemoPodPersistenceException>(() =>
                MemoPod.Open(_root, PodId));

        Assert.Equal(
            MemoPodPersistenceFailureKind.InvalidDocument,
            failure.FailureKind
        );
        Assert.IsType<MemoPodDocumentFormatException>(
            failure.InnerException
        );
    }

    [Fact]
    public async Task GapsCorrectionAndAllocatorHighWaterRoundTrip() {
        MemoPod pod = MemoPod.Create(_root, PodId, "topic");
        MemoId oldId = pod.Append("old fact");
        MemoId removedGap = pod.Append("temporary");
        MemoId retained = pod.Append("retained");
        pod.Remove(removedGap);
        await pod.FreezeAsync();

        MemoPod edit = MemoPod.Open(_root, PodId);
        Assert.Equal(
            [oldId, retained],
            edit.List().Select(static memo => memo.Id).ToArray()
        );
        edit.ResumeEditing();
        edit.Remove(oldId);
        MemoId corrected = edit.Append("corrected fact");
        Assert.Equal("m1:00000004", corrected.Value);
        await edit.FreezeAsync();

        MemoPod reopened = MemoPod.Open(_root, PodId);
        Assert.False(reopened.TryGet(oldId, out _));
        Assert.False(reopened.TryGet(removedGap, out _));
        Assert.Equal(
            [retained, corrected],
            reopened.List().Select(static memo => memo.Id).ToArray()
        );
        Assert.Equal("corrected fact", reopened.Get(corrected).ExactText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorrectionPreparationFailurePreservesExactWorkingSetAndRetries(
        bool cancelBeforeSettlementFence
    ) {
        MemoPod created = MemoPod.Create(_root, PodId, "topic");
        MemoId oldId = created.Append("old fact");
        Assert.Equal("m1:00000001", oldId.Value);
        await created.FreezeAsync();
        AssertCorrectionDocument(
            MemoPodDocumentStore.Read(_root, PodId),
            oldId,
            "old fact",
            expectedNextMemoOrdinal: 2
        );

        bool failFirstPublish = true;
        using var cancellation = new CancellationTokenSource();
        MemoPod edit = MemoPod.OpenForTesting(
            _root,
            PodId,
            new MemoPodLifecycleTestHooks(
                PublisherHooks: new MemoPodPublisherTestHooks(
                    BeforePublish: _ => {
                        if (!failFirstPublish) { return; }
                        failFirstPublish = false;
                        if (cancelBeforeSettlementFence) {
                            cancellation.Cancel();
                            return;
                        }
                        throw new IOException("correction prepublish fixture");
                    }
                )
            )
        );
        edit.ResumeEditing();
        edit.Remove(oldId);
        MemoId newId = edit.Append("new fact");
        Assert.Equal("m1:00000002", newId.Value);

        if (cancelBeforeSettlementFence) {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => edit.FreezeAsync(cancellation.Token)
            );
        }
        else {
            MemoPodPersistenceException failure =
                await Assert.ThrowsAsync<MemoPodPersistenceException>(
                    () => edit.FreezeAsync()
                );
            Assert.Equal(
                MemoPodPersistenceFailureKind.IoFailure,
                failure.FailureKind
            );
        }

        Assert.Equal(MemoPodPhase.Editable, edit.Phase);
        Assert.False(edit.TryGet(oldId, out _));
        Memo working = Assert.Single(edit.List());
        Assert.Equal(newId, working.Id);
        Assert.Equal("new fact", working.ExactText);
        AssertCorrectionDocument(
            MemoPodDocumentStore.Read(_root, PodId),
            oldId,
            "old fact",
            expectedNextMemoOrdinal: 2
        );

        await edit.FreezeAsync();

        Assert.Equal(MemoPodPhase.Frozen, edit.Phase);
        MemoPod reopened = MemoPod.Open(_root, PodId);
        Assert.False(reopened.TryGet(oldId, out _));
        Assert.Equal("new fact", reopened.Get(newId).ExactText);
        AssertCorrectionDocument(
            MemoPodDocumentStore.Read(_root, PodId),
            newId,
            "new fact",
            expectedNextMemoOrdinal: 3
        );

        reopened.ResumeEditing();
        Assert.Equal(
            "m1:00000003",
            reopened.Append("following fact").Value
        );
    }

    [Fact]
    public async Task ProvisionalIdsMayDisappearButCommittedIdsAreNotReused() {
        MemoPod abandoned = MemoPod.Create(_root, PodId, "topic");
        MemoId provisional = abandoned.Append("never committed");
        Assert.Equal("m1:00000001", provisional.Value);
        Assert.Equal(
            MemoPodPersistenceFailureKind.NotFound,
            Assert.Throws<MemoPodPersistenceException>(() =>
                MemoPod.Open(_root, PodId)).FailureKind
        );

        MemoPod replacement = MemoPod.Create(_root, PodId, "topic");
        MemoId committed = replacement.Append("committed");
        Assert.Equal(provisional, committed);
        await replacement.FreezeAsync();

        MemoPod edit = MemoPod.Open(_root, PodId);
        edit.ResumeEditing();
        edit.Remove(committed);
        MemoId next = edit.Append("next");
        Assert.Equal("m1:00000002", next.Value);
        await edit.FreezeAsync();

        Assert.Equal(
            next,
            Assert.Single(MemoPod.Open(_root, PodId).List()).Id
        );
    }

    [Fact]
    public async Task DoubleRemoveIsAtomicAndDoesNotDisturbAllocator() {
        MemoPod pod = MemoPod.Create(_root, PodId, "topic");
        MemoId first = pod.Append("first");
        MemoId second = pod.Append("second");
        await pod.FreezeAsync();

        pod.ResumeEditing();
        pod.Remove(first);
        Assert.Throws<KeyNotFoundException>(() => pod.Remove(first));
        Assert.Equal(second, Assert.Single(pod.List()).Id);
        MemoId third = pod.Append("third");
        Assert.Equal("m1:00000003", third.Value);
        await pod.FreezeAsync();

        Assert.Equal(
            [second, third],
            MemoPod.Open(_root, PodId).List()
                .Select(static memo => memo.Id)
                .ToArray()
        );
    }

    [Fact]
    public void MaximumLogicalV2DocumentCanPublishOpenAndWorstCaseRender() {
        const int jsonWorstCaseExpansion = 6;
        const int memoLineBytesExcludingText = 37;
        string topic = new('"', MemoPodLimits.MaximumTopicUtf8Bytes);
        int exactTextBytesPerMemo =
            MemoPodLimits.MaximumActiveExactTextUtf8Bytes
            / MemoPodLimits.MaximumActiveMemoCount;
        string text = new('\0', exactTextBytesPerMemo);
        Memo[] memos = Enumerable.Range(
                1,
                MemoPodLimits.MaximumActiveMemoCount
            )
            .Select(ordinal => new Memo(
                MemoId.FromOrdinal(checked((uint)ordinal)),
                text
            ))
            .ToArray();
        var document = new MemoPodDocument(
            PodId,
            topic,
            checked((ulong)memos.Length + 1),
            memos
        );
        MemoPodPublishResult publish = MemoPodDocumentPublisher.Publish(
            _root,
            document,
            MemoPodPublishMode.CreateNew
        );
        Assert.Equal(
            MemoPodPublishSettlement.Published,
            publish.Settlement
        );
        MemoPodStorePaths paths = MemoPodStoreLayout.Resolve(_root, PodId);
        long documentLength = new FileInfo(paths.DocumentPath).Length;
        Assert.InRange(
            documentLength,
            (long)jsonWorstCaseExpansion
                * MemoPodLimits.MaximumActiveExactTextUtf8Bytes,
            MemoPodLimits.MaximumDocumentUtf8Bytes
        );
        using (var stream = new FileStream(
            paths.DocumentPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read
        )) {
            byte[] prefix = new byte[64 * 1024];
            stream.ReadExactly(prefix);
            Assert.Contains(
                "\\u0000",
                Encoding.UTF8.GetString(prefix),
                StringComparison.Ordinal
            );
        }

        MemoPod pod = MemoPod.Open(_root, PodId);
        Memo[] reopened = pod.List().ToArray();
        string headerWithoutTopic =
            """
            {"schema":"atelia.memo-pod.prompt.v3","pod_id":"99999999999999999999999999999999","topic":""}
            """ + "\n";
        int expectedPromptLength = checked(
            Encoding.UTF8.GetByteCount(headerWithoutTopic)
            + (2 * MemoPodLimits.MaximumTopicUtf8Bytes)
            + MemoPodLimits.MaximumActiveMemoCount
                * (memoLineBytesExcludingText
                    + jsonWorstCaseExpansion * exactTextBytesPerMemo)
        );

        Assert.Equal(topic, pod.Topic);
        Assert.Equal(
            MemoPodLimits.MaximumTopicUtf8Bytes,
            Encoding.UTF8.GetByteCount(pod.Topic)
        );
        Assert.Equal(MemoPodLimits.MaximumActiveMemoCount, reopened.Length);
        Assert.All(reopened, memo => Assert.Equal(
            exactTextBytesPerMemo,
            memo.ExactTextUtf8ByteCount
        ));
        Assert.Equal(
            MemoPodLimits.MaximumActiveExactTextUtf8Bytes,
            reopened.Sum(static memo => memo.ExactTextUtf8ByteCount)
        );
        Assert.Equal(expectedPromptLength, pod.FrozenPrompt.Utf8Length);
        Assert.True(
            pod.FrozenPrompt.Utf8Length
                <= MemoPodLimits.MaximumRenderedPromptUtf8Bytes
        );
    }

    public void Dispose() {
        if (Directory.Exists(_root)) {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static void AssertCorrectionDocument(
        MemoPodDocument document,
        MemoId expectedId,
        string expectedExactText,
        ulong expectedNextMemoOrdinal
    ) {
        Assert.Equal(expectedNextMemoOrdinal, document.NextMemoOrdinal);
        Memo memo = Assert.Single(document.Memos);
        Assert.Equal(expectedId, memo.Id);
        Assert.Equal(expectedExactText, memo.ExactText);
    }
}
