using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Atelia.Diagnostics;
using Atelia.LiveContextProto.State.History;

namespace Atelia.LiveContextProto.Provider;

internal sealed record ModelInvocationAggregate(
    ModelOutputEntry OutputEntry,
    ToolResultsEntry? ToolResultsEntry
);

internal static class ModelOutputAccumulator {
    private const string DebugCategory = "Provider";

    public static async Task<ModelInvocationAggregate> AggregateAsync(
        IAsyncEnumerable<ModelOutputDelta> deltas,
        ModelInvocationDescriptor invocation,
        CancellationToken cancellationToken
    ) {
        var contents = new List<string>();
        var toolCalls = new List<ToolCallRequest>();
        var toolResults = new List<ToolCallResult>();
        var contentBuilder = new StringBuilder();
        string? executeError = null;
        TokenUsage? tokenUsage = null;

        await foreach (var delta in deltas.WithCancellation(cancellationToken)) {
            DebugUtil.Print(DebugCategory, $"[Aggregate] Received delta kind={delta.Kind}");

            switch (delta.Kind) {
                case ModelOutputDeltaKind.Content:
                    if (!string.IsNullOrEmpty(delta.ContentFragment)) {
                        contentBuilder.Append(delta.ContentFragment);
                    }

                    if (delta.EndSegment) {
                        if (contentBuilder.Length > 0) {
                            contents.Add(contentBuilder.ToString());
                            contentBuilder.Clear();
                        }
                        else {
                            contents.Add(string.Empty);
                        }
                    }

                    break;
                case ModelOutputDeltaKind.ToolCallDeclared:
                    if (delta.ToolCallRequest is not null) {
                        toolCalls.Add(delta.ToolCallRequest);
                    }
                    break;
                case ModelOutputDeltaKind.ToolResultProduced:
                    if (delta.ToolCallResult is not null) {
                        toolResults.Add(delta.ToolCallResult);
                    }
                    break;
                case ModelOutputDeltaKind.ExecuteError:
                    executeError = delta.ExecuteError;
                    break;
                case ModelOutputDeltaKind.TokenUsage:
                    tokenUsage = delta.TokenUsage;
                    break;
            }
        }

        if (contentBuilder.Length > 0) {
            contents.Add(contentBuilder.ToString());
        }

        if (contents.Count == 0 && toolCalls.Count == 0) {
            // Guarantee at least one content slot for downstream consumers.
            contents.Add(string.Empty);
        }

        var outputEntry = new ModelOutputEntry(contents, toolCalls, invocation);
        if (tokenUsage is not null) {
            var metadata = outputEntry.Metadata.SetItem("token_usage", tokenUsage);
            outputEntry = outputEntry with { Metadata = metadata };
        }

        ToolResultsEntry? toolResultsEntry = null;
        if (toolResults.Count > 0 || !string.IsNullOrWhiteSpace(executeError)) {
            toolResultsEntry = new ToolResultsEntry(toolResults, executeError);
            if (tokenUsage is not null) {
                var metadata = toolResultsEntry.Metadata.SetItem("token_usage", tokenUsage);
                toolResultsEntry = toolResultsEntry with { Metadata = metadata };
            }
        }

        DebugUtil.Print(DebugCategory, $"[Aggregate] Produced output segments={contents.Count}, toolCalls={toolCalls.Count}, toolResults={toolResults.Count}");

        return new ModelInvocationAggregate(outputEntry, toolResultsEntry);
    }
}
