using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionJournalEngineTests : IDisposable {
    private readonly List<string> _tempDirectories = new();

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
    public void Create_WritesSessionCreatedAndProjectsConfigFromJournal() {
        string path = NewJournalPath();

        using var engine = SessionJournalEngine.Create(path, new SessionCreateOptions(
            ModelId: "model-A",
            SystemPrompt: "system-A",
            CompletionSurfaceId: "surface-A"
        ));

        SessionProjection projection = engine.Project();

        Assert.NotNull(projection.Head);
        string createdJson = System.Text.Encoding.UTF8.GetString(engine.ReadPayloadBytes(projection.Head.Value));
        Assert.Equal("{\"v\":1,\"body\":{\"modelId\":\"model-A\",\"systemPrompt\":\"system-A\",\"completionSurfaceId\":\"surface-A\",\"schema\":\"atelia.session-journal.trunk.v1\"}}", createdJson);
        Assert.NotNull(projection.Config);
        Assert.Equal("model-A", projection.Config.ModelId);
        Assert.Equal("system-A", projection.Config.SystemPrompt);
        Assert.Equal("surface-A", projection.Config.CompletionSurfaceId);
        Assert.Equal(SessionJournalDefaults.Schema, projection.Config.Schema);
        Assert.Empty(projection.Context);
        Assert.Equal(SessionExecutionPhase.Idle, projection.ExecutionState.Phase);
        Assert.Equal(SessionEventKind.SessionCreated, projection.ExecutionState.HeadKind);
    }

    [Fact]
    public void AppendObservationAndAction_ReopenRebuildsContextAndConfigFromJournal() {
        string path = NewJournalPath();
        var invocation = new CompletionDescriptor("fake-provider", "fake-api-v1", "model-A");
        var action = new ActionMessage(new ActionBlock[] {
            new ActionBlock.Text("answer"),
            new ActionBlock.Text(" continued")
        });

        using (var engine = SessionJournalEngine.Create(path, new SessionCreateOptions(
            ModelId: "model-A",
            SystemPrompt: "system-A",
            CompletionSurfaceId: "surface-A"
        ))) {
            engine.AppendObservation("hello");
            engine.AppendAssistantAction(action, invocation);
        }

        using var reopened = SessionJournalEngine.Open(path);
        SessionProjection projection = reopened.Project();

        Assert.NotNull(projection.Config);
        Assert.Equal("model-A", projection.Config.ModelId);
        Assert.Equal("system-A", projection.Config.SystemPrompt);
        Assert.Equal("surface-A", projection.Config.CompletionSurfaceId);
        Assert.Equal(2, projection.Context.Count);

        var observation = Assert.IsType<ObservationMessage>(projection.Context[0]);
        Assert.Equal("hello", observation.Content);

        var projectedAction = Assert.IsType<ActionMessage>(projection.Context[1]);
        Assert.Equal("answer continued", projectedAction.GetFlattenedText());
        Assert.Empty(projectedAction.ToolCalls);
        Assert.Equal(SessionExecutionPhase.Idle, projection.ExecutionState.Phase);
        Assert.Equal(SessionEventKind.AssistantActionProduced, projection.ExecutionState.HeadKind);
    }

    [Fact]
    public void ObservationPayload_UsesCanonicalEnvelopeBytesWithoutHeaderDuplication() {
        string path = NewJournalPath();
        using var engine = SessionJournalEngine.Create(path, new SessionCreateOptions(
            ModelId: "model-A",
            SystemPrompt: "system-A",
            CompletionSurfaceId: "surface-A"
        ));

        var address = engine.AppendObservation("hello");
        byte[] payload = engine.ReadPayloadBytes(address);
        string json = System.Text.Encoding.UTF8.GetString(payload);

        Assert.Equal("{\"v\":1,\"body\":{\"content\":\"hello\"}}", json);
        Assert.DoesNotContain("opaqueEventKind", json, StringComparison.Ordinal);
        Assert.DoesNotContain("sequenceNumber", json, StringComparison.Ordinal);
        Assert.DoesNotContain("utcUnixTimeMilliseconds", json, StringComparison.Ordinal);
        Assert.DoesNotContain("parent", json, StringComparison.Ordinal);
        Assert.DoesNotContain("payloadLength", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ActionPayload_RoundTripsToolCallAndProjectsPendingToolState() {
        string path = NewJournalPath();
        var invocation = new CompletionDescriptor("fake-provider", "fake-api-v1", "model-A");
        var action = new ActionMessage(new ActionBlock[] {
            new ActionBlock.Text("I will call a tool."),
            new ActionBlock.ToolCall(new RawToolCall("lookup", "call-1", "{\"q\":\"x\"}"))
        });

        EventAddress actionAddress;
        using (var engine = SessionJournalEngine.Create(path, new SessionCreateOptions(
            ModelId: "model-A",
            SystemPrompt: "system-A",
            CompletionSurfaceId: "surface-A"
        ))) {
            engine.AppendObservation("need lookup");
            actionAddress = engine.AppendAssistantAction(action, invocation);
            string actionJson = System.Text.Encoding.UTF8.GetString(engine.ReadPayloadBytes(actionAddress));
            Assert.Equal("{\"v\":1,\"body\":{\"action\":[{\"kind\":\"text\",\"content\":\"I will call a tool.\"},{\"kind\":\"tool-call\",\"toolName\":\"lookup\",\"toolCallId\":\"call-1\",\"rawArgumentsJson\":\"{\\u0022q\\u0022:\\u0022x\\u0022}\"}],\"invocation\":{\"providerId\":\"fake-provider\",\"apiSpecId\":\"fake-api-v1\",\"model\":\"model-A\"}}}", actionJson);
        }

        using var reopened = SessionJournalEngine.Open(path);
        SessionProjection projection = reopened.Project();

        var projectedAction = Assert.IsType<ActionMessage>(projection.Context[1]);
        RawToolCall call = Assert.Single(projectedAction.ToolCalls);
        Assert.Equal("lookup", call.ToolName);
        Assert.Equal("call-1", call.ToolCallId);
        Assert.Equal("{\"q\":\"x\"}", call.RawArgumentsJson);
        Assert.Equal(SessionExecutionPhase.AwaitingToolExecution, projection.ExecutionState.Phase);
        Assert.Equal(call, projection.ExecutionState.PendingToolCall);
    }

    [Fact]
    public async Task SendAsync_CommitsObservationThenActionAndUsesJournalConfig() {
        string path = NewJournalPath();
        var client = new ScriptedCompletionClient();
        client.Enqueue(request => {
            Assert.Equal("model-A", request.ModelId);
            Assert.Equal("system-A", request.SystemPrompt);
            Assert.Empty(request.Tools);
            var observation = Assert.IsType<ObservationMessage>(Assert.Single(request.Context));
            Assert.Equal("hello", observation.Content);
            return new CompletionResult(
                new ActionMessage(new ActionBlock[] { new ActionBlock.Text("answer") }),
                new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
            );
        });

        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            new SessionRuntime(client)
        );

        TurnResult result = await engine.SendAsync("hello", CancellationToken.None);
        SessionProjection projection = engine.Project();

        Assert.Equal("answer", result.Message.GetFlattenedText());
        Assert.Equal("scripted", result.Invocation.ProviderId);
        Assert.Equal(2, projection.Context.Count);
        Assert.Equal(SessionExecutionPhase.Idle, projection.ExecutionState.Phase);
        Assert.Equal(0, client.RemainingResponses);
    }

    [Fact]
    public async Task ResumeAsync_AfterObservationCommitted_ReplaysCompletionAndCommitsAction() {
        string path = NewJournalPath();
        var firstClient = new ScriptedCompletionClient();

        using (var engine = SessionJournalEngine.CreateForTest(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            new SessionRuntime(firstClient),
            new SessionJournalTestHooks(SessionJournalFailpoint.AfterObservationCommitted)
        )) {
            var ex = await Assert.ThrowsAsync<SessionJournalFailpointException>(
                () => engine.SendAsync("hello", CancellationToken.None)
            );
            Assert.Equal(SessionJournalFailpoint.AfterObservationCommitted, ex.Failpoint);
            Assert.Equal(SessionExecutionPhase.AwaitingAssistantAction, engine.Project().ExecutionState.Phase);
            Assert.Single(engine.Project().Context);
            Assert.Equal(0, firstClient.Calls);
        }

        var resumeClient = new ScriptedCompletionClient();
        resumeClient.Enqueue(request => {
            var observation = Assert.IsType<ObservationMessage>(Assert.Single(request.Context));
            Assert.Equal("hello", observation.Content);
            return new CompletionResult(
                new ActionMessage(new ActionBlock[] { new ActionBlock.Text("resumed") }),
                new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
            );
        });

        using var reopened = SessionJournalEngine.Open(path, new SessionRuntime(resumeClient));
        ResumeOutcome outcome = await reopened.ResumeAsync(CancellationToken.None);
        SessionProjection projection = reopened.Project();

        Assert.True(outcome.Advanced);
        Assert.Equal("resumed", outcome.Message!.GetFlattenedText());
        Assert.Equal(SessionExecutionPhase.Idle, projection.ExecutionState.Phase);
        Assert.Equal(2, projection.Context.Count);
        Assert.Equal(1, resumeClient.Calls);
    }

    [Fact]
    public async Task ResumeAsync_AfterCompletionBeforeAction_RerunsCompletionDeterministically() {
        string path = NewJournalPath();
        var firstClient = new ScriptedCompletionClient();
        firstClient.Enqueue(request => new CompletionResult(
            new ActionMessage(new ActionBlock[] { new ActionBlock.Text("not-yet-persisted") }),
            new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
        ));

        using (var engine = SessionJournalEngine.CreateForTest(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            new SessionRuntime(firstClient),
            new SessionJournalTestHooks(SessionJournalFailpoint.AfterCompletionBeforeActionCommitted)
        )) {
            var ex = await Assert.ThrowsAsync<SessionJournalFailpointException>(
                () => engine.SendAsync("hello", CancellationToken.None)
            );
            Assert.Equal(SessionJournalFailpoint.AfterCompletionBeforeActionCommitted, ex.Failpoint);
            Assert.Equal(SessionExecutionPhase.AwaitingAssistantAction, engine.Project().ExecutionState.Phase);
            Assert.Equal(1, firstClient.Calls);
        }

        var resumeClient = new ScriptedCompletionClient();
        resumeClient.Enqueue(request => new CompletionResult(
            new ActionMessage(new ActionBlock[] { new ActionBlock.Text("persisted-on-resume") }),
            new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
        ));

        using var reopened = SessionJournalEngine.Open(path, new SessionRuntime(resumeClient));
        ResumeOutcome outcome = await reopened.ResumeAsync(CancellationToken.None);

        Assert.True(outcome.Advanced);
        Assert.Equal("persisted-on-resume", outcome.Message!.GetFlattenedText());
        Assert.Equal("persisted-on-resume", Assert.IsType<ActionMessage>(reopened.Project().Context[1]).GetFlattenedText());
        Assert.Equal(1, resumeClient.Calls);
    }

    [Fact]
    public async Task ResumeAsync_WhenIdle_DoesNotCallCompletion() {
        string path = NewJournalPath();
        var client = new ScriptedCompletionClient();
        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            new SessionRuntime(client)
        );

        ResumeOutcome outcome = await engine.ResumeAsync(CancellationToken.None);

        Assert.False(outcome.Advanced);
        Assert.Equal(0, client.Calls);
    }

    private sealed class ScriptedCompletionClient : ICompletionClient {
        private readonly Queue<Func<CompletionRequest, CompletionResult>> _responses = new();

        public string Name => "scripted";

        public string ApiSpecId => "test-api-v1";

        public int Calls { get; private set; }

        public int RemainingResponses => _responses.Count;

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
            if (_responses.Count == 0) { throw new InvalidOperationException("No scripted response remaining."); }
            return Task.FromResult(_responses.Dequeue()(request));
        }
    }

    private string NewJournalPath() {
        string path = Path.Combine(System.IO.Path.GetTempPath(), "atelia-session-journal-tests", Guid.NewGuid().ToString("N"));
        _tempDirectories.Add(path);
        return path;
    }
}
