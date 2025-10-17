using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Atelia.Diagnostics;
using Atelia.LiveContextProto.Provider;
using Atelia.LiveContextProto.State;
using Atelia.LiveContextProto.State.History;
using Atelia.LiveContextProto.Tools;
using Atelia.LiveContextProto.Context;
using Atelia.LiveContextProto.Profile;

namespace Atelia.LiveContextProto.Agent;

internal sealed class LlmAgent {
    private const string ProviderDebugCategory = "Provider";
    private const string StateMachineDebugCategory = "StateMachine";
    private const int MaxStepsPerInvocation = 64;

    private readonly AgentState _state;
    private readonly ProviderRouter _router;
    private readonly ToolExecutor _toolExecutor;
    private readonly ToolCatalog _toolCatalog;

    private readonly ConcurrentQueue<PendingUserInput> _pendingInputs = new();
    private readonly Dictionary<string, ToolExecutionRecord> _pendingToolResults = new(StringComparer.OrdinalIgnoreCase);

    private string _defaultInvocationOptions;
    private string? _activeInvocationOptions;
    private RunLoopContext? _activeRunLoop;
    private AgentRunState? _lastLoggedState;

    public LlmAgent(AgentState state, ProviderRouter router, ToolExecutor toolExecutor, ToolCatalog toolCatalog, string defaultInvocationOptions) {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _toolExecutor = toolExecutor ?? throw new ArgumentNullException(nameof(toolExecutor));
        _toolCatalog = toolCatalog ?? throw new ArgumentNullException(nameof(toolCatalog));
        _defaultInvocationOptions = defaultInvocationOptions ?? throw new ArgumentNullException(nameof(defaultInvocationOptions));
    }

    public string SystemInstruction => _state.SystemInstruction;

    public string MemoryNotebookSnapshot => _state.MemoryNotebookSnapshot;

    public IReadOnlyList<IContextMessage> RenderLiveContext() => _state.RenderLiveContext();

    public void Reset() {
        _state.Reset();
        _pendingToolResults.Clear();
        _pendingInputs.Clear();
        ResetInvocation();
    }

    public void UpdateMemoryNotebook(string? content) => _state.UpdateMemoryNotebook(content);

    public void UpdateDefaultInvocationOptions(string options) {
        _defaultInvocationOptions = options ?? throw new ArgumentNullException(nameof(options));
    }

    public void EnqueueUserInput(string text, string? options = null) {
        if (string.IsNullOrWhiteSpace(text)) { throw new ArgumentException("Value cannot be null or whitespace.", nameof(text)); }
        var invocation = options ?? _defaultInvocationOptions;
        _pendingInputs.Enqueue(new PendingUserInput(text, invocation));
    }

    public async Task<AgentStepResult> DoStepAsync(CancellationToken cancellationToken = default) {
        var stateBefore = DetermineState();
        LogStateIfChanged(stateBefore);

        var outcome = await ExecuteStateAsync(stateBefore, cancellationToken).ConfigureAwait(false);

        var stateAfter = DetermineState();
        LogStateIfChanged(stateAfter);

        if (stateAfter == AgentRunState.WaitingInput) {
            ResetInvocation();
        }

        return CreateStepResult(stateBefore, stateAfter, outcome);
    }

    internal async Task<AgentInvocationResult> InvokePipelineAsync(
        string options,
        CancellationToken cancellationToken = default
    ) {
        ResetInvocation();
        _pendingToolResults.Clear();
        _activeInvocationOptions = options ?? throw new ArgumentNullException(nameof(options));

        var lastOutput = (ModelOutputEntry?)null;
        var lastToolResults = (ToolResultsEntry?)null;
        AgentRunState? previousState = null;
        var reachedStepLimit = true;

        try {
            for (var step = 0; step < MaxStepsPerInvocation; step++) {
                cancellationToken.ThrowIfCancellationRequested();

                var state = DetermineState();
                if (previousState is null || previousState != state) {
                    DebugUtil.Print(StateMachineDebugCategory, $"[StateMachine] state={state} historyCount={_state.History.Count} pendingToolResults={_pendingToolResults.Count}");
                    previousState = state;
                }

                if (state == AgentRunState.WaitingInput) {
                    reachedStepLimit = false;
                    break;
                }

                var outcome = await ExecuteStateAsync(state, cancellationToken).ConfigureAwait(false);
                if (!outcome.ProgressMade) { throw new InvalidOperationException($"State machine stalled in state '{state}'."); }

                if (outcome.Output is not null) {
                    lastOutput = outcome.Output;
                }

                if (outcome.ToolResults is not null) {
                    lastToolResults = outcome.ToolResults;
                }

                var nextState = DetermineState();
                if (nextState == AgentRunState.WaitingInput && lastOutput is not null) {
                    reachedStepLimit = false;
                    break;
                }
            }

            if (lastOutput is null) { throw new InvalidOperationException("Provider returned no model output."); }

            var finalState = DetermineState();
            var steps = _activeRunLoop?.StepCount ?? 0;
            if (reachedStepLimit && finalState != AgentRunState.WaitingInput) {
                DebugUtil.Print(StateMachineDebugCategory, $"[StateMachine] Step limit reached state={finalState} steps={steps}");
                throw new InvalidOperationException($"State machine exceeded {MaxStepsPerInvocation} steps without reaching a stable state (last={finalState}).");
            }

            DebugUtil.Print(ProviderDebugCategory, $"[Orchestrator] Invocation completed steps={steps} state={finalState}");

            return new AgentInvocationResult(lastOutput, lastToolResults);
        }
        finally {
            ResetInvocation();
        }
    }

    private AgentRunState DetermineState() {
        if (_state.History.Count == 0) { return AgentRunState.WaitingInput; }

        var last = _state.History[^1];
        return last switch {
            ModelInputEntry => AgentRunState.PendingInput,
            ModelOutputEntry outputEntry => DetermineOutputState(outputEntry),
            ToolResultsEntry => AgentRunState.PendingToolResults,
            _ => AgentRunState.WaitingInput
        };
    }

    private AgentRunState DetermineOutputState(ModelOutputEntry outputEntry) {
        if (outputEntry.ToolCalls is not { Count: > 0 }) { return AgentRunState.WaitingInput; }
        return HasAllToolResults(outputEntry)
            ? AgentRunState.ToolResultsReady
            : AgentRunState.WaitingToolResults;
    }

    private async Task<StepOutcome> ExecuteStateAsync(AgentRunState state, CancellationToken cancellationToken) {
        switch (state) {
            case AgentRunState.WaitingInput:
                return ProcessWaitingInput();
            case AgentRunState.PendingInput:
            case AgentRunState.PendingToolResults:
                return await ProcessPendingInputAsync(cancellationToken).ConfigureAwait(false);
            case AgentRunState.WaitingToolResults:
                return await ProcessWaitingToolResultsAsync(cancellationToken).ConfigureAwait(false);
            case AgentRunState.ToolResultsReady:
                return ProcessToolResultsReady();
            default:
                return StepOutcome.NoProgress;
        }
    }

    private StepOutcome ProcessWaitingInput() {
        if (!_pendingInputs.TryDequeue(out var pending)) { return StepOutcome.NoProgress; }

        var entry = AppendModelInput(pending.Text);
        _activeInvocationOptions = pending.Options;
        _activeRunLoop = null;

        DebugUtil.Print(StateMachineDebugCategory, $"[StateMachine] Input dequeued strategy={pending.Options} length={pending.Text.Length}");

        return StepOutcome.FromInput(entry);
    }

    private async Task<StepOutcome> ProcessPendingInputAsync(CancellationToken cancellationToken) {
        var context = EnsureRunLoopContext();
        context.StepCount++;

        var liveContext = _state.RenderLiveContext();
        DebugUtil.Print(ProviderDebugCategory, $"[StateMachine] Rendering context step={context.StepCount} count={liveContext.Count}");

        var invocation = new ModelInvocationDescriptor(context.Profile.Client.Name, context.Profile.Client.Specification, context.Profile.ModelId);
        var request = new LlmRequest(context.Profile.ModelId, liveContext, context.Tools);

        var deltas = context.Profile.Client.CallModelAsync(request, cancellationToken);
        var aggregatedOutput = await ModelOutputAccumulator.AggregateAsync(deltas, invocation, cancellationToken).ConfigureAwait(false);
        var normalizedOutput = NormalizeToolCalls(aggregatedOutput);

        _pendingToolResults.Clear();

        var appended = _state.AppendModelOutput(normalizedOutput);
        context.LastOutput = appended;

        var toolCallCount = appended.ToolCalls?.Count ?? 0;
        DebugUtil.Print(ProviderDebugCategory, $"[StateMachine] Model output appended segments={appended.Contents.Count} toolCalls={toolCallCount}");

        return StepOutcome.FromOutput(appended);
    }

    private async Task<StepOutcome> ProcessWaitingToolResultsAsync(CancellationToken cancellationToken) {
        if (_state.History.Count == 0 || _state.History[^1] is not ModelOutputEntry outputEntry) {
            DebugUtil.Print(StateMachineDebugCategory, "[StateMachine] WaitingToolResults but no model output available");
            return StepOutcome.NoProgress;
        }

        var nextCall = FindNextPendingToolCall(outputEntry);
        if (nextCall is null) { return StepOutcome.NoProgress; }

        var context = EnsureRunLoopContext();
        context.StepCount++;

        var record = await _toolExecutor.ExecuteAsync(nextCall, cancellationToken).ConfigureAwait(false);
        _pendingToolResults[nextCall.ToolCallId] = record;

        DebugUtil.Print(StateMachineDebugCategory, $"[StateMachine] Tool executed toolName={record.ToolName} callId={record.ToolCallId} status={record.Status}");

        return StepOutcome.FromToolExecution();
    }

    private StepOutcome ProcessToolResultsReady() {
        if (_state.History.Count == 0 || _state.History[^1] is not ModelOutputEntry outputEntry) { return StepOutcome.NoProgress; }

        if (outputEntry.ToolCalls is not { Count: > 0 }) { return StepOutcome.NoProgress; }

        var context = EnsureRunLoopContext();

        var executionRecords = new List<ToolExecutionRecord>(outputEntry.ToolCalls.Count);
        var results = new HistoryToolCallResult[outputEntry.ToolCalls.Count];

        for (var index = 0; index < outputEntry.ToolCalls.Count; index++) {
            var call = outputEntry.ToolCalls[index];
            if (!_pendingToolResults.TryGetValue(call.ToolCallId, out var record)) {
                DebugUtil.Print(StateMachineDebugCategory, $"[StateMachine] Missing tool execution record callId={call.ToolCallId}");
                return StepOutcome.NoProgress;
            }

            executionRecords.Add(record);
            results[index] = CreateHistoryResult(record);
        }

        context.StepCount++;

        var failure = results.FirstOrDefault(static result => result.Status == ToolExecutionStatus.Failed);
        var executeError = failure is null
            ? null
            : LevelOfDetailSections.ToPlainText(failure.Result.GetSections(LevelOfDetail.Live));

        var entry = new ToolResultsEntry(results, executeError);
        entry = entry with { Metadata = ToolResultMetadataHelper.PopulateSummary(executionRecords, entry.Metadata) };

        var appended = AppendToolResultsWithSummary(entry);
        context.LastToolResults = appended;
        _pendingToolResults.Clear();

        DebugUtil.Print(StateMachineDebugCategory, $"[StateMachine] Tool results appended count={results.Length} failure={(failure is null ? "none" : failure.ToolCallId)}");

        return StepOutcome.FromToolResults(appended);
    }

    private ToolCallRequest? FindNextPendingToolCall(ModelOutputEntry outputEntry) {
        if (outputEntry.ToolCalls is not { Count: > 0 }) { return null; }

        foreach (var call in outputEntry.ToolCalls) {
            if (!_pendingToolResults.ContainsKey(call.ToolCallId)) { return call; }
        }

        return null;
    }

    private bool HasAllToolResults(ModelOutputEntry outputEntry) {
        if (outputEntry.ToolCalls is not { Count: > 0 }) { return false; }
        if (_pendingToolResults.Count < outputEntry.ToolCalls.Count) { return false; }

        foreach (var call in outputEntry.ToolCalls) {
            if (!_pendingToolResults.ContainsKey(call.ToolCallId)) { return false; }
        }

        return true;
    }

    private RunLoopContext EnsureRunLoopContext() {
        if (_activeRunLoop is not null) { return _activeRunLoop; }

        var options = _activeInvocationOptions ?? _defaultInvocationOptions;
        _activeInvocationOptions ??= options;

        var profile = _router.Resolve(options);
        var tools = ToolDefinitionBuilder.FromTools(_toolCatalog.Tools);
        DebugUtil.Print(ProviderDebugCategory, $"[Orchestrator] Tool definitions count={tools.Length}");

        _activeRunLoop = new RunLoopContext(profile, tools);
        return _activeRunLoop;
    }

    private ToolResultsEntry AppendToolResultsWithSummary(ToolResultsEntry entry) {
        var metadata = ToolResultMetadataHelper.PopulateSummary(entry.Results, entry.Metadata);
        entry = entry with { Metadata = metadata };
        return _state.AppendToolResults(entry);
    }

    private ModelInputEntry AppendModelInput(string text) {
        var sections = LevelOfDetailSections.FromSingleSection("default", text);
        return _state.AppendModelInput(new ModelInputEntry(sections));
    }

    private ModelOutputEntry NormalizeToolCalls(ModelOutputEntry entry) {
        if (entry.ToolCalls is not { Count: > 0 }) { return entry; }

        var builder = ImmutableArray.CreateBuilder<ToolCallRequest>(entry.ToolCalls.Count);

        foreach (var request in entry.ToolCalls) {
            builder.Add(NormalizeToolCall(request));
        }

        return entry with { ToolCalls = builder.ToImmutable() };
    }

    private ToolCallRequest NormalizeToolCall(ToolCallRequest request) {
        if (!_toolCatalog.TryGet(request.ToolName, out var tool)) {
            return request with {
                ParseWarning = CombineMessages(request.ParseWarning, "tool_definition_missing")
            };
        }

        var parsed = ToolArgumentParser.ParseArguments(tool, request.RawArguments);
        return request with {
            Arguments = parsed.Arguments,
            ParseError = CombineMessages(request.ParseError, parsed.ParseError),
            ParseWarning = CombineMessages(request.ParseWarning, parsed.ParseWarning)
        };
    }

    private static string? CombineMessages(string? first, string? second) {
        if (string.IsNullOrWhiteSpace(first)) { return second; }
        if (string.IsNullOrWhiteSpace(second)) { return first; }
        return string.Concat(first, "; ", second);
    }

    public AgentToolExecutionResult ExecuteTool(ToolCallRequest request, CancellationToken cancellationToken = default) {
        var executionRecords = _toolExecutor.ExecuteBatchAsync(new[] { request }, cancellationToken).GetAwaiter().GetResult();
        if (executionRecords.Count == 0) { return AgentToolExecutionResult.NoResults(); }

        var results = executionRecords
            .Select(static record => CreateHistoryResult(record))
            .ToArray();

        var failure = results.FirstOrDefault(static result => result.Status == ToolExecutionStatus.Failed);
        var failureMessage = failure is null
            ? null
            : LevelOfDetailSections.ToPlainText(failure.Result.GetSections(LevelOfDetail.Live));

        var entry = new ToolResultsEntry(results, failureMessage);
        entry = entry with { Metadata = ToolResultMetadataHelper.PopulateSummary(executionRecords, entry.Metadata) };

        var appended = _state.AppendToolResults(entry);
        return failure is null
            ? AgentToolExecutionResult.Success(appended)
            : AgentToolExecutionResult.SuccessWithFailure(appended, failureMessage);
    }

    private void ResetInvocation() {
        _activeRunLoop = null;
        _activeInvocationOptions = null;
        _lastLoggedState = null;
    }

    private void LogStateIfChanged(AgentRunState state) {
        if (_lastLoggedState is AgentRunState previous && previous == state) { return; }

        DebugUtil.Print(StateMachineDebugCategory, $"[StateMachine] state={state} historyCount={_state.History.Count} pendingToolResults={_pendingToolResults.Count}");
        _lastLoggedState = state;
    }

    private static AgentStepResult CreateStepResult(AgentRunState before, AgentRunState after, StepOutcome outcome)
        => new(outcome.ProgressMade, before, after, outcome.Input, outcome.Output, outcome.ToolResults);

    private sealed class RunLoopContext {
        public RunLoopContext(LlmProfile profile, ImmutableArray<ToolDefinition> tools) {
            Profile = profile;
            Tools = tools;
        }

        public LlmProfile Profile { get; }
        public ImmutableArray<ToolDefinition> Tools { get; }
        public ModelOutputEntry? LastOutput { get; set; }
        public ToolResultsEntry? LastToolResults { get; set; }
        public int StepCount { get; set; }
    }

    private readonly record struct PendingUserInput(string Text, string Options);

    private readonly record struct StepOutcome(
        bool ProgressMade,
        ModelInputEntry? Input,
        ModelOutputEntry? Output,
        ToolResultsEntry? ToolResults
    ) {
        public static StepOutcome NoProgress => default;

        public static StepOutcome FromInput(ModelInputEntry input)
            => new(true, input, null, null);

        public static StepOutcome FromOutput(ModelOutputEntry output)
            => new(true, null, output, null);

        public static StepOutcome FromToolResults(ToolResultsEntry toolResults)
            => new(true, null, null, toolResults);

        public static StepOutcome FromToolExecution()
            => new(true, null, null, null);
    }

    private static HistoryToolCallResult CreateHistoryResult(ToolExecutionRecord record) {
        var sections = LevelOfDetailSections.CreateUniform(
            new[] { new KeyValuePair<string, string>(string.Empty, record.Result.Live) }
        );
        return new HistoryToolCallResult(
            record.ToolName,
            record.ToolCallId,
            record.Status,
            sections,
            record.Elapsed
        );
    }
}

internal sealed record AgentInvocationResult(
    ModelOutputEntry Output,
    ToolResultsEntry? ToolResults
);

internal readonly record struct AgentStepResult(
    bool ProgressMade,
    AgentRunState StateBefore,
    AgentRunState StateAfter,
    ModelInputEntry? Input,
    ModelOutputEntry? Output,
    ToolResultsEntry? ToolResults
) {
    public bool BlockedOnInput => !ProgressMade && StateAfter == AgentRunState.WaitingInput;
}

internal enum AgentRunState {
    WaitingInput,
    PendingInput,
    WaitingToolResults,
    ToolResultsReady,
    PendingToolResults
}

internal enum AgentToolExecutionResultStatus {
    Success,
    ToolNotRegistered,
    NoResults
}

internal sealed record AgentToolExecutionResult(
    AgentToolExecutionResultStatus Status,
    ToolResultsEntry? Entry,
    string? FailureMessage,
    string? ToolName
) {
    public static AgentToolExecutionResult ToolNotRegistered(string toolName)
        => new(AgentToolExecutionResultStatus.ToolNotRegistered, null, null, toolName);

    public static AgentToolExecutionResult NoResults()
        => new(AgentToolExecutionResultStatus.NoResults, null, null, null);

    public static AgentToolExecutionResult Success(ToolResultsEntry entry)
        => new(AgentToolExecutionResultStatus.Success, entry, null, null);

    public static AgentToolExecutionResult SuccessWithFailure(ToolResultsEntry entry, string? failureMessage)
        => new(AgentToolExecutionResultStatus.Success, entry, failureMessage, null);
}
