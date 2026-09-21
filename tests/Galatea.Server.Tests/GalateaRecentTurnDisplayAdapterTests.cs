using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.Completion.OpenAI;
using Atelia.SessionJournal;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaRecentTurnDisplayAdapterTests {
    [Theory]
    [InlineData(SessionTurnEndReason.Stopped, "stopped")]
    [InlineData(SessionTurnEndReason.Rejected, "rejected")]
    [InlineData(SessionTurnEndReason.Incomplete, "incomplete")]
    public void Project_TerminatedHasReasonWithoutInventingAssistant(
        SessionTurnEndReason reason, string expected
    ) {
        var end = new SessionTurnEndProjection(default, reason);
        var turn = new SessionCompletedTurnProjection(
            default, SessionInputContent.Text("user"),
            new SessionClosedTurnOutcome.Terminated(end));
        RecentTurnDto projected = GalateaRecentTurnDisplayAdapter.Project(turn);
        Assert.Equal("user", projected.UserText);
        Assert.Null(projected.Assistant);
        Assert.Equal(expected, projected.EndReason);
        Assert.Equal(projected, GalateaRecentTurnDisplayAdapter.Project(
            new SessionRetractedTurnProjection(default, turn.ObservationContent, null, end)));
    }

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

        Assert.Equal("text-atext-b", projected.RequireAssistant().Text);
        Assert.Equal(
            "reasoning-areasoning-b",
            projected.RequireAssistant().ReasoningText
        );
    }

    [Fact]
    public void Project_ExposesProviderNativeReasoningPlainText() {
        RecentTurnDto projected = GalateaRecentTurnDisplayAdapter.Project(
            Turn(
                "user",
                new OpenAIChatReasoningBlock("provider reasoning", Invocation),
                new ActionBlock.Text("answer")
            )
        );

        Assert.Equal("answer", projected.RequireAssistant().Text);
        Assert.Equal("provider reasoning", projected.RequireAssistant().ReasoningText);
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

        Assert.Equal("before after", projected.RequireAssistant().Text);
        Assert.Null(projected.RequireAssistant().ReasoningText);
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

        Assert.Equal(string.Empty, projected.RequireAssistant().Text);
        Assert.Equal(
            "reasoning-only",
            projected.RequireAssistant().ReasoningText
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
        Assert.Equal(string.Empty, empty.RequireAssistant().Text);
        Assert.Null(empty.RequireAssistant().ReasoningText);
        Assert.Equal("think-only", thinkOnly.UserText);
        Assert.Equal(string.Empty, thinkOnly.RequireAssistant().Text);
        Assert.Null(thinkOnly.RequireAssistant().ReasoningText);
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
}
