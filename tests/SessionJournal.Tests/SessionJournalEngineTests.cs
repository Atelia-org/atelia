using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
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

        using var engine = SessionJournalEngine.Create(path,
            new SessionCreateOptions(
                ModelId: "model-A",
                SystemPrompt: "system-A",
                CompletionSurfaceId: "surface-A"
            )
        );

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
        var action = new ActionMessage(
            new ActionBlock[] {
                new ActionBlock.Text("answer"),
                new ActionBlock.Text(" continued")
        }
        );

        using (var engine = SessionJournalEngine.Create(path,
            new SessionCreateOptions(
                ModelId: "model-A",
                SystemPrompt: "system-A",
                CompletionSurfaceId: "surface-A"
            )
        )) {
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
        using var engine = SessionJournalEngine.Create(path,
            new SessionCreateOptions(
                ModelId: "model-A",
                SystemPrompt: "system-A",
                CompletionSurfaceId: "surface-A"
            )
        );

        var address = engine.AppendObservation("你好，Atelia <session>");
        byte[] payload = engine.ReadPayloadBytes(address);
        string json = System.Text.Encoding.UTF8.GetString(payload);

        Assert.Equal("{\"v\":1,\"body\":{\"content\":\"你好，Atelia <session>\"}}", json);
        Assert.DoesNotContain("\\u4F60", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\u597D", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("opaqueEventKind", json, StringComparison.Ordinal);
        Assert.DoesNotContain("sequenceNumber", json, StringComparison.Ordinal);
        Assert.DoesNotContain("utcUnixTimeMilliseconds", json, StringComparison.Ordinal);
        Assert.DoesNotContain("parent", json, StringComparison.Ordinal);
        Assert.DoesNotContain("payloadLength", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ObservationPayload_CompressedEventJournalStillProjectsLogicalPayload() {
        string path = NewJournalPath();
        var journalOptions = new EventJournalOptions {
            PayloadCodecPolicy = EventPayloadCodecPolicy.Brotli with {
                MinimumPayloadLength = 0,
                MinimumSavingsBytes = 1,
                MinimumSavingsRatio = 0.01
            }
        };
        string content = string.Concat(Enumerable.Repeat("这是一段用于验证 SessionJournal logical payload 透明读取的中文内容。", 128));
        EventAddress observationAddress;

        using (var engine = SessionJournalEngine.CreateForTest(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            runtime: null,
            new SessionJournalTestHooks(),
            journalOptions
        )) {
            observationAddress = engine.AppendObservation(content);
        }

        using (var reopened = SessionJournalEngine.OpenForTest(path, runtime: null, new SessionJournalTestHooks(), journalOptions)) {
            SessionProjection projection = reopened.Project();

            var observation = Assert.IsType<ObservationMessage>(Assert.Single(projection.Context));
            Assert.Equal(content, observation.Content);
        }

        using var journal = EventJournal.EventJournal.OpenExisting(path, journalOptions);
        EventFrameHeader header = journal.ReadEventHeaderChecked(observationAddress).Unwrap();
        Assert.Equal(EventPayloadCodecId.Brotli, header.PayloadCodecId);
    }

    [Fact]
    public void ObservationPayload_DefaultSessionJournalCompressionUsesZlib() {
        string path = NewJournalPath();
        string content = string.Concat(Enumerable.Repeat("SessionJournal 默认压缩应适合中文 LLM 输出和 JSON payload。", 160));
        EventAddress observationAddress;

        using (var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A")
        )) {
            observationAddress = engine.AppendObservation(content);
        }

        using (var reopened = SessionJournalEngine.Open(path)) {
            SessionProjection projection = reopened.Project();

            var observation = Assert.IsType<ObservationMessage>(Assert.Single(projection.Context));
            Assert.Equal(content, observation.Content);
        }

        using var journal = EventJournal.EventJournal.OpenExisting(path);
        EventFrameHeader header = journal.ReadEventHeaderChecked(observationAddress).Unwrap();
        Assert.Equal(EventPayloadCodecId.Zlib, header.PayloadCodecId);
    }

    [Fact]
    public void ActionPayload_RoundTripsToolCallAndProjectsPendingToolState() {
        string path = NewJournalPath();
        var invocation = new CompletionDescriptor("fake-provider", "fake-api-v1", "model-A");
        var action = new ActionMessage(
            new ActionBlock[] {
                new ActionBlock.Text("I will call a tool."),
                new ActionBlock.ToolCall(new RawToolCall("lookup", "call-1", "{\"q\":\"x\"}"))
        }
        );

        EventAddress actionAddress;
        using (var engine = SessionJournalEngine.Create(path,
            new SessionCreateOptions(
                ModelId: "model-A",
                SystemPrompt: "system-A",
                CompletionSurfaceId: "surface-A"
            )
        )) {
            engine.AppendObservation("need lookup");
            actionAddress = engine.AppendAssistantAction(action, invocation);
            string actionJson = System.Text.Encoding.UTF8.GetString(engine.ReadPayloadBytes(actionAddress));
            Assert.Equal("{\"v\":1,\"body\":{\"action\":[{\"kind\":\"text\",\"content\":\"I will call a tool.\"},{\"kind\":\"tool-call\",\"toolName\":\"lookup\",\"toolCallId\":\"call-1\",\"rawArgumentsJson\":\"{\\\"q\\\":\\\"x\\\"}\"}],\"invocation\":{\"providerId\":\"fake-provider\",\"apiSpecId\":\"fake-api-v1\",\"model\":\"model-A\"}}}", actionJson);
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
        client.Enqueue(
            request => {
                Assert.Equal("model-A", request.ModelId);
                Assert.Equal("system-A", request.SystemPrompt);
                Assert.Empty(request.Tools);
                var observation = Assert.IsType<ObservationMessage>(Assert.Single(request.Context));
                Assert.Equal("hello", observation.Content);
                return new CompletionResult(
                    new ActionMessage(new ActionBlock[] { new ActionBlock.Text("answer") }),
                    new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
                );
            }
        );

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
        resumeClient.Enqueue(
            request => {
                var observation = Assert.IsType<ObservationMessage>(Assert.Single(request.Context));
                Assert.Equal("hello", observation.Content);
                return new CompletionResult(
                    new ActionMessage(new ActionBlock[] { new ActionBlock.Text("resumed") }),
                    new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
                );
            }
        );

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
        firstClient.Enqueue(
            request => new CompletionResult(
                new ActionMessage(new ActionBlock[] { new ActionBlock.Text("not-yet-persisted") }),
                new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
            )
        );

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
        resumeClient.Enqueue(
            request => new CompletionResult(
                new ActionMessage(new ActionBlock[] { new ActionBlock.Text("persisted-on-resume") }),
                new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
            )
        );

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

    [Fact]
    public async Task SendAsync_ToolLoop_PersistsStartResultAndFeedsCompletion() {
        string path = NewJournalPath();
        var client = new ScriptedCompletionClient();
        var tool = new RecordingTool("lookup", context => ToolExecuteResult.FromText(ToolExecutionStatus.Success, $"result:{context.RawToolCall.RawArgumentsJson}"));
        ToolSession toolSession = new ToolRegistry([tool]).CreateSession();

        client.Enqueue(
            request => {
                Assert.Single(request.Tools);
                var observation = Assert.IsType<ObservationMessage>(Assert.Single(request.Context));
                Assert.Equal("need lookup", observation.Content);
                return new CompletionResult(
                    new ActionMessage(
                        new ActionBlock[] {
                            new ActionBlock.Text("calling"),
                            new ActionBlock.ToolCall(new RawToolCall("lookup", "call-1", "{\"q\":\"x\"}"))
                    }
                    ),
                    new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
                );
            }
        );
        client.Enqueue(
            request => {
                Assert.Equal(3, request.Context.Count);
                var action = Assert.IsType<ActionMessage>(request.Context[1]);
                Assert.Single(action.ToolCalls);
                var results = Assert.IsType<ToolResultsMessage>(request.Context[2]);
                ToolResult result = Assert.Single(results.Results);
                Assert.Equal("lookup", result.ToolName);
                Assert.Equal("call-1", result.ToolCallId);
                Assert.Equal(ToolExecutionStatus.Success, result.Status);
                Assert.Equal("result:{\"q\":\"x\"}", result.GetFlattenedText());
                return new CompletionResult(
                    new ActionMessage(new ActionBlock[] { new ActionBlock.Text("final") }),
                    new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
                );
            }
        );

        using (var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            new SessionRuntime(client, toolSession)
        )) {
            TurnResult turn = await engine.SendAsync("need lookup", CancellationToken.None);
            SessionProjection projection = engine.Project();

            Assert.Equal("final", turn.Message.GetFlattenedText());
            Assert.Equal(SessionExecutionPhase.Idle, projection.ExecutionState.Phase);
            Assert.Equal(4, projection.Context.Count);
            Assert.Equal(1, tool.Calls);
            Assert.Equal(2, client.Calls);
        }

        string[] payloads = ReadJournalPayloadJson(path);
        Assert.Equal("{\"v\":1,\"body\":{\"toolCallId\":\"call-1\",\"toolName\":\"lookup\",\"rawArgumentsJson\":\"{\\\"q\\\":\\\"x\\\"}\",\"operationId\":\"" + ExtractOperationId(payloads[3]) + "\"}}", payloads[3]);
        Assert.Equal("{\"v\":1,\"body\":{\"toolCallId\":\"call-1\",\"toolName\":\"lookup\",\"status\":\"success\",\"blocks\":[{\"kind\":\"text\",\"content\":\"result:{\\\"q\\\":\\\"x\\\"}\"}]}}", payloads[4]);
        Assert.DoesNotContain("opaqueEventKind", payloads[3], StringComparison.Ordinal);
        Assert.DoesNotContain("sequenceNumber", payloads[4], StringComparison.Ordinal);

        using var reopened = SessionJournalEngine.Open(path);
        SessionProjection replayed = reopened.Project();
        Assert.Equal(SessionExecutionPhase.Idle, replayed.ExecutionState.Phase);
        var replayedResults = Assert.IsType<ToolResultsMessage>(replayed.Context[2]);
        Assert.Equal("result:{\"q\":\"x\"}", Assert.Single(replayedResults.Results).GetFlattenedText());
    }

    [Fact]
    public async Task ResumeAsync_AfterToolStarted_ReexecutesToolAndUsesPersistedOperationId() {
        string path = NewJournalPath();
        var firstClient = new ScriptedCompletionClient();
        var firstTool = new RecordingTool("lookup", _ => ToolExecuteResult.FromText(ToolExecutionStatus.Success, "not-persisted"));
        firstClient.Enqueue(
            request => new CompletionResult(
                new ActionMessage(new ActionBlock[] { new ActionBlock.ToolCall(new RawToolCall("lookup", "call-1", "{}")) }),
                new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
            )
        );

        using (var engine = SessionJournalEngine.CreateForTest(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            new SessionRuntime(firstClient, new ToolRegistry([firstTool]).CreateSession()),
            new SessionJournalTestHooks(SessionJournalFailpoint.AfterToolStartedCommitted)
        )) {
            var ex = await Assert.ThrowsAsync<SessionJournalFailpointException>(
                () => engine.SendAsync("need lookup", CancellationToken.None)
            );
            Assert.Equal(SessionJournalFailpoint.AfterToolStartedCommitted, ex.Failpoint);
            SessionExecutionState state = engine.Project().ExecutionState;
            Assert.Equal(SessionExecutionPhase.AwaitingToolExecution, state.Phase);
            Assert.True(state.PendingToolExecutionStarted);
            Assert.NotNull(state.PendingOperationId);
            Assert.Equal(0, firstTool.Calls);
        }

        string persistedOperationId;
        using (var inspection = SessionJournalEngine.Open(path)) {
            persistedOperationId = inspection.Project().ExecutionState.PendingOperationId!;
        }

        Assert.Equal(persistedOperationId, ExtractOperationId(ReadJournalPayloadJson(path)[3]));

        var resumeClient = new ScriptedCompletionClient();
        var resumeTool = new RecordingTool("lookup", _ => ToolExecuteResult.FromText(ToolExecutionStatus.Success, "resumed-result"));
        resumeClient.Enqueue(
            request => {
                var results = Assert.IsType<ToolResultsMessage>(request.Context[2]);
                Assert.Equal("resumed-result", Assert.Single(results.Results).GetFlattenedText());
                return new CompletionResult(
                    new ActionMessage(new ActionBlock[] { new ActionBlock.Text("done") }),
                    new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
                );
            }
        );

        using var reopened = SessionJournalEngine.Open(path, new SessionRuntime(resumeClient, new ToolRegistry([resumeTool]).CreateSession()));
        ResumeOutcome outcome = await reopened.ResumeAsync(CancellationToken.None);
        SessionProjection projection = reopened.Project();

        Assert.True(outcome.Advanced);
        Assert.Equal("done", outcome.Message!.GetFlattenedText());
        Assert.Equal(SessionExecutionPhase.Idle, projection.ExecutionState.Phase);
        Assert.Equal(1, resumeTool.Calls);
        Assert.False(string.IsNullOrWhiteSpace(persistedOperationId));
    }

    [Fact]
    public async Task ResumeAsync_AfterToolResult_CompletesWithoutReexecutingTool() {
        string path = NewJournalPath();
        var firstClient = new ScriptedCompletionClient();
        var tool = new RecordingTool("lookup", _ => ToolExecuteResult.FromText(ToolExecutionStatus.Success, "persisted-result"));
        firstClient.Enqueue(
            request => new CompletionResult(
                new ActionMessage(new ActionBlock[] { new ActionBlock.ToolCall(new RawToolCall("lookup", "call-1", "{}")) }),
                new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
            )
        );

        using (var engine = SessionJournalEngine.CreateForTest(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            new SessionRuntime(firstClient, new ToolRegistry([tool]).CreateSession()),
            new SessionJournalTestHooks(SessionJournalFailpoint.AfterToolResultCommitted)
        )) {
            var ex = await Assert.ThrowsAsync<SessionJournalFailpointException>(
                () => engine.SendAsync("need lookup", CancellationToken.None)
            );
            Assert.Equal(SessionJournalFailpoint.AfterToolResultCommitted, ex.Failpoint);
            Assert.Equal(SessionExecutionPhase.AwaitingAssistantAction, engine.Project().ExecutionState.Phase);
            Assert.Equal(1, tool.Calls);
        }

        var resumeClient = new ScriptedCompletionClient();
        var resumeTool = new RecordingTool("lookup", _ => ToolExecuteResult.FromText(ToolExecutionStatus.Success, "should-not-run"));
        resumeClient.Enqueue(
            request => {
                var results = Assert.IsType<ToolResultsMessage>(request.Context[2]);
                Assert.Equal("persisted-result", Assert.Single(results.Results).GetFlattenedText());
                return new CompletionResult(
                    new ActionMessage(new ActionBlock[] { new ActionBlock.Text("done") }),
                    new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
                );
            }
        );

        using var reopened = SessionJournalEngine.Open(path, new SessionRuntime(resumeClient, new ToolRegistry([resumeTool]).CreateSession()));
        ResumeOutcome outcome = await reopened.ResumeAsync(CancellationToken.None);

        Assert.True(outcome.Advanced);
        Assert.Equal("done", outcome.Message!.GetFlattenedText());
        Assert.Equal(0, resumeTool.Calls);
        Assert.Equal(SessionExecutionPhase.Idle, reopened.Project().ExecutionState.Phase);
    }

    [Fact]
    public async Task ResumeAsync_AfterFirstToolResult_RestoresExecutionSequenceForNextTool() {
        string path = NewJournalPath();
        var firstClient = new ScriptedCompletionClient();
        var alpha = new RecordingTool("alpha", context => ToolExecuteResult.FromText(ToolExecutionStatus.Success, $"seq:{context.ExecutionSequence}"));
        var beta = new RecordingTool("beta", context => ToolExecuteResult.FromText(ToolExecutionStatus.Success, $"seq:{context.ExecutionSequence}"));
        firstClient.Enqueue(
            request => new CompletionResult(
                new ActionMessage(
                    new ActionBlock[] {
                        new ActionBlock.ToolCall(new RawToolCall("alpha", "call-A", "{}")),
                        new ActionBlock.ToolCall(new RawToolCall("beta", "call-B", "{}"))
            }
                ),
                new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
            )
        );

        using (var engine = SessionJournalEngine.CreateForTest(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            new SessionRuntime(firstClient, new ToolRegistry([alpha, beta]).CreateSession()),
            new SessionJournalTestHooks(SessionJournalFailpoint.AfterToolResultCommitted)
        )) {
            var ex = await Assert.ThrowsAsync<SessionJournalFailpointException>(
                () => engine.SendAsync("need two tools", CancellationToken.None)
            );
            Assert.Equal(SessionJournalFailpoint.AfterToolResultCommitted, ex.Failpoint);
            Assert.Equal(1, alpha.Calls);
            Assert.Equal(0, beta.Calls);
            Assert.Equal(1, engine.Project().ExecutionState.ToolExecutionSequenceCheckpoint);
        }

        var resumeClient = new ScriptedCompletionClient();
        var resumedAlpha = new RecordingTool("alpha", _ => ToolExecuteResult.FromText(ToolExecutionStatus.Success, "should-not-run"));
        var resumedBeta = new RecordingTool("beta", context => ToolExecuteResult.FromText(ToolExecutionStatus.Success, $"seq:{context.ExecutionSequence}"));
        resumeClient.Enqueue(
            request => {
                var results = Assert.IsType<ToolResultsMessage>(request.Context[2]);
                Assert.Collection(
                    results.Results,
                    first => Assert.Equal("seq:1", first.GetFlattenedText()),
                    second => Assert.Equal("seq:2", second.GetFlattenedText())
                );
                return new CompletionResult(
                    new ActionMessage(new ActionBlock[] { new ActionBlock.Text("done") }),
                    new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
                );
            }
        );

        using var reopened = SessionJournalEngine.Open(path, new SessionRuntime(resumeClient, new ToolRegistry([resumedAlpha, resumedBeta]).CreateSession()));
        ResumeOutcome outcome = await reopened.ResumeAsync(CancellationToken.None);

        Assert.True(outcome.Advanced);
        Assert.Equal("done", outcome.Message!.GetFlattenedText());
        Assert.Equal(0, resumedAlpha.Calls);
        Assert.Equal(1, resumedBeta.Calls);
        Assert.Equal(SessionExecutionPhase.Idle, reopened.Project().ExecutionState.Phase);
    }

    [Fact]
    public async Task SendAsync_LaterToolTurn_ContinuesExecutionSequence() {
        string path = NewJournalPath();
        var client = new ScriptedCompletionClient();
        var tool = new RecordingTool("lookup", context => ToolExecuteResult.FromText(ToolExecutionStatus.Success, $"seq:{context.ExecutionSequence}"));
        ToolSession toolSession = new ToolRegistry([tool]).CreateSession();

        client.Enqueue(
            request => new CompletionResult(
                new ActionMessage(new ActionBlock[] { new ActionBlock.ToolCall(new RawToolCall("lookup", "call-1", "{}")) }),
                new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
            )
        );
        client.Enqueue(
            request => new CompletionResult(
                new ActionMessage(new ActionBlock[] { new ActionBlock.Text("first done") }),
                new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
            )
        );
        client.Enqueue(
            request => new CompletionResult(
                new ActionMessage(new ActionBlock[] { new ActionBlock.ToolCall(new RawToolCall("lookup", "call-2", "{}")) }),
                new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
            )
        );
        client.Enqueue(
            request => {
                var results = Assert.IsType<ToolResultsMessage>(request.Context[^1]);
                Assert.Equal("seq:2", Assert.Single(results.Results).GetFlattenedText());
                return new CompletionResult(
                    new ActionMessage(new ActionBlock[] { new ActionBlock.Text("second done") }),
                    new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
                );
            }
        );

        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            new SessionRuntime(client, toolSession)
        );

        await engine.SendAsync("first", CancellationToken.None);
        TurnResult second = await engine.SendAsync("second", CancellationToken.None);

        Assert.Equal("second done", second.Message.GetFlattenedText());
        Assert.Equal(2, tool.Calls);
        Assert.Equal(2, engine.Project().ExecutionState.ToolExecutionSequenceCheckpoint);
    }

    [Fact]
    public async Task SendAsync_MultipleToolCalls_ProjectsResultsInDeclaredOrder() {
        string path = NewJournalPath();
        var client = new ScriptedCompletionClient();
        var registry = new ToolRegistry(
            [
            new RecordingTool("alpha", _ => ToolExecuteResult.FromText(ToolExecutionStatus.Success, "A")),
            new RecordingTool("beta", _ => ToolExecuteResult.FromText(ToolExecutionStatus.Success, "B"))
        ]
        );

        client.Enqueue(
            request => new CompletionResult(
                new ActionMessage(
                    new ActionBlock[] {
                        new ActionBlock.ToolCall(new RawToolCall("alpha", "call-A", "{}")),
                        new ActionBlock.ToolCall(new RawToolCall("beta", "call-B", "{}"))
            }
                ),
                new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
            )
        );
        client.Enqueue(
            request => {
                var results = Assert.IsType<ToolResultsMessage>(request.Context[2]);
                Assert.Collection(
                    results.Results,
                    first => Assert.Equal("call-A", first.ToolCallId),
                    second => Assert.Equal("call-B", second.ToolCallId)
                );
                return new CompletionResult(
                    new ActionMessage(new ActionBlock[] { new ActionBlock.Text("done") }),
                    new CompletionDescriptor("scripted", "test-api-v1", request.ModelId)
                );
            }
        );

        using (var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-A", "system-A", "surface-A"),
            new SessionRuntime(client, registry.CreateSession())
        )) {
            await engine.SendAsync("need two tools", CancellationToken.None);
        }

        using var reopened = SessionJournalEngine.Open(path);
        SessionProjection projection = reopened.Project();
        var results = Assert.IsType<ToolResultsMessage>(projection.Context[2]);
        Assert.Collection(
            results.Results,
            first => Assert.Equal("call-A", first.ToolCallId),
            second => Assert.Equal("call-B", second.ToolCallId)
        );
        Assert.Equal(SessionExecutionPhase.Idle, projection.ExecutionState.Phase);
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

    private sealed class RecordingTool : ITool {
        private readonly Func<ToolExecutionContext, ToolExecuteResult> _execute;

        public RecordingTool(string name, Func<ToolExecutionContext, ToolExecuteResult> execute) {
            Definition = new ToolDefinition(name, $"Tool {name}.", new ToolSchema.Object());
            _execute = execute;
        }

        public ToolDefinition Definition { get; }

        public int Calls { get; private set; }

        public ValueTask<ToolExecuteResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult(_execute(context));
        }
    }

    private static string[] ReadJournalPayloadJson(string path) {
        using var journal = EventJournal.EventJournal.OpenExisting(path);
        RefId main = journal.OpenBranch(SessionJournalDefaults.MainBranchName).Unwrap();
        EventAddress head = journal.GetHead(main) ?? throw new InvalidDataException("SessionJournal test journal has no head.");
        IReadOnlyList<EventAddress> chain = journal.ReadChronologicalChain(head, checkedRead: true).Unwrap();
        var payloads = new string[chain.Count];
        for (int i = 0; i < chain.Count; i++) {
            using EventFrame frame = journal.ReadEvent(chain[i]).Unwrap();
            payloads[i] = System.Text.Encoding.UTF8.GetString(frame.Payload.ToArray());
        }

        return payloads;
    }

    private static string ExtractOperationId(string startedPayload) {
        using var document = System.Text.Json.JsonDocument.Parse(startedPayload);
        return document.RootElement.GetProperty("body").GetProperty("operationId").GetString()
            ?? throw new InvalidDataException("tool-execution-started payload is missing operationId.");
    }

    private string NewJournalPath() {
        string path = Path.Combine(System.IO.Path.GetTempPath(), "atelia-session-journal-tests", Guid.NewGuid().ToString("N"));
        _tempDirectories.Add(path);
        return path;
    }
}
