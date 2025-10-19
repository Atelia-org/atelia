using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Atelia.LiveContextProto.Context;

namespace Atelia.LiveContextProto.Tools;

internal sealed record LodToolCallResult {
    public LodToolCallResult(
        ToolExecutionStatus status,
        LevelOfDetailSections result,
        ImmutableDictionary<string, object?> metadata,
        string? toolName = null,
        string? toolCallId = null,
        TimeSpan? elapsed = null
    ) {
        Status = status;
        Result = result ?? throw new ArgumentNullException(nameof(result));
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        ToolName = toolName;
        ToolCallId = toolCallId;
        Elapsed = elapsed;
    }

    public ToolExecutionStatus Status { get; init; }

    public LevelOfDetailSections Result { get; init; }

    public ImmutableDictionary<string, object?> Metadata { get; init; }

    public string? ToolName { get; init; }

    public string? ToolCallId { get; init; }

    public TimeSpan? Elapsed { get; init; }

    public LodToolCallResult WithContext(
        string toolName,
        string toolCallId,
        TimeSpan? elapsed = null,
        ImmutableDictionary<string, object?>? metadata = null
    ) {
        if (string.IsNullOrWhiteSpace(toolName)) { throw new ArgumentException("Tool name cannot be empty.", nameof(toolName)); }
        if (string.IsNullOrWhiteSpace(toolCallId)) { throw new ArgumentException("Tool call id cannot be empty.", nameof(toolCallId)); }

        var mergedMetadata = metadata is null || metadata.Count == 0
            ? Metadata
            : Metadata.SetItems(metadata);

        return this with {
            ToolName = toolName,
            ToolCallId = toolCallId,
            Elapsed = elapsed ?? Elapsed,
            Metadata = mergedMetadata
        };
    }

    public static LodToolCallResult FromContent(
        ToolExecutionStatus status,
        LevelOfDetailContent content,
        ImmutableDictionary<string, object?>? metadata = null
    ) {
        if (content is null) { throw new ArgumentNullException(nameof(content)); }
        return new LodToolCallResult(status, ToSections(content), metadata ?? ImmutableDictionary<string, object?>.Empty);
    }

    public static LodToolCallResult FromContent(
        ToolExecutionStatus status,
        string basic,
        string? extra = null,
        ImmutableDictionary<string, object?>? metadata = null
    ) {
        var content = new LevelOfDetailContent(basic ?? string.Empty, extra);
        return FromContent(status, content, metadata);
    }

    public static LodToolCallResult Success(
        string basic,
        string? extra = null,
        ImmutableDictionary<string, object?>? metadata = null
    ) => FromContent(ToolExecutionStatus.Success, basic, extra, metadata);

    public LodToolCallResult WithMetadata(string key, object? value)
        => this with { Metadata = Metadata.SetItem(key, value) };

    public LodToolCallResult WithMetadata(IEnumerable<KeyValuePair<string, object?>> items)
        => this with { Metadata = Metadata.SetItems(items) };

    private static LevelOfDetailSections ToSections(LevelOfDetailContent content) {
        var basicSection = new[] { new KeyValuePair<string, string>(string.Empty, content.Basic) };
        IReadOnlyList<KeyValuePair<string, string>> extraSections;

        if (string.IsNullOrEmpty(content.Extra)) {
            extraSections = Array.Empty<KeyValuePair<string, string>>();
        }
        else {
            extraSections = new[] { new KeyValuePair<string, string>(string.Empty, content.Extra!) };
        }

        return new LevelOfDetailSections(basicSection, extraSections);
    }
}
