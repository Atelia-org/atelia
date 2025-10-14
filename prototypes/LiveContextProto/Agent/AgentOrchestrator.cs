using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Atelia.Diagnostics;
using Atelia.LiveContextProto.Provider;
using Atelia.LiveContextProto.State;
using Atelia.LiveContextProto.State.History;
using Atelia.LiveContextProto.Tools;
using System.Collections.Generic;
using System.Linq;

namespace Atelia.LiveContextProto.Agent;

internal sealed record AgentInvocationResult(
    ModelOutputEntry Output,
    ToolResultsEntry? ToolResults
);

internal sealed class AgentOrchestrator {
    private const string DebugCategory = "Provider";

    private readonly AgentState _state;
    private readonly ProviderRouter _router;
    private readonly ToolExecutor _toolExecutor;
    private readonly ToolCatalog _toolCatalog;

    public AgentOrchestrator(AgentState state, ProviderRouter router, ToolExecutor toolExecutor, ToolCatalog toolCatalog) {
        _state = state;
        _router = router;
        _toolExecutor = toolExecutor;
        _toolCatalog = toolCatalog ?? throw new ArgumentNullException(nameof(toolCatalog));
    }

    public async Task<AgentInvocationResult> InvokeAsync(
        ProviderInvocationOptions options,
        CancellationToken cancellationToken = default
    ) {
        var context = _state.RenderLiveContext();
        DebugUtil.Print(DebugCategory, $"[Orchestrator] Rendering context count={context.Count}");

        var plan = _router.Resolve(options);
        var request = new ProviderRequest(plan.StrategyId, plan.Invocation, context);

        var deltas = plan.Client.CallModelAsync(request, cancellationToken);
        var aggregate = await ModelOutputAccumulator.AggregateAsync(deltas, plan.Invocation, cancellationToken);
        var normalizedOutput = NormalizeToolCalls(aggregate.OutputEntry);

        var outputEntry = _state.AppendModelOutput(normalizedOutput);
        ToolResultsEntry? toolResultsEntry = null;

        if (aggregate.ToolResultsEntry is not null) {
            toolResultsEntry = AppendToolResultsWithSummary(aggregate.ToolResultsEntry);
        }
        else {
            toolResultsEntry = await ExecuteToolsAsync(outputEntry, cancellationToken).ConfigureAwait(false);
        }

        DebugUtil.Print(DebugCategory, "[Orchestrator] Invocation completed and appended to state");

        return new AgentInvocationResult(outputEntry, toolResultsEntry);
    }

    private async Task<ToolResultsEntry?> ExecuteToolsAsync(ModelOutputEntry outputEntry, CancellationToken cancellationToken) {
        if (outputEntry.ToolCalls is not { Count: > 0 }) { return null; }

        var executionRecords = await _toolExecutor.ExecuteBatchAsync(outputEntry.ToolCalls, cancellationToken).ConfigureAwait(false);
        if (executionRecords.Count == 0) { return null; }

        var results = executionRecords.Select(record => record.CallResult).ToArray();
        var failure = results.FirstOrDefault(static result => result.Status == ToolExecutionStatus.Failed);
        var executeError = failure is null ? null : failure.Result;

        var entry = new ToolResultsEntry(results, executeError);
        entry = entry with { Metadata = ToolResultMetadataHelper.PopulateSummary(executionRecords, entry.Metadata) };

        DebugUtil.Print(DebugCategory, $"[Orchestrator] Tool executor produced results count={results.Length} failure={(failure is null ? "none" : failure.ToolCallId)}");

        return AppendToolResultsWithSummary(entry);
    }

    private ToolResultsEntry AppendToolResultsWithSummary(ToolResultsEntry entry) {
        var metadata = ToolResultMetadataHelper.PopulateSummary(entry.Results, entry.Metadata);
        entry = entry with { Metadata = metadata };
        return _state.AppendToolResults(entry);
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
}
