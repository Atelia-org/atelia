using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Atelia.Diagnostics;
using Atelia.Completion.Abstractions;

namespace Atelia.Agent.Core.History;

internal static class CompletionAccumulator {
    private const string DebugCategory = "Provider";

    /// <summary>
    /// 按 chunk 到达顺序聚合为 <see cref="ActionEntry"/>，保留 text/tool_call 之间的 ordering。
    /// 设计契约见 <c>docs/Agent/Thinking-Replay-Design.md §5</c>。
    /// </summary>
    public static async Task<ActionEntry> AggregateAsync(
        IAsyncEnumerable<CompletionChunk> deltas,
        CompletionDescriptor invocation,
        CancellationToken cancellationToken
    ) {
        var blocks = new List<ActionBlock>();
        var contentBuilder = new StringBuilder();
        TokenUsage? tokenUsage = null;
        int toolCallCount = 0;

        void FlushPendingText() {
            if (contentBuilder.Length == 0) { return; }

            blocks.Add(new ActionBlock.Text(contentBuilder.ToString()));
            contentBuilder.Clear();
        }

        await foreach (var delta in deltas.WithCancellation(cancellationToken)) {
            DebugUtil.Trace(DebugCategory, $"[Aggregate] Received delta kind={delta.Kind}");

            switch (delta.Kind) {
                case CompletionChunkKind.Content:
                    if (!string.IsNullOrEmpty(delta.Content)) {
                        contentBuilder.Append(delta.Content);
                    }
                    break;
                case CompletionChunkKind.ToolCall:
                    FlushPendingText();
                    if (delta.ToolCall is not null) {
                        if (delta.ToolCall.Arguments is null && string.IsNullOrWhiteSpace(delta.ToolCall.ParseError)) {
                            DebugUtil.Warning(DebugCategory, $"[Aggregate] Tool call missing parsed arguments toolName={delta.ToolCall.ToolName} callId={delta.ToolCall.ToolCallId}");
                        }
                        blocks.Add(new ActionBlock.ToolCall(delta.ToolCall));
                        toolCallCount++;
                    }
                    break;
                case CompletionChunkKind.Thinking:
                    FlushPendingText();
                    if (delta.Thinking is not null) {
                        // OpaquePayload 在此完全透明：Accumulator 不解释 bytes，仅按到达顺序串入 Blocks。
                        // 详见 docs/Agent/Thinking-Replay-Design.md §5.2。
                        blocks.Add(new ActionBlock.Thinking(
                            invocation,
                            delta.Thinking.OpaquePayload,
                            delta.Thinking.PlainTextForDebug
                        ));
                    }
                    break;
                case CompletionChunkKind.Error:
                    FlushPendingText();
                    break;
                case CompletionChunkKind.TokenUsage:
                    FlushPendingText();
                    tokenUsage = delta.TokenUsage;
                    break;
                default:
                    FlushPendingText();
                    break;
            }
        }

        FlushPendingText();

        if (blocks.Count == 0) {
            // Guarantee at least one Text block (empty) so downstream consumers always have a deterministic shape.
            blocks.Add(new ActionBlock.Text(string.Empty));
        }

        var outputEntry = new ActionEntry(blocks, invocation);

        DebugUtil.Info(DebugCategory, $"[Aggregate] Produced output blocks={blocks.Count}, toolCalls={toolCallCount}");

        return outputEntry;
    }
}
