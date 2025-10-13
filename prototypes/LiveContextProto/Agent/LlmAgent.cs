using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Atelia.Diagnostics;
using Atelia.LiveContextProto.Provider;
using Atelia.LiveContextProto.State;
using Atelia.LiveContextProto.State.History;
using Atelia.LiveContextProto.Tools;

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

    public IReadOnlyDictionary<string, string> LiveInfoSections => _state.LiveInfoSections;

    public IReadOnlyList<IContextMessage> RenderLiveContext() => _state.RenderLiveContext();

    public void Reset() => _state.Reset();

    public void UpdateMemoryNotebook(string? content) => _state.UpdateMemoryNotebook(content);

    public void UpdateLiveInfoSection(string sectionName, string? content) => _state.UpdateLiveInfoSection(sectionName, content);

    public ModelInputEntry AppendUserInput(string text) {
        if (string.IsNullOrWhiteSpace(text)) { throw new ArgumentException("Value cannot be null or whitespace.", nameof(text)); }

        var sections = new List<KeyValuePair<string, string>> {
            new("default", text)
        };

        return _state.AppendModelInput(new ModelInputEntry(sections));
    }

    public AgentInvocationOutcome InvokeStubProvider(string? stubScript, CancellationToken cancellationToken = default) {
        try {
            var options = new ProviderInvocationOptions(ProviderRouter.DefaultStubStrategy, stubScript);
            var result = _orchestrator.InvokeAsync(options, cancellationToken).GetAwaiter().GetResult();
            return new AgentInvocationOutcome(result, null);
        }
        catch (Exception ex) {
            DebugUtil.Print(ProviderDebugCategory, $"Stub provider invocation failed: {ex}");
            return new AgentInvocationOutcome(null, ex);
        }
    }

    public AgentToolExecutionResult ExecuteInteractiveTool(bool includeError, CancellationToken cancellationToken = default) {
        var toolName = includeError ? "diagnostics.raise" : "memory.search";

        if (!_toolCatalog.TryGet(toolName, out var tool)) { return AgentToolExecutionResult.ToolNotRegistered(toolName); }

        var rawArguments = includeError
            ? "{\"reason\":\"Console trigger\"}"
            : "{\"query\":\"LiveContextProto 核心阶段\"}";

        var request = ToolArgumentParser.CreateRequest(tool, GenerateConsoleToolCallId(), rawArguments);
        var requests = new[] { request };

        var records = _toolExecutor.ExecuteBatchAsync(requests, cancellationToken).GetAwaiter().GetResult();
        if (records.Count == 0) { return AgentToolExecutionResult.NoResults(); }

        var results = records.Select(static record => record.CallResult).ToArray();
        var failure = results.FirstOrDefault(static result => result.Status == ToolExecutionStatus.Failed);
        var entry = new ToolResultsEntry(results, failure?.Result) {
            Metadata = ToolResultMetadataHelper.PopulateSummary(records, ImmutableDictionary<string, object?>.Empty)
        };

        var appended = _state.AppendToolResults(entry);

        return failure is null
            ? AgentToolExecutionResult.Success(appended)
            : AgentToolExecutionResult.SuccessWithFailure(appended, failure.Result);
    }

    public AgentToolExecutionResult ExecuteTool(ToolCallRequest request, CancellationToken cancellationToken = default) {
        var executionRecords = _toolExecutor.ExecuteBatchAsync(new[] { request }, cancellationToken).GetAwaiter().GetResult();
        if (executionRecords.Count == 0) { return AgentToolExecutionResult.NoResults(); }

        var results = executionRecords.Select(static record => record.CallResult).ToArray();
        var failure = results.FirstOrDefault(static result => result.Status == ToolExecutionStatus.Failed);
        var entry = new ToolResultsEntry(results, failure?.Result);
        entry = entry with { Metadata = ToolResultMetadataHelper.PopulateSummary(executionRecords, entry.Metadata) };

        var appended = _state.AppendToolResults(entry);
        return failure is null
            ? AgentToolExecutionResult.Success(appended)
            : AgentToolExecutionResult.SuccessWithFailure(appended, failure.Result);
    }

    private static string GenerateConsoleToolCallId()
        => $"console-{Guid.NewGuid():N}";
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
