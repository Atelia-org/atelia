using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Atelia.ChatSession;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.FamilyChat.Server;
using Atelia.StateJournal;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atelia.FamilyChat.Server.Tests;

public sealed class FamilyChatServerTests {
    [Fact]
    public async Task ChatSession_SendMessageAsync_WithObserver_PropagatesAcrossToolLoop() {
        string repoDir = CreateTempDirectory();
        try {
            var client = new ScriptedCompletionClient("openai-chat-v1");
            client.Enqueue(
                async (request, observer, ct) => {
                    observer?.OnTextDelta("thinking ");
                    var call = new RawToolCall("echo", "call-1", """{"value":"alpha"}""");
                    observer?.OnToolCall(call);
                    return new CompletionResult(
                        new ActionMessage([
                            new ActionBlock.Text("thinking "),
                            new ActionBlock.ToolCall(call),
                        ]),
                        new CompletionDescriptor("test", "openai-chat-v1", request.ModelId)
                    );
                }
            );
            client.Enqueue(
                async (request, observer, ct) => {
                    observer?.OnTextDelta("done");
                    return new CompletionResult(
                        new ActionMessage([
                            new ActionBlock.Text("done"),
                        ]),
                        new CompletionDescriptor("test", "openai-chat-v1", request.ModelId)
                    );
                }
            );

            using var engine = await ChatSessionEngine.CreateAsync(
                repoDir,
                new ChatSessionCreateOptions("system"),
                new ChatSessionRuntime(
                    client,
                    "openai-chat/strict",
                    "model-a",
                    new ToolRegistry([new EchoTool()]).CreateSession()
                )
            );

            string observed = string.Empty;
            var observer = new CompletionStreamObserver();
            observer.ReceivedTextDelta += delta => observed += delta;

            var result = await engine.SendMessageAsync("hello", observer);

            Assert.Equal("thinking done", observed);
            Assert.Equal("done", result.Message.GetFlattenedText());
            Assert.Equal(1, result.ToolCallsExecuted);
        }
        finally {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    [Fact]
    public async Task ChatSession_SendMessageAsync_StripsInlineThinkFromPersistedAssistantText() {
        string repoDir = CreateTempDirectory();
        try {
            var client = new ScriptedCompletionClient("openai-chat-v1");
            client.Enqueue(
                async (request, observer, ct) => {
                    observer?.OnTextDelta("<think>secret</think>visible");
                    return new CompletionResult(
                        new ActionMessage([
                            new ActionBlock.Text("<think>secret</think>visible"),
                        ]),
                        new CompletionDescriptor("test", "openai-chat-v1", request.ModelId)
                    );
                }
            );

            using var engine = await ChatSessionEngine.CreateAsync(
                repoDir,
                new ChatSessionCreateOptions("system"),
                new ChatSessionRuntime(
                    client,
                    "openai-chat/strict",
                    "model-a",
                    new ToolRegistry(Array.Empty<ITool>()).CreateSession()
                )
            );

            await engine.SendMessageAsync("hello");

            Assert.Collection(
                engine.Context,
                message => Assert.Equal("hello", Assert.IsType<ObservationMessage>(message).Content),
                message => Assert.Equal("visible", Assert.IsType<ActionMessage>(message).GetFlattenedText())
            );
        }
        finally {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    [Fact]
    public async Task ChatSession_SendMessageAsync_WhenCompletionIsIncomplete_DoesNotPersistPartialTurn() {
        string repoDir = CreateTempDirectory();
        try {
            var client = new ScriptedCompletionClient("openai-chat-v1");
            client.Enqueue(
                async (request, observer, ct) => {
                    observer?.OnTextDelta("<think>partial");
                    return new CompletionResult(
                        new ActionMessage([
                            new ActionBlock.Text("<think>partial"),
                        ]),
                        new CompletionDescriptor("test", "openai-chat-v1", request.ModelId),
                        termination: CompletionTermination.Incomplete("length", "scripted truncation")
                    );
                }
            );

            using var engine = await ChatSessionEngine.CreateAsync(
                repoDir,
                new ChatSessionCreateOptions("system"),
                new ChatSessionRuntime(
                    client,
                    "openai-chat/strict",
                    "model-a",
                    new ToolRegistry(Array.Empty<ITool>()).CreateSession()
                )
            );

            var ex = await Assert.ThrowsAsync<ChatSessionTurnAbortedException>(
                () => engine.SendMessageAsync("one")
            );

            Assert.Equal(CompletionTerminationKind.Incomplete, ex.Termination.Kind);
            Assert.Empty(engine.Context);
        }
        finally {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    [Fact]
    public async Task FamilyChat_AutoPrefillThinkOpenTag_DefaultsOnForUnslothQwen36() {
        string tempDir = CreateTempDirectory();
        try {
            var configPath = WriteConfig(
                tempDir,
                thresholdTokens: 200,
                users: [
                    new FamilyChatUserConfig(
                        "alice",
                        "Alice",
                        "pw1",
                        Path.Combine(tempDir, "alice-session"),
                        200,
                        "compact-system",
                        "compact-prompt",
                        "system"
                    )
                ],
                connectionModelId: "unsloth/qwen3.6",
                connectionSurfaceId: "openai-chat/sglang-compatible"
            );

            var scriptFactory = new ScriptedCompletionClientFactory();
            scriptFactory.For("alice").Enqueue(
                (request, observer, ct) => {
                    Assert.Collection(
                        request.Context,
                        message => Assert.Equal("玩家角色试图采取如下动作：\n```\none\n```\n", Assert.IsType<ObservationMessage>(message).Content),
                        message => Assert.Equal("<think>", Assert.IsType<ActionMessage>(message).GetFlattenedText())
                    );

                    observer?.OnTextDelta("先想一想</think>正式回答");
                    return Task.FromResult(
                        new CompletionResult(
                            new ActionMessage([new ActionBlock.Text("先想一想</think>正式回答")]),
                            new CompletionDescriptor("test", "openai-chat-v1", request.ModelId)
                        )
                    );
                }
            );

            await using var factory = new FamilyChatServerFactory(configPath, scriptFactory);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions {
                AllowAutoRedirect = false,
                HandleCookies = true,
            });
            await LoginAsync(client, "alice", "pw1");

            string sse = await ReadSseAsStringAsync(client, "one");

            var turns = await GetRecentTurnsAsync(client);
            Assert.Single(turns);
            Assert.Equal("正式回答", turns[0].Assistant.Text);
            Assert.DoesNotContain("先想一想", sse, StringComparison.Ordinal);
        }
        finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task FamilyChat_AutoPrefillThinkOpenTag_CanBeDisabledPerRequest() {
        string tempDir = CreateTempDirectory();
        try {
            var configPath = WriteConfig(
                tempDir,
                thresholdTokens: 200,
                users: [
                    new FamilyChatUserConfig(
                        "alice",
                        "Alice",
                        "pw1",
                        Path.Combine(tempDir, "alice-session"),
                        200,
                        "compact-system",
                        "compact-prompt",
                        "system"
                    )
                ],
                connectionModelId: "unsloth/qwen3.6",
                connectionSurfaceId: "openai-chat/sglang-compatible"
            );

            var scriptFactory = new ScriptedCompletionClientFactory();
            scriptFactory.For("alice").Enqueue(
                (request, observer, ct) => {
                    Assert.Single(request.Context);
                    Assert.Equal(
                        "玩家角色试图采取如下动作：\n```\none\n```\n",
                        Assert.IsType<ObservationMessage>(request.Context[0]).Content
                    );

                    observer?.OnTextDelta("先想一想</think>正式回答");
                    return Task.FromResult(
                        new CompletionResult(
                            new ActionMessage([new ActionBlock.Text("先想一想</think>正式回答")]),
                            new CompletionDescriptor("test", "openai-chat-v1", request.ModelId)
                        )
                    );
                }
            );

            await using var factory = new FamilyChatServerFactory(configPath, scriptFactory);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions {
                AllowAutoRedirect = false,
                HandleCookies = true,
            });
            await LoginAsync(client, "alice", "pw1");

            var started = await StartTurnAsync(client, "one", autoPrefillThinkOpenTag: false);
            _ = await ReadTurnEventsAsStringAsync(client, started.TurnId);
            await WaitForCurrentTurnIdleAsync(client);

            var turns = await GetRecentTurnsAsync(client);
            Assert.Single(turns);
            Assert.Equal("先想一想</think>正式回答", turns[0].Assistant.Text);
        }
        finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Login_And_RecentTurns_Work() {
        string tempDir = CreateTempDirectory();
        try {
            var configPath = WriteConfig(
                tempDir,
                thresholdTokens: 200,
                users: [
                    new FamilyChatUserConfig(
                        "alice",
                        "Alice",
                        "pw1",
                        Path.Combine(tempDir, "alice-session"),
                        200,
                        "compact-system",
                        "compact-prompt",
                        "system"
                    )
                ]
            );

            var scriptFactory = new ScriptedCompletionClientFactory();
            scriptFactory.For("alice").EnqueueText("first reply");
            scriptFactory.For("alice").EnqueueText("second reply");

            await using var factory = new FamilyChatServerFactory(configPath, scriptFactory);
            using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var redirect = await anonymous.GetAsync("/");
            Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
            Assert.Equal("/login", redirect.Headers.Location?.OriginalString);

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions {
                AllowAutoRedirect = false,
                HandleCookies = true,
            });

            var loginResponse = await client.PostAsync(
                "/login",
                new FormUrlEncodedContent(new Dictionary<string, string> {
                    ["userId"] = "alice",
                    ["password"] = "pw1",
                })
            );
            Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);

            await ReadSseAsStringAsync(client, "one");
            await ReadSseAsStringAsync(client, "two");

            var me = await client.GetFromJsonAsync<FamilyChatMeDto>("/api/me");
            Assert.NotNull(me);
            Assert.Equal("alice", me!.UserId);

            var turns = await GetRecentTurnsAsync(client);
            Assert.NotNull(turns);
            Assert.Equal(2, turns!.Count);
            Assert.Equal("two", turns[0].UserText);
            Assert.Equal("second reply", turns[0].Assistant.Text);
            Assert.Equal("one", turns[1].UserText);
        }
        finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task UserMessageNormalizer_RewritesInputBeforeMainModel_AndPersistsRewrittenText() {
        string tempDir = CreateTempDirectory();
        try {
            var configPath = WriteConfig(
                tempDir,
                thresholdTokens: 200,
                users: [
                    new FamilyChatUserConfig(
                        "alice",
                        "Alice",
                        "pw1",
                        Path.Combine(tempDir, "alice-session"),
                        200,
                        "compact-system",
                        "compact-prompt",
                        "system"
                    )
                ]
            );

            var scriptFactory = new ScriptedCompletionClientFactory();
            scriptFactory.For("alice").Enqueue(
                (request, observer, ct) => {
                    var lastObservation = Assert.IsType<ObservationMessage>(request.Context[^1]);
                    Assert.Contains("麦林炮手", lastObservation.Content, StringComparison.Ordinal);
                    Assert.DoesNotContain("买林炮手", lastObservation.Content, StringComparison.Ordinal);

                    return Task.FromResult(
                        new CompletionResult(
                            new ActionMessage([new ActionBlock.Text("明白了，小炮登场。")]),
                            new CompletionDescriptor("test", "openai-chat-v1", request.ModelId)
                        )
                    );
                }
            );

            var normalizer = new ScriptedUserMessageNormalizer();
            normalizer.Enqueue("我想玩麦林炮手。");

            await using var factory = new FamilyChatServerFactory(configPath, scriptFactory, normalizer);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions {
                AllowAutoRedirect = false,
                HandleCookies = true,
            });

            await LoginAsync(client, "alice", "pw1");
            string sse = await ReadSseAsStringAsync(client, "我想玩买林炮手。");

            Assert.Contains("event: done", sse, StringComparison.Ordinal);

            var turns = await GetRecentTurnsAsync(client);
            Assert.NotNull(turns);
            Assert.Single(turns!);
            Assert.Equal("我想玩麦林炮手。", turns[0].UserText);
            Assert.Equal("明白了，小炮登场。", turns[0].Assistant.Text);
        }
        finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task UserMessageNormalizer_FailureFallsBackToOriginalInput() {
        string tempDir = CreateTempDirectory();
        try {
            var configPath = WriteConfig(
                tempDir,
                thresholdTokens: 200,
                users: [
                    new FamilyChatUserConfig(
                        "alice",
                        "Alice",
                        "pw1",
                        Path.Combine(tempDir, "alice-session"),
                        200,
                        "compact-system",
                        "compact-prompt",
                        "system"
                    )
                ]
            );

            var scriptFactory = new ScriptedCompletionClientFactory();
            scriptFactory.For("alice").Enqueue(
                (request, observer, ct) => {
                    var lastObservation = Assert.IsType<ObservationMessage>(request.Context[^1]);
                    Assert.Contains("买林炮手", lastObservation.Content, StringComparison.Ordinal);

                    return Task.FromResult(
                        new CompletionResult(
                            new ActionMessage([new ActionBlock.Text("按原文继续。")]),
                            new CompletionDescriptor("test", "openai-chat-v1", request.ModelId)
                        )
                    );
                }
            );

            await using var factory = new FamilyChatServerFactory(
                configPath,
                scriptFactory,
                new ThrowingUserMessageNormalizer()
            );
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions {
                AllowAutoRedirect = false,
                HandleCookies = true,
            });

            await LoginAsync(client, "alice", "pw1");
            string sse = await ReadSseAsStringAsync(client, "我想玩买林炮手。");

            Assert.Contains("event: done", sse, StringComparison.Ordinal);

            var turns = await GetRecentTurnsAsync(client);
            Assert.NotNull(turns);
            Assert.Single(turns!);
            Assert.Equal("我想玩买林炮手。", turns[0].UserText);
            Assert.Equal("按原文继续。", turns[0].Assistant.Text);
        }
        finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task UserMessageNormalizer_WhitespaceResultFallsBackToOriginalInput_AndDoesNotReportChanged() {
        string tempDir = CreateTempDirectory();
        try {
            var configPath = WriteConfig(
                tempDir,
                thresholdTokens: 200,
                users: [
                    new FamilyChatUserConfig(
                        "alice",
                        "Alice",
                        "pw1",
                        Path.Combine(tempDir, "alice-session"),
                        200,
                        "compact-system",
                        "compact-prompt",
                        "system"
                    )
                ]
            );

            var scriptFactory = new ScriptedCompletionClientFactory();
            scriptFactory.For("alice").Enqueue(
                (request, observer, ct) => {
                    var lastObservation = Assert.IsType<ObservationMessage>(request.Context[^1]);
                    Assert.Contains("买林炮手", lastObservation.Content, StringComparison.Ordinal);

                    return Task.FromResult(
                        new CompletionResult(
                            new ActionMessage([new ActionBlock.Text("按原文继续。")]),
                            new CompletionDescriptor("test", "openai-chat-v1", request.ModelId)
                        )
                    );
                }
            );

            var normalizer = new ScriptedUserMessageNormalizer();
            normalizer.Enqueue("   ");

            await using var factory = new FamilyChatServerFactory(configPath, scriptFactory, normalizer);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions {
                AllowAutoRedirect = false,
                HandleCookies = true,
            });

            await LoginAsync(client, "alice", "pw1");
            string sse = await ReadSseAsStringAsync(client, "我想玩买林炮手。");

            Assert.Contains("event: done", sse, StringComparison.Ordinal);
            Assert.Contains("\"phase\":\"input-normalization-finish\",\"changed\":false", sse, StringComparison.Ordinal);

            var turns = await GetRecentTurnsAsync(client);
            Assert.NotNull(turns);
            Assert.Single(turns!);
            Assert.Equal("我想玩买林炮手。", turns[0].UserText);
            Assert.Equal("按原文继续。", turns[0].Assistant.Text);
        }
        finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("<cleaned>修正后文本</cleaned>", "修正后文本")]
    [InlineData("prefix<cleaned>修正后文本</cleaned>suffix", "修正后文本")]
    [InlineData("<think>hidden</think><cleaned>修正后文本</cleaned>", "修正后文本")]
    [InlineData("修正后文本", "")]
    [InlineData("修正后：<cleaned-missing>", "")]
    public void DeepSeekUserMessageNormalizer_ExtractNormalizedText_RequiresCleanedTag(string rawText, string expected) {
        Assert.Equal(expected, DeepSeekFamilyChatUserMessageNormalizer.ExtractNormalizedText(rawText));
    }

    [Fact]
    public void DeepSeekUserMessageNormalizer_BuildNormalizationPrompt_EscapesXmlSensitiveCharacters() {
        string prompt = DeepSeekFamilyChatUserMessageNormalizer.BuildNormalizationPrompt("A < B && x </player-input> y");

        Assert.Contains("&lt;", prompt, StringComparison.Ordinal);
        Assert.Contains("&amp;&amp;", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("A < B", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("</player-input> y\n</player-input>", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExistingSession_ReopenServer_StillReturnsRecentTurns() {
        string tempDir = CreateTempDirectory();
        try {
            string sessionDir = Path.Combine(tempDir, "alice-session");
            await SeedSessionAsync(
                sessionDir,
                "model-a",
                "openai-chat/strict",
                [
                    ("one", "first reply"),
                    ("two", "second reply"),
                ]
            );

            var configPath = WriteConfig(
                tempDir,
                thresholdTokens: 200,
                users: [
                    new FamilyChatUserConfig(
                        "alice",
                        "Alice",
                        "pw1",
                        sessionDir,
                        200,
                        "compact-system",
                        "compact-prompt",
                        "system"
                    )
                ]
            );

            await using var factory = new FamilyChatServerFactory(configPath, new ScriptedCompletionClientFactory());
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions {
                AllowAutoRedirect = false,
                HandleCookies = true,
            });
            await LoginAsync(client, "alice", "pw1");

            var turns = await GetRecentTurnsAsync(client);

            Assert.NotNull(turns);
            Assert.Equal(2, turns!.Count);
            Assert.Equal("two", turns[0].UserText);
            Assert.Equal("second reply", turns[0].Assistant.Text);
            Assert.Equal("one", turns[1].UserText);
            Assert.Equal("first reply", turns[1].Assistant.Text);
        }
        finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ChatSession_TryRemoveLatestCompletedTurn_RemovesLatestTurnFromContext() {
        string repoDir = CreateTempDirectory();
        try {
            var client = new ScriptedCompletionClient("openai-chat-v1");
            client.EnqueueText("first reply");
            client.EnqueueText("second reply");

            using var engine = await ChatSessionEngine.CreateAsync(
                repoDir,
                new ChatSessionCreateOptions("system"),
                new ChatSessionRuntime(
                    client,
                    "openai-chat/strict",
                    "model-a",
                    new ToolRegistry(Array.Empty<ITool>()).CreateSession()
                )
            );

            await engine.SendMessageAsync("one");
            await engine.SendMessageAsync("two");

            bool removed = engine.TryRemoveLatestCompletedTurn(out var result);

            Assert.True(removed);
            Assert.NotNull(result);
            Assert.Equal("two", result!.UserMessage);
            Assert.Equal(2, result.RemovedMessageCount);
            Assert.Collection(
                engine.Context,
                message => {
                    var observation = Assert.IsType<ObservationMessage>(message);
                    Assert.Equal("one", observation.Content);
                },
                message => {
                    var action = Assert.IsType<ActionMessage>(message);
                    Assert.Equal("first reply", action.GetFlattenedText());
                }
            );
        }
        finally {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    [Fact]
    public async Task ChatSession_TryRemoveLatestCompletedTurn_WithToolLoop_RemovesWholeTurnSuffix() {
        string repoDir = CreateTempDirectory();
        try {
            var client = new ScriptedCompletionClient("openai-chat-v1");
            client.Enqueue(
                async (request, observer, ct) => {
                    var call = new RawToolCall("echo", "call-1", """{"value":"alpha"}""");
                    observer?.OnToolCall(call);
                    return new CompletionResult(
                        new ActionMessage([
                            new ActionBlock.ToolCall(call),
                        ]),
                        new CompletionDescriptor("test", "openai-chat-v1", request.ModelId)
                    );
                }
            );
            client.EnqueueText("final reply");

            using var engine = await ChatSessionEngine.CreateAsync(
                repoDir,
                new ChatSessionCreateOptions("system"),
                new ChatSessionRuntime(
                    client,
                    "openai-chat/strict",
                    "model-a",
                    new ToolRegistry([new EchoTool()]).CreateSession()
                )
            );

            await engine.SendMessageAsync("one");

            bool removed = engine.TryRemoveLatestCompletedTurn(out var result);

            Assert.True(removed);
            Assert.NotNull(result);
            Assert.Equal("one", result!.UserMessage);
            Assert.Equal(4, result.RemovedMessageCount);
            Assert.Empty(engine.Context);
        }
        finally {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    [Fact]
    public async Task ChatSession_CompactAsync_DoesNotLeaveRecapFollowedByOrphanAction() {
        string repoDir = CreateTempDirectory();
        try {
            var client = new ScriptedCompletionClient("openai-chat-v1");
            client.EnqueueText(new string('A', 200));
            client.EnqueueText("summary");

            using var engine = await ChatSessionEngine.CreateAsync(
                repoDir,
                new ChatSessionCreateOptions("system"),
                new ChatSessionRuntime(
                    client,
                    "openai-chat/strict",
                    "model-a",
                    new ToolRegistry(Array.Empty<ITool>()).CreateSession()
                )
            );

            await engine.SendMessageAsync("one");
            var compaction = await engine.CompactAsync("compact-system", "compact-prompt");

            Assert.False(compaction.Applied);
            Assert.Equal(CompactionFailureReason.NoValidSplitPoint, compaction.FailureReason);
            Assert.Collection(
                engine.Context,
                message => Assert.IsType<ObservationMessage>(message),
                message => Assert.IsType<ActionMessage>(message)
            );
        }
        finally {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    [Fact]
    public async Task ChatSession_CompactAsync_StripsInlineThinkFromRecapSummary() {
        string repoDir = CreateTempDirectory();
        try {
            var client = new ScriptedCompletionClient("openai-chat-v1");
            client.EnqueueText(new string('A', 120));
            client.EnqueueText("keep");
            client.EnqueueText("<think>hidden</think>summary");

            using var engine = await ChatSessionEngine.CreateAsync(
                repoDir,
                new ChatSessionCreateOptions("system"),
                new ChatSessionRuntime(
                    client,
                    "openai-chat/strict",
                    "model-a",
                    new ToolRegistry(Array.Empty<ITool>()).CreateSession()
                )
            );

            await engine.SendMessageAsync("first");
            await engine.SendMessageAsync("second");

            var compaction = await engine.CompactAsync("compact-system", "compact-prompt");

            Assert.True(compaction.Applied);
            Assert.Collection(
                engine.Context,
                message => Assert.Equal("summary", Assert.IsType<RecapMessage>(message).Content),
                message => Assert.Equal("second", Assert.IsType<ObservationMessage>(message).Content),
                message => Assert.Equal("keep", Assert.IsType<ActionMessage>(message).GetFlattenedText())
            );
        }
        finally {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    [Fact]
    public async Task ChatSession_CompactAsync_WritesRecapSourceAnchor() {
        string repoDir = CreateTempDirectory();
        try {
            var client = new ScriptedCompletionClient("openai-chat-v1");
            client.EnqueueText(new string('A', 120));
            client.EnqueueText("keep");
            client.EnqueueText("summary");

            using var engine = await ChatSessionEngine.CreateAsync(
                repoDir,
                new ChatSessionCreateOptions("system"),
                new ChatSessionRuntime(
                    client,
                    "openai-chat/strict",
                    "model-a",
                    new ToolRegistry(Array.Empty<ITool>()).CreateSession()
                )
            );

            await engine.SendMessageAsync("first");
            await engine.SendMessageAsync("second");
            Assert.True(engine.PersistedHeadAddress is { });
            string sourceHead = engine.PersistedHeadAddress.Value.ToString();
            int sourceMessageCount = engine.Context.Count;

            var compaction = await engine.CompactAsync("compact-system", "compact-prompt");

            Assert.True(compaction.Applied);
            Assert.Collection(
                engine.Context,
                message => {
                    var recap = Assert.IsType<RecapMessage>(message);
                    Assert.Equal("summary", recap.Content);
                    Assert.NotNull(recap.SourceAnchor);
                    var anchor = recap.SourceAnchor!;
                    Assert.Equal(sourceHead, anchor.SourceHeadBeforeCompaction);
                    Assert.Equal("main", anchor.SourceBranchName);
                    Assert.Equal(0, anchor.SourceStartIndex);
                    Assert.Equal(compaction.SplitIndex, anchor.SourceEndExclusive);
                    Assert.Equal(sourceMessageCount, anchor.SourceMessageCountBefore);
                    Assert.Equal("prefix-summary", anchor.CompactionKind);
                },
                message => Assert.Equal("second", Assert.IsType<ObservationMessage>(message).Content),
                message => Assert.Equal("keep", Assert.IsType<ActionMessage>(message).GetFlattenedText())
            );
        }
        finally {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    [Fact]
    public void ChatSession_ReadRecapWithoutSourceAnchor_KeepsLegacyRecordCompatible() {
        string repoDir = CreateTempDirectory();
        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var messages = revision.CreateDeque();

            MessageRecord.PrependRecap(messages, "legacy summary");

            var context = MessageRecord.ToHistoryMessages(messages);
            var recap = Assert.IsType<RecapMessage>(Assert.Single(context));
            Assert.Equal("legacy summary", recap.Content);
            Assert.Null(recap.SourceAnchor);
        }
        finally {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    [Fact]
    public async Task ChatSessionHistoryReader_ReadCurrent_ReturnsRecordsWithMetadata() {
        string repoDir = CreateTempDirectory();
        try {
            var client = new ScriptedCompletionClient("openai-chat-v1");
            client.EnqueueText("reply");

            using (var engine = await ChatSessionEngine.CreateAsync(
                repoDir,
                new ChatSessionCreateOptions("system"),
                new ChatSessionRuntime(
                    client,
                    "openai-chat/strict",
                    "model-a",
                    new ToolRegistry(Array.Empty<ITool>()).CreateSession()
                )
            )) {
                engine.SetContextHeader(
                    new ContextHeader(
                        "header-system",
                        "header-user",
                        new ActionMessage([new ActionBlock.Text("header-assistant")])
                    )
                );
                await engine.SendMessageAsync("hello");
            }

            var records = ChatSessionHistoryReader.ReadCurrent(repoDir);

            Assert.Collection(
                records,
                record => {
                    Assert.Equal(0, record.Index);
                    Assert.Equal(MessageRecord.KindContextHeader, record.Kind);
                    Assert.NotNull(record.TimestampUtc);
                    var header = Assert.IsType<ContextHeader>(record.Message);
                    Assert.Equal("header-system", header.SystemPromptFragment);
                },
                record => {
                    Assert.Equal(1, record.Index);
                    Assert.Equal(MessageRecord.KindObservation, record.Kind);
                    Assert.NotNull(record.TimestampUtc);
                    Assert.Equal("hello", Assert.IsType<ObservationMessage>(record.Message).Content);
                },
                record => {
                    Assert.Equal(2, record.Index);
                    Assert.Equal(MessageRecord.KindAction, record.Kind);
                    Assert.NotNull(record.TimestampUtc);
                    Assert.Equal("reply", Assert.IsType<ActionMessage>(record.Message).GetFlattenedText());
                }
            );

            string markdown = ChatSessionMarkdownExporter.Export(records);
            Assert.Contains("## 00000 context-header", markdown, StringComparison.Ordinal);
            Assert.Contains("### systemPromptFragment", markdown, StringComparison.Ordinal);
            Assert.Contains("header-assistant", markdown, StringComparison.Ordinal);
            Assert.Contains("## 00001 observation", markdown, StringComparison.Ordinal);
            Assert.Contains("## 00002 action", markdown, StringComparison.Ordinal);
        }
        finally {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    [Fact]
    public async Task ChatSessionMarkdownExporter_PreservesToolCallsAndToolResults() {
        string repoDir = CreateTempDirectory();
        try {
            var client = new ScriptedCompletionClient("openai-chat-v1");
            client.Enqueue(
                (request, observer, ct) => {
                    var call = new RawToolCall("echo", "call-1", "{\"value\":\"alpha\"}");
                    return Task.FromResult(
                        new CompletionResult(
                            new ActionMessage([
                                new ActionBlock.Text("thinking"),
                                new ActionBlock.ToolCall(call),
                            ]),
                            new CompletionDescriptor("test", "openai-chat-v1", request.ModelId)
                        )
                    );
                }
            );
            client.EnqueueText("done");

            using (var engine = await ChatSessionEngine.CreateAsync(
                repoDir,
                new ChatSessionCreateOptions("system"),
                new ChatSessionRuntime(
                    client,
                    "openai-chat/strict",
                    "model-a",
                    new ToolRegistry([new EchoTool()]).CreateSession()
                )
            )) {
                await engine.SendMessageAsync("hello");
            }

            var records = ChatSessionHistoryReader.ReadCurrent(repoDir);
            string markdown = ChatSessionMarkdownExporter.Export(records);

            Assert.Contains(records, static record => record.Kind == MessageRecord.KindToolResults);
            Assert.Contains("### toolCall 00", markdown, StringComparison.Ordinal);
            Assert.Contains("- toolName: echo", markdown, StringComparison.Ordinal);
            Assert.Contains("- toolCallId: call-1", markdown, StringComparison.Ordinal);
            Assert.Contains("{\"value\":\"alpha\"}", markdown, StringComparison.Ordinal);
            Assert.Contains("### toolResult 00", markdown, StringComparison.Ordinal);
            Assert.Contains("- status: Success", markdown, StringComparison.Ordinal);
            Assert.Contains("echo:{\"value\":\"alpha\"}", markdown, StringComparison.Ordinal);
        }
        finally {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    [Fact]
    public async Task ChatSessionMarkdownExporter_HandlesRecapAnchorAndUnresolvedRecap() {
        string repoDir = CreateTempDirectory();
        try {
            var client = new ScriptedCompletionClient("openai-chat-v1");
            client.EnqueueText(new string('A', 120));
            client.EnqueueText("keep");
            client.EnqueueText("summary");

            using (var engine = await ChatSessionEngine.CreateAsync(
                repoDir,
                new ChatSessionCreateOptions("system"),
                new ChatSessionRuntime(
                    client,
                    "openai-chat/strict",
                    "model-a",
                    new ToolRegistry(Array.Empty<ITool>()).CreateSession()
                )
            )) {
                await engine.SendMessageAsync("first");
                await engine.SendMessageAsync("second");
                var compaction = await engine.CompactAsync("compact-system", "compact-prompt");
                Assert.True(compaction.Applied);
            }

            var records = ChatSessionHistoryReader.ReadCurrent(repoDir);
            var recapRecord = Assert.Single(records, static record => record.Message is RecapMessage);
            Assert.NotNull(recapRecord.RecapSource);

            string markdown = ChatSessionMarkdownExporter.Export(records);
            Assert.Contains("- recapSource: anchored", markdown, StringComparison.Ordinal);
            Assert.Contains("- sourceStartIndex: 0", markdown, StringComparison.Ordinal);
            Assert.Contains("- compactionKind: prefix-summary", markdown, StringComparison.Ordinal);

            string skipped = ChatSessionMarkdownExporter.Export(
                records,
                new ChatSessionMarkdownExportOptions(ChatSessionMarkdownRecapMode.Skip)
            );
            Assert.DoesNotContain("summary", skipped, StringComparison.Ordinal);

            string unresolved = ChatSessionMarkdownExporter.Export(
                [
                    new ChatSessionHistoryRecord(
                        0,
                        MessageRecord.KindRecap,
                        null,
                        new RecapMessage("legacy summary"),
                        null
                    ),
                ]
            );
            Assert.Contains("- recapSource: unresolved-recap", unresolved, StringComparison.Ordinal);
        }
        finally {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    [Fact]
    public async Task ChatSession_CompactAsync_PreservesLeadingContextHeader() {
        string repoDir = CreateTempDirectory();
        try {
            var client = new ScriptedCompletionClient("openai-chat-v1");
            client.EnqueueText(new string('A', 120));
            client.EnqueueText("keep");
            client.EnqueueText("summary");

            using var engine = await ChatSessionEngine.CreateAsync(
                repoDir,
                new ChatSessionCreateOptions("system"),
                new ChatSessionRuntime(
                    client,
                    "openai-chat/strict",
                    "model-a",
                    new ToolRegistry(Array.Empty<ITool>()).CreateSession()
                )
            );

            engine.SetContextHeader(
                new ContextHeader(
                    "header-system",
                    "header-user",
                    new ActionMessage([new ActionBlock.Text("header-assistant")])
                )
            );
            await engine.SendMessageAsync("first");
            await engine.SendMessageAsync("second");

            var compaction = await engine.CompactAsync("compact-system", "compact-prompt");

            Assert.True(compaction.Applied);
            Assert.Collection(
                engine.Context,
                message => Assert.Equal("header-system", Assert.IsType<ContextHeader>(message).SystemPromptFragment),
                message => Assert.Equal("summary", Assert.IsType<RecapMessage>(message).Content),
                message => Assert.Equal("second", Assert.IsType<ObservationMessage>(message).Content),
                message => Assert.Equal("keep", Assert.IsType<ActionMessage>(message).GetFlattenedText())
            );
        }
        finally {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    [Fact]
    public async Task PopLatestTurn_ReturnsPoppedTurn_AndRemovesItFromRecentTurns() {
        string tempDir = CreateTempDirectory();
        try {
            var configPath = WriteConfig(
                tempDir,
                thresholdTokens: 200,
                users: [
                    new FamilyChatUserConfig(
                        "alice",
                        "Alice",
                        "pw1",
                        Path.Combine(tempDir, "alice-session"),
                        200,
                        "compact-system",
                        "compact-prompt",
                        "system"
                    )
                ]
            );

            var scriptFactory = new ScriptedCompletionClientFactory();
            scriptFactory.For("alice").EnqueueText("bad reply");
            scriptFactory.For("alice").EnqueueText("rerolled reply");

            await using var factory = new FamilyChatServerFactory(configPath, scriptFactory);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions {
                AllowAutoRedirect = false,
                HandleCookies = true,
            });
            await LoginAsync(client, "alice", "pw1");

            await ReadSseAsStringAsync(client, "one");
            await ReadSseAsStringAsync(client, "two");

            var popped = await PopLatestTurnAsync(client);

            Assert.NotNull(popped);
            Assert.Equal("two", popped!.Turn.UserText);
            Assert.Equal("rerolled reply", popped.Turn.Assistant.Text);

            var turns = await GetRecentTurnsAsync(client);
            Assert.NotNull(turns);
            Assert.Single(turns!);
            Assert.Equal("one", turns[0].UserText);
            Assert.Equal("bad reply", turns[0].Assistant.Text);
        }
        finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task PopLatestTurn_ThenSendSameMessage_ReplacesPreviousAssistantReply() {
        string tempDir = CreateTempDirectory();
        try {
            var configPath = WriteConfig(
                tempDir,
                thresholdTokens: 200,
                users: [
                    new FamilyChatUserConfig(
                        "alice",
                        "Alice",
                        "pw1",
                        Path.Combine(tempDir, "alice-session"),
                        200,
                        "compact-system",
                        "compact-prompt",
                        "system"
                    )
                ]
            );

            var scriptFactory = new ScriptedCompletionClientFactory();
            scriptFactory.For("alice").EnqueueText("bad reply");
            scriptFactory.For("alice").EnqueueText("fixed reply");

            await using var factory = new FamilyChatServerFactory(configPath, scriptFactory);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions {
                AllowAutoRedirect = false,
                HandleCookies = true,
            });
            await LoginAsync(client, "alice", "pw1");

            await ReadSseAsStringAsync(client, "one");

            var popped = await PopLatestTurnAsync(client);
            Assert.NotNull(popped);

            var replacement = await StartTurnAsync(client, popped!.Turn.UserText);
            string sse = await ReadTurnEventsAsStringAsync(client, replacement.TurnId);
            await WaitForCurrentTurnIdleAsync(client);

            Assert.Contains("event: done", sse, StringComparison.Ordinal);

            var turns = await GetRecentTurnsAsync(client);
            Assert.NotNull(turns);
            Assert.Single(turns!);
            Assert.Equal("one", turns[0].UserText);
            Assert.Equal("fixed reply", turns[0].Assistant.Text);
        }
        finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task PopLatestTurn_ThenSendEditedMessage_ReplacesLatestUserMessage() {
        string tempDir = CreateTempDirectory();
        try {
            var configPath = WriteConfig(
                tempDir,
                thresholdTokens: 200,
                users: [
                    new FamilyChatUserConfig(
                        "alice",
                        "Alice",
                        "pw1",
                        Path.Combine(tempDir, "alice-session"),
                        200,
                        "compact-system",
                        "compact-prompt",
                        "system"
                    )
                ]
            );

            var scriptFactory = new ScriptedCompletionClientFactory();
            scriptFactory.For("alice").EnqueueText("bad reply");
            scriptFactory.For("alice").EnqueueText("fixed reply");

            await using var factory = new FamilyChatServerFactory(configPath, scriptFactory);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions {
                AllowAutoRedirect = false,
                HandleCookies = true,
            });
            await LoginAsync(client, "alice", "pw1");

            await ReadSseAsStringAsync(client, "原始坏提示词");

            var popped = await PopLatestTurnAsync(client);
            Assert.NotNull(popped);

            var replacement = await StartTurnAsync(client, "修正后的提示词");
            string sse = await ReadTurnEventsAsStringAsync(client, replacement.TurnId);
            await WaitForCurrentTurnIdleAsync(client);

            Assert.Contains("event: done", sse, StringComparison.Ordinal);

            var turns = await GetRecentTurnsAsync(client);
            Assert.NotNull(turns);
            Assert.Single(turns!);
            Assert.Equal("修正后的提示词", turns[0].UserText);
            Assert.Equal("fixed reply", turns[0].Assistant.Text);
        }
        finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task PopLatestTurn_WhenLatestTurnIsHiddenStillReturnsLatestUserMessage() {
        string tempDir = CreateTempDirectory();
        try {
            var configPath = WriteConfig(
                tempDir,
                thresholdTokens: 200,
                users: [
                    new FamilyChatUserConfig(
                        "alice",
                        "Alice",
                        "pw1",
                        Path.Combine(tempDir, "alice-session"),
                        200,
                        "compact-system",
                        "compact-prompt",
                        "system"
                    )
                ]
            );

            var scriptFactory = new ScriptedCompletionClientFactory();
            scriptFactory.For("alice").EnqueueText("visible reply");
            scriptFactory.For("alice").Enqueue(
                async (request, observer, ct) => {
                    return new CompletionResult(
                        new ActionMessage([
                            new ActionBlock.Text(""),
                        ]),
                        new CompletionDescriptor("test", "openai-chat-v1", request.ModelId)
                    );
                }
            );

            await using var factory = new FamilyChatServerFactory(configPath, scriptFactory);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions {
                AllowAutoRedirect = false,
                HandleCookies = true,
            });
            await LoginAsync(client, "alice", "pw1");

            await ReadSseAsStringAsync(client, "one");
            await ReadSseAsStringAsync(client, "two");

            var turnsBeforePop = await GetRecentTurnsAsync(client);
            Assert.NotNull(turnsBeforePop);
            Assert.Single(turnsBeforePop!);
            Assert.Equal("one", turnsBeforePop[0].UserText);

            var popped = await PopLatestTurnAsync(client);

            Assert.Equal("two", popped.Turn.UserText);
            Assert.Equal(string.Empty, popped.Turn.Assistant.Text);

            var turnsAfterPop = await GetRecentTurnsAsync(client);
            Assert.NotNull(turnsAfterPop);
            Assert.Single(turnsAfterPop!);
            Assert.Equal("one", turnsAfterPop[0].UserText);
        }
        finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RecentTurns_WhenOnlyRecapRemains_StillShowsRecapEntry() {
        string tempDir = CreateTempDirectory();
        try {
            string sessionDir = Path.Combine(tempDir, "alice-session");

            var setupClient = new ScriptedCompletionClient("openai-chat-v1");
            setupClient.EnqueueText(new string('A', 80));
            setupClient.EnqueueText("keep");
            setupClient.EnqueueText("summary");

            using (var engine = await ChatSessionEngine.CreateAsync(
                sessionDir,
                new ChatSessionCreateOptions("system"),
                new ChatSessionRuntime(
                    setupClient,
                    "openai-chat/strict",
                    "model-a",
                    new ToolRegistry(Array.Empty<ITool>()).CreateSession()
                )
            )) {
                await engine.SendMessageAsync("first");
                await engine.SendMessageAsync("second");
                var compaction = await engine.CompactAsync("compact-system", "compact-prompt");
                Assert.True(compaction.Applied);
            }

            var configPath = WriteConfig(
                tempDir,
                thresholdTokens: 200,
                users: [
                    new FamilyChatUserConfig(
                        "alice",
                        "Alice",
                        "pw1",
                        sessionDir,
                        200,
                        "compact-system",
                        "compact-prompt",
                        "system"
                    )
                ]
            );

            await using var factory = new FamilyChatServerFactory(configPath, new ScriptedCompletionClientFactory());
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions {
                AllowAutoRedirect = false,
                HandleCookies = true,
            });
            await LoginAsync(client, "alice", "pw1");

            var beforePop = await GetRecentTurnsResponseAsync(client);
            Assert.Equal(2, beforePop.Turns.Count);
            Assert.Equal("second", beforePop.Turns[0].UserText);
            Assert.True(beforePop.Turns[1].IsRecap);
            Assert.Equal("summary", beforePop.Turns[1].Assistant.Text);

            var popped = await PopLatestTurnAsync(client);
            Assert.Equal("second", popped.Turn.UserText);

            var afterPop = await GetRecentTurnsResponseAsync(client);
            Assert.Single(afterPop.Turns);
            Assert.True(afterPop.Turns[0].IsRecap);
            Assert.Equal("summary", afterPop.Turns[0].Assistant.Text);
        }
        finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task BuildRecentTurnsResponse_WhenRecapFallsOutsideMaxTurns_StillIncludesRecapAndBoundaryTurn() {
        string repoDir = CreateTempDirectory();
        try {
            var client = new ScriptedCompletionClient("openai-chat-v1");
            client.EnqueueText(new string('A', 120));
            client.EnqueueText("reply-second");
            client.EnqueueText("summary");
            client.EnqueueText("reply-third");
            client.EnqueueText("reply-fourth");
            client.EnqueueText("reply-fifth");

            using var engine = await ChatSessionEngine.CreateAsync(
                repoDir,
                new ChatSessionCreateOptions("system"),
                new ChatSessionRuntime(
                    client,
                    "openai-chat/strict",
                    "model-a",
                    new ToolRegistry(Array.Empty<ITool>()).CreateSession()
                )
            );

            await engine.SendMessageAsync("first");
            await engine.SendMessageAsync("second");
            var compaction = await engine.CompactAsync("compact-system", "compact-prompt");
            Assert.True(compaction.Applied);
            await engine.SendMessageAsync("third");
            await engine.SendMessageAsync("fourth");
            await engine.SendMessageAsync("fifth");

            var scriptedFactory = new ScriptedCompletionClientFactory();
            var hostConfig = new FamilyChatConfig(
                [],
                [
                    new FamilyChatConnectionConfig(
                        "test",
                        "Test",
                        "openai-chat",
                        "model-a",
                        "openai-chat/strict",
                        "http://localhost:8000/",
                        ApiKey: "test-key"
                    )
                ],
                "test"
            );
            var hostService = new FamilyChatHostService(
                hostConfig,
                new FamilyChatConnectionRegistry(hostConfig, scriptedFactory),
                DisabledFamilyChatUserMessageNormalizer.Instance
            );

            var response = hostService.BuildRecentTurnsResponse(engine, maxTurns: 2);

            Assert.Collection(
                response.Turns,
                turn => {
                    Assert.Equal("fifth", turn.UserText);
                    Assert.Equal("reply-fifth", turn.Assistant.Text);
                    Assert.False(turn.IsRecap);
                },
                turn => {
                    Assert.Equal("fourth", turn.UserText);
                    Assert.Equal("reply-fourth", turn.Assistant.Text);
                    Assert.False(turn.IsRecap);
                },
                turn => {
                    Assert.Equal("second", turn.UserText);
                    Assert.Equal("reply-second", turn.Assistant.Text);
                    Assert.False(turn.IsRecap);
                },
                turn => {
                    Assert.True(turn.IsRecap);
                    Assert.Equal("summary", turn.Assistant.Text);
                }
            );
        }
        finally {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    [Fact]
    public async Task BuildRecentTurnsResponse_WhenBoundaryTurnAlreadyVisible_DoesNotDuplicateIt() {
        string repoDir = CreateTempDirectory();
        try {
            var client = new ScriptedCompletionClient("openai-chat-v1");
            client.EnqueueText(new string('A', 120));
            client.EnqueueText("reply-second");
            client.EnqueueText("summary");
            client.EnqueueText("reply-third");
            client.EnqueueText("reply-fourth");
            client.EnqueueText("reply-fifth");

            using var engine = await ChatSessionEngine.CreateAsync(
                repoDir,
                new ChatSessionCreateOptions("system"),
                new ChatSessionRuntime(
                    client,
                    "openai-chat/strict",
                    "model-a",
                    new ToolRegistry(Array.Empty<ITool>()).CreateSession()
                )
            );

            await engine.SendMessageAsync("first");
            await engine.SendMessageAsync("second");
            var compaction = await engine.CompactAsync("compact-system", "compact-prompt");
            Assert.True(compaction.Applied);
            await engine.SendMessageAsync("third");
            await engine.SendMessageAsync("fourth");
            await engine.SendMessageAsync("fifth");

            var scriptedFactory2 = new ScriptedCompletionClientFactory();
            var hostConfig2 = new FamilyChatConfig(
                [],
                [
                    new FamilyChatConnectionConfig(
                        "test",
                        "Test",
                        "openai-chat",
                        "model-a",
                        "openai-chat/strict",
                        "http://localhost:8000/",
                        ApiKey: "test-key"
                    )
                ],
                "test"
            );
            var hostService = new FamilyChatHostService(
                hostConfig2,
                new FamilyChatConnectionRegistry(hostConfig2, scriptedFactory2),
                DisabledFamilyChatUserMessageNormalizer.Instance
            );

            var response = hostService.BuildRecentTurnsResponse(engine, maxTurns: 4);

            Assert.Collection(
                response.Turns,
                turn => Assert.Equal("fifth", turn.UserText),
                turn => Assert.Equal("fourth", turn.UserText),
                turn => Assert.Equal("third", turn.UserText),
                turn => Assert.Equal("second", turn.UserText),
                turn => Assert.True(turn.IsRecap)
            );
            Assert.Single(response.Turns, static turn => turn.UserText == "second");
        }
        finally {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    [Fact]
    public async Task PopLatestTurn_WithoutCompletedTurn_Returns409() {
        string tempDir = CreateTempDirectory();
        try {
            var configPath = WriteConfig(
                tempDir,
                thresholdTokens: 200,
                users: [
                    new FamilyChatUserConfig(
                        "alice",
                        "Alice",
                        "pw1",
                        Path.Combine(tempDir, "alice-session"),
                        200,
                        "compact-system",
                        "compact-prompt",
                        "system"
                    )
                ]
            );

            await using var factory = new FamilyChatServerFactory(configPath, new ScriptedCompletionClientFactory());
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions {
                AllowAutoRedirect = false,
                HandleCookies = true,
            });
            await LoginAsync(client, "alice", "pw1");

            using var response = await client.PostAsync("/api/chat/turns/pop-latest", content: null);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<StartTurnResponseDto>();
            Assert.NotNull(payload);
            Assert.Equal("idle", payload!.Status);
            Assert.Contains("当前没有可取出", payload.Error, StringComparison.Ordinal);
        }
        finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Login_WithWrongPassword_Returns401() {
        string tempDir = CreateTempDirectory();
        try {
            var configPath = WriteConfig(
                tempDir,
                thresholdTokens: 200,
                users: [
                    new FamilyChatUserConfig(
                        "alice",
                        "Alice",
                        "pw1",
                        Path.Combine(tempDir, "alice-session"),
                        200,
                        "compact-system",
                        "compact-prompt",
                        "system"
                    )
                ]
            );

            await using var factory = new FamilyChatServerFactory(configPath, new ScriptedCompletionClientFactory());
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var response = await client.PostAsync(
                "/login",
                new FormUrlEncodedContent(new Dictionary<string, string> {
                    ["userId"] = "alice",
                    ["password"] = "wrong",
                })
            );

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            string html = await response.Content.ReadAsStringAsync();
            Assert.Contains("用户名或密码不正确", html, StringComparison.Ordinal);
        }
        finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void MissingConfig_GeneratesTemplate_AndFailsWithGuidance() {
        string tempDir = CreateTempDirectory();
        try {
            string configPath = Path.Combine(tempDir, ".atelia", "family-chat", "config.json");

            using var factory = new FamilyChatServerFactory(configPath, new ScriptedCompletionClientFactory());
            var exception = Record.Exception(() => factory.CreateClient());

            Assert.NotNull(exception);
            Assert.True(File.Exists(configPath));

            string configDir = Path.GetDirectoryName(configPath)!;
            string connectionsPath = Path.Combine(configDir, "connections.json");
            Assert.True(File.Exists(connectionsPath));

            string details = FlattenExceptionMessages(exception!);
            Assert.Contains("modelId", details, StringComparison.Ordinal);
            Assert.Contains("listenUrls", details, StringComparison.Ordinal);
            Assert.Contains("passwords", details, StringComparison.Ordinal);

            var generatedUsers = ReadUsersFile(configPath);
            Assert.Equal("http://0.0.0.0:3510", Assert.Single(generatedUsers.ListenUrls!));
            Assert.Equal(2, generatedUsers.Users.Count);
            Assert.Equal("alice", generatedUsers.Users[0].UserId);
            Assert.Equal("alice123", generatedUsers.Users[0].Password);
            Assert.Equal(".atelia/family-chat/sessions/alice", generatedUsers.Users[0].SessionDir);

            var generatedConnections = ReadConnectionsFile(connectionsPath);
            Assert.Equal("local", generatedConnections.DefaultConnectionId);
            var connection = Assert.Single(generatedConnections.Connections);
            Assert.Equal("local", connection.Id);
            Assert.Equal("REPLACE_WITH_YOUR_LOCAL_MODEL_ID", connection.ModelId);
        }
        finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task GeneratedTemplate_AfterModelIdUpdate_CanLoginAndChat() {
        string tempDir = CreateTempDirectory();
        try {
            string configPath = BootstrapTemplate(tempDir);
            string configDir = Path.GetDirectoryName(configPath)!;
            string connectionsPath = Path.Combine(configDir, "connections.json");

            var usersConfig = ReadUsersFile(configPath);
            var updatedUsers = usersConfig with {
                Users = usersConfig.Users
                    .Select(
                        user => user with {
                            SessionDir = Path.Combine(tempDir, "generated-sessions", user.UserId)
                        }
                    )
                    .ToArray()
            };
            File.WriteAllText(configPath, JsonSerializer.Serialize(updatedUsers, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

            var connectionsConfig = ReadConnectionsFile(connectionsPath);
            var updatedConnections = connectionsConfig with {
                Connections = connectionsConfig.Connections
                    .Select(conn => conn with { ModelId = "model-a" })
                    .ToArray()
            };
            File.WriteAllText(connectionsPath, JsonSerializer.Serialize(updatedConnections, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

            var scriptFactory = new ScriptedCompletionClientFactory();
            scriptFactory.For("alice").EnqueueText("hello from template");

            await using var factory = new FamilyChatServerFactory(configPath, scriptFactory);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions {
                AllowAutoRedirect = false,
                HandleCookies = true,
            });

            await LoginAsync(client, "alice", "alice123");
            string sse = await ReadSseAsStringAsync(client, "hi");

            Assert.Contains("event: done", sse, StringComparison.Ordinal);

            var turns = await GetRecentTurnsAsync(client);
            Assert.NotNull(turns);
            Assert.Single(turns!);
            Assert.Equal("hi", turns[0].UserText);
            Assert.Equal("hello from template", turns[0].Assistant.Text);
        }
        finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ConfigValidation_RejectsBlankPassword_WithoutOverwritingFile() {
        string tempDir = CreateTempDirectory();
        try {
            string configPath = WriteConfig(
                tempDir,
                thresholdTokens: 200,
                users: [
                    new FamilyChatUserConfig(
                        "alice",
                        "Alice",
                        "",
                        Path.Combine(tempDir, "alice-session"),
                        200,
                        "compact-system",
                        "compact-prompt",
                        "system"
                    )
                ]
            );

            string before = File.ReadAllText(configPath);
            using var factory = new FamilyChatServerFactory(configPath, new ScriptedCompletionClientFactory());

            var exception = Record.Exception(() => factory.CreateClient());

            Assert.NotNull(exception);
            Assert.Contains("non-empty password", FlattenExceptionMessages(exception!), StringComparison.Ordinal);
            Assert.Equal(before, File.ReadAllText(configPath));
        }
        finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ConfigValidation_RejectsDuplicateUserId() {
        string tempDir = CreateTempDirectory();
        try {
            string configPath = WriteConfig(
                tempDir,
                thresholdTokens: 200,
                users: [
                    new FamilyChatUserConfig(
                        "alice",
                        "Alice",
                        "pw1",
                        Path.Combine(tempDir, "alice-session"),
                        200,
                        "compact-system",
                        "compact-prompt",
                        "system"
                    ),
                    new FamilyChatUserConfig(
                        "alice",
                        "Alice 2",
                        "pw2",
                        Path.Combine(tempDir, "alice-session-2"),
                        200,
                        "compact-system",
                        "compact-prompt",
                        "system"
                    )
                ]
            );

            using var factory = new FamilyChatServerFactory(configPath, new ScriptedCompletionClientFactory());
            var exception = Record.Exception(() => factory.CreateClient());

            Assert.NotNull(exception);
            Assert.Contains("duplicate userId 'alice'", FlattenExceptionMessages(exception!), StringComparison.Ordinal);
        }
        finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Readme_JsonSample_MatchesGeneratedTemplate() {
        string tempDir = CreateTempDirectory();
        try {
            string configPath = BootstrapTemplate(tempDir);
            string configDir = Path.GetDirectoryName(configPath)!;
            string connectionsPath = Path.Combine(configDir, "connections.json");

            var generatedUsers = ReadUsersFile(configPath);
            var generatedConnections = ReadConnectionsFile(connectionsPath);

            string readmePath = Path.Combine("/repos/focus/atelia", "prototypes", "FamilyChat.Server", "README.md");
            string readme = File.ReadAllText(readmePath);
            var jsonBlocks = Regex.Matches(readme, "```json\\s*(.*?)\\s*```", RegexOptions.Singleline)
                .Select(m => m.Groups[1].Value)
                .ToArray();

            Assert.True(jsonBlocks.Length >= 2, "README should contain at least two JSON code blocks.");

            var documentedUsers = JsonSerializer.Deserialize<FamilyChatUsersFileConfig>(jsonBlocks[0], new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var documentedConnections = JsonSerializer.Deserialize<FamilyChatConnectionsFileConfig>(jsonBlocks[1], new JsonSerializerOptions(JsonSerializerDefaults.Web));

            Assert.NotNull(documentedUsers);
            Assert.NotNull(documentedConnections);
            Assert.Equal(generatedUsers.ListenUrls, documentedUsers!.ListenUrls);
            Assert.Equal(generatedUsers.Users, documentedUsers.Users);
            Assert.Equal(generatedConnections.Connections, documentedConnections!.Connections);
            Assert.Equal(generatedConnections.DefaultConnectionId, documentedConnections.DefaultConnectionId);

            Assert.Contains("dotnet run --project prototypes/FamilyChat.Server", readme, StringComparison.Ordinal);
            Assert.Contains("http://<你的局域网IP>:3510", readme, StringComparison.Ordinal);
            Assert.Contains(".atelia/family-chat/config.json", readme, StringComparison.Ordinal);
        }
        finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SameUserConcurrentSend_Returns409() {
        string tempDir = CreateTempDirectory();
        try {
            var configPath = WriteConfig(
                tempDir,
                thresholdTokens: 200,
                users: [
                    new FamilyChatUserConfig(
                        "alice",
                        "Alice",
                        "pw1",
                        Path.Combine(tempDir, "alice-session"),
                        200,
                        "compact-system",
                        "compact-prompt",
                        "system"
                    )
                ]
            );

            var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var scriptFactory = new ScriptedCompletionClientFactory();
            scriptFactory.For("alice").Enqueue(
                async (request, observer, ct) => {
                    blocker.TrySetResult();
                    await release.Task.WaitAsync(ct);
                    observer?.OnTextDelta("slow");
                    return new CompletionResult(
                        new ActionMessage([new ActionBlock.Text("slow")]),
                        new CompletionDescriptor("test", "openai-chat-v1", request.ModelId)
                    );
                }
            );

            await using var factory = new FamilyChatServerFactory(configPath, scriptFactory);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions {
                AllowAutoRedirect = false,
                HandleCookies = true,
            });
            await LoginAsync(client, "alice", "pw1");

            var first = await StartTurnAsync(client, "one");

            await blocker.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var second = await client.PostAsJsonAsync("/api/chat/turns", new ChatStreamRequest("two"));
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
            var secondPayload = await second.Content.ReadFromJsonAsync<StartTurnResponseDto>();
            Assert.NotNull(secondPayload);
            Assert.Equal(first.TurnId, secondPayload!.TurnId);

            release.TrySetResult();
            string sse = await ReadTurnEventsAsStringAsync(client, first.TurnId);
            Assert.Contains("event: done", sse, StringComparison.Ordinal);
        }
        finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task IncompleteCompletion_ReturnsError_AndDoesNotCreateRecentTurn() {
        string tempDir = CreateTempDirectory();
        try {
            var configPath = WriteConfig(
                tempDir,
                thresholdTokens: 200,
                users: [
                    new FamilyChatUserConfig(
                        "alice",
                        "Alice",
                        "pw1",
                        Path.Combine(tempDir, "alice-session"),
                        200,
                        "compact-system",
                        "compact-prompt",
                        "system"
                    )
                ]
            );

            var scriptFactory = new ScriptedCompletionClientFactory();
            scriptFactory.For("alice").Enqueue(
                async (request, observer, ct) => {
                    observer?.OnTextDelta("<think>broken");
                    return new CompletionResult(
                        new ActionMessage([
                            new ActionBlock.Text("<think>broken"),
                        ]),
                        new CompletionDescriptor("test", "openai-chat-v1", request.ModelId),
                        termination: CompletionTermination.Incomplete("length", "scripted truncation")
                    );
                }
            );

            await using var factory = new FamilyChatServerFactory(configPath, scriptFactory);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions {
                AllowAutoRedirect = false,
                HandleCookies = true,
            });
            await LoginAsync(client, "alice", "pw1");

            string sse = await ReadSseAsStringAsync(client, "one");

            Assert.Contains("event: error", sse, StringComparison.Ordinal);
            Assert.DoesNotContain("event: done", sse, StringComparison.Ordinal);

            var turns = await GetRecentTurnsAsync(client);
            Assert.NotNull(turns);
            Assert.Empty(turns!);
        }
        finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task DisconnectingSse_DoesNotCancelBackendTurn() {
        string tempDir = CreateTempDirectory();
        try {
            var configPath = WriteConfig(
                tempDir,
                thresholdTokens: 200,
                users: [
                    new FamilyChatUserConfig(
                        "alice",
                        "Alice",
                        "pw1",
                        Path.Combine(tempDir, "alice-session"),
                        200,
                        "compact-system",
                        "compact-prompt",
                        "system"
                    )
                ]
            );

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled = false;
            var scriptFactory = new ScriptedCompletionClientFactory();
            scriptFactory.For("alice").Enqueue(
                async (request, observer, ct) => {
                    observer?.OnTextDelta("partial");
                    started.TrySetResult();
                    try {
                        await release.Task.WaitAsync(ct);
                    }
                    catch (OperationCanceledException) {
                        cancelled = true;
                        throw;
                    }

                    observer?.OnTextDelta(" done");
                    return new CompletionResult(
                        new ActionMessage([new ActionBlock.Text("partial done")]),
                        new CompletionDescriptor("test", "openai-chat-v1", request.ModelId)
                    );
                }
            );

            await using var factory = new FamilyChatServerFactory(configPath, scriptFactory);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions {
                AllowAutoRedirect = false,
                HandleCookies = true,
            });
            await LoginAsync(client, "alice", "pw1");

            var start = await StartTurnAsync(client, "one");
            using (var response = await OpenTurnEventsAsync(client, start.TurnId)) {
                string partial = await ReadUntilContainsAsync(response, "\"delta\":\"partial\"");
                Assert.Contains("\"delta\":\"partial\"", partial, StringComparison.Ordinal);
            }

            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            release.TrySetResult();
            await WaitForRecentTurnCountAsync(client, 1);

            Assert.False(cancelled);
            var turns = await GetRecentTurnsAsync(client);
            Assert.NotNull(turns);
            Assert.Single(turns!);
            Assert.Equal("partial done", turns[0].Assistant.Text);
        }
        finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ReconnectingEvents_ReplaysExistingDeltas_AndStreamsNewOnes() {
        string tempDir = CreateTempDirectory();
        try {
            var configPath = WriteConfig(
                tempDir,
                thresholdTokens: 200,
                users: [
                    new FamilyChatUserConfig(
                        "alice",
                        "Alice",
                        "pw1",
                        Path.Combine(tempDir, "alice-session"),
                        200,
                        "compact-system",
                        "compact-prompt",
                        "system"
                    )
                ]
            );

            var firstDeltaSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var scriptFactory = new ScriptedCompletionClientFactory();
            scriptFactory.For("alice").Enqueue(
                async (request, observer, ct) => {
                    observer?.OnTextDelta("alpha");
                    firstDeltaSeen.TrySetResult();
                    await release.Task.WaitAsync(ct);
                    observer?.OnTextDelta("beta");
                    return new CompletionResult(
                        new ActionMessage([new ActionBlock.Text("alphabeta")]),
                        new CompletionDescriptor("test", "openai-chat-v1", request.ModelId)
                    );
                }
            );

            await using var factory = new FamilyChatServerFactory(configPath, scriptFactory);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions {
                AllowAutoRedirect = false,
                HandleCookies = true,
            });
            await LoginAsync(client, "alice", "pw1");

            var start = await StartTurnAsync(client, "one");
            using (var firstResponse = await OpenTurnEventsAsync(client, start.TurnId)) {
                string partial = await ReadUntilContainsAsync(firstResponse, "\"delta\":\"alpha\"");
                Assert.Contains("\"delta\":\"alpha\"", partial, StringComparison.Ordinal);
            }

            await firstDeltaSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var secondResponseTask = OpenTurnEventsAsync(client, start.TurnId);
            release.TrySetResult();
            using var secondResponse = await secondResponseTask;
            string sse = await secondResponse.Content.ReadAsStringAsync();

            Assert.Contains("\"delta\":\"alpha\"", sse, StringComparison.Ordinal);
            Assert.Contains("\"delta\":\"beta\"", sse, StringComparison.Ordinal);
            Assert.Contains("event: done", sse, StringComparison.Ordinal);
        }
        finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task InlineThinkTagsSplitAcrossDeltas_AreHiddenFromStreamAndRecentTurns() {
        string tempDir = CreateTempDirectory();
        try {
            var configPath = WriteConfig(
                tempDir,
                thresholdTokens: 200,
                users: [
                    new FamilyChatUserConfig(
                        "alice",
                        "Alice",
                        "pw1",
                        Path.Combine(tempDir, "alice-session"),
                        200,
                        "compact-system",
                        "compact-prompt",
                        "system"
                    )
                ]
            );

            var scriptFactory = new ScriptedCompletionClientFactory();
            scriptFactory.For("alice").Enqueue(
                async (request, observer, ct) => {
                    observer?.OnTextDelta("before ");
                    observer?.OnTextDelta("<th");
                    observer?.OnTextDelta("ink>secret");
                    observer?.OnTextDelta("</thi");
                    observer?.OnTextDelta("nk>after");
                    return new CompletionResult(
                        new ActionMessage([
                            new ActionBlock.Text("before <think>secret</think>after"),
                        ]),
                        new CompletionDescriptor("test", "openai-chat-v1", request.ModelId)
                    );
                }
            );

            await using var factory = new FamilyChatServerFactory(configPath, scriptFactory);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions {
                AllowAutoRedirect = false,
                HandleCookies = true,
            });
            await LoginAsync(client, "alice", "pw1");

            string sse = await ReadSseAsStringAsync(client, "one");

            Assert.Contains("\"delta\":\"before \"", sse, StringComparison.Ordinal);
            Assert.Contains("\"delta\":\"after\"", sse, StringComparison.Ordinal);
            Assert.DoesNotContain("secret", sse, StringComparison.Ordinal);
            Assert.DoesNotContain("<think>", sse, StringComparison.Ordinal);

            var turns = await GetRecentTurnsAsync(client);
            Assert.NotNull(turns);
            Assert.Single(turns!);
            Assert.Equal("before after", turns[0].Assistant.Text);
        }
        finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task CurrentTurnEndpoint_ReportsRunningTurn() {
        string tempDir = CreateTempDirectory();
        try {
            var configPath = WriteConfig(
                tempDir,
                thresholdTokens: 200,
                users: [
                    new FamilyChatUserConfig(
                        "alice",
                        "Alice",
                        "pw1",
                        Path.Combine(tempDir, "alice-session"),
                        200,
                        "compact-system",
                        "compact-prompt",
                        "system"
                    )
                ]
            );

            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var scriptFactory = new ScriptedCompletionClientFactory();
            scriptFactory.For("alice").Enqueue(
                async (request, observer, ct) => {
                    observer?.OnTextDelta("working");
                    await release.Task.WaitAsync(ct);
                    return new CompletionResult(
                        new ActionMessage([new ActionBlock.Text("working")]),
                        new CompletionDescriptor("test", "openai-chat-v1", request.ModelId)
                    );
                }
            );

            await using var factory = new FamilyChatServerFactory(configPath, scriptFactory);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions {
                AllowAutoRedirect = false,
                HandleCookies = true,
            });
            await LoginAsync(client, "alice", "pw1");

            var started = await StartTurnAsync(client, "one");
            var current = await client.GetFromJsonAsync<CurrentTurnDto>("/api/chat/turns/current");

            Assert.NotNull(current);
            Assert.Equal("running", current!.Status);
            Assert.Equal(started.TurnId, current.TurnId);
            Assert.Equal("one", current.UserMessage);

            release.TrySetResult();
            await WaitForRecentTurnCountAsync(client, 1);

            current = await client.GetFromJsonAsync<CurrentTurnDto>("/api/chat/turns/current");
            Assert.NotNull(current);
            Assert.Equal("idle", current!.Status);
        }
        finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task StopTurnEndpoint_StopsRunningTurn_AndDoesNotPersistPartialTurn() {
        string tempDir = CreateTempDirectory();
        try {
            var configPath = WriteConfig(
                tempDir,
                thresholdTokens: 200,
                users: [
                    new FamilyChatUserConfig(
                        "alice",
                        "Alice",
                        "pw1",
                        Path.Combine(tempDir, "alice-session"),
                        200,
                        "compact-system",
                        "compact-prompt",
                        "system"
                    )
                ]
            );

            var startedStreaming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var scriptFactory = new ScriptedCompletionClientFactory();
            scriptFactory.For("alice").Enqueue(
                async (request, observer, ct) => {
                    observer?.OnTextDelta("partial");
                    startedStreaming.TrySetResult();

                    while (observer?.ShouldStop != true) {
                        await Task.Delay(20, ct);
                    }

                    return new CompletionResult(
                        new ActionMessage([new ActionBlock.Text("partial")]),
                        new CompletionDescriptor("test", "openai-chat-v1", request.ModelId),
                        termination: CompletionTermination.Incomplete(
                            detail: "Streaming observer stopped scripted completion early."
                        )
                    );
                }
            );

            await using var factory = new FamilyChatServerFactory(configPath, scriptFactory);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions {
                AllowAutoRedirect = false,
                HandleCookies = true,
            });
            await LoginAsync(client, "alice", "pw1");

            var started = await StartTurnAsync(client, "one");
            await startedStreaming.Task.WaitAsync(TimeSpan.FromSeconds(5));

            using var stopResponse = await client.PostAsync($"/api/chat/turns/{started.TurnId}/stop", content: null);
            Assert.Equal(HttpStatusCode.OK, stopResponse.StatusCode);

            await WaitForCurrentTurnIdleAsync(client);
            var turns = await GetRecentTurnsAsync(client);
            Assert.Empty(turns);
        }
        finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static async Task LoginAsync(HttpClient client, string userId, string password) {
        var response = await client.PostAsync(
            "/login",
            new FormUrlEncodedContent(new Dictionary<string, string> {
                ["userId"] = userId,
                ["password"] = password,
            })
        );
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task<string> ReadSseAsStringAsync(HttpClient client, string message) {
        var started = await StartTurnAsync(client, message);
        string sse = await ReadTurnEventsAsStringAsync(client, started.TurnId);
        await WaitForCurrentTurnIdleAsync(client);
        return sse;
    }

    private static async Task<StartTurnResponseDto> StartTurnAsync(
        HttpClient client,
        string message,
        bool? autoPrefillThinkOpenTag = null
    ) {
        using var response = await client.PostAsJsonAsync(
            "/api/chat/turns",
            new ChatStreamRequest(message, autoPrefillThinkOpenTag, ConnectionId: null)
        );
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var started = await response.Content.ReadFromJsonAsync<StartTurnResponseDto>();
        Assert.NotNull(started);
        Assert.False(string.IsNullOrWhiteSpace(started!.TurnId));
        return started!;
    }

    private static async Task<PopLatestTurnResponseDto> PopLatestTurnAsync(HttpClient client) {
        using var response = await client.PostAsync("/api/chat/turns/pop-latest", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var popped = await response.Content.ReadFromJsonAsync<PopLatestTurnResponseDto>();
        Assert.NotNull(popped);
        return popped!;
    }

    private static async Task<RecentTurnsResponseDto> GetRecentTurnsResponseAsync(HttpClient client) {
        var response = await client.GetFromJsonAsync<RecentTurnsResponseDto>("/api/recent-turns");
        Assert.NotNull(response);
        Assert.NotNull(response!.Turns);
        return response!;
    }

    private static async Task<IReadOnlyList<RecentTurnDto>> GetRecentTurnsAsync(HttpClient client) {
        var response = await GetRecentTurnsResponseAsync(client);
        return response.Turns;
    }

    private static async Task<HttpResponseMessage> OpenTurnEventsAsync(HttpClient client, string turnId) {
        var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/api/chat/turns/{turnId}/events"),
            HttpCompletionOption.ResponseHeadersRead
        );
        response.EnsureSuccessStatusCode();
        return response;
    }

    private static async Task<string> ReadTurnEventsAsStringAsync(HttpClient client, string turnId) {
        using var response = await OpenTurnEventsAsync(client, turnId);
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<string> ReadUntilContainsAsync(HttpResponseMessage response, string expected) {
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        var buffer = new char[256];
        var builder = new StringBuilder();
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        while (!builder.ToString().Contains(expected, StringComparison.Ordinal)) {
            int read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), timeoutCts.Token);
            if (read == 0) {
                break;
            }

            builder.Append(buffer, 0, read);
        }

        return builder.ToString();
    }

    private static async Task WaitForRecentTurnCountAsync(HttpClient client, int expectedCount) {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline) {
            var turns = await GetRecentTurnsAsync(client);
            if (turns?.Count == expectedCount) {
                return;
            }

            await Task.Delay(50);
        }

        var latest = await GetRecentTurnsAsync(client);
        Assert.Equal(expectedCount, latest?.Count ?? 0);
    }

    private static async Task WaitForCurrentTurnIdleAsync(HttpClient client) {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline) {
            var current = await client.GetFromJsonAsync<CurrentTurnDto>("/api/chat/turns/current");
            if (current?.Status == "idle") {
                return;
            }

            await Task.Delay(50);
        }

        var latest = await client.GetFromJsonAsync<CurrentTurnDto>("/api/chat/turns/current");
        Assert.Equal("idle", latest?.Status);
    }

    private static async Task SeedSessionAsync(
        string sessionDir,
        string modelId,
        string completionSurfaceId,
        IReadOnlyList<(string UserMessage, string AssistantText)> turns
    ) {
        var scriptedClient = new ScriptedCompletionClient("openai-chat-v1");
        foreach (var turn in turns) {
            scriptedClient.EnqueueText(turn.AssistantText);
        }

        var runtime = new ChatSessionRuntime(
            scriptedClient,
            completionSurfaceId,
            modelId,
            new ToolRegistry(Array.Empty<ITool>()).CreateSession()
        );
        var engine = await ChatSessionEngine.CreateAsync(
            sessionDir,
            new ChatSessionCreateOptions("system"),
            runtime
        );

        try {
            foreach (var turn in turns) {
                await engine.SendMessageAsync(turn.UserMessage);
            }
        }
        finally {
            engine.Dispose();
        }
    }

    private static string CreateTempDirectory() {
        string path = Path.Combine(Path.GetTempPath(), "familychat-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string WriteConfig(
        string tempDir,
        ulong thresholdTokens,
        IReadOnlyList<FamilyChatUserConfig> users,
        string connectionModelId = "model-a",
        string connectionSurfaceId = "openai-chat/strict"
    ) {
        string configDir = Path.Combine(tempDir, ".atelia", "family-chat");
        Directory.CreateDirectory(configDir);

        var usersConfig = new FamilyChatUsersFileConfig(users, ["http://localhost:3510"]);
        string usersPath = Path.Combine(configDir, "config.json");
        File.WriteAllText(usersPath, JsonSerializer.Serialize(usersConfig, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var connectionsConfig = new FamilyChatConnectionsFileConfig(
            [
                new FamilyChatConnectionConfig(
                    "test",
                    "Test",
                    "openai-chat",
                    connectionModelId,
                    connectionSurfaceId,
                    "http://localhost:8000/",
                    ApiKey: "test-key"
                )
            ],
            "test"
        );
        string connectionsPath = Path.Combine(configDir, "connections.json");
        File.WriteAllText(connectionsPath, JsonSerializer.Serialize(connectionsConfig, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        return usersPath;
    }





    private static FamilyChatUsersFileConfig ReadUsersFile(string path) {
        var config = JsonSerializer.Deserialize<FamilyChatUsersFileConfig>(
            File.ReadAllText(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        );
        Assert.NotNull(config);
        return config!;
    }

    private static FamilyChatConnectionsFileConfig ReadConnectionsFile(string path) {
        var config = JsonSerializer.Deserialize<FamilyChatConnectionsFileConfig>(
            File.ReadAllText(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        );
        Assert.NotNull(config);
        return config!;
    }

    private static string BootstrapTemplate(string tempDir) {
        string configPath = Path.Combine(tempDir, ".atelia", "family-chat", "config.json");
        using var factory = new FamilyChatServerFactory(configPath, new ScriptedCompletionClientFactory());
        var exception = Record.Exception(() => factory.CreateClient());
        Assert.NotNull(exception);
        Assert.True(File.Exists(configPath));
        return configPath;
    }

    private static string FlattenExceptionMessages(Exception exception) {
        var parts = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException) {
            parts.Add(current.Message);
        }

        return string.Join(Environment.NewLine, parts);
    }



    private sealed class FamilyChatServerFactory(
        string configPath,
        ScriptedCompletionClientFactory scriptedFactory,
        IFamilyChatUserMessageNormalizer? userMessageNormalizer = null
    ) : WebApplicationFactory<Program> {
        protected override void ConfigureWebHost(IWebHostBuilder builder) {
            builder.UseSetting("FamilyChat:ConfigPath", configPath);
            builder.ConfigureTestServices(
                services => {
                    services.AddSingleton<IFamilyChatCompletionClientFactory>(scriptedFactory);
                    services.AddSingleton<IFamilyChatUserMessageNormalizer>(
                        userMessageNormalizer ?? DisabledFamilyChatUserMessageNormalizer.Instance
                    );
                }
            );
        }
    }

    private sealed class ScriptedCompletionClientFactory : IFamilyChatCompletionClientFactory {
        private readonly ScriptedCompletionClient _sharedClient = new ScriptedCompletionClient("openai-chat-v1");

        public ScriptedCompletionClient For(string userId) {
            return _sharedClient;
        }

        public ICompletionClient Create(FamilyChatConnectionConfig connection)
            => _sharedClient;
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

            var next = _responses.Dequeue();
            return next(request, observer, cancellationToken);
        }
    }

    private sealed class ScriptedUserMessageNormalizer : IFamilyChatUserMessageNormalizer {
        private readonly Queue<Func<string, CancellationToken, Task<string>>> _responses = new();

        public bool ShouldNormalize(string userMessage) => true;

        public void Enqueue(string normalizedText) {
            _responses.Enqueue(
                (userMessage, ct) => Task.FromResult(normalizedText)
            );
        }

        public ValueTask<string> NormalizeAsync(string userMessage, CancellationToken ct) {
            if (_responses.Count == 0) {
                throw new InvalidOperationException("No scripted user-message-normalizer response remaining.");
            }

            var next = _responses.Dequeue();
            return new ValueTask<string>(next(userMessage, ct));
        }
    }

    private sealed class ThrowingUserMessageNormalizer : IFamilyChatUserMessageNormalizer {
        public bool ShouldNormalize(string userMessage) => true;

        public ValueTask<string> NormalizeAsync(string userMessage, CancellationToken ct) {
            throw new InvalidOperationException("scripted normalizer failure");
        }
    }

    private sealed class EchoTool : ITool {
        public ToolDefinition Definition { get; } = new(
            "echo",
            "Echoes text.",
            new ToolSchema.Object(
                [
                    new ToolSchema.Property(
                        "value",
                        new ToolSchema.Value(ToolParamType.String, description: "Echo payload"),
                        isRequired: true
                    )
                ]
            )
        );

        public ValueTask<ToolExecuteResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken) {
            return ValueTask.FromResult(
                ToolExecuteResult.FromText(
                    ToolExecutionStatus.Success,
                    "echo:" + context.RawToolCall.RawArgumentsJson
                )
            );
        }
    }
}
