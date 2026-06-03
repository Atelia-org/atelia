using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Atelia.ChatSession;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Anthropic;
using Atelia.Completion.OpenAI;
using Atelia.Completion.Tools;
using Atelia.Completion.Transport;

namespace Atelia.FamilyChat.Server;

public interface IFamilyChatCompletionClientFactory {
    ICompletionClient Create(FamilyChatBackendConfig backend, FamilyChatUserConfig user);
}

public sealed class DefaultFamilyChatCompletionClientFactory : IFamilyChatCompletionClientFactory {
    public ICompletionClient Create(FamilyChatBackendConfig backend, FamilyChatUserConfig user) {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(user);

        var httpClient = CompletionHttpTransportFactory.CreateLiveClient(new Uri(backend.BaseAddress, UriKind.Absolute));

        return backend.Kind.Trim().ToLowerInvariant() switch {
            "openai-chat" => new OpenAIChatClient(
                apiKey: backend.ApiKey,
                httpClient: httpClient,
                dialect: ResolveOpenAiChatDialect(user.CompletionSurfaceId)
                // , options: OpenAIChatClientOptions.QwenThinkingDisabled()
            ),
            "openai-responses" => new OpenAIResponsesClient(
                apiKey: backend.ApiKey,
                httpClient: httpClient
            ),
            "anthropic" => new AnthropicClient(
                apiKey: backend.ApiKey,
                httpClient: httpClient
            ),
            _ => throw new InvalidOperationException($"Unsupported backend kind '{backend.Kind}'.")
        };
    }

    private static OpenAIChatDialect ResolveOpenAiChatDialect(string completionSurfaceId) {
        return completionSurfaceId switch {
            "openai-chat/strict" => OpenAIChatDialects.Strict,
            "openai-chat/sglang-compatible" => OpenAIChatDialects.SgLangCompatible,
            "openai-chat/deepseek-v4" => OpenAIChatDialects.DeepSeekV4,
            _ => throw new InvalidOperationException($"Unsupported OpenAI Chat surface '{completionSurfaceId}'.")
        };
    }
}

public sealed class FamilyChatHostService : IAsyncDisposable {
    private readonly FamilyChatConfig _config;
    private readonly IFamilyChatCompletionClientFactory _completionClientFactory;
    private readonly ConcurrentDictionary<string, Lazy<Task<UserSessionHost>>> _sessions = new(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, FamilyChatUserConfig> _users;

    public FamilyChatHostService(
        FamilyChatConfig config,
        IFamilyChatCompletionClientFactory completionClientFactory
    ) {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _completionClientFactory = completionClientFactory ?? throw new ArgumentNullException(nameof(completionClientFactory));
        _users = config.Users.ToDictionary(x => x.UserId, StringComparer.Ordinal);
    }

    public bool TryGetUser(string userId, out FamilyChatUserConfig user)
        => _users.TryGetValue(userId, out user!);

    public bool ValidatePassword(FamilyChatUserConfig user, string password) {
        ArgumentNullException.ThrowIfNull(user);
        password ??= string.Empty;

        byte[] left = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        byte[] right = SHA256.HashData(Encoding.UTF8.GetBytes(user.Password ?? string.Empty));
        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    public async Task<UserSessionHost> GetSessionAsync(string userId, CancellationToken ct) {
        var user = _users.GetValueOrDefault(userId)
            ?? throw new InvalidOperationException($"Unknown user '{userId}'.");

        var lazy = _sessions.GetOrAdd(
            userId,
            static (key, state) => new Lazy<Task<UserSessionHost>>(
                () => state.Service.CreateSessionAsync(state.User, CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication
            ),
            (Service: this, User: user)
        );

        return await lazy.Value.ConfigureAwait(false);
    }

    public IReadOnlyList<RecentTurnDto> BuildRecentTurns(ChatSessionEngine engine, int maxTurns = 6) {
        ArgumentNullException.ThrowIfNull(engine);

        var turns = new List<RecentTurnDto>();
        string? pendingUserText = null;
        AssistantMessageDto? latestAssistant = null;

        foreach (var message in engine.Context) {
            if (message is RecapMessage) {
                continue;
            }

            if (message is ToolResultsMessage) {
                continue;
            }

            if (message is ObservationMessage observation) {
                if (pendingUserText is not null && latestAssistant is not null) {
                    turns.Add(new RecentTurnDto(pendingUserText, latestAssistant));
                }

                pendingUserText = observation.Content ?? string.Empty;
                latestAssistant = null;
                continue;
            }

            if (message is ActionMessage action && pendingUserText is not null) {
                var projected = ProjectAssistant(action);
                if (projected is not null) {
                    latestAssistant = projected;
                }
            }
        }

        if (pendingUserText is not null && latestAssistant is not null) {
            turns.Add(new RecentTurnDto(pendingUserText, latestAssistant));
        }

        turns.Reverse();
        if (turns.Count <= maxTurns) { return turns; }
        return turns.Take(maxTurns).ToArray();
    }

    public async Task RunTurnAsync(
        UserSessionHost host,
        string userMessage,
        ChannelWriter<StreamEventDto> writer,
        CancellationToken ct
    ) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);

        await writer.WriteAsync(new StreamEventDto("meta", new { phase = "turn-start" }), ct).ConfigureAwait(false);

        while (host.Engine.GetStatistics().EstimatedTokens >= host.User.CompactionThresholdTokens) {
            await writer.WriteAsync(
                new StreamEventDto("meta", new { phase = "compaction-start" }),
                ct
            ).ConfigureAwait(false);

            var compaction = await host.Engine.CompactAsync(
                host.User.CompactionSystemPrompt,
                host.User.CompactionPrompt,
                ct
            ).ConfigureAwait(false);

            await writer.WriteAsync(
                new StreamEventDto(
                    "meta",
                    new {
                        phase = "compaction-finish",
                        applied = compaction.Applied,
                        failureReason = compaction.FailureReason?.ToString(),
                        tokensBefore = compaction.TokensBefore,
                        tokensAfter = compaction.TokensAfter,
                    }
                ),
                ct
            ).ConfigureAwait(false);

            if (!compaction.Applied) {
                break;
            }
        }

        if (host.Engine.GetStatistics().EstimatedTokens >= host.User.CompactionThresholdTokens) {
            throw new InvalidOperationException("当前会话上下文过长，且无法继续压缩。");
        }

        var observer = new CompletionStreamObserver();
        var toolLoopStarted = 0;
        observer.ReceivedThinkingBegin += () => writer.TryWrite(new StreamEventDto("meta", new { phase = "reasoning-start" }));
        observer.ReceivedThinkingEnd += () => writer.TryWrite(new StreamEventDto("meta", new { phase = "reasoning-end" }));
        observer.ReceivedReasoningDelta += delta => writer.TryWrite(new StreamEventDto("reasoning-delta", new { delta }));
        observer.ReceivedTextDelta += delta => writer.TryWrite(new StreamEventDto("text-delta", new { delta }));
        observer.ReceivedToolCall += call => {
            if (Interlocked.Exchange(ref toolLoopStarted, 1) == 0) {
                writer.TryWrite(new StreamEventDto("meta", new { phase = "tool-loop-start" }));
            }

            writer.TryWrite(
                new StreamEventDto("meta", new { phase = "tool-call", toolName = call.ToolName, toolCallId = call.ToolCallId })
            );
        };

        var turnResult = await host.Engine.SendMessageAsync(userMessage, observer, ct).ConfigureAwait(false);
        var snapshot = BuildRecentTurns(host.Engine);

        if (Volatile.Read(ref toolLoopStarted) == 1) {
            await writer.WriteAsync(new StreamEventDto("meta", new { phase = "tool-loop-finish" }), ct).ConfigureAwait(false);
        }

        await writer.WriteAsync(
            new StreamEventDto(
                "done",
                new {
                    recentTurns = snapshot,
                    toolCallsExecuted = turnResult.ToolCallsExecuted,
                    errors = turnResult.Errors
                }
            ),
            ct
        ).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() {
        foreach (var entry in _sessions.Values) {
            if (!entry.IsValueCreated) { continue; }

            try {
                var session = await entry.Value.ConfigureAwait(false);
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch {
                // Ignore teardown failures during host shutdown.
            }
        }
    }

    private async Task<UserSessionHost> CreateSessionAsync(FamilyChatUserConfig user, CancellationToken ct) {
        var completionClient = _completionClientFactory.Create(_config.Backend, user);
        var runtime = new ChatSessionRuntime(
            CompletionClient: completionClient,
            CompletionSurfaceId: user.CompletionSurfaceId,
            ToolRegistry: new ToolRegistry(Array.Empty<ITool>()),
            ToolSessionState: new ToolSessionState()
        );
        var createOptions = new ChatSessionCreateOptions(
            ModelId: user.ModelId,
            SystemPrompt: user.SystemPrompt,
            CompletionSurfaceId: user.CompletionSurfaceId
        );

        var sessionDir = Path.GetFullPath(user.SessionDir);
        ChatSessionEngine engine;
        if (Directory.Exists(sessionDir) && Directory.EnumerateFileSystemEntries(sessionDir).Any()) {
            engine = await ChatSessionEngine.OpenAsync(sessionDir, runtime, ct: ct).ConfigureAwait(false);
        }
        else {
            Directory.CreateDirectory(sessionDir);
            engine = await ChatSessionEngine.CreateAsync(sessionDir, createOptions, runtime, ct).ConfigureAwait(false);
        }

        return new UserSessionHost(user, engine, completionClient);
    }

    private static AssistantMessageDto? ProjectAssistant(ActionMessage action) {
        var textBuilder = new StringBuilder();
        var reasoningBuilder = new StringBuilder();

        foreach (var block in action.Blocks) {
            switch (block) {
                case ActionBlock.Text text:
                    textBuilder.Append(text.Content);
                    break;
                case ActionBlock.TextReasoningBlock reasoning:
                    reasoningBuilder.Append(reasoning.Content);
                    break;
            }
        }

        if (textBuilder.Length == 0 && reasoningBuilder.Length == 0) {
            return null;
        }

        string? reasoningText = reasoningBuilder.Length == 0 ? null : reasoningBuilder.ToString();
        return new AssistantMessageDto(
            Text: textBuilder.ToString(),
            ReasoningText: reasoningText,
            HasReasoning: !string.IsNullOrEmpty(reasoningText)
        );
    }
}

public sealed class UserSessionHost : IAsyncDisposable {
    private readonly ICompletionClient _completionClient;

    public UserSessionHost(
        FamilyChatUserConfig user,
        ChatSessionEngine engine,
        ICompletionClient completionClient
    ) {
        User = user;
        Engine = engine;
        _completionClient = completionClient;
    }

    public FamilyChatUserConfig User { get; }

    public ChatSessionEngine Engine { get; }

    public SemaphoreSlim TurnLock { get; } = new(1, 1);

    public ValueTask DisposeAsync() {
        Engine.Dispose();
        if (_completionClient is IDisposable disposable) {
            disposable.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}

internal static class FamilyChatConfigLoader {
    public static FamilyChatConfig Load(string configPath) {
        if (string.IsNullOrWhiteSpace(configPath)) {
            throw new InvalidOperationException("FamilyChat config path must not be blank.");
        }

        string resolvedPath = Path.GetFullPath(configPath);
        if (!File.Exists(resolvedPath)) {
            throw new FileNotFoundException(
                $"FamilyChat config file was not found: {resolvedPath}",
                resolvedPath
            );
        }

        var json = File.ReadAllText(resolvedPath);
        var config = JsonSerializer.Deserialize(json, FamilyChatJsonContext.Default.FamilyChatConfig);
        if (config is null) {
            throw new InvalidOperationException($"Failed to deserialize FamilyChat config: {resolvedPath}");
        }

        if (config.Users.Count == 0) {
            throw new InvalidOperationException("FamilyChat config must contain at least one user.");
        }

        Validate(config);
        return config;
    }

    private static void Validate(FamilyChatConfig config) {
        var userIds = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < config.Users.Count; i++) {
            var user = config.Users[i];
            if (string.IsNullOrWhiteSpace(user.UserId)) {
                throw new InvalidOperationException($"FamilyChat config user[{i}] must have a non-empty userId.");
            }

            if (!userIds.Add(user.UserId)) {
                throw new InvalidOperationException($"FamilyChat config contains duplicate userId '{user.UserId}'.");
            }

            if (string.IsNullOrWhiteSpace(user.Password)) {
                throw new InvalidOperationException($"FamilyChat config user '{user.UserId}' must have a non-empty password.");
            }

            if (string.IsNullOrWhiteSpace(user.SessionDir)) {
                throw new InvalidOperationException($"FamilyChat config user '{user.UserId}' must have a non-empty sessionDir.");
            }
        }

        if (config.ListenUrls is null) { return; }

        for (int i = 0; i < config.ListenUrls.Count; i++) {
            if (string.IsNullOrWhiteSpace(config.ListenUrls[i])) {
                throw new InvalidOperationException($"FamilyChat config listenUrls[{i}] must not be blank.");
            }
        }
    }
}

internal static class FamilyChatConfigBootstrapper {
    public static void EnsureExistsOrBootstrap(string configPath) {
        if (string.IsNullOrWhiteSpace(configPath)) {
            throw new InvalidOperationException("FamilyChat config path must not be blank.");
        }

        string resolvedPath = Path.GetFullPath(configPath);
        if (File.Exists(resolvedPath)) { return; }

        string? parentDir = Path.GetDirectoryName(resolvedPath);
        if (string.IsNullOrWhiteSpace(parentDir)) {
            throw new InvalidOperationException($"Cannot determine parent directory for FamilyChat config path: {resolvedPath}");
        }

        Directory.CreateDirectory(parentDir);

        var template = FamilyChatConfigTemplateFactory.Create(resolvedPath);
        var jsonOptions = new JsonSerializerOptions(FamilyChatJson.Options) {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        File.WriteAllText(resolvedPath, JsonSerializer.Serialize(template, jsonOptions) + Environment.NewLine, Encoding.UTF8);

        throw new InvalidOperationException(
            "FamilyChat config template has been generated at "
            + resolvedPath
            + ". Please update listenUrls, modelId, and the default account passwords before restarting the server."
        );
    }
}

internal static class FamilyChatConfigTemplateFactory {
    public const string PlaceholderModelId = "REPLACE_WITH_YOUR_LOCAL_MODEL_ID";

    public const string DefaultSystemPrompt =
        "你是家庭局域网里的私人助手。优先用简洁、直接、可信的中文回答。"
        + "不确定时明确说明不确定，不编造细节。";

    public const string DefaultCompactionSystemPrompt =
        "你负责压缩长期对话上下文。请保留用户偏好、未完成事项、关键事实、约定、限制与后续行动线索。"
        + "输出简洁中文摘要，避免虚构。";

    public const string DefaultCompactionPrompt =
        "请把以上较早的对话压缩成一段可供后续继续聊天的中文 recap。"
        + "保留人物偏好、进行中的任务、重要事实、未决问题与明确约定。";

    public static FamilyChatConfig Create(string configPath) {
        return new FamilyChatConfig(
            Backend: new FamilyChatBackendConfig(
                Kind: "openai-chat",
                BaseAddress: "http://localhost:8888/",
                ApiKey: null
            ),
            Users: [
                CreateUser("alice", "Alice", "alice123", ".atelia/family-chat/sessions/alice"),
                CreateUser("bob", "Bob", "bob123", ".atelia/family-chat/sessions/bob"),
            ],
            ListenUrls: ["http://0.0.0.0:3510"]
        );
    }

    private static FamilyChatUserConfig CreateUser(string userId, string displayName, string password, string sessionDir) {
        return new FamilyChatUserConfig(
            UserId: userId,
            DisplayName: displayName,
            Password: password,
            SessionDir: sessionDir,
            ModelId: PlaceholderModelId,
            CompletionSurfaceId: "openai-chat/sglang-compatible",
            SystemPrompt: DefaultSystemPrompt,
            CompactionThresholdTokens: 32000,
            CompactionSystemPrompt: DefaultCompactionSystemPrompt,
            CompactionPrompt: DefaultCompactionPrompt
        );
    }
}

internal static class FamilyChatHtml {
    public static string RenderLoginPage(bool invalidCredentials) {
        string errorHtml = invalidCredentials
            ? "<p class=\"error\">用户名或密码不正确。</p>"
            : string.Empty;

        return $$"""
<!doctype html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Family Chat Login</title>
  <link rel="stylesheet" href="/assets/family-chat.css">
</head>
<body class="login-body">
  <main class="login-shell">
    <h1>Family Chat</h1>
    <p class="login-copy">局域网家庭单会话 Chat</p>
    <p class="login-hint">首次启动后，请先确认 <code>.atelia/family-chat/config.json</code>。</p>
    {{errorHtml}}
    <form method="post" action="/login" class="login-form">
      <label>用户名<input name="userId" autocomplete="username" required></label>
      <label>密码<input type="password" name="password" autocomplete="current-password" required></label>
      <button type="submit">登录</button>
    </form>
  </main>
</body>
</html>
""";
    }

    public static string RenderAppPage(string displayName) {
        string safeDisplayName = System.Net.WebUtility.HtmlEncode(displayName);
        return $$"""
<!doctype html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Family Chat</title>
  <link rel="stylesheet" href="/assets/family-chat.css">
</head>
<body class="app-body">
  <main class="app-shell">
    <header class="topbar">
      <div>
        <div class="eyebrow">Family Chat</div>
        <h1>{{safeDisplayName}}</h1>
      </div>
      <form method="post" action="/logout">
        <button type="submit" class="ghost-button">退出</button>
      </form>
    </header>

    <section class="composer">
      <form id="chat-form">
        <textarea id="message-input" rows="3" placeholder="说点什么……" required></textarea>
        <div class="composer-actions">
          <span id="status-text" class="status-text"></span>
          <button id="send-button" type="submit">发送</button>
        </div>
      </form>
    </section>

    <section id="live-turn" class="live-turn hidden" aria-live="polite">
      <article class="turn-card assistant live">
        <header>Assistant</header>
        <details class="reasoning-panel hidden" id="live-reasoning-panel">
          <summary>Reasoning</summary>
          <pre id="live-reasoning"></pre>
        </details>
        <pre id="live-text"></pre>
      </article>
    </section>

    <section class="history">
      <div id="turn-list" class="turn-list"></div>
    </section>
  </main>

  <script>
    window.familyChatBootstrap = { displayName: {{JsonSerializer.Serialize(displayName, FamilyChatJson.Options)}} };
  </script>
  <script src="/assets/family-chat.js"></script>
</body>
</html>
""";
    }
}

internal static class FamilyChatSseWriter {
    public static async Task WriteEventAsync(HttpResponse response, string eventName, object? payload, CancellationToken ct) {
        string json = JsonSerializer.Serialize(payload, FamilyChatJson.Options);
        await response.WriteAsync($"event: {eventName}\n", ct);
        await response.WriteAsync($"data: {json}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }
}

internal static class FamilyChatClaimTypes {
    public const string UserId = "family_chat_user_id";
}

internal static class FamilyChatJson {
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) {
        WriteIndented = false
    };
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(FamilyChatConfig))]
[JsonSerializable(typeof(FamilyChatBackendConfig))]
[JsonSerializable(typeof(FamilyChatUserConfig))]
internal sealed partial class FamilyChatJsonContext : JsonSerializerContext;
