using System.Reflection;
using Atelia.Rbf;
using Xunit;

namespace Atelia.StateJournal.Tests;

partial class RevisionTests {
    private static int GetMixedDequeDurableRefCount(DurableDeque deque) {
        var field = deque.GetType().GetField("_durableRefCount", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return Assert.IsType<int>(field!.GetValue(deque));
    }

    [Fact]
    public void MixedDeque_DurableRefCount_TracksOverwriteAndRemove() {
        var rev = CreateRevision();
        var deque = rev.CreateDeque();
        var child1 = rev.CreateDict<int, int>();
        var child2 = rev.CreateDict<int, int>();

        deque.PushBack(child1);
        deque.PushBack(123);
        Assert.Equal(1, GetMixedDequeDurableRefCount(deque));

        Assert.True(deque.TrySetAt<int>(0, 456));
        Assert.Equal(0, GetMixedDequeDurableRefCount(deque));

        Assert.True(deque.TrySetAt<DurableDict<int, int>>(1, child2));
        Assert.Equal(1, GetMixedDequeDurableRefCount(deque));

        Assert.True(deque.TrySetBack<DurableDict<int, int>>(null));
        Assert.Equal(0, GetMixedDequeDurableRefCount(deque));

        deque.PushFront(child1);
        Assert.Equal(1, GetMixedDequeDurableRefCount(deque));

        Assert.Equal(GetIssue.None, deque.PopFront<DurableObject>(out var popped));
        Assert.Same(child1, popped);
        Assert.Equal(0, GetMixedDequeDurableRefCount(deque));
    }

    [Fact]
    public void PushBack_DurableObjectFromDifferentRevision_InTypedDurObjDeque_Throws() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev1 = CreateRevision();
        var ownerDeque = rev1.CreateDeque<DurableDict<int, double>>();
        var childSameRevision = rev1.CreateDict<int, double>();
        ownerDeque.PushBack(childSameRevision); // same revision should pass

        var rev2 = CreateRevision();
        var childForeignRevision = rev2.CreateDict<int, double>();

        Assert.Throws<InvalidOperationException>(() => ownerDeque.PushBack(childForeignRevision));
    }

    [Fact]
    public void SetAt_DurableObjectFromDifferentRevision_InTypedDurObjDeque_Throws() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev1 = CreateRevision();
        var ownerDeque = rev1.CreateDeque<DurableDict<int, double>>();
        var childSameRevision = rev1.CreateDict<int, double>();
        ownerDeque.PushBack(childSameRevision);

        var rev2 = CreateRevision();
        var childForeignRevision = rev2.CreateDict<int, double>();

        Assert.Throws<InvalidOperationException>(() => ownerDeque.TrySetAt(0, childForeignRevision));
    }

    [Fact]
    public void TypedDurObjDeque_NullElement_RoundTripsAsNull() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDeque<DurableDict<int, double>>();
        root.PushBack(null);

        Assert.Equal(GetIssue.None, root.GetAt(0, out DurableDict<int, double>? beforeCommitValue));
        Assert.Null(beforeCommitValue);
        Assert.Equal(GetIssue.None, root.PeekFront(out DurableDict<int, double>? beforeCommitFront));
        Assert.Null(beforeCommitFront);

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file));
        var openResult = OpenRevision(outcome.HeadCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loadedRoot = Assert.IsAssignableFrom<DurableDeque<DurableDict<int, double>>>(openResult.Value!.GraphRoot);
        Assert.Equal(1, loadedRoot.Count);
        Assert.Equal(GetIssue.None, loadedRoot.GetAt(0, out DurableDict<int, double>? afterLoadValue));
        Assert.Null(afterLoadValue);
        Assert.Equal(GetIssue.None, loadedRoot.PeekFront(out DurableDict<int, double>? afterLoadFront));
        Assert.Null(afterLoadFront);
        Assert.True(loadedRoot.TryPopFront(out DurableDict<int, double>? popped));
        Assert.Null(popped);
    }

    [Fact]
    public void Commit_WithDurObjDequeChildRefs_CompactionRewritesCorrectly() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDeque<DurableDict<int, int>>();

        const int totalChildren = 100;
        var children = new DurableDict<int, int>[totalChildren];
        for (int i = 0; i < totalChildren; i++) {
            children[i] = rev.CreateDict<int, int>();
            children[i].Upsert(i, i * 100);
            root.PushBack(children[i]);
        }

        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        for (int i = 0; i < 80; i++) {
            Assert.True(root.TryPopFront(out DurableDict<int, int>? removedChild));
            Assert.Same(children[i], removedChild);
        }

        CommitTicket lastCommitTicket = default;
        for (int round = 0; round < 10; round++) {
            lastCommitTicket = AssertHeadCommitTicket(CommitToFile(rev, root, file), $"Commit round {round}");
        }

        Assert.Equal(20, root.Count);
        for (int i = 80; i < totalChildren; i++) {
            var child = children[i];
            Assert.NotEqual(DurableState.Detached, child.State);
            Assert.Equal(GetIssue.None, child.Get(i, out int value));
            Assert.Equal(i * 100, value);
        }

        var openResult = OpenRevision(lastCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loadedRoot = Assert.IsAssignableFrom<DurableDeque<DurableDict<int, int>>>(openResult.Value!.GraphRoot);
        Assert.Equal(20, loadedRoot.Count);
        for (int i = 80; i < totalChildren; i++) {
            Assert.True(loadedRoot.TryPopFront(out DurableDict<int, int>? loadedChild));
            Assert.Equal(GetIssue.None, loadedChild!.Get(i, out int value));
            Assert.Equal(i * 100, value);
        }
    }

    [Fact]
    public void Commit_WithMixedDequeChildRefs_CompactionRewritesCorrectly() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDeque();

        const int totalChildren = 100;
        var children = new DurableDict<int, int>[totalChildren];
        for (int i = 0; i < totalChildren; i++) {
            children[i] = rev.CreateDict<int, int>();
            children[i].Upsert(i, i * 10);
            root.PushBack(children[i]);
        }

        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        for (int i = 0; i < 80; i++) {
            Assert.True(root.TryPopFront(out DurableObject? removedChild));
            Assert.Same(children[i], removedChild);
        }

        CommitTicket lastCommitTicket = default;
        for (int round = 0; round < 10; round++) {
            lastCommitTicket = AssertHeadCommitTicket(CommitToFile(rev, root, file), $"Commit round {round}");
        }

        Assert.Equal(20, root.Count);
        for (int i = 80; i < totalChildren; i++) {
            var child = children[i];
            Assert.NotEqual(DurableState.Detached, child.State);
            Assert.Equal(GetIssue.None, child.Get(i, out int value));
            Assert.Equal(i * 10, value);
        }

        var openResult = OpenRevision(lastCommitTicket, file);
        Assert.True(openResult.IsSuccess, $"Open failed: {openResult.Error}");

        var loadedRoot = Assert.IsAssignableFrom<DurableDeque>(openResult.Value!.GraphRoot);
        Assert.Equal(20, loadedRoot.Count);
        for (int i = 80; i < totalChildren; i++) {
            Assert.True(loadedRoot.TryPopFront(out DurableObject? loadedChildObj));
            var loadedChild = Assert.IsAssignableFrom<DurableDict<int, int>>(loadedChildObj);
            Assert.Equal(GetIssue.None, loadedChild.Get(i, out int value));
            Assert.Equal(i * 10, value);
        }
    }
}
