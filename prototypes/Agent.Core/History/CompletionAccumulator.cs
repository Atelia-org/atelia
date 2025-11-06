using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Atelia.Diagnostics;
using Atelia.Completion.Abstractions;

namespace Atelia.Agent.Core.History;

internal static class CompletionAccumulator {
    private const string DebugCategory = "Provider";

    public static async Task<ActionEntry> AggregateAsync(
        IAsyncEnumerable<CompletionChunk> deltas,
        CompletionDescriptor invocation,
        CancellationToken cancellationToken
    ) {
        var content = new List<string>();
        var toolCalls = new List<ParsedToolCall>();
        var contentBuilder = new StringBuilder();
        TokenUsage? tokenUsage = null;

        void CommitPendingContent() {
            if (contentBuilder.Length == 0) { return; }

            content.Add(contentBuilder.ToString());
            contentBuilder.Clear();
        }

        await foreach (var delta in deltas.WithCancellation(cancellationToken)) {
            DebugUtil.Print(DebugCategory, $"[Aggregate] Received delta kind={delta.Kind}");

            switch (delta.Kind) {
                case CompletionChunkKind.Content:
                    if (!string.IsNullOrEmpty(delta.Content)) {
                        contentBuilder.Append(delta.Content);
                    }
                    break;
                case CompletionChunkKind.ToolCall:
                    CommitPendingContent();
                    if (delta.ToolCall is not null) {
                        toolCalls.Add(delta.ToolCall);
                    }
                    break;
                case CompletionChunkKind.Error:
                    CommitPendingContent();
                    break;
                case CompletionChunkKind.TokenUsage:
                    CommitPendingContent();
                    tokenUsage = delta.TokenUsage;
                    break;
                default:
                    CommitPendingContent();
                    break;
            }
        }

        CommitPendingContent();

        if (content.Count == 0 && toolCalls.Count == 0) {
            // Guarantee at least one content slot for downstream consumers.
            content.Add(string.Empty);
        }

        foreach (var call in toolCalls) {
            if (call.Arguments is null && string.IsNullOrWhiteSpace(call.ParseError)) {
                DebugUtil.Print(DebugCategory, $"[Aggregate] Tool call missing parsed arguments toolName={call.ToolName} callId={call.ToolCallId}");
            }
        }

        var fullContentText = string.Join('\n', content);
        var outputEntry = new ActionEntry(fullContentText, toolCalls, invocation);

        DebugUtil.Print(DebugCategory, $"[Aggregate] Produced output fullContentText.Length={fullContentText.Length}, toolCalls={toolCalls.Count}");

        return outputEntry;
    }
}
