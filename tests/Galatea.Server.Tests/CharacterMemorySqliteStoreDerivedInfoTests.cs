using Atelia.Data;
using Atelia.EventJournal;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.SessionJournal;
using Atelia.Testing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class CharacterMemorySqliteStoreTestsV2 {
    [Fact]
    public void ExactSettlementCreatesPendingAndPreparedBatchesRemainQueued() {
        using var fixture = new ReadyStore();
        CharacterMemoryDerivedInfoWorkSnapshot first = ApplyCapture(
            fixture.Store,
            Address(10),
            State('p'),
            State('a'),
            ["first", "second"],
            ["m1:00000001", "m1:00000002"]
        );
        CharacterMemoryDerivedInfoWorkSnapshot second = ApplyCapture(
            fixture.Store,
            Address(11),
            State('a'),
            State('b'),
            ["third"],
            ["m1:00000003"]
        );

        Assert.Equal(CharacterMemoryDerivedInfoState.Pending, first.State);
        Assert.Equal(CharacterMemoryDerivedInfoState.Pending, second.State);
        Assert.Equal(Address(10), fixture.Store.ReadNextDerivedInfoWork()!
            .SourceActionAddress);

        CharacterMemoryPrepareDerivedInfoResult prepared =
            fixture.Store.PrepareDerivedInfo(Prepare(first, "one"));
        Assert.Equal(CharacterMemoryPrepareDerivedInfoDisposition.Prepared,
            prepared.Disposition);
        Assert.Equal(CharacterMemoryDerivedInfoState.Prepared,
            prepared.Work.State);
        Assert.NotNull(prepared.Work.DerivedInfoCommitment);
        Assert.Null(fixture.Store.ReadStatusSnapshot()
            .ActiveDerivedInfoSourceAction);
        CharacterMemoryPrepareDerivedInfoResult preparedSecond =
            fixture.Store.PrepareDerivedInfo(Prepare(second, "two"));
        Assert.Equal(CharacterMemoryDerivedInfoState.Prepared,
            preparedSecond.Work.State);
        Assert.Null(fixture.Store.ReadStatusSnapshot()
            .ActiveDerivedInfoSourceAction);

        CharacterMemoryCaptureResult later = fixture.Store.CaptureNew(
            Capture(Address(12), ["prepared does not block capture"])
        );
        Assert.Equal(CharacterMemoryCaptureDisposition.Captured,
            later.Disposition);
        Assert.Equal(Address(10), fixture.Store.ReadNextDerivedInfoWork()!
            .SourceActionAddress);
    }

    [Fact]
    public void PrepareIsExactAtomicAndCommitmentIsRuntimeComputed() {
        using var fixture = new ReadyStore();
        CharacterMemoryDerivedInfoWorkSnapshot pending = ApplyCapture(
            fixture.Store,
            Address(20),
            State('p'),
            State('a'),
            ["first", "second"],
            ["m1:00000001", "m1:00000002"]
        );
        CharacterMemoryPrepareDerivedInfoRequest request = Prepare(pending, "v1");

        CharacterMemoryPrepareDerivedInfoResult prepared =
            fixture.Store.PrepareDerivedInfo(request);
        string expected = CharacterMemorySqliteStore.ComputeDerivedInfoCommitment(
            pending.SourceActionAddress,
            pending.ExtractionCommitment,
            request.EnricherContractId,
            request.Values
        );
        Assert.Equal(expected, prepared.Work.DerivedInfoCommitment);
        Assert.Equal(["Title v1 0", "Title v1 1"],
            prepared.Work.Notes.Select(static note => note.Title));
        long revision = prepared.StoreRevision;
        Assert.Equal(CharacterMemoryPrepareDerivedInfoDisposition.AlreadyPrepared,
            fixture.Store.PrepareDerivedInfo(request).Disposition);
        Assert.Equal(revision, fixture.Store.ReadStatusSnapshot().StoreRevision);

        Assert.Throws<CharacterMemoryStoreConflictException>(() =>
            fixture.Store.PrepareDerivedInfo(request with {
                Values = request.Values.Select(value => value with {
                    Gist = value.Gist + " changed"
                }).ToArray()
            }));
        Assert.Equal(["Gist v1 0", "Gist v1 1"],
            fixture.Store.ReadDerivedInfoWorkExact(Address(20))!.Notes
                .Select(static note => note.Gist));
        Assert.Throws<ArgumentException>(() =>
            fixture.Store.PrepareDerivedInfo(request with {
                Values = [request.Values[1], request.Values[0]]
            }));
    }

    [Fact]
    public void PrepareRejectsIncompleteOrNoncanonicalBatchBeforeMutation() {
        using var fixture = new ReadyStore();
        CharacterMemoryDerivedInfoWorkSnapshot pending = ApplyCapture(
            fixture.Store,
            Address(21),
            State('p'),
            State('a'),
            ["first", "second"],
            ["m1:00000001", "m1:00000002"]
        );
        CharacterMemoryPrepareDerivedInfoRequest valid = Prepare(pending, "bounds");
        long revision = fixture.Store.ReadStatusSnapshot().StoreRevision;

        Assert.Throws<CharacterMemoryStoreConflictException>(() =>
            fixture.Store.PrepareDerivedInfo(valid with {
                Values = [valid.Values[0]]
            }));
        Assert.Throws<ArgumentException>(() =>
            fixture.Store.PrepareDerivedInfo(valid with {
                Values = valid.Values.Select(value => value with {
                    Title = " untrimmed"
                }).ToArray()
            }));
        Assert.Throws<ArgumentException>(() =>
            fixture.Store.PrepareDerivedInfo(valid with {
                Values = valid.Values.Select(value => value with {
                    Gist = "control\ncharacter"
                }).ToArray()
            }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            fixture.Store.PrepareDerivedInfo(valid with {
                Values = valid.Values.Select(value => value with {
                    Summary = new string('x', 8193)
                }).ToArray()
            }));
        CharacterMemoryDerivedInfoWorkSnapshot unchanged =
            fixture.Store.ReadDerivedInfoWorkExact(Address(21))!;
        Assert.Equal(CharacterMemoryDerivedInfoState.Pending, unchanged.State);
        Assert.Equal(revision, fixture.Store.ReadStatusSnapshot().StoreRevision);
    }

    [Fact]
    public void PlannedDerivedInfoOwnsOnlyMutationSlotAndSettlesAtomically() {
        using var fixture = new ReadyStore();
        CharacterMemoryDerivedInfoWorkSnapshot first = ApplyCapture(
            fixture.Store,
            Address(30),
            State('p'),
            State('a'),
            ["first"],
            ["m1:00000001"]
        );
        CharacterMemoryDerivedInfoWorkSnapshot prepared = fixture.Store
            .PrepareDerivedInfo(Prepare(first, "first")).Work;

        CharacterMemoryCaptureSnapshot exact = fixture.Store.CaptureNew(
            Capture(Address(31), ["second"])
        ).Capture!;
        var derivedPlan = new CharacterMemoryPlanDerivedInfoRequest(
            first.SourceActionAddress,
            first.ExtractionCommitment,
            prepared.DerivedInfoCommitment!,
            State('a'),
            State('c')
        );
        Assert.Throws<CharacterMemoryStoreConflictException>(() =>
            fixture.Store.PlanDerivedInfo(derivedPlan));

        _ = fixture.Store.PlanApply(new CharacterMemoryPlanRequest(
            Address(31),
            exact.ExtractionCommitment,
            State('a'),
            State('b'),
            ["m1:00000002"]
        ));
        _ = fixture.Store.SettleApplied(new CharacterMemorySettleRequest(
            Address(31),
            exact.ExtractionCommitment,
            State('b')
        ));
        derivedPlan = derivedPlan with {
            BasePodStateIdentity = State('b')
        };
        CharacterMemoryPlanDerivedInfoResult planned =
            fixture.Store.PlanDerivedInfo(derivedPlan);
        Assert.Equal(CharacterMemoryPlanDerivedInfoDisposition.Planned,
            planned.Disposition);
        Assert.Equal(Address(30), fixture.Store.ReadStatusSnapshot()
            .ActiveDerivedInfoSourceAction);
        Assert.Throws<CharacterMemoryStoreConflictException>(() =>
            fixture.Store.CaptureNew(Capture(Address(32), ["blocked"])));
        Assert.Throws<CharacterMemoryStoreConflictException>(() =>
            fixture.Store.RejectDerivedInfo(new(
                first.SourceActionAddress,
                first.ExtractionCommitment,
                "DO_NOT_DROP_PLANNED"
            )));

        CharacterMemorySettleDerivedInfoResult settled =
            fixture.Store.SettleDerivedInfoApplied(new(
                first.SourceActionAddress,
                first.ExtractionCommitment,
                prepared.DerivedInfoCommitment!,
                State('c')
            ));
        Assert.Equal(CharacterMemorySettleDerivedInfoDisposition.Applied,
            settled.Disposition);
        CharacterMemoryStatusSnapshot status =
            fixture.Store.ReadStatusSnapshot();
        Assert.Equal(State('c'), status.SettledDefaultPodStateIdentity);
        Assert.Null(status.ActiveDerivedInfoSourceAction);
        Assert.Equal(CharacterMemoryCaptureDisposition.Captured,
            fixture.Store.CaptureNew(Capture(Address(32), ["released"]))
                .Disposition);
    }

    [Fact]
    public void RejectIsTerminalExactAndDoesNotAffectOtherPendingWork() {
        using var fixture = new ReadyStore();
        CharacterMemoryDerivedInfoWorkSnapshot first = ApplyCapture(
            fixture.Store,
            Address(40),
            State('p'),
            State('a'),
            ["first"],
            ["m1:00000001"]
        );
        CharacterMemoryDerivedInfoWorkSnapshot second = ApplyCapture(
            fixture.Store,
            Address(41),
            State('a'),
            State('b'),
            ["second"],
            ["m1:00000002"]
        );
        var request = new CharacterMemoryRejectDerivedInfoRequest(
            first.SourceActionAddress,
            first.ExtractionCommitment,
            "SOURCE_TURN_UNAVAILABLE"
        );

        Assert.Equal(CharacterMemoryRejectDerivedInfoDisposition.Rejected,
            fixture.Store.RejectDerivedInfo(request).Disposition);
        Assert.Equal(CharacterMemoryRejectDerivedInfoDisposition.AlreadyRejected,
            fixture.Store.RejectDerivedInfo(request).Disposition);
        Assert.Throws<CharacterMemoryStoreConflictException>(() =>
            fixture.Store.RejectDerivedInfo(request with {
                RejectionCode = "DIFFERENT"
            }));
        Assert.Equal(second.SourceActionAddress,
            fixture.Store.ReadNextDerivedInfoWork()!.SourceActionAddress);
    }

    [Theory]
    [InlineData("prepare-derived-info")]
    [InlineData("plan-derived-info")]
    [InlineData("settle-derived-info")]
    [InlineData("reject-derived-info")]
    public void DerivedInfoAfterCommitLossAcceptsOnlyExactPostState(
        string targetOperation
    ) {
        int fired = 0;
        var hooks = new CharacterMemoryStoreTestHooks(
            AfterCommitBeforeReturn: operation => {
                if (operation == targetOperation
                    && Interlocked.Exchange(ref fired, 1) == 0) {
                    throw new IOException("simulated response loss");
                }
            }
        );
        using var fixture = new ReadyStore(hooks);
        CharacterMemoryDerivedInfoWorkSnapshot pending = ApplyCapture(
            fixture.Store,
            Address(50),
            State('p'),
            State('a'),
            ["note"],
            ["m1:00000001"]
        );
        CharacterMemoryPrepareDerivedInfoRequest prepare = Prepare(pending, "loss");
        CharacterMemoryDerivedInfoWorkSnapshot work = fixture.Store
            .PrepareDerivedInfo(prepare).Work;
        if (targetOperation == "prepare-derived-info") {
            Assert.Equal(CharacterMemoryDerivedInfoState.Prepared, work.State);
            Assert.Equal(1, fired);
            return;
        }
        if (targetOperation == "reject-derived-info") {
            CharacterMemoryRejectDerivedInfoResult rejected =
                fixture.Store.RejectDerivedInfo(new(
                    pending.SourceActionAddress,
                    pending.ExtractionCommitment,
                    "NO_CONTEXT"
                ));
            Assert.Equal(CharacterMemoryDerivedInfoState.Rejected,
                rejected.Work.State);
            Assert.Equal(1, fired);
            return;
        }
        var plan = new CharacterMemoryPlanDerivedInfoRequest(
            pending.SourceActionAddress,
            pending.ExtractionCommitment,
            work.DerivedInfoCommitment!,
            State('a'),
            State('b')
        );
        work = fixture.Store.PlanDerivedInfo(plan).Work;
        if (targetOperation == "plan-derived-info") {
            Assert.Equal(CharacterMemoryDerivedInfoState.Planned, work.State);
            Assert.Equal(1, fired);
            return;
        }
        work = fixture.Store.SettleDerivedInfoApplied(new(
            pending.SourceActionAddress,
            pending.ExtractionCommitment,
            work.DerivedInfoCommitment!,
            State('b')
        )).Work;
        Assert.Equal(CharacterMemoryDerivedInfoState.Applied, work.State);
        Assert.Equal(1, fired);
    }

    [Theory]
    [InlineData("prepare-derived-info")]
    [InlineData("plan-derived-info")]
    [InlineData("settle-derived-info")]
    [InlineData("reject-derived-info")]
    public void DerivedInfoBeforeCommitFailureRollsBackWholeTransition(
        string targetOperation
    ) {
        int fired = 0;
        var hooks = new CharacterMemoryStoreTestHooks(
            BeforeCommit: operation => {
                if (operation == targetOperation
                    && Interlocked.Exchange(ref fired, 1) == 0) {
                    throw new IOException("simulated pre-commit failure");
                }
            }
        );
        using var fixture = new ReadyStore(hooks);
        CharacterMemoryDerivedInfoWorkSnapshot pending = ApplyCapture(
            fixture.Store,
            Address(55),
            State('p'),
            State('a'),
            ["note"],
            ["m1:00000001"]
        );
        CharacterMemoryPrepareDerivedInfoRequest prepare = Prepare(pending, "rollback");
        if (targetOperation == "prepare-derived-info") {
            Assert.Throws<IOException>(() =>
                fixture.Store.PrepareDerivedInfo(prepare));
            CharacterMemoryDerivedInfoWorkSnapshot rolledBack =
                fixture.Store.ReadDerivedInfoWorkExact(Address(55))!;
            Assert.Equal(CharacterMemoryDerivedInfoState.Pending,
                rolledBack.State);
            Assert.All(rolledBack.Notes, static note => {
                Assert.Null(note.Title);
                Assert.Null(note.Gist);
                Assert.Null(note.Summary);
            });
            Assert.Equal(CharacterMemoryPrepareDerivedInfoDisposition.Prepared,
                fixture.Store.PrepareDerivedInfo(prepare).Disposition);
            return;
        }

        CharacterMemoryDerivedInfoWorkSnapshot prepared = fixture.Store
            .PrepareDerivedInfo(prepare).Work;
        if (targetOperation == "reject-derived-info") {
            var reject = new CharacterMemoryRejectDerivedInfoRequest(
                pending.SourceActionAddress,
                pending.ExtractionCommitment,
                "NO_CONTEXT"
            );
            Assert.Throws<IOException>(() =>
                fixture.Store.RejectDerivedInfo(reject));
            Assert.Equal(CharacterMemoryDerivedInfoState.Prepared,
                fixture.Store.ReadDerivedInfoWorkExact(Address(55))!.State);
            Assert.Equal(CharacterMemoryRejectDerivedInfoDisposition.Rejected,
                fixture.Store.RejectDerivedInfo(reject).Disposition);
            return;
        }

        var plan = new CharacterMemoryPlanDerivedInfoRequest(
            pending.SourceActionAddress,
            pending.ExtractionCommitment,
            prepared.DerivedInfoCommitment!,
            State('a'),
            State('b')
        );
        if (targetOperation == "plan-derived-info") {
            Assert.Throws<IOException>(() =>
                fixture.Store.PlanDerivedInfo(plan));
            Assert.Equal(CharacterMemoryDerivedInfoState.Prepared,
                fixture.Store.ReadDerivedInfoWorkExact(Address(55))!.State);
            Assert.Null(fixture.Store.ReadStatusSnapshot()
                .ActiveDerivedInfoSourceAction);
            Assert.Equal(CharacterMemoryPlanDerivedInfoDisposition.Planned,
                fixture.Store.PlanDerivedInfo(plan).Disposition);
            return;
        }

        _ = fixture.Store.PlanDerivedInfo(plan);
        var settle = new CharacterMemorySettleDerivedInfoRequest(
            pending.SourceActionAddress,
            pending.ExtractionCommitment,
            prepared.DerivedInfoCommitment!,
            State('b')
        );
        Assert.Throws<IOException>(() =>
            fixture.Store.SettleDerivedInfoApplied(settle));
        CharacterMemoryStatusSnapshot status =
            fixture.Store.ReadStatusSnapshot();
        Assert.Equal(CharacterMemoryDerivedInfoState.Planned,
            status.ActiveDerivedInfoWork!.State);
        Assert.Equal(State('a'), status.SettledDefaultPodStateIdentity);
        Assert.Equal(CharacterMemorySettleDerivedInfoDisposition.Applied,
            fixture.Store.SettleDerivedInfoApplied(settle).Disposition);
    }

    [Fact]
    public void V1StrictStoreMigratesOnceAndCreatesPendingForAppliedOnly() {
        using var fixture = V1Store.Create(valid: true);
        using (CharacterMemorySqliteStore store =
               CharacterMemorySqliteStore.OpenExisting(
                   fixture.Path,
                   Owner()
               )) {
            Assert.Equal(2, ReadUserVersion(fixture.DatabasePath));
            CharacterMemoryStatusSnapshot status = store.ReadStatusSnapshot();
            Assert.Equal(8, status.StoreRevision);
            Assert.Equal(Address(63), status.ActiveSourceAction);
            Assert.Null(status.ActiveDerivedInfoSourceAction);
            CharacterMemoryDerivedInfoWorkSnapshot pending =
                store.ReadDerivedInfoWorkExact(Address(60))!;
            Assert.Equal(CharacterMemoryDerivedInfoState.Pending, pending.State);
            Assert.Equal(4, pending.CreatedRevision);
            Assert.Null(store.ReadDerivedInfoWorkExact(Address(61)));
            Assert.Null(store.ReadDerivedInfoWorkExact(Address(62)));
            Assert.Null(store.ReadDerivedInfoWorkExact(Address(63)));
        }

        using CharacterMemorySqliteStore reopened =
            CharacterMemorySqliteStore.OpenExisting(fixture.Path, Owner());
        Assert.Equal(CharacterMemoryDerivedInfoState.Pending,
            reopened.ReadNextDerivedInfoWork()!.State);
    }

    [Fact]
    public void V1MigrationValidatesHistoricalCommitmentsBeforeMutation() {
        using var fixture = V1Store.Create(valid: false);

        Assert.Throws<InvalidDataException>(() =>
            CharacterMemorySqliteStore.OpenExisting(fixture.Path, Owner()));
        Assert.Equal(1, ReadUserVersion(fixture.DatabasePath));
        Assert.False(TableExists(fixture.DatabasePath, "derived_info_work"));
    }

    [Fact]
    public void V1MigrationAfterCommitLossStrictlyReopensV2() {
        using var fixture = V1Store.Create(valid: true);
        int fired = 0;
        var hooks = new CharacterMemoryStoreTestHooks(
            AfterCommitBeforeReturn: operation => {
                if (operation == "migrate-character-memory-v1-to-v2"
                    && Interlocked.Exchange(ref fired, 1) == 0) {
                    throw new IOException("simulated migration response loss");
                }
            }
        );

        using CharacterMemorySqliteStore store =
            CharacterMemorySqliteStore.OpenExisting(
                fixture.Path,
                Owner(),
                hooks
            );
        Assert.Equal(1, fired);
        Assert.Equal(2, ReadUserVersion(fixture.DatabasePath));
        Assert.NotNull(store.ReadDerivedInfoWorkExact(Address(60)));
    }

    [Fact]
    public void V1MigrationBeforeCommitFailureLeavesExactV1Store() {
        using var fixture = V1Store.Create(valid: true);
        int fired = 0;
        var hooks = new CharacterMemoryStoreTestHooks(
            BeforeCommit: operation => {
                if (operation == "migrate-character-memory-v1-to-v2"
                    && Interlocked.Exchange(ref fired, 1) == 0) {
                    throw new IOException("simulated migration pre-commit failure");
                }
            }
        );

        Assert.Throws<IOException>(() =>
            CharacterMemorySqliteStore.OpenExisting(
                fixture.Path,
                Owner(),
                hooks
            ));
        Assert.Equal(1, fired);
        Assert.Equal(1, ReadUserVersion(fixture.DatabasePath));
        Assert.False(TableExists(fixture.DatabasePath, "derived_info_work"));

        using CharacterMemorySqliteStore migrated =
            CharacterMemorySqliteStore.OpenExisting(fixture.Path, Owner());
        Assert.Equal(2, ReadUserVersion(fixture.DatabasePath));
        Assert.NotNull(migrated.ReadDerivedInfoWorkExact(Address(60)));
    }

    [Fact]
    public void V1MigrationRejectsSchemaDriftBeforeMutation() {
        using var fixture = V1Store.Create(valid: true);
        ExecuteSql(fixture.DatabasePath, """
            PRAGMA writable_schema = ON;
            UPDATE sqlite_schema
            SET sql = replace(
                sql,
                'CHECK(visible_action_utf8_bytes >= 0)',
                'CHECK(visible_action_utf8_bytes >= 1)'
            )
            WHERE type = 'table' AND name = 'note_action_capture';
            PRAGMA writable_schema = OFF;
            PRAGMA schema_version = 999;
            """);

        Assert.Throws<InvalidDataException>(() =>
            CharacterMemorySqliteStore.OpenExisting(fixture.Path, Owner()));
        Assert.Equal(1, ReadUserVersion(fixture.DatabasePath));
        Assert.False(TableExists(fixture.DatabasePath, "derived_info_work"));
    }

    [Fact]
    public void StrictV2ReopenRejectsPartialDerivedInfoBatch() {
        using var fixture = new ReadyStore();
        _ = ApplyCapture(
            fixture.Store,
            Address(70),
            State('p'),
            State('a'),
            ["first", "second"],
            ["m1:00000001", "m1:00000002"]
        );
        string path = fixture.DatabasePath;
        fixture.DisposeStore();
        ExecuteSql(path, """
            UPDATE character_note SET derived_title = 'partial'
            WHERE source_action_address = $source AND artifact_ordinal = 0;
            """, ("$source", Address(70)));

        Assert.Throws<InvalidDataException>(() =>
            CharacterMemorySqliteStore.OpenExisting(
                fixture.DirectoryPath,
                fixture.Owner
            ));
    }

    [Fact]
    public void StrictV2ReopenRecomputesHistoricalDerivedInfoCommitment() {
        using var fixture = new ReadyStore();
        CharacterMemoryDerivedInfoWorkSnapshot pending = ApplyCapture(
            fixture.Store,
            Address(71),
            State('p'),
            State('a'),
            ["note"],
            ["m1:00000001"]
        );
        _ = fixture.Store.PrepareDerivedInfo(Prepare(pending, "strict"));
        string path = fixture.DatabasePath;
        fixture.DisposeStore();
        ExecuteSql(path, """
            UPDATE derived_info_work SET derived_info_commitment = $hash
            WHERE source_action_address = $source;
            """, ("$hash", Sha('f')), ("$source", Address(71)));

        Assert.Throws<InvalidDataException>(() =>
            CharacterMemorySqliteStore.OpenExisting(
                fixture.DirectoryPath,
                fixture.Owner
            ));
    }

    private static CharacterMemoryDerivedInfoWorkSnapshot ApplyCapture(
        CharacterMemorySqliteStore store,
        string source,
        string baseIdentity,
        string targetIdentity,
        IReadOnlyList<string> texts,
        IReadOnlyList<string> memoIds
    ) {
        CharacterMemoryCaptureSnapshot capture = store.CaptureNew(
            Capture(source, texts)
        ).Capture!;
        _ = store.PlanApply(new CharacterMemoryPlanRequest(
            source,
            capture.ExtractionCommitment,
            baseIdentity,
            targetIdentity,
            memoIds
        ));
        _ = store.SettleApplied(new CharacterMemorySettleRequest(
            source,
            capture.ExtractionCommitment,
            targetIdentity
        ));
        return store.ReadDerivedInfoWorkExact(source)!;
    }

    private static CharacterMemoryPrepareDerivedInfoRequest Prepare(
        CharacterMemoryDerivedInfoWorkSnapshot work,
        string suffix
    ) => new(
        work.SourceActionAddress,
        work.ExtractionCommitment,
        "derived-info-enricher-contract-v1",
        work.Notes.Select(note => new CharacterMemoryDerivedInfoValue(
            note.ArtifactOrdinal,
            $"Title {suffix} {note.ArtifactOrdinal}",
            $"Gist {suffix} {note.ArtifactOrdinal}",
            $"Summary {suffix} {note.ArtifactOrdinal}"
        )).ToArray()
    );

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
        12,
        "extractor-contract-v1",
        exactTexts
    );

    private static string Address(int value) => EventAddressTextCodec.Format(
        new EventAddress(
            SizedPtr.Create(checked(value * 4L), 4),
            1,
            AddressHint.None
        )
    );

    private static string Sha(char value) => new(value, 64);

    private static string State(char value) =>
        "mps1-" + new string(value, 64);

    private static void ExecuteSql(
        string databasePath,
        string sql,
        params (string Name, object Value)[] parameters
    ) {
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
        foreach ((string name, object value) in parameters) {
            command.Parameters.AddWithValue(name, value);
        }
        _ = command.ExecuteNonQuery();
    }

    private static long ReadUserVersion(string databasePath) {
        var builder = new SqliteConnectionStringBuilder {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static bool TableExists(string databasePath, string table) {
        var builder = new SqliteConnectionStringBuilder {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM sqlite_schema
            WHERE type = 'table' AND name = $table;
            """;
        command.Parameters.AddWithValue("$table", table);
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }

    private sealed class ReadyStore : IDisposable {
        private readonly StoreDirectory _directory = new();
        private CharacterMemorySqliteStore? _store;

        internal ReadyStore(CharacterMemoryStoreTestHooks? hooks = null) {
            Owner = CharacterMemorySqliteStoreTestsV2.Owner();
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
        internal string DatabasePath => System.IO.Path.Combine(
            DirectoryPath,
            CharacterMemorySqliteStore.DatabaseFileName
        );
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

    private sealed class V1Store : IDisposable {
        private readonly StoreDirectory _directory;

        private V1Store(StoreDirectory directory) {
            _directory = directory;
        }

        internal string Path => _directory.Path;
        internal string DatabasePath => System.IO.Path.Combine(
            Path,
            CharacterMemorySqliteStore.DatabaseFileName
        );

        internal static V1Store Create(bool valid) {
            var directory = new StoreDirectory(createLeaf: true);
            using (File.Create(System.IO.Path.Combine(
                directory.Path,
                CharacterMemorySqliteStore.LockFileName
            ))) { }
            string databasePath = System.IO.Path.Combine(
                directory.Path,
                CharacterMemorySqliteStore.DatabaseFileName
            );
            var builder = new SqliteConnectionStringBuilder {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = V1SchemaSql + """

                INSERT INTO character_memory_meta VALUES (
                    1, 1, $user, $repository, 1, 12, $head, 'Ready',
                    $provision, $settled, $active, NULL, NULL, 8
                );

                INSERT INTO note_action_capture VALUES (
                    $applied, $visible_hash, 12, $contract,
                    $applied_commitment, 1, 'Applied', $provision, $settled,
                    NULL, 4
                );
                INSERT INTO character_note VALUES (
                    $applied, 0, 'applied note', 'm1:00000001'
                );

                INSERT INTO note_action_capture VALUES (
                    $zero, $visible_hash, 12, $contract,
                    $zero_commitment, 0, 'ZeroCaptured', NULL, NULL, NULL, 5
                );

                INSERT INTO note_action_capture VALUES (
                    $rejected, $visible_hash, 12, $contract,
                    $rejected_commitment, 1, 'Rejected', NULL, NULL,
                    'CAPACITY', 6
                );
                INSERT INTO character_note VALUES (
                    $rejected, 0, 'rejected note', NULL
                );

                INSERT INTO note_action_capture VALUES (
                    $active, $visible_hash, 12, $contract,
                    $active_commitment, 1, 'Captured', NULL, NULL, NULL, 8
                );
                INSERT INTO character_note VALUES (
                    $active, 0, 'active note', NULL
                );
                """;
            command.Parameters.AddWithValue("$user", Owner().UserId);
            command.Parameters.AddWithValue("$repository", Owner().SessionRepositoryId);
            command.Parameters.AddWithValue("$head", Address(2));
            command.Parameters.AddWithValue("$provision", State('p'));
            command.Parameters.AddWithValue("$settled", State('a'));
            command.Parameters.AddWithValue("$applied", Address(60));
            command.Parameters.AddWithValue("$zero", Address(61));
            command.Parameters.AddWithValue("$rejected", Address(62));
            command.Parameters.AddWithValue("$active", Address(63));
            command.Parameters.AddWithValue("$visible_hash", Sha('a'));
            command.Parameters.AddWithValue("$contract", "extractor-contract-v1");
            command.Parameters.AddWithValue("$applied_commitment", valid
                ? Commitment(Address(60), ["applied note"])
                : Sha('f'));
            command.Parameters.AddWithValue("$zero_commitment",
                Commitment(Address(61), []));
            command.Parameters.AddWithValue("$rejected_commitment",
                Commitment(Address(62), ["rejected note"]));
            command.Parameters.AddWithValue("$active_commitment",
                Commitment(Address(63), ["active note"]));
            _ = command.ExecuteNonQuery();
            return new V1Store(directory);
        }

        private static string Commitment(
            string source,
            IReadOnlyList<string> texts
        ) => CharacterMemorySqliteStore.ComputeExtractionCommitment(
            source,
            Sha('a'),
            12,
            "extractor-contract-v1",
            texts
        );

        public void Dispose() => _directory.Dispose();

        private const string V1SchemaSql = """
            PRAGMA page_size = 4096;
            PRAGMA application_id = 1195593009;
            PRAGMA user_version = 1;
            PRAGMA foreign_keys = ON;

            CREATE TABLE character_memory_meta (
                singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton = 1),
                schema_version INTEGER NOT NULL CHECK(schema_version = 1),
                user_id TEXT NOT NULL,
                session_repository_id TEXT NOT NULL,
                capture_frontier_segment_number INTEGER NOT NULL
                    CHECK(capture_frontier_segment_number BETWEEN 1 AND 4294967295),
                capture_frontier_tail_offset INTEGER NOT NULL
                    CHECK(capture_frontier_tail_offset >= 4
                        AND capture_frontier_tail_offset % 4 = 0),
                baseline_selected_head TEXT NULL,
                store_state TEXT NOT NULL CHECK(store_state IN (
                    'Provisioning', 'Ready', 'Quarantined'
                )),
                provision_target_pod_state_identity TEXT NOT NULL,
                settled_default_pod_state_identity TEXT NULL,
                active_source_action TEXT NULL,
                quarantine_code TEXT NULL,
                quarantine_observed_pod_state_identity TEXT NULL,
                store_revision INTEGER NOT NULL CHECK(store_revision >= 0),
                CHECK(
                    (store_state = 'Provisioning'
                        AND settled_default_pod_state_identity IS NULL
                        AND active_source_action IS NULL
                        AND quarantine_code IS NULL
                        AND quarantine_observed_pod_state_identity IS NULL)
                    OR (store_state = 'Ready'
                        AND settled_default_pod_state_identity IS NOT NULL
                        AND quarantine_code IS NULL
                        AND quarantine_observed_pod_state_identity IS NULL)
                    OR (store_state = 'Quarantined'
                        AND quarantine_code IS NOT NULL)
                )
            ) STRICT;

            CREATE TABLE note_action_capture (
                source_action_address TEXT NOT NULL PRIMARY KEY,
                visible_action_sha256 TEXT NOT NULL CHECK(length(visible_action_sha256) = 64),
                visible_action_utf8_bytes INTEGER NOT NULL CHECK(visible_action_utf8_bytes >= 0),
                extractor_contract_id TEXT NOT NULL,
                extraction_commitment TEXT NOT NULL CHECK(length(extraction_commitment) = 64),
                artifact_count INTEGER NOT NULL CHECK(artifact_count BETWEEN 0 AND 16),
                state TEXT NOT NULL CHECK(state IN (
                    'ZeroCaptured', 'Captured', 'Planned', 'Applied', 'Rejected'
                )),
                base_pod_state_identity TEXT NULL,
                target_pod_state_identity TEXT NULL,
                rejection_code TEXT NULL,
                state_revision INTEGER NOT NULL CHECK(state_revision >= 1),
                CHECK(
                    (state = 'ZeroCaptured' AND artifact_count = 0
                        AND base_pod_state_identity IS NULL
                        AND target_pod_state_identity IS NULL
                        AND rejection_code IS NULL)
                    OR (state = 'Captured' AND artifact_count > 0
                        AND base_pod_state_identity IS NULL
                        AND target_pod_state_identity IS NULL
                        AND rejection_code IS NULL)
                    OR (state IN ('Planned', 'Applied')
                        AND artifact_count > 0
                        AND base_pod_state_identity IS NOT NULL
                        AND target_pod_state_identity IS NOT NULL
                        AND rejection_code IS NULL)
                    OR (state = 'Rejected' AND artifact_count > 0
                        AND base_pod_state_identity IS NULL
                        AND target_pod_state_identity IS NULL
                        AND rejection_code IS NOT NULL)
                )
            ) STRICT;

            CREATE TABLE character_note (
                source_action_address TEXT NOT NULL
                    REFERENCES note_action_capture(source_action_address)
                    ON DELETE RESTRICT,
                artifact_ordinal INTEGER NOT NULL CHECK(artifact_ordinal BETWEEN 0 AND 15),
                exact_text TEXT NOT NULL CHECK(length(exact_text) > 0),
                memo_id TEXT NULL,
                PRIMARY KEY(source_action_address, artifact_ordinal)
            ) STRICT;

            CREATE UNIQUE INDEX ux_character_note_memo_id
            ON character_note(memo_id) WHERE memo_id IS NOT NULL;

            CREATE UNIQUE INDEX ux_note_capture_single_active
            ON note_action_capture((1))
            WHERE state IN ('Captured', 'Planned');
            """;
    }

    private sealed class StoreDirectory : IDisposable {
        internal StoreDirectory(bool createLeaf = false) {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "atelia-character-memory-v2-store-"
                    + Guid.NewGuid().ToString("N")
            );
            TestDirectorySafety.EnsureExistingPathChainHasNoReparsePoint(Path);
            if (createLeaf) { Directory.CreateDirectory(Path); }
        }

        internal string Path { get; }

        public void Dispose() =>
            TestDirectorySafety.DeleteOwnedTreeNoFollow(Path);
    }
}
