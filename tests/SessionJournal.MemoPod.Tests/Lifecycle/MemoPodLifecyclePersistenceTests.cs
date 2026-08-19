using Atelia.SessionJournal.MemoPod;

namespace Atelia.SessionJournal.MemoPod.Tests.Lifecycle;

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
    public void MaximumLogicalV1DocumentCanOpenAndRender() {
        string text = new('x', 1024);
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
            "maximum logical fixture",
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

        MemoPod pod = MemoPod.Open(_root, PodId);

        Assert.Equal(MemoPodLimits.MaximumActiveMemoCount, pod.List().Length);
        Assert.InRange(
            pod.FrozenPrompt.Utf8Length,
            1,
            MemoPodLimits.MaximumRenderedPromptUtf8Bytes
        );
    }

    public void Dispose() {
        if (Directory.Exists(_root)) {
            Directory.Delete(_root, recursive: true);
        }
    }
}
