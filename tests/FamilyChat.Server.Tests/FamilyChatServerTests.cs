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
                new ChatSessionCreateOptions("model-a", "system", "openai-chat/strict"),
                new ChatSessionRuntime(
                    client,
                    "openai-chat/strict",
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
                new ChatSessionCreateOptions("model-a", "system", "openai-chat/strict"),
                new ChatSessionRuntime(
                    client,
                    "openai-chat/strict",
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
                new ChatSessionCreateOptions("model-a", "system", "openai-chat/strict"),
                new ChatSessionRuntime(
                    client,
                    "openai-chat/strict",
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
                        "model-a",
                        "openai-chat/strict",
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
                        "model-a",
                        "openai-chat/strict",
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
                new ChatSessionCreateOptions("model-a", "system", "openai-chat/strict"),
                new ChatSessionRuntime(
                    client,
                    "openai-chat/strict",
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
                new ChatSessionCreateOptions("model-a", "system", "openai-chat/strict"),
                new ChatSessionRuntime(
                    client,
                    "openai-chat/strict",
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
                new ChatSessionCreateOptions("model-a", "system", "openai-chat/strict"),
                new ChatSessionRuntime(
                    client,
                    "openai-chat/strict",
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
                new ChatSessionCreateOptions("model-a", "system", "openai-chat/strict"),
                new ChatSessionRuntime(
                    client,
                    "openai-chat/strict",
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
                        "model-a",
                        "openai-chat/strict",
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
                        "model-a",
                        "openai-chat/strict",
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
                        "model-a",
                        "openai-chat/strict",
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
                        "model-a",
                        "openai-chat/strict",
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
                new ChatSessionCreateOptions("model-a", "system", "openai-chat/strict"),
                new ChatSessionRuntime(
                    setupClient,
                    "openai-chat/strict",
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
                        "model-a",
                        "openai-chat/strict",
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
                        "model-a",
                        "openai-chat/strict",
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
                        "model-a",
                        "openai-chat/strict",
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

            string details = FlattenExceptionMessages(exception!);
            Assert.Contains(configPath, details, StringComparison.Ordinal);
            Assert.Contains("modelId", details, StringComparison.Ordinal);
            Assert.Contains("listenUrls", details, StringComparison.Ordinal);
            Assert.Contains("passwords", details, StringComparison.Ordinal);

            var generated = ReadConfig(configPath);
            Assert.Equal("openai-chat", generated.Backend.Kind);
            Assert.Equal("http://localhost:8888/", generated.Backend.BaseAddress);
            Assert.Equal("http://0.0.0.0:3510", Assert.Single(generated.ListenUrls!));
            Assert.Equal(2, generated.Users.Count);
            Assert.Equal("alice", generated.Users[0].UserId);
            Assert.Equal("alice123", generated.Users[0].Password);
            Assert.Equal(".atelia/family-chat/sessions/alice", generated.Users[0].SessionDir);
            Assert.Equal("REPLACE_WITH_YOUR_LOCAL_MODEL_ID", generated.Users[0].ModelId);
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
            var config = ReadConfig(configPath);
            var updated = config with {
                Users = config.Users
                    .Select(
                        user => user with {
                            ModelId = "model-a",
                            SessionDir = Path.Combine(tempDir, "generated-sessions", user.UserId)
                        }
                    )
                    .ToArray()
            };
            WriteConfigFile(configPath, updated);

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
                        "model-a",
                        "openai-chat/strict",
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
                        "model-a",
                        "openai-chat/strict",
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
                        "model-b",
                        "openai-chat/strict",
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
    public async Task ConfigWithoutCompactionPrompts_FallsBackToBuiltInDefaults() {
        string tempDir = CreateTempDirectory();
        try {
            string sessionDir = Path.Combine(tempDir, "alice-session");
            await SeedSessionAsync(
                sessionDir,
                "model-a",
                "openai-chat/strict",
                [
                    ("one", new string('A', 180)),
                    ("two", new string('B', 180)),
                ]
            );

            var (defaultCompactionSystemPrompt, defaultCompactionPrompt) = GetGeneratedTemplateCompactionPrompts();
            string configPath = WriteRawConfig(
                tempDir,
                new {
                    backend = new {
                        kind = "openai-chat",
                        baseAddress = "http://localhost:8000/",
                        apiKey = (string?)null,
                    },
                    users = new[] {
                        new {
                            userId = "alice",
                            displayName = "Alice",
                            password = "pw1",
                            sessionDir,
                            modelId = "model-a",
                            completionSurfaceId = "openai-chat/strict",
                            compactionThresholdTokens = 120UL,
                            systemPrompt = "system",
                        }
                    }
                }
            );

            var scriptFactory = new ScriptedCompletionClientFactory();
            scriptFactory.For("alice").Enqueue(
                (request, observer, ct) => {
                    Assert.Equal(defaultCompactionSystemPrompt, request.SystemPrompt);
                    var summarizePrompt = Assert.IsType<ObservationMessage>(request.Context[^1]);
                    Assert.Equal(defaultCompactionPrompt, summarizePrompt.Content);

                    return Task.FromResult(
                        new CompletionResult(
                            new ActionMessage([new ActionBlock.Text("summary")]),
                            new CompletionDescriptor("test", "openai-chat-v1", request.ModelId)
                        )
                    );
                }
            );
            scriptFactory.For("alice").EnqueueText("after compact");

            await using var factory = new FamilyChatServerFactory(configPath, scriptFactory);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions {
                AllowAutoRedirect = false,
                HandleCookies = true,
            });

            await LoginAsync(client, "alice", "pw1");
            string sse = await ReadSseAsStringAsync(client, "three");

            Assert.Contains("event: done", sse, StringComparison.Ordinal);

            var turns = await GetRecentTurnsAsync(client);
            Assert.NotNull(turns);
            Assert.Equal("three", turns![0].UserText);
            Assert.Equal("after compact", turns[0].Assistant.Text);
        }
        finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ConfigWithCompactionPromptOverrides_UsesConfiguredValues() {
        string tempDir = CreateTempDirectory();
        try {
            string sessionDir = Path.Combine(tempDir, "alice-session");
            await SeedSessionAsync(
                sessionDir,
                "model-a",
                "openai-chat/strict",
                [
                    ("one", new string('A', 180)),
                    ("two", new string('B', 180)),
                ]
            );

            const string customCompactionSystemPrompt = "custom-compaction-system";
            const string customCompactionPrompt = "custom-compaction-prompt";
            string configPath = WriteConfig(
                tempDir,
                thresholdTokens: 120,
                users: [
                    new FamilyChatUserConfig(
                        "alice",
                        "Alice",
                        "pw1",
                        sessionDir,
                        "model-a",
                        "openai-chat/strict",
                        120,
                        customCompactionSystemPrompt,
                        customCompactionPrompt,
                        "system"
                    )
                ]
            );

            var scriptFactory = new ScriptedCompletionClientFactory();
            scriptFactory.For("alice").Enqueue(
                (request, observer, ct) => {
                    Assert.Equal(customCompactionSystemPrompt, request.SystemPrompt);
                    var summarizePrompt = Assert.IsType<ObservationMessage>(request.Context[^1]);
                    Assert.Equal(customCompactionPrompt, summarizePrompt.Content);

                    return Task.FromResult(
                        new CompletionResult(
                            new ActionMessage([new ActionBlock.Text("summary")]),
                            new CompletionDescriptor("test", "openai-chat-v1", request.ModelId)
                        )
                    );
                }
            );
            scriptFactory.For("alice").EnqueueText("after compact");

            await using var factory = new FamilyChatServerFactory(configPath, scriptFactory);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions {
                AllowAutoRedirect = false,
                HandleCookies = true,
            });

            await LoginAsync(client, "alice", "pw1");
            string sse = await ReadSseAsStringAsync(client, "three");

            Assert.Contains("event: done", sse, StringComparison.Ordinal);
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
            var generated = ReadConfig(configPath);

            string readmePath = Path.Combine("/repos/focus/atelia", "prototypes", "FamilyChat.Server", "README.md");
            string readme = File.ReadAllText(readmePath);
            string json = ExtractFirstJsonCodeBlock(readme);
            var documented = JsonSerializer.Deserialize<FamilyChatConfig>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

            Assert.NotNull(documented);
            Assert.Equal(generated.Backend, documented!.Backend);
            Assert.Equal(generated.ListenUrls, documented.ListenUrls);
            Assert.Equal(generated.Users, documented.Users);
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
                        "model-a",
                        "openai-chat/strict",
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
    public async Task CompactionBeforeSend_EmitsMeta_And_ShowsRecapInRecentTurns() {
        string tempDir = CreateTempDirectory();
        try {
            string sessionDir = Path.Combine(tempDir, "alice-session");
            await SeedSessionAsync(
                sessionDir,
                "model-a",
                "openai-chat/strict",
                [
                    ("first", new string('A', 80)),
                    ("second", "keep"),
                ]
            );

            var configPath = WriteConfig(
                tempDir,
                thresholdTokens: 45,
                users: [
                    new FamilyChatUserConfig(
                        "alice",
                        "Alice",
                        "pw1",
                        sessionDir,
                        "model-a",
                        "openai-chat/strict",
                        45,
                        "compact-system",
                        "compact-prompt",
                        "system"
                    )
                ]
            );

            var scriptFactory = new ScriptedCompletionClientFactory();
            scriptFactory.For("alice").EnqueueText("S");
            scriptFactory.For("alice").EnqueueText("after");

            await using var factory = new FamilyChatServerFactory(configPath, scriptFactory);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions {
                AllowAutoRedirect = false,
                HandleCookies = true,
            });
            await LoginAsync(client, "alice", "pw1");

            string third = await ReadSseAsStringAsync(client, "third");

            Assert.Contains("\"phase\":\"compaction-start\"", third, StringComparison.Ordinal);
            Assert.Contains("\"phase\":\"compaction-finish\"", third, StringComparison.Ordinal);
            Assert.Contains("event: done", third, StringComparison.Ordinal);

            var turns = await GetRecentTurnsAsync(client);
            Assert.NotNull(turns);
            Assert.Collection(
                turns!,
                turn => {
                    Assert.Equal("third", turn.UserText);
                    Assert.Equal("after", turn.Assistant.Text);
                },
                turn => {
                    Assert.Equal("second", turn.UserText);
                    Assert.Equal("keep", turn.Assistant.Text);
                },
                turn => {
                    Assert.True(turn.IsRecap);
                    Assert.Equal("S", turn.Assistant.Text);
                }
            );
        }
        finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task CompactionCannotReduceEnough_ReturnsErrorEvent() {
        string tempDir = CreateTempDirectory();
        try {
            var configPath = WriteConfig(
                tempDir,
                thresholdTokens: 20,
                users: [
                    new FamilyChatUserConfig(
                        "alice",
                        "Alice",
                        "pw1",
                        Path.Combine(tempDir, "alice-session"),
                        "model-a",
                        "openai-chat/strict",
                        20,
                        "compact-system",
                        "compact-prompt",
                        "system"
                    )
                ]
            );

            var scriptFactory = new ScriptedCompletionClientFactory();
            scriptFactory.For("alice").EnqueueText(new string('A', 80));
            scriptFactory.For("alice").EnqueueText("S");

            await using var factory = new FamilyChatServerFactory(configPath, scriptFactory);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions {
                AllowAutoRedirect = false,
                HandleCookies = true,
            });
            await LoginAsync(client, "alice", "pw1");

            await ReadSseAsStringAsync(client, "first");
            string second = await ReadSseAsStringAsync(client, "second");

            Assert.Contains("\"phase\":\"compaction-start\"", second, StringComparison.Ordinal);
            Assert.Contains("event: error", second, StringComparison.Ordinal);
            Assert.Contains("\"failureReason\":\"NoValidSplitPoint\"", second, StringComparison.Ordinal);
            Assert.DoesNotContain("event: done", second, StringComparison.Ordinal);
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
                        "model-a",
                        "openai-chat/strict",
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
                        "model-a",
                        "openai-chat/strict",
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
                        "model-a",
                        "openai-chat/strict",
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
                        "model-a",
                        "openai-chat/strict",
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
                        "model-a",
                        "openai-chat/strict",
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

    private static async Task<StartTurnResponseDto> StartTurnAsync(HttpClient client, string message) {
        using var response = await client.PostAsJsonAsync("/api/chat/turns", new ChatStreamRequest(message));
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
            new ToolRegistry(Array.Empty<ITool>()).CreateSession()
        );
        var engine = await ChatSessionEngine.CreateAsync(
            sessionDir,
            new ChatSessionCreateOptions(modelId, "system", completionSurfaceId),
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
        IReadOnlyList<FamilyChatUserConfig> users
    ) {
        var config = new FamilyChatConfig(
            new FamilyChatBackendConfig("openai-chat", "http://localhost:8000/"),
            users
        );

        string configDir = Path.Combine(tempDir, ".atelia", "family-chat");
        Directory.CreateDirectory(configDir);
        string path = Path.Combine(configDir, "config.json");
        WriteConfigFile(path, config);
        return path;
    }

    private static string WriteRawConfig(string tempDir, object config) {
        string configDir = Path.Combine(tempDir, ".atelia", "family-chat");
        Directory.CreateDirectory(configDir);
        string path = Path.Combine(configDir, "config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(config, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return path;
    }

    private static void WriteConfigFile(string path, FamilyChatConfig config) {
        File.WriteAllText(path, JsonSerializer.Serialize(config, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static FamilyChatConfig ReadConfig(string path) {
        var config = JsonSerializer.Deserialize<FamilyChatConfig>(
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

    private static (string CompactionSystemPrompt, string CompactionPrompt) GetGeneratedTemplateCompactionPrompts() {
        string tempDir = CreateTempDirectory();
        try {
            string configPath = BootstrapTemplate(tempDir);
            var config = ReadConfig(configPath);
            var user = config.Users[0];
            Assert.NotNull(user.CompactionSystemPrompt);
            Assert.NotNull(user.CompactionPrompt);
            return (user.CompactionSystemPrompt!, user.CompactionPrompt!);
        }
        finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static string FlattenExceptionMessages(Exception exception) {
        var parts = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException) {
            parts.Add(current.Message);
        }

        return string.Join(Environment.NewLine, parts);
    }

    private static string ExtractFirstJsonCodeBlock(string markdown) {
        var match = Regex.Match(markdown, "```json\\s*(.*?)\\s*```", RegexOptions.Singleline);
        Assert.True(match.Success, "README should contain a JSON code block.");
        return match.Groups[1].Value;
    }

    private sealed class FamilyChatServerFactory(
        string configPath,
        ScriptedCompletionClientFactory scriptedFactory
    ) : WebApplicationFactory<Program> {
        protected override void ConfigureWebHost(IWebHostBuilder builder) {
            builder.UseSetting("FamilyChat:ConfigPath", configPath);
            builder.ConfigureTestServices(
                services => {
                    services.AddSingleton<IFamilyChatCompletionClientFactory>(scriptedFactory);
                }
            );
        }
    }

    private sealed class ScriptedCompletionClientFactory : IFamilyChatCompletionClientFactory {
        private readonly ConcurrentDictionary<string, ScriptedCompletionClient> _clients = new(StringComparer.Ordinal);

        public ScriptedCompletionClient For(string userId) {
            return _clients.GetOrAdd(userId, _ => new ScriptedCompletionClient("openai-chat-v1"));
        }

        public ICompletionClient Create(FamilyChatBackendConfig backend, FamilyChatUserConfig user)
            => For(user.UserId);
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
