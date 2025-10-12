using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using Atelia.Diagnostics;
using Atelia.LiveContextProto.State.History;

namespace Atelia.LiveContextProto.State;

internal sealed class AgentState {
    private readonly List<HistoryEntry> _history = new();
    private readonly Func<DateTimeOffset> _timestampProvider;
    private readonly Dictionary<string, string> _liveInfoSections = new(StringComparer.OrdinalIgnoreCase);
    private string? _memoryNotebook;

    private const string DefaultMemoryNotebookSnapshot = "（暂无 Memory Notebook 内容）";

    private AgentState(Func<DateTimeOffset> timestampProvider, string systemInstruction) {
        _timestampProvider = timestampProvider;
        SystemInstruction = systemInstruction;
        DebugUtil.Print("History", $"AgentState initialized with instruction length={systemInstruction.Length}");
    }

    public string SystemInstruction { get; private set; }

    public IReadOnlyList<HistoryEntry> History => _history;

    public string MemoryNotebookSnapshot => _memoryNotebook is null
        ? DefaultMemoryNotebookSnapshot
        : _memoryNotebook;

    public IReadOnlyDictionary<string, string> LiveInfoSections
        => new ReadOnlyDictionary<string, string>(_liveInfoSections);

    public static AgentState CreateDefault(string? systemInstruction = null, Func<DateTimeOffset>? timestampProvider = null) {
        var instruction = string.IsNullOrWhiteSpace(systemInstruction)
            ? "You are LiveContextProto, a placeholder agent validating the Conversation History refactor skeleton."
            : systemInstruction;

        var provider = timestampProvider ?? (() => DateTimeOffset.UtcNow);
        return new AgentState(provider, instruction);
    }

    public ModelInputEntry AppendModelInput(ModelInputEntry entry) {
        if (entry.ContentSections is not { Count: > 0 }) { throw new ArgumentException("ContentSections must contain at least one section.", nameof(entry)); }

        return AppendContextualEntry(entry);
    }

    public ModelOutputEntry AppendModelOutput(ModelOutputEntry entry) {
        if ((entry.Contents is null || entry.Contents.Count == 0) && (entry.ToolCalls is null || entry.ToolCalls.Count == 0)) { throw new ArgumentException("ModelOutputEntry must include content or tool calls.", nameof(entry)); }

        return AppendContextualEntry(entry);
    }

    public ToolResultsEntry AppendToolResults(ToolResultsEntry entry) {
        if (entry.Results is not { Count: > 0 } && string.IsNullOrWhiteSpace(entry.ExecuteError)) { throw new ArgumentException("ToolResultsEntry must include results or an execution error.", nameof(entry)); }

        return AppendContextualEntry(entry);
    }

    public void SetSystemInstruction(string instruction) {
        SystemInstruction = instruction;
        DebugUtil.Print("History", $"System instruction updated length={instruction.Length}");
    }

    public void UpdateMemoryNotebook(string? content) {
        var sanitized = string.IsNullOrWhiteSpace(content)
            ? null
            : content.TrimEnd();

        _memoryNotebook = sanitized;
        DebugUtil.Print("History", $"Memory notebook updated length={(sanitized?.Length ?? 0)}");
    }

    public void UpdateLiveInfoSection(string sectionName, string? content) {
        if (string.IsNullOrWhiteSpace(sectionName)) { throw new ArgumentException("Section name is required.", nameof(sectionName)); }

        var key = sectionName.Trim();
        var sanitized = string.IsNullOrWhiteSpace(content)
            ? null
            : content.TrimEnd();

        if (sanitized is null) {
            if (_liveInfoSections.Remove(key)) {
                DebugUtil.Print("History", $"LiveInfo section removed name={key}");
            }
            else {
                DebugUtil.Print("History", $"LiveInfo section skip remove name={key}");
            }
            return;
        }

        _liveInfoSections[key] = sanitized;
        DebugUtil.Print("History", $"LiveInfo section updated name={key} length={sanitized.Length}");
    }

    public void Reset() {
        _history.Clear();
        _memoryNotebook = null;
        _liveInfoSections.Clear();
        DebugUtil.Print("History", "AgentState history cleared");
    }

    public IReadOnlyList<IContextMessage> RenderLiveContext() {
        var messages = new List<IContextMessage>(_history.Count + 1);
        var liveScreen = BuildLiveScreenSnapshot();
        var shouldDecorate = !string.IsNullOrWhiteSpace(liveScreen);
        var liveScreenInjected = false;

        for (var index = _history.Count - 1; index >= 0; index--) {
            if (_history[index] is not ContextualHistoryEntry contextual) { continue; }

            IContextMessage message = contextual;

            if (!liveScreenInjected && shouldDecorate && ShouldDecorateWithLiveScreen(contextual)) {
                message = ContextMessageLiveScreenHelper.AttachLiveScreen(contextual, liveScreen);
                liveScreenInjected = true;
            }

            messages.Add(message);
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

    private static bool ShouldDecorateWithLiveScreen(ContextualHistoryEntry entry)
        => entry.Role is ContextMessageRole.ModelInput or ContextMessageRole.ToolResult;

    private string? BuildLiveScreenSnapshot() {
        var sections = new List<(string Title, string Content)>();

        if (!string.IsNullOrWhiteSpace(_memoryNotebook)) {
            sections.Add(("Memory Notebook", _memoryNotebook!));
        }

        foreach (var pair in _liveInfoSections.OrderBy(static entry => entry.Key, StringComparer.OrdinalIgnoreCase)) {
            if (!string.IsNullOrWhiteSpace(pair.Value)) {
                sections.Add((pair.Key, pair.Value));
            }
        }

        if (sections.Count == 0) { return null; }

        var builder = new StringBuilder();
        builder.AppendLine("# [Live Screen]");

        for (var index = 0; index < sections.Count; index++) {
            var (title, content) = sections[index];
            builder.AppendLine($"## [{title}]");
            builder.AppendLine();
            builder.AppendLine(content);

            if (index < sections.Count - 1) {
                builder.AppendLine();
            }
        }

        return builder.ToString().TrimEnd();
    }
}
