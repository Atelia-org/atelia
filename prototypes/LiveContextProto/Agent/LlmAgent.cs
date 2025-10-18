using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
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

    private readonly AgentState _state;
    private readonly ToolExecutor _toolExecutor;

    /// 未来扩展到支持多种输入渠道，比如增加即时通讯软件发来的消息。还需要增加发送方身份标识。
    /// 规划后续把部分信息直送LiveScreen，引入LiveEvent机制，要保证每条Event都能被LLM看到，又能直通LiveScreen来减少延迟(以LlmAgent状态机Step数计量)。
    private readonly ConcurrentQueue<PendingUserInput> _pendingInputs = new();
    private readonly Dictionary<string, ToolExecutionRecord> _pendingToolResults = new(StringComparer.OrdinalIgnoreCase);

    private readonly ImmutableArray<ToolDefinition> _toolDefinitions;
    private AgentRunState? _lastLoggedState;

    public LlmAgent(AgentState state, ToolExecutor toolExecutor) {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _toolExecutor = toolExecutor ?? throw new ArgumentNullException(nameof(toolExecutor));
        _toolDefinitions = ToolDefinitionBuilder.FromTools(_toolExecutor.Tools);
        DebugUtil.Print(ProviderDebugCategory, $"[Orchestrator] Tool definitions count={_toolDefinitions.Length}");
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

    public void EnqueueUserInput(string text) {
        if (string.IsNullOrWhiteSpace(text)) { throw new ArgumentException("Value cannot be null or whitespace.", nameof(text)); }
        var timestamp = DateTimeOffset.Now;
        _pendingInputs.Enqueue(new PendingUserInput(text, timestamp));
    }

    public async Task<AgentStepResult> DoStepAsync(LlmProfile profile, CancellationToken cancellationToken = default) {
        if (profile is null) { throw new ArgumentNullException(nameof(profile)); }

        var stateBefore = DetermineState();
        LogStateIfChanged(stateBefore);

        var outcome = await ExecuteStateAsync(stateBefore, profile, cancellationToken).ConfigureAwait(false);

        var stateAfter = DetermineState();
        LogStateIfChanged(stateAfter);

        if (stateAfter == AgentRunState.WaitingInput) {
            ResetInvocation();
        }

        return CreateStepResult(stateBefore, stateAfter, outcome);
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

    private async Task<StepOutcome> ExecuteStateAsync(AgentRunState state, LlmProfile profile, CancellationToken cancellationToken) {
        switch (state) {
            case AgentRunState.WaitingInput:
                return ProcessWaitingInput();
            case AgentRunState.PendingInput:
            case AgentRunState.PendingToolResults:
                return await ProcessPendingInputAsync(profile, cancellationToken).ConfigureAwait(false);
            case AgentRunState.WaitingToolResults:
                return await ProcessWaitingToolResultsAsync(cancellationToken).ConfigureAwait(false);
            case AgentRunState.ToolResultsReady:
                return ProcessToolResultsReady();
            default:
                return StepOutcome.NoProgress;
        }
    }

    private StepOutcome ProcessWaitingInput() {
        if (_pendingInputs.IsEmpty) { return StepOutcome.NoProgress; }

        var builder = new StringBuilder();
        var totalInputLength = 0;
        var drainedCount = 0;

        while (_pendingInputs.TryDequeue(out var pending)) {
            if (drainedCount > 0) {
                builder.AppendLine().AppendLine();
            }

            var heading = pending.Timestamp.ToString("o");

            builder.Append("### ").AppendLine(heading);
            builder.AppendLine();
            builder.AppendLine(pending.Text);

            totalInputLength += pending.Text.Length;
            drainedCount++;
        }

        if (drainedCount == 0) { return StepOutcome.NoProgress; }

        var aggregated = builder.ToString().TrimEnd('\r', '\n');

        var entry = AppendModelInput(aggregated);

        DebugUtil.Print(StateMachineDebugCategory, $"[StateMachine] Inputs dequeued count={drainedCount} totalInputLength={totalInputLength} aggregatedLength={aggregated.Length}");

        return StepOutcome.FromInput(entry);
    }

    private async Task<StepOutcome> ProcessPendingInputAsync(LlmProfile profile, CancellationToken cancellationToken) {
        var liveContext = _state.RenderLiveContext();
        DebugUtil.Print(ProviderDebugCategory, $"[StateMachine] Rendering context count={liveContext.Count}");

        var invocation = new ModelInvocationDescriptor(profile.Client.Name, profile.Client.Specification, profile.ModelId);
        var request = new LlmRequest(profile.ModelId, SystemInstruction, liveContext, _toolDefinitions);

        var deltas = profile.Client.CallModelAsync(request, cancellationToken);
        var aggregatedOutput = await ModelOutputAccumulator.AggregateAsync(deltas, invocation, cancellationToken).ConfigureAwait(false);
        var normalizedOutput = NormalizeToolCalls(aggregatedOutput);

        _pendingToolResults.Clear();

        var appended = _state.AppendModelOutput(normalizedOutput);

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

        var record = await _toolExecutor.ExecuteAsync(nextCall, cancellationToken).ConfigureAwait(false);
        _pendingToolResults[nextCall.ToolCallId] = record;

        DebugUtil.Print(StateMachineDebugCategory, $"[StateMachine] Tool executed toolName={record.ToolName} callId={record.ToolCallId} status={record.Status}");

        return StepOutcome.FromToolExecution();
    }

    private StepOutcome ProcessToolResultsReady() {
        if (_state.History.Count == 0 || _state.History[^1] is not ModelOutputEntry outputEntry) { return StepOutcome.NoProgress; }

        if (outputEntry.ToolCalls is not { Count: > 0 }) { return StepOutcome.NoProgress; }

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

        var failure = results.FirstOrDefault(static result => result.Status == ToolExecutionStatus.Failed);
        var executeError = failure is null
            ? null
            : LevelOfDetailSections.ToPlainText(failure.Result.GetSections(LevelOfDetail.Live));

        var entry = new ToolResultsEntry(results, executeError);
        entry = entry with { Metadata = ToolResultMetadataHelper.PopulateSummary(executionRecords, entry.Metadata) };

        var appended = AppendToolResultsWithSummary(entry);
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
        if (!_toolExecutor.TryGetTool(request.ToolName, out var tool)) {
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
        _lastLoggedState = null;
    }

    private void LogStateIfChanged(AgentRunState state) {
        if (_lastLoggedState is AgentRunState previous && previous == state) { return; }

        DebugUtil.Print(StateMachineDebugCategory, $"[StateMachine] state={state} historyCount={_state.History.Count} pendingToolResults={_pendingToolResults.Count}");
        _lastLoggedState = state;
    }

    private static AgentStepResult CreateStepResult(AgentRunState before, AgentRunState after, StepOutcome outcome)
        => new(outcome.ProgressMade, before, after, outcome.Input, outcome.Output, outcome.ToolResults);

    private readonly record struct PendingUserInput(string Text, DateTimeOffset Timestamp);

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
