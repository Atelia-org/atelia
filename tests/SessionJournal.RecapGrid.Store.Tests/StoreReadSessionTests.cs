using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Store.Tests;

public sealed class StoreReadSessionTests : IDisposable {
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "atelia-recap-grid-store-read-session-tests",
        Guid.NewGuid().ToString("N")
    );

    public void Dispose() {
        if (Directory.Exists(_root)) {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void SessionDisposeIsIdempotentAndSubsequentReadsReturnDisposed() {
        Create();
        using RecapGridStoreHandle handle = Open();
        RecapGridStoreReadSession session = OpenSession(handle);
        session.Dispose();
        session.Dispose();
        Assert.IsType<RecapGridStoreReadResult<RecapRowView>.Disposed>(
            session.ReadViewAt(UnwrittenKey())
        );
    }

    [Fact]
    public void SessionDetectsInPlaceIdentityRewriteAndLatchesStore() {
        Create();
        RecapGridStoreHandle handle = Open();
        RecapGridStoreReadSession session = OpenSession(handle);
        string replacement = RecapGridStoreInstanceId.Generate().Value;
        using (SqliteConnection connection = CreateRawConnection()) {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                $"""
                UPDATE store_metadata
                SET store_instance_id = '{replacement}'
                WHERE singleton = 1;
                """;
            command.ExecuteNonQuery();
        }
        var sessionRead = Assert.IsType<
            RecapGridStoreReadResult<RecapRowView>.Invalid
            >(session.ReadViewAt(UnwrittenKey()));
        Assert.Equal("GridStoreInstanceIdMismatch", sessionRead.Code);
        var freshRead = Assert.IsType<
            RecapGridStoreReadResult<RecapRowView>.Invalid
            >(handle.Reader.ReadViewAt(UnwrittenKey()));
        Assert.Equal("GridStoreInstanceIdMismatch", freshRead.Code);
        session.Dispose();
        handle.Dispose();

        using RecapGridStoreHandle untouchedHandle = Open();
        Assert.IsType<RecapGridStoreReadResult<RecapRowView>.Missing>(
            untouchedHandle.Reader.ReadViewAt(UnwrittenKey())
        );
    }

    [Fact]
    public void IdleSessionDoesNotBlockConcurrentWriter() {
        Create();
        using RecapGridStoreHandle handleA = Open();
        RecapGridStoreReadSession session = OpenSession(handleA);
        using RecapGridStoreHandle handleB = Open();
        RowBuildSpec spec = StoreFixture.Spec();
        RecapCellArtifact cell = StoreFixture.Put(handleB, spec, "concurrent");
        RecapRowView first = Assert.IsType<RecapGridRowViewPutResult.Inserted>(
            handleB.Writer.PutRowView(spec, [cell])
        ).Winner;
        RecapRowView again = Assert.IsType<
            RecapGridRowViewPutResult.AlreadyPresent
            >(handleB.Writer.PutRowView(spec, [cell])).Winner;
        Assert.Equal(first.Id, again.Id);
        session.Dispose();
    }

    [Fact]
    public async Task StoreDisposeDrainsInFlightSessionReadThenDisposed() {
        Create();
        RecapGridStoreHandle handle = Open();
        RecapGridStoreReadSession session = OpenSession(handle);
        RecapRowView row = SeedRow(handle);
        RowViewAssignmentKey key = row.Coordinate.AssignmentKey;
        Assert.IsType<RecapGridStoreReadResult<RecapRowView>.Found>(
            session.ReadViewAt(key)
        );
        Task<RecapGridStoreReadResult<RecapRowView>> inFlight =
            Task.Run(() => session.ReadViewAt(key));
        handle.Dispose();
        RecapGridStoreReadResult<RecapRowView> drained =
            await inFlight.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(
            drained is RecapGridStoreReadResult<RecapRowView>.Found
                or RecapGridStoreReadResult<RecapRowView>.Disposed,
            drained.ToString()
        );
        Assert.IsType<RecapGridStoreReadResult<RecapRowView>.Disposed>(
            session.ReadViewAt(key)
        );
        session.Dispose();
    }

    [Fact]
    public void SessionReadPropagatesLatchedInvalid() {
        Create();
        RecapGridStoreHandle handle = Open();
        RecapGridStoreReadSession session = OpenSession(handle);
        RecapRowView row = SeedRow(handle);
        RowViewAssignmentKey key = row.Coordinate.AssignmentKey;
        Assert.IsType<RecapGridStoreReadResult<RecapRowView>.Found>(
            session.ReadViewAt(key)
        );
        using (SqliteConnection connection = CreateRawConnection()) {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                PRAGMA ignore_check_constraints = ON;
                UPDATE store_metadata SET schema_version = 99 WHERE singleton = 1;
                """;
            command.ExecuteNonQuery();
        }
        var freshRead = Assert.IsType<
            RecapGridStoreReadResult<RecapRowView>.Invalid
            >(handle.Reader.ReadViewAt(key));
        var sessionRead = Assert.IsType<
            RecapGridStoreReadResult<RecapRowView>.Invalid
            >(session.ReadViewAt(key));
        Assert.Equal(freshRead.Code, sessionRead.Code);
        session.Dispose();
        handle.Dispose();
    }

    [Fact]
    public void SessionReadViewAtMatchesFreshOpenRead() {
        Create();
        using RecapGridStoreHandle handle = Open();
        RecapGridStoreReadSession session = OpenSession(handle);
        RecapRowView row = SeedRow(handle);
        RowViewAssignmentKey key = row.Coordinate.AssignmentKey;

        var viaSession = Assert.IsType<
            RecapGridStoreReadResult<RecapRowView>.Found
            >(session.ReadViewAt(key));
        var viaReader = Assert.IsType<
            RecapGridStoreReadResult<RecapRowView>.Found
            >(handle.Reader.ReadViewAt(key));
        Assert.Equal(viaReader.Value.Id, viaSession.Value.Id);
        Assert.Equal(viaReader.Value.Coordinate, viaSession.Value.Coordinate);

        RowViewAssignmentKey missingKey = new(
            key.RefId,
            key.TimelineId,
            key.RecipeDigest,
            new HistoryRowId(new string('d', 64))
        );
        Assert.IsType<RecapGridStoreReadResult<RecapRowView>.Missing>(
            session.ReadViewAt(missingKey)
        );
        Assert.IsType<RecapGridStoreReadResult<RecapRowView>.Missing>(
            handle.Reader.ReadViewAt(missingKey)
        );
        session.Dispose();
    }

    private void Create() {
        Directory.CreateDirectory(_root);
        Assert.IsType<RecapGridStoreCreateResult.Created>(
            RecapGridStoreFactory.Create(_root)
        );
    }

    private RecapGridStoreHandle Open() => Assert.IsType<
        RecapGridStoreOpenResult.Opened
        >(RecapGridStoreFactory.Open(_root)).Handle;

    private static RecapGridStoreReadSession OpenSession(
        RecapGridStoreHandle handle
    ) => Assert.IsType<RecapGridStoreSessionOpenResult.Opened>(
        handle.Reader.OpenSession()
    ).Session;

    private SqliteConnection CreateRawConnection() {
        var connection = new SqliteConnection(
            $"Data Source={new StorePaths(_root).DatabasePath};Pooling=false"
        );
        connection.Open();
        return connection;
    }

    private static RecapRowView SeedRow(RecapGridStoreHandle handle) {
        RowBuildSpec spec = StoreFixture.Spec();
        RecapCellArtifact cell = StoreFixture.Put(handle, spec);
        return Assert.IsType<RecapGridRowViewPutResult.Inserted>(
            handle.Writer.PutRowView(spec, [cell])
        ).Winner;
    }

    private static RowViewAssignmentKey UnwrittenKey() => new(
        new RefId(1),
        StoreFixture.Timeline,
        StoreFixture.Recipe().Digest,
        new HistoryRowId(new string('d', 64))
    );
}
