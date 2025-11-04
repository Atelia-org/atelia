using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Atelia.Diagnostics;
using Atelia.Completion.Abstractions;
using Atelia.Agent.Core.App;
using Atelia.Agent.Core.History;
using Atelia.Agent.Core.Tool;

namespace Atelia.Agent.Core;

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

    public AgentState State => _state;

    public string SystemInstruction => _state.SystemInstruction;

    public void RegisterApp(IApp app) {
        if (app is null) { throw new ArgumentNullException(nameof(app)); }

        EnsureNoToolConflicts(app.Tools, replacingAppName: app.Name);
        _appHost.RegisterApp(app);
        _toolsDirty = true;
    }

    public bool RemoveApp(string name) {
        if (string.IsNullOrWhiteSpace(name)) { return false; }

        var removed = _appHost.RemoveApp(name);
        if (!removed) { return false; }

        _toolsDirty = true;
        return true;
    }

    public void RegisterTool(ITool tool) {
        if (tool is null) { throw new ArgumentNullException(nameof(tool)); }

        if (_standaloneTools.ContainsKey(tool.Name)) { throw new InvalidOperationException($"Duplicate tool registration detected for '{tool.Name}'."); }

        EnsureToolNameAvailable(tool.Name);
        _standaloneTools[tool.Name] = tool;
        _toolsDirty = true;
    }

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

        DebugUtil.Print(StateMachineDebugCategory, $"[Engine] Tool cache rebuilt count={executor.ToolDefinitions.Length}");
        return executor;
    }

    public IReadOnlyList<IHistoryMessage> RenderLiveContext() {
        var windows = _appHost.RenderWindows();
        return _state.RenderLiveContext(windows);
    }

    public void AppendNotification(LevelOfDetailContent notificationContent) {
        if (notificationContent is null) { throw new ArgumentNullException(nameof(notificationContent)); }
        _state.AppendNotification(notificationContent);
        DebugUtil.Print(StateMachineDebugCategory, $"[Engine] Host notification appended basicLength={notificationContent.Basic.Length}");
    }

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

    public event EventHandler<WaitingInputEventArgs>? WaitingInput;
    public event EventHandler<BeforeModelCallEventArgs>? BeforeModelCall;
    public event EventHandler<AfterModelCallEventArgs>? AfterModelCall;
    public event EventHandler<BeforeToolExecuteEventArgs>? BeforeToolExecute;
    public event EventHandler<AfterToolExecuteEventArgs>? AfterToolExecute;
    public event EventHandler<StateTransitionEventArgs>? StateTransition;

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
        if (_state.History.Count == 0) { return AgentRunState.WaitingInput; }

        var last = _state.History[^1];
        return last switch {
            ToolEntry => AgentRunState.PendingToolResults,
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
        var lastEntry = _state.History.Count > 0 ? _state.History[^1] : null;
        var hasPendingNotification = _state.HasPendingNotification;
        var args = new WaitingInputEventArgs(hasPendingNotification, lastEntry);

        OnWaitingInput(args);

        if (!args.ShouldContinue) { return StepOutcome.NoProgress; }

        if (args.AdditionalNotification is not null) {
            _state.AppendNotification(args.AdditionalNotification);
        }

        var inputEntry = args.InputEntry ?? new ObservationEntry();
        var appended = _state.AppendModelInput(inputEntry);

        DebugUtil.Print(StateMachineDebugCategory, $"[Engine] Inputs {appended}");

        return StepOutcome.FromInput(appended);
    }

    private async Task<StepOutcome> ProcessPendingModelCallAsync(AgentRunState state, LlmProfile profile, CancellationToken cancellationToken) {
        var liveContext = RenderLiveContext();
        DebugUtil.Print(ProviderDebugCategory, $"[Engine] Rendering context count={liveContext.Count}");

        var toolExecutor = ToolExecutor;
        var toolDefinitions = toolExecutor.ToolDefinitions;

        var args = new BeforeModelCallEventArgs(state, profile, liveContext, toolDefinitions);
        OnBeforeModelCall(args);

        if (args.Cancel) { return StepOutcome.NoProgress; }

        if (args.Profile is null) { throw new InvalidOperationException("BeforeModelCall handlers must not set Profile to null."); }
        if (args.LiveContext is null) { throw new InvalidOperationException("BeforeModelCall handlers must provide a LiveContext instance."); }

        toolExecutor = ToolExecutor;
        toolDefinitions = toolExecutor.ToolDefinitions;

        var invocation = new CompletionDescriptor(args.Profile.Client.Name, args.Profile.Client.ApiSpecId, args.Profile.ModelId);
        var request = new CompletionRequest(args.Profile.ModelId, SystemInstruction, args.LiveContext, toolDefinitions);

        var deltas = args.Profile.Client.StreamCompletionAsync(request, cancellationToken);
        var aggregatedOutput = await CompletionAccumulator.AggregateAsync(deltas, invocation, cancellationToken).ConfigureAwait(false);

        _pendingToolResults.Clear();

        var appended = _state.AppendModelOutput(aggregatedOutput);

        var afterArgs = new AfterModelCallEventArgs(state, args.Profile, appended);
        OnAfterModelCall(afterArgs);

        var toolCallCount = appended.ToolCalls?.Count ?? 0;
        DebugUtil.Print(ProviderDebugCategory, $"[Engine] Model output appended Contents.Length={appended.Contents.Length} toolCalls={toolCallCount}");

        return StepOutcome.FromOutput(appended);
    }

    private async Task<StepOutcome> ProcessWaitingToolResultsAsync(CancellationToken cancellationToken) {
        if (_state.History.Count == 0 || _state.History[^1] is not ActionEntry outputEntry) {
            DebugUtil.Print(StateMachineDebugCategory, "[Engine] WaitingToolResults but no model output available");
            return StepOutcome.NoProgress;
        }

        var nextCall = FindNextPendingToolCall(outputEntry);
        if (nextCall is null) { return StepOutcome.NoProgress; }

        var beforeArgs = new BeforeToolExecuteEventArgs(nextCall);
        OnBeforeToolExecute(beforeArgs);

        if (beforeArgs.Cancel) {
            var cancelledResult = beforeArgs.OverrideResult ?? new LodToolCallResult(
                ToolExecutionStatus.Failed,
                new LevelOfDetailContent("工具执行被取消", "工具执行在调度前被扩展逻辑取消。"),
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
        DebugUtil.Print(StateMachineDebugCategory, $"[Engine] Tool executed toolName={toolName} callId={result.ToolCallId} status={result.Status}");

        return StepOutcome.FromToolExecution();
    }

    private StepOutcome ProcessToolResultsReady() {
        if (_state.History.Count == 0 || _state.History[^1] is not ActionEntry outputEntry) { return StepOutcome.NoProgress; }
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

        var failure = collectedResults.FirstOrDefault(static result => result.Status == ToolExecutionStatus.Failed);
        var executeError = failure is null
            ? null
            : failure.Result.GetContent(LevelOfDetail.Basic);

        var results = collectedResults.ToArray();
        var entry = new ToolEntry(results, executeError);

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

    private ToolEntry AppendToolResultsWithSummary(ToolEntry entry) {
        return _state.AppendToolResults(entry);
    }

    private void ResetInvocation() {
        _lastLoggedState = null;
    }

    private void LogStateIfChanged(AgentRunState state) {
        if (_lastLoggedState is AgentRunState previous && previous == state) { return; }

        DebugUtil.Print(StateMachineDebugCategory, $"[Engine] state={state} historyCount={_state.History.Count} pendingToolResults={_pendingToolResults.Count}");
        _lastLoggedState = state;
    }

    private static AgentStepResult CreateStepResult(AgentRunState before, AgentRunState after, StepOutcome outcome)
        => new(outcome.ProgressMade, before, after, outcome.Input, outcome.Output, outcome.ToolResults);

    private readonly record struct StepOutcome(
        bool ProgressMade,
        ObservationEntry? Input,
        ActionEntry? Output,
        ToolEntry? ToolResults
    ) {
        public static StepOutcome NoProgress => default;

        public static StepOutcome FromInput(ObservationEntry input)
            => new(true, input, null, null);

        public static StepOutcome FromOutput(ActionEntry output)
            => new(true, null, output, null);

        public static StepOutcome FromToolResults(ToolEntry toolResults)
            => new(true, null, null, toolResults);

        public static StepOutcome FromToolExecution()
            => new(true, null, null, null);
    }
}

public sealed class WaitingInputEventArgs : EventArgs {
    internal WaitingInputEventArgs(bool hasPendingNotification, HistoryEntry? lastEntry) {
        HasPendingNotification = hasPendingNotification;
        LastEntry = lastEntry;
        ShouldContinue = hasPendingNotification;
    }

    public bool HasPendingNotification { get; }

    public HistoryEntry? LastEntry { get; }

    public bool ShouldContinue { get; set; }

    public ObservationEntry? InputEntry { get; set; }

    public LevelOfDetailContent? AdditionalNotification { get; set; }
}

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

    public AgentRunState State { get; }

    public LlmProfile Profile { get; set; }

    public IReadOnlyList<IHistoryMessage> LiveContext { get; set; }

    public ImmutableArray<ToolDefinition> ToolDefinitions { get; }

    public bool Cancel { get; set; }
}

public sealed class AfterModelCallEventArgs : EventArgs {
    internal AfterModelCallEventArgs(AgentRunState state, LlmProfile profile, ActionEntry output) {
        State = state;
        Profile = profile;
        Output = output ?? throw new ArgumentNullException(nameof(output));
    }

    public AgentRunState State { get; }

    public LlmProfile Profile { get; }

    public ActionEntry Output { get; }
}

public sealed class BeforeToolExecuteEventArgs : EventArgs {
    internal BeforeToolExecuteEventArgs(ParsedToolCall toolCall) {
        ToolCall = toolCall ?? throw new ArgumentNullException(nameof(toolCall));
    }

    public ParsedToolCall ToolCall { get; }

    public bool Cancel { get; set; }

    public LodToolCallResult? OverrideResult { get; set; }
}

public sealed class AfterToolExecuteEventArgs : EventArgs {
    internal AfterToolExecuteEventArgs(ParsedToolCall toolCall, LodToolCallResult result) {
        ToolCall = toolCall ?? throw new ArgumentNullException(nameof(toolCall));
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    public ParsedToolCall ToolCall { get; }

    public LodToolCallResult Result { get; set; }
}

public sealed class StateTransitionEventArgs : EventArgs {
    internal StateTransitionEventArgs(AgentRunState fromState, AgentRunState toState) {
        FromState = fromState;
        ToState = toState;
    }

    public AgentRunState FromState { get; }

    public AgentRunState ToState { get; }
}
