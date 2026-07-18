using System.ComponentModel;
using System.Text.Json.Serialization;
using Atelia.ChatSession;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Xunit;

namespace Atelia.ChatSession.Tests;

public sealed class ChatSessionQuickStartSamplesTests {
    [Fact]
    public async Task QuickStart_CreateSendAndReopen_PersistsConversation() {
        string repoDir = CreateTempDirectory();
        try {
            var completionClient = new ScriptedCompletionClient("openai-chat-v1");
            completionClient.EnqueueText("hello from assistant");

            var runtime = new ChatSessionRuntime(
                CompletionClient: completionClient,
                CompletionSurfaceId: "openai-chat/strict",
                ModelId: "model-a",
                ToolSession: new ToolRegistry(Array.Empty<ITool>()).CreateSession()
            );

            using (var engine = await ChatSessionEngine.CreateAsync(
                       repoDir,
                       new ChatSessionCreateOptions(
                           SystemPrompt: "You are a helpful assistant."
                       ),
                       runtime
                   )) {
                var turn = await engine.SendMessageAsync("hi", CancellationToken.None);

                Assert.Equal("hello from assistant", turn.Message.GetFlattenedText());
                Assert.Equal(0, turn.ToolCallsExecuted);
                Assert.Equal(2, engine.Context.Count);
                Assert.Equal(2, engine.GetStatistics().MessageCount);
            }

            using var reopened = await ChatSessionEngine.OpenAsync(
                repoDir,
                new ChatSessionRuntime(
                    CompletionClient: new ScriptedCompletionClient("openai-chat-v1"),
                    CompletionSurfaceId: "openai-chat/strict",
                    ModelId: "model-a",
                    ToolSession: new ToolRegistry(Array.Empty<ITool>()).CreateSession()
                )
            );

            Assert.Collection(
                reopened.Context,
                message => {
                    var observation = Assert.IsType<ObservationMessage>(message);
                    Assert.Equal("hi", observation.Content);
                },
                message => {
                    var action = Assert.IsType<ActionMessage>(message);
                    Assert.Equal("hello from assistant", action.GetFlattenedText());
                }
            );
        }
        finally {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    [Fact]
    public async Task QuickStart_SendMessageAsync_AutomaticallyRunsToolLoop() {
        string repoDir = CreateTempDirectory();
        try {
            var completionClient = new ScriptedCompletionClient("openai-chat-v1");
            completionClient.Enqueue(
                async (request, observer, ct) => {
                    Assert.Equal("workspace.echo", Assert.Single(request.Tools).Name);

                    var call = new RawToolCall("workspace.echo", "call-1", """{"text":"alpha"}""");
                    observer?.OnToolCall(call);
                    return new CompletionResult(
                        new ActionMessage([
                            new ActionBlock.ToolCall(call),
                        ]),
                        new CompletionDescriptor("scripted", "openai-chat-v1", request.ModelId)
                    );
                }
            );
            completionClient.Enqueue(
                async (request, observer, ct) => {
                    var toolResults = Assert.IsType<ToolResultsMessage>(request.Context[^1]);
                    var toolResult = Assert.Single(toolResults.Results);
                    Assert.Equal("workspace.echo", toolResult.ToolName);
                    Assert.Equal("echo:alpha|demo", toolResult.GetFlattenedText());

                    observer?.OnTextDelta("final answer");
                    return new CompletionResult(
                        new ActionMessage([
                            new ActionBlock.Text("final answer"),
                        ]),
                        new CompletionDescriptor("scripted", "openai-chat-v1", request.ModelId)
                    );
                }
            );

            var toolHost = new DemoTools();
            var echoTool = MethodToolWrapper.FromMethod(
                toolHost,
                typeof(DemoTools).GetMethod(nameof(DemoTools.EchoAsync))!
            );

            var runtime = new ChatSessionRuntime(
                CompletionClient: completionClient,
                CompletionSurfaceId: "openai-chat/strict",
                ModelId: "model-a",
                ToolSession: new ToolRegistry([echoTool]).CreateSession(
                    items: new Dictionary<string, object?> { ["scope"] = "demo" }
                )
            );

            using var engine = await ChatSessionEngine.CreateAsync(
                repoDir,
                new ChatSessionCreateOptions(
                    SystemPrompt: "You are a coding assistant."
                ),
                runtime
            );

            var turn = await engine.SendMessageAsync("call workspace.echo", CancellationToken.None);

            Assert.Equal("final answer", turn.Message.GetFlattenedText());
            Assert.Equal(1, turn.ToolCallsExecuted);
            Assert.Collection(
                engine.Context,
                message => Assert.Equal("call workspace.echo", Assert.IsType<ObservationMessage>(message).Content),
                message => Assert.Single(Assert.IsType<ActionMessage>(message).ToolCalls),
                message => Assert.Equal("echo:alpha|demo", Assert.Single(Assert.IsType<ToolResultsMessage>(message).Results).GetFlattenedText()),
                message => Assert.Equal("final answer", Assert.IsType<ActionMessage>(message).GetFlattenedText())
            );
        }
        finally {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    [Fact]
    public async Task ContextHeader_ProjectsToSystemUserAndAssistantBeforeRecentHistory() {
        string repoDir = CreateTempDirectory();
        try {
            var completionClient = new ScriptedCompletionClient("openai-chat-v1");
            completionClient.Enqueue(
                (request, observer, ct) => {
                    Assert.Equal("base-system\n\nheader-system", request.SystemPrompt);
                    Assert.Collection(
                        request.Context,
                        message => Assert.Equal("header-user", Assert.IsType<ObservationMessage>(message).Content),
                        message => Assert.Equal("header-assistant", Assert.IsType<ActionMessage>(message).GetFlattenedText()),
                        message => Assert.Equal("fresh-user", Assert.IsType<ObservationMessage>(message).Content)
                    );

                    return Task.FromResult(
                        new CompletionResult(
                            new ActionMessage([new ActionBlock.Text("fresh-assistant")]),
                            new CompletionDescriptor("scripted", "openai-chat-v1", request.ModelId)
                        )
                    );
                }
            );

            using var engine = await ChatSessionEngine.CreateAsync(
                repoDir,
                new ChatSessionCreateOptions(SystemPrompt: "base-system"),
                new ChatSessionRuntime(
                    CompletionClient: completionClient,
                    CompletionSurfaceId: "openai-chat/strict",
                    ModelId: "model-a",
                    ToolSession: new ToolRegistry(Array.Empty<ITool>()).CreateSession()
                )
            );

            engine.SetContextHeader(
                new ContextHeader(
                    "header-system",
                    "header-user",
                    new ActionMessage([new ActionBlock.Text("header-assistant")])
                )
            );

            var turn = await engine.SendMessageAsync("fresh-user", CancellationToken.None);

            Assert.Equal("fresh-assistant", turn.Message.GetFlattenedText());
            Assert.Collection(
                engine.Context,
                message => Assert.IsType<ContextHeader>(message),
                message => Assert.Equal("fresh-user", Assert.IsType<ObservationMessage>(message).Content),
                message => Assert.Equal("fresh-assistant", Assert.IsType<ActionMessage>(message).GetFlattenedText())
            );
        }
        finally {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    [Fact]
    public void FindHalfContextSplitPoint_WhenEnabled_CanSplitAssistantToUserBoundary() {
        var messages = new IHistoryMessage[] {
            new ContextHeader(
                new string('s', 80),
                "oldest-user",
                new ActionMessage([new ActionBlock.Text(new string('a', 80))])
            ),
            new ObservationMessage("recent-user")
        };

        int withoutBoundary = ChatSessionEngine.FindHalfContextSplitPoint(messages);
        int withBoundary = ChatSessionEngine.FindHalfContextSplitPoint(messages, allowActionToObservationBoundary: true);

        Assert.Equal(-1, withoutBoundary);
        Assert.Equal(1, withBoundary);
    }

    private static string CreateTempDirectory() {
        string path = Path.Combine(Path.GetTempPath(), "chat-session-quickstart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class ScriptedCompletionClient(string apiSpecId) : ICompletionClient {
        private readonly Queue<Func<CompletionRequest, CompletionStreamObserver?, CancellationToken, Task<CompletionResult>>> _responses = new();

        public string Name => "scripted";

        public string ApiSpecId => apiSpecId;

        public void Enqueue(Func<CompletionRequest, CompletionStreamObserver?, CancellationToken, Task<CompletionResult>> response) {
            _responses.Enqueue(response);
        }

        public void EnqueueText(string text) {
            Enqueue(
                async (request, observer, ct) => {
                    observer?.OnTextDelta(text);
                    return new CompletionResult(
                        new ActionMessage([new ActionBlock.Text(text)]),
                        new CompletionDescriptor(Name, ApiSpecId, request.ModelId)
                    );
                }
            );
        }

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            if (_responses.Count == 0) {
                throw new InvalidOperationException("No scripted response remaining.");
            }

            return _responses.Dequeue()(request, observer, cancellationToken);
        }
    }

    private sealed class DemoTools {
        [Tool("workspace.echo", "Echo text with session scope.")]
        public ValueTask<ToolExecuteResult> EchoAsync(
            EchoInput input,
            ToolExecutionContext context,
            CancellationToken cancellationToken
        ) {
            _ = cancellationToken;

            var scope = context.Items is not null && context.Items.TryGetValue("scope", out var value)
                ? value as string
                : null;

            return ValueTask.FromResult(
                ToolExecuteResult.FromText(
                    ToolExecutionStatus.Success,
                    $"echo:{input.Text}|{scope}"
                )
            );
        }
    }

    [Description("Input for workspace.echo.")]
    private sealed record class EchoInput(
        [property: Description("Text to echo back.")]
        [property: JsonPropertyName("text")]
        string Text
    );
}
