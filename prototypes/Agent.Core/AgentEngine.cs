using Atelia.Agent.Core.App;
using Atelia.Agent.Core.History;
using Atelia.Agent.Core.Tool;
using Atelia.Completion.Abstractions;
using Atelia.Diagnostics;

namespace Atelia.Agent.Core;

/// <summary>
/// Agent 执行引擎，负责管理 Agent 状态机、工具调度与模型交互的核心协调器。
/// </summary>
/// <remarks>
/// 警告：此类型非线程安全，不支持并发使用。
/// 所有公开方法（包括异步方法）都不应从多个线程同时调用。
/// 如需并发执行多个 Agent，请为每个执行上下文创建独立的 <see cref="AgentEngine"/> 实例。
/// </remarks>
public partial class AgentEngine {
    private const string ProviderDebugCategory = "Provider";
    private const string StateMachineDebugCategory = "StateMachine";

    private readonly AgentState _state;
    private readonly DefaultAppHost _appHost;
    private readonly Dictionary<string, ITool> _standaloneTools;
    private readonly Dictionary<string, LodToolCallResult> _pendingToolResults = new(StringComparer.OrdinalIgnoreCase);
    private readonly IIdleObservationProvider _idleProvider;
    private readonly Func<DateTimeOffset> _utcNowProvider;
    private readonly AutoCompactionOptions? _autoCompactionOptions;

    private ToolExecutor? _toolExecutor;
    private ToolExecutor ToolExecutor => EnsureToolsBuilt();
    private bool _toolsDirty;
    private AgentRunState? _lastLoggedState;

    /// <summary>
    /// 初始化 <see cref="AgentEngine"/> 的新实例。
    /// </summary>
    /// <param name="state">Agent 状态实例，如为 <c>null</c> 则创建默认状态。</param>
    /// <param name="initialApps">初始注册的应用列表（可选）。</param>
    /// <param name="initialTools">初始注册的独立工具列表（可选）。</param>
    /// <param name="idleProvider">在无外部输入且无待处理通知时产生"心跳观测"的提供器；
    /// 传 <c>null</c> 使用默认的 <see cref="TimestampHeartbeatObservationProvider"/>。</param>
    /// <param name="utcNowProvider">UTC 时间源（可选，主要供测试注入）。</param>
    /// <param name="autoCompaction">自动上下文压缩配置（可选）。
    /// 传 <c>null</c> 不启用自动压缩触发，仅保留手动 <see cref="RequestCompaction"/> 路径。</param>
    public AgentEngine(
        AgentState? state = null,
        IEnumerable<IApp>? initialApps = null,
        IEnumerable<ITool>? initialTools = null,
        IIdleObservationProvider? idleProvider = null,
        Func<DateTimeOffset>? utcNowProvider = null,
        AutoCompactionOptions? autoCompaction = null
    ) {
        _state = state ?? AgentState.CreateDefault();
        _appHost = new DefaultAppHost();
        _standaloneTools = new Dictionary<string, ITool>(StringComparer.OrdinalIgnoreCase);
        _toolsDirty = true;
        _idleProvider = idleProvider ?? new TimestampHeartbeatObservationProvider();
        _utcNowProvider = utcNowProvider ?? (() => DateTimeOffset.UtcNow);
        _autoCompactionOptions = autoCompaction;

        RegisterDefaultEnginePanels();

        if (initialApps is not null) {
            foreach (var app in initialApps) {
                if (app is null) { continue; }
                RegisterApp(app);
            }
        }

        if (initialTools is not null) {
            foreach (var tool in initialTools) {
                if (tool is null) { continue; }
                RegisterTool(tool);
            }
        }

        EnsureToolsBuilt();
    }

    /// <summary>
    /// 获取当前 Agent 状态实例。
    /// </summary>
    public AgentState State => _state;

    /// <summary>
    /// 获取当前系统指令。
    /// </summary>
    public string SystemPrompt => _state.SystemPrompt;

    private void RegisterDefaultEnginePanels() {
        RegisterApp(new EnginePanelApp(this));
    }

    /// <summary>
    /// 注册一个应用（App）及其提供的工具。
    /// </summary>
    /// <param name="app">要注册的应用实例。</param>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> 为 <c>null</c>。</exception>
    /// <exception cref="InvalidOperationException">应用提供的工具名称与已注册工具冲突。</exception>
    /// <remarks>此操作非线程安全，不应与其他方法并发调用。</remarks>
    public void RegisterApp(IApp app) {
        if (app is null) { throw new ArgumentNullException(nameof(app)); }

        EnsureNoToolConflicts(app.Tools, replacingAppName: app.Name);
        _appHost.RegisterApp(app);
        _toolsDirty = true;
    }

    /// <summary>
    /// 移除已注册的应用。
    /// </summary>
    /// <param name="name">要移除的应用名称。</param>
    /// <returns>如成功移除返回 <c>true</c>；否则返回 <c>false</c>。</returns>
    /// <remarks>此操作非线程安全，不应与其他方法并发调用。</remarks>
    public bool RemoveApp(string name) {
        if (string.IsNullOrWhiteSpace(name)) { return false; }

        var removed = _appHost.RemoveApp(name);
        if (!removed) { return false; }

        _toolsDirty = true;
        return true;
    }

    /// <summary>
    /// 注册一个独立工具（不属于任何应用）。
    /// </summary>
    /// <param name="tool">要注册的工具实例。</param>
    /// <exception cref="ArgumentNullException"><paramref name="tool"/> 为 <c>null</c>。</exception>
    /// <exception cref="InvalidOperationException">工具名称与已注册工具冲突。</exception>
    /// <remarks>此操作非线程安全，不应与其他方法并发调用。</remarks>
    public void RegisterTool(ITool tool) {
        if (tool is null) { throw new ArgumentNullException(nameof(tool)); }

        if (_standaloneTools.ContainsKey(tool.Name)) { throw new InvalidOperationException($"Duplicate tool registration detected for '{tool.Name}'."); }

        EnsureToolNameAvailable(tool.Name);
        _standaloneTools[tool.Name] = tool;
        _toolsDirty = true;
    }

    /// <summary>
    /// 移除已注册的独立工具。
    /// </summary>
    /// <param name="name">要移除的工具名称。</param>
    /// <returns>如成功移除返回 <c>true</c>；否则返回 <c>false</c>。</returns>
    /// <remarks>此操作非线程安全，不应与其他方法并发调用。</remarks>
    public bool RemoveTool(string name) {
        if (string.IsNullOrWhiteSpace(name)) { return false; }

        if (!_standaloneTools.Remove(name)) { return false; }

        _toolsDirty = true;
        return true;
    }

    private ToolExecutor EnsureToolsBuilt() {
        if (!_toolsDirty && _toolExecutor is not null) { return _toolExecutor; }

        var aggregate = new List<ITool>();

        if (!_appHost.Tools.IsDefaultOrEmpty) {
            foreach (var tool in _appHost.Tools) {
                if (tool is not null) {
                    aggregate.Add(tool);
                }
            }
        }

        if (_standaloneTools.Count > 0) {
            aggregate.AddRange(_standaloneTools.Values);
        }

        var executor = new ToolExecutor(aggregate);
        _toolExecutor = executor;
        _toolsDirty = false;

        // Capture a snapshot for diagnostics so日志能体现当前可见工具数量。
        var visibleDefinitions = executor.GetVisibleToolDefinitions();
        DebugUtil.Info(
            StateMachineDebugCategory,
            $"[Engine] Tool cache rebuilt all={executor.AllToolDefinitions.Length} visible={visibleDefinitions.Length}"
        );
        return executor;
    }

    /// <summary>
    /// 向 Agent 追加主机通知（Host Notification）。
    /// </summary>
    /// <param name="notificationContent">通知内容（包含基础与详细两级）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="notificationContent"/> 为 <c>null</c>。</exception>
    /// <remarks>此方法非线程安全，不应与其他方法并发调用。</remarks>
    public void AppendNotification(LevelOfDetailContent notificationContent) {
        if (notificationContent is null) { throw new ArgumentNullException(nameof(notificationContent)); }
        _state.AppendNotification(notificationContent);
        DebugUtil.Trace(StateMachineDebugCategory, $"[Engine] Host notification appended basicLength={notificationContent.Basic.Length}");
    }

    /// <summary>
    /// 向 Agent 追加主机通知（Host Notification）。
    /// </summary>
    /// <param name="basic">基础通知文本。</param>
    /// <param name="detail">详细通知文本（可选，默认同 <paramref name="basic"/>）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="basic"/> 为 <c>null</c>。</exception>
    /// <remarks>此方法非线程安全，不应与其他方法并发调用。</remarks>
    public void AppendNotification(string basic, string? detail = null) {
        if (basic is null) { throw new ArgumentNullException(nameof(basic)); }
        var content = new LevelOfDetailContent(basic, detail ?? basic);
        AppendNotification(content);
    }

    private void EnsureToolNameAvailable(string toolName) {
        if (string.IsNullOrWhiteSpace(toolName)) { throw new ArgumentException("Tool name must not be null or whitespace.", nameof(toolName)); }

        if (IsToolNameInUse(toolName)) { throw new InvalidOperationException($"Duplicate tool registration detected for '{toolName}'."); }
    }

    private void EnsureNoToolConflicts(IReadOnlyList<ITool>? tools, string? replacingAppName) {
        if (tools is null || tools.Count == 0) { return; }

        var existingNames = GatherExistingToolNames(replacingAppName);
        var newNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tool in tools) {
            if (tool is null) { continue; }

            if (!newNames.Add(tool.Name)) { throw new InvalidOperationException($"App '{replacingAppName ?? "<unknown>"}' attempted to register duplicate tool name '{tool.Name}'."); }

            if (existingNames.Contains(tool.Name)) { throw new InvalidOperationException($"Tool name conflict detected for '{tool.Name}'."); }
        }
    }

    private HashSet<string> GatherExistingToolNames(string? excludingAppName) {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!_appHost.Apps.IsDefaultOrEmpty) {
            foreach (var app in _appHost.Apps) {
                if (app is null) { continue; }

                if (!string.IsNullOrWhiteSpace(excludingAppName) && string.Equals(app.Name, excludingAppName, StringComparison.OrdinalIgnoreCase)) { continue; }

                if (app.Tools is { Count: > 0 }) {
                    foreach (var tool in app.Tools) {
                        if (tool is not null) {
                            names.Add(tool.Name);
                        }
                    }
                }
            }
        }

        foreach (var tool in _standaloneTools.Values) {
            names.Add(tool.Name);
        }

        return names;
    }

    private bool IsToolNameInUse(string toolName) {
        if (_standaloneTools.ContainsKey(toolName)) { return true; }

        if (!_appHost.Apps.IsDefaultOrEmpty) {
            foreach (var app in _appHost.Apps) {
                if (app?.Tools is not { Count: > 0 }) { continue; }

                foreach (var tool in app.Tools) {
                    if (tool is null) { continue; }
                    if (string.Equals(tool.Name, toolName, StringComparison.OrdinalIgnoreCase)) { return true; }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 当 Agent 处于等待输入状态时触发。
    /// </summary>
    public event EventHandler<WaitingInputEventArgs>? WaitingInput;

    /// <summary>
    /// 在构造模型请求前触发，用于决议本次调用的最终 LLM 配置文件。
    /// </summary>
    public event EventHandler<ResolveProfileEventArgs>? ResolveProfile;

    /// <summary>
    /// 在真正构造模型请求前执行一次可等待的准备阶段。
    /// 适用于刷新 orchestrator、App snapshot 或工具可见性等会影响当前轮请求的外部状态。
    /// 仅支持单个处理器；如需组合多个准备步骤，请在宿主侧自行封装一个顺序调用链。
    /// </summary>
    public PrepareInvocationAsyncHandler? PrepareInvocationAsync { get; set; }

    /// <summary>
    /// 当 Agent 状态发生转换时触发。
    /// </summary>
    public event EventHandler<StateTransitionEventArgs>? StateTransition;

    /// <summary>
    /// 执行 Agent 状态机的单步推进。
    /// </summary>
    /// <param name="profile">LLM 配置文件。</param>
    /// <param name="cancellationToken">取消令牌（可选）。</param>
    /// <returns>包含本次步进结果的 <see cref="AgentStepResult"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> 为 <c>null</c>。</exception>
    /// <exception cref="InvalidOperationException">
    /// 当 <paramref name="profile"/> 与当前 Turn 已锁定的模型（由首次模型调用确立）不一致时抛出。
    /// </exception>
    /// <remarks>
    /// 此方法非线程安全，不应与其他方法并发调用。
    /// 每次调用将根据当前状态执行相应操作（等待输入、调用模型、执行工具等）。
    /// <para>
    /// <b>Turn 锁定约束</b>：一个 Turn 由最近一条 <see cref="History.ObservationEntry"/> 起至历史末尾的连续段构成。
    /// 在 Turn 内的所有模型调用必须使用同一 <see cref="LlmProfile"/>（按 Provider/ApiSpec/Model 三元组比对）。
    /// 切换 profile 仅允许在 Turn 起点（即没有未完结的工具往返时）进行。
    /// 该约束为后续 thinking/reasoning 内容的窗口化注入提供前提保证。
    /// </para>
    /// </remarks>
    public Task<AgentStepResult> StepAsync(LlmProfile profile, CancellationToken cancellationToken = default) {
        return StepAsync(profile, completionObserver: null, cancellationToken);
    }

    /// <summary>
    /// 执行 Agent 状态机的单步推进，并将本次模型调用的流式输出转发给指定 observer。
    /// </summary>
    /// <param name="profile">LLM 配置文件。</param>
    /// <param name="completionObserver">本次真实模型调用使用的流式观察者；当前步若未触发模型调用则忽略。</param>
    /// <param name="cancellationToken">取消令牌（可选）。</param>
    /// <returns>包含本次步进结果的 <see cref="AgentStepResult"/>。</returns>
    /// <remarks>
    /// 每次 <see cref="StepAsync(LlmProfile, CompletionStreamObserver?, CancellationToken)"/> 最多只会转发一次真实模型调用。
    /// 如宿主需要跨多步持续观察一个完整 turn（例如工具往返后的二次模型调用），应在外层推进循环中为每次步进传入新的 observer。
    /// </remarks>
    public async Task<AgentStepResult> StepAsync(
        LlmProfile profile,
        CompletionStreamObserver? completionObserver,
        CancellationToken cancellationToken = default
    ) {
        if (profile is null) { throw new ArgumentNullException(nameof(profile)); }

        var stateBefore = DetermineState();
        LogStateIfChanged(stateBefore);

        var outcome = await ExecuteStateAsync(stateBefore, profile, completionObserver, cancellationToken).ConfigureAwait(false);

        var stateAfter = DetermineState();
        LogStateIfChanged(stateAfter);

        if (stateAfter != stateBefore) {
            OnStateTransition(stateBefore, stateAfter);
        }

        if (stateAfter == AgentRunState.WaitingInput) {
            ResetStateLogging();
        }

        return CreateStepResult(stateBefore, stateAfter, outcome);
    }

    protected virtual void OnWaitingInput(WaitingInputEventArgs e) {
        WaitingInput?.Invoke(this, e);
    }

    protected virtual void OnResolveProfile(ResolveProfileEventArgs e) {
        ResolveProfile?.Invoke(this, e);
    }

    protected virtual Task OnPrepareInvocationAsync(PrepareInvocationEventArgs e, CancellationToken cancellationToken) {
        return PrepareInvocationAsync is { } prepare
            ? prepare(e, cancellationToken)
            : Task.CompletedTask;
    }

    protected virtual void OnStateTransition(AgentRunState from, AgentRunState to) {
        StateTransition?.Invoke(this, new StateTransitionEventArgs(from, to));
    }

    private AgentRunState DetermineState() {
        if (_compactionRequest.HasValue) { return AgentRunState.Compacting; }
        if (_state.RecentHistory.Count == 0) { return AgentRunState.WaitingInput; }

        var last = _state.RecentHistory[^1];
        return last switch {
            ToolResultsEntry => AgentRunState.PendingToolResults,
            ObservationEntry => AgentRunState.PendingInput,
            ActionEntry outputEntry => DetermineOutputState(outputEntry),
            _ => AgentRunState.WaitingInput
        };
    }

    private AgentRunState DetermineOutputState(ActionEntry outputEntry) {
        if (outputEntry.Message.ToolCalls is not { Count: > 0 }) { return AgentRunState.WaitingInput; }
        return HasAllToolResults(outputEntry)
            ? AgentRunState.ToolResultsReady
            : AgentRunState.WaitingToolResults;
    }

    private async Task<StepOutcome> ExecuteStateAsync(
        AgentRunState state,
        LlmProfile profile,
        CompletionStreamObserver? completionObserver,
        CancellationToken cancellationToken
    ) {
        switch (state) {
            case AgentRunState.WaitingInput:
                return ProcessWaitingInput();
            case AgentRunState.PendingInput:
            case AgentRunState.PendingToolResults:
                return await ProcessPendingModelCallAsync(state, profile, completionObserver, cancellationToken).ConfigureAwait(false);
            case AgentRunState.WaitingToolResults:
                return await ProcessWaitingToolResultsAsync(cancellationToken).ConfigureAwait(false);
            case AgentRunState.ToolResultsReady:
                return ProcessToolResultsReady();
            case AgentRunState.Compacting:
                return await ProcessCompactingAsync(profile, cancellationToken).ConfigureAwait(false);
            default:
                return StepOutcome.NoProgress;
        }
    }

    private StepOutcome ProcessWaitingInput() {
        var lastEntry = _state.RecentHistory.Count > 0 ? _state.RecentHistory[^1] : null;
        var hasPendingNotification = _state.HasPendingNotification;
        var args = new WaitingInputEventArgs(hasPendingNotification, lastEntry);

        OnWaitingInput(args);

        if (!args.ShouldContinue) { return StepOutcome.NoProgress; }

        var observation = args.Observation;
        var inputEntry = observation?.Entry;
        var recentEvents = observation?.RecentEvents;

        if (inputEntry is null) {
            // 上游决定推进但未提供任何输入：若此时连 pending notification 也没有，调用 idle provider 填充
            // 一条内源性"心跳"通知。这避免向下游 provider 提交"完全空"的 user 消息（会被 Anthropic 等拒绝），
            // 同时使"空推进"这个语义变成可配置的宛转点而不是 provider 层隐式兼容。
            if (recentEvents is null && !_state.HasPendingNotification) {
                var idleContext = new IdleObservationContext {
                    UtcNow = _utcNowProvider(),
                    RecentHistory = _state.RecentHistory
                };

                var heartbeat = _idleProvider.CreateIdleNotification(idleContext);
                if (heartbeat is null) {
                    DebugUtil.Trace(StateMachineDebugCategory, "[Engine] Idle provider declined to produce a heartbeat; staying NoProgress.");
                    return StepOutcome.NoProgress;
                }

                recentEvents = heartbeat;
            }

            inputEntry = new ObservationEntry();
        }

        var appended = _state.AppendObservation(inputEntry, recentEvents);

        DebugUtil.Trace(StateMachineDebugCategory, $"[Engine] Inputs {appended}");

        return StepOutcome.FromInput(appended);
    }

    private async Task<StepOutcome> ProcessPendingModelCallAsync(
        AgentRunState state,
        LlmProfile profile,
        CompletionStreamObserver? completionObserver,
        CancellationToken cancellationToken
    ) {
        var resolveArgs = new ResolveProfileEventArgs(state, profile);
        OnResolveProfile(resolveArgs);

        if (resolveArgs.Cancel) { return StepOutcome.NoProgress; }
        if (resolveArgs.Profile is null) { throw new InvalidOperationException("ResolveProfile handlers must not set Profile to null."); }

        var resolvedProfile = resolveArgs.Profile;

        // Turn 锁定校验：ResolveProfile 阶段给出本次实际要调用的 profile 后，仅对最终结果做一次校验。
        EnsureProfileMatchesCurrentTurnLock(resolvedProfile);

        var estimatedContextTokens = EstimateCurrentContextTokens();

        // 软上下文上限检查：在最终 profile 已确定后、构造 liveContext 前执行。
        // 这样窗口渲染、上下文投影、cap 判定与最终调用 profile 始终保持一致。
        var cap = resolvedProfile.SoftContextTokenCap;
        if (estimatedContextTokens >= cap) {
            if (TryRequestAutoCompaction()) {
                DebugUtil.Info(
                    StateMachineDebugCategory,
                    $"[Engine] Soft context token cap hit (estimate>={cap}); deferring model call for auto compaction."
                );
                return StepOutcome.FromStateMutation();
            }
            // RequestCompaction 失败（如无合法切分点）时回退到正常调用，避免死锁。
            DebugUtil.Warning(
                StateMachineDebugCategory,
                $"[Engine] Soft cap hit but no valid split point; falling back to normal model call. cap={cap}"
            );
        }

        var prepareArgs = new PrepareInvocationEventArgs(state, resolvedProfile, estimatedContextTokens);
        await OnPrepareInvocationAsync(prepareArgs, cancellationToken).ConfigureAwait(false);

        if (prepareArgs.Cancel) { return StepOutcome.NoProgress; }

        var invocation = resolvedProfile.ToCompletionDescriptor();
        var renderContext = new AppRenderContext(
            CurrentProfile: resolvedProfile,
            EstimatedContextTokens: estimatedContextTokens,
            HasPendingCompaction: HasPendingCompaction
        );
        var projection = _state.ProjectInvocationContext(
            new ContextProjectionOptions(
                TargetInvocation: invocation,
                Windows: _appHost.RenderWindows(renderContext)
            )
        );
        var liveContext = projection.ToFlat();
        DebugUtil.Trace(ProviderDebugCategory, $"[Engine] Rendering context count={liveContext.Count}");

        var toolExecutor = ToolExecutor;
        var toolDefinitions = toolExecutor.GetVisibleToolDefinitions();

        var request = new CompletionRequest(resolvedProfile.ModelId, SystemPrompt, liveContext, toolDefinitions);

        var result = await resolvedProfile.Client.StreamCompletionAsync(request, completionObserver, cancellationToken).ConfigureAwait(false);
        EnsureCompletionInvocationMatchesExpected(invocation, result.Invocation);
        var aggregatedOutput = new ActionEntry(result.Message, invocation);

        DebugUtil.Info(
            ProviderDebugCategory,
            $"[Engine] Completion result blocks={result.Message.Blocks.Count} toolCalls={aggregatedOutput.Message.ToolCalls.Count} errors={result.Errors?.Count ?? 0}"
        );

        _pendingToolResults.Clear();

        var appended = _state.AppendAction(aggregatedOutput);

        var toolCallCount = appended.Message.ToolCalls?.Count ?? 0;
        var textLen = appended.Message.Blocks.OfType<ActionBlock.Text>().Sum(b => b.Content.Length);
        DebugUtil.Info(ProviderDebugCategory, $"[Engine] Model output appended textLen={textLen} toolCalls={toolCallCount}");

        return StepOutcome.FromOutput(appended);
    }

    private async Task<StepOutcome> ProcessWaitingToolResultsAsync(CancellationToken cancellationToken) {
        if (_state.RecentHistory.Count == 0 || _state.RecentHistory[^1] is not ActionEntry outputEntry) {
            DebugUtil.Warning(StateMachineDebugCategory, "[Engine] WaitingToolResults but no model output available");
            return StepOutcome.NoProgress;
        }

        var nextCall = FindNextPendingToolCall(outputEntry);
        if (nextCall is null) { return StepOutcome.NoProgress; }

        var toolExecutor = ToolExecutor;
        var result = await toolExecutor.ExecuteAsync(nextCall, cancellationToken).ConfigureAwait(false);
        _pendingToolResults[nextCall.ToolCallId] = result;

        var toolName = result.ToolName ?? nextCall.ToolName;
        DebugUtil.Info(StateMachineDebugCategory, $"[Engine] Tool executed toolName={toolName} callId={result.ToolCallId} status={result.ExecuteResult.Status}");

        return StepOutcome.FromToolExecution();
    }

    private StepOutcome ProcessToolResultsReady() {
        if (_state.RecentHistory.Count == 0 || _state.RecentHistory[^1] is not ActionEntry outputEntry) { return StepOutcome.NoProgress; }
        if (outputEntry.Message.ToolCalls is not { Count: > 0 }) { return StepOutcome.NoProgress; }

        var collectedResults = new List<LodToolCallResult>(outputEntry.Message.ToolCalls.Count);

        for (var index = 0; index < outputEntry.Message.ToolCalls.Count; index++) {
            var call = outputEntry.Message.ToolCalls[index];
            if (!_pendingToolResults.TryGetValue(call.ToolCallId, out var result)) {
                DebugUtil.Warning(StateMachineDebugCategory, $"[Engine] Missing tool execution result callId={call.ToolCallId}");
                return StepOutcome.NoProgress;
            }

            collectedResults.Add(result);
        }

        var failure = collectedResults.FirstOrDefault(static result => result.ExecuteResult.Status == ToolExecutionStatus.Failed);
        var executeError = failure is null
            ? null
            : failure.ExecuteResult.Result.GetContent(LevelOfDetail.Basic);

        var results = collectedResults.ToArray();
        var entry = new ToolResultsEntry(results, executeError);

        var appended = AppendToolResultsWithSummary(entry);
        _pendingToolResults.Clear();

        var failureCallId = failure?.ToolCallId ?? "none";
        DebugUtil.Info(StateMachineDebugCategory, $"[Engine] Tool results appended count={results.Length} failure={failureCallId}");

        return StepOutcome.FromToolResults(appended);
    }

    private RawToolCall? FindNextPendingToolCall(ActionEntry outputEntry) {
        if (outputEntry.Message.ToolCalls is not { Count: > 0 }) { return null; }

        foreach (var call in outputEntry.Message.ToolCalls) {
            if (!_pendingToolResults.ContainsKey(call.ToolCallId)) { return call; }
        }

        return null;
    }

    private bool HasAllToolResults(ActionEntry outputEntry) {
        if (outputEntry.Message.ToolCalls is not { Count: > 0 }) { return false; }
        if (_pendingToolResults.Count < outputEntry.Message.ToolCalls.Count) { return false; }

        foreach (var call in outputEntry.Message.ToolCalls) {
            if (!_pendingToolResults.ContainsKey(call.ToolCallId)) { return false; }
        }

        return true;
    }

    private ToolResultsEntry AppendToolResultsWithSummary(ToolResultsEntry entry) {
        return _state.AppendToolResults(entry);
    }

    private void ResetStateLogging() {
        _lastLoggedState = null;
    }

    /// <summary>
    /// 分析当前 Turn，并返回其显式边界与已锁定的模型身份。
    /// </summary>
    /// <remarks>
    /// Turn 起点判定：精确匹配 <see cref="HistoryEntryKind.Observation"/>。
    /// 注意 <see cref="ToolResultsEntry"/> 虽然继承自 <see cref="ObservationEntry"/>，但其 Kind 为 <see cref="HistoryEntryKind.ToolResults"/>，
    /// 不视为 Turn 起点（工具结果是 Turn 中段的环境反馈）。
    /// 若显式起点已被 Recap 裁剪，返回结果的 <c>StartIndex</c> 为 -1，但仍会保留可从残留片段推断出的锁定信息。
    /// </remarks>
    private CurrentTurnInfo AnalyzeCurrentTurn()
        => TurnAnalyzer.Analyze(_state.RecentHistory);

    private static string DescribeDescriptor(CompletionDescriptor descriptor)
        => $"{{Provider={descriptor.ProviderId}, ApiSpec={descriptor.ApiSpecId}, Model={descriptor.Model}}}";

    private static void EnsureCompletionInvocationMatchesExpected(CompletionDescriptor expected, CompletionDescriptor actual) {
        if (expected is null) { throw new ArgumentNullException(nameof(expected)); }
        if (actual is null) { throw new ArgumentNullException(nameof(actual)); }
        if (Equals(expected, actual)) { return; }

        throw new InvalidOperationException(
            "Completion client returned a CompletionResult.Invocation that does not match the requested profile. " +
            $"Expected {DescribeDescriptor(expected)}, but received {DescribeDescriptor(actual)}."
        );
    }

    private static string DescribeCurrentTurn(CurrentTurnInfo turn)
        => turn.HasExplicitStartBoundary
            ? $"CurrentTurn.StartIndex={turn.StartIndex}, EndIndex={turn.EndIndex}"
            : $"CurrentTurn.StartIndex=<recapped>, EndIndex={turn.EndIndex}";

    /// <summary>
    /// 校验给定 profile 是否与当前 Turn 已锁定的模型一致。Turn 起点（无锁定）时无条件通过。
    /// </summary>
    /// <exception cref="InvalidOperationException">profile 与当前 Turn 锁定的模型不一致时抛出。</exception>
    private void EnsureProfileMatchesCurrentTurnLock(LlmProfile profile) {
        var turn = AnalyzeCurrentTurn();
        var locked = turn.LockedInvocation;
        if (locked is null) { return; }

        var requested = profile.ToCompletionDescriptor();
        if (Equals(locked, requested)) { return; }

        throw new InvalidOperationException(
            $"LlmProfile switch is not allowed within an active Turn. " +
            $"Turn is locked to {DescribeDescriptor(locked)}, but received {DescribeDescriptor(requested)}. " +
            $"{DescribeCurrentTurn(turn)}. " +
            $"A Turn spans from the most recent ObservationEntry to the end of history; " +
            $"to switch profile, complete the current Turn (return to WaitingInput) and start a new ObservationEntry."
        );
    }

    private void LogStateIfChanged(AgentRunState state) {
        if (_lastLoggedState is AgentRunState previous && previous == state) { return; }

        DebugUtil.Trace(StateMachineDebugCategory, $"[Engine] state={state} historyCount={_state.RecentHistory.Count} pendingToolResults={_pendingToolResults.Count}");
        _lastLoggedState = state;
    }

    private static AgentStepResult CreateStepResult(AgentRunState before, AgentRunState after, StepOutcome outcome)
        => new(outcome.ProgressMade, before, after, outcome.Input, outcome.Output, outcome.ToolResults);

    private readonly record struct StepOutcome(
        bool ProgressMade,
        ObservationEntry? Input,
        ActionEntry? Output,
        ToolResultsEntry? ToolResults
    ) {
        public static StepOutcome NoProgress => default;

        public static StepOutcome FromInput(ObservationEntry input)
            => new(true, input, null, null);

        public static StepOutcome FromOutput(ActionEntry output)
            => new(true, null, output, null);

        public static StepOutcome FromToolResults(ToolResultsEntry toolResults)
            => new(true, null, null, toolResults);

        public static StepOutcome FromToolExecution()
            => new(true, null, null, null);

        /// <summary>
        /// 表示状态机发生了内部状态变更（如上下文压缩），但未产生新的 I/O 条目。
        /// 返回 <c>ProgressMade == true</c>，确保外层驱动循环不会误判为阻塞。
        /// </summary>
        public static StepOutcome FromStateMutation()
            => FromToolExecution();
    }
}
