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
/// <para><strong>警告：此类型非线程安全，不支持并发使用。</strong></para>
/// <para>所有公开方法（包括异步方法）都不应从多个线程同时调用。</para>
/// <para>如需并发执行多个 Agent，请为每个执行上下文创建独立的 <see cref="AgentEngine"/> 实例。</para>
/// </remarks>
public class AgentEngine {
    private const string ProviderDebugCategory = "Provider";
    private const string StateMachineDebugCategory = "StateMachine";

    private readonly AgentState _state;
    private readonly DefaultAppHost _appHost;
    private readonly Dictionary<string, ITool> _standaloneTools;
    private readonly Dictionary<string, LodToolCallResult> _pendingToolResults = new(StringComparer.OrdinalIgnoreCase);

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
    public AgentEngine(
        AgentState? state = null,
        IEnumerable<IApp>? initialApps = null,
        IEnumerable<ITool>? initialTools = null
    ) {
        _state = state ?? AgentState.CreateDefault();
        _appHost = new DefaultAppHost();
        _standaloneTools = new Dictionary<string, ITool>(StringComparer.OrdinalIgnoreCase);
        _toolsDirty = true;

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
        DebugUtil.Print(
            StateMachineDebugCategory,
            $"[Engine] Tool cache rebuilt all={executor.AllToolDefinitions.Length} visible={visibleDefinitions.Length}"
        );
        return executor;
    }

    /// <summary>
    /// 渲染当前实时上下文，包括历史消息与应用窗口内容。
    /// </summary>
    /// <returns>渲染后的历史消息列表。</returns>
    /// <remarks>此方法非线程安全，不应与其他方法并发调用。</remarks>
    public IReadOnlyList<IHistoryMessage> ProjectContext() {
        var windows = _appHost.RenderWindows();
        return _state.ProjectContext(windows);
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
        DebugUtil.Print(StateMachineDebugCategory, $"[Engine] Host notification appended basicLength={notificationContent.Basic.Length}");
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
    /// <remarks>
    /// <para>此方法非线程安全，不应与其他方法并发调用。</para>
    /// <para>每次调用将根据当前状态执行相应操作（等待输入、调用模型、执行工具等）。</para>
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
            ResetInvocation();
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

        if (args.AdditionalNotification is not null) {
            _state.AppendNotification(args.AdditionalNotification);
        }

        var inputEntry = args.InputEntry ?? new ObservationEntry();
        var appended = _state.AppendObservation(inputEntry);

        DebugUtil.Print(StateMachineDebugCategory, $"[Engine] Inputs {appended}");

        return StepOutcome.FromInput(appended);
    }

    private async Task<StepOutcome> ProcessPendingModelCallAsync(AgentRunState state, LlmProfile profile, CancellationToken cancellationToken) {
        var liveContext = ProjectContext();
        DebugUtil.Print(ProviderDebugCategory, $"[Engine] Rendering context count={liveContext.Count}");

        var toolExecutor = ToolExecutor;
        var toolDefinitions = toolExecutor.GetVisibleToolDefinitions();

        var args = new BeforeModelCallEventArgs(state, profile, liveContext, toolDefinitions);
        OnBeforeModelCall(args);

        if (args.Cancel) { return StepOutcome.NoProgress; }

        if (args.Profile is null) { throw new InvalidOperationException("BeforeModelCall handlers must not set Profile to null."); }
        if (args.LiveContext is null) { throw new InvalidOperationException("BeforeModelCall handlers must provide a LiveContext instance."); }

        toolExecutor = ToolExecutor;
        toolDefinitions = toolExecutor.GetVisibleToolDefinitions();

        var invocation = new CompletionDescriptor(args.Profile.Client.Name, args.Profile.Client.ApiSpecId, args.Profile.ModelId);
        var request = new CompletionRequest(args.Profile.ModelId, SystemPrompt, args.LiveContext, toolDefinitions);

        var deltas = args.Profile.Client.StreamCompletionAsync(request, cancellationToken);
        var aggregatedOutput = await CompletionAccumulator.AggregateAsync(deltas, invocation, cancellationToken).ConfigureAwait(false);

        _pendingToolResults.Clear();

        var appended = _state.AppendAction(aggregatedOutput);

        var afterArgs = new AfterModelCallEventArgs(state, args.Profile, appended);
        OnAfterModelCall(afterArgs);

        var toolCallCount = appended.ToolCalls?.Count ?? 0;
        DebugUtil.Print(ProviderDebugCategory, $"[Engine] Model output appended Content.Length={appended.Content.Length} toolCalls={toolCallCount}");

        return StepOutcome.FromOutput(appended);
    }

    private async Task<StepOutcome> ProcessWaitingToolResultsAsync(CancellationToken cancellationToken) {
        if (_state.RecentHistory.Count == 0 || _state.RecentHistory[^1] is not ActionEntry outputEntry) {
            DebugUtil.Print(StateMachineDebugCategory, "[Engine] WaitingToolResults but no model output available");
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
        DebugUtil.Print(StateMachineDebugCategory, $"[Engine] Tool executed toolName={toolName} callId={result.ToolCallId} status={result.ExecuteResult.Status}");

        return StepOutcome.FromToolExecution();
    }

    private StepOutcome ProcessToolResultsReady() {
        if (_state.RecentHistory.Count == 0 || _state.RecentHistory[^1] is not ActionEntry outputEntry) { return StepOutcome.NoProgress; }
        if (outputEntry.ToolCalls is not { Count: > 0 }) { return StepOutcome.NoProgress; }

        var collectedResults = new List<LodToolCallResult>(outputEntry.ToolCalls.Count);

        for (var index = 0; index < outputEntry.ToolCalls.Count; index++) {
            var call = outputEntry.ToolCalls[index];
            if (!_pendingToolResults.TryGetValue(call.ToolCallId, out var result)) {
                DebugUtil.Print(StateMachineDebugCategory, $"[Engine] Missing tool execution result callId={call.ToolCallId}");
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
        DebugUtil.Print(StateMachineDebugCategory, $"[Engine] Tool results appended count={results.Length} failure={failureCallId}");

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

    private void ResetInvocation() {
        _lastLoggedState = null;
    }

    private void LogStateIfChanged(AgentRunState state) {
        if (_lastLoggedState is AgentRunState previous && previous == state) { return; }

        DebugUtil.Print(StateMachineDebugCategory, $"[Engine] state={state} historyCount={_state.RecentHistory.Count} pendingToolResults={_pendingToolResults.Count}");
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
