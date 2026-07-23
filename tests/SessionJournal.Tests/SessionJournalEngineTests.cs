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

    private string NewJournalPath() {
        string path = Path.Combine(System.IO.Path.GetTempPath(), "atelia-session-journal-tests", Guid.NewGuid().ToString("N"));
        _tempDirectories.Add(path);
        return path;
    }
}
