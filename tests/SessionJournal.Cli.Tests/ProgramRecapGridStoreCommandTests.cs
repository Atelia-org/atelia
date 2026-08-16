using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Store;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Atelia.SessionJournal.Cli.Tests;

[Collection(ConsoleSerialCollection.Name)]
public sealed class ProgramRecapGridStoreCommandTests : IDisposable {
    private const string ReportSchema =
        "atelia.session-journal.recap-grid-cli.v1";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "atelia-recap-grid-cli-tests",
        Guid.NewGuid().ToString("N")
    );

    [Fact]
    public void InspectExportVerifyAndResetNeverConstructProvider() {
        Directory.CreateDirectory(_root);
        Assert.Equal(0, Run("inspect", "--input", _root));
        Assert.Equal(0, Run("export", "--input", _root));
        Assert.Equal(0, Run("verify", "--input", _root));
        (int absentCode, string absentJson) = RunCaptured(
            "reset", "--prepare", "--input", _root
        );
        Assert.Equal(0, absentCode);
        using (JsonDocument absent = JsonDocument.Parse(absentJson)) {
            Assert.Equal(
                "absent",
                absent.RootElement.GetProperty("status").GetString()
            );
        }
        Assert.False(Directory.Exists(Path.Combine(
            _root,
            "derived",
            "recap-grid"
        )));

        Assert.IsType<RecapGridStoreCreateResult.Created>(
            RecapGridStoreFactory.Create(_root)
        );
        Assert.Equal(0, Run("inspect", "--input", _root));
        Assert.Equal(0, Run("export", "--input", _root));
        Assert.Equal(0, Run("verify", "--input", _root));

        (int prepareCode, string prepareJson) = RunCaptured(
            "reset", "--prepare", "--input", _root
        );
        Assert.Equal(0, prepareCode);
        long length;
        string sha256;
        using (JsonDocument prepared = JsonDocument.Parse(prepareJson)) {
            Assert.Equal(
                "prepared",
                prepared.RootElement.GetProperty("status").GetString()
            );
            JsonElement detail = prepared.RootElement.GetProperty("detail");
            length = detail.GetProperty("length").GetInt64();
            sha256 = detail.GetProperty("sha256").GetString()!;
        }
        Assert.Equal(JsonSerializer.Serialize(new {
            schema = ReportSchema,
            command = "reset",
            status = "prepared",
            detail = new { length, sha256 }
        }), prepareJson);
        (int staleCode, string staleJson) = RunCaptured(
            "reset",
            "--input", _root,
            "--confirm-length", length.ToString(),
            "--confirm-sha256", new string('0', 64)
        );
        Assert.Equal(2, staleCode);
        Assert.Equal(JsonSerializer.Serialize(new {
            schema = ReportSchema,
            command = "reset",
            status = "stale-confirmation",
            detail = new {
                actualLength = length,
                actualSha256 = sha256
            }
        }), staleJson);
        (int resetCode, string resetJson) = RunCaptured(
            "reset",
            "--input", _root,
            "--confirm-length", length.ToString(),
            "--confirm-sha256", sha256
        );
        Assert.Equal(0, resetCode);
        using (JsonDocument reset = JsonDocument.Parse(resetJson)) {
            Assert.Equal(ReportSchema, reset.RootElement.GetProperty(
                "schema"
            ).GetString());
            Assert.Equal("reset", reset.RootElement.GetProperty(
                "command"
            ).GetString());
            Assert.Equal("reset", reset.RootElement.GetProperty(
                "status"
            ).GetString());
        }
        Assert.Equal(0, Run("verify", "--input", _root));
    }

    [Theory]
    [InlineData("inspect")]
    [InlineData("export")]
    [InlineData("verify")]
    public void AbsentStoreCommandsUseTheSharedExactEnvelope(string command) {
        Directory.CreateDirectory(_root);

        (int exitCode, string json) = RunCaptured(
            command, "--input", _root
        );

        Assert.Equal(0, exitCode);
        Assert.Equal(
            $$"""{"schema":"{{ReportSchema}}","command":"{{command}}","status":"absent","detail":null}""",
            json
        );
        Assert.DoesNotContain(
            "atelia.session-journal.recap-grid-store-cli.v1",
            json,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void ResetPrepareUsesResetCommandInTheSharedExactEnvelope() {
        Directory.CreateDirectory(_root);

        (int exitCode, string json) = RunCaptured(
            "reset", "--prepare", "--input", _root
        );

        Assert.Equal(0, exitCode);
        Assert.Equal(
            $$"""{"schema":"{{ReportSchema}}","command":"reset","status":"absent","detail":null}""",
            json
        );
    }

    [Fact]
    public void SharedEnvelopeAcceptsTheExactUtf8LimitAndFailsClosedAboveIt() {
        const string command = "boundary";
        const string status = "ok";
        int fixedBytes = JsonSerializer.SerializeToUtf8Bytes(new {
            schema = ReportSchema,
            command,
            status,
            detail = string.Empty
        }).Length;
        string exactDetail = new(
            'a',
            RecapGridCommands.MaximumReportUtf8Bytes - fixedBytes
        );

        (int exactCode, string exactJson) = CaptureOutput(
            () => RecapGridCommands.Print(command, status, exactDetail)
        );

        Assert.Equal(0, exactCode);
        Assert.Equal(
            RecapGridCommands.MaximumReportUtf8Bytes,
            Encoding.UTF8.GetByteCount(exactJson)
        );
        using (JsonDocument exact = JsonDocument.Parse(exactJson)) {
            Assert.Equal(
                status,
                exact.RootElement.GetProperty("status").GetString()
            );
        }

        (int exceededCode, string exceededJson) = CaptureOutput(
            () => RecapGridCommands.Print(
                command,
                status,
                exactDetail + "a"
            )
        );

        Assert.Equal(2, exceededCode);
        Assert.Equal(
            """{"schema":"atelia.session-journal.recap-grid-cli.v1","command":"boundary","status":"limit-exceeded","detail":{"limit":"RecapGridReportUtf8Bytes"}}""",
            exceededJson
        );
    }

    [Fact]
    public void PreviousFourMiBCanonicalPageCanExceedTheReportLimit() {
        const int previousMaximumPageBytes = 4 * 1024 * 1024;
        RecapGridStoreExportItem[] items = AdversarialItems(10_600);
        int canonicalBytes = items.Sum(static item => item.CanonicalBytes);
        var page = new RecapGridStoreExportPage(
            items,
            ParseCellCursor(items[^1].Key),
            Incomplete: true
        );

        (int exitCode, string json) = CaptureOutput(
            () => RecapGridStoreCommands.PrintExportPage(page)
        );

        Assert.Equal(128, items.Length);
        Assert.InRange(
            canonicalBytes,
            previousMaximumPageBytes - 64 * 1024,
            previousMaximumPageBytes
        );
        Assert.Equal(2, exitCode);
        Assert.Equal(
            """{"schema":"atelia.session-journal.recap-grid-cli.v1","command":"export","status":"limit-exceeded","detail":{"limit":"RecapGridReportUtf8Bytes"}}""",
            json
        );
    }

    [Fact]
    public void MaximumItemAdversarialPageFitsTheSharedReportEnvelope() {
        RecapGridStoreExportItem[] items = AdversarialItems(5_196);
        int canonicalBytes = items.Sum(static item => item.CanonicalBytes);
        RecapGridStoreExportCursor cursor = ParseCellCursor(items[^1].Key);
        var page = new RecapGridStoreExportPage(
            items,
            cursor,
            Incomplete: true
        );

        (int exitCode, string json) = CaptureOutput(
            () => RecapGridStoreCommands.PrintExportPage(page)
        );

        Assert.Equal(
            RecapGridStoreLimits.MaximumPageItems,
            items.Length
        );
        Assert.InRange(
            canonicalBytes,
            RecapGridStoreLimits.MaximumPageBytes - 64 * 1024,
            RecapGridStoreLimits.MaximumPageBytes
        );
        Assert.Equal(0, exitCode);
        Assert.True(
            Encoding.UTF8.GetByteCount(json)
                < RecapGridCommands.MaximumReportUtf8Bytes
        );
        using JsonDocument report = JsonDocument.Parse(json);
        Assert.Equal(
            "page",
            report.RootElement.GetProperty("status").GetString()
        );
        JsonElement detail = report.RootElement.GetProperty("detail");
        Assert.Equal(
            cursor.Value,
            detail.GetProperty("nextCursor").GetString()
        );
        Assert.True(detail.GetProperty("Incomplete").GetBoolean());
        Assert.Equal(
            RecapGridStoreLimits.MaximumPageItems,
            detail.GetProperty("items").GetArrayLength()
        );
    }

    private static int Run(params string[] args) => Program.MainCore(
        ["recap-grid", .. args],
        ThrowingCompletionClientFactory.Instance
    );

    private static (int ExitCode, string Json) RunCaptured(
        params string[] args
    ) => CaptureOutput(() => Run(args));

    private static (int ExitCode, string Json) CaptureOutput(
        Func<int> action
    ) {
        TextWriter original = Console.Out;
        using var output = new StringWriter();
        try {
            Console.SetOut(output);
            int exitCode = action();
            string json = output.ToString().Split(
                Environment.NewLine,
                StringSplitOptions.RemoveEmptyEntries
            )[^1];
            return (exitCode, json);
        }
        finally {
            Console.SetOut(original);
        }
    }

    private static RecapGridStoreExportItem[] AdversarialItems(
        int contentScalars
    ) {
        string content = new('\u9ffe', contentScalars);
        return Enumerable.Range(1, 128)
            .Select(index => {
                RecapCellArtifact cell = Cell(index, content);
                byte[] canonical = cell.ToCanonicalBytes();
                return new RecapGridStoreExportItem(
                    "cell",
                    cell.CellDigest.Value,
                    canonical.Length,
                    canonical
                );
            })
            .ToArray();
    }

    private static RecapGridStoreExportCursor ParseCellCursor(string key) {
        var bytes = new byte[66];
        bytes[0] = 1;
        bytes[1] = 1;
        Encoding.ASCII.GetBytes(key, bytes.AsSpan(2));
        string value = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return RecapGridStoreExportCursor.Parse(value);
    }

    private static RecapCellArtifact Cell(int descriptor, string content) {
        var definition = new MaintainerDefinitionDigest(new string('a', 64));
        EvaluationKey evaluation = EvaluationKey.Create(
            new HistorySegmentDescriptorDigest(descriptor.ToString("x64")),
            definition,
            PriorInputReference.FirstRow.Value
        );
        return RecapCellArtifact.Create(
            new LogicalColumnId("case.culprit"),
            definition,
            evaluation,
            RecapCellOutcome.Updated,
            content,
            RecapGridLimits.MaximumContentUtf8Bytes
        );
    }

    public void Dispose() {
        if (Directory.Exists(_root)) {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class ThrowingCompletionClientFactory
        : ICompletionClientFactory {
        internal static ThrowingCompletionClientFactory Instance { get; } =
            new();

        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) => throw new InvalidOperationException(
            $"recap-grid must not construct provider '{connection.Id}'."
        );
    }
}
