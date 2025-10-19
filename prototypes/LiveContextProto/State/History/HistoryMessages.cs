using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Atelia.LiveContextProto.Context;
using Atelia.LiveContextProto.Tools;

namespace Atelia.LiveContextProto.State.History;

internal sealed class ModelInputMessage : IModelInputMessage {
    private readonly ModelInputEntry _entry;
    private readonly LevelOfDetail _detailLevel;

    public ModelInputMessage(ModelInputEntry entry, LevelOfDetail detailLevel) {
        _entry = entry ?? throw new ArgumentNullException(nameof(entry));
        _detailLevel = detailLevel;
    }

    public ContextMessageRole Role => ContextMessageRole.ModelInput;

    public DateTimeOffset Timestamp => _entry.Timestamp;

    public ImmutableDictionary<string, object?> Metadata => _entry.Metadata;

    public IReadOnlyList<KeyValuePair<string, string>> ContentSections
        => _entry.ContentSections.GetSections(_detailLevel);

    public IReadOnlyList<IContextAttachment> Attachments => _entry.Attachments;

    public LevelOfDetail DetailLevel => _detailLevel;
}

internal sealed class ToolResultsMessage : IToolResultsMessage {
    private readonly ToolResultsEntry _entry;
    private readonly LevelOfDetail _detailLevel;
    private IReadOnlyList<ToolCallResult>? _cachedResults;

    public ToolResultsMessage(ToolResultsEntry entry, LevelOfDetail detailLevel) {
        _entry = entry ?? throw new ArgumentNullException(nameof(entry));
        _detailLevel = detailLevel;
    }

    public ContextMessageRole Role => ContextMessageRole.ToolResult;

    public DateTimeOffset Timestamp => _entry.Timestamp;

    public ImmutableDictionary<string, object?> Metadata => _entry.Metadata;

    public IReadOnlyList<ToolCallResult> Results => _cachedResults ??= BuildResults();

    public string? ExecuteError => _entry.ExecuteError;

    public LevelOfDetail DetailLevel => _detailLevel;

    private IReadOnlyList<ToolCallResult> BuildResults() {
        if (_entry.Results.Count == 0) { return Array.Empty<ToolCallResult>(); }

        var builder = new List<ToolCallResult>(_entry.Results.Count);
        foreach (var historyResult in _entry.Results) {
            var sections = historyResult.Result.GetSections(_detailLevel);
            builder.Add(
                new ToolCallResult(
                    historyResult.ToolName!,
                    historyResult.ToolCallId!,
                    historyResult.Status,
                    sections,
                    historyResult.Elapsed
                )
            );
        }

        return builder;
    }
}
