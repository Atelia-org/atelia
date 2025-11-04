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
    private readonly ToolExecutor _toolExecutor;
    private readonly IAppHost _appHost;
    private readonly Dictionary<string, LodToolCallResult> _pendingToolResults = new(StringComparer.OrdinalIgnoreCase);

    private ImmutableArray<ToolDefinition> _toolDefinitions;
    private AgentRunState? _lastLoggedState;

    public AgentEngine(AgentState state, ToolExecutor toolExecutor, IAppHost appHost) {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _toolExecutor = toolExecutor ?? throw new ArgumentNullException(nameof(toolExecutor));
        _appHost = appHost ?? throw new ArgumentNullException(nameof(appHost));
        _toolDefinitions = ToolDefinitionBuilder.FromTools(_toolExecutor.Tools);

        DebugUtil.Print(StateMachineDebugCategory, $"[Engine] Initialized toolDefinitions={_toolDefinitions.Length}");
    }

    public AgentState State => _state;
    public ToolExecutor ToolExecutor => _toolExecutor;

    public ImmutableArray<ToolDefinition> ToolDefinitions => _toolDefinitions;

    public string SystemInstruction => _state.SystemInstruction;

    public IAppHost AppHost => _appHost;

    public IReadOnlyList<IContextMessage> RenderLiveContext() {
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

    public void RefreshToolDefinitions() {
        _toolDefinitions = ToolDefinitionBuilder.FromTools(_toolExecutor.Tools);
        DebugUtil.Print(StateMachineDebugCategory, $"[Engine] Tool definitions refreshed count={_toolDefinitions.Length}");
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
            PromptEntry => AgentRunState.PendingInput,
            ModelEntry outputEntry => DetermineOutputState(outputEntry),
            _ => AgentRunState.WaitingInput
        };
    }

    private AgentRunState DetermineOutputState(ModelEntry outputEntry) {
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

        var inputEntry = args.InputEntry ?? new PromptEntry();
        var appended = _state.AppendModelInput(inputEntry);

        DebugUtil.Print(StateMachineDebugCategory, $"[Engine] Inputs {appended}");

        return StepOutcome.FromInput(appended);
    }

    private async Task<StepOutcome> ProcessPendingModelCallAsync(AgentRunState state, LlmProfile profile, CancellationToken cancellationToken) {
        var liveContext = RenderLiveContext();
        DebugUtil.Print(ProviderDebugCategory, $"[Engine] Rendering context count={liveContext.Count}");

        var args = new BeforeModelCallEventArgs(state, profile, liveContext, _toolDefinitions);
        OnBeforeModelCall(args);

        if (args.Cancel) { return StepOutcome.NoProgress; }

        if (args.Profile is null) { throw new InvalidOperationException("BeforeModelCall handlers must not set Profile to null."); }
        if (args.LiveContext is null) { throw new InvalidOperationException("BeforeModelCall handlers must provide a LiveContext instance."); }

        var effectiveToolDefinitions = args.ToolDefinitions.IsDefault
            ? _toolDefinitions
            : args.ToolDefinitions;

        var invocation = new ModelInvocationDescriptor(args.Profile.Client.Name, args.Profile.Client.ProtocolVersion, args.Profile.ModelId);
        var request = new CompletionRequest(args.Profile.ModelId, SystemInstruction, args.LiveContext, effectiveToolDefinitions);

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
        if (_state.History.Count == 0 || _state.History[^1] is not ModelEntry outputEntry) {
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

        var result = await _toolExecutor.ExecuteAsync(nextCall, cancellationToken).ConfigureAwait(false);
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
        if (_state.History.Count == 0 || _state.History[^1] is not ModelEntry outputEntry) { return StepOutcome.NoProgress; }
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

    private ParsedToolCall? FindNextPendingToolCall(ModelEntry outputEntry) {
        if (outputEntry.ToolCalls is not { Count: > 0 }) { return null; }

        foreach (var call in outputEntry.ToolCalls) {
            if (!_pendingToolResults.ContainsKey(call.ToolCallId)) { return call; }
        }

        return null;
    }

    private bool HasAllToolResults(ModelEntry outputEntry) {
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
        PromptEntry? Input,
        ModelEntry? Output,
        ToolEntry? ToolResults
    ) {
        public static StepOutcome NoProgress => default;

        public static StepOutcome FromInput(PromptEntry input)
            => new(true, input, null, null);

        public static StepOutcome FromOutput(ModelEntry output)
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

    public PromptEntry? InputEntry { get; set; }

    public LevelOfDetailContent? AdditionalNotification { get; set; }
}

public sealed class BeforeModelCallEventArgs : EventArgs {
    internal BeforeModelCallEventArgs(
        AgentRunState state,
        LlmProfile profile,
        IReadOnlyList<IContextMessage> liveContext,
        ImmutableArray<ToolDefinition> toolDefinitions
    ) {
        State = state;
        Profile = profile;
        LiveContext = liveContext;
        ToolDefinitions = toolDefinitions;
    }

    public AgentRunState State { get; }

    public LlmProfile Profile { get; set; }

    public IReadOnlyList<IContextMessage> LiveContext { get; set; }

    public ImmutableArray<ToolDefinition> ToolDefinitions { get; set; }

    public bool Cancel { get; set; }
}

public sealed class AfterModelCallEventArgs : EventArgs {
    internal AfterModelCallEventArgs(AgentRunState state, LlmProfile profile, ModelEntry output) {
        State = state;
        Profile = profile;
        Output = output ?? throw new ArgumentNullException(nameof(output));
    }

    public AgentRunState State { get; }

    public LlmProfile Profile { get; }

    public ModelEntry Output { get; }
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
