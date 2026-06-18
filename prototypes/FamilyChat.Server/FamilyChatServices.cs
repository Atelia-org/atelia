using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Atelia.ChatSession;
using Atelia.Diagnostics;
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
    private const string UserMessagePrefix = "玩家角色试图采取如下动作：\n```\n";
    private const string UserMessageSuffix = "\n```\n";

    private readonly FamilyChatConfig _config;
    private readonly IFamilyChatCompletionClientFactory _completionClientFactory;
    private readonly IFamilyChatUserMessageNormalizer _userMessageNormalizer;
    private readonly ConcurrentDictionary<string, Lazy<Task<UserSessionHost>>> _sessions = new(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, FamilyChatUserConfig> _users;

    public FamilyChatHostService(
        FamilyChatConfig config,
        IFamilyChatCompletionClientFactory completionClientFactory,
        IFamilyChatUserMessageNormalizer userMessageNormalizer
    ) {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _completionClientFactory = completionClientFactory ?? throw new ArgumentNullException(nameof(completionClientFactory));
        _userMessageNormalizer = userMessageNormalizer ?? throw new ArgumentNullException(nameof(userMessageNormalizer));
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

        var session = await lazy.Value.ConfigureAwait(false);
        DebugUtil.Info("FamilyChat.Session", $"GetSessionAsync: user={userId}, {session.Engine.GetDebugStateSummary()}");
        return session;
    }

    public IReadOnlyList<RecentTurnDto> BuildRecentTurns(ChatSessionEngine engine, int maxTurns = 12) {
        ArgumentNullException.ThrowIfNull(engine);

        var turns = new List<RecentTurnDto>();
        string? pendingUserText = null;
        AssistantMessageDto? latestAssistant = null;

        foreach (var message in engine.Context) {
            if (message is RecapMessage recap) {
                if (pendingUserText is not null && latestAssistant is not null) {
                    turns.Add(new RecentTurnDto(pendingUserText, latestAssistant));
                }

                pendingUserText = null;
                latestAssistant = null;
                turns.Add(
                    new RecentTurnDto(
                        string.Empty,
                        new AssistantMessageDto(recap.Content ?? string.Empty, null, HasReasoning: false),
                        IsRecap: true
                    )
                );
                continue;
            }

            if (message is ToolResultsMessage) { continue; }

            if (message is ObservationMessage observation) {
                if (pendingUserText is not null && latestAssistant is not null) {
                    turns.Add(new RecentTurnDto(pendingUserText, latestAssistant));
                }

                pendingUserText = NormalizeUserMessageForDisplay(observation.Content);
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
        IReadOnlyList<RecentTurnDto> projectedTurns = turns.Count <= maxTurns ? turns : turns.Take(maxTurns).ToArray();
        DebugUtil.Info(
            "FamilyChat.Session",
            $"BuildRecentTurns: {engine.GetDebugStateSummary()}, projectedTurns={projectedTurns.Count}, firstTurn={DescribeTurn(projectedTurns.FirstOrDefault())}"
        );
        return projectedTurns;
    }

    public RecentTurnsResponseDto BuildRecentTurnsResponse(ChatSessionEngine engine, int maxTurns = 12) {
        ArgumentNullException.ThrowIfNull(engine);

        var allTurns = BuildRecentTurns(engine, int.MaxValue);
        var turns = ProjectRecentTurnsResponse(allTurns, maxTurns);
        DebugUtil.Info(
            "FamilyChat.Session",
            $"BuildRecentTurnsResponse: {engine.GetDebugStateSummary()}, responseTurns={turns.Count}, recapVisible={turns.Any(static x => x.IsRecap)}, firstTurn={DescribeTurn(turns.FirstOrDefault())}"
        );
        return new RecentTurnsResponseDto(turns);
    }

    public CurrentTurnDto BuildCurrentTurn(UserSessionHost host) {
        ArgumentNullException.ThrowIfNull(host);

        var currentTurn = host.GetCurrentTurn();
        var result = currentTurn is null
            ? new CurrentTurnDto("idle")
            : new CurrentTurnDto(
                "running",
                currentTurn.TurnId,
                currentTurn.UserMessage,
                currentTurn.Phase,
                currentTurn.Options.AutoPrefillThinkOpenTag
            );
        DebugUtil.Info(
            "FamilyChat.Session",
            $"BuildCurrentTurn: user={host.User.UserId}, status={result.Status}, turnId={result.TurnId ?? "<none>"}, phase={result.Phase ?? "<none>"}, head={host.Engine.PersistedHeadAddress}"
        );
        return result;
    }

    internal FamilyChatLiveTurn StartTurn(UserSessionHost host, string userMessage, FamilyChatTurnOptions options) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
        ArgumentNullException.ThrowIfNull(options);
        return host.StartTurn(userMessage, options);
    }

    internal RecentTurnDto? PopLatestTurn(UserSessionHost host) {
        ArgumentNullException.ThrowIfNull(host);

        var previousLatestTurn = BuildRecentTurns(host.Engine, maxTurns: 1).FirstOrDefault();
        DebugUtil.Info(
            "FamilyChat.Session",
            $"PopLatestTurn before remove: user={host.User.UserId}, head={host.Engine.PersistedHeadAddress}, latestVisible={DescribeTurn(previousLatestTurn)}"
        );
        if (!host.Engine.TryRemoveLatestCompletedTurn(out var removedTurn) || removedTurn is null) { return null; }
        string removedUserText = NormalizeUserMessageForDisplay(removedTurn.UserMessage);

        if (previousLatestTurn is not null
            && !previousLatestTurn.IsRecap
            && string.Equals(previousLatestTurn.UserText, removedUserText, StringComparison.Ordinal)) {
            DebugUtil.Info(
                "FamilyChat.Session",
                $"PopLatestTurn matched visible turn: user={host.User.UserId}, removedCount={removedTurn.RemovedMessageCount}, head={host.Engine.PersistedHeadAddress}"
            );
            return previousLatestTurn;
        }

        DebugUtil.Warning(
            "FamilyChat.Session",
            $"PopLatestTurn fallback DTO: user={host.User.UserId}, removedCount={removedTurn.RemovedMessageCount}, removedUser={Preview(removedUserText)}, previousVisible={DescribeTurn(previousLatestTurn)}, head={host.Engine.PersistedHeadAddress}"
        );
        return new RecentTurnDto(
            removedUserText,
            new AssistantMessageDto(string.Empty, null, HasReasoning: false)
        );
    }

    internal FamilyChatLiveTurn? FindTurn(UserSessionHost host, string turnId) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(turnId);
        return host.FindTurn(turnId);
    }

    internal void FinishTurn(UserSessionHost host, FamilyChatLiveTurn turn) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(turn);
        host.FinishTurn(turn);
    }

    internal bool RequestStop(UserSessionHost host, string turnId) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(turnId);

        var turn = host.FindTurn(turnId);
        return turn?.RequestStop() == true;
    }

    internal async Task RunTurnAsync(
        UserSessionHost host,
        FamilyChatLiveTurn liveTurn,
        CancellationToken ct
    ) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(liveTurn);

        liveTurn.Publish(new StreamEventDto("meta", new { phase = "turn-start" }), phase: "turn-start");
        DebugUtil.Info(
            "FamilyChat.Session",
            $"RunTurnAsync start: user={host.User.UserId}, turnId={liveTurn.TurnId}, input={Preview(liveTurn.UserMessage)}, {host.Engine.GetDebugStateSummary()}",
            eventKind: DebugEventKind.Start
        );

        string effectiveUserMessage = await NormalizeUserMessageAsync(liveTurn, ct).ConfigureAwait(false);
        string promptedUserMessage = WrapUserMessageForEngine(effectiveUserMessage);

        // Failsafe: if the previous turn's post-generation compaction didn't happen
        // or failed, try once more before generating.  Otherwise the user would wait
        // for compaction *plus* generation in a single turn.
        if (host.Engine.GetStatistics().EstimatedTokens >= host.User.CompactionThresholdTokens) {
            liveTurn.Publish(new StreamEventDto("meta", new { phase = "compaction-start" }), phase: "compaction-start");
            var emergency = await host.Engine.CompactAsync(
                host.User.CompactionSystemPrompt!,
                host.User.CompactionPrompt!,
                ct
            ).ConfigureAwait(false);

            if (!emergency.Applied) {
                throw new FamilyChatTurnException(
                    "当前会话上下文过长，且无法继续压缩。",
                    emergency.FailureReason?.ToString()
                );
            }

            liveTurn.Publish(new StreamEventDto("meta", new { phase = "compaction-finish" }), phase: "compaction-finish");
        }

        var observer = new CompletionStreamObserver();
        liveTurn.AttachObserver(observer);
        var toolLoopStarted = 0;
        observer.ReceivedThinkingBegin += () => liveTurn.Publish(
            new StreamEventDto("meta", new { phase = "reasoning-start" }),
            phase: "reasoning-start"
        );
        observer.ReceivedThinkingEnd += () => liveTurn.Publish(
            new StreamEventDto("meta", new { phase = "reasoning-end" }),
            phase: "reasoning-end"
        );
        observer.ReceivedReasoningDelta += delta => liveTurn.Publish(new StreamEventDto("reasoning-delta", new { delta }));
        var textFilter = new InlineThinkTextFilter(startInsideThink: liveTurn.Options.AutoPrefillThinkOpenTag);
        observer.ReceivedTextDelta += delta => {
            var visibleText = textFilter.Filter(delta);
            if (string.IsNullOrEmpty(visibleText)) { return; }
            liveTurn.Publish(new StreamEventDto("text-delta", new { delta = visibleText }));
        };
        observer.ReceivedToolCall += call => {
            if (Interlocked.Exchange(ref toolLoopStarted, 1) == 0) {
                liveTurn.Publish(new StreamEventDto("meta", new { phase = "tool-loop-start" }), phase: "tool-loop-start");
            }

            liveTurn.Publish(
                new StreamEventDto("meta", new { phase = "tool-call", toolName = call.ToolName, toolCallId = call.ToolCallId }),
                phase: "tool-call"
            );
        };

        ChatSessionTurnResult turnResult;
        try {
            using var behaviorScope = FamilyChatCompletionExecutionContext.Push(
                FamilyChatTurnBehavior.FromUserAndTurn(host.User, liveTurn.Options)
            );
            turnResult = await host.Engine.SendMessageAsync(promptedUserMessage, observer, ct).ConfigureAwait(false);
        }
        catch (ChatSessionTurnAbortedException ex) {
            DebugUtil.Warning(
                "FamilyChat.Session",
                $"RunTurnAsync completion aborted: user={host.User.UserId}, turnId={liveTurn.TurnId}, termination={ex.Termination.Kind}, providerReason={ex.Termination.ProviderReason ?? "<none>"}, detail={ex.Termination.Detail ?? "<none>"}"
            );
            if (liveTurn.StopRequested && WasStoppedByObserver(ex.Termination)) {
                throw new FamilyChatTurnException(
                    "已停止生成，本轮结果未写入历史。你可以调整开关或修改输入后重试。",
                    "stopped-by-user"
                );
            }

            throw new FamilyChatTurnException(
                "模型本次输出未正常结束，本轮结果已放弃写入历史。请刷新页面后重试。",
                ex.Termination.ProviderReason ?? ex.Termination.Kind.ToString()
            );
        }
        var snapshot = BuildRecentTurnsResponse(host.Engine);
        DebugUtil.Info(
            "FamilyChat.Session",
            $"RunTurnAsync send done: user={host.User.UserId}, turnId={liveTurn.TurnId}, errors={turnResult.Errors?.Count ?? 0}, snapshotTurns={snapshot.Turns.Count}, recapVisible={snapshot.Turns.Any(static x => x.IsRecap)}, head={host.Engine.PersistedHeadAddress}"
        );

        if (Volatile.Read(ref toolLoopStarted) == 1) {
            liveTurn.Publish(new StreamEventDto("meta", new { phase = "tool-loop-finish" }), phase: "tool-loop-finish");
        }

        liveTurn.Publish(
            new StreamEventDto(
                "done",
                new {
                    recentTurns = snapshot.Turns,
                    toolCallsExecuted = turnResult.ToolCallsExecuted,
                    errors = turnResult.Errors
                }
            ),
            status: "completed"
        );

        // ── Post-generation compaction ──────────────────────────────
        // Compact *after* the response is sent so the user spends the
        // reading / typing time waiting, not the generation latency.
        if (host.Engine.GetStatistics().EstimatedTokens >= host.User.CompactionThresholdTokens) {
            using var compactCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            DebugUtil.Info("FamilyChat.Session", $"RunTurnAsync post-compaction trigger: user={host.User.UserId}, turnId={liveTurn.TurnId}, head={host.Engine.PersistedHeadAddress}");
            await host.Engine.CompactAsync(
                host.User.CompactionSystemPrompt!,
                host.User.CompactionPrompt!,
                compactCts.Token
            ).ConfigureAwait(false);
            DebugUtil.Info("FamilyChat.Session", $"RunTurnAsync post-compaction done: user={host.User.UserId}, turnId={liveTurn.TurnId}, {host.Engine.GetDebugStateSummary()}");
        }
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
        var completionClient = new FamilyChatCompletionClientDecorator(
            _completionClientFactory.Create(_config.Backend, user)
        );
        var runtime = new ChatSessionRuntime(
            CompletionClient: completionClient,
            CompletionSurfaceId: user.CompletionSurfaceId,
            ToolSession: new ToolRegistry(Array.Empty<ITool>()).CreateSession()
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

        // Reflect config-level prompt changes into the persisted session so that
        // editing config.json / prompts/*.md and restarting the server is enough
        // to update the system prompt for existing sessions.
        if (engine.TrySyncSystemPrompt(user.SystemPrompt)) {
            DebugUtil.Info("FamilyChat.Session", $"CreateSessionAsync: system prompt synced from config for user={user.UserId}");
        }

        DebugUtil.Info("FamilyChat.Session", $"CreateSessionAsync: user={user.UserId}, sessionDir={sessionDir}, {engine.GetDebugStateSummary()}");

        return new UserSessionHost(user, engine, completionClient);
    }

    private async Task<string> NormalizeUserMessageAsync(FamilyChatLiveTurn liveTurn, CancellationToken ct) {
        string original = liveTurn.UserMessage;
        if (!_userMessageNormalizer.ShouldNormalize(original)) { return original; }

        liveTurn.Publish(
            new StreamEventDto("meta", new { phase = "input-normalization-start" }),
            phase: "input-normalization-start"
        );

        try {
            string effective = await _userMessageNormalizer.NormalizeAsync(original, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(effective)) {
                effective = original;
            }
            bool changed = !string.Equals(original, effective, StringComparison.Ordinal);
            liveTurn.Publish(
                new StreamEventDto("meta", new { phase = "input-normalization-finish", changed }),
                phase: "input-normalization-finish"
            );
            return effective;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        }
        catch (Exception ex) {
            DebugUtil.Warning(
                "FamilyChat.Session",
                $"NormalizeUserMessageAsync fallback to original: turnId={liveTurn.TurnId}, input={Preview(original)}, error={ex.Message}"
            );
            liveTurn.Publish(
                new StreamEventDto("meta", new { phase = "input-normalization-finish", changed = false, fallback = true }),
                phase: "input-normalization-finish"
            );
            return original;
        }
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

        if (textBuilder.Length == 0 && reasoningBuilder.Length == 0) { return null; }

        var cleanedText = StripInlineThinkBlocks(textBuilder.ToString());
        string? reasoningText = reasoningBuilder.Length == 0 ? null : reasoningBuilder.ToString();
        return new AssistantMessageDto(
            Text: cleanedText,
            ReasoningText: reasoningText,
            HasReasoning: !string.IsNullOrEmpty(reasoningText)
        );
    }

    /// <summary>
    /// Removes inline <c>&lt;think&gt;...&lt;/think&gt;</c> blocks from the text.
    /// If the closing tag is missing, the text from <c>&lt;think&gt;</c> onward is dropped.
    /// </summary>
    private static string StripInlineThinkBlocks(string text) {
        return InlineThinkTextFilter.StripInlineThinkBlocks(text);
    }

    private static bool WasStoppedByObserver(CompletionTermination termination) {
        ArgumentNullException.ThrowIfNull(termination);

        if (termination.Kind is not CompletionTerminationKind.Incomplete) { return false; }

        return termination.Detail?.Contains("Streaming observer stopped", StringComparison.Ordinal) == true;
    }

    internal static string WrapUserMessageForEngine(string userMessage) {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
        return UserMessagePrefix + userMessage + UserMessageSuffix;
    }

    internal static string NormalizeUserMessageForDisplay(string? storedUserMessage) {
        if (string.IsNullOrEmpty(storedUserMessage)) { return string.Empty; }

        if (storedUserMessage.StartsWith(UserMessagePrefix, StringComparison.Ordinal)
            && storedUserMessage.EndsWith(UserMessageSuffix, StringComparison.Ordinal)) {
            return storedUserMessage.Substring(
                UserMessagePrefix.Length,
                storedUserMessage.Length - UserMessagePrefix.Length - UserMessageSuffix.Length
            );
        }

        return storedUserMessage;
    }

    private static string DescribeTurn(RecentTurnDto? turn) {
        if (turn is null) { return "<null>"; }
        if (turn.IsRecap) { return $"recap={Preview(turn.Assistant.Text)}"; }
        return $"user={Preview(turn.UserText)}, assistant={Preview(turn.Assistant.Text)}";
    }

    private static IReadOnlyList<RecentTurnDto> ProjectRecentTurnsResponse(IReadOnlyList<RecentTurnDto> allTurns, int maxTurns) {
        ArgumentNullException.ThrowIfNull(allTurns);

        var projectedTurns = allTurns.Count <= maxTurns
            ? new List<RecentTurnDto>(allTurns)
            : allTurns.Take(maxTurns).ToList();

        int recapIndex = FindRecapIndex(allTurns);
        if (recapIndex < 0 || recapIndex < maxTurns) { return projectedTurns; }

        // Optional boundary hint: include the first turn immediately after the recap
        // if it fell outside maxTurns, so the UI can see where the uncompressed range starts.
        if (recapIndex > 0 && recapIndex - 1 >= maxTurns) {
            projectedTurns.Add(allTurns[recapIndex - 1]);
        }

        projectedTurns.Add(allTurns[recapIndex]);
        return projectedTurns;
    }

    private static int FindRecapIndex(IReadOnlyList<RecentTurnDto> turns) {
        for (int i = 0; i < turns.Count; i++) {
            if (turns[i].IsRecap) { return i; }
        }

        return -1;
    }

    private static string Preview(string? text) {
        if (string.IsNullOrWhiteSpace(text)) { return "<null>"; }
        string normalized = text.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
        return normalized.Length <= 120 ? normalized : normalized[..120] + "...";
    }
}

public sealed class UserSessionHost : IAsyncDisposable {
    private readonly ICompletionClient _completionClient;
    private readonly object _turnStateGate = new();
    private FamilyChatLiveTurn? _currentTurn;
    private FamilyChatLiveTurn? _lastTurn;

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

    internal FamilyChatLiveTurn StartTurn(string userMessage, FamilyChatTurnOptions options) {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
        var liveTurn = new FamilyChatLiveTurn(userMessage, options);
        lock (_turnStateGate) {
            _lastTurn = null;
            _currentTurn = liveTurn;
        }

        return liveTurn;
    }

    internal FamilyChatLiveTurn? GetCurrentTurn() {
        lock (_turnStateGate) {
            return _currentTurn;
        }
    }

    internal FamilyChatLiveTurn? FindTurn(string turnId) {
        ArgumentException.ThrowIfNullOrWhiteSpace(turnId);

        lock (_turnStateGate) {
            if (string.Equals(_currentTurn?.TurnId, turnId, StringComparison.Ordinal)) { return _currentTurn; }

            if (string.Equals(_lastTurn?.TurnId, turnId, StringComparison.Ordinal)) { return _lastTurn; }

            return null;
        }
    }

    internal void FinishTurn(FamilyChatLiveTurn turn) {
        ArgumentNullException.ThrowIfNull(turn);

        lock (_turnStateGate) {
            if (ReferenceEquals(_currentTurn, turn)) {
                _currentTurn = null;
                _lastTurn = turn;
            }
            else if (ReferenceEquals(_lastTurn, turn)) {
                _lastTurn = turn;
            }
        }
    }

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
        if (string.IsNullOrWhiteSpace(configPath)) { throw new InvalidOperationException("FamilyChat config path must not be blank."); }

        string resolvedPath = Path.GetFullPath(configPath);
        if (!File.Exists(resolvedPath)) {
            throw new FileNotFoundException(
                $"FamilyChat config file was not found: {resolvedPath}",
                resolvedPath
            );
        }

        var json = File.ReadAllText(resolvedPath);
        var config = JsonSerializer.Deserialize(json, FamilyChatJsonContext.Default.FamilyChatConfig);
        if (config is null) { throw new InvalidOperationException($"Failed to deserialize FamilyChat config: {resolvedPath}"); }

        if (config.Users.Count == 0) { throw new InvalidOperationException("FamilyChat config must contain at least one user."); }

        config = ResolveSystemPromptFiles(config, resolvedPath);
        config = ApplyDefaultCompactionPrompts(config);

        Validate(config);
        return config;
    }

    private static FamilyChatConfig ResolveSystemPromptFiles(FamilyChatConfig config, string configPath) {
        string configDir = Path.GetDirectoryName(configPath)
            ?? throw new InvalidOperationException($"Cannot determine config directory for: {configPath}");

        var resolvedUsers = new List<FamilyChatUserConfig>(config.Users.Count);
        foreach (var user in config.Users) {
            if (string.IsNullOrWhiteSpace(user.SystemPromptFile)) {
                resolvedUsers.Add(user);
                continue;
            }

            string promptPath = Path.GetFullPath(user.SystemPromptFile, configDir);
            if (!File.Exists(promptPath)) {
                throw new FileNotFoundException(
                    $"FamilyChat user '{user.UserId}' systemPromptFile was not found: {promptPath}",
                    promptPath
                );
            }

            string promptText = File.ReadAllText(promptPath).Trim();
            resolvedUsers.Add(user with { SystemPrompt = promptText });
        }

        return config with { Users = resolvedUsers };
    }

    private static FamilyChatConfig ApplyDefaultCompactionPrompts(FamilyChatConfig config) {
        var normalizedUsers = new List<FamilyChatUserConfig>(config.Users.Count);
        foreach (var user in config.Users) {
            normalizedUsers.Add(
                user with {
                    CompactionSystemPrompt = user.CompactionSystemPrompt ?? FamilyChatDefaults.CompactionSystemPrompt,
                    CompactionPrompt = user.CompactionPrompt ?? FamilyChatDefaults.CompactionPrompt,
                }
            );
        }

        return config with { Users = normalizedUsers };
    }

    private static void Validate(FamilyChatConfig config) {
        var userIds = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < config.Users.Count; i++) {
            var user = config.Users[i];
            if (string.IsNullOrWhiteSpace(user.UserId)) { throw new InvalidOperationException($"FamilyChat config user[{i}] must have a non-empty userId."); }

            if (!userIds.Add(user.UserId)) { throw new InvalidOperationException($"FamilyChat config contains duplicate userId '{user.UserId}'."); }

            if (string.IsNullOrWhiteSpace(user.Password)) { throw new InvalidOperationException($"FamilyChat config user '{user.UserId}' must have a non-empty password."); }

            if (string.IsNullOrWhiteSpace(user.SessionDir)) { throw new InvalidOperationException($"FamilyChat config user '{user.UserId}' must have a non-empty sessionDir."); }

            if (string.IsNullOrWhiteSpace(user.SystemPrompt)) {
                throw new InvalidOperationException(
                    $"FamilyChat config user '{user.UserId}' must provide a non-empty systemPrompt "
                    + "(either inline via 'systemPrompt' or by pointing 'systemPromptFile' at a non-empty file)."
                );
            }

            if (string.IsNullOrWhiteSpace(user.CompactionSystemPrompt)) { throw new InvalidOperationException($"FamilyChat config user '{user.UserId}' must have a non-empty compactionSystemPrompt."); }

            if (string.IsNullOrWhiteSpace(user.CompactionPrompt)) { throw new InvalidOperationException($"FamilyChat config user '{user.UserId}' must have a non-empty compactionPrompt."); }
        }

        if (config.ListenUrls is null) { return; }

        for (int i = 0; i < config.ListenUrls.Count; i++) {
            if (string.IsNullOrWhiteSpace(config.ListenUrls[i])) { throw new InvalidOperationException($"FamilyChat config listenUrls[{i}] must not be blank."); }
        }
    }
}

internal static class FamilyChatConfigBootstrapper {
    public static void EnsureExistsOrBootstrap(string configPath) {
        if (string.IsNullOrWhiteSpace(configPath)) { throw new InvalidOperationException("FamilyChat config path must not be blank."); }

        string resolvedPath = Path.GetFullPath(configPath);
        if (File.Exists(resolvedPath)) { return; }

        string? parentDir = Path.GetDirectoryName(resolvedPath);
        if (string.IsNullOrWhiteSpace(parentDir)) { throw new InvalidOperationException($"Cannot determine parent directory for FamilyChat config path: {resolvedPath}"); }

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

internal static class FamilyChatDefaults {
    public const string SystemPrompt =
        "你是家庭局域网里的私人助手。优先用简洁、直接、可信的中文回答。"
        + "不确定时明确说明不确定，不编造细节。";

    public const string CompactionSystemPrompt = @"你是一位专业的游戏书吏（Game Scribe），负责将漫长的冒险历程提炼成引人入胜的“前情提要”。你的任务不是简单地压缩文本，而是要捕捉故事的核心戏剧性：
  1. **聚焦动机与冲突**：识别并强调每个主要角色的核心目标、他们遇到的阻碍，以及角色之间的内在或外在冲突。
  2. **保留关键“变化”**：记录世界状态、人物关系或角色心境发生的关键转折点。
  3. **提炼悬念**：清晰地列出当前故事中悬而未决的问题、未解的谜团或迫在眉睫的危机。
  4. **使用第三人称**：始终使用角色的名字进行叙述，保持客观的史官视角。";

    public const string CompactionPrompt = @"请根据以上对话历史，撰写一段承前启后的中文剧情摘要。摘要需要清晰且充满张力，并至少包括以下结构：
  1. **【主要角色间的重要交互历史】**：重要！这是防止两个角色前一天刚海誓山盟，后一天因遗忘而又回退到保持距离的关键信息。迭代时摘要旧条目，添加新条目。
  2. **【当前局势】**：概括主要角色们目前所处的地点、时间和核心情境。
  3. **【角色动态与内心驱动】**：分点阐述每个主要角色：
      * 他们最新的状态是怎样的？
      * 他们当前正在做什么？
      * 他们内心最迫切的目标或欲望是什么？
      * 他们与其他角色的关系（如信任、怀疑、联盟、敌对）的状态与最新变化？
  4. **【悬念与线索】**：分点列出故事中所有待解决的谜题、未完成的任务、隐藏的线索或潜在的威胁。这些是推动故事继续发展的关键。";
}

internal static class FamilyChatConfigTemplateFactory {
    public const string PlaceholderModelId = "REPLACE_WITH_YOUR_LOCAL_MODEL_ID";

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
            SystemPrompt: FamilyChatDefaults.SystemPrompt,
            CompactionThresholdTokens: 32000,
            CompactionSystemPrompt: FamilyChatDefaults.CompactionSystemPrompt,
            CompactionPrompt: FamilyChatDefaults.CompactionPrompt
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

    public static string RenderAppPage(FamilyChatUserConfig user) {
        ArgumentNullException.ThrowIfNull(user);
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

    <section class="composer">
      <form id="chat-form">
        <textarea id="message-input" rows="3" placeholder="说点什么……" required></textarea>
        <label class="composer-option">
          <input id="auto-repair-missing-think-open-tag" type="checkbox">
          <span>强制以 <code>&lt;think&gt;</code> 续写思考模式</span>
        </label>
        <div class="composer-actions">
          <div class="composer-status">
            <span id="composer-mode-hint" class="eyebrow hidden"></span>
            <span id="status-text" class="status-text"></span>
          </div>
          <div class="composer-buttons">
            <button id="undo-last-button" type="button" class="ghost-button">撤销上一轮</button>
            <button id="stop-button" type="button" class="ghost-button">停止</button>
            <button id="send-button" type="submit">发送</button>
          </div>
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

    <button id="scroll-to-top" class="scroll-to-top" title="回到顶端">↑ 回到顶端</button>
  </main>

  <script>
    window.familyChatBootstrap = {
      displayName: {{JsonSerializer.Serialize(user.DisplayName, FamilyChatJson.Options)}},
      userId: {{JsonSerializer.Serialize(user.UserId, FamilyChatJson.Options)}},
      modelId: {{JsonSerializer.Serialize(user.ModelId, FamilyChatJson.Options)}},
      defaultAutoPrefillThinkOpenTag: {{JsonSerializer.Serialize(FamilyChatThinkRepairDefaults.ShouldEnableForModel(user.ModelId), FamilyChatJson.Options)}}
    };
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
