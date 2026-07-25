using System.Text.Json;
using Atelia.ChatSession;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal;
using Atelia.SessionJournal.Derived;
using ChatSessionBacktestCli;
using SJ = Atelia.SessionJournal;
using Xunit;
using Xunit.Sdk;

namespace Atelia.ChatSession.BacktestCli.Tests;

public sealed class ProgramSessionJournalE2ETests : IDisposable {
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "atelia-backtest-cli-e2e",
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

    [Fact]
    public async Task SessionJournalCommand_ImportsProducesRejectsExistingAndRegeneratesAfterExactDerivedDeletion() {
        Directory.CreateDirectory(_tempRoot);
        string legacyPath = Path.Combine(_tempRoot, "legacy.json");
        string repoPath = Path.Combine(_tempRoot, "session-journal");
        string connectionsPath = Path.Combine(_tempRoot, "connections.json");
        WriteLegacyExport(legacyPath, turnCount: 3);
        WriteConnections(connectionsPath);
        var factory = new ScriptedCompletionClientFactory("summary-e2e");

        int importExitCode = Program.MainCore(
            [
                "import-session-journal",
                "--input", legacyPath,
                "--output", repoPath
            ],
            factory
        );

        Assert.Equal(0, importExitCode);
        Assert.Equal(0, factory.CompletionCallCount);
        SessionHistorySnapshot rawBefore = ReadHistorySnapshot(repoPath);
        Assert.Equal(6, rawBefore.Messages.Count);
        SJ.SessionGoverningSetup governingSetup = ReadGoverningSetup(
            repoPath,
            EventAddressTextCodec.Parse(rawBefore.SourceRawHead)
        );

        string firstOutputPath = Path.Combine(_tempRoot, "first.jsonl");
        string firstCallLogDir = Path.Combine(_tempRoot, "first-calls");
        int firstExitCode = RunSessionJournalReplay(
            factory,
            repoPath,
            connectionsPath,
            firstOutputPath,
            firstCallLogDir
        );

        Assert.Equal(0, firstExitCode);
        Assert.Equal(1, factory.CompletionCallCount);
        RollingSummaryReplayRecord firstRecord = ReadSingleRecord(firstOutputPath);
        Assert.Equal(RollingSummaryReplaySourceKinds.SessionJournal, firstRecord.SourceKind);
        Assert.Equal(rawBefore.SourceRawHead, firstRecord.SourceRawHead);
        Assert.NotNull(firstRecord.SourceStartInclusive);
        Assert.NotNull(firstRecord.SourceEndInclusive);
        Assert.Equal("succeeded", firstRecord.Status);
        Assert.NotNull(firstRecord.ArtifactId);
        Assert.NotNull(firstRecord.ArtifactPath);
        Assert.True(Path.IsPathFullyQualified(firstRecord.ArtifactPath));
        Assert.True(File.Exists(firstRecord.ArtifactPath));
        Assert.Equal(firstRecord.SourceEndInclusive, firstRecord.AnchorRawEvent);
        Assert.Null(firstRecord.PreviousArtifact);
        string firstCallLogPath = Assert.Single(firstRecord.CallLogPaths);
        Assert.Equal(firstCallLogPath, firstRecord.CallLogPath);
        Assert.True(Path.IsPathFullyQualified(firstCallLogPath));
        Assert.True(File.Exists(firstCallLogPath));
        Assert.Equal(
            "replay-rolling-summary-session-journal",
            ReadCallLogCommand(firstCallLogPath)
        );

        var store = DerivedRecapStore.Open(repoPath);
        DerivedRecapArtifact? firstArtifact = await store.TryReadArtifactAsync(firstRecord.ArtifactId);
        Assert.NotNull(firstArtifact);
        Assert.Equal(DerivedRecapArtifactKinds.RollingSummary, firstArtifact.ArtifactKind);
        Assert.Equal(rawBefore.SourceRawHead, EventAddressTextCodec.Format(firstArtifact.SourceRawHead));
        Assert.Null(firstArtifact.SourceStartExclusive);
        Assert.Equal(firstRecord.SourceEndInclusive, EventAddressTextCodec.Format(firstArtifact.SourceEndInclusive));
        Assert.Equal(firstArtifact.SourceEndInclusive, firstArtifact.AnchorRawEvent);
        Assert.Equal(governingSetup.RuntimeConfigSetupAddress, firstArtifact.GoverningRuntimeConfigSetup);
        Assert.Equal(governingSetup.SystemPromptSetupAddress, firstArtifact.GoverningSystemPromptSetup);
        Assert.Null(firstArtifact.PreviousArtifact);
        Assert.Empty(firstArtifact.InputArtifacts);
        Assert.Equal(SJ.MemoryPackCarrier.Action, firstArtifact.Target.Carrier);
        Assert.Equal("roleplay.first-person-autobiography", firstArtifact.Target.BlockKey);
        Assert.Equal("summary-e2e", firstArtifact.Content);
        Assert.True(firstArtifact.MemoryPack.TryGetBlock(firstArtifact.Target, out SJ.MemoryPackBlock? targetBlock));
        Assert.Equal("summary-e2e", targetBlock.Text);
        Assert.NotNull(firstArtifact.Invocation);
        Assert.Equal(firstRecord.CallLogPaths, firstArtifact.CallLogPaths);
        AssertHistoryUnchanged(rawBefore, ReadHistorySnapshot(repoPath));
        byte[] firstOutputBytes = File.ReadAllBytes(firstOutputPath);

        int callCountBeforeRejectedReplay = factory.CompletionCallCount;
        int rejectedExitCode = RunSessionJournalReplay(
            factory,
            repoPath,
            connectionsPath,
            firstOutputPath,
            Path.Combine(_tempRoot, "rejected-calls")
        );

        Assert.Equal(1, rejectedExitCode);
        Assert.Equal(callCountBeforeRejectedReplay, factory.CompletionCallCount);
        Assert.Equal(firstOutputBytes, File.ReadAllBytes(firstOutputPath));
        RollingSummaryReplayRecord preservedRecord = ReadSingleRecord(firstOutputPath);
        Assert.Equal(firstRecord.ArtifactId, preservedRecord.ArtifactId);
        Assert.Equal(firstRecord.SourceEndInclusive, preservedRecord.SourceEndInclusive);
        Assert.Equal(firstRecord.Status, preservedRecord.Status);
        Assert.Empty(Directory.EnumerateFiles(
            Path.GetDirectoryName(firstOutputPath)!,
            $".{Path.GetFileName(firstOutputPath)}.*.tmp"
        ));
        AssertHistoryUnchanged(rawBefore, ReadHistorySnapshot(repoPath));

        string exactDerivedStoreRoot = Path.Combine(repoPath, "derived", "recaps", "v1");
        Assert.Equal(Path.GetFullPath(store.StoreRoot), Path.GetFullPath(exactDerivedStoreRoot));
        Directory.Delete(exactDerivedStoreRoot, recursive: true);
        string regeneratedOutputPath = Path.Combine(_tempRoot, "regenerated.jsonl");
        string regeneratedCallLogDir = Path.Combine(_tempRoot, "regenerated-calls");

        int regeneratedExitCode = RunSessionJournalReplay(
            factory,
            repoPath,
            connectionsPath,
            regeneratedOutputPath,
            regeneratedCallLogDir
        );

        Assert.Equal(0, regeneratedExitCode);
        Assert.Equal(callCountBeforeRejectedReplay + 1, factory.CompletionCallCount);
        RollingSummaryReplayRecord regeneratedRecord = ReadSingleRecord(regeneratedOutputPath);
        Assert.Equal("succeeded", regeneratedRecord.Status);
        Assert.NotNull(regeneratedRecord.ArtifactId);
        string regeneratedCallLogPath = Assert.Single(regeneratedRecord.CallLogPaths);
        Assert.Equal(
            "replay-rolling-summary-session-journal",
            ReadCallLogCommand(regeneratedCallLogPath)
        );
        var regeneratedStore = DerivedRecapStore.Open(repoPath);
        DerivedRecapArtifact? regeneratedArtifact = await regeneratedStore.TryReadArtifactAsync(
            regeneratedRecord.ArtifactId
        );
        Assert.NotNull(regeneratedArtifact);
        DerivedRecapLineageKey lineageKey = DerivedRecapLineageKey.Create(
            DerivedRecapArtifactKinds.RollingSummary,
            regeneratedArtifact.ProfileId,
            regeneratedArtifact.Target
        );
        DerivedRecapArtifact? usableLatest = await regeneratedStore.TryReadLatestAsync(lineageKey);
        Assert.NotNull(usableLatest);
        Assert.Equal(regeneratedArtifact.ArtifactId, usableLatest.ArtifactId);
        AssertHistoryUnchanged(rawBefore, ReadHistorySnapshot(repoPath));
    }

    [Fact]
    public void InjectedFactory_DrivesLlmSmokeAndLegacyRollingWithExactCommandContexts() {
        Directory.CreateDirectory(_tempRoot);
        string legacyPath = Path.Combine(_tempRoot, "legacy.json");
        string connectionsPath = Path.Combine(_tempRoot, "connections.json");
        WriteLegacyExport(legacyPath, turnCount: 3);
        WriteConnections(connectionsPath);
        var factory = new ScriptedCompletionClientFactory("summary-e2e");
        string smokeCallLogDir = Path.Combine(_tempRoot, "smoke-calls");

        int smokeExitCode = Program.MainCore(
            [
                "llm-smoke",
                "--connections", connectionsPath,
                "--connection", "scripted",
                "--call-log-dir", smokeCallLogDir
            ],
            factory
        );

        Assert.Equal(0, smokeExitCode);
        Assert.Equal(1, factory.CompletionCallCount);
        Assert.Equal(
            "llm-smoke",
            ReadCallLogCommand(Assert.Single(Directory.EnumerateFiles(smokeCallLogDir, "*.json")))
        );

        string legacyOutputPath = Path.Combine(_tempRoot, "legacy-rolling.jsonl");
        string legacyCallLogDir = Path.Combine(_tempRoot, "legacy-rolling-calls");
        int legacyExitCode = Program.MainCore(
            [
                "replay-rolling-summary",
                "--input", legacyPath,
                "--output", legacyOutputPath,
                "--connections", connectionsPath,
                "--connection", "scripted",
                "--call-log-dir", legacyCallLogDir,
                "--threshold-tokens", "1",
                "--max-epochs", "1",
                "--preset", "autobiographical-rewrite"
            ],
            factory
        );

        Assert.Equal(0, legacyExitCode);
        Assert.Equal(2, factory.CompletionCallCount);
        RollingSummaryReplayRecord record = ReadSingleRecord(legacyOutputPath);
        Assert.Equal(RollingSummaryReplaySourceKinds.LegacyChatSessionExport, record.SourceKind);
        Assert.Null(record.ArtifactId);
        string callLogPath = Assert.Single(record.CallLogPaths);
        Assert.Equal("replay-rolling-summary", ReadCallLogCommand(callLogPath));
    }

    [Fact]
    public void SessionJournalCommand_RejectsRepositoryContainedOutputAndCallLogPathsBeforeWrites() {
        Directory.CreateDirectory(_tempRoot);
        string legacyPath = Path.Combine(_tempRoot, "legacy.json");
        string repoPath = Path.Combine(_tempRoot, "session-journal");
        string connectionsPath = Path.Combine(_tempRoot, "connections.json");
        WriteLegacyExport(legacyPath, turnCount: 3);
        WriteConnections(connectionsPath);
        var factory = new ScriptedCompletionClientFactory("must-not-run");

        Assert.Equal(
            0,
            Program.MainCore(
                [
                    "import-session-journal",
                    "--input", legacyPath,
                    "--output", repoPath
                ],
                factory
            )
        );
        SessionHistorySnapshot rawBefore = ReadHistorySnapshot(repoPath);
        string rawFilePath = Assert.Single(
            Directory.EnumerateFiles(
                Path.Combine(repoPath, "events"),
                "*.rbf",
                SearchOption.AllDirectories
            )
        );
        byte[] rawFileBytes = File.ReadAllBytes(rawFilePath);

        int containedOutputExitCode = RunSessionJournalReplay(
            factory,
            repoPath,
            connectionsPath,
            rawFilePath,
            Path.Combine(_tempRoot, "outside-calls")
        );

        Assert.Equal(1, containedOutputExitCode);
        Assert.Equal(0, factory.CompletionCallCount);
        Assert.Equal(rawFileBytes, File.ReadAllBytes(rawFilePath));
        AssertHistoryUnchanged(rawBefore, ReadHistorySnapshot(repoPath));

        string outsideOutputPath = Path.Combine(_tempRoot, "outside.jsonl");
        string containedCallLogDir = Path.Combine(repoPath, "forbidden-calls");
        int containedCallLogExitCode = RunSessionJournalReplay(
            factory,
            repoPath,
            connectionsPath,
            outsideOutputPath,
            containedCallLogDir
        );

        Assert.Equal(1, containedCallLogExitCode);
        Assert.Equal(0, factory.CompletionCallCount);
        Assert.False(File.Exists(outsideOutputPath));
        Assert.False(Directory.Exists(containedCallLogDir));
        Assert.Equal(rawFileBytes, File.ReadAllBytes(rawFilePath));
        AssertHistoryUnchanged(rawBefore, ReadHistorySnapshot(repoPath));
    }

    [Fact]
    public void SessionJournalCommand_RejectsSymlinkAliasesBeforeWrites() {
        Directory.CreateDirectory(_tempRoot);
        string legacyPath = Path.Combine(_tempRoot, "legacy.json");
        string repoPath = Path.Combine(_tempRoot, "session-journal");
        string connectionsPath = Path.Combine(_tempRoot, "connections.json");
        WriteLegacyExport(legacyPath, turnCount: 3);
        WriteConnections(connectionsPath);
        var factory = new ScriptedCompletionClientFactory("must-not-run");

        Assert.Equal(
            0,
            Program.MainCore(
                [
                    "import-session-journal",
                    "--input", legacyPath,
                    "--output", repoPath
                ],
                factory
            )
        );
        SessionHistorySnapshot rawBefore = ReadHistorySnapshot(repoPath);
        string rawFilePath = Assert.Single(
            Directory.EnumerateFiles(
                Path.Combine(repoPath, "events"),
                "*.rbf",
                SearchOption.AllDirectories
            )
        );
        byte[] rawFileBytes = File.ReadAllBytes(rawFilePath);

        string repoAliasPath = Path.Combine(_tempRoot, "repo-alias");
        CreateDirectorySymbolicLinkOrSkip(repoAliasPath, repoPath);
        int repoAliasExitCode = RunSessionJournalReplay(
            factory,
            repoAliasPath,
            connectionsPath,
            rawFilePath,
            Path.Combine(_tempRoot, "repo-alias-calls")
        );

        Assert.Equal(1, repoAliasExitCode);
        Assert.Equal(0, factory.CompletionCallCount);
        Assert.Equal(rawFileBytes, File.ReadAllBytes(rawFilePath));
        AssertHistoryUnchanged(rawBefore, ReadHistorySnapshot(repoPath));

        string rawParentPath = Path.GetDirectoryName(rawFilePath)!;
        string outputParentAliasPath = Path.Combine(_tempRoot, "output-parent-alias");
        CreateDirectorySymbolicLinkOrSkip(outputParentAliasPath, rawParentPath);
        string aliasedOutputPath = Path.Combine(outputParentAliasPath, "must-not-exist.jsonl");
        int outputAliasExitCode = RunSessionJournalReplay(
            factory,
            repoPath,
            connectionsPath,
            aliasedOutputPath,
            Path.Combine(_tempRoot, "output-alias-calls")
        );

        Assert.Equal(1, outputAliasExitCode);
        Assert.Equal(0, factory.CompletionCallCount);
        Assert.False(File.Exists(Path.Combine(rawParentPath, "must-not-exist.jsonl")));
        Assert.Equal(rawFileBytes, File.ReadAllBytes(rawFilePath));
        AssertHistoryUnchanged(rawBefore, ReadHistorySnapshot(repoPath));

        string callLogAliasPath = Path.Combine(_tempRoot, "call-log-alias");
        CreateDirectorySymbolicLinkOrSkip(callLogAliasPath, rawParentPath);
        string outsideOutputPath = Path.Combine(_tempRoot, "outside.jsonl");
        int callLogAliasExitCode = RunSessionJournalReplay(
            factory,
            repoPath,
            connectionsPath,
            outsideOutputPath,
            callLogAliasPath
        );

        Assert.Equal(1, callLogAliasExitCode);
        Assert.Equal(0, factory.CompletionCallCount);
        Assert.False(File.Exists(outsideOutputPath));
        Assert.Equal(rawFileBytes, File.ReadAllBytes(rawFilePath));
        AssertHistoryUnchanged(rawBefore, ReadHistorySnapshot(repoPath));
    }

    [Fact]
    public void MainCore_MalformedConnectionsJsonReturnsFailureInsteadOfEscaping() {
        Directory.CreateDirectory(_tempRoot);
        string malformedConnectionsPath = Path.Combine(_tempRoot, "malformed-connections.json");
        File.WriteAllText(malformedConnectionsPath, "{");
        var factory = new ScriptedCompletionClientFactory("must-not-run");

        int exitCode = Program.MainCore(
            [
                "llm-smoke",
                "--connections", malformedConnectionsPath,
                "--call-log-dir", Path.Combine(_tempRoot, "calls")
            ],
            factory
        );

        Assert.Equal(1, exitCode);
        Assert.Equal(0, factory.CompletionCallCount);
    }

    private static void CreateDirectorySymbolicLinkOrSkip(string path, string targetPath) {
        try {
            Directory.CreateSymbolicLink(path, targetPath);
        }
        catch (Exception ex) when (
            ex is IOException or NotSupportedException or UnauthorizedAccessException
        ) {
            throw SkipException.ForSkip(
                $"Directory symbolic links are unavailable on this platform: {ex.Message}"
            );
        }
    }

    private static int RunSessionJournalReplay(
        ICompletionClientFactory factory,
        string repoPath,
        string connectionsPath,
        string outputPath,
        string callLogDir
    ) => Program.MainCore(
        [
            "replay-rolling-summary-session-journal",
            "--input", repoPath,
            "--output", outputPath,
            "--connections", connectionsPath,
            "--connection", "scripted",
            "--call-log-dir", callLogDir,
            "--threshold-tokens", "1",
            "--max-epochs", "1",
            "--preset", "autobiographical-rewrite"
        ],
        factory
    );

    private static RollingSummaryReplayRecord ReadSingleRecord(string path) {
        string line = Assert.Single(File.ReadAllLines(path));
        return JsonSerializer.Deserialize<RollingSummaryReplayRecord>(line, WebJsonOptions)
            ?? throw new InvalidDataException("Replay output record is empty.");
    }

    private static string ReadCallLogCommand(string path) {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement
            .GetProperty("context")
            .GetProperty("command")
            .GetString()
            ?? throw new InvalidDataException("Call log command is empty.");
    }

    private static SessionHistorySnapshot ReadHistorySnapshot(string repoPath) {
        using var engine = SJ.SessionJournalEngine.Open(repoPath);
        SJ.SessionHistoryReplay replay = engine.ReplayHistory();
        return new SessionHistorySnapshot(
            EventAddressTextCodec.Format(replay.SourceRawHead!.Value),
            replay.Messages.Select(static entry => new SessionHistoryMessageSnapshot(
                entry.Message.Kind.ToString(),
                entry.Message switch {
                    ActionMessage action => action.GetFlattenedText(),
                    ObservationMessage observation => observation.Content ?? string.Empty,
                    _ => entry.Message.ToString() ?? string.Empty
                },
                EventAddressTextCodec.Format(entry.SourceStartInclusive),
                EventAddressTextCodec.Format(entry.SourceEndInclusive)
            )).ToArray()
        );
    }

    private static SJ.SessionGoverningSetup ReadGoverningSetup(
        string repoPath,
        Atelia.EventJournal.EventAddress sourceRawHead
    ) {
        using var engine = SJ.SessionJournalEngine.Open(repoPath);
        return engine.ResolveGoverningSetup(sourceRawHead);
    }

    private static void AssertHistoryUnchanged(
        SessionHistorySnapshot expected,
        SessionHistorySnapshot actual
    ) {
        Assert.Equal(expected.SourceRawHead, actual.SourceRawHead);
        Assert.Equal(expected.Messages, actual.Messages);
    }

    private static void WriteConnections(string path) {
        var config = new CompletionConnectionsFileConfig(
            Connections: [
                new CompletionConnectionConfig(
                    Id: "scripted",
                    Kind: "scripted",
                    ModelId: "model-a",
                    CompletionSurfaceId: "surface-a",
                    BaseAddress: "http://localhost/"
                )
            ],
            DefaultConnectionId: "scripted"
        );
        File.WriteAllText(path, JsonSerializer.Serialize(config, WebJsonOptions));
    }

    private static void WriteLegacyExport(string path, int turnCount) {
        var events = new List<ChatSessionLegacyReplayEvent> {
            new() {
                Ordinal = 0,
                Commit = "commit-0",
                Kind = ChatSessionLegacyEventKinds.InitialState,
                Root = new ChatSessionLegacyRootMetadataDto {
                    Kind = "chat-session",
                    SchemaVersion = 1,
                    ApiSpecId = "legacy-upgrade-export",
                    CompletionSurfaceId = "surface-a",
                    ModelId = "model-a",
                    SystemPrompt = "system-a"
                },
                Messages = []
            }
        };
        for (int turn = 1; turn <= turnCount; turn++) {
            events.Add(new ChatSessionLegacyReplayEvent {
                Ordinal = turn,
                Commit = $"commit-{turn}",
                Kind = ChatSessionLegacyEventKinds.ModelTurn,
                AppendedMessages = [
                    new ChatSessionLegacyMessageDto {
                        Kind = "observation",
                        Content = $"observation {turn}"
                    },
                    new ChatSessionLegacyMessageDto {
                        Kind = "action",
                        Action = new ChatSessionLegacyActionMessageDto {
                            Blocks = [
                                new SerializedActionBlock(
                                    ActionMessageSerialization.BlockKindText,
                                    $"action {turn}",
                                    ToolName: null,
                                    ToolCallId: null,
                                    RawArgumentsJson: null,
                                    Reasoning: null
                                )
                            ]
                        }
                    }
                ]
            });
        }

        var source = new ChatSessionLegacyEventSource {
            Schema = ChatSessionLegacyEventSourceSchema.SchemaId,
            BranchName = "main",
            Events = events
        };
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(source, ChatSessionLegacyEventSourceReader.JsonOptions)
        );
    }

    private sealed class ScriptedCompletionClientFactory(string responseText) : ICompletionClientFactory {
        private readonly ScriptedCompletionClient _client = new(responseText);

        public int CompletionCallCount => _client.CallCount;

        public ICompletionClient Create(CompletionConnectionConfig connection) {
            _ = connection;
            return _client;
        }
    }

    private sealed class ScriptedCompletionClient(string responseText) : ICompletionClient {
        public string Name => "scripted";

        public string ApiSpecId => "test-api-v1";

        public int CallCount { get; private set; }

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(new CompletionResult(
                new ActionMessage([new ActionBlock.Text(responseText)]),
                CompletionDescriptor.From(this, request)
            ));
        }
    }

    private sealed record SessionHistorySnapshot(
        string SourceRawHead,
        IReadOnlyList<SessionHistoryMessageSnapshot> Messages
    );

    private sealed record SessionHistoryMessageSnapshot(
        string Kind,
        string Text,
        string SourceStartInclusive,
        string SourceEndInclusive
    );
}
