using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.EventJournal;
using Atelia.SessionJournal.Derived;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionJournalRequestContextPerformanceTests : IDisposable {
    private readonly List<string> _paths = [];

    [Fact]
    public void EventReader_TracksSuccessfulLogicalPayloadLifetimeAndFailedReads() {
        string path = NewPath();
        EventAddress head;
        using (var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
            head = engine.AppendObservation("payload");
        }

        using var journal = EventJournal.EventJournal.OpenExisting(path);
        var reader = new SessionJournalEventReader(journal);

        SessionJournalEventFrame frame = reader.ReadEvent(head).Unwrap();
        long logicalPayloadBytes = frame.Header.PayloadLength;
        Assert.True(logicalPayloadBytes > 0);
        Assert.Equal(
            new SessionJournalReadDiagnostics(
                HeaderPreviewReadCount: 0,
                PayloadReadCount: 1,
                LogicalPayloadByteCount: logicalPayloadBytes,
                ChronologicalChainReadCount: 0,
                ChronologicalEventCount: 0,
                FullProjectionInvocationCount: 0
            ),
            reader.CaptureDiagnostics()
        );
        Assert.Equal(
            new SessionJournalPayloadLifetimeDiagnostics(
                CurrentLiveLogicalPayloadBytes: logicalPayloadBytes,
                PeakLiveLogicalPayloadBytes: logicalPayloadBytes
            ),
            reader.CapturePayloadLifetimeDiagnostics()
        );

        frame.Dispose();
        frame.Dispose();
        Assert.Equal(
            new SessionJournalPayloadLifetimeDiagnostics(
                CurrentLiveLogicalPayloadBytes: 0,
                PeakLiveLogicalPayloadBytes: logicalPayloadBytes
            ),
            reader.CapturePayloadLifetimeDiagnostics()
        );

        EventAddress missing = head with { SegmentNumber = uint.MaxValue };
        Assert.True(reader.ReadEvent(missing).IsFailure);
        Assert.Equal(2, reader.CaptureDiagnostics().PayloadReadCount);
        Assert.Equal(
            logicalPayloadBytes,
            reader.CaptureDiagnostics().LogicalPayloadByteCount
        );
        Assert.Equal(
            new SessionJournalPayloadLifetimeDiagnostics(
                CurrentLiveLogicalPayloadBytes: 0,
                PeakLiveLogicalPayloadBytes: logicalPayloadBytes
            ),
            reader.CapturePayloadLifetimeDiagnostics()
        );
    }

    [Fact]
    public async Task ArtifactTailRequests_AvoidColdPayloadReplayAcrossTenThousandColdTurns() {
        string shortPath = await CreateActivatedColdJournalAsync(turnCount: 1);
        string longPath = await CreateActivatedColdJournalAsync(turnCount: 10001);

        RequestCost shortObservation =
            await CompleteObservationAsync(shortPath);
        RequestCost longObservation =
            await CompleteObservationAsync(longPath);
        AssertNoColdPayloadReplay(shortObservation, longObservation);

        RequestCost shortToolContinuation =
            await CompleteTwoToolContinuationsAsync(shortPath);
        RequestCost longToolContinuation =
            await CompleteTwoToolContinuationsAsync(longPath);
        AssertNoColdPayloadReplay(shortToolContinuation, longToolContinuation);
    }

    public void Dispose() {
        foreach (string path in _paths) {
            try {
                if (Directory.Exists(path)) {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch {
                // Best-effort cleanup for performance fixtures.
            }
        }
    }

    private async ValueTask<string> CreateActivatedColdJournalAsync(
        int turnCount
    ) {
        string path = NewPath();
        using (SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
        }

        EventAddress anchor;
        using (var journal = EventJournal.EventJournal.OpenExisting(path)) {
            RefId main = journal.OpenBranch(
                SessionJournalDefaults.MainBranchName
            ).Unwrap();
            EventAddress head = journal.GetHead(main)
                ?? throw new InvalidDataException(
                    "Created SessionJournal has no head."
                );
            for (int i = 0; i < turnCount; i++) {
                EventAddress observation = Commit(
                    journal,
                    head,
                    SessionEventKind.ObservationAccepted,
                    new ObservationAcceptedBody("cold-observation")
                );
                head = Commit(
                    journal,
                    observation,
                    SessionEventKind.ImportedAgentAction,
                    new AgentActionProducedBody(
                        new ActionMessage([
                            new ActionBlock.Text("cold-action")
                        ]),
                        new CompletionDescriptor(
                            "import",
                            "import-v1",
                            "model-A"
                        ),
                        Correlation(observation),
                        new SessionExecutionCheckpoint(0),
                        ToolRuntimeIdentity: null
                    )
                );
            }
            anchor = head;
        }

        using var activating = SessionJournalEngine.Open(path);
        SessionGoverningSetup setup =
            activating.ResolveGoverningSetup(anchor);
        TestArtifactSet artifacts = await WriteArtifactSetAsync(
            path,
            anchor,
            setup
        );
        await activating.CommitArtifactSetAsync([
            new SessionArtifactSetMemberSelection(
                "world-understanding",
                artifacts.WorldUnderstanding.ArtifactId
            ),
            new SessionArtifactSetMemberSelection(
                "autobiography",
                artifacts.Autobiography.ArtifactId
            )
        ]);
        return path;
    }

    private static async ValueTask<RequestCost> CompleteObservationAsync(
        string path
    ) {
        SessionJournalEngine? observedEngine = null;
        SessionJournalReadDiagnostics previousProviderReads = default;
        var providerReadDeltas =
            new List<SessionJournalReadDiagnostics>();
        var client = new CapturingCompletionClient(request =>
            new CompletionResult(
                new ActionMessage([
                    new ActionBlock.Text("observation-complete")
                ]),
                Invocation(request)
            ),
            _ => CaptureProviderReadDelta(
                observedEngine,
                ref previousProviderReads,
                providerReadDeltas
            )
        );
        using var engine = SessionJournalEngine.Open(
            path,
            CreateRuntime(client)
        );
        observedEngine = engine;
        SessionJournalReadDiagnostics before =
            engine.CaptureReadDiagnostics();
        previousProviderReads = before;

        TurnResult result = await engine.SendAsync(
            "measured-observation",
            CancellationToken.None
        );

        Assert.Equal(
            "observation-complete",
            result.Message.GetFlattenedText()
        );
        Assert.Single(client.Requests);
        return CaptureCost(engine, before, providerReadDeltas);
    }

    private static async ValueTask<RequestCost>
        CompleteTwoToolContinuationsAsync(string path) {
        int responseIndex = 0;
        SessionJournalEngine? observedEngine = null;
        SessionJournalReadDiagnostics previousProviderReads = default;
        var providerReadDeltas =
            new List<SessionJournalReadDiagnostics>();
        var client = new CapturingCompletionClient(request =>
            responseIndex++ switch {
                0 => new CompletionResult(
                    new ActionMessage([
                        new ActionBlock.ToolCall(
                            new RawToolCall("noop", "call-1", "{}")
                        )
                    ]),
                    Invocation(request)
                ),
                1 => new CompletionResult(
                    new ActionMessage([
                        new ActionBlock.ToolCall(
                            new RawToolCall("noop", "call-2", "{}")
                        )
                    ]),
                    Invocation(request)
                ),
                _ => new CompletionResult(
                    new ActionMessage([
                        new ActionBlock.Text("tool-complete")
                    ]),
                    Invocation(request)
                )
            },
            _ => CaptureProviderReadDelta(
                observedEngine,
                ref previousProviderReads,
                providerReadDeltas
            )
        );
        var tool = new NoopTool();
        ToolSession tools =
            new ToolRegistry([tool]).CreateSession();
        using var engine = SessionJournalEngine.Open(
            path,
            CreateRuntime(client, tools)
        );
        observedEngine = engine;
        SessionJournalReadDiagnostics before =
            engine.CaptureReadDiagnostics();
        previousProviderReads = before;

        TurnResult result = await engine.SendAsync(
            "measured-tools",
            CancellationToken.None
        );

        Assert.Equal("tool-complete", result.Message.GetFlattenedText());
        Assert.Equal(3, client.Requests.Count);
        Assert.All(client.Requests, request =>
            Assert.Single(request.Tools)
        );
        Assert.Equal(2, tool.OperationIds.Count);
        Assert.All(tool.OperationIds, operationId =>
            Assert.StartsWith(
                "atelia.session-journal.tool.v1:ej1:",
                operationId,
                StringComparison.Ordinal
            )
        );
        return CaptureCost(engine, before, providerReadDeltas);
    }

    private static RequestCost CaptureCost(
        SessionJournalEngine engine,
        SessionJournalReadDiagnostics before,
        IReadOnlyList<SessionJournalReadDiagnostics> providerReadDeltas
    ) {
        SessionJournalReadDiagnostics reads =
            engine.CaptureReadDiagnostics() - before;
        SessionJournalPayloadLifetimeDiagnostics lifetime =
            engine.CapturePayloadLifetimeDiagnostics();
        Assert.True(reads.HeaderPreviewReadCount > 0);
        Assert.True(reads.PayloadReadCount > 0);
        Assert.True(reads.LogicalPayloadByteCount > 0);
        Assert.True(lifetime.PeakLiveLogicalPayloadBytes > 0);
        Assert.Equal(0, lifetime.CurrentLiveLogicalPayloadBytes);
        Assert.Equal(0, reads.ChronologicalChainReadCount);
        Assert.Equal(0, reads.ChronologicalEventCount);
        Assert.Equal(0, reads.FullProjectionInvocationCount);
        Assert.NotEmpty(providerReadDeltas);
        Assert.All(providerReadDeltas, static providerReads => {
            Assert.True(providerReads.HeaderPreviewReadCount > 0);
            Assert.True(providerReads.PayloadReadCount > 0);
            Assert.True(providerReads.LogicalPayloadByteCount > 0);
            Assert.Equal(0, providerReads.ChronologicalChainReadCount);
            Assert.Equal(0, providerReads.ChronologicalEventCount);
            Assert.Equal(0, providerReads.FullProjectionInvocationCount);
        });
        return new RequestCost(
            reads,
            lifetime,
            providerReadDeltas.ToArray()
        );
    }

    private static void AssertNoColdPayloadReplay(
        RequestCost shortPrefix,
        RequestCost longPrefix
    ) {
        Assert.True(longPrefix.Reads.HeaderPreviewReadCount > shortPrefix.Reads.HeaderPreviewReadCount);
        Assert.Equal(
            shortPrefix.Reads.PayloadReadCount,
            longPrefix.Reads.PayloadReadCount
        );
        Assert.Equal(
            shortPrefix.Reads.LogicalPayloadByteCount,
            longPrefix.Reads.LogicalPayloadByteCount
        );
        Assert.Equal(
            shortPrefix.Lifetime.PeakLiveLogicalPayloadBytes,
            longPrefix.Lifetime.PeakLiveLogicalPayloadBytes
        );
        Assert.Equal(shortPrefix.ProviderReadDeltas.Count, longPrefix.ProviderReadDeltas.Count);
        for (int i = 0; i < shortPrefix.ProviderReadDeltas.Count; i++) {
            SessionJournalReadDiagnostics shortReads = shortPrefix.ProviderReadDeltas[i];
            SessionJournalReadDiagnostics longReads = longPrefix.ProviderReadDeltas[i];
            Assert.True(longReads.HeaderPreviewReadCount >= shortReads.HeaderPreviewReadCount);
            Assert.Equal(shortReads.PayloadReadCount, longReads.PayloadReadCount);
            Assert.Equal(shortReads.LogicalPayloadByteCount, longReads.LogicalPayloadByteCount);
            Assert.Equal(0, longReads.FullProjectionInvocationCount);
        }
    }

    private static void CaptureProviderReadDelta(
        SessionJournalEngine? engine,
        ref SessionJournalReadDiagnostics previous,
        ICollection<SessionJournalReadDiagnostics> destination
    ) {
        SessionJournalReadDiagnostics current =
            (engine ?? throw new InvalidOperationException(
                "The measured engine must be assigned before provider invocation."
            )).CaptureReadDiagnostics();
        destination.Add(current - previous);
        previous = current;
    }

    private static async ValueTask<TestArtifactSet> WriteArtifactSetAsync(
        string path,
        EventAddress anchor,
        SessionGoverningSetup setup
    ) {
        var memoryPack = new MemoryPack();
        memoryPack.Observation.Add(
            "roleplay.world-understanding",
            new MemoryPackBlock("bounded world")
        );
        memoryPack.Action.Add(
            "roleplay.first-person-autobiography",
            new MemoryPackBlock("bounded self")
        );
        DerivedRecapStore store = DerivedRecapStore.Open(path);
        DerivedRecapArtifact world = await store.WriteProducedAsync(
            CreateArtifactWriteRequest(
                "world-understanding",
                "performance-world",
                new MemoryPackBlockPath(
                    MemoryPackCarrier.Observation,
                    "roleplay.world-understanding"
                ),
                anchor,
                setup,
                memoryPack
            )
        );
        DerivedRecapArtifact autobiography =
            await store.WriteProducedAsync(
                CreateArtifactWriteRequest(
                    "autobiography",
                    "performance-autobiography",
                    new MemoryPackBlockPath(
                        MemoryPackCarrier.Action,
                        "roleplay.first-person-autobiography"
                    ),
                    anchor,
                    setup,
                    memoryPack
                )
            );
        return new TestArtifactSet(world, autobiography);
    }

    private static DerivedRecapWriteRequest CreateArtifactWriteRequest(
        string artifactKind,
        string profileId,
        MemoryPackBlockPath target,
        EventAddress anchor,
        SessionGoverningSetup setup,
        MemoryPack memoryPack
    ) => new(
        ArtifactKind: artifactKind,
        ProfileId: profileId,
        Producer: "performance-tests",
        ProducerFingerprint: "performance-tests-v1",
        SourceRawHead: anchor,
        SourceStartExclusive: null,
        SourceEndInclusive: anchor,
        AnchorRawEvent: anchor,
        GoverningRuntimeConfigSetup:
            setup.RuntimeConfigSetupAddress,
        GoverningSystemPromptSetup:
            setup.SystemPromptSetupAddress,
        PreviousArtifact: null,
        Target: target,
        MemoryPack: memoryPack
    );

    private static SessionRuntime CreateRuntime(
        CapturingCompletionClient client,
        ToolSession? tools = null
    ) => new(
        CompletionClient: client,
        ToolSession: tools,
        CompletionTarget: new SessionCompletionTargetIdentity(
            "performance-connection",
            "test",
            "performance-connection-v1",
            "performance-adapter-v1"
        ),
        MaxTokens: 512,
        ToolRuntimeIdentity: tools is null
            ? null
            : new SessionToolRuntimeIdentity(
                "performance-tool-host",
                "performance-tools-v1",
                "performance-capabilities-v1"
            )
    );

    private static CompletionDescriptor Invocation(
        CompletionRequest request
    ) => new(
        "performance-client",
        "performance-api-v1",
        request.ModelId
    );

    private static string Correlation(EventAddress observation)
        => "atelia.session-journal.turn.v1:"
            + EventAddressTextCodec.Format(observation);

    private static EventAddress Commit(
        EventJournal.EventJournal journal,
        EventAddress expectedHead,
        SessionEventKind kind,
        object body
    ) => journal.CommitToRef(
        SessionJournalDefaults.MainBranchName,
        expectedHead,
        SessionEventCodec.Encode(kind, body),
        opaqueEventKind: (uint)kind,
        hint: default
    ).Unwrap().EventAddress;

    private string NewPath() {
        string tempRoot = Directory.Exists("/dev/shm")
            ? "/dev/shm"
            : Path.GetTempPath();
        string path = Path.Combine(
            tempRoot,
            "atelia-session-request-performance-tests",
            Guid.NewGuid().ToString("N")
        );
        _paths.Add(path);
        return path;
    }

    private sealed record RequestCost(
        SessionJournalReadDiagnostics Reads,
        SessionJournalPayloadLifetimeDiagnostics Lifetime,
        IReadOnlyList<SessionJournalReadDiagnostics> ProviderReadDeltas
    );

    private sealed record TestArtifactSet(
        DerivedRecapArtifact WorldUnderstanding,
        DerivedRecapArtifact Autobiography
    );

    private sealed class NoopTool : ITool {
        public ToolDefinition Definition { get; } =
            new("noop", "No-op tool.", new ToolSchema.Object());

        public List<string> OperationIds { get; } = [];

        public ValueTask<ToolExecuteResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            OperationIds.Add(
                context.OperationId
                    ?? throw new InvalidOperationException(
                        "Reserved tool execution requires an operation id."
                    )
            );
            return ValueTask.FromResult(
                ToolExecuteResult.FromText(
                    ToolExecutionStatus.Success,
                    "ok"
                )
            );
        }
    }

    private sealed class CapturingCompletionClient(
        Func<CompletionRequest, CompletionResult> response,
        Action<CompletionRequest>? onRequest = null
    ) : ICompletionClient {
        private readonly Func<CompletionRequest, CompletionResult> _response =
            response;
        private readonly Action<CompletionRequest>? _onRequest = onRequest;

        public string Name => "performance-client";

        public string ApiSpecId => "performance-api-v1";

        public List<CompletionRequest> Requests { get; } = [];

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            _onRequest?.Invoke(request);
            return Task.FromResult(_response(request));
        }
    }
}
