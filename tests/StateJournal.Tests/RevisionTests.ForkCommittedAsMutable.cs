using Atelia.Rbf;
using Atelia.StateJournal.Internal;
using Xunit;

namespace Atelia.StateJournal.Tests;

partial class RevisionTests {
    [Fact]
    public void ForkCommittedAsMutable_CleanSource_RegistersForkWithoutWritingUserPayload() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, DurableDict<int, int>>();
        var template = rev.CreateDict<int, int>();
        template.Upsert(1, 10);
        root.Upsert(1, template);
        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");
        int typedPayloadFramesBefore = CountUserPayloadFrames(file, DurableObjectKind.TypedDict);

        var fork = template.ForkCommittedAsMutable();
        root.Upsert(2, fork);

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");
        int typedPayloadFramesAfter = CountUserPayloadFrames(file, DurableObjectKind.TypedDict);
        Assert.Equal(1, typedPayloadFramesAfter - typedPayloadFramesBefore);
        Assert.NotEqual(template.LocalId, fork.LocalId);
        Assert.False(fork.HasChanges);
        Assert.Equal(DurableState.Clean, fork.State);

        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var loadedRoot = Assert.IsAssignableFrom<DurableDict<int, DurableDict<int, int>>>(opened.GraphRoot);
        Assert.Equal(GetIssue.None, loadedRoot.Get(2, out DurableDict<int, int>? loadedFork));
        Assert.NotNull(loadedFork);
        Assert.Equal(fork.LocalId, loadedFork!.LocalId);
        Assert.Equal(GetIssue.None, loadedFork.Get(1, out int value));
        Assert.Equal(10, value);
    }

    [Fact]
    public void ForkCommittedAsMutable_DirtySource_CopiesCommittedStateOnly() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, DurableDict<int, int>>();
        var source = rev.CreateDict<int, int>();
        source.Upsert(1, 10);
        root.Upsert(1, source);
        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        source.Upsert(1, 20);
        var fork = source.ForkCommittedAsMutable();
        root.Upsert(2, fork);

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");
        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var loadedRoot = Assert.IsAssignableFrom<DurableDict<int, DurableDict<int, int>>>(opened.GraphRoot);

        Assert.Equal(GetIssue.None, loadedRoot.Get(1, out DurableDict<int, int>? loadedSource));
        Assert.Equal(GetIssue.None, loadedRoot.Get(2, out DurableDict<int, int>? loadedFork));
        Assert.Equal(GetIssue.None, loadedSource!.Get(1, out int sourceValue));
        Assert.Equal(GetIssue.None, loadedFork!.Get(1, out int forkValue));
        Assert.Equal(20, sourceValue);
        Assert.Equal(10, forkValue);
    }

    [Fact]
    public void ForkCommittedAsMutable_ModifyingFork_DoesNotModifySource() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, DurableDict<int, int>>();
        var source = rev.CreateDict<int, int>();
        source.Upsert(1, 10);
        root.Upsert(1, source);
        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        var fork = source.ForkCommittedAsMutable();
        fork.Upsert(1, 30);
        root.Upsert(2, fork);

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");
        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var loadedRoot = Assert.IsAssignableFrom<DurableDict<int, DurableDict<int, int>>>(opened.GraphRoot);

        Assert.Equal(GetIssue.None, loadedRoot.Get(1, out DurableDict<int, int>? loadedSource));
        Assert.Equal(GetIssue.None, loadedRoot.Get(2, out DurableDict<int, int>? loadedFork));
        Assert.Equal(GetIssue.None, loadedSource!.Get(1, out int sourceValue));
        Assert.Equal(GetIssue.None, loadedFork!.Get(1, out int forkValue));
        Assert.Equal(10, sourceValue);
        Assert.Equal(30, forkValue);
    }

    [Fact]
    public void ForkCommittedAsMutable_TransientSource_Throws() {
        var rev = CreateRevision();
        var source = rev.CreateDict<int, int>();
        source.Upsert(1, 10);

        Assert.Throws<InvalidOperationException>(() => source.ForkCommittedAsMutable());
    }

    [Fact]
    public void ForkCommittedAsMutable_MixedHeapValue_UsesIndependentBits64Slot() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, DurableDict<int>>();
        var source = rev.CreateDict<int>();
        source.Upsert(1, ulong.MaxValue);
        root.Upsert(1, source);
        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        var fork = source.ForkCommittedAsMutable();
        fork.Upsert(1, 42UL);
        root.Upsert(2, fork);

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");
        Assert.Equal(GetIssue.None, source.Get<ulong>(1, out ulong sourceValueBeforeOpen));
        Assert.Equal(ulong.MaxValue, sourceValueBeforeOpen);

        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var loadedRoot = Assert.IsAssignableFrom<DurableDict<int, DurableDict<int>>>(opened.GraphRoot);
        Assert.Equal(GetIssue.None, loadedRoot.Get(1, out DurableDict<int>? loadedSource));
        Assert.Equal(GetIssue.None, loadedRoot.Get(2, out DurableDict<int>? loadedFork));
        Assert.Equal(GetIssue.None, loadedSource!.Get<ulong>(1, out ulong sourceValue));
        Assert.Equal(GetIssue.None, loadedFork!.Get<ulong>(1, out ulong forkValue));
        Assert.Equal(ulong.MaxValue, sourceValue);
        Assert.Equal(42UL, forkValue);
    }

    [Fact]
    public void ForkCommittedAsMutable_DurableObjectValues_AreShallowReferences() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, DurableDict<int, DurableDict<int, int>>>();
        var source = rev.CreateDict<int, DurableDict<int, int>>();
        var child = rev.CreateDict<int, int>();
        child.Upsert(1, 10);
        source.Upsert(1, child);
        root.Upsert(1, source);
        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        var fork = source.ForkCommittedAsMutable();
        root.Remove(1);
        root.Upsert(2, fork);
        Assert.Equal(GetIssue.None, fork.Get(1, out DurableDict<int, int>? forkChild));
        Assert.Same(child, forkChild);

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");
        Assert.Equal(DurableState.Detached, source.State);
        Assert.NotEqual(DurableState.Detached, child.State);

        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var loadedRoot = Assert.IsAssignableFrom<DurableDict<int, DurableDict<int, DurableDict<int, int>>>>(opened.GraphRoot);
        Assert.Equal(GetIssue.None, loadedRoot.Get(2, out DurableDict<int, DurableDict<int, int>>? loadedFork));
        Assert.Equal(GetIssue.None, loadedFork!.Get(1, out DurableDict<int, int>? loadedChild));
        Assert.Equal(child.LocalId, loadedChild!.LocalId);
        Assert.Equal(GetIssue.None, loadedChild.Get(1, out int value));
        Assert.Equal(10, value);
    }

    [Fact]
    public void ForkCommittedAsMutable_DirtyPendingFork_ClearsRegistrationAfterCommit() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, DurableDict<int, int>>();
        var source = rev.CreateDict<int, int>();
        source.Upsert(1, 10);
        root.Upsert(1, source);
        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        var fork = source.ForkCommittedAsMutable();
        fork.Upsert(1, 20);
        root.Upsert(2, fork);

        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");
        int typedPayloadFramesBefore = CountUserPayloadFrames(file, DurableObjectKind.TypedDict);

        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit3");
        int typedPayloadFramesAfter = CountUserPayloadFrames(file, DurableObjectKind.TypedDict);
        Assert.Equal(typedPayloadFramesBefore, typedPayloadFramesAfter);
    }

    [Fact]
    public void ForkCommittedAsMutable_AllowsConsecutiveForksBeforeCommit() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, DurableDict<int, int>>();
        var source = rev.CreateDict<int, int>();
        source.Upsert(1, 10);
        root.Upsert(1, source);
        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        var fork1 = source.ForkCommittedAsMutable();
        var fork2 = fork1.ForkCommittedAsMutable();
        root.Upsert(2, fork1);
        root.Upsert(3, fork2);

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");
        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var loadedRoot = Assert.IsAssignableFrom<DurableDict<int, DurableDict<int, int>>>(opened.GraphRoot);

        Assert.Equal(GetIssue.None, loadedRoot.Get(2, out DurableDict<int, int>? loadedFork1));
        Assert.Equal(GetIssue.None, loadedRoot.Get(3, out DurableDict<int, int>? loadedFork2));
        Assert.Equal(GetIssue.None, loadedFork1!.Get(1, out int fork1Value));
        Assert.Equal(GetIssue.None, loadedFork2!.Get(1, out int fork2Value));
        Assert.Equal(10, fork1Value);
        Assert.Equal(10, fork2Value);
        Assert.NotEqual(fork1.LocalId, fork2.LocalId);
    }

    [Fact]
    public void ForkCommittedAsMutable_UnreachablePendingFork_IsSwept() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, DurableDict<int, int>>();
        var source = rev.CreateDict<int, int>();
        source.Upsert(1, 10);
        root.Upsert(1, source);
        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        var fork = source.ForkCommittedAsMutable();
        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");

        Assert.Equal(DurableState.Detached, fork.State);
        Assert.True(rev.Load(fork.LocalId).IsFailure);
    }

    [Fact]
    public void ForkCommittedAsMutable_SaveAsWritesForkAndClearsPendingRegistration() {
        var srcPath = GetTempFilePath();
        var dstPath = GetTempFilePath();
        using var srcFile = RbfFile.CreateNew(srcPath);
        using var dstFile = RbfFile.CreateNew(dstPath);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, DurableDict<int, int>>();
        var source = rev.CreateDict<int, int>();
        source.Upsert(1, 10);
        root.Upsert(1, source);
        _ = AssertCommitSucceeded(CommitToFile(rev, root, srcFile), "Commit1");

        var fork = source.ForkCommittedAsMutable();
        root.Upsert(2, fork);

        var saveAs = AssertCommitSucceeded(SaveAsToFile(rev, root, dstFile, segmentNumber: 2), "SaveAs");
        var opened = AssertSuccess(OpenRevision(saveAs.HeadCommitTicket, dstFile, segmentNumber: 2));
        var loadedRoot = Assert.IsAssignableFrom<DurableDict<int, DurableDict<int, int>>>(opened.GraphRoot);
        Assert.Equal(GetIssue.None, loadedRoot.Get(2, out DurableDict<int, int>? loadedFork));
        Assert.Equal(GetIssue.None, loadedFork!.Get(1, out int value));
        Assert.Equal(10, value);

        int typedPayloadFramesBefore = CountUserPayloadFrames(dstFile, DurableObjectKind.TypedDict);
        _ = AssertCommitSucceeded(CommitToFile(rev, root, dstFile, segmentNumber: 2), "CommitAfterSaveAs");
        int typedPayloadFramesAfter = CountUserPayloadFrames(dstFile, DurableObjectKind.TypedDict);
        Assert.Equal(typedPayloadFramesBefore, typedPayloadFramesAfter);
    }

    [Fact]
    public void ForkCommittedAsMutable_ValueTupleFork_RoundTripsTypedTuple() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, DurableDict<int, (ulong, string)>>();
        var source = rev.CreateDict<int, (ulong, string)>();
        source.Upsert(1, (ulong.MaxValue, "source"));
        root.Upsert(1, source);
        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        var fork = source.ForkCommittedAsMutable();
        fork.Upsert(1, (42UL, "fork"));
        root.Upsert(2, fork);

        var outcome = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");
        var opened = AssertSuccess(OpenRevision(outcome.HeadCommitTicket, file));
        var loadedRoot = Assert.IsAssignableFrom<DurableDict<int, DurableDict<int, (ulong, string)>>>(opened.GraphRoot);
        Assert.Equal(GetIssue.None, loadedRoot.Get(1, out DurableDict<int, (ulong, string)>? loadedSource));
        Assert.Equal(GetIssue.None, loadedRoot.Get(2, out DurableDict<int, (ulong, string)>? loadedFork));
        Assert.Equal(GetIssue.None, loadedSource!.Get(1, out (ulong, string) sourceValue));
        Assert.Equal(GetIssue.None, loadedFork!.Get(1, out (ulong, string) forkValue));
        Assert.Equal((ulong.MaxValue, "source"), sourceValue);
        Assert.Equal((42UL, "fork"), forkValue);
    }

    [Fact]
    public void ValueTupleHelper_ForkFrozenForNewOwner_ClonesNeedReleaseItems() {
        int before = ValuePools.OfBits64.Count;
        var sourceBox = ValueBox.Freeze(ValueBox.UInt64Face.From(ulong.MaxValue));
        var source = (sourceBox, 7);

        var fork = ValueTuple2Helper<ValueBox, int, ValueBoxHelper, Int32Helper>.ForkFrozenForNewOwner(source);

        Assert.Equal(before + 2, ValuePools.OfBits64.Count);
        Assert.True(ValueBox.ValueEquals(source.Item1, fork.Item1));
        Assert.NotEqual(source.Item1.GetBits(), fork.Item1.GetBits());

        ValueBoxHelper.ReleaseSlot(source.Item1);
        Assert.Equal(before + 1, ValuePools.OfBits64.Count);
        Assert.Equal(GetIssue.None, ValueBox.UInt64Face.Get(fork.Item1, out ulong forkValue));
        Assert.Equal(ulong.MaxValue, forkValue);

        ValueBoxHelper.ReleaseSlot(fork.Item1);
        Assert.Equal(before, ValuePools.OfBits64.Count);
    }

    private static int CountUserPayloadFrames(IRbfFile file, DurableObjectKind objectKind) {
        int count = 0;
        foreach (var info in file.ScanReverse()) {
            var tag = new FrameTag(info.Tag);
            if (tag.Usage == FrameUsage.UserPayload && tag.ObjectKind == objectKind) { count++; }
        }
        return count;
    }

    private static T AssertSuccess<T>(AteliaResult<T> result) {
        Assert.True(result.IsSuccess, $"Expected success, got: {result.Error}");
        return result.Value!;
    }
}
