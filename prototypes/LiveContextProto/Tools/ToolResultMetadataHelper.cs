using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Atelia.LiveContextProto.Context;

namespace Atelia.LiveContextProto.Tools;

internal static class ToolResultMetadataHelper {
    public static ImmutableDictionary<string, object?> PopulateSummary(
        IReadOnlyCollection<ToolCallResult> results,
        ImmutableDictionary<string, object?> metadata
    ) {
        if (!metadata.ContainsKey("tool_call_count")) {
            metadata = metadata.SetItem("tool_call_count", results.Count);
        }

        var failedCount = results.Count(static result => result.Status == ToolExecutionStatus.Failed);
        if (!metadata.ContainsKey("tool_failed_count")) {
            metadata = metadata.SetItem("tool_failed_count", failedCount);
        }

        var totalElapsed = results.Sum(static result => result.Elapsed?.TotalMilliseconds ?? 0d);
        if (!metadata.ContainsKey("tool_elapsed_total_ms")) {
            metadata = metadata.SetItem("tool_elapsed_total_ms", totalElapsed);
        }

        return metadata;
    }

    public static ImmutableDictionary<string, object?> PopulateSummary(
        IReadOnlyList<ToolExecutionRecord> executionRecords,
        ImmutableDictionary<string, object?> metadata
    ) {
        var results = executionRecords.Select(static record => record.CallResult).ToArray();
        metadata = PopulateSummary(results, metadata);

        var perCallBuilder = ImmutableDictionary.CreateBuilder<string, object?>();
        foreach (var record in executionRecords) {
            if (record.Metadata.Count == 0) { continue; }
            perCallBuilder[record.CallResult.ToolCallId] = record.Metadata;
        }

        if (perCallBuilder.Count > 0 && !metadata.ContainsKey("per_call_metadata")) {
            metadata = metadata.SetItem("per_call_metadata", perCallBuilder.ToImmutable());
        }

        return metadata;
    }
}
