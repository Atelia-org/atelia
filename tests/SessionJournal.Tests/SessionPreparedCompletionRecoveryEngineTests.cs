using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.EventJournal;
using Atelia.SessionJournal.Derived;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionPreparedCompletionRecoveryEngineTests : IDisposable {
    private static readonly SessionCompletionTargetIdentity DefaultTarget = new(
        "recovery-connection",
        "test",
        "recovery-connection-v1",
        "recovery-adapter-v1"
    );

    private readonly List<string> _tempDirectories = [];

    public void Dispose() {
        foreach (string path in _tempDirectories) {
            try {
                if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); }
            }
            catch {
                // Best-effort cleanup.
            }
        }
    }

    [Fact]
    public async Task ResumeAsync_DefaultRefuse_ValidatesPreparedButDoesNotMutateOrCallProvider() {
        string path = NewJournalPath();
        var client = new ScriptedClient();
        EventAddress prepared = await CreateFullRawPreparedAsync(
            path,
            CreateRuntime(client)
        );
        using (var reopened = SessionJournalEngine.Open(path, CreateRuntime(client))) {
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => reopened.ResumeAsync(CancellationToken.None)
            );
            Assert.Contains("RefuseUncertain", error.Message, StringComparison.Ordinal);
        }
        Assert.Equal(prepared, ReadHead(path));
        Assert.Empty(ReadAddressesByKind(path, SessionEventKind.CompletionAttemptRestarted));
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task ResumeAsync_RestartSuccess_AppendsNewAttemptAndBindsActionToIt() {
        string path = NewJournalPath();
        var sourceClient = new ScriptedClient();
        EventAddress prepared = await CreateFullRawPreparedAsync(
            path,
            CreateRuntime(sourceClient)
        );
        var recoveryClient = new ScriptedClient();
        recoveryClient.Enqueue(request => Success(request, "recovered"));
        using (var reopened = SessionJournalEngine.Open(
            path,
            CreateRuntime(
                recoveryClient,
                recoveryPolicy: SessionPreparedCompletionRecoveryPolicy.RestartWithNewAttempt
            )
        )) {
            ResumeOutcome outcome = await reopened.ResumeAsync(CancellationToken.None);
            Assert.True(outcome.Advanced);
            Assert.Equal("recovered", outcome.Message?.GetFlattenedText());
        }
        EventAddress restarted = Assert.Single(
            ReadAddressesByKind(path, SessionEventKind.CompletionAttemptRestarted)
        );
        EventAddress action = Assert.Single(
            ReadAddressesByKind(path, SessionEventKind.AgentActionProduced)
        );
        CompletionRequestPreparedBody source = ReadBody<CompletionRequestPreparedBody>(
            path,
            prepared,
            SessionEventKind.CompletionRequestPrepared
        );
        CompletionAttemptRestartedBody restart = ReadBody<CompletionAttemptRestartedBody>(
            path,
            restarted,
            SessionEventKind.CompletionAttemptRestarted
        );
        Assert.Equal(prepared, ReadParent(path, restarted));
        Assert.Equal(prepared, restart.SourcePreparedAddress);
        Assert.Equal(source.Attempt.AttemptId, restart.ReplacesAttemptId);
        Assert.NotEqual(restart.ReplacesAttemptId, restart.AttemptId);
        Assert.Equal(restarted, ReadParent(path, action));
        Assert.Equal(1, recoveryClient.Calls);
    }

    [Fact]
    public async Task ResumeAsync_RestartUsesPreparedRequestInsteadOfCurrentRuntimeParameters() {
        string path = NewJournalPath();
        var sourceClient = new ScriptedClient();
        _ = await CreateFullRawPreparedAsync(
            path,
            CreateRuntime(sourceClient, maxTokens: 111)
        );
        var recoveryClient = new ScriptedClient();
        recoveryClient.Enqueue(request => Success(request, "reconstructed"));
        using (var reopened = SessionJournalEngine.Open(
            path,
            CreateRuntime(
                recoveryClient,
                recoveryPolicy: SessionPreparedCompletionRecoveryPolicy.RestartWithNewAttempt,
                maxTokens: 999
            )
        )) {
            _ = await reopened.ResumeAsync(CancellationToken.None);
        }

        CompletionRequest request = Assert.Single(recoveryClient.Requests);
        Assert.Equal(111, request.MaxTokens);
    }

    [Fact]
    public async Task ResumeAsync_RestartKnownFailure_BindsFailureToRestartAttempt() {
        string path = NewJournalPath();
        var sourceClient = new ScriptedClient();
        _ = await CreateFullRawPreparedAsync(path, CreateRuntime(sourceClient));
        var recoveryClient = new ScriptedClient();
        recoveryClient.Enqueue(request => new CompletionResult(
            new ActionMessage([new ActionBlock.Text("unused")]),
            Descriptor(request),
            termination: CompletionTermination.Failed("provider-failed", "known")
        ));
        using (var reopened = SessionJournalEngine.Open(
            path,
            CreateRuntime(
                recoveryClient,
                recoveryPolicy: SessionPreparedCompletionRecoveryPolicy.RestartWithNewAttempt
            )
        )) {
            await Assert.ThrowsAsync<SessionJournalTurnAbortedException>(
                () => reopened.ResumeAsync(CancellationToken.None)
            );
            Assert.Equal(SessionExecutionPhase.TurnFailed, reopened.Project().ExecutionState.Phase);
        }

        EventAddress restarted = Assert.Single(
            ReadAddressesByKind(path, SessionEventKind.CompletionAttemptRestarted)
        );
        EventAddress failureAddress = Assert.Single(
            ReadAddressesByKind(path, SessionEventKind.CompletionAttemptFailed)
        );
        CompletionAttemptRestartedBody restart = ReadBody<CompletionAttemptRestartedBody>(
            path,
            restarted,
            SessionEventKind.CompletionAttemptRestarted
        );
        CompletionAttemptFailedBody failure = ReadBody<CompletionAttemptFailedBody>(
            path,
            failureAddress,
            SessionEventKind.CompletionAttemptFailed
        );
        Assert.Equal(restarted, ReadParent(path, failureAddress));
        Assert.Equal(restart.AttemptId, failure.AttemptId);
    }

    [Fact]
    public async Task ResumeAsync_RestartFailpointThenReopen_AppendsSecondAuditableRestart() {
        string path = NewJournalPath();
        var sourceClient = new ScriptedClient();
        EventAddress prepared = await CreateFullRawPreparedAsync(
            path,
            CreateRuntime(sourceClient)
        );
        var recoveryClient = new ScriptedClient();
        using (var firstRecovery = SessionJournalEngine.OpenForTest(
            path,
            CreateRuntime(
                recoveryClient,
                recoveryPolicy: SessionPreparedCompletionRecoveryPolicy.RestartWithNewAttempt
            ),
            new SessionJournalTestHooks(
                SessionJournalFailpoint.AfterCompletionAttemptRestartedCommitted
            )
        )) {
            SessionJournalFailpointException error =
                await Assert.ThrowsAsync<SessionJournalFailpointException>(
                    () => firstRecovery.ResumeAsync(CancellationToken.None)
                );
            Assert.Equal(
                SessionJournalFailpoint.AfterCompletionAttemptRestartedCommitted,
                error.Failpoint
            );
        }
        Assert.Equal(0, recoveryClient.Calls);

        recoveryClient.Enqueue(request => Success(request, "second restart"));
        using (var secondRecovery = SessionJournalEngine.Open(
            path,
            CreateRuntime(
                recoveryClient,
                recoveryPolicy: SessionPreparedCompletionRecoveryPolicy.RestartWithNewAttempt
            )
        )) {
            ResumeOutcome outcome = await secondRecovery.ResumeAsync(CancellationToken.None);
            Assert.True(outcome.Advanced);
        }

        EventAddress[] restarts =
            ReadAddressesByKind(path, SessionEventKind.CompletionAttemptRestarted);
        Assert.Equal(2, restarts.Length);
        CompletionAttemptRestartedBody restart1 = ReadBody<CompletionAttemptRestartedBody>(
            path,
            restarts[0],
            SessionEventKind.CompletionAttemptRestarted
        );
        CompletionAttemptRestartedBody restart2 = ReadBody<CompletionAttemptRestartedBody>(
            path,
            restarts[1],
            SessionEventKind.CompletionAttemptRestarted
        );
        Assert.Equal(prepared, restart1.SourcePreparedAddress);
        Assert.Equal(prepared, restart2.SourcePreparedAddress);
        Assert.Equal(restarts[0], ReadParent(path, restarts[1]));
        Assert.Equal(restart1.AttemptId, restart2.ReplacesAttemptId);
    }

    [Fact]
    public async Task SendFailpoint_AfterProviderBeforeAction_CanRestartWithNewAttempt() {
        string path = NewJournalPath();
        var client = new ScriptedClient();
        client.Enqueue(request => Success(request, "uncertain first result"));
        using (var engine = SessionJournalEngine.CreateForTest(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            CreateRuntime(client),
            new SessionJournalTestHooks(
                SessionJournalFailpoint.AfterCompletionBeforeActionCommitted
            )
        )) {
            await Assert.ThrowsAsync<SessionJournalFailpointException>(
                () => engine.SendAsync("hello", CancellationToken.None)
            );
        }
        Assert.Equal(1, client.Calls);

        client.Enqueue(request => Success(request, "restarted result"));
        using (var reopened = SessionJournalEngine.Open(
            path,
            CreateRuntime(
                client,
                recoveryPolicy: SessionPreparedCompletionRecoveryPolicy.RestartWithNewAttempt
            )
        )) {
            ResumeOutcome outcome = await reopened.ResumeAsync(CancellationToken.None);
            Assert.Equal("restarted result", outcome.Message?.GetFlattenedText());
        }
        Assert.Equal(2, client.Calls);
        Assert.Single(ReadAddressesByKind(path, SessionEventKind.CompletionAttemptRestarted));
    }

    [Theory]
    [InlineData("target")]
    [InlineData("client")]
    [InlineData("tools")]
    public async Task ResumeAsync_RuntimeDispatchMismatch_HasZeroMutationAndZeroProviderCall(
        string mismatch
    ) {
        string path = NewJournalPath();
        var sourceClient = new ScriptedClient();
        EventAddress prepared = await CreateFullRawPreparedAsync(
            path,
            CreateRuntime(sourceClient)
        );
        var recoveryClient = mismatch == "client"
            ? new ScriptedClient("other-client", "other-api")
            : new ScriptedClient();
        SessionCompletionTargetIdentity target = mismatch == "target"
            ? DefaultTarget with { ConnectionFingerprint = "different" }
            : DefaultTarget;
        ToolSession? tools = mismatch == "tools"
            ? new ToolRegistry([new RecordingTool("unexpected")]).CreateSession()
            : null;
        using (var reopened = SessionJournalEngine.Open(
            path,
            CreateRuntime(
                recoveryClient,
                tools,
                target,
                SessionPreparedCompletionRecoveryPolicy.RestartWithNewAttempt
            )
        )) {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => reopened.ResumeAsync(CancellationToken.None)
            );
        }

        Assert.Equal(prepared, ReadHead(path));
        Assert.Empty(ReadAddressesByKind(path, SessionEventKind.CompletionAttemptRestarted));
        Assert.Equal(0, recoveryClient.Calls);
    }

    [Fact]
    public async Task ResumeAsync_TailPreparedAfterArtifactDeletion_ReconstructsInlineWithoutProject() {
        string path = NewJournalPath();
        DerivedRecapArtifact artifact;
        using (var setup = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
            setup.AppendObservation("old");
            EventAddress anchor = setup.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("old answer")]),
                new CompletionDescriptor("import", "import-v1", "model-A")
            );
            artifact = await WriteArtifactAsync(
                path,
                anchor,
                setup.ResolveGoverningSetup(anchor)
            );
        }
        var sourceClient = new ScriptedClient();
        using (var source = SessionJournalEngine.OpenForTest(
            path,
            CreateRuntime(
                sourceClient,
                tailProjection: new SessionTailProjectionOptions(artifact.ArtifactId)
            ),
            new SessionJournalTestHooks(SessionJournalFailpoint.AfterRequestPreparedCommitted)
        )) {
            await Assert.ThrowsAsync<SessionJournalFailpointException>(
                () => source.SendAsync("tail observation", CancellationToken.None)
            );
        }
        string artifactPath = Path.Combine(
            DerivedRecapStore.Open(path).ArtifactsDirectory,
            $"{artifact.ArtifactId}.json"
        );
        File.Delete(artifactPath);
        Assert.False(File.Exists(artifactPath));

        var recoveryClient = new ScriptedClient();
        recoveryClient.Enqueue(request => Success(request, "inline recovery"));
        using var reopened = SessionJournalEngine.Open(
            path,
            CreateRuntime(
                recoveryClient,
                recoveryPolicy: SessionPreparedCompletionRecoveryPolicy.RestartWithNewAttempt,
                tailProjection: new SessionTailProjectionOptions(artifact.ArtifactId)
            )
        );
        int projectionCountBeforeResume = reopened.FullProjectionInvocationCount;

        ResumeOutcome outcome = await reopened.ResumeAsync(CancellationToken.None);

        Assert.Equal("inline recovery", outcome.Message?.GetFlattenedText());
        Assert.Equal(projectionCountBeforeResume, reopened.FullProjectionInvocationCount);
        Assert.Single(recoveryClient.Requests);
    }

    [Fact]
    public async Task ResumeAsync_FullRawToolCall_ContinuesExistingToolLoop() {
        string path = NewJournalPath();
        var tool = new RecordingTool("lookup");
        ToolSession sourceTools = new ToolRegistry([tool]).CreateSession();
        var client = new ScriptedClient();
        _ = await CreateFullRawPreparedAsync(
            path,
            CreateRuntime(client, sourceTools)
        );
        ToolSession recoveryTools = new ToolRegistry([tool]).CreateSession();
        client.Enqueue(request => new CompletionResult(
            new ActionMessage([
                new ActionBlock.ToolCall(new RawToolCall("lookup", "call-1", "{}"))
            ]),
            Descriptor(request)
        ));
        client.Enqueue(request => Success(request, "tool loop done"));
        using (var reopened = SessionJournalEngine.Open(
            path,
            CreateRuntime(
                client,
                recoveryTools,
                recoveryPolicy: SessionPreparedCompletionRecoveryPolicy.RestartWithNewAttempt
            )
        )) {
            ResumeOutcome outcome = await reopened.ResumeAsync(CancellationToken.None);
            Assert.Equal("tool loop done", outcome.Message?.GetFlattenedText());
        }
        Assert.Equal(1, tool.Calls);
        Assert.Equal(2, client.Calls);
        Assert.Single(ReadAddressesByKind(path, SessionEventKind.CompletionAttemptRestarted));
        Assert.Single(ReadAddressesByKind(path, SessionEventKind.ToolExecutionStarted));
        Assert.Single(ReadAddressesByKind(path, SessionEventKind.ToolResultObserved));
    }

    private async Task<EventAddress> CreateFullRawPreparedAsync(
        string path,
        SessionRuntime runtime
    ) {
        using var engine = SessionJournalEngine.CreateForTest(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            runtime,
            new SessionJournalTestHooks(SessionJournalFailpoint.AfterRequestPreparedCommitted)
        );
        await Assert.ThrowsAsync<SessionJournalFailpointException>(
            () => engine.SendAsync("prepared observation", CancellationToken.None)
        );
        return engine.Project().Head!.Value;
    }

    private static SessionRuntime CreateRuntime(
        ScriptedClient client,
        ToolSession? tools = null,
        SessionCompletionTargetIdentity? target = null,
        SessionPreparedCompletionRecoveryPolicy recoveryPolicy =
            SessionPreparedCompletionRecoveryPolicy.RefuseUncertain,
        SessionTailProjectionOptions? tailProjection = null,
        int? maxTokens = 256
    ) => new(
        CompletionClient: client,
        ToolSession: tools,
        CompletionTarget: target ?? DefaultTarget,
        MaxTokens: maxTokens,
        TailProjection: tailProjection,
        PreparedCompletionRecoveryPolicy: recoveryPolicy
    );

    private static CompletionResult Success(CompletionRequest request, string text)
        => new(
            new ActionMessage([new ActionBlock.Text(text)]),
            Descriptor(request)
        );

    private static CompletionDescriptor Descriptor(CompletionRequest request)
        => new("recovery-client", "recovery-api-v1", request.ModelId);

    private static async ValueTask<DerivedRecapArtifact> WriteArtifactAsync(
        string path,
        EventAddress anchor,
        SessionGoverningSetup setup
    ) {
        var memoryPack = new MemoryPack();
        memoryPack.System.Add("policy", new MemoryPackBlock("memory system"));
        memoryPack.Observation.Add("summary", new MemoryPackBlock("memory observation"));
        memoryPack.Action.Add("self", new MemoryPackBlock("memory action"));
        return await DerivedRecapStore.Open(path).WriteProducedAsync(new DerivedRecapWriteRequest(
            ArtifactKind: DerivedRecapArtifactKinds.RollingSummary,
            ProfileId: "recovery-tests",
            Producer: "tests",
            ProducerFingerprint: "recovery-tests-v1",
            SourceRawHead: anchor,
            SourceStartExclusive: null,
            SourceEndInclusive: anchor,
            AnchorRawEvent: anchor,
            GoverningRuntimeConfigSetup: setup.RuntimeConfigSetupAddress,
            GoverningSystemPromptSetup: setup.SystemPromptSetupAddress,
            PreviousArtifact: null,
            Target: new MemoryPackBlockPath(MemoryPackCarrier.Observation, "summary"),
            MemoryPack: memoryPack
        ));
    }

    private static EventAddress ReadHead(string path) {
        using var journal = EventJournal.EventJournal.OpenExisting(path);
        RefId main = journal.OpenBranch(SessionJournalDefaults.MainBranchName).Unwrap();
        return journal.GetHead(main)!.Value;
    }

    private static EventAddress[] ReadAddressesByKind(string path, SessionEventKind kind) {
        using var journal = EventJournal.EventJournal.OpenExisting(path);
        RefId main = journal.OpenBranch(SessionJournalDefaults.MainBranchName).Unwrap();
        EventAddress head = journal.GetHead(main)!.Value;
        IReadOnlyList<EventAddress> chain =
            journal.ReadChronologicalChain(head, checkedRead: true).Unwrap();
        return chain.Where(address =>
            journal.ReadEventHeaderPreview(address).Unwrap().OpaqueEventKind == (uint)kind
        ).ToArray();
    }

    private static EventAddress? ReadParent(string path, EventAddress address) {
        using var journal = EventJournal.EventJournal.OpenExisting(path);
        return journal.ReadEventHeaderPreview(address).Unwrap().Parent;
    }

    private static T ReadBody<T>(
        string path,
        EventAddress address,
        SessionEventKind kind
    ) where T : class {
        using var engine = SessionJournalEngine.Open(path);
        return Assert.IsType<T>(
            SessionEventCodec.Decode(kind, engine.ReadPayloadBytes(address), out _)
        );
    }

    private string NewJournalPath() {
        string path = Path.Combine(
            Path.GetTempPath(),
            "atelia-session-recovery-tests",
            Guid.NewGuid().ToString("N")
        );
        _tempDirectories.Add(path);
        return path;
    }

    private sealed class ScriptedClient(
        string name = "recovery-client",
        string apiSpecId = "recovery-api-v1"
    ) : ICompletionClient {
        private readonly Queue<Func<CompletionRequest, CompletionResult>> _responses = [];

        public string Name { get; } = name;

        public string ApiSpecId { get; } = apiSpecId;

        public int Calls { get; private set; }

        public List<CompletionRequest> Requests { get; } = [];

        public void Enqueue(Func<CompletionRequest, CompletionResult> response)
            => _responses.Enqueue(response);

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Requests.Add(request);
            if (_responses.Count == 0) {
                throw new InvalidOperationException("No scripted response.");
            }
            return Task.FromResult(_responses.Dequeue()(request));
        }
    }

    private sealed class RecordingTool(string name) : ITool {
        public ToolDefinition Definition { get; } =
            new(name, $"Tool {name}.", new ToolSchema.Object());

        public int Calls { get; private set; }

        public ValueTask<ToolExecuteResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken
        ) {
            _ = context;
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult(
                ToolExecuteResult.FromText(ToolExecutionStatus.Success, "tool result")
            );
        }
    }
}
