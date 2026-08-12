using System.Runtime.InteropServices;
using System.Text;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Cadence;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Cadence.Tests;

public sealed class CadenceTests : IDisposable {
    private readonly List<string> _paths = [];

    [Fact]
    public void CanonicalCodecHasExactGoldenAndRejectsNoncanonicalBytes() {
        var policy = Policy();
        RecapGridCadenceSnapshot snapshot = CadenceCanonicalCodec.Create(
            new RefId(1), 7, policy);
        const string expected =
            "{\"schema\":\"atelia.session-journal.recap-grid.cadence.v1\","
            + "\"refId\":\"0000000000000001\",\"generation\":7,"
            + "\"minimumRecentHistoryLoad\":24000,"
            + "\"partitionAlgorithmId\":\"atelia.history-timeline.partition.first-replay-safe-at-target.v1\","
            + "\"historyLoadEstimatorId\":\"atelia.history-load.o200k-base.history-unit-v1\","
            + "\"targetHistoryLoad\":60000,\"maxRawEvents\":4096,"
            + "\"maxRenderedBytes\":1048576,"
            + "\"domainDigest\":\"170ef30005babe8e68fb9328c0137fc5940eda1e47d98d9c38774e3696beae52\"}";
        Assert.Equal(expected, Encoding.UTF8.GetString(
            snapshot.ToCanonicalBytes()));
        Assert.Equal(expected, Encoding.UTF8.GetString(
            RecapGridCadenceSnapshot.DecodeCanonical(
                snapshot.ToCanonicalBytes()).ToCanonicalBytes()));
        byte[] copy = snapshot.ToCanonicalBytes();
        copy[0] = (byte)'!';
        Assert.Equal((byte)'{', snapshot.ToCanonicalBytes()[0]);

        byte[] reordered = Encoding.UTF8.GetBytes(expected.Replace(
            "\"schema\":\"atelia.session-journal.recap-grid.cadence.v1\",\"refId\":\"0000000000000001\"",
            "\"refId\":\"0000000000000001\",\"schema\":\"atelia.session-journal.recap-grid.cadence.v1\"",
            StringComparison.Ordinal));
        Assert.ThrowsAny<Exception>(() =>
            RecapGridCadenceSnapshot.DecodeCanonical(reordered));
        Assert.ThrowsAny<Exception>(() =>
            RecapGridCadenceSnapshot.DecodeCanonical(
                [.. snapshot.ToCanonicalBytes(), (byte)' ']));
        Assert.ThrowsAny<Exception>(() =>
            RecapGridCadenceSnapshot.DecodeCanonical(
                [0xef, 0xbb, 0xbf, .. snapshot.ToCanonicalBytes()]));
        byte[] duplicate = Encoding.UTF8.GetBytes(expected.Replace(
            "\"generation\":7",
            "\"generation\":7,\"generation\":7",
            StringComparison.Ordinal));
        Assert.ThrowsAny<Exception>(() =>
            RecapGridCadenceSnapshot.DecodeCanonical(duplicate));
        Assert.Equal("CadenceCanonicalInvalid", Assert.Throws<
            CadenceStoreException
        >(() => RecapGridCadenceSnapshot.DecodeCanonical(
            new byte[RecapGridCadenceLimits.MaximumCanonicalUtf8Bytes]))
            .Code);
        Assert.Equal("CadenceCanonicalLimitExceeded", Assert.Throws<
            CadenceStoreException
        >(() => RecapGridCadenceSnapshot.DecodeCanonical(
            new byte[RecapGridCadenceLimits.MaximumCanonicalUtf8Bytes + 1]))
            .Code);
    }

    [Fact]
    public void PolicyBoundsAreCheckedInHistoryLoadAndUtf8Bytes() {
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy(recent: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy(target: 0));
        Assert.Throws<OverflowException>(() => Policy(
            recent: long.MaxValue, target: long.MaxValue));
        Assert.Throws<ArgumentException>(() => new RecapGridCadencePolicySpec(
            1,
            "unknown",
            O200kBaseHistoryUnitLoadEstimator.EstimatorId,
            1,
            1,
            1));
        Assert.Throws<ArgumentException>(() => new RecapGridCadencePolicySpec(
            1,
            HistoryPartitionAlgorithms.FirstReplaySafeBoundaryAtTargetV1,
            "bad\ud800id",
            1,
            1,
            1));
        Assert.Throws<ArgumentException>(() => new RecapGridCadencePolicySpec(
            1,
            HistoryPartitionAlgorithms.FirstReplaySafeBoundaryAtTargetV1,
            new string('\u0800', 43),
            1,
            1,
            1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RecapGridCadencePolicySpec(
                1,
                HistoryPartitionAlgorithms.FirstReplaySafeBoundaryAtTargetV1,
                O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                1,
                HistoryPartitionPolicyLimits.MaximumRawEvents + 1,
                1));
    }

    [Fact]
    public void MutableOwnerCanCreateOpenReadCasAndDispose() {
        string path = NewPath();
        using SessionJournalEngine engine = CreateJournal(path);
        Assert.IsType<RecapGridCadenceReaderOpenResult.Absent>(
            RecapGridCadenceFactory.OpenReader(engine.ReadView));

        RecapGridCadenceSnapshot created = Assert.IsType<
            RecapGridCadenceCreateResult.Created
        >(RecapGridCadenceFactory.Create(engine, Policy())).Snapshot;
        Assert.Equal(0, created.Head.Generation);
        using RecapGridCadenceHandle handle = Assert.IsType<
            RecapGridCadenceOpenResult.Opened
        >(RecapGridCadenceFactory.OpenMutable(engine)).Handle;
        Assert.Equal(created.Head, Assert.IsType<
            RecapGridCadenceReadResult.Available
        >(handle.Reader.ReadSnapshot()).Snapshot.Head);

        RecapGridCadenceSnapshot updated = Assert.IsType<
            RecapGridCadenceCompareExchangeResult.Updated
        >(handle.Coordinator.CompareExchangePolicy(
            created.Head, Policy(target: 60001))).Snapshot;
        Assert.Equal(1, updated.Head.Generation);
        Assert.IsType<RecapGridCadenceCompareExchangeResult.Unchanged>(
            handle.Coordinator.CompareExchangePolicy(
                updated.Head, Policy(target: 60001)));
        handle.Dispose();
        Assert.IsType<RecapGridCadenceReadResult.Disposed>(
            handle.Reader.ReadSnapshot());
        Assert.IsType<RecapGridCadenceCompareExchangeResult.Disposed>(
            handle.Coordinator.CompareExchangePolicy(
                updated.Head, Policy()));
    }

    [Fact]
    public void WholeHeadCasRejectsStaleAndClosesAba() {
        string path = NewPath();
        using SessionJournalEngine engine = CreateJournal(path);
        RecapGridCadenceSnapshot original = Assert.IsType<
            RecapGridCadenceCreateResult.Created
        >(RecapGridCadenceFactory.Create(engine, Policy())).Snapshot;
        using RecapGridCadenceHandle handle = Assert.IsType<
            RecapGridCadenceOpenResult.Opened
        >(RecapGridCadenceFactory.OpenMutable(engine)).Handle;
        RecapGridCadenceSnapshot changed = Assert.IsType<
            RecapGridCadenceCompareExchangeResult.Updated
        >(handle.Coordinator.CompareExchangePolicy(
            original.Head, Policy(target: 60001))).Snapshot;
        Assert.IsType<RecapGridCadenceCompareExchangeResult.Stale>(
            handle.Coordinator.CompareExchangePolicy(
                original.Head, Policy(target: 60002)));
        RecapGridCadenceSnapshot restored = Assert.IsType<
            RecapGridCadenceCompareExchangeResult.Updated
        >(handle.Coordinator.CompareExchangePolicy(
            changed.Head, Policy())).Snapshot;
        Assert.Equal(original.Head.DomainDigest,
            restored.Head.DomainDigest);
        Assert.Equal(2, restored.Head.Generation);
        Assert.IsType<RecapGridCadenceCompareExchangeResult.Stale>(
            handle.Coordinator.CompareExchangePolicy(
                original.Head, Policy(target: 60003)));
    }

    [Fact]
    public void CloneWithSameRefCannotSelectOriginalCadenceSlot() {
        string original = NewPath();
        string clone = NewPath();
        using (SessionJournalEngine created = CreateJournal(original)) { }
        CopyDirectory(original, clone);
        using SessionJournalEngine originalOwner = SessionJournalEngine.Open(original);
        using SessionJournalEngine cloneOwner = SessionJournalEngine.Open(clone);
        Assert.Equal(originalOwner.BranchRefId, cloneOwner.BranchRefId);

        RecapGridCadenceSnapshot snapshot = Assert.IsType<
            RecapGridCadenceCreateResult.Created
        >(RecapGridCadenceFactory.Create(
            originalOwner, Policy())).Snapshot;
        Assert.IsType<RecapGridCadenceReaderOpenResult.Absent>(
            RecapGridCadenceFactory.OpenReader(cloneOwner.ReadView));
        Assert.IsType<RecapGridCadenceCreateResult.Created>(
            RecapGridCadenceFactory.Create(
                cloneOwner, Policy(target: 60001)));
        using RecapGridCadenceHandle originalHandle = Assert.IsType<
            RecapGridCadenceOpenResult.Opened
        >(RecapGridCadenceFactory.OpenMutable(originalOwner)).Handle;
        Assert.Equal(snapshot.Head, Assert.IsType<
            RecapGridCadenceReadResult.Available
        >(originalHandle.Reader.ReadSnapshot()).Snapshot.Head);
    }

    [Fact]
    public void OpenAndInspectAbsentNeverCreateCadenceSlots() {
        string path = NewPath();
        RefId refId;
        using (SessionJournalEngine engine = CreateJournal(path)) {
            refId = engine.BranchRefId;
            Assert.IsType<RecapGridCadenceReaderOpenResult.Absent>(
                RecapGridCadenceFactory.OpenReader(engine.ReadView));
            Assert.False(Directory.Exists(CadenceDirectory(path, refId)));
        }
        Assert.IsType<RecapGridCadenceInspectResult.Absent>(
            RecapGridCadenceMaintenance.Inspect(path, refId));
        Assert.False(Directory.Exists(CadenceDirectory(path, refId)));
        var foreignRef = new RefId(refId.Packed == ulong.MaxValue
            ? refId.Packed - 1
            : refId.Packed + 1);
        Assert.IsType<RecapGridCadenceInspectResult.Invalid>(
            RecapGridCadenceMaintenance.Inspect(path, foreignRef));
        Assert.False(Directory.Exists(CadenceDirectory(path, foreignRef)));
    }

    [Fact]
    public async Task ConcurrentCreateReturnsRetryableBusyNotCorruption() {
        string path = NewPath();
        using SessionJournalEngine engine = CreateJournal(path);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var hooks = new CadencePersistenceTestHooks(BeforePublish: _ => {
            entered.Set();
            release.Wait();
        });
        Task<RecapGridCadenceCreateResult> first = Task.Run(() =>
            RecapGridCadenceFactory.CreateWithHooks(
                engine, Policy(), hooks));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        try {
            Assert.IsType<RecapGridCadenceCreateResult.Busy>(
                RecapGridCadenceFactory.Create(engine, Policy()));
        }
        finally {
            release.Set();
        }
        Assert.IsType<RecapGridCadenceCreateResult.Created>(await first);
        Assert.IsType<RecapGridCadenceCreateResult.AlreadyExists>(
            RecapGridCadenceFactory.Create(engine, Policy()));
    }

    [Fact]
    public void PrepublishReoccupationIsNeverPublishedOrDeleted() {
        string path = NewPath();
        using SessionJournalEngine engine = CreateJournal(path);
        string? temporary = null;
        const string foreign = "foreign-owner";
        var hooks = new CadencePersistenceTestHooks(BeforePublish: value => {
            temporary = value;
            File.Delete(value);
            File.WriteAllText(value, foreign);
        });
        RecapGridCadenceCreateResult.Invalid invalid = Assert.IsType<
            RecapGridCadenceCreateResult.Invalid
        >(RecapGridCadenceFactory.CreateWithHooks(
            engine, Policy(), hooks));
        Assert.Equal("CadenceTemporaryIdentityChanged", invalid.Code);
        Assert.NotNull(temporary);
        Assert.Equal(foreign, File.ReadAllText(temporary!));
        Assert.IsType<RecapGridCadenceReaderOpenResult.Absent>(
            RecapGridCadenceFactory.OpenReader(engine.ReadView));
    }

    [Fact]
    public void PublishFailuresAreOldOrTypedIndeterminateExactNew() {
        string path = NewPath();
        using SessionJournalEngine engine = CreateJournal(path);
        RecapGridCadenceCreateResult.Invalid before = Assert.IsType<
            RecapGridCadenceCreateResult.Invalid
        >(RecapGridCadenceFactory.CreateWithHooks(
            engine,
            Policy(),
            new CadencePersistenceTestHooks(
                BeforePublish: _ => throw new IOException("before"))));
        Assert.Equal("CadenceCreateInvalid", before.Code);
        Assert.IsType<RecapGridCadenceReaderOpenResult.Absent>(
            RecapGridCadenceFactory.OpenReader(engine.ReadView));

        RecapGridCadenceCreateResult.CommitIndeterminate after = Assert.IsType<
            RecapGridCadenceCreateResult.CommitIndeterminate
        >(RecapGridCadenceFactory.CreateWithHooks(
            engine,
            Policy(),
            new CadencePersistenceTestHooks(
                AfterPublish: _ => throw new IOException("after"))));
        Assert.Equal(after.Intended, after.Observed);
        using RecapGridCadenceHandle handle = Assert.IsType<
            RecapGridCadenceOpenResult.Opened
        >(RecapGridCadenceFactory.OpenMutable(engine)).Handle;
        Assert.Equal(after.Intended, Assert.IsType<
            RecapGridCadenceReadResult.Available
        >(handle.Reader.ReadSnapshot()).Snapshot.Head);
    }

    [Fact]
    public void UnsupportedSchemaIsTypedAcrossCreateAndOpen() {
        string path = NewPath();
        using SessionJournalEngine engine = CreateJournal(path);
        RecapGridCadenceSnapshot created = Assert.IsType<
            RecapGridCadenceCreateResult.Created
        >(RecapGridCadenceFactory.Create(engine, Policy())).Snapshot;
        string state = CadenceState(path, engine.BranchRefId);
        byte[] future = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(created.ToCanonicalBytes()).Replace(
                ".cadence.v1", ".cadence.v2",
                StringComparison.Ordinal));
        File.WriteAllBytes(state, future);
        Assert.Equal(2, Assert.IsType<
            RecapGridCadenceOpenResult.UnsupportedSchema
        >(RecapGridCadenceFactory.OpenMutable(engine)).Version);
        Assert.Equal(2, Assert.IsType<
            RecapGridCadenceCreateResult.UnsupportedSchema
        >(RecapGridCadenceFactory.Create(
            engine, Policy())).Version);
    }

    [Fact]
    public void CasPostpublishFailureSettlesToExactObservedHead() {
        string path = NewPath();
        using SessionJournalEngine engine = CreateJournal(path);
        RecapGridCadenceSnapshot created = Assert.IsType<
            RecapGridCadenceCreateResult.Created
        >(RecapGridCadenceFactory.Create(engine, Policy())).Snapshot;
        using (RecapGridCadenceHandle beforeHandle = Assert.IsType<
                   RecapGridCadenceOpenResult.Opened
               >(RecapGridCadenceFactory.OpenMutableForTest(
                   engine,
                   new CadencePersistenceTestHooks(
                       BeforePublish: _ => throw new IOException("before"))))
               .Handle) {
            Assert.IsType<RecapGridCadenceCompareExchangeResult.Invalid>(
                beforeHandle.Coordinator.CompareExchangePolicy(
                    created.Head, Policy(target: 60001)));
            Assert.Equal(created.Head, Assert.IsType<
                RecapGridCadenceReadResult.Available
            >(beforeHandle.Reader.ReadSnapshot()).Snapshot.Head);
        }
        using RecapGridCadenceHandle handle = Assert.IsType<
            RecapGridCadenceOpenResult.Opened
        >(RecapGridCadenceFactory.OpenMutableForTest(
            engine,
            new CadencePersistenceTestHooks(
                AfterPublish: _ => throw new IOException("after"))))
            .Handle;
        RecapGridCadenceCompareExchangeResult.CommitIndeterminate result =
            Assert.IsType<RecapGridCadenceCompareExchangeResult.CommitIndeterminate>(
                handle.Coordinator.CompareExchangePolicy(
                    created.Head, Policy(target: 60002)));
        Assert.Equal(result.Intended, result.Observed);
        Assert.Equal(1, result.Intended.Generation);
    }

    [Fact]
    public void SymlinkFifoAndDeviceShapedPathsFailClosed() {
        string path = NewPath();
        string external = NewPath();
        Directory.CreateDirectory(external);
        using SessionJournalEngine engine = CreateJournal(path);
        Directory.CreateSymbolicLink(
            Path.Combine(path, "control"), external);
        Assert.IsType<RecapGridCadenceCreateResult.Invalid>(
            RecapGridCadenceFactory.Create(engine, Policy()));
        Assert.Empty(Directory.EnumerateFileSystemEntries(external));
        Directory.Delete(Path.Combine(path, "control"));

        Assert.IsType<RecapGridCadenceCreateResult.Created>(
            RecapGridCadenceFactory.Create(engine, Policy()));
        string state = CadenceState(path, engine.BranchRefId);
        File.Delete(state);
        Assert.Equal(0, MkFifo(state, Convert.ToUInt32("600", 8)));
        Assert.IsType<RecapGridCadenceOpenResult.Invalid>(
            RecapGridCadenceFactory.OpenMutable(engine));
        File.Delete(state);

        Assert.IsType<RecapGridCadenceInspectResult.Invalid>(
            RecapGridCadenceMaintenance.Inspect("/dev/null", new RefId(1)));
    }

    [Fact]
    public void DisposedOrForeignOwnerCannotDriveFactory() {
        string path = NewPath();
        var engine = CreateJournal(path);
        SessionJournalReadView view = engine.ReadView;
        engine.Dispose();
        Assert.IsType<RecapGridCadenceCreateResult.Invalid>(
            RecapGridCadenceFactory.Create(engine, Policy()));
        Assert.IsType<RecapGridCadenceReaderOpenResult.Invalid>(
            RecapGridCadenceFactory.OpenReader(view));
    }

    [Fact]
    public void ReadOnlyOwnerCannotCreateOrOpenMutable() {
        string path = NewPath();
        using (SessionJournalEngine mutable = CreateJournal(path)) { }
        using SessionJournalEngine readOnly =
            SessionJournalEngine.OpenReadOnly(path);
        Assert.IsType<RecapGridCadenceCreateResult.Invalid>(
            RecapGridCadenceFactory.Create(readOnly, Policy()));
        Assert.IsType<RecapGridCadenceOpenResult.Invalid>(
            RecapGridCadenceFactory.OpenMutable(readOnly));
        Assert.False(Directory.Exists(CadenceDirectory(
            path, readOnly.BranchRefId)));
    }

    [Fact]
    public void DisposedMutableOwnerCannotCommitThroughLeakedHandle() {
        string path = NewPath();
        SessionJournalEngine engine = CreateJournal(path);
        RecapGridCadenceSnapshot created = Assert.IsType<
            RecapGridCadenceCreateResult.Created
        >(RecapGridCadenceFactory.Create(engine, Policy())).Snapshot;
        using RecapGridCadenceHandle handle = Assert.IsType<
            RecapGridCadenceOpenResult.Opened
        >(RecapGridCadenceFactory.OpenMutable(engine)).Handle;
        byte[] before = File.ReadAllBytes(CadenceState(
            path, engine.BranchRefId));
        engine.Dispose();

        Assert.IsType<RecapGridCadenceCompareExchangeResult.Invalid>(
            handle.Coordinator.CompareExchangePolicy(
                created.Head, Policy(target: 60001)));
        Assert.Equal(before, File.ReadAllBytes(CadenceState(
            path, created.Head.RefId)));
    }

    [Fact]
    public async Task OwnerDisposeDrainsPublishedCasAndRawMutationIsRefused() {
        string path = NewPath();
        SessionJournalEngine engine = CreateJournal(path);
        RecapGridCadenceSnapshot created = Assert.IsType<
            RecapGridCadenceCreateResult.Created
        >(RecapGridCadenceFactory.Create(engine, Policy())).Snapshot;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using RecapGridCadenceHandle handle = Assert.IsType<
            RecapGridCadenceOpenResult.Opened
        >(RecapGridCadenceFactory.OpenMutableForTest(
            engine,
            new CadencePersistenceTestHooks(BeforePublish: _ => {
                entered.Set();
                release.Wait();
            }))).Handle;
        Task<RecapGridCadenceCompareExchangeResult> cas = Task.Run(() =>
            handle.Coordinator.CompareExchangePolicy(
                created.Head, Policy(target: 60001)));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        await Assert.ThrowsAsync<SessionJournalConcurrentMutationException>(
            () => engine.SendAsync(
                engine.ReadCurrentHead()!.Value,
                "must-not-append"));
        Task dispose = Task.Run(engine.Dispose);
        await Task.Delay(50);
        Assert.False(dispose.IsCompleted);
        release.Set();
        Assert.IsType<RecapGridCadenceCompareExchangeResult.Updated>(
            await cas);
        await dispose;
        Assert.IsType<RecapGridCadenceCompareExchangeResult.Invalid>(
            handle.Coordinator.CompareExchangePolicy(
                created.Head, Policy(target: 60002)));
    }

    [Fact]
    public void ReentrantOwnerDisposeIsRefusedWithoutDeadlockOrLostPublish() {
        string path = NewPath();
        SessionJournalEngine engine = CreateJournal(path);
        RecapGridCadenceCreateResult result =
            RecapGridCadenceFactory.CreateWithHooks(
                engine,
                Policy(),
                new CadencePersistenceTestHooks(BeforePublish: _ =>
                    Assert.Throws<
                        SessionJournalConcurrentMutationException>(
                        engine.Dispose)));
        Assert.IsType<RecapGridCadenceCreateResult.Created>(result);
        Assert.NotNull(engine.ReadCurrentHead());
        engine.Dispose();
    }

    [Fact]
    public void HeldDirectoryFdNeverPublishesIntoSwappedAncestor() {
        string path = NewPath();
        string external = NewPath();
        Directory.CreateDirectory(external);
        using SessionJournalEngine engine = CreateJournal(path);
        string control = Path.Combine(path, "control");
        string displaced = Path.Combine(path, "control.displaced");
        bool swapped = false;
        RecapGridCadenceCreateResult result =
            RecapGridCadenceFactory.CreateWithHooks(
                engine,
                Policy(),
                new CadencePersistenceTestHooks(
                    AfterDirectoryOpen: _ => {
                        Directory.Move(control, displaced);
                        Directory.CreateSymbolicLink(control, external);
                        swapped = true;
                    }));
        Assert.True(swapped);
        RecapGridCadenceCreateResult.CommitIndeterminate indeterminate =
            Assert.IsType<RecapGridCadenceCreateResult.CommitIndeterminate>(
                result);
        Assert.Null(indeterminate.Observed);
        Assert.Empty(Directory.EnumerateFileSystemEntries(external));
        Directory.Delete(control);
        Directory.Move(displaced, control);
    }

    [Fact]
    public void DanglingStateAndLockSlotsAreInvalidRatherThanAbsent() {
        string statePath = NewPath();
        using (SessionJournalEngine engine = CreateJournal(statePath)) {
            Assert.IsType<RecapGridCadenceCreateResult.Created>(
                RecapGridCadenceFactory.Create(engine, Policy()));
            string state = CadenceState(statePath, engine.BranchRefId);
            File.Delete(state);
            File.CreateSymbolicLink(state, Path.Combine(statePath, "missing"));
            Assert.IsType<RecapGridCadenceReaderOpenResult.Invalid>(
                RecapGridCadenceFactory.OpenReader(engine.ReadView));
        }

        string lockPath = NewPath();
        using SessionJournalEngine second = CreateJournal(lockPath);
        Assert.IsType<RecapGridCadenceCreateResult.Created>(
            RecapGridCadenceFactory.Create(second, Policy()));
        string cadenceLock = Path.Combine(CadenceDirectory(
            lockPath, second.BranchRefId), "cadence.lock");
        File.Delete(cadenceLock);
        File.CreateSymbolicLink(cadenceLock,
            Path.Combine(lockPath, "missing"));
        Assert.IsType<RecapGridCadenceReaderOpenResult.Invalid>(
            RecapGridCadenceFactory.OpenReader(second.ReadView));
    }

    [Fact]
    public void PrepublishFailurePreservesOwnedOrphanWithoutDeletingPath() {
        string path = NewPath();
        using SessionJournalEngine engine = CreateJournal(path);
        Assert.IsType<RecapGridCadenceCreateResult.Invalid>(
            RecapGridCadenceFactory.CreateWithHooks(
                engine,
                Policy(),
                new CadencePersistenceTestHooks(
                    BeforePublish: _ => throw new IOException("before"))));
        Assert.Single(Directory.EnumerateFiles(
            CadenceDirectory(path, engine.BranchRefId),
            ".cadence.json.*.tmp"));
        Assert.IsType<RecapGridCadenceReaderOpenResult.Absent>(
            RecapGridCadenceFactory.OpenReader(engine.ReadView));
    }

    [Fact]
    public void SharedAncestorsMayBe0755ButPrivateCadenceLeafMustBe0700() {
        if (!OperatingSystem.IsLinux()) {
            return;
        }
        string path = NewPath();
        using SessionJournalEngine engine = CreateJournal(path);
        string sharedRef = Path.Combine(path, "control", "recap-grid", "v1",
            "refs", engine.BranchRefId.ToHexString());
        Directory.CreateDirectory(sharedRef);
        File.SetUnixFileMode(sharedRef,
            UnixFileMode.UserRead | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute | UnixFileMode.GroupRead
            | UnixFileMode.GroupExecute | UnixFileMode.OtherRead
            | UnixFileMode.OtherExecute);
        Assert.IsType<RecapGridCadenceCreateResult.Created>(
            RecapGridCadenceFactory.Create(engine, Policy()));

        string secondPath = NewPath();
        using SessionJournalEngine second = CreateJournal(secondPath);
        string leaf = CadenceDirectory(secondPath, second.BranchRefId);
        Directory.CreateDirectory(leaf);
        File.SetUnixFileMode(leaf,
            UnixFileMode.UserRead | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute | UnixFileMode.GroupRead
            | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite
            | UnixFileMode.OtherExecute);
        RecapGridCadenceCreateResult.Invalid invalid = Assert.IsType<
            RecapGridCadenceCreateResult.Invalid
        >(RecapGridCadenceFactory.Create(second, Policy()));
        Assert.Equal("CadenceDirectoryPermissionsInvalid", invalid.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(leaf));
    }

    [Fact]
    public void ExistingStateAndLockMustBePrivateOwnerOnlyFiles() {
        if (!OperatingSystem.IsLinux()) {
            return;
        }
        string statePath = NewPath();
        using (SessionJournalEngine engine = CreateJournal(statePath)) {
            Assert.IsType<RecapGridCadenceCreateResult.Created>(
                RecapGridCadenceFactory.Create(engine, Policy()));
            File.SetUnixFileMode(CadenceState(
                    statePath, engine.BranchRefId),
                UnixFileMode.UserRead | UnixFileMode.UserWrite
                | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            Assert.IsType<RecapGridCadenceReaderOpenResult.Invalid>(
                RecapGridCadenceFactory.OpenReader(engine.ReadView));
        }
        string lockPath = NewPath();
        using SessionJournalEngine second = CreateJournal(lockPath);
        Assert.IsType<RecapGridCadenceCreateResult.Created>(
            RecapGridCadenceFactory.Create(second, Policy()));
        File.SetUnixFileMode(Path.Combine(CadenceDirectory(
                lockPath, second.BranchRefId), "cadence.lock"),
            UnixFileMode.UserRead | UnixFileMode.UserWrite
            | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        Assert.IsType<RecapGridCadenceReaderOpenResult.Invalid>(
            RecapGridCadenceFactory.OpenReader(second.ReadView));
    }

    [Fact]
    public void FutureSchemaDiscriminatorIsTypedBeforeV1ShapeValidation() {
        string path = NewPath();
        using SessionJournalEngine engine = CreateJournal(path);
        Assert.IsType<RecapGridCadenceCreateResult.Created>(
            RecapGridCadenceFactory.Create(engine, Policy()));
        File.WriteAllText(CadenceState(path, engine.BranchRefId),
            "{\"schema\":\"atelia.session-journal.recap-grid.cadence.v2\"}");
        Assert.Equal(2, Assert.IsType<
            RecapGridCadenceReaderOpenResult.UnsupportedSchema
        >(RecapGridCadenceFactory.OpenReader(engine.ReadView)).Version);
        Assert.Equal(2, Assert.IsType<
            RecapGridCadenceCreateResult.UnsupportedSchema
        >(RecapGridCadenceFactory.Create(engine, Policy())).Version);
    }

    private static RecapGridCadencePolicySpec Policy(
        long recent = 24000,
        long target = 60000
    ) => new(
        recent,
        HistoryPartitionAlgorithms.FirstReplaySafeBoundaryAtTargetV1,
        O200kBaseHistoryUnitLoadEstimator.EstimatorId,
        target,
        maxRawEvents: 4096,
        maxRenderedBytes: 1024 * 1024);

    private SessionJournalEngine CreateJournal(string path)
        => SessionJournalEngine.Create(path,
            new SessionCreateOptions("model", "system", "cadence-test"));

    private string NewPath() {
        string path = Path.Combine(
            Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath(),
            "atelia-recap-grid-cadence-tests",
            Guid.NewGuid().ToString("N"));
        _paths.Add(path);
        return path;
    }

    private static string CadenceDirectory(string path, RefId refId)
        => Path.Combine(path, "control", "recap-grid", "v1", "refs",
            refId.ToHexString(), "cadence");

    private static string CadenceState(string path, RefId refId)
        => Path.Combine(CadenceDirectory(path, refId), "cadence.json");

    private static void CopyDirectory(string source, string destination) {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(
                     source, "*", SearchOption.AllDirectories)) {
            Directory.CreateDirectory(Path.Combine(destination,
                Path.GetRelativePath(source, directory)));
        }
        foreach (string file in Directory.EnumerateFiles(
                     source, "*", SearchOption.AllDirectories)) {
            File.Copy(file, Path.Combine(destination,
                Path.GetRelativePath(source, file)));
        }
    }

    public void Dispose() {
        foreach (string path in _paths) {
            if (Directory.Exists(path)) {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo(string path, uint mode);
}
