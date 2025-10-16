using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using Atelia.Diagnostics;
using Atelia.LiveContextProto.State.History;
using Atelia.LiveContextProto.Context;
using Atelia.LiveContextProto.Tools;
using Atelia.LiveContextProto.Widgets;

namespace Atelia.LiveContextProto.State;

internal sealed class AgentState {
    private readonly List<HistoryEntry> _history = new();
    private readonly Func<DateTimeOffset> _timestampProvider;
    private readonly MemoryNotebookWidget _memoryNotebookWidget;
    private readonly ImmutableArray<IWidget> _widgets;

    private AgentState(Func<DateTimeOffset> timestampProvider, string systemInstruction) {
        _timestampProvider = timestampProvider;
        SystemInstruction = systemInstruction;
        _memoryNotebookWidget = new MemoryNotebookWidget();
        _widgets = ImmutableArray.Create<IWidget>(_memoryNotebookWidget);

        DebugUtil.Print("History", $"AgentState initialized with instruction length={systemInstruction.Length}");
    }

    public string SystemInstruction { get; private set; }

    public IReadOnlyList<HistoryEntry> History => _history;

    public string MemoryNotebookSnapshot => _memoryNotebookWidget.GetSnapshot();

    public MemoryNotebookWidget MemoryNotebookWidget => _memoryNotebookWidget;

    public static AgentState CreateDefault(string? systemInstruction = null, Func<DateTimeOffset>? timestampProvider = null) {
        var instruction = string.IsNullOrWhiteSpace(systemInstruction)
            ? "You are LiveContextProto, a placeholder agent validating the Conversation History refactor skeleton."
            : systemInstruction;

        var provider = timestampProvider ?? (() => DateTimeOffset.UtcNow);
        return new AgentState(provider, instruction);
    }

    public ModelInputEntry AppendModelInput(ModelInputEntry entry) {
        if (entry.ContentSections?.Live is not { Count: > 0 }) { throw new ArgumentException("ContentSections must contain at least one section.", nameof(entry)); }

        var enriched = AttachLiveScreen(entry);
        return AppendContextualEntry(enriched);
    }

    public ModelOutputEntry AppendModelOutput(ModelOutputEntry entry) {
        if ((entry.Contents is null || entry.Contents.Count == 0) && (entry.ToolCalls is null || entry.ToolCalls.Count == 0)) { throw new ArgumentException("ModelOutputEntry must include content or tool calls.", nameof(entry)); }

        return AppendContextualEntry(entry);
    }

    public ToolResultsEntry AppendToolResults(ToolResultsEntry entry) {
        if (entry.Results is not { Count: > 0 } && string.IsNullOrWhiteSpace(entry.ExecuteError)) { throw new ArgumentException("ToolResultsEntry must include results or an execution error.", nameof(entry)); }

        var enriched = AttachLiveScreen(entry);
        return AppendContextualEntry(enriched);
    }

    public void SetSystemInstruction(string instruction) {
        SystemInstruction = instruction;
        DebugUtil.Print("History", $"System instruction updated length={instruction.Length}");
    }

    public void UpdateMemoryNotebook(string? content)
        => _memoryNotebookWidget.ReplaceNotebookFromHost(content);

    public void Reset() {
        _history.Clear();
        _memoryNotebookWidget.Reset();
        DebugUtil.Print("History", "AgentState history cleared");
    }

    public IReadOnlyList<IContextMessage> RenderLiveContext() {
        var messages = new List<IContextMessage>(_history.Count + 1);
        var detailOrdinal = 0;

        for (var index = _history.Count - 1; index >= 0; index--) {
            if (_history[index] is not ContextualHistoryEntry contextual) { continue; }

            switch (contextual) {
                case ModelInputEntry modelInputEntry:
                    var inputDetail = ResolveDetailLevel(detailOrdinal++);
                    messages.Add(new ModelInputMessage(modelInputEntry, inputDetail));
                    break;

                case ToolResultsEntry toolResultsEntry:
                    var toolDetail = ResolveDetailLevel(detailOrdinal++);
                    messages.Add(new ToolResultsMessage(toolResultsEntry, toolDetail));
                    break;

                default:
                    messages.Add(contextual);
                    break;
            }
        }

        var systemMessage = new SystemInstructionMessage(SystemInstruction) {
            Timestamp = _timestampProvider(),
            Metadata = ImmutableDictionary<string, object?>.Empty
        };

        messages.Add(systemMessage);
        messages.Reverse();
        return messages;
    }

    private T AppendContextualEntry<T>(T entry) where T : ContextualHistoryEntry {
        var finalized = entry with {
            Timestamp = _timestampProvider(),
            Metadata = entry.Metadata
        };

        _history.Add(finalized);
        DebugUtil.Print("History", $"Appended {finalized.Role} entry (count={_history.Count})");
        return finalized;
    }

    private static LevelOfDetail ResolveDetailLevel(int ordinal)
        => ordinal == 0
            ? LevelOfDetail.Live
            : LevelOfDetail.Summary;

    private ModelInputEntry AttachLiveScreen(ModelInputEntry entry) {
        var liveScreen = BuildLiveScreenSnapshot();
        if (string.IsNullOrWhiteSpace(liveScreen)) { return entry; }

        var sections = entry.ContentSections.WithFullSection(LevelOfDetailSectionNames.LiveScreen, liveScreen);
        if (ReferenceEquals(sections, entry.ContentSections)) { return entry; }

        return entry with { ContentSections = sections };
    }

    private ToolResultsEntry AttachLiveScreen(ToolResultsEntry entry) {
        if (entry.Results.Count == 0) { return entry; }

        var liveScreen = BuildLiveScreenSnapshot();
        if (string.IsNullOrWhiteSpace(liveScreen)) { return entry; }

        var results = new HistoryToolCallResult[entry.Results.Count];
        for (var index = 0; index < entry.Results.Count; index++) {
            results[index] = entry.Results[index];
        }

        var latest = results[^1];
        var updatedSections = latest.Result.WithFullSection(LevelOfDetailSectionNames.LiveScreen, liveScreen);
        if (ReferenceEquals(updatedSections, latest.Result)) { return entry; }

        results[^1] = latest with { Result = updatedSections };
        return entry with { Results = results };
    }

    private string? BuildLiveScreenSnapshot() {
        var fragments = new List<string>();
        var renderContext = new WidgetRenderContext(this, ImmutableDictionary<string, object?>.Empty);

        foreach (var widget in _widgets) {
            var fragment = widget.RenderLiveScreen(renderContext);
            if (!string.IsNullOrWhiteSpace(fragment)) {
                fragments.Add(fragment.TrimEnd());
            }
        }

        if (fragments.Count == 0) { return null; }

        var liveScreenBuilder = new StringBuilder();
        liveScreenBuilder.AppendLine("# [Live Screen]");
        liveScreenBuilder.AppendLine();

        for (var index = 0; index < fragments.Count; index++) {
            liveScreenBuilder.AppendLine(fragments[index]);

            if (index < fragments.Count - 1) {
                liveScreenBuilder.AppendLine();
            }
        }

        return liveScreenBuilder.ToString().TrimEnd();
    }

    internal IEnumerable<ITool> EnumerateWidgetTools() {
        foreach (var widget in _widgets) {
            foreach (var tool in widget.Tools) {
                yield return tool;
            }
        }
    }
}
