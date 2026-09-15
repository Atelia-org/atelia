using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.Cli;
using Xunit;

namespace Atelia.SessionJournal.Cli.Tests;

[Collection(ConsoleSerialCollection.Name)]
public sealed class ProgramStructuredInputTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "atelia-cli-structured-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void OnlineStructuredSetupIsRejectedBeforeConnectionsOrMutation() {
        SessionInputContent content = Structured();
        string refId;
        Atelia.EventJournal.EventAddress? head;
        using (SessionJournalEngine engine = SessionJournalEngine.Create(_root, new SessionCreateOptions("model", content, "test-surface"))) {
            refId = engine.BranchRefId.ToHexString();
            head = engine.ReadCurrentHead();
        }
        var factory = new RejectingFactory();
        TextWriter previous = Console.Out;
        TextWriter previousError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        int exitCode;
        try {
            Console.SetOut(output);
            Console.SetError(error);
            exitCode = Program.MainCore([
                "run-online-turn", "--input", _root,
                "--branch", SessionJournalDefaults.MainBranchName,
                "--confirm-ref", refId, "--message", "new action",
                "--connections", Path.Combine(_root, "missing-connections.json")
            ], factory);
        }
        finally { Console.SetOut(previous); Console.SetError(previousError); }

        Assert.True(exitCode == 2, $"CLI exit {exitCode}; stderr: {error}");
        using JsonDocument report = JsonDocument.Parse(output.ToString());
        Assert.Equal("unsupported-input-schema", report.RootElement.GetProperty("status").GetString());
        Assert.Equal(content.SchemaId, report.RootElement.GetProperty("detail").GetProperty("schemaId").GetString());
        Assert.Equal(0, factory.CreateCount);
        using var reopened = SessionJournalEngine.OpenReadOnly(_root);
        Assert.Equal(head, reopened.ReadCurrentHead());
        Assert.Equal(content, reopened.ResolveGoverningSetup(head!.Value).SystemPrompt);
    }

    [Fact]
    public void MachineReportSerializesTypedFactsWithoutProjectingOrAccessingTextValue() {
        TextWriter previous = Console.Out;
        using var output = new StringWriter();
        try {
            Console.SetOut(output);
            Assert.Equal(0, RecapGridCommands.Print("typed-test", "ok", new { input = Structured(), legacy = SessionInputContent.Text("legacy") }));
        }
        finally { Console.SetOut(previous); }

        using JsonDocument report = JsonDocument.Parse(output.ToString());
        JsonElement detail = report.RootElement.GetProperty("detail");
        Assert.Equal("structured", detail.GetProperty("input").GetProperty("kind").GetString());
        Assert.Equal("semantic instructions", detail.GetProperty("input").GetProperty("value").GetProperty("body").GetString());
        Assert.Equal("text", detail.GetProperty("legacy").GetProperty("kind").GetString());
        Assert.Equal("legacy", detail.GetProperty("legacy").GetProperty("value").GetString());
    }

    [Fact]
    public void DeepestLegalInputFitsMachineReportAndAtomicFileWrappers() {
        string arrays = new string('[', SessionInputContent.MaximumStructuredDepth - 1)
            + new string(']', SessionInputContent.MaximumStructuredDepth - 1);
        using JsonDocument value = JsonDocument.Parse("{\"nested\":" + arrays + "}");
        SessionInputContent content = SessionInputContent.Structured("test.depth.v1", value.RootElement);
        TextWriter previous = Console.Out;
        using var output = new StringWriter();
        try {
            Console.SetOut(output);
            Assert.Equal(0, RecapGridCommands.Print("typed-test", "ok", new { input = content }));
        }
        finally { Console.SetOut(previous); }
        using JsonDocument report = JsonDocument.Parse(output.ToString(), new JsonDocumentOptions { MaxDepth = 128 });
        Assert.Equal("test.depth.v1", report.RootElement.GetProperty("detail").GetProperty("input").GetProperty("schemaId").GetString());
        string file = Path.Combine(_root, "deep-report.json");
        CliIo.WriteJsonAtomically(file, new { input = content });
        using JsonDocument written = JsonDocument.Parse(File.ReadAllBytes(file), new JsonDocumentOptions { MaxDepth = 128 });
        Assert.Equal("test.depth.v1", written.RootElement.GetProperty("input").GetProperty("schemaId").GetString());
    }

    private static SessionInputContent Structured() {
        using JsonDocument json = JsonDocument.Parse("{\"body\":\"semantic instructions\"}");
        return SessionInputContent.Structured("test.system-instructions.v1", json.RootElement);
    }

    public void Dispose() {
        if (Directory.Exists(_root)) { Directory.Delete(_root, true); }
    }

    private sealed class RejectingFactory : ICompletionClientFactory {
        internal int CreateCount { get; private set; }
        public ICompletionClient Create(CompletionConnectionConfig connection) {
            CreateCount++;
            throw new InvalidOperationException("No client should be created.");
        }
    }
}
