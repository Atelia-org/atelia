using Atelia.Rbf;
using Atelia.StateJournal.Internal;
using Xunit;

namespace Atelia.StateJournal.Tests;

public partial class RevisionTests {
    private const int ByteStringDeltaBaselineCount = 32;

    private readonly record struct ByteStringDeltaChain(
        CommitTicket Head,
        LocalId DictId,
        LocalId DequeId,
        LocalId OrderedId,
        LocalId SetId
    );

    [Fact]
    public void TypedByteString_CoreContainers_SecondCommitWritesDeltaAndReopens() {
        var path = GetTempFilePath();
        ByteStringDeltaChain chain = WriteByteStringDeltaChain(path);

        using var reopenedFile = RbfFile.OpenExisting(path);
        Revision reopened = AssertSuccess(OpenRevision(chain.Head, reopenedFile));

        DurableDict<ByteString, ByteString> reopenedDict =
            Assert.IsAssignableFrom<DurableDict<ByteString, ByteString>>(AssertSuccess(reopened.Load(chain.DictId)));
        Assert.Equal(ByteStringDeltaBaselineCount, reopenedDict.Count);
        Assert.Equal(DeltaValue(99), reopenedDict.GetOrThrow(DeltaKey(0)));
        Assert.Equal(DeltaValue(31), reopenedDict.GetOrThrow(DeltaKey(31)));

        DurableDeque<ByteString> reopenedDeque =
            Assert.IsAssignableFrom<DurableDeque<ByteString>>(AssertSuccess(reopened.Load(chain.DequeId)));
        Assert.Equal(ByteStringDeltaBaselineCount, reopenedDeque.Count);
        Assert.Equal(GetIssue.None, reopenedDeque.GetAt(0, out ByteString reopenedFront));
        Assert.Equal(DeltaValue(99), reopenedFront);
        Assert.Equal(GetIssue.None, reopenedDeque.PeekBack(out ByteString reopenedBack));
        Assert.Equal(DeltaValue(31), reopenedBack);

        DurableOrderedDict<ByteString, ByteString> reopenedOrdered =
            Assert.IsAssignableFrom<DurableOrderedDict<ByteString, ByteString>>(AssertSuccess(reopened.Load(chain.OrderedId)));
        Assert.Equal(ByteStringDeltaBaselineCount, reopenedOrdered.Count);
        Assert.True(reopenedOrdered.TryGet(DeltaKey(1), out ByteString updatedOrderedValue));
        Assert.Equal(DeltaValue(99), updatedOrderedValue);
        Assert.True(reopenedOrdered.TryGet(DeltaKey(31), out ByteString orderedValue));
        Assert.Equal(DeltaValue(31), orderedValue);

        DurableHashSet<ByteString> reopenedSet =
            Assert.IsAssignableFrom<DurableHashSet<ByteString>>(AssertSuccess(reopened.Load(chain.SetId)));
        Assert.Equal(ByteStringDeltaBaselineCount - 1, reopenedSet.Count);
        Assert.False(reopenedSet.Contains(DeltaKey(2)));
        Assert.True(reopenedSet.Contains(DeltaKey(31)));
    }

    private static ByteStringDeltaChain WriteByteStringDeltaChain(string path) {
        using var file = RbfFile.CreateNew(path);

        Revision revision = CreateRevision();
        DurableDict<string> root = revision.CreateDict<string>();
        DurableDict<ByteString, ByteString> dict = revision.CreateDict<ByteString, ByteString>();
        DurableDeque<ByteString> deque = revision.CreateDeque<ByteString>();
        DurableOrderedDict<ByteString, ByteString> ordered = revision.CreateOrderedDict<ByteString, ByteString>();
        DurableHashSet<ByteString> set = revision.CreateHashSet<ByteString>();

        for (int i = 0; i < ByteStringDeltaBaselineCount; i++) {
            ByteString key = DeltaKey(i);
            ByteString value = DeltaValue(i);
            dict.Upsert(key, value);
            deque.PushBack(value);
            ordered.Upsert(key, value);
            set.Add(key);
        }

        root.Upsert("dict", dict);
        root.Upsert("deque", deque);
        root.Upsert("ordered", ordered);
        root.Upsert("set", set);

        _ = AssertCommitSucceeded(CommitToFile(revision, root, file), "Commit1");
        AssertUserPayloadVersion(file, dict, VersionKind.Rebase);
        AssertUserPayloadVersion(file, deque, VersionKind.Rebase);
        AssertUserPayloadVersion(file, ordered, VersionKind.Rebase);
        AssertUserPayloadVersion(file, set, VersionKind.Rebase);
        var dictRebaseHead = dict.HeadTicket;
        var dequeRebaseHead = deque.HeadTicket;
        var orderedRebaseHead = ordered.HeadTicket;
        var setRebaseHead = set.HeadTicket;

        Assert.Equal(UpsertStatus.Updated, dict.Upsert(DeltaKey(0), DeltaValue(99)));
        Assert.True(deque.TrySetAt(0, DeltaValue(99)));
        Assert.Equal(UpsertStatus.Updated, ordered.Upsert(DeltaKey(1), DeltaValue(99)));
        Assert.True(set.Remove(DeltaKey(2)));

        CommitOutcome second = AssertCommitSucceeded(CommitToFile(revision, root, file), "Commit2");
        AssertUserPayloadVersion(file, dict, VersionKind.Delta);
        AssertUserPayloadVersion(file, deque, VersionKind.Delta);
        AssertUserPayloadVersion(file, ordered, VersionKind.Delta);
        AssertUserPayloadVersion(file, set, VersionKind.Delta);
        Assert.NotEqual(dictRebaseHead, dict.HeadTicket);
        Assert.NotEqual(dequeRebaseHead, deque.HeadTicket);
        Assert.NotEqual(orderedRebaseHead, ordered.HeadTicket);
        Assert.NotEqual(setRebaseHead, set.HeadTicket);

        return new(second.HeadCommitTicket, dict.LocalId, deque.LocalId, ordered.LocalId, set.LocalId);
    }

    private static ByteString DeltaKey(int seed) {
        byte[] bytes = new byte[16];
        for (int i = 0; i < bytes.Length; i++) { bytes[i] = unchecked((byte)(seed + i)); }
        return new ByteString(bytes);
    }

    private static ByteString DeltaValue(int seed) {
        byte[] bytes = new byte[64];
        for (int i = 0; i < bytes.Length; i++) { bytes[i] = unchecked((byte)(seed * 7 + i)); }
        return new ByteString(bytes);
    }

    private static void AssertUserPayloadVersion(
        IRbfFile file,
        DurableObject durableObject,
        VersionKind expectedVersion
    ) {
        var frameInfo = file.ReadFrameInfo(durableObject.HeadTicket);
        Assert.True(frameInfo.IsSuccess, $"ReadFrameInfo failed for {durableObject.Kind}: {frameInfo.Error}");
        var tag = new FrameTag(frameInfo.Value.Tag);
        Assert.Equal(FrameUsage.UserPayload, tag.Usage);
        Assert.Equal(durableObject.Kind, tag.ObjectKind);
        Assert.Equal(FrameSource.PrimaryCommit, tag.Source);
        Assert.Equal(expectedVersion, tag.VersionKind);
    }
}
