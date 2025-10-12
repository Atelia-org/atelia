using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Atelia.Diagnostics;
using Atelia.LiveContextProto.State.History;

namespace Atelia.LiveContextProto.State;

internal sealed class AgentState {
    private readonly List<HistoryEntry> _history = new();
    private readonly Func<DateTimeOffset> _timestampProvider;

    private AgentState(Func<DateTimeOffset> timestampProvider, string systemInstruction) {
        _timestampProvider = timestampProvider;
        SystemInstruction = systemInstruction;
        DebugUtil.Print("History", $"AgentState initialized with instruction length={systemInstruction.Length}");
    }

    public string SystemInstruction { get; private set; }

    public IReadOnlyList<HistoryEntry> History => _history;

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

    public void Reset() {
        _history.Clear();
        DebugUtil.Print("History", "AgentState history cleared");
    }

    public IReadOnlyList<IContextMessage> RenderLiveContext() {
        var messages = new List<IContextMessage>(_history.Count + 1);

        foreach (var entry in _history) {
            if (entry is ContextualHistoryEntry contextual) {
                messages.Add(contextual);
            }
        }

        var systemMessage = new SystemInstructionMessage(SystemInstruction) {
            Timestamp = _timestampProvider(),
            Metadata = ImmutableDictionary<string, object?>.Empty
        };

        messages.Insert(0, systemMessage);
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
}
