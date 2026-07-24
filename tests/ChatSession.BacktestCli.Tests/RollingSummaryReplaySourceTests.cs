using Atelia.ChatSession;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.Derived;
using ChatSessionBacktestCli;
using SJ = Atelia.SessionJournal;
using Xunit;

namespace Atelia.ChatSession.BacktestCli.Tests;

public sealed class RollingSummaryReplaySourceTests : IDisposable {
    private readonly List<string> _tempDirectories = [];

    public void Dispose() {
        foreach (string path in _tempDirectories) {
            try {
                if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); }
            }
            catch {
                // Best-effort cleanup for temp test directories.
            }
        }
    }

    [Fact]
    public async Task LegacySource_PreservesExistingTriggerShape() {
        var runner = CreateRunner(new LegacyRollingSummaryReplaySource(CreateLegacyEventSource()));

        var records = await RunAllAsync(runner);

        var record = Assert.Single(records);
        Assert.Equal(RollingSummaryReplaySourceKinds.LegacyChatSessionExport, record.SourceKind);
        Assert.Equal("commit-1", record.SourceId);
        Assert.Equal(1, record.EventOrdinal);
        Assert.Equal("commit-1", record.EventCommit);
        Assert.Null(record.SourceRawHead);
        Assert.Null(record.SourceStartInclusive);
        Assert.Null(record.SourceEndInclusive);
        Assert.Equal(2, record.SplitIndex);
        Assert.Equal(2, record.RemainingActiveMessageCount);
        Assert.Equal("succeeded", record.Status);
    }

    [Fact]
    public async Task LegacySource_OrdinalMismatchThrowsInvalidDataException() {
        var source = new LegacyRollingSummaryReplaySource(new ChatSessionLegacyEventSource {
            Schema = ChatSessionLegacyEventSourceSchema.SchemaId,
            Events = [
                new ChatSessionLegacyReplayEvent {
                    Ordinal = 1,
                    Commit = "commit-1",
                    Kind = ChatSessionLegacyEventKinds.InitialState,
                    Messages = []
                }
            ]
        });

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => DrainAsync(source));
        Assert.Contains("Event ordinal mismatch", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionJournalSource_UsesAddressedReplayAndSameRunner() {
        string repoPath = CreateSessionJournalWithTwoTurns();
        var runner = CreateRunner(SessionJournalRollingSummaryReplaySource.Open(repoPath));

        var records = await RunAllAsync(runner);

        var record = Assert.Single(records);
        Assert.Equal(RollingSummaryReplaySourceKinds.SessionJournal, record.SourceKind);
        Assert.Null(record.EventOrdinal);
        Assert.Null(record.EventCommit);
        Assert.NotNull(record.SourceRawHead);
        Assert.NotNull(record.SourceStartInclusive);
        Assert.NotNull(record.SourceEndInclusive);
        Assert.Equal(record.SourceEndInclusive, record.SourceId);
        Assert.True(EventAddressTextCodec.TryParse(record.SourceEndInclusive, out _));
        Assert.Equal(2, record.SplitIndex);
        Assert.Equal(2, record.RemainingActiveMessageCount);
    }

    [Fact]
    public async Task SessionJournalSource_EmptyHistoryProducesNoRecords() {
        string repoPath = NewTempPath();
        using (SJ.SessionJournalEngine.Create(repoPath, new SJ.SessionCreateOptions("model-a", "system", "surface"))) {
        }
        var runner = CreateRunner(SessionJournalRollingSummaryReplaySource.Open(repoPath));

        var records = await RunAllAsync(runner);

        Assert.Empty(records);
    }

    [Fact]
    public async Task Runner_RemovesSlidingPrefixAfterSuccessfulMaintainer() {
        var runner = CreateRunner(new LegacyRollingSummaryReplaySource(CreateLegacyEventSource()));

        var records = await RunAllAsync(runner);

        var record = Assert.Single(records);
        Assert.Equal(record.SplitIndex, record.SlidingOutMessageCount);
        Assert.Equal(2, record.RemainingActiveMessageCount);
        Assert.NotNull(record.NewBlock);
    }

    private RollingSummaryReplayRunner CreateRunner(IRollingSummaryReplaySource source)
        => new(
            source,
            new ScriptedCompletionClient("summary"),
            new CompletionConnectionConfig(
                Id: "test",
                Kind: "scripted",
                ModelId: "model-a",
                CompletionSurfaceId: "surface",
                BaseAddress: "http://localhost"
            ),
            new ReplayMemoryMaintainerProfile(
                "test",
                new SJ.MemoryRewriteProfile(
                    "rolling-summary",
                    new SJ.MemoryPackBlockPath(SJ.MemoryPackCarrier.Observation, "session.rolling-summary"),
                    "system prompt",
                    "user prompt"
                )
            ),
            Path.Combine(NewTempPath(), "calls"),
            thresholdTokens: 1,
            maxEpochs: 1
        );

    private static async Task<IReadOnlyList<RollingSummaryReplayRecord>> RunAllAsync(RollingSummaryReplayRunner runner) {
        var records = new List<RollingSummaryReplayRecord>();
        await foreach (var record in runner.RunAsync(CancellationToken.None)) {
            records.Add(record);
        }

        return records;
    }

    private static async Task DrainAsync(IRollingSummaryReplaySource source) {
        await foreach (var _ in source.ReadStepsAsync(CancellationToken.None)) {
        }
    }

    private static ChatSessionLegacyEventSource CreateLegacyEventSource()
        => new() {
            Schema = ChatSessionLegacyEventSourceSchema.SchemaId,
            Events = [
                new ChatSessionLegacyReplayEvent {
                    Ordinal = 0,
                    Commit = "commit-0",
                    Kind = ChatSessionLegacyEventKinds.InitialState,
                    Messages = [
                        Observation("hello 1"),
                        Action("answer 1")
                    ]
                },
                new ChatSessionLegacyReplayEvent {
                    Ordinal = 1,
                    Commit = "commit-1",
                    Kind = ChatSessionLegacyEventKinds.ModelTurn,
                    AppendedMessages = [
                        Observation("hello 2"),
                        Action("answer 2")
                    ]
                }
            ]
        };

    private string CreateSessionJournalWithTwoTurns() {
        string repoPath = NewTempPath();
        using var engine = SJ.SessionJournalEngine.Create(repoPath, new SJ.SessionCreateOptions("model-a", "system", "surface"));
        engine.AppendObservation("hello 1");
        engine.AppendAgentAction(
            new ActionMessage([new ActionBlock.Text("answer 1")]),
            new CompletionDescriptor("scripted", "openai-chat-v1", "model-a")
        );
        engine.AppendObservation("hello 2");
        engine.AppendAgentAction(
            new ActionMessage([new ActionBlock.Text("answer 2")]),
            new CompletionDescriptor("scripted", "openai-chat-v1", "model-a")
        );
        return repoPath;
    }

    private static ChatSessionLegacyMessageDto Observation(string text)
        => new() {
            Kind = "observation",
            Content = text
        };

    private static ChatSessionLegacyMessageDto Action(string text)
        => new() {
            Kind = "action",
            Action = new ChatSessionLegacyActionMessageDto {
                Blocks = [
                    new SerializedActionBlock(
                        ActionMessageSerialization.BlockKindText,
                        text,
                        ToolName: null,
                        ToolCallId: null,
                        RawArgumentsJson: null,
                        Reasoning: null
                    )
                ]
            }
        };

    private string NewTempPath() {
        string path = Path.Combine(Path.GetTempPath(), "atelia-backtest-cli-tests", Guid.NewGuid().ToString("N"));
        _tempDirectories.Add(path);
        return path;
    }

    private sealed class ScriptedCompletionClient(string responseText) : ICompletionClient {
        public string Name => "scripted";

        public string ApiSpecId => "openai-chat-v1";

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new CompletionResult(
                new ActionMessage([new ActionBlock.Text(responseText)]),
                CompletionDescriptor.From(this, request)
            ));
        }
    }
}
