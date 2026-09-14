using System.Buffers.Binary;
using System.Text;
using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Store.Tests;

public sealed partial class StoreAuthorityRegressionTests {
    [Theory]
    [InlineData("row")]
    [InlineData("ref")]
    [InlineData("timeline")]
    [InlineData("recipe")]
    public void FulfilledApiAndSqlForeignKeyRejectWrongThroughOrScope(string changed) {
        Create();
        using RecapGridStoreHandle handle = Open();
        RowBuildSpec spec = StoreFixture.Spec();
        RecapCellArtifact cell = StoreFixture.Put(handle, spec);
        RecapRowView row = Assert.IsType<RecapGridRowViewPutResult.Inserted>(
            handle.Writer.PutRowView(spec, [cell])).Winner;
        var invalid = new FulfilledViewKey(
            changed == "ref" ? new RefId(2) : spec.RefId,
            changed == "timeline" ? new TimelineId(new string('e', 32)) : spec.TimelineId,
            1,
            changed == "row" ? new HistoryRowId(new string('e', 64)) : spec.HistoryRowId,
            changed == "recipe" ? new GridBuildRecipeDigest(new string('e', 64)) : spec.RecipeDigest);
        Assert.Equal("FulfilledViewScopeMismatch", Assert.IsType<RecapGridFulfilledPutResult.Rejected>(
            handle.Writer.PutFulfilled(invalid, row.Id)).Code);

        using SqliteConnection connection = OpenRaw();
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys=ON;
            INSERT INTO fulfilled_view_ref(ref_id,timeline_id,timeline_head_generation,
                through_history_row_id,recipe_digest,row_result_id)
            VALUES($ref,$timeline,1,$through,$recipe,$result);
            """;
        command.Parameters.AddWithValue("$ref", invalid.RefId.ToHexString());
        command.Parameters.AddWithValue("$timeline", invalid.TimelineId.Value);
        command.Parameters.AddWithValue("$through", invalid.ThroughRowId.Value);
        command.Parameters.AddWithValue("$recipe", invalid.RecipeDigest.Value);
        command.Parameters.AddWithValue("$result", row.Id.Value);
        Assert.Equal(19, Assert.Throws<SqliteException>(() => command.ExecuteNonQuery()).SqliteErrorCode);
        Assert.IsType<RecapGridStoreReadResult<RecapGridFulfilledView>.Missing>(handle.Reader.ReadFulfilled(invalid));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void CursorV2RejectsEveryOldV1Kind(int kind) {
        // Fixed v1 layout, independent of the current cursor factory. Fulfilled
        // through has the old descriptor meaning despite the same 64-hex width.
        byte[] bytes = new byte[kind == 3 ? 186 : 34];
        bytes[0] = 1;
        bytes[1] = (byte)kind;
        if (kind == 3) {
            Encoding.ASCII.GetBytes(new string('0', 16)).CopyTo(bytes, 2);
            Encoding.ASCII.GetBytes(new string('1', 32)).CopyTo(bytes, 18);
            BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(50, 8), 7);
            Encoding.ASCII.GetBytes(new string('b', 64)).CopyTo(bytes, 58);
            Encoding.ASCII.GetBytes(new string('c', 64)).CopyTo(bytes, 122);
        }
        else {
            Encoding.ASCII.GetBytes(new string('a', 32)).CopyTo(bytes, 2);
        }
        string old = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Throws<ArgumentException>(() => RecapGridStoreExportCursor.Parse(old));
    }

    [Fact]
    public void CursorV2RoundTripsAllKindsAndPreservesThroughRowId() {
        RecapGridStoreExportCursor[] values = [
            RecapGridStoreExportCursor.CreateId("cell", new string('a', 32)),
            RecapGridStoreExportCursor.CreateId("row-view", new string('b', 32)),
            RecapGridStoreExportCursor.CreateFulfilled(new RefId(1).ToHexString(), StoreFixture.Timeline.Value,
                7, new string('c', 64), StoreFixture.Recipe().Digest.Value)
        ];
        foreach (RecapGridStoreExportCursor value in values) {
            Assert.Equal(value, RecapGridStoreExportCursor.Parse(value.Value));
            string padded = value.Value.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            Assert.Equal(2, Convert.FromBase64String(padded)[0]);
        }
        Assert.Equal(new string('c', 64), values[2].Through);
    }
}
