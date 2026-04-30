using System.Collections.Immutable;
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
public class AgentEngine {
    private const string ProviderDebugCategory = "Provider";
    private const string StateMachineDebugCategory = "StateMachine";

    private readonly AgentState _state;
    private readonly DefaultAppHost _appHost;
    private readonly Dictionary<string, ITool> _standaloneTools;
    private readonly Dictionary<string, LodToolCallResult> _pendingToolResults = new(StringComparer.OrdinalIgnoreCase);
    private readonly IIdleObservationProvider _idleProvider;
    private readonly Func<DateTimeOffset> _utcNowProvider;

    private ToolExecutor? _toolExecutor;
    private ToolExecutor ToolExecutor => EnsureToolsBuilt();
    private bool _toolsDirty;
    private AgentRunState? _lastLoggedState;

    /// <summary>
    /// 捆绑一次上下文压缩请求所需的全部参数：切分点与 LLM 调用所需的两个 prompt。
    /// </summary>
    /// <remarks>
    /// prompt 文本不由引擎内置，而是由 <see cref="RequestCompaction"/> 的调用者注入，
    /// 便于在不同实验台项目中进行提示词工程。
    /// </remarks>
    /// <param name="SplitIndex">suffix 起始索引（由 <see cref="ContextSplitter.FindHalfContextSplitPoint"/> 返回）。</param>
    /// <param name="SystemPrompt">摘要 LLM 的系统提示词。</param>
    /// <param name="SummarizePrompt">追加在待摘要历史末尾的请求消息。</param>
    private readonly record struct CompactionRequest(int SplitIndex, string SystemPrompt, string SummarizePrompt);

    /// <summary>
    /// 待执行的上下文压缩请求。
    /// <c>null</c> 表示无待处理的压缩请求；非 <c>null</c> 时，<see cref="DetermineState"/> 返回 <see cref="AgentRunState.Compacting"/>。
    /// </summary>
    /// <remarks>
    /// 在 <see cref="RequestCompaction"/> 中计算并写入。
    /// 清除时机：
    /// <list type="bullet">
    /// <item><see cref="ProcessCompactingAsync"/> 成功调用 <see cref="AgentState.ReplacePrefixWithRecap"/> 后清除（正常完成）；</item>
    /// <item>stale 校验失败（splitIndex 越界或不再满足 Observation→Action 边界不变式）时清除；</item>
    /// <item>LLM 摘要返回空字符串时清除。</item>
    /// </list>
    /// LLM 调用抛出异常或 cancellation 时<em>不</em>清除，允许下一次 <see cref="StepAsync"/> 自动重试。
    /// 这替代了原先的 <c>bool _compactionPending</c>，使得状态标志同时携带切分点与 prompt 信息，避免 flag 与各参数分离导致的一致性问题。
    /// </remarks>
    private CompactionRequest? _compactionRequest;

    /// <summary>
    /// 初始化 <see cref="AgentEngine"/> 的新实例。
    /// </summary>
    /// <param name="state">Agent 状态实例，如为 <c>null</c> 则创建默认状态。</param>
    /// <param name="initialApps">初始注册的应用列表（可选）。</param>
    /// <param name="initialTools">初始注册的独立工具列表（可选）。</param>
    /// <param name="idleProvider">在无外部输入且无待处理通知时产生"心跳观测"的提供器；
    /// 传 <c>null</c> 使用默认的 <see cref="TimestampHeartbeatObservationProvider"/>。</param>
    /// <param name="utcNowProvider">UTC 时间源（可选，主要供测试注入）。</param>
    public AgentEngine(
        AgentState? state = null,
        IEnumerable<IApp>? initialApps = null,
        IEnumerable<ITool>? initialTools = null,
        IIdleObservationProvider? idleProvider = null,
        Func<DateTimeOffset>? utcNowProvider = null
    ) {
        _state = state ?? AgentState.CreateDefault();
        _appHost = new DefaultAppHost();
        _standaloneTools = new Dictionary<string, ITool>(StringComparer.OrdinalIgnoreCase);
        _toolsDirty = true;
        _idleProvider = idleProvider ?? new TimestampHeartbeatObservationProvider();
        _utcNowProvider = utcNowProvider ?? (() => DateTimeOffset.UtcNow);

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
    /// 在调用模型前触发，允许修改或取消调用。
    /// </summary>
    public event EventHandler<BeforeModelCallEventArgs>? BeforeModelCall;

    /// <summary>
    /// 在模型调用完成后触发。
    /// </summary>
    public event EventHandler<AfterModelCallEventArgs>? AfterModelCall;

    /// <summary>
    /// 在工具执行前触发，允许取消或覆盖执行结果。
    /// </summary>
    public event EventHandler<BeforeToolExecuteEventArgs>? BeforeToolExecute;

    /// <summary>
    /// 在工具执行完成后触发，允许修改执行结果。
    /// </summary>
    public event EventHandler<AfterToolExecuteEventArgs>? AfterToolExecute;

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
    public async Task<AgentStepResult> StepAsync(LlmProfile profile, CancellationToken cancellationToken = default) {
        if (profile is null) { throw new ArgumentNullException(nameof(profile)); }

        var stateBefore = DetermineState();
        LogStateIfChanged(stateBefore);

        var outcome = await ExecuteStateAsync(stateBefore, profile, cancellationToken).ConfigureAwait(false);

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

    protected virtual void OnBeforeModelCall(BeforeModelCallEventArgs e) {
        BeforeModelCall?.Invoke(this, e);
    }

    protected virtual void OnAfterModelCall(AfterModelCallEventArgs e) {
        AfterModelCall?.Invoke(this, e);
    }

    protected virtual void OnBeforeToolExecute(BeforeToolExecuteEventArgs e) {
        BeforeToolExecute?.Invoke(this, e);
    }

    protected virtual void OnAfterToolExecute(AfterToolExecuteEventArgs e) {
        AfterToolExecute?.Invoke(this, e);
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
        if (outputEntry.ToolCalls is not { Count: > 0 }) { return AgentRunState.WaitingInput; }
        return HasAllToolResults(outputEntry)
            ? AgentRunState.ToolResultsReady
            : AgentRunState.WaitingToolResults;
    }

    private async Task<StepOutcome> ExecuteStateAsync(AgentRunState state, LlmProfile profile, CancellationToken cancellationToken) {
        switch (state) {
            case AgentRunState.WaitingInput:
                return ProcessWaitingInput();
            case AgentRunState.PendingInput:
            case AgentRunState.PendingToolResults:
                return await ProcessPendingModelCallAsync(state, profile, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// 请求下一次 <see cref="StepAsync"/> 调用时执行上下文压缩。
    /// 主要用于测试目的；自动触发策略留待后续设计。
    /// </summary>
    /// <param name="systemPrompt">摘要 LLM 的系统提示词（由调用方注入，便于提示词工程）。</param>
    /// <param name="summarizePrompt">追加在待摘要历史末尾的摘要请求消息（由调用方注入）。</param>
    /// <returns>
    /// <c>true</c> 表示找到合法切分点并已记录到 <see cref="_compactionRequest"/>；
    /// <c>false</c> 表示当前历史没有可用的切分点（如条目数不足 2），不会进入 <see cref="AgentRunState.Compacting"/> 状态。
    /// </returns>
    /// <remarks>
    /// 若已有待处理的压缩请求（<see cref="_compactionRequest"/> 非 <c>null</c>），
    /// 本次调用会刷新切分点与 prompt（重新采样当前历史快照）。重复调用是幂等的：只要历史量足够，
    /// 始终返回 <c>true</c>。
    /// <para>
    /// Compaction 是一个高优先级内部状态：一旦 <see cref="_compactionRequest"/> 被设置，
    /// <see cref="DetermineState"/> 会优先返回 <see cref="AgentRunState.Compacting"/>，
    /// 插队到 <c>PendingInput</c> / <c>PendingToolResults</c> 等状态之前。
    /// 由于 suffix 部分原样保留，此插队不会破坏 <see cref="_pendingToolResults"/> 与末尾 <see cref="ActionEntry"/> 的对应关系。
    /// </para>
    /// </remarks>
    public bool RequestCompaction(string systemPrompt, string summarizePrompt) {
        if (systemPrompt is null) { throw new ArgumentNullException(nameof(systemPrompt)); }
        if (summarizePrompt is null) { throw new ArgumentNullException(nameof(summarizePrompt)); }

        var snapshot = _state.RecentHistory;
        int splitIndex = ContextSplitter.FindHalfContextSplitPoint(snapshot);
        if (splitIndex < 0) {
            DebugUtil.Trace(StateMachineDebugCategory, "[Compacting] RequestCompaction: no valid split point; skipping.");
            return false;
        }
        _compactionRequest = new CompactionRequest(splitIndex, systemPrompt, summarizePrompt);
        DebugUtil.Trace(StateMachineDebugCategory, $"[Compacting] RequestCompaction: splitIndex={splitIndex} historyCount={snapshot.Count}");
        return true;
    }

    /// <summary>
    /// 将历史条目投影为 <see cref="IHistoryMessage"/> 列表，末尾追加摘要请求消息。
    /// </summary>
    /// <remarks>
    /// 这是原 <c>ContextSummarizer.ProjectToMessages</c> 的迁移版本。
    /// 使用 Detail 级别、不注入 windows、不处理 Turn 切分——
    /// 摘要场景下 LLM 只需看到内容文本，无需完整投影管线。
    /// </remarks>
    private static List<IHistoryMessage> ProjectForSummarization(
        IReadOnlyList<HistoryEntry> entries,
        string summarizePrompt
    ) {
        var messages = new List<IHistoryMessage>(entries.Count + 1);

        foreach (var entry in entries) {
            switch (entry) {
                case ActionEntry action:
                    messages.Add(action);
                    break;

                case ObservationEntry observation:
                    messages.Add(observation.GetMessage(LevelOfDetail.Detail, windows: null));
                    break;

                case RecapEntry recap:
                    messages.Add(new ObservationMessage(recap.Content));
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported HistoryEntry type: {entry.GetType().Name} (Kind={entry.Kind})"
                    );
            }
        }

        messages.Add(new ObservationMessage(summarizePrompt));
        return messages;
    }

    private async Task<StepOutcome> ProcessCompactingAsync(LlmProfile profile, CancellationToken cancellationToken) {
        if (!_compactionRequest.HasValue) {
            // 防御性：状态机不应走到这里，但保留安全处理。
            DebugUtil.Warning(StateMachineDebugCategory, "[Compacting] Entered without valid compaction request; aborting.");
            return StepOutcome.NoProgress;
        }

        var request = _compactionRequest.Value;
        int splitIndex = request.SplitIndex;
        DebugUtil.Info(StateMachineDebugCategory, $"[Compacting] Starting half-context compaction. splitIndex={splitIndex}");

        // 校验 splitIndex 对当前历史仍然合法（RequestCompaction 与执行之间历史可能变化）
        // 需要同时验证索引边界和 Observation→Action 结构不变式，因为 ReplacePrefixWithRecap
        // 仅在 DEBUG 下对这些前置条件做 Assert，Release 下不会阻止。
        var snapshot = _state.RecentHistory;
        if (splitIndex < 1 || splitIndex >= snapshot.Count
            || !snapshot[splitIndex - 1].IsObservationLike
            || snapshot[splitIndex] is not ActionEntry) {
            DebugUtil.Warning(StateMachineDebugCategory, $"[Compacting] splitIndex={splitIndex} no longer valid for current history (count={snapshot.Count}); aborting.");
            _compactionRequest = null;
            return StepOutcome.NoProgress;
        }

        var prefix = new List<HistoryEntry>(splitIndex);
        for (int i = 0; i < splitIndex; i++) {
            prefix.Add(snapshot[i]);
        }
        var messages = ProjectForSummarization(prefix, request.SummarizePrompt);
        var summary = await ContextSummarizer.SummarizeAsync(
            profile, messages, request.SystemPrompt, cancellationToken
        ).ConfigureAwait(false);

        if (string.IsNullOrEmpty(summary)) {
            DebugUtil.Warning(StateMachineDebugCategory, "[Compacting] Summarization returned empty; skipping replacement.");
            _compactionRequest = null;
            return StepOutcome.NoProgress;
        }

        _state.ReplacePrefixWithRecap(splitIndex, summary);
        _compactionRequest = null;
        DebugUtil.Info(StateMachineDebugCategory, $"[Compacting] Done. splitIndex={splitIndex} summaryLen={summary.Length} remaining={_state.RecentHistory.Count}");
        return StepOutcome.FromStateMutation();
    }

    private StepOutcome ProcessWaitingInput() {
        var lastEntry = _state.RecentHistory.Count > 0 ? _state.RecentHistory[^1] : null;
        var hasPendingNotification = _state.HasPendingNotification;
        var args = new WaitingInputEventArgs(hasPendingNotification, lastEntry);

        OnWaitingInput(args);

        if (!args.ShouldContinue) { return StepOutcome.NoProgress; }

        if (args.AdditionalNotification is not null) {
            _state.AppendNotification(args.AdditionalNotification);
        }

        var inputEntry = args.InputEntry;
        if (inputEntry is null) {
            // 上游决定推进但未提供任何输入：若此时连 pending notification 也没有，调用 idle provider 填充
            // 一条内源性"心跳"通知。这避免向下游 provider 提交"完全空"的 user 消息（会被 Anthropic 等拒绝），
            // 同时使"空推进"这个语义变成可配置的宛转点而不是 provider 层隐式兼容。
            if (!_state.HasPendingNotification) {
                var idleContext = new IdleObservationContext {
                    UtcNow = _utcNowProvider(),
                    RecentHistory = _state.RecentHistory
                };

                var heartbeat = _idleProvider.CreateIdleNotification(idleContext);
                if (heartbeat is null) {
                    DebugUtil.Trace(StateMachineDebugCategory, "[Engine] Idle provider declined to produce a heartbeat; staying NoProgress.");
                    return StepOutcome.NoProgress;
                }

                _state.AppendNotification(heartbeat);
            }

            inputEntry = new ObservationEntry();
        }

        var appended = _state.AppendObservation(inputEntry);

        DebugUtil.Trace(StateMachineDebugCategory, $"[Engine] Inputs {appended}");

        return StepOutcome.FromInput(appended);
    }

    private async Task<StepOutcome> ProcessPendingModelCallAsync(AgentRunState state, LlmProfile profile, CancellationToken cancellationToken) {
        // Turn 锁定校验：在事件触发前拦截，防止宿主侧绕过约束。
        EnsureProfileMatchesCurrentTurnLock(profile);

        var invocation = profile.ToCompletionDescriptor();
        var projection = _state.ProjectInvocationContext(
            new ContextProjectionOptions(
                TargetInvocation: invocation,
                Windows: _appHost.RenderWindows()
            )
        );
        var liveContext = projection.ToFlat();
        DebugUtil.Trace(ProviderDebugCategory, $"[Engine] Rendering context count={liveContext.Count}");

        var toolExecutor = ToolExecutor;
        var toolDefinitions = toolExecutor.GetVisibleToolDefinitions();

        var args = new BeforeModelCallEventArgs(state, profile, liveContext, toolDefinitions);
        OnBeforeModelCall(args);

        if (args.Cancel) { return StepOutcome.NoProgress; }

        if (args.Profile is null) { throw new InvalidOperationException("BeforeModelCall handlers must not set Profile to null."); }
        if (args.LiveContext is null) { throw new InvalidOperationException("BeforeModelCall handlers must provide a LiveContext instance."); }

        // 二次校验：BeforeModelCall handler 可能替换了 Profile，需再次确认仍在 turn 锁定范围内。
        EnsureProfileMatchesCurrentTurnLock(args.Profile);

        toolExecutor = ToolExecutor;
        toolDefinitions = toolExecutor.GetVisibleToolDefinitions();

        invocation = args.Profile.ToCompletionDescriptor();
        var request = new CompletionRequest(args.Profile.ModelId, SystemPrompt, args.LiveContext, toolDefinitions);

        var aggregated = await args.Profile.Client.StreamCompletionAsync(request, null, cancellationToken).ConfigureAwait(false);
        EnsureAggregatedInvocationMatchesExpected(invocation, aggregated.Invocation);
        var aggregatedOutput = new ActionEntry(aggregated.Blocks, invocation);

        DebugUtil.Info(
            ProviderDebugCategory,
            $"[Engine] Aggregated completion blocks={aggregated.Blocks.Count} toolCalls={aggregatedOutput.ToolCalls.Count} errors={aggregated.Errors?.Count ?? 0}"
        );

        _pendingToolResults.Clear();

        var appended = _state.AppendAction(aggregatedOutput);

        var afterArgs = new AfterModelCallEventArgs(state, args.Profile, appended);
        OnAfterModelCall(afterArgs);

        var toolCallCount = appended.ToolCalls?.Count ?? 0;
        var textLen = appended.Blocks.OfType<ActionBlock.Text>().Sum(b => b.Content.Length);
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

        var beforeArgs = new BeforeToolExecuteEventArgs(nextCall);
        OnBeforeToolExecute(beforeArgs);

        if (beforeArgs.Cancel) {
            var cancelledResult = beforeArgs.OverrideResult ?? new LodToolCallResult(
                new LodToolExecuteResult(ToolExecutionStatus.Failed, new LevelOfDetailContent("工具执行被取消", "工具执行在调度前被扩展逻辑取消。")),
                nextCall.ToolName,
                nextCall.ToolCallId
            );

            _pendingToolResults[nextCall.ToolCallId] = cancelledResult;
            return StepOutcome.FromToolExecution();
        }

        var toolExecutor = ToolExecutor;
        var result = await toolExecutor.ExecuteAsync(nextCall, cancellationToken).ConfigureAwait(false);
        _pendingToolResults[nextCall.ToolCallId] = result;

        var afterArgs = new AfterToolExecuteEventArgs(nextCall, result);
        OnAfterToolExecute(afterArgs);

        if (!ReferenceEquals(afterArgs.Result, result)) {
            result = afterArgs.Result ?? result;
            _pendingToolResults[nextCall.ToolCallId] = result;
        }

        var toolName = result.ToolName ?? nextCall.ToolName;
        DebugUtil.Info(StateMachineDebugCategory, $"[Engine] Tool executed toolName={toolName} callId={result.ToolCallId} status={result.ExecuteResult.Status}");

        return StepOutcome.FromToolExecution();
    }

    private StepOutcome ProcessToolResultsReady() {
        if (_state.RecentHistory.Count == 0 || _state.RecentHistory[^1] is not ActionEntry outputEntry) { return StepOutcome.NoProgress; }
        if (outputEntry.ToolCalls is not { Count: > 0 }) { return StepOutcome.NoProgress; }

        var collectedResults = new List<LodToolCallResult>(outputEntry.ToolCalls.Count);

        for (var index = 0; index < outputEntry.ToolCalls.Count; index++) {
            var call = outputEntry.ToolCalls[index];
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

    private ParsedToolCall? FindNextPendingToolCall(ActionEntry outputEntry) {
        if (outputEntry.ToolCalls is not { Count: > 0 }) { return null; }

        foreach (var call in outputEntry.ToolCalls) {
            if (!_pendingToolResults.ContainsKey(call.ToolCallId)) { return call; }
        }

        return null;
    }

    private bool HasAllToolResults(ActionEntry outputEntry) {
        if (outputEntry.ToolCalls is not { Count: > 0 }) { return false; }
        if (_pendingToolResults.Count < outputEntry.ToolCalls.Count) { return false; }

        foreach (var call in outputEntry.ToolCalls) {
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

    private static void EnsureAggregatedInvocationMatchesExpected(CompletionDescriptor expected, CompletionDescriptor actual) {
        if (expected is null) { throw new ArgumentNullException(nameof(expected)); }
        if (actual is null) { throw new ArgumentNullException(nameof(actual)); }
        if (Equals(expected, actual)) { return; }

        throw new InvalidOperationException(
            "Completion client returned an AggregatedAction.Invocation that does not match the requested profile. " +
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

/// <summary>
/// <see cref="AgentEngine.WaitingInput"/> 事件的参数。
/// </summary>
public sealed class WaitingInputEventArgs : EventArgs {
    internal WaitingInputEventArgs(bool hasPendingNotification, HistoryEntry? lastEntry) {
        HasPendingNotification = hasPendingNotification;
        LastEntry = lastEntry;
        ShouldContinue = hasPendingNotification;
    }

    /// <summary>
    /// 获取一个值，指示是否有待处理的主机通知。
    /// </summary>
    public bool HasPendingNotification { get; }

    /// <summary>
    /// 获取历史中的最后一个条目（可能为 <c>null</c>）。
    /// </summary>
    public HistoryEntry? LastEntry { get; }

    /// <summary>
    /// 获取或设置一个值，指示是否应继续执行（默认为 <see cref="HasPendingNotification"/>）。
    /// </summary>
    public bool ShouldContinue { get; set; }

    /// <summary>
    /// 获取或设置要追加的用户输入条目（可选）。
    /// </summary>
    public ObservationEntry? InputEntry { get; set; }

    /// <summary>
    /// 获取或设置额外的主机通知内容（可选）。
    /// </summary>
    public LevelOfDetailContent? AdditionalNotification { get; set; }
}

/// <summary>
/// <see cref="AgentEngine.BeforeModelCall"/> 事件的参数。
/// </summary>
public sealed class BeforeModelCallEventArgs : EventArgs {
    internal BeforeModelCallEventArgs(
        AgentRunState state,
        LlmProfile profile,
        IReadOnlyList<IHistoryMessage> liveContext,
        ImmutableArray<ToolDefinition> toolDefinitions
    ) {
        State = state;
        Profile = profile;
        LiveContext = liveContext;
        ToolDefinitions = toolDefinitions;
    }

    /// <summary>
    /// 获取当前 Agent 运行状态。
    /// </summary>
    public AgentRunState State { get; }

    /// <summary>
    /// 获取或设置 LLM 配置文件（可被事件处理器修改）。
    /// </summary>
    public LlmProfile Profile { get; set; }

    /// <summary>
    /// 获取或设置实时上下文消息列表（可被事件处理器修改）。
    /// </summary>
    public IReadOnlyList<IHistoryMessage> LiveContext { get; set; }

    /// <summary>
    /// 获取当前对模型可见的工具定义列表。
    /// </summary>
    public ImmutableArray<ToolDefinition> ToolDefinitions { get; }

    /// <summary>
    /// 获取或设置一个值，指示是否取消本次模型调用。
    /// </summary>
    public bool Cancel { get; set; }
}

/// <summary>
/// <see cref="AgentEngine.AfterModelCall"/> 事件的参数。
/// </summary>
public sealed class AfterModelCallEventArgs : EventArgs {
    internal AfterModelCallEventArgs(AgentRunState state, LlmProfile profile, ActionEntry output) {
        State = state;
        Profile = profile;
        Output = output ?? throw new ArgumentNullException(nameof(output));
    }

    /// <summary>
    /// 获取当前 Agent 运行状态。
    /// </summary>
    public AgentRunState State { get; }

    /// <summary>
    /// 获取使用的 LLM 配置文件。
    /// </summary>
    public LlmProfile Profile { get; }

    /// <summary>
    /// 获取模型输出的动作条目。
    /// </summary>
    public ActionEntry Output { get; }
}

/// <summary>
/// <see cref="AgentEngine.BeforeToolExecute"/> 事件的参数。
/// </summary>
public sealed class BeforeToolExecuteEventArgs : EventArgs {
    internal BeforeToolExecuteEventArgs(ParsedToolCall toolCall) {
        ToolCall = toolCall ?? throw new ArgumentNullException(nameof(toolCall));
    }

    /// <summary>
    /// 获取待执行的工具调用信息。
    /// </summary>
    public ParsedToolCall ToolCall { get; }

    /// <summary>
    /// 获取或设置一个值，指示是否取消本次工具执行。
    /// </summary>
    public bool Cancel { get; set; }

    /// <summary>
    /// 获取或设置覆盖结果（当 <see cref="Cancel"/> 为 <c>true</c> 时使用）。
    /// </summary>
    public LodToolCallResult? OverrideResult { get; set; }
}

/// <summary>
/// <see cref="AgentEngine.AfterToolExecute"/> 事件的参数。
/// </summary>
public sealed class AfterToolExecuteEventArgs : EventArgs {
    internal AfterToolExecuteEventArgs(ParsedToolCall toolCall, LodToolCallResult result) {
        ToolCall = toolCall ?? throw new ArgumentNullException(nameof(toolCall));
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    /// <summary>
    /// 获取已执行的工具调用信息。
    /// </summary>
    public ParsedToolCall ToolCall { get; }

    /// <summary>
    /// 获取或设置工具执行结果（可被事件处理器修改）。
    /// </summary>
    public LodToolCallResult Result { get; set; }
}

/// <summary>
/// <see cref="AgentEngine.StateTransition"/> 事件的参数。
/// </summary>
public sealed class StateTransitionEventArgs : EventArgs {
    internal StateTransitionEventArgs(AgentRunState fromState, AgentRunState toState) {
        FromState = fromState;
        ToState = toState;
    }

    /// <summary>
    /// 获取转换前的状态。
    /// </summary>
    public AgentRunState FromState { get; }

    /// <summary>
    /// 获取转换后的状态。
    /// </summary>
    public AgentRunState ToState { get; }
}
