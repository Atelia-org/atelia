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

            var engine = await ChatSessionEngine.CreateAsync(
                repoDir,
                new ChatSessionCreateOptions("model-a", "system", "openai-chat/strict"),
                new ChatSessionRuntime(
                    client,
                    "openai-chat/strict",
                    new ToolRegistry([new EchoTool()]),
                    new ToolSessionState()
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
                        "system",
                        200,
                        "compact-system",
                        "compact-prompt"
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

            var turns = await client.GetFromJsonAsync<List<RecentTurnDto>>("/api/recent-turns");
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
                        "system",
                        200,
                        "compact-system",
                        "compact-prompt"
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
            Assert.Equal("http://localhost:8000/", generated.Backend.BaseAddress);
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
                            ModelId = "model-a"
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

            var turns = await client.GetFromJsonAsync<List<RecentTurnDto>>("/api/recent-turns");
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
                        "system",
                        200,
                        "compact-system",
                        "compact-prompt"
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
                        "system",
                        200,
                        "compact-system",
                        "compact-prompt"
                    ),
                    new FamilyChatUserConfig(
                        "alice",
                        "Alice 2",
                        "pw2",
                        Path.Combine(tempDir, "alice-session-2"),
                        "model-b",
                        "openai-chat/strict",
                        "system",
                        200,
                        "compact-system",
                        "compact-prompt"
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
                        "system",
                        200,
                        "compact-system",
                        "compact-prompt"
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

            var first = client.SendAsync(
                new HttpRequestMessage(HttpMethod.Post, "/api/chat/stream") {
                    Content = JsonContent.Create(new ChatStreamRequest("one")),
                },
                HttpCompletionOption.ResponseHeadersRead
            );

            await blocker.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var second = await client.PostAsJsonAsync("/api/chat/stream", new ChatStreamRequest("two"));
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

            release.TrySetResult();
            using var firstResponse = await first;
            string sse = await firstResponse.Content.ReadAsStringAsync();
            Assert.Contains("event: done", sse, StringComparison.Ordinal);
        }
        finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task CompactionBeforeSend_EmitsMeta_And_HidesRecapFromRecentTurns() {
        string tempDir = CreateTempDirectory();
        try {
            var configPath = WriteConfig(
                tempDir,
                thresholdTokens: 45,
                users: [
                    new FamilyChatUserConfig(
                        "alice",
                        "Alice",
                        "pw1",
                        Path.Combine(tempDir, "alice-session"),
                        "model-a",
                        "openai-chat/strict",
                        "system",
                        45,
                        "compact-system",
                        "compact-prompt"
                    )
                ]
            );

            var scriptFactory = new ScriptedCompletionClientFactory();
            scriptFactory.For("alice").EnqueueText(new string('A', 80));
            scriptFactory.For("alice").EnqueueText("keep");
            scriptFactory.For("alice").EnqueueText("S");
            scriptFactory.For("alice").EnqueueText("after");

            await using var factory = new FamilyChatServerFactory(configPath, scriptFactory);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions {
                AllowAutoRedirect = false,
                HandleCookies = true,
            });
            await LoginAsync(client, "alice", "pw1");

            await ReadSseAsStringAsync(client, "first");
            await ReadSseAsStringAsync(client, "second");
            string third = await ReadSseAsStringAsync(client, "third");

            Assert.Contains("\"phase\":\"compaction-start\"", third, StringComparison.Ordinal);
            Assert.Contains("\"phase\":\"compaction-finish\"", third, StringComparison.Ordinal);
            Assert.Contains("event: done", third, StringComparison.Ordinal);

            var turns = await client.GetFromJsonAsync<List<RecentTurnDto>>("/api/recent-turns");
            Assert.NotNull(turns);
            Assert.Single(turns!);
            Assert.Equal("third", turns[0].UserText);
            Assert.Equal("after", turns[0].Assistant.Text);
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
                        "system",
                        20,
                        "compact-system",
                        "compact-prompt"
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
        using var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/chat/stream") {
                Content = JsonContent.Create(new ChatStreamRequest(message)),
            },
            HttpCompletionOption.ResponseHeadersRead
        );
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
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
