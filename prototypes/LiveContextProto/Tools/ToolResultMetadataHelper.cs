using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Atelia.LiveContextProto.Context;

namespace Atelia.LiveContextProto.Tools;

internal static class ToolResultMetadataHelper {
    public static ImmutableDictionary<string, object?> PopulateSummary(
        IReadOnlyCollection<LodToolCallResult> results,
        ImmutableDictionary<string, object?> metadata
    ) {
        metadata = PopulateSummaryCore(results, metadata);

        var perCallBuilder = ImmutableDictionary.CreateBuilder<string, object?>();
        foreach (var result in results) {
            if (string.IsNullOrWhiteSpace(result.ToolCallId)) { continue; }
            if (result.Metadata.Count == 0) { continue; }
            perCallBuilder[result.ToolCallId!] = result.Metadata;
        }

        if (perCallBuilder.Count > 0 && !metadata.ContainsKey("per_call_metadata")) {
            metadata = metadata.SetItem("per_call_metadata", perCallBuilder.ToImmutable());
        }

        return metadata;
    }

    private static ImmutableDictionary<string, object?> PopulateSummaryCore(
        IReadOnlyCollection<LodToolCallResult> results,
        ImmutableDictionary<string, object?> metadata
    ) {
        if (!metadata.ContainsKey("tool_call_count")) {
            metadata = metadata.SetItem("tool_call_count", results.Count);
        }

        var failedCount = results.Count(result => result.Status == ToolExecutionStatus.Failed);
        if (!metadata.ContainsKey("tool_failed_count")) {
            metadata = metadata.SetItem("tool_failed_count", failedCount);
        }

        var totalElapsed = results.Sum(result => result.Elapsed?.TotalMilliseconds ?? 0d);
        if (!metadata.ContainsKey("tool_elapsed_total_ms")) {
            metadata = metadata.SetItem("tool_elapsed_total_ms", totalElapsed);
        }

        return metadata;
    }
}
