using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.Cli;
using Xunit;

namespace Atelia.SessionJournal.Cli.Tests;

public sealed class ProgramDesiredSetupReconciliationCommandTests
    : IDisposable {
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "atelia-desired-setup-command-tests",
        Guid.NewGuid().ToString("N")
    );

    public void Dispose() {
        try {
            if (Directory.Exists(_tempRoot)) {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch {
            // Best-effort cleanup for temp test directories.
        }
    }

    [Theory]
    [InlineData("model-B", "surface-B", "prompt-B", false, false, 0)]
    [InlineData("model-A", "surface-A", "prompt-B", true, false, 1)]
    [InlineData("model-B", "surface-B", "prompt-A", false, true, 1)]
    [InlineData("model-A", "surface-A", "prompt-A", true, true, 2)]
    public void ReconcilesExactIdleSetupWithoutCreatingAClient(
        string initialModel,
        string initialSurface,
        string initialPrompt,
        bool expectedRuntimeChanged,
        bool expectedPromptChanged,
        int expectedAddedEvents
    ) {
        TestInputs inputs = CreateInputs(
            initialModel,
            initialSurface,
            initialPrompt
        );
        JournalSnapshot before = ReadSnapshot(inputs.RepositoryPath);

        int exitCode = Run(inputs, before.Head);

        Assert.Equal(0, exitCode);
        JournalSnapshot after = ReadSnapshot(inputs.RepositoryPath);
        Assert.Equal(
            before.EventCount + expectedAddedEvents,
            after.EventCount
        );
        Assert.Equal("model-B", after.Governing.RuntimeConfig.ModelId);
        Assert.Equal(
            "surface-B",
            after.Governing.RuntimeConfig.CompletionSurfaceId
        );
        Assert.Equal("prompt-B", after.Governing.SystemPrompt);

        string reportJson = File.ReadAllText(inputs.ReportPath);
        using JsonDocument report = JsonDocument.Parse(reportJson);
        JsonElement root = report.RootElement;
        AssertReport(
            root,
            before.Head,
            after.Head,
            expectedRuntimeChanged,
            expectedPromptChanged
        );
        Assert.DoesNotContain("prompt-B", reportJson, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SECRET-API-KEY",
            reportJson,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "https://example.invalid",
            reportJson,
            StringComparison.Ordinal
        );
        Assert.False(
            root.TryGetProperty("systemPromptUtf8Sha256CodecId", out _)
        );
        Assert.False(root.TryGetProperty("phase", out _));
    }

    [Fact]
    public void RepeatingAgainstTheCommittedHeadIsIdempotent() {
        TestInputs inputs = CreateInputs(
            "model-A",
            "surface-A",
            "prompt-A"
        );
        JournalSnapshot initial = ReadSnapshot(inputs.RepositoryPath);
        Assert.Equal(0, Run(inputs, initial.Head));
        JournalSnapshot reconciled = ReadSnapshot(inputs.RepositoryPath);

        Assert.Equal(0, Run(inputs, reconciled.Head));

        JournalSnapshot repeated = ReadSnapshot(inputs.RepositoryPath);
        Assert.Equal(reconciled.Head, repeated.Head);
        Assert.Equal(reconciled.EventCount, repeated.EventCount);
        using JsonDocument report = JsonDocument.Parse(
            File.ReadAllText(inputs.ReportPath)
        );
        AssertReport(
            report.RootElement,
            reconciled.Head,
            repeated.Head,
            runtimeConfigChanged: false,
            systemPromptChanged: false
        );
    }

    [Fact]
    public void StaleExpectedHeadFailsWithoutRawMutation() {
        TestInputs inputs = CreateInputs(
            "model-A",
            "surface-A",
            "prompt-A"
        );
        EventAddress staleHead;
        using (SessionJournalEngine engine = SessionJournalEngine.Open(
            inputs.RepositoryPath
        )) {
            staleHead = engine.ReadCurrentHead()!.Value;
            engine.AppendSystemPromptSetup("later-prompt");
        }
        JournalSnapshot before = ReadSnapshot(inputs.RepositoryPath);

        int exitCode = Run(inputs, staleHead);

        Assert.Equal(1, exitCode);
        Assert.Equal(before, ReadSnapshot(inputs.RepositoryPath));
        Assert.False(File.Exists(inputs.ReportPath));
    }

    [Fact]
    public void ActiveTurnFailsWithoutRawMutation() {
        TestInputs inputs = CreateInputs(
            "model-A",
            "surface-A",
            "prompt-A"
        );
        using (SessionJournalEngine engine = SessionJournalEngine.Open(
            inputs.RepositoryPath
        )) {
            engine.AppendObservation("active observation");
        }
        JournalSnapshot before = ReadSnapshot(inputs.RepositoryPath);

        int exitCode = Run(inputs, before.Head);

        Assert.Equal(1, exitCode);
        Assert.Equal(before, ReadSnapshot(inputs.RepositoryPath));
        Assert.False(File.Exists(inputs.ReportPath));
    }

    [Fact]
    public void UnknownConnectionFailsBeforeRawMutation() {
        TestInputs inputs = CreateInputs(
            "model-A",
            "surface-A",
            "prompt-A"
        );
        JournalSnapshot before = ReadSnapshot(inputs.RepositoryPath);

        int exitCode = Run(inputs, before.Head, connectionId: "missing");

        Assert.Equal(1, exitCode);
        Assert.Equal(before, ReadSnapshot(inputs.RepositoryPath));
        Assert.False(File.Exists(inputs.ReportPath));
    }

    [Fact]
    public void MissingSystemPromptFileFailsBeforeRawMutation() {
        TestInputs inputs = CreateInputs(
            "model-A",
            "surface-A",
            "prompt-A"
        );
        File.Delete(inputs.SystemPromptPath);
        JournalSnapshot before = ReadSnapshot(inputs.RepositoryPath);

        int exitCode = Run(inputs, before.Head);

        Assert.Equal(1, exitCode);
        Assert.Equal(before, ReadSnapshot(inputs.RepositoryPath));
        Assert.False(File.Exists(inputs.ReportPath));
    }

    [Theory]
    [InlineData("report-inside-repo")]
    [InlineData("prompt-inside-repo")]
    public void UnsafeNestedPathFailsBeforeRawMutation(string unsafePath) {
        TestInputs inputs = CreateInputs(
            "model-A",
            "surface-A",
            "prompt-A"
        );
        string promptPath = inputs.SystemPromptPath;
        string reportPath = inputs.ReportPath;
        if (unsafePath == "report-inside-repo") {
            reportPath = Path.Combine(
                inputs.RepositoryPath,
                "activation-report.json"
            );
        }
        else {
            promptPath = Path.Combine(
                inputs.RepositoryPath,
                "desired-prompt.md"
            );
            File.WriteAllText(promptPath, "prompt-B");
        }
        JournalSnapshot before = ReadSnapshot(inputs.RepositoryPath);

        int exitCode = Run(
            inputs with {
                SystemPromptPath = promptPath,
                ReportPath = reportPath
            },
            before.Head
        );

        Assert.Equal(1, exitCode);
        Assert.Equal(before, ReadSnapshot(inputs.RepositoryPath));
        Assert.False(File.Exists(reportPath));
    }

    [Fact]
    public void NoVersionConnectionsFailBeforeRepositoryMutation() {
        TestInputs inputs = CreateInputs(
            "model-A",
            "surface-A",
            "prompt-A"
        );
        string noVersion = File.ReadAllText(inputs.ConnectionsPath).Replace(
            "\"v\": 2,",
            string.Empty,
            StringComparison.Ordinal
        );
        File.WriteAllText(inputs.ConnectionsPath, noVersion);
        JournalSnapshot before = ReadSnapshot(inputs.RepositoryPath);

        int exitCode = Run(inputs, before.Head);

        Assert.Equal(1, exitCode);
        Assert.Equal(before, ReadSnapshot(inputs.RepositoryPath));
        Assert.False(File.Exists(inputs.ReportPath));
    }

    private int Run(
        TestInputs inputs,
        EventAddress expectedHead,
        string connectionId = "target"
    ) => Program.MainCore(
        [
            "reconcile-desired-setup",
            "--input", inputs.RepositoryPath,
            "--branch", SessionJournalDefaults.MainBranchName,
            "--expected-head", EventAddressTextCodec.Format(expectedHead),
            "--connections", inputs.ConnectionsPath,
            "--connection", connectionId,
            "--system-prompt-file", inputs.SystemPromptPath,
            "--report-json", inputs.ReportPath
        ],
        ThrowingCompletionClientFactory.Instance
    );

    private TestInputs CreateInputs(
        string initialModel,
        string initialSurface,
        string initialPrompt
    ) {
        Directory.CreateDirectory(_tempRoot);
        string repositoryPath = Path.Combine(
            _tempRoot,
            $"repo-{Guid.NewGuid():N}"
        );
        string connectionsPath = Path.Combine(
            _tempRoot,
            $"connections-{Guid.NewGuid():N}.json"
        );
        string systemPromptPath = Path.Combine(
            _tempRoot,
            $"prompt-{Guid.NewGuid():N}.md"
        );
        string reportPath = Path.Combine(
            _tempRoot,
            $"report-{Guid.NewGuid():N}.json"
        );
        using (SessionJournalEngine engine = SessionJournalEngine.Create(
            repositoryPath,
            new SessionCreateOptions(
                initialModel,
                initialPrompt,
                initialSurface
            )
        )) { }
        File.WriteAllText(
            connectionsPath,
            """
            {
              "v": 2,
              "defaultConnectionId": "target",
              "connections": [
                {
                  "id": "target",
                  "kind": "openai-chat",
                  "modelId": "model-B",
                  "completionSurfaceId": "surface-B",
                  "baseAddress": "https://example.invalid",
                  "apiKey": "SECRET-API-KEY"
                }
              ]
            }
            """
        );
        File.WriteAllText(systemPromptPath, "  prompt-B\r\n");
        return new TestInputs(
            repositoryPath,
            connectionsPath,
            systemPromptPath,
            reportPath
        );
    }

    private static JournalSnapshot ReadSnapshot(string repositoryPath) {
        using SessionJournalEngine engine =
            SessionJournalEngine.OpenReadOnly(repositoryPath);
        EventAddress head = engine.ReadCurrentHead()!.Value;
        SessionCurrentLineageSnapshot lineage =
            engine.ReadCurrentLineageHeaders();
        return new JournalSnapshot(
            head,
            lineage.HeadToRoot.Count,
            engine.ResolveGoverningSetup(head)
        );
    }

    private static string ComputeUtf8Sha256(string value) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(value))
        );

    private static void AssertReport(
        JsonElement root,
        EventAddress beforeHead,
        EventAddress afterHead,
        bool runtimeConfigChanged,
        bool systemPromptChanged
    ) {
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(
            [
                "afterHead",
                "beforeHead",
                "branchName",
                "completionSurfaceId",
                "connectionId",
                "modelId",
                "runtimeConfigChanged",
                "schema",
                "systemPromptChanged",
                "systemPromptUtf8Sha256"
            ],
            root.EnumerateObject()
                .Select(static property => property.Name)
                .OrderBy(static name => name, StringComparer.Ordinal)
        );
        AssertJsonString(
            root,
            "schema",
            "atelia.session-journal.desired-setup-reconciliation.v2"
        );
        AssertJsonString(root, "branchName", "main");
        AssertJsonString(root, "connectionId", "target");
        AssertJsonString(
            root,
            "beforeHead",
            EventAddressTextCodec.Format(beforeHead)
        );
        AssertJsonString(
            root,
            "afterHead",
            EventAddressTextCodec.Format(afterHead)
        );
        AssertJsonBoolean(root, "runtimeConfigChanged", runtimeConfigChanged);
        AssertJsonBoolean(root, "systemPromptChanged", systemPromptChanged);
        AssertJsonString(root, "modelId", "model-B");
        AssertJsonString(root, "completionSurfaceId", "surface-B");
        AssertJsonString(
            root,
            "systemPromptUtf8Sha256",
            ComputeUtf8Sha256("prompt-B")
        );
    }

    private static void AssertJsonString(
        JsonElement root,
        string propertyName,
        string expected
    ) {
        JsonElement property = root.GetProperty(propertyName);
        Assert.Equal(JsonValueKind.String, property.ValueKind);
        Assert.Equal(expected, property.GetString());
    }

    private static void AssertJsonBoolean(
        JsonElement root,
        string propertyName,
        bool expected
    ) {
        JsonElement property = root.GetProperty(propertyName);
        Assert.Equal(
            expected ? JsonValueKind.True : JsonValueKind.False,
            property.ValueKind
        );
        Assert.Equal(expected, property.GetBoolean());
    }

    private sealed record TestInputs(
        string RepositoryPath,
        string ConnectionsPath,
        string SystemPromptPath,
        string ReportPath
    );

    private sealed record JournalSnapshot(
        EventAddress Head,
        int EventCount,
        SessionGoverningSetup Governing
    );

    private sealed class ThrowingCompletionClientFactory
        : ICompletionClientFactory {
        public static ThrowingCompletionClientFactory Instance { get; } =
            new();

        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) => throw new InvalidOperationException(
            $"Setup-only command must not create client '{connection.Id}'."
        );
    }
}
