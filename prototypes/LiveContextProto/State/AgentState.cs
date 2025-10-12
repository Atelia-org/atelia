using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using Atelia.Diagnostics;
using Atelia.LiveContextProto.State.History;

namespace Atelia.LiveContextProto.State;

internal sealed class AgentState {
    private readonly List<HistoryEntry> _history = new();
    private readonly Func<DateTimeOffset> _timestampProvider;
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

    public void Reset() {
        _history.Clear();
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
        if (string.IsNullOrWhiteSpace(_memoryNotebook)) { return null; }

        var builder = new StringBuilder();
        builder.AppendLine("# [Live Screen]");
        builder.AppendLine("## [Memory Notebook]");
        builder.AppendLine();
        builder.Append(_memoryNotebook);

        return builder.ToString();
    }
}
