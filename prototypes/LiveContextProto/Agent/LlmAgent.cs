using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Atelia.Diagnostics;
using Atelia.LiveContextProto.Provider;
using Atelia.LiveContextProto.State;
using Atelia.LiveContextProto.State.History;
using Atelia.LiveContextProto.Tools;
using Atelia.LiveContextProto.Context;

namespace Atelia.LiveContextProto.Agent;

internal sealed class LlmAgent {
    private const string ProviderDebugCategory = "Provider";

    private readonly AgentState _state;
    private readonly AgentOrchestrator _orchestrator;
    private readonly ToolExecutor _toolExecutor;
    private readonly ToolCatalog _toolCatalog;

    public LlmAgent(AgentState state, AgentOrchestrator orchestrator, ToolExecutor toolExecutor, ToolCatalog toolCatalog) {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _toolExecutor = toolExecutor ?? throw new ArgumentNullException(nameof(toolExecutor));
        _toolCatalog = toolCatalog ?? throw new ArgumentNullException(nameof(toolCatalog));
    }

    public string SystemInstruction => _state.SystemInstruction;

    public string MemoryNotebookSnapshot => _state.MemoryNotebookSnapshot;

    public IReadOnlyList<IContextMessage> RenderLiveContext() => _state.RenderLiveContext();

    public void Reset() => _state.Reset();

    public void UpdateMemoryNotebook(string? content) => _state.UpdateMemoryNotebook(content);

    public ModelInputEntry AppendUserInput(string text) {
        if (string.IsNullOrWhiteSpace(text)) { throw new ArgumentException("Value cannot be null or whitespace.", nameof(text)); }

        var sections = LevelOfDetailSections.FromSingleSection("default", text);

        return _state.AppendModelInput(new ModelInputEntry(sections));
    }

    public AgentInvocationOutcome InvokeProvider(LlmInvocationOptions options, CancellationToken cancellationToken = default) {
        if (options is null) { throw new ArgumentNullException(nameof(options)); }

        try {
            var result = _orchestrator.InvokeAsync(options, cancellationToken).GetAwaiter().GetResult();
            return new AgentInvocationOutcome(result, null);
        }
        catch (Exception ex) {
            DebugUtil.Print(ProviderDebugCategory, $"Provider invocation failed (strategy={options.StrategyId}): {ex}");
            return new AgentInvocationOutcome(null, ex);
        }
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

internal readonly record struct AgentInvocationOutcome(AgentInvocationResult? Result, Exception? Exception) {
    public bool Success => Result is not null && Exception is null;
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
