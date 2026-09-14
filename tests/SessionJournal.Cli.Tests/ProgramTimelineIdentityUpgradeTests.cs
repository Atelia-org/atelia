using Atelia.SessionJournal.HistoryTimeline;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Atelia.SessionJournal.Cli.Tests;

public sealed partial class ProgramRecapGridCommandTests {
    [Fact]
    public void TimelineUpgradeCommandPreservesOtherDomainsAndIsIdempotentWithoutProvider() {
        ExtractControlReceiptFixture("after-control-commit", upgradeTimeline: false);
        string timelineRoot = Path.Combine(_root, "derived", "history-timeline");
        string database = Directory.GetFiles(timelineRoot, "*.sqlite", SearchOption.AllDirectories).Single();
        ActiveTimelineLocator locator = HistoryTimelineCanonicalCodec.DecodeActiveTimelineLocator(
            File.ReadAllBytes(Directory.GetFiles(timelineRoot, "locator.json", SearchOption.AllDirectories).Single()));
        var preserved = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Where(path => path != database).ToDictionary(path => path, File.ReadAllBytes);
        byte[] head = ReadUpgradeHead(database, expectedSchema: 2);
        string[] arguments = ["timeline", "upgrade-schema-v2", "--input", _root,
            "--ref", locator.RefId.ToHexString(), "--timeline", locator.ActiveTimelineId.Value];

        // RunCaptured uses ThrowingCompletionClientFactory: maintenance must not
        // even construct a provider, including for this genuinely old fixture.
        var first = RunCaptured(arguments);
        Assert.Equal(0, first.ExitCode);
        Assert.Equal("upgraded", first.Json.GetProperty("status").GetString());
        Assert.Equal("timeline.upgrade-schema-v2", first.Json.GetProperty("command").GetString());
        Assert.Equal(head, ReadUpgradeHead(database, expectedSchema: 3));
        foreach (var pair in preserved) {
            Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
        }
        byte[] upgraded = File.ReadAllBytes(database);

        var repeated = RunCaptured(arguments);
        Assert.Equal(0, repeated.ExitCode);
        Assert.Equal("already-current", repeated.Json.GetProperty("status").GetString());
        Assert.Equal(upgraded, File.ReadAllBytes(database));
        using var reader = Assert.IsType<HistoryTimelineReaderOpenResult.Opened>(
            HistoryTimelineMaintenance.OpenReader(_root, locator.RefId)).Handle;
        var busy = RunCaptured(arguments);
        Assert.Equal(2, busy.ExitCode);
        Assert.Equal("busy", busy.Json.GetProperty("status").GetString());
        Assert.Equal(upgraded, File.ReadAllBytes(database));
    }

    private static byte[] ReadUpgradeHead(string database, int expectedSchema) {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT schema_version, head_canonical FROM store_metadata WHERE singleton = 1";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(expectedSchema, reader.GetInt32(0));
        return reader.GetFieldValue<byte[]>(1);
    }
}
