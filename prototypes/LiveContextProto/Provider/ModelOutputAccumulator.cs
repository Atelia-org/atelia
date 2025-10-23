using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Atelia.Diagnostics;
using Atelia.LiveContextProto.State.History;
using Atelia.LiveContextProto.Context;

namespace Atelia.LiveContextProto.Provider;

internal static class ModelOutputAccumulator {
    private const string DebugCategory = "Provider";

    public static async Task<ModelOutputEntry> AggregateAsync(
        IAsyncEnumerable<ModelOutputDelta> deltas,
        ModelInvocationDescriptor invocation,
        CancellationToken cancellationToken
    ) {
        var contents = new List<string>();
        var toolCalls = new List<ToolCallRequest>();
        var contentBuilder = new StringBuilder();
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

        var fullContentText = string.Join('\n', contents);
        var outputEntry = new ModelOutputEntry(fullContentText, toolCalls, invocation);

        DebugUtil.Print(DebugCategory, $"[Aggregate] Produced output fullContentText.Length={fullContentText.Length}, toolCalls={toolCalls.Count}");

        return outputEntry;
    }
}
