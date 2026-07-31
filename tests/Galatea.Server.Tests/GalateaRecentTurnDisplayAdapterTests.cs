using System.Text.Json;
using Atelia.ChatSession;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.SessionJournal;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaRecentTurnDisplayAdapterTests {
    private static readonly CompletionDescriptor Invocation = new(
        "test",
        "test-api-v1",
        "model-a"
    );

    [Theory]
    [InlineData(
        "玩家角色试图采取如下动作：\n```\nhello\n```\n",
        "hello"
    )]
    [InlineData(
        "玩家角色试图采取如下动作：\n```\nhello",
        "玩家角色试图采取如下动作：\n```\nhello"
    )]
    [InlineData("hello\n```\n", "hello\n```\n")]
    [InlineData("hello", "hello")]
    public void Project_NormalizesOnlyExactUserEnvelope(
        string stored,
        string expected
    ) {
        RecentTurnDto projected =
            GalateaRecentTurnDisplayAdapter.Project(
                Turn(stored, new ActionBlock.Text("answer"))
            );

        Assert.Equal(expected, projected.UserText);
    }

    [Fact]
    public void Project_PreservesOrderWithinTextAndReasoningChannels() {
        RecentTurnDto projected =
            GalateaRecentTurnDisplayAdapter.Project(
                Turn(
                    "user",
                    new ActionBlock.Text("text-a"),
                    new ActionBlock.TextReasoningBlock(
                        "reasoning-a",
                        Invocation
                    ),
                    new ActionBlock.Text("text-b"),
                    new ActionBlock.TextReasoningBlock(
                        "reasoning-b",
                        Invocation
                    )
                )
            );

        Assert.Equal("text-atext-b", projected.Assistant.Text);
        Assert.Equal(
            "reasoning-areasoning-b",
            projected.Assistant.ReasoningText
        );
    }

    [Fact]
    public void Project_StripsInlineThinkAcrossTextBlockBoundaries() {
        RecentTurnDto projected =
            GalateaRecentTurnDisplayAdapter.Project(
                Turn(
                    "user",
                    new ActionBlock.Text("before <thi"),
                    new ActionBlock.Text("nk>hidden"),
                    new ActionBlock.Text("</think>after")
                )
            );

        Assert.Equal("before after", projected.Assistant.Text);
        Assert.Null(projected.Assistant.ReasoningText);
    }

    [Fact]
    public void Project_PreservesReasoningOnlyTerminal() {
        RecentTurnDto projected =
            GalateaRecentTurnDisplayAdapter.Project(
                Turn(
                    "user",
                    new ActionBlock.TextReasoningBlock(
                        "reasoning-only",
                        Invocation
                    )
                )
            );

        Assert.Equal(string.Empty, projected.Assistant.Text);
        Assert.Equal(
            "reasoning-only",
            projected.Assistant.ReasoningText
        );
    }

    [Fact]
    public void Project_EmptyAndThinkOnlyTerminalsStillProduceDtos() {
        RecentTurnDto empty =
            GalateaRecentTurnDisplayAdapter.Project(Turn("empty"));
        RecentTurnDto thinkOnly =
            GalateaRecentTurnDisplayAdapter.Project(
                Turn(
                    "think-only",
                    new ActionBlock.Text(
                        "<think>not display text</think>"
                    )
                )
            );

        Assert.Equal("empty", empty.UserText);
        Assert.Equal(string.Empty, empty.Assistant.Text);
        Assert.Null(empty.Assistant.ReasoningText);
        Assert.Equal("think-only", thinkOnly.UserText);
        Assert.Equal(string.Empty, thinkOnly.Assistant.Text);
        Assert.Null(thinkOnly.Assistant.ReasoningText);
    }

    [Fact]
    public void Project_BatchMappingKeepsNewestFirstInputOrder() {
        SessionCompletedTurnProjection[] newestFirst = [
            Turn("newest", new ActionBlock.Text("answer-newest")),
            Turn("middle", new ActionBlock.Text("answer-middle")),
            Turn("oldest", new ActionBlock.Text("answer-oldest"))
        ];

        RecentTurnDto[] projected = newestFirst
            .Select(GalateaRecentTurnDisplayAdapter.Project)
            .ToArray();

        Assert.Equal(
            ["newest", "middle", "oldest"],
            projected.Select(static turn => turn.UserText).ToArray()
        );
    }

    [Fact]
    public void RecentTurnWire_HasNoRecapOrReasoningPresenceFlags() {
        RecentTurnDto projected =
            GalateaRecentTurnDisplayAdapter.Project(
                Turn(
                    "user",
                    new ActionBlock.TextReasoningBlock(
                        "reasoning",
                        Invocation
                    ),
                    new ActionBlock.Text("answer")
                )
            );

        string json = JsonSerializer.Serialize(
            projected,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        );

        Assert.Contains("\"userText\":\"user\"", json);
        Assert.Contains("\"text\":\"answer\"", json);
        Assert.Contains("\"reasoningText\":\"reasoning\"", json);
        Assert.DoesNotContain("isRecap", json, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "hasReasoning",
            json,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task LegacyRecentTurns_HidesRecapAndStrictlyHonorsMaximum() {
        string path = Path.Combine(
            Path.GetTempPath(),
            "atelia-galatea-display-tests",
            Guid.NewGuid().ToString("N")
        );
        try {
            var client = new QueueCompletionClient();
            client.EnqueueText(new string('A', 120));
            client.EnqueueText("reply-second");
            client.EnqueueText("summary");
            client.EnqueueText("reply-third");
            client.EnqueueText("reply-fourth");
            using var engine = await ChatSessionEngine.CreateAsync(
                path,
                new ChatSessionCreateOptions("system"),
                new ChatSessionRuntime(
                    client,
                    "openai-chat/strict",
                    "model-a",
                    new ToolRegistry(Array.Empty<ITool>())
                        .CreateSession()
                )
            );

            await engine.SendMessageAsync("first");
            await engine.SendMessageAsync("second");
            var compacted = await engine.CompactAsync(
                "compact-system",
                "compact-prompt"
            );
            Assert.True(compacted.Applied);
            await engine.SendMessageAsync("third");
            await engine.SendMessageAsync("fourth");

            var connection = new CompletionConnectionConfig(
                "test",
                "openai-chat",
                "model-a",
                "openai-chat/strict",
                "http://localhost:8000/",
                ApiKey: "test-key"
            );
            using var registry = new CompletionConnectionRegistry(
                new CompletionConnectionsFileConfig(
                    [connection],
                    DefaultConnectionId: connection.Id
                ),
                new SingleClientFactory(client)
            );
            await using var service = new GalateaHostService(
                new GalateaConfig([], [connection], connection.Id),
                registry,
                DisabledGalateaUserMessageNormalizer.Instance
            );

            RecentTurnsResponseDto response =
                service.BuildRecentTurnsResponse(
                    engine,
                    maxTurns: 2
                );

            Assert.Collection(
                response.Turns,
                turn => {
                    Assert.Equal("fourth", turn.UserText);
                    Assert.Equal(
                        "reply-fourth",
                        turn.Assistant.Text
                    );
                },
                turn => {
                    Assert.Equal("third", turn.UserText);
                    Assert.Equal(
                        "reply-third",
                        turn.Assistant.Text
                    );
                }
            );
        }
        finally {
            if (Directory.Exists(path)) {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LegacyRecentTurns_UsesEmptyTerminalInsteadOfToolAction() {
        string path = Path.Combine(
            Path.GetTempPath(),
            "atelia-galatea-display-tests",
            Guid.NewGuid().ToString("N")
        );
        try {
            var client = new QueueCompletionClient();
            client.Enqueue(new ActionMessage([
                new ActionBlock.Text("intermediate"),
                new ActionBlock.ToolCall(
                    new RawToolCall(
                        "echo",
                        "call-1",
                        """{"value":"x"}"""
                    )
                )
            ]));
            client.Enqueue(new ActionMessage([]));
            using var engine = await ChatSessionEngine.CreateAsync(
                path,
                new ChatSessionCreateOptions("system"),
                new ChatSessionRuntime(
                    client,
                    "openai-chat/strict",
                    "model-a",
                    new ToolRegistry([new EchoTool()]).CreateSession()
                )
            );
            await engine.SendMessageAsync("use tool");

            var connection = new CompletionConnectionConfig(
                "test",
                "openai-chat",
                "model-a",
                "openai-chat/strict",
                "http://localhost:8000/",
                ApiKey: "test-key"
            );
            using var registry = new CompletionConnectionRegistry(
                new CompletionConnectionsFileConfig(
                    [connection],
                    DefaultConnectionId: connection.Id
                ),
                new SingleClientFactory(client)
            );
            await using var service = new GalateaHostService(
                new GalateaConfig([], [connection], connection.Id),
                registry,
                DisabledGalateaUserMessageNormalizer.Instance
            );

            RecentTurnDto projected = Assert.Single(
                service.BuildRecentTurns(engine)
            );
            Assert.Equal("use tool", projected.UserText);
            Assert.Equal(string.Empty, projected.Assistant.Text);
            Assert.Null(projected.Assistant.ReasoningText);
        }
        finally {
            if (Directory.Exists(path)) {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    private static SessionCompletedTurnProjection Turn(
        string observation,
        params ActionBlock[] blocks
    ) => new(
        ObservationAddress: default,
        observation,
        new SessionTerminalActionProjection(
            Address: default,
            new ActionMessage(blocks)
        )
    );

    private sealed class QueueCompletionClient : ICompletionClient {
        private readonly Queue<ActionMessage> _responses = [];

        public string Name => "test";

        public string ApiSpecId => "test-api-v1";

        public void EnqueueText(string text) => Enqueue(
            new ActionMessage([new ActionBlock.Text(text)])
        );

        public void Enqueue(ActionMessage message) =>
            _responses.Enqueue(message);

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            ActionMessage message = _responses.Dequeue();
            string text = message.GetFlattenedText();
            if (text.Length > 0) {
                observer?.OnTextDelta(text);
            }
            return Task.FromResult(new CompletionResult(
                message,
                new CompletionDescriptor(
                    Name,
                    ApiSpecId,
                    request.ModelId
                )
            ));
        }
    }

    private sealed class EchoTool : ITool {
        public ToolDefinition Definition { get; } = new(
            "echo",
            "Echoes a value.",
            new ToolSchema.Object([
                new ToolSchema.Property(
                    "value",
                    new ToolSchema.Value(ToolParamType.String),
                    isRequired: true
                )
            ])
        );

        public ValueTask<ToolExecuteResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                ToolExecuteResult.FromText(
                    ToolExecutionStatus.Success,
                    "ok"
                )
            );
        }
    }

    private sealed class SingleClientFactory(ICompletionClient client)
        : ICompletionClientFactory {
        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) {
            _ = connection;
            return client;
        }
    }
}
