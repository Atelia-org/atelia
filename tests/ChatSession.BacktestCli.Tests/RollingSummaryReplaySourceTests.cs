using Atelia.ChatSession;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal;
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
        SessionHistoryReplaySnapshot replay = ReadHistoryReplay(repoPath);
        var runner = CreateRunner(SessionJournalRollingSummaryReplaySource.Open(repoPath));

        var records = await RunAllAsync(runner);

        var record = Assert.Single(records);
        Assert.Equal(RollingSummaryReplaySourceKinds.SessionJournal, record.SourceKind);
        Assert.Null(record.EventOrdinal);
        Assert.Null(record.EventCommit);
        Assert.NotNull(record.SourceRawHead);
        Assert.NotNull(record.SourceStartInclusive);
        Assert.NotNull(record.SourceEndInclusive);
        Assert.Equal(replay.SourceRawHead, record.SourceRawHead);
        Assert.Equal(replay.Messages[0].SourceStartInclusive, record.SourceStartInclusive);
        Assert.Equal(replay.Messages[1].SourceEndInclusive, record.SourceEndInclusive);
        Assert.Equal(replay.Messages[3].SourceEndInclusive, record.SourceId);
        Assert.NotEqual(record.SourceEndInclusive, record.SourceId);
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

    [Fact]
    public async Task SessionJournalSource_ConsecutiveEpochsUseFragmentRangesAndSameRawHead() {
        string repoPath = CreateSessionJournalWithTurns(3);
        SessionHistoryReplaySnapshot replay = ReadHistoryReplay(repoPath);
        var runner = CreateRunner(
            SessionJournalRollingSummaryReplaySource.Open(repoPath),
            maxEpochs: 2
        );

        var records = await RunAllAsync(runner);

        Assert.Collection(
            records,
            first => {
                Assert.Equal(replay.Messages[0].SourceStartInclusive, first.SourceStartInclusive);
                Assert.Equal(replay.Messages[1].SourceEndInclusive, first.SourceEndInclusive);
                Assert.Equal(replay.Messages[3].SourceEndInclusive, first.SourceId);
                Assert.Equal(replay.SourceRawHead, first.SourceRawHead);
            },
            second => {
                Assert.Equal(replay.Messages[2].SourceStartInclusive, second.SourceStartInclusive);
                Assert.Equal(replay.Messages[3].SourceEndInclusive, second.SourceEndInclusive);
                Assert.Equal(replay.Messages[5].SourceEndInclusive, second.SourceId);
                Assert.Equal(replay.SourceRawHead, second.SourceRawHead);
            }
        );
        Assert.Equal(records[0].SourceRawHead, records[1].SourceRawHead);
    }

    [Fact]
    public async Task Runner_FailureReportsAttemptedFragmentRangeWithoutRemovingPrefix() {
        string repoPath = CreateSessionJournalWithTwoTurns();
        SessionHistoryReplaySnapshot replay = ReadHistoryReplay(repoPath);
        var runner = CreateRunner(
            SessionJournalRollingSummaryReplaySource.Open(repoPath),
            new ThrowingCompletionClient()
        );

        var records = await RunAllAsync(runner);

        var record = Assert.Single(records);
        Assert.True(runner.HadFailure);
        Assert.Equal("failed", record.Status);
        Assert.Equal(typeof(InvalidOperationException).FullName, record.ExceptionType);
        Assert.Equal(replay.Messages[0].SourceStartInclusive, record.SourceStartInclusive);
        Assert.Equal(replay.Messages[1].SourceEndInclusive, record.SourceEndInclusive);
        Assert.Equal(2, record.SplitIndex);
        Assert.Equal(4, record.RemainingActiveMessageCount);
    }

    [Fact]
    public async Task ArtifactWriter_SuccessWritesLinkedArtifactWithSnapshotProvenance() {
        string repoPath = CreateSessionJournalWithGoverningPromptChange();
        SessionHistoryReplaySnapshot replay = ReadHistoryReplay(repoPath);
        SJ.SessionGoverningSetup sourceHeadSetup;
        using (var engine = SJ.SessionJournalEngine.Open(repoPath)) {
            sourceHeadSetup = engine.ResolveGoverningSetup(
                EventAddressTextCodec.Parse(replay.SourceRawHead)
            );
        }
        var runner = CreateRunner(
            SessionJournalRollingSummaryReplaySource.Open(repoPath),
            artifactRepoPath: repoPath
        );

        var records = await RunAllAsync(runner);

        var record = Assert.Single(records);
        Assert.Equal("succeeded", record.Status);
        Assert.NotNull(record.ArtifactId);
        Assert.NotNull(record.ArtifactPath);
        Assert.True(File.Exists(record.ArtifactPath));
        Assert.Equal(record.SourceEndInclusive, record.AnchorRawEvent);
        Assert.Null(record.PreviousArtifact);
        Assert.NotEmpty(record.CallLogPaths);

        var store = DerivedRecapStore.Open(repoPath);
        var artifact = await store.TryReadArtifactAsync(record.ArtifactId);
        Assert.NotNull(artifact);
        SJ.SessionGoverningSetup anchorSetup;
        using (var engine = SJ.SessionJournalEngine.Open(repoPath)) {
            anchorSetup = engine.ResolveGoverningSetup(
                artifact.AnchorRawEvent
            );
        }
        Assert.Equal(DerivedRecapArtifactKinds.RollingSummary, artifact.ArtifactKind);
        Assert.Equal(EventAddressTextCodec.Parse(record.SourceRawHead!), artifact.SourceRawHead);
        Assert.Null(artifact.SourceStartExclusive);
        Assert.Equal(EventAddressTextCodec.Parse(record.SourceEndInclusive!), artifact.SourceEndInclusive);
        Assert.Equal(artifact.SourceEndInclusive, artifact.AnchorRawEvent);
        Assert.NotEqual(
            sourceHeadSetup.SystemPromptSetupAddress,
            anchorSetup.SystemPromptSetupAddress
        );
        Assert.Equal(
            anchorSetup.RuntimeConfigSetupAddress,
            artifact.GoverningRuntimeConfigSetup
        );
        Assert.Equal(
            anchorSetup.SystemPromptSetupAddress,
            artifact.GoverningSystemPromptSetup
        );
        Assert.Equal("rolling-summary", artifact.ProfileId);
        Assert.Equal(record.Invocation, artifact.Invocation);
        Assert.Equal(record.CallLogPaths, artifact.CallLogPaths);
        Assert.True(artifact.MemoryPack.TryGetBlock(artifact.Target, out var targetBlock));
        Assert.Equal("summary", targetBlock.Text);

        SessionHistoryReplaySnapshot replayAfter = ReadHistoryReplay(repoPath);
        Assert.Equal(replay.SourceRawHead, replayAfter.SourceRawHead);
        Assert.Equal(replay.Messages, replayAfter.Messages);
    }

    [Fact]
    public async Task ArtifactWriter_ConsecutiveEpochsBuildsRunLocalLineage() {
        string repoPath = CreateSessionJournalWithTurns(3);
        var runner = CreateRunner(
            SessionJournalRollingSummaryReplaySource.Open(repoPath),
            maxEpochs: 2,
            artifactRepoPath: repoPath
        );

        var records = await RunAllAsync(runner);

        Assert.Equal(2, records.Count);
        var store = DerivedRecapStore.Open(repoPath);
        var first = await store.TryReadArtifactAsync(records[0].ArtifactId!);
        var second = await store.TryReadArtifactAsync(records[1].ArtifactId!);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(first.PreviousArtifact);
        Assert.Null(first.SourceStartExclusive);
        Assert.Empty(first.InputArtifacts);
        Assert.Equal(first.ArtifactId, second.PreviousArtifact);
        Assert.Equal(first.AnchorRawEvent, second.SourceStartExclusive);
        Assert.Equal([first.ArtifactId], second.InputArtifacts);
        Assert.Equal(first.ArtifactId, records[1].PreviousArtifact);
        Assert.Equal(EventAddressTextCodec.Format(second.AnchorRawEvent), records[1].AnchorRawEvent);

        var latest = await store.TryReadLatestAsync(first.LineageKey);
        Assert.NotNull(latest);
        Assert.Equal(second.ArtifactId, latest.ArtifactId);
    }

    [Fact]
    public async Task ArtifactWriter_MaintainerFailureDoesNotWriteProducedArtifact() {
        string repoPath = CreateSessionJournalWithTwoTurns();
        var runner = CreateRunner(
            SessionJournalRollingSummaryReplaySource.Open(repoPath),
            new ThrowingCompletionClient(),
            artifactRepoPath: repoPath
        );

        var records = await RunAllAsync(runner);

        var record = Assert.Single(records);
        Assert.Equal("failed", record.Status);
        Assert.Null(record.ArtifactId);
        Assert.Null(record.ArtifactPath);
        Assert.Null(record.AnchorRawEvent);
        Assert.Null(record.PreviousArtifact);
        Assert.Equal(4, record.RemainingActiveMessageCount);
        var store = DerivedRecapStore.Open(repoPath);
        Assert.Empty(Directory.EnumerateFiles(store.ArtifactsDirectory, "*.json"));
        var lineage = DerivedRecapLineageKey.Create(
            DerivedRecapArtifactKinds.RollingSummary,
            "rolling-summary",
            new SJ.MemoryPackBlockPath(SJ.MemoryPackCarrier.Observation, "session.rolling-summary")
        );
        Assert.Null(await store.TryReadLatestAsync(lineage));
    }

    [Fact]
    public async Task ArtifactWriter_OperationalFailureReportsCandidateWithoutCommittingPrefix() {
        string repoPath = CreateSessionJournalWithTwoTurns();
        var writer = new ThrowingArtifactWriter();
        var runner = CreateRunner(
            SessionJournalRollingSummaryReplaySource.Open(repoPath),
            artifactWriter: writer
        );

        var records = await RunAllAsync(runner);

        var record = Assert.Single(records);
        Assert.True(runner.HadFailure);
        Assert.Equal("failed", record.Status);
        Assert.Equal(typeof(RollingSummaryArtifactWriteException).FullName, record.ExceptionType);
        Assert.NotNull(record.NewBlock);
        Assert.NotNull(record.Invocation);
        Assert.NotEmpty(record.CallLogPaths);
        Assert.Null(record.ArtifactId);
        Assert.Null(record.ArtifactPath);
        Assert.Null(record.AnchorRawEvent);
        Assert.Null(record.PreviousArtifact);
        Assert.Equal(4, record.RemainingActiveMessageCount);
        Assert.Equal(1, writer.WriteCount);
    }

    [Fact]
    public async Task ArtifactWriter_ExistingTargetLineageFailsBeforeCompletionCall() {
        string repoPath = CreateSessionJournalWithTwoTurns();
        var firstClient = new ScriptedCompletionClient("summary");
        var firstRunner = CreateRunner(
            SessionJournalRollingSummaryReplaySource.Open(repoPath),
            firstClient,
            artifactRepoPath: repoPath
        );
        _ = await RunAllAsync(firstRunner);
        var secondClient = new ScriptedCompletionClient("summary");
        var secondRunner = CreateRunner(
            SessionJournalRollingSummaryReplaySource.Open(repoPath),
            secondClient,
            artifactRepoPath: repoPath
        );

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAllAsync(secondRunner));

        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, secondClient.CallCount);
    }

    [Fact]
    public async Task ArtifactWriter_ConcurrentRootWritersAllowOnlyOneLineageCommit() {
        string repoPath = CreateSessionJournalWithTwoTurns();
        SessionHistoryReplaySnapshot replay = ReadHistoryReplay(repoPath);
        EventAddress sourceRawHead = EventAddressTextCodec.Parse(replay.SourceRawHead);
        var client = new ScriptedCompletionClient("summary");
        CompletionConnectionConfig connection = CreateTestConnection();
        ReplayMemoryMaintainerProfile profile = CreateTestProfile();
        var firstWriter = SessionJournalDerivedRecapWriter.Open(repoPath, profile, client, connection);
        var secondWriter = SessionJournalDerivedRecapWriter.Open(repoPath, profile, client, connection);
        await firstWriter.PrepareAsync(sourceRawHead, CancellationToken.None);
        await secondWriter.PrepareAsync(sourceRawHead, CancellationToken.None);
        RollingSummaryArtifactCandidate firstCandidate = CreateArtifactCandidate(
            profile,
            EventAddressTextCodec.Parse(replay.Messages[1].SourceEndInclusive),
            "summary-a"
        );
        RollingSummaryArtifactCandidate secondCandidate = CreateArtifactCandidate(
            profile,
            EventAddressTextCodec.Parse(replay.Messages[3].SourceEndInclusive),
            "summary-b"
        );

        Task<RollingSummaryArtifactLink> firstTask = firstWriter
            .WriteProducedAsync(firstCandidate, CancellationToken.None)
            .AsTask();
        Task<RollingSummaryArtifactLink> secondTask = secondWriter
            .WriteProducedAsync(secondCandidate, CancellationToken.None)
            .AsTask();
        await Assert.ThrowsAnyAsync<Exception>(() => Task.WhenAll(firstTask, secondTask));

        Task<RollingSummaryArtifactLink>[] tasks = [firstTask, secondTask];
        _ = Assert.Single(tasks, static task => task.IsCompletedSuccessfully);
        Task<RollingSummaryArtifactLink> failedTask = Assert.Single(tasks, static task => task.IsFaulted);
        Assert.IsType<InvalidOperationException>(failedTask.Exception!.InnerException);
        var store = DerivedRecapStore.Open(repoPath);
        Assert.Single(Directory.EnumerateFiles(store.ArtifactsDirectory, "*.json"));
    }

    [Fact]
    public void ArtifactWriter_DifferentRepositorySourceIsRejectedAtCompositionBoundary() {
        string sourceRepoPath = CreateSessionJournalWithTwoTurns();
        string writerRepoPath = CreateSessionJournalWithTwoTurns();
        var client = new ScriptedCompletionClient("summary");
        var writer = SessionJournalDerivedRecapWriter.Open(
            writerRepoPath,
            CreateTestProfile(),
            client,
            CreateTestConnection()
        );

        var ex = Assert.Throws<ArgumentException>(() => CreateRunner(
            SessionJournalRollingSummaryReplaySource.Open(sourceRepoPath),
            client,
            artifactWriter: writer
        ));

        Assert.Contains("same SessionJournal repository", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public void ArtifactWriter_LegacySourceIsRejectedAtCompositionBoundary() {
        var client = new ScriptedCompletionClient("summary");

        var ex = Assert.Throws<ArgumentException>(() => CreateRunner(
            new LegacyRollingSummaryReplaySource(CreateLegacyEventSource()),
            client,
            artifactWriter: new ThrowingArtifactWriter()
        ));

        Assert.Contains("requires source kind", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public void ReplayMessage_RejectsPartialRawRange() {
        Assert.Throws<ArgumentException>(() => new RollingSummaryReplayMessage(
            new ObservationMessage("hello"),
            sourceStartInclusive: Address(1),
            sourceEndInclusive: null
        ));
    }

    [Fact]
    public async Task Runner_MixedFragmentProvenanceFailsBeforeMaintainerCall() {
        var client = new ScriptedCompletionClient("summary");
        var source = new StaticReplaySource(
            "custom",
            [
                new RollingSummaryReplayStep(
                    new RollingSummaryReplaySourceCursor("custom", "trigger", SourceRawHead: Address(9)),
                    [
                        new RollingSummaryReplayMessage(new ObservationMessage("hello 1"), Address(1), Address(1)),
                        new RollingSummaryReplayMessage(new ActionMessage([new ActionBlock.Text("answer 1")])),
                        new RollingSummaryReplayMessage(new ObservationMessage("hello 2"), Address(2), Address(2)),
                        new RollingSummaryReplayMessage(new ActionMessage([new ActionBlock.Text("answer 2")]), Address(3), Address(3))
                    ],
                    IsTriggerBoundary: true
                )
            ]
        );
        var runner = CreateRunner(source, client);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => RunAllAsync(runner));

        Assert.Contains("cannot mix addressed and unaddressed", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task Runner_AddressedFragmentWithoutRawHeadFailsBeforeMaintainerCall() {
        var client = new ScriptedCompletionClient("summary");
        var source = new StaticReplaySource(
            "custom",
            [
                new RollingSummaryReplayStep(
                    new RollingSummaryReplaySourceCursor("custom", "trigger"),
                    [
                        new RollingSummaryReplayMessage(new ObservationMessage("hello 1"), Address(1), Address(1)),
                        new RollingSummaryReplayMessage(new ActionMessage([new ActionBlock.Text("answer 1")]), Address(2), Address(2)),
                        new RollingSummaryReplayMessage(new ObservationMessage("hello 2"), Address(3), Address(3)),
                        new RollingSummaryReplayMessage(new ActionMessage([new ActionBlock.Text("answer 2")]), Address(4), Address(4))
                    ],
                    IsTriggerBoundary: true
                )
            ]
        );
        var runner = CreateRunner(source, client);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => RunAllAsync(runner));

        Assert.Contains("must match", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task Runner_RawHeadDriftFailsBeforeProcessingSecondStep() {
        var source = new StaticReplaySource(
            "custom",
            [
                new RollingSummaryReplayStep(
                    new RollingSummaryReplaySourceCursor("custom", "step-1", SourceRawHead: Address(1)),
                    [],
                    IsTriggerBoundary: false
                ),
                new RollingSummaryReplayStep(
                    new RollingSummaryReplaySourceCursor("custom", "step-2", SourceRawHead: Address(2)),
                    [],
                    IsTriggerBoundary: false
                )
            ]
        );
        var runner = CreateRunner(source);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => RunAllAsync(runner));

        Assert.Contains("raw head changed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runner_SourceKindMismatchFailsBeforeProcessingStep() {
        var source = new StaticReplaySource(
            "custom",
            [
                new RollingSummaryReplayStep(
                    new RollingSummaryReplaySourceCursor("other", "step-1"),
                    [],
                    IsTriggerBoundary: false
                )
            ]
        );
        var runner = CreateRunner(source);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => RunAllAsync(runner));

        Assert.Contains("does not match step source kind", ex.Message, StringComparison.Ordinal);
    }

    private RollingSummaryReplayRunner CreateRunner(
        IRollingSummaryReplaySource source,
        ICompletionClient? client = null,
        int maxEpochs = 1,
        string? artifactRepoPath = null,
        IRollingSummaryArtifactWriter? artifactWriter = null
    ) {
        client ??= new ScriptedCompletionClient("summary");
        CompletionConnectionConfig connection = CreateTestConnection();
        ReplayMemoryMaintainerProfile profile = CreateTestProfile();
        if (artifactRepoPath is not null) {
            if (artifactWriter is not null) {
                throw new ArgumentException("Specify either an artifact repo path or an artifact writer, not both.");
            }
            artifactWriter = SessionJournalDerivedRecapWriter.Open(
                artifactRepoPath,
                profile,
                client,
                connection
            );
        }

        return new RollingSummaryReplayRunner(
            source,
            client,
            connection,
            profile,
            Path.Combine(NewTempPath(), "calls"),
            thresholdTokens: 1,
            maxEpochs,
            artifactWriter
        );
    }

    private static CompletionConnectionConfig CreateTestConnection()
        => new(
            Id: "test",
            Kind: "scripted",
            ModelId: "model-a",
            CompletionSurfaceId: "surface",
            BaseAddress: "http://localhost"
        );

    private static ReplayMemoryMaintainerProfile CreateTestProfile()
        => new(
            "test",
            new SJ.MemoryRewriteProfile(
                "rolling-summary",
                new SJ.MemoryPackBlockPath(SJ.MemoryPackCarrier.Observation, "session.rolling-summary"),
                "system prompt",
                "user prompt"
            )
        );

    private static RollingSummaryArtifactCandidate CreateArtifactCandidate(
        ReplayMemoryMaintainerProfile profile,
        EventAddress sourceEndInclusive,
        string summary
    ) {
        var memoryPack = new SJ.MemoryPack();
        memoryPack.Observation.Add(profile.Target.BlockKey, new SJ.MemoryPackBlock(summary));
        return new RollingSummaryArtifactCandidate(
            sourceEndInclusive,
            memoryPack,
            new SJ.MemoryBlockMaintenanceResult(
                profile.MaintainerId,
                profile.Target,
                new SJ.MemoryPackBlock(summary),
                new CompletionDescriptor("scripted", "openai-chat-v1", "model-a")
            ),
            []
        );
    }

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

    private static EventAddress Address(uint segmentNumber)
        => new(Ticket: default, SegmentNumber: segmentNumber, Hint: default);

    private string CreateSessionJournalWithTwoTurns() {
        return CreateSessionJournalWithTurns(2);
    }

    private string CreateSessionJournalWithTurns(int turnCount) {
        string repoPath = NewTempPath();
        using var engine = SJ.SessionJournalEngine.Create(repoPath, new SJ.SessionCreateOptions("model-a", "system", "surface"));
        for (int turn = 1; turn <= turnCount; turn++) {
            engine.AppendObservation($"hello {turn}");
            engine.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text($"answer {turn}")]),
                new CompletionDescriptor("scripted", "openai-chat-v1", "model-a")
            );
        }

        return repoPath;
    }

    private string CreateSessionJournalWithGoverningPromptChange() {
        string repoPath = NewTempPath();
        using var engine = SJ.SessionJournalEngine.Create(
            repoPath,
            new SJ.SessionCreateOptions("model-a", "system-a", "surface")
        );
        engine.AppendObservation("hello 1");
        engine.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("answer 1")]),
            new CompletionDescriptor("scripted", "openai-chat-v1", "model-a")
        );
        engine.AppendSystemPromptSetup("system-b");
        engine.AppendObservation("hello 2");
        engine.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("answer 2")]),
            new CompletionDescriptor("scripted", "openai-chat-v1", "model-a")
        );
        return repoPath;
    }

    private static SessionHistoryReplaySnapshot ReadHistoryReplay(string repoPath) {
        using var engine = SJ.SessionJournalEngine.Open(repoPath);
        SJ.SessionHistoryReplay replay = engine.ReplayHistory();
        return new SessionHistoryReplaySnapshot(
            EventAddressTextCodec.Format(replay.SourceRawHead!.Value),
            replay.Messages.Select(static message => new AddressedMessageSnapshot(
                EventAddressTextCodec.Format(message.SourceStartInclusive),
                EventAddressTextCodec.Format(message.SourceEndInclusive)
            )).ToArray()
        );
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

    private sealed class ThrowingCompletionClient : ICompletionClient {
        public string Name => "throwing";

        public string ApiSpecId => "openai-chat-v1";

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = request;
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("scripted failure");
        }
    }

    private sealed class ThrowingArtifactWriter : IRollingSummaryArtifactWriter {
        public string RequiredSourceKind => RollingSummaryReplaySourceKinds.SessionJournal;

        public int WriteCount { get; private set; }

        public ValueTask PrepareAsync(EventAddress sourceRawHead, CancellationToken ct) {
            _ = sourceRawHead;
            ct.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask<RollingSummaryArtifactLink> WriteProducedAsync(
            RollingSummaryArtifactCandidate candidate,
            CancellationToken ct
        ) {
            _ = candidate;
            ct.ThrowIfCancellationRequested();
            WriteCount++;
            throw new RollingSummaryArtifactWriteException(
                "Scripted artifact write failure.",
                new IOException("disk unavailable")
            );
        }
    }

    private sealed class StaticReplaySource : IRollingSummaryReplaySource {
        private readonly IReadOnlyList<RollingSummaryReplayStep> _steps;

        public StaticReplaySource(string sourceKind, IReadOnlyList<RollingSummaryReplayStep> steps) {
            SourceKind = sourceKind;
            _steps = steps;
        }

        public string SourceKind { get; }

        public async IAsyncEnumerable<RollingSummaryReplayStep> ReadStepsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct
        ) {
            foreach (RollingSummaryReplayStep step in _steps) {
                ct.ThrowIfCancellationRequested();
                yield return step;
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    private sealed record SessionHistoryReplaySnapshot(
        string SourceRawHead,
        IReadOnlyList<AddressedMessageSnapshot> Messages
    );

    private sealed record AddressedMessageSnapshot(
        string SourceStartInclusive,
        string SourceEndInclusive
    );
}
