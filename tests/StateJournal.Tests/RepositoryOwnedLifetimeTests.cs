using Atelia.Rbf;
using Xunit;

namespace Atelia.StateJournal.Tests;

public sealed class RepositoryOwnedLifetimeTests {
    [Fact]
    public void NewBranchRevisionAndObject_ThrowAfterRepositoryDispose() {
        string repoDir = CreateTempRepoPath();

        try {
            using Repository repo = AssertSuccess(Repository.Create(repoDir));
            Revision revision = AssertSuccess(repo.CreateBranch("main"));
            DurableDict<string, int> dict = revision.CreateDict<string, int>();
            dict.Upsert("answer", 42);
            DurableObjectKind kind = dict.Kind;
            LocalId localId = dict.LocalId;

            repo.Dispose();

            AssertRepositoryDisposed(() => _ = revision.GraphRoot);
            AssertRepositoryDisposed(() => _ = dict.Count);
            AssertRepositoryDisposed(() => dict.Upsert("later", 43));
            Assert.Equal(kind, dict.Kind);
            Assert.Equal(localId, dict.LocalId);
        }
        finally {
            DeleteDirectoryBestEffort(repoDir);
        }
    }

    [Fact]
    public void NewBranch_AllConcreteFamiliesAndViews_FollowRepositoryLifetime() {
        string repoDir = CreateTempRepoPath();

        try {
            using Repository repo = AssertSuccess(Repository.Create(repoDir));
            Revision revision = AssertSuccess(repo.CreateBranch("main"));
            DurableText text = revision.CreateText();
            text.Append("alpha");

            DurableDict<string, int> typedDict = revision.CreateDict<string, int>();
            typedDict.Upsert("one", 1);
            DurableDict<string> mixedDict = revision.CreateDict<string>();
            mixedDict.Upsert("one", 1);
            DurableDict<string, DurableText> durObjDict = revision.CreateDict<string, DurableText>();
            durObjDict.Upsert("text", text);

            DurableDeque<int> typedDeque = revision.CreateDeque<int>();
            typedDeque.PushBack(1);
            DurableDeque mixedDeque = revision.CreateDeque();
            mixedDeque.PushBack(1);
            DurableDeque<DurableText> durObjDeque = revision.CreateDeque<DurableText>();
            durObjDeque.PushBack(text);

            DurableOrderedDict<string, int> typedOrdered = revision.CreateOrderedDict<string, int>();
            typedOrdered.Upsert("one", 1);
            DurableOrderedDict<string> mixedOrdered = revision.CreateOrderedDict<string>();
            mixedOrdered.Upsert("one", 1);
            DurableOrderedDict<string, DurableText> durObjOrdered = revision.CreateOrderedDict<string, DurableText>();
            durObjOrdered.Upsert("text", text);

            DurableHashSet<int> set = revision.CreateHashSet<int>();
            set.Add(1);

            IEnumerable<string> typedKeys = typedDict.Keys;
            IEnumerable<string> mixedKeys = mixedDict.Keys;
            IEnumerable<string> durObjKeys = durObjDict.Keys;
            IEnumerator<string> typedKeyEnumerator = typedKeys.GetEnumerator();
            IEnumerator<string> mixedKeyEnumerator = mixedKeys.GetEnumerator();
            IEnumerator<string> durObjKeyEnumerator = durObjKeys.GetEnumerator();

            IReadOnlyCollection<int> setSnapshot = set.Items;
            IReadOnlyList<string> typedOrderedSnapshot = typedOrdered.GetKeys();
            IReadOnlyList<string> mixedOrderedSnapshot = mixedOrdered.GetKeys();
            IReadOnlyList<string> durObjOrderedSnapshot = durObjOrdered.GetKeys();
            List<KeyValuePair<string, DurableText?>> durObjRangeSnapshot = durObjOrdered.ReadAscendingFrom("text", 1);
            IReadOnlyList<TextBlock> textSnapshot = text.GetAllBlocks();

            IDict<string, int> acquiredMixedDictView = mixedDict.OfInt32;
            IDeque<int> acquiredMixedDequeView = mixedDeque.OfInt32;
            IDict<string, int> acquiredMixedOrderedView = mixedOrdered.OfInt32;

            DurableObject[] allObjects = [
                typedDict, mixedDict, durObjDict,
                typedDeque, mixedDeque, durObjDeque,
                typedOrdered, mixedOrdered, durObjOrdered,
                set, text
            ];
            var identities = allObjects.Select(static obj => (obj.Kind, obj.LocalId)).ToArray();

            repo.Dispose();
            repo.Dispose();

            AssertRevisionDisposed(revision, typedDict.LocalId);
            for (int i = 0; i < allObjects.Length; i++) {
                DurableObject obj = allObjects[i];
                AssertRepositoryDisposed(() => _ = obj.Revision);
                AssertRepositoryDisposed(() => _ = obj.State);
                AssertRepositoryDisposed(() => _ = obj.IsFrozen);
                AssertRepositoryDisposed(() => _ = obj.HasChanges);
                AssertRepositoryDisposed(obj.Freeze);
                Assert.Equal(identities[i].Kind, obj.Kind);
                Assert.Equal(identities[i].LocalId, obj.LocalId);
            }

            AssertRepositoryDisposed(() => _ = typedDict.Count);
            AssertRepositoryDisposed(() => typedDict.Get("one", out _));
            AssertRepositoryDisposed(() => typedDict.Upsert("two", 2));
            AssertRepositoryDisposed(() => typedDict.ForkCommittedAsMutable());
            AssertRepositoryDisposed(() => _ = mixedDict.Count);
            AssertRepositoryDisposed(() => mixedDict.Get<int>("one", out _));
            AssertRepositoryDisposed(() => mixedDict.Get<decimal>("one", out _));
            AssertRepositoryDisposed(() => mixedDict.Upsert("two", 2));
            AssertRepositoryDisposed(() => mixedDict.ForkCommittedAsMutable());
            AssertRepositoryDisposed(() => _ = durObjDict.Count);
            AssertRepositoryDisposed(() => durObjDict.Get("text", out _));
            AssertRepositoryDisposed(() => durObjDict.Upsert("two", text));
            AssertRepositoryDisposed(() => durObjDict.ForkCommittedAsMutable());

            AssertRepositoryDisposed(() => _ = typedDeque.Count);
            AssertRepositoryDisposed(() => typedDeque.GetAt(0, out _));
            AssertRepositoryDisposed(() => typedDeque.PushBack(2));
            AssertRepositoryDisposed(() => typedDeque.ForkCommittedAsMutable());
            AssertRepositoryDisposed(() => _ = mixedDeque.Count);
            AssertRepositoryDisposed(() => mixedDeque.GetAt<int>(0, out _));
            AssertRepositoryDisposed(() => mixedDeque.TrySetAt(999, 2));
            AssertRepositoryDisposed(() => mixedDeque.PushBack(2));
            AssertRepositoryDisposed(() => mixedDeque.ForkCommittedAsMutable());
            AssertRepositoryDisposed(() => _ = durObjDeque.Count);
            AssertRepositoryDisposed(() => durObjDeque.GetAt(0, out _));
            AssertRepositoryDisposed(() => durObjDeque.PushBack(text));
            AssertRepositoryDisposed(() => durObjDeque.ForkCommittedAsMutable());

            AssertRepositoryDisposed(() => _ = typedOrdered.Count);
            AssertRepositoryDisposed(() => typedOrdered.TryGet("one", out _));
            AssertRepositoryDisposed(() => typedOrdered.GetKeys());
            AssertRepositoryDisposed(() => typedOrdered.ReadAscendingFrom("one", 1));
            AssertRepositoryDisposed(() => typedOrdered.Upsert("two", 2));
            AssertRepositoryDisposed(() => _ = mixedOrdered.Count);
            AssertRepositoryDisposed(() => mixedOrdered.Get<int>("one", out _));
            AssertRepositoryDisposed(() => mixedOrdered.Get<decimal>("one", out _));
            AssertRepositoryDisposed(() => mixedOrdered.GetKeys());
            AssertRepositoryDisposed(() => mixedOrdered.GetKeysFrom("one", 1));
            AssertRepositoryDisposed(() => mixedOrdered.Upsert("two", 2));
            AssertRepositoryDisposed(() => _ = durObjOrdered.Count);
            AssertRepositoryDisposed(() => durObjOrdered.TryGet("text", out _));
            AssertRepositoryDisposed(() => durObjOrdered.GetKeys());
            AssertRepositoryDisposed(() => durObjOrdered.ReadAscendingFrom("text", 1));
            AssertRepositoryDisposed(() => durObjOrdered.Upsert("two", text));

            AssertRepositoryDisposed(() => _ = set.Count);
            AssertRepositoryDisposed(() => set.Contains(1));
            AssertRepositoryDisposed(() => set.Add(2));
            AssertRepositoryDisposed(() => set.ForkCommittedAsMutable());
            AssertRepositoryDisposed(() => _ = set.Items);

            AssertRepositoryDisposed(() => _ = text.BlockCount);
            AssertRepositoryDisposed(() => text.GetAllBlocks());
            AssertRepositoryDisposed(() => text.Append("later"));

            AssertRepositoryDisposed(() => _ = typedDict.Keys);
            AssertRepositoryDisposed(() => _ = mixedDict.Keys);
            AssertRepositoryDisposed(() => _ = durObjDict.Keys);
            AssertLiveKeysDisposed(typedKeys, typedKeyEnumerator);
            AssertLiveKeysDisposed(mixedKeys, mixedKeyEnumerator);
            AssertLiveKeysDisposed(durObjKeys, durObjKeyEnumerator);

            AssertRepositoryDisposed(() => mixedDict.Of<int>());
            AssertRepositoryDisposed(() => mixedDict.Of<decimal>());
            AssertRepositoryDisposed(() => _ = mixedDict.OfInt32);
            AssertRepositoryDisposed(() => mixedDeque.Of<int>());
            AssertRepositoryDisposed(() => mixedDeque.Of<decimal>());
            AssertRepositoryDisposed(() => _ = mixedDeque.OfInt32);
            AssertRepositoryDisposed(() => mixedDeque.TrySetAtTrustedBlob(-1, ByteString.Empty));
            AssertRepositoryDisposed(() => mixedOrdered.Of<int>());
            AssertRepositoryDisposed(() => mixedOrdered.Of<decimal>());
            AssertRepositoryDisposed(() => _ = mixedOrdered.OfInt32);
            AssertRepositoryDisposed(() => acquiredMixedDictView.Get("one", out _));
            AssertRepositoryDisposed(() => acquiredMixedDequeView.GetAt(0, out _));
            AssertRepositoryDisposed(() => acquiredMixedOrderedView.Get("one", out _));

            Assert.Equal([1], setSnapshot);
            Assert.Equal(["one"], typedOrderedSnapshot);
            Assert.Equal(["one"], mixedOrderedSnapshot);
            Assert.Equal(["text"], durObjOrderedSnapshot);
            Assert.Single(durObjRangeSnapshot);
            Assert.Equal("text", durObjRangeSnapshot[0].Key);
            Assert.Equal(DurableObjectKind.Text, durObjRangeSnapshot[0].Value!.Kind);
            AssertRepositoryDisposed(() => _ = durObjRangeSnapshot[0].Value!.BlockCount);
            Assert.Single(textSnapshot);
            Assert.Equal("alpha", textSnapshot[0].Content);
        }
        finally {
            DeleteDirectoryBestEffort(repoDir);
        }
    }

    [Fact]
    public void ReopenedUnbornBranchRevision_ExpiresWithRepository() {
        string repoDir = CreateTempRepoPath();

        try {
            using (Repository initialRepo = AssertSuccess(Repository.Create(repoDir))) {
                _ = AssertSuccess(initialRepo.CreateBranch("unborn"));
            }

            using Repository reopenedRepo = AssertSuccess(Repository.Open(repoDir));
            Revision reopenedUnborn = AssertSuccess(reopenedRepo.CheckoutBranch("unborn"));
            Assert.Null(reopenedUnborn.GraphRoot);

            reopenedRepo.Dispose();

            AssertRepositoryDisposed(() => _ = reopenedUnborn.GraphRoot);
            AssertRepositoryDisposed(() => reopenedUnborn.CreateText());
        }
        finally {
            DeleteDirectoryBestEffort(repoDir);
        }
    }

    [Fact]
    public void CheckoutDetachedRootAndReplayClone_AllExpireWithOwningRepository() {
        string repoDir = CreateTempRepoPath();

        try {
            using Repository initialRepo = AssertSuccess(Repository.Create(repoDir));
            Revision initialRevision = AssertSuccess(initialRepo.CreateBranch("main"));
            initialRepo.SetRotationThreshold(1);
            DurableDict<string, int> initialRoot = initialRevision.CreateDict<string, int>();
            initialRoot.Upsert("one", 1);
            CommitAddress address = AssertSuccess(initialRepo.Commit(initialRoot));
            initialRoot.Upsert("two", 2);
            CommitAddress latestAddress = AssertSuccess(initialRepo.Commit(initialRoot));
            Assert.NotEqual(address.SegmentNumber, latestAddress.SegmentNumber);
            initialRepo.Dispose();

            AssertRepositoryDisposed(() => _ = initialRevision.GraphRoot);
            AssertRepositoryDisposed(() => _ = initialRoot.Count);

            using Repository reopenedRepo = AssertSuccess(Repository.Open(repoDir));
            Revision checkoutRevision = AssertSuccess(reopenedRepo.CheckoutBranch("main"));
            DurableDict<string, int> checkoutRoot = Assert.IsAssignableFrom<DurableDict<string, int>>(checkoutRevision.GraphRoot);
            Assert.Equal(1, checkoutRoot.GetOrThrow("one"));
            Assert.Equal(2, checkoutRoot.GetOrThrow("two"));

            DurableDict<string, int> detachedRoot =
                Assert.IsAssignableFrom<DurableDict<string, int>>(AssertSuccess(reopenedRepo.LoadRootAtCommit(address)));
            Revision detachedRevision = detachedRoot.Revision;
            Assert.Null(detachedRevision.BranchName);
            Assert.Equal(GetIssue.NotFound, detachedRoot.Get("two", out _));

            DurableDict<string, int> replayClone = AssertSuccess(
                reopenedRepo.ReplayCommitted(checkoutRoot, LoadMaterializationMode.ForceMutable)
            );
            Assert.Same(checkoutRevision, replayClone.Revision);
            Assert.Equal(1, replayClone.GetOrThrow("one"));
            Assert.Equal(2, replayClone.GetOrThrow("two"));

            reopenedRepo.Dispose();
            reopenedRepo.Dispose();

            AssertRepositoryDisposed(() => _ = checkoutRevision.GraphRoot);
            AssertRepositoryDisposed(() => _ = checkoutRoot.Count);
            AssertRepositoryDisposed(() => checkoutRoot.Upsert("two", 2));
            AssertRepositoryDisposed(() => _ = detachedRevision.GraphRoot);
            AssertRepositoryDisposed(() => _ = detachedRoot.Count);
            AssertRepositoryDisposed(() => detachedRoot.Upsert("two", 2));
            AssertRepositoryDisposed(() => _ = replayClone.Count);
            AssertRepositoryDisposed(() => replayClone.Upsert("two", 2));

            Assert.Equal(DurableObjectKind.TypedDict, checkoutRoot.Kind);
            Assert.Equal(DurableObjectKind.TypedDict, detachedRoot.Kind);
            Assert.Equal(DurableObjectKind.TypedDict, replayClone.Kind);
            Assert.False(checkoutRoot.LocalId.IsNull);
            Assert.False(detachedRoot.LocalId.IsNull);
            Assert.False(replayClone.LocalId.IsNull);
        }
        finally {
            DeleteDirectoryBestEffort(repoDir);
        }
    }

    [Fact]
    public void StandaloneRevisionAndDirectOpen_DoNotAcquireRepositoryLifetime() {
        string repoDir = CreateTempRepoPath();
        string rbfPath = Path.Combine(Path.GetTempPath(), $"state-journal-standalone-{Guid.NewGuid():N}.rbf");

        try {
            using Repository unrelatedRepo = AssertSuccess(Repository.Create(repoDir));
            Revision standalone = new(1);
            DurableDict<string, int> standaloneRoot = standalone.CreateDict<string, int>();
            standaloneRoot.Upsert("one", 1);

            using var file = RbfFile.CreateNew(rbfPath);
            AteliaResult<CommitOutcome> commit = standalone.Commit(standaloneRoot, file);
            Assert.True(commit.IsSuccess, $"Commit failed: {commit.Error}");
            standalone.AcceptPersistedSegment(1);
            Revision directOpen = AssertSuccess(Revision.Open(commit.Value.HeadCommitTicket, file, 1));
            DurableDict<string, int> directRoot = Assert.IsAssignableFrom<DurableDict<string, int>>(directOpen.GraphRoot);

            unrelatedRepo.Dispose();

            Assert.Equal(1, standaloneRoot.GetOrThrow("one"));
            standaloneRoot.Upsert("two", 2);
            Assert.Equal(2, standaloneRoot.Count);
            Assert.Equal(1, directRoot.GetOrThrow("one"));
            directRoot.Upsert("two", 2);
            Assert.Equal(2, directRoot.Count);
            Assert.NotNull(standalone.CreateText());
            Assert.NotNull(directOpen.CreateText());
        }
        finally {
            DeleteDirectoryBestEffort(repoDir);
            DeleteFileBestEffort(rbfPath);
        }
    }

    private static T AssertSuccess<T>(AteliaResult<T> result) where T : notnull {
        Assert.True(result.IsSuccess, $"Expected success, got: {result.Error}");
        return result.Value!;
    }

    private static void AssertRepositoryDisposed(Action action) {
        ObjectDisposedException exception = Assert.Throws<ObjectDisposedException>(action);
        Assert.Equal(typeof(Repository).FullName, exception.ObjectName);
    }

    private static void AssertRevisionDisposed(Revision revision, LocalId existingId) {
        AssertRepositoryDisposed(() => _ = revision.HeadParentId);
        AssertRepositoryDisposed(() => _ = revision.HeadParentAddress);
        AssertRepositoryDisposed(() => _ = revision.GraphRoot);
        AssertRepositoryDisposed(() => _ = revision.HeadAddress);
        AssertRepositoryDisposed(() => revision.GetGraphRoot<DurableDict<string, int>>());
        AssertRepositoryDisposed(() => revision.Load(existingId));
        AssertRepositoryDisposed(() => revision.CreateDict<string, int>());
        AssertRepositoryDisposed(() => revision.CreateDict<string>());
        AssertRepositoryDisposed(() => revision.CreateDeque<int>());
        AssertRepositoryDisposed(() => revision.CreateDeque());
        AssertRepositoryDisposed(() => revision.CreateHashSet<int>());
        AssertRepositoryDisposed(() => revision.CreateOrderedDict<string, int>());
        AssertRepositoryDisposed(() => revision.CreateOrderedDict<string>());
        AssertRepositoryDisposed(() => revision.CreateText());
    }

    private static void AssertLiveKeysDisposed(
        IEnumerable<string> keys,
        IEnumerator<string> enumerator
    ) {
        AssertRepositoryDisposed(() => _ = keys.GetEnumerator());
        AssertRepositoryDisposed(() => enumerator.MoveNext());
        AssertRepositoryDisposed(() => _ = enumerator.Current);
        enumerator.Dispose();
    }

    private static string CreateTempRepoPath() =>
        Path.Combine(Path.GetTempPath(), $"state-journal-owned-lifetime-{Guid.NewGuid():N}");

    private static void DeleteDirectoryBestEffort(string path) {
        try {
            if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); }
        }
        catch {
        }
    }

    private static void DeleteFileBestEffort(string path) {
        try {
            if (File.Exists(path)) { File.Delete(path); }
        }
        catch {
        }
    }
}
