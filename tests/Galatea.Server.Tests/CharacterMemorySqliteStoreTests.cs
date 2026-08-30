using Atelia.EventJournal;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.SessionJournal;
using Atelia.Testing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class CharacterMemorySqliteStoreTests {
    [Fact]
    public void CreateProvisionOpen_RequiresExactOwnerAndExclusiveLock() {
        using var directory = new StoreDirectory();
        CharacterMemoryStoreOwner owner = Owner();
        CharacterMemoryStoreBaseline baseline = Baseline();
        using (CharacterMemorySqliteStore store =
               CharacterMemorySqliteStore.CreateNew(
                   directory.Path,
                   owner,
                   baseline,
                   State('p'))) {
            CharacterMemoryStatusSnapshot initial =
                store.ReadStatusSnapshot();
            Assert.Equal(CharacterMemoryStoreState.Provisioning,
                initial.StoreState);
            Assert.Equal(State('p'),
                initial.ProvisionTargetPodStateIdentity);
            Assert.Null(initial.SettledDefaultPodStateIdentity);
            Assert.Equal(0, initial.StoreRevision);
            Assert.Equal(owner, initial.Owner);
            Assert.Equal(baseline, initial.Baseline);
            Assert.Throws<CharacterMemoryStoreConflictException>(() =>
                store.CaptureNew(Capture(Address(1), [])));
            Assert.ThrowsAny<IOException>(() =>
                CharacterMemorySqliteStore.OpenExisting(
                    directory.Path,
                    owner
                ));

            CharacterMemoryProvisionResult recorded =
                store.RecordInitialDefaultPod(State('p'));
            Assert.Equal(CharacterMemoryProvisionDisposition.Recorded,
                recorded.Disposition);
            Assert.Equal(1, recorded.StoreRevision);
            CharacterMemoryProvisionResult duplicate =
                store.RecordInitialDefaultPod(State('p'));
            Assert.Equal(
                CharacterMemoryProvisionDisposition.AlreadyRecorded,
                duplicate.Disposition
            );
            Assert.Throws<CharacterMemoryStoreConflictException>(() =>
                store.RecordInitialDefaultPod(State('x')));
        }

        using CharacterMemorySqliteStore reopened =
            CharacterMemorySqliteStore.OpenExisting(
                directory.Path,
                owner
            );
        CharacterMemoryStatusSnapshot status = reopened.ReadStatusSnapshot();
        Assert.Equal(CharacterMemoryStoreState.Ready, status.StoreState);
        Assert.Equal(State('p'), status.SettledDefaultPodStateIdentity);
        reopened.Dispose();
        Assert.Throws<InvalidDataException>(() =>
            CharacterMemorySqliteStore.OpenExisting(
                directory.Path,
                owner with { UserId = "other" }
            ));
    }

    [Fact]
    public void Capture_IsBaselineAwareZeroDurableAndSingleActive() {
        using var fixture = new ReadyStore();
        CharacterMemoryCaptureResult baseline = fixture.Store.CaptureNew(
            Capture(Address(1), [])
        );
        Assert.Equal(CharacterMemoryCaptureDisposition.BaselineCovered,
            baseline.Disposition);
        Assert.Null(fixture.Store.ReadCaptureExact(Address(1)));

        CharacterMemoryCaptureRequest zeroRequest = Capture(Address(10), []);
        CharacterMemoryCaptureResult zero = fixture.Store.CaptureNew(
            zeroRequest
        );
        Assert.Equal(CharacterMemoryCaptureDisposition.ZeroCaptured,
            zero.Disposition);
        Assert.Equal(CharacterMemoryCaptureState.ZeroCaptured,
            zero.Capture!.State);
        Assert.Empty(zero.Capture.Notes);
        CharacterMemoryCaptureResult zeroAgain = fixture.Store.CaptureNew(
            zeroRequest
        );
        Assert.Equal(CharacterMemoryCaptureDisposition.AlreadyCaptured,
            zeroAgain.Disposition);

        CharacterMemoryCaptureRequest request = Capture(
            Address(11),
            ["first", "second"]
        );
        CharacterMemoryCaptureResult captured = fixture.Store.CaptureNew(
            request
        );
        Assert.Equal(CharacterMemoryCaptureDisposition.Captured,
            captured.Disposition);
        Assert.Equal(Address(11),
            fixture.Store.ReadStatusSnapshot().ActiveSourceAction);
        Assert.Equal(["first", "second"],
            captured.Capture!.Notes.Select(static note => note.ExactText));
        Assert.All(captured.Capture.Notes,
            static note => Assert.Null(note.MemoId));

        CharacterMemoryCaptureResult same = fixture.Store.CaptureNew(request);
        Assert.Equal(CharacterMemoryCaptureDisposition.AlreadyCaptured,
            same.Disposition);
        Assert.Throws<CharacterMemoryStoreConflictException>(() =>
            fixture.Store.CaptureNew(request with {
                ExactTexts = ["changed"]
            }));
        Assert.Throws<CharacterMemoryStoreConflictException>(() =>
            fixture.Store.CaptureNew(Capture(Address(12), ["blocked"])));
    }

    [Fact]
    public void Capture_RejectsCountPerItemAndTotalBoundsBeforeMutation() {
        using var fixture = new ReadyStore();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            fixture.Store.CaptureNew(Capture(
                Address(13),
                Enumerable.Repeat("note", 17).ToArray()
            )));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            fixture.Store.CaptureNew(Capture(
                Address(14),
                [new string('x', 64 * 1024 + 1)]
            )));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            fixture.Store.CaptureNew(Capture(
                Address(15),
                Enumerable.Repeat(new string('x', 64 * 1024), 5)
                    .ToArray()
            )));

        Assert.Null(fixture.Store.ReadCaptureExact(Address(13)));
        Assert.Null(fixture.Store.ReadCaptureExact(Address(14)));
        Assert.Null(fixture.Store.ReadCaptureExact(Address(15)));
        Assert.Equal(1, fixture.Store.ReadStatusSnapshot().StoreRevision);
    }

    [Fact]
    public void PlanAndSettle_AreExactIdempotentAndAdvanceTipAtomically() {
        using var fixture = new ReadyStore();
        CharacterMemoryCaptureSnapshot captured = fixture.Store.CaptureNew(
            Capture(Address(20), ["first", "second"])
        ).Capture!;
        var planRequest = new CharacterMemoryPlanRequest(
            Address(20),
            captured.ExtractionCommitment,
            State('p'),
            State('t'),
            ["m1:00000001", "m1:00000002"]
        );

        CharacterMemoryPlanResult planned = fixture.Store.PlanApply(
            planRequest
        );
        Assert.Equal(CharacterMemoryPlanDisposition.Planned,
            planned.Disposition);
        Assert.Equal(CharacterMemoryCaptureState.Planned,
            planned.Capture.State);
        Assert.Equal(["m1:00000001", "m1:00000002"],
            planned.Capture.Notes.Select(static note => note.MemoId));
        Assert.Equal(CharacterMemoryPlanDisposition.AlreadyPlanned,
            fixture.Store.PlanApply(planRequest).Disposition);
        Assert.Throws<CharacterMemoryStoreConflictException>(() =>
            fixture.Store.PlanApply(planRequest with {
                TargetPodStateIdentity = State('u')
            }));

        var settleRequest = new CharacterMemorySettleRequest(
            Address(20),
            captured.ExtractionCommitment,
            State('t')
        );
        CharacterMemorySettleResult applied = fixture.Store.SettleApplied(
            settleRequest
        );
        Assert.Equal(CharacterMemorySettleDisposition.Applied,
            applied.Disposition);
        Assert.Equal(CharacterMemoryCaptureState.Applied,
            applied.Capture.State);
        CharacterMemoryStatusSnapshot status =
            fixture.Store.ReadStatusSnapshot();
        Assert.Null(status.ActiveSourceAction);
        Assert.Equal(State('t'), status.SettledDefaultPodStateIdentity);
        Assert.Equal(CharacterMemorySettleDisposition.AlreadyApplied,
            fixture.Store.SettleApplied(settleRequest).Disposition);
        Assert.Equal(CharacterMemoryPlanDisposition.AlreadyApplied,
            fixture.Store.PlanApply(planRequest).Disposition);

        CharacterMemoryCaptureSnapshot later = fixture.Store.CaptureNew(
            Capture(Address(21), ["later"])
        ).Capture!;
        var laterPlan = new CharacterMemoryPlanRequest(
            Address(21),
            later.ExtractionCommitment,
            State('t'),
            State('u'),
            ["m1:00000003"]
        );
        _ = fixture.Store.PlanApply(laterPlan);
        _ = fixture.Store.SettleApplied(new CharacterMemorySettleRequest(
            Address(21),
            later.ExtractionCommitment,
            State('u')
        ));
        Assert.Equal(CharacterMemorySettleDisposition.AlreadyApplied,
            fixture.Store.SettleApplied(settleRequest).Disposition);
        Assert.Equal(CharacterMemoryProvisionDisposition.AlreadyRecorded,
            fixture.Store.RecordInitialDefaultPod(State('p')).Disposition);
        Assert.Equal(State('u'), fixture.Store.ReadStatusSnapshot()
            .SettledDefaultPodStateIdentity);
    }

    [Fact]
    public void Reject_IsTerminalIdempotentAndReleasesActiveSlot() {
        using var fixture = new ReadyStore();
        CharacterMemoryCaptureSnapshot capture = fixture.Store.CaptureNew(
            Capture(Address(30), ["too large for remaining Pod capacity"])
        ).Capture!;
        var request = new CharacterMemoryRejectRequest(
            Address(30),
            capture.ExtractionCommitment,
            "DEFAULT_POD_CAPACITY"
        );

        Assert.Equal(CharacterMemoryRejectDisposition.Rejected,
            fixture.Store.Reject(request).Disposition);
        Assert.Equal(CharacterMemoryRejectDisposition.AlreadyRejected,
            fixture.Store.Reject(request).Disposition);
        Assert.Null(fixture.Store.ReadStatusSnapshot().ActiveSourceAction);
        Assert.Throws<CharacterMemoryStoreConflictException>(() =>
            fixture.Store.Reject(request with { RejectionCode = "OTHER" }));
        Assert.Equal(CharacterMemoryCaptureDisposition.Captured,
            fixture.Store.CaptureNew(
                Capture(Address(31), ["next"])
            ).Disposition);
    }

    [Fact]
    public void Quarantine_IsGlobalExactAndFailClosed() {
        using var fixture = new ReadyStore();
        CharacterMemoryCaptureSnapshot capture = fixture.Store.CaptureNew(
            Capture(Address(40), ["pending"])
        ).Capture!;
        CharacterMemoryStatusSnapshot before =
            fixture.Store.ReadStatusSnapshot();
        var request = new CharacterMemoryQuarantineRequest(
            before.StoreRevision,
            "POD_STATE_MISMATCH",
            Address(40),
            State('x')
        );

        Assert.Equal(CharacterMemoryQuarantineDisposition.Quarantined,
            fixture.Store.Quarantine(request).Disposition);
        CharacterMemoryStatusSnapshot after =
            fixture.Store.ReadStatusSnapshot();
        Assert.Equal(CharacterMemoryStoreState.Quarantined,
            after.StoreState);
        Assert.Equal("POD_STATE_MISMATCH", after.QuarantineCode);
        Assert.Equal(State('x'),
            after.QuarantineObservedPodStateIdentity);
        Assert.Equal(CharacterMemoryQuarantineDisposition.AlreadyQuarantined,
            fixture.Store.Quarantine(request).Disposition);
        Assert.Throws<CharacterMemoryStoreConflictException>(() =>
            fixture.Store.Quarantine(request with {
                QuarantineCode = "DIFFERENT"
            }));
        Assert.Throws<CharacterMemoryStoreQuarantinedException>(() =>
            fixture.Store.PlanApply(new CharacterMemoryPlanRequest(
                Address(40),
                capture.ExtractionCommitment,
                State('p'),
                State('t'),
                ["m1:00000001"]
            )));
        Assert.Throws<CharacterMemoryStoreQuarantinedException>(() =>
            fixture.Store.CaptureNew(Capture(Address(1), [])));
    }

    [Theory]
    [InlineData("capture-note-action")]
    [InlineData("plan-note-apply")]
    [InlineData("settle-note-apply")]
    [InlineData("reject-note-apply")]
    [InlineData("quarantine-character-memory")]
    public void AfterCommitHook_ReopensAndAcceptsOnlyExactPostState(
        string targetOperation
    ) {
        int fired = 0;
        var hooks = new CharacterMemoryStoreTestHooks(
            AfterCommitBeforeReturn: operation => {
                if (string.Equals(operation, targetOperation,
                        StringComparison.Ordinal)
                    && Interlocked.Exchange(ref fired, 1) == 0) {
                    throw new IOException("simulated response loss");
                }
            }
        );
        using var fixture = new ReadyStore(hooks);
        CharacterMemoryCaptureSnapshot capture = fixture.Store.CaptureNew(
            Capture(Address(50), ["note"])
        ).Capture!;
        var plan = new CharacterMemoryPlanRequest(
            Address(50),
            capture.ExtractionCommitment,
            State('p'),
            State('t'),
            ["m1:00000001"]
        );
        if (targetOperation == "capture-note-action") {
            Assert.Equal(CharacterMemoryCaptureState.Captured,
                fixture.Store.ReadCaptureExact(Address(50))!.State);
            Assert.Equal(1, fired);
            return;
        }
        if (targetOperation == "reject-note-apply") {
            _ = fixture.Store.Reject(new CharacterMemoryRejectRequest(
                Address(50),
                capture.ExtractionCommitment,
                "CAPACITY"
            ));
            Assert.Equal(CharacterMemoryCaptureState.Rejected,
                fixture.Store.ReadCaptureExact(Address(50))!.State);
            Assert.Equal(1, fired);
            return;
        }
        _ = fixture.Store.PlanApply(plan);
        if (targetOperation == "plan-note-apply") {
            Assert.Equal(CharacterMemoryCaptureState.Planned,
                fixture.Store.ReadCaptureExact(Address(50))!.State);
            Assert.Equal(1, fired);
            return;
        }
        if (targetOperation == "quarantine-character-memory") {
            CharacterMemoryStatusSnapshot status =
                fixture.Store.ReadStatusSnapshot();
            _ = fixture.Store.Quarantine(new CharacterMemoryQuarantineRequest(
                status.StoreRevision,
                "MISMATCH",
                Address(50),
                State('x')
            ));
            Assert.Equal(CharacterMemoryStoreState.Quarantined,
                fixture.Store.ReadStatusSnapshot().StoreState);
            Assert.Equal(1, fired);
            return;
        }
        _ = fixture.Store.SettleApplied(new CharacterMemorySettleRequest(
            Address(50),
            capture.ExtractionCommitment,
            State('t')
        ));
        Assert.Equal(CharacterMemoryCaptureState.Applied,
            fixture.Store.ReadCaptureExact(Address(50))!.State);
        Assert.Equal(1, fired);
    }

    [Fact]
    public void BeforeCommitHook_RollsBackWholeCapture() {
        int fired = 0;
        var hooks = new CharacterMemoryStoreTestHooks(
            BeforeCommit: operation => {
                if (operation == "capture-note-action"
                    && Interlocked.Exchange(ref fired, 1) == 0) {
                    throw new IOException("before commit");
                }
            }
        );
        using var fixture = new ReadyStore(hooks);

        Assert.Throws<IOException>(() => fixture.Store.CaptureNew(
            Capture(Address(60), ["first", "second"])
        ));
        Assert.Null(fixture.Store.ReadCaptureExact(Address(60)));
        Assert.Null(fixture.Store.ReadStatusSnapshot().ActiveSourceAction);
        Assert.Equal(CharacterMemoryCaptureDisposition.Captured,
            fixture.Store.CaptureNew(
                Capture(Address(60), ["first", "second"])
            ).Disposition);
    }

    [Fact]
    public void StrictReopenRejectsCommitmentCorruption() {
        using var fixture = new ReadyStore();
        _ = fixture.Store.CaptureNew(Capture(Address(70), ["note"]));
        string directory = fixture.DirectoryPath;
        CharacterMemoryStoreOwner owner = fixture.Owner;
        fixture.DisposeStore();
        ExecuteSql(
            System.IO.Path.Combine(
                directory,
                CharacterMemorySqliteStore.DatabaseFileName
            ),
            "UPDATE note_action_capture SET extraction_commitment = '"
                + Sha('f') + "' WHERE source_action_address = '"
                + Address(70) + "';"
        );

        Assert.Throws<InvalidDataException>(() =>
            CharacterMemorySqliteStore.OpenExisting(directory, owner));
    }

    private static CharacterMemoryStoreOwner Owner() => new(
        "user",
        "session-repository-id"
    );

    private static CharacterMemoryStoreBaseline Baseline() {
        string selectedHead = Address(2);
        EventAddress address = EventAddressTextCodec.Parse(selectedHead);
        return new CharacterMemoryStoreBaseline(
            new EventJournalPhysicalAppendFrontier(
                address.SegmentNumber,
                address.Ticket.EndOffsetExclusive
            ),
            selectedHead
        );
    }

    private static CharacterMemoryCaptureRequest Capture(
        string source,
        IReadOnlyList<string> exactTexts
    ) => new(
        source,
        Sha('a'),
        VisibleActionUtf8Bytes: 12,
        "extractor-contract-v1",
        exactTexts
    );

    private static string Address(
        int value,
        uint segmentNumber = 1
    ) => $"ej1:{value:x16}{segmentNumber:x8}00000000";

    private static string Sha(char value) => new(value, 64);

    private static string State(char value) =>
        "mps1-" + new string(value, 64);

    private static void ExecuteSql(string databasePath, string sql) {
        var builder = new SqliteConnectionStringBuilder {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class ReadyStore : IDisposable {
        private readonly StoreDirectory _directory = new();
        private CharacterMemorySqliteStore? _store;

        internal ReadyStore(CharacterMemoryStoreTestHooks? hooks = null) {
            Owner = CharacterMemorySqliteStoreTests.Owner();
            _store = CharacterMemorySqliteStore.CreateNew(
                _directory.Path,
                Owner,
                Baseline(),
                State('p'),
                hooks
            );
            _ = _store.RecordInitialDefaultPod(State('p'));
        }

        internal CharacterMemoryStoreOwner Owner { get; }
        internal string DirectoryPath => _directory.Path;
        internal CharacterMemorySqliteStore Store => _store
            ?? throw new ObjectDisposedException(nameof(ReadyStore));

        internal void DisposeStore() {
            _store?.Dispose();
            _store = null;
        }

        public void Dispose() {
            DisposeStore();
            _directory.Dispose();
        }
    }

    private sealed class StoreDirectory : IDisposable {
        internal StoreDirectory() {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "atelia-character-memory-store-"
                    + Guid.NewGuid().ToString("N")
            );
            TestDirectorySafety.EnsureExistingPathChainHasNoReparsePoint(Path);
        }

        internal string Path { get; }

        public void Dispose() =>
            TestDirectorySafety.DeleteOwnedTreeNoFollow(Path);
    }
}
