using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;

namespace Atelia.ChatSession;

public static class ChatSessionLegacyEventSourceSchema {
    public const string SchemaId = "atelia.chat-session.legacy-upgrade-export.v1";
}

public static class ChatSessionLegacyEventKinds {
    public const string InitialState = "initial-state";
    public const string ModelTurn = "model-turn";
    public const string Compaction = "compaction";
    public const string UpdateSystemPrompt = "update-system-prompt";
    public const string RedundantSave = "redundant-save";
}

public enum ChatSessionLegacyReplayMode {
    RespectOriginalCompaction,
    IgnoreOriginalCompaction
}

public sealed record ChatSessionLegacyEventSource {
    public string? Schema { get; init; }
    public string? BranchName { get; init; }
    public IReadOnlyList<ChatSessionLegacyReplayEvent> Events { get; init; } = Array.Empty<ChatSessionLegacyReplayEvent>();
}

public sealed record ChatSessionLegacyReplayEvent {
    public int Ordinal { get; init; }
    public string? Commit { get; init; }
    public string Kind { get; init; } = string.Empty;
    public ChatSessionLegacyCommitMetadataDto? CommitMetadata { get; init; }
    public ChatSessionLegacyRootMetadataDto? Root { get; init; }
    public IReadOnlyList<ChatSessionLegacyMessageDto>? Messages { get; init; }
    public IReadOnlyList<ChatSessionLegacyMessageDto>? AppendedMessages { get; init; }
    public ChatSessionLegacySourceRangeDto? SourceRange { get; init; }
    public int? RecapIndex { get; init; }
    public ChatSessionLegacyMessageDto? RecapMessage { get; init; }
    public ChatSessionLegacyRecapSourceAnchorDto? RecapSourceAnchor { get; init; }
    public ChatSessionLegacySystemPromptChangeDto? SystemPromptChange { get; init; }
}

public sealed record ChatSessionLegacyRootMetadataDto {
    public string? Kind { get; init; }
    public long? SchemaVersion { get; init; }
    public string? ApiSpecId { get; init; }
    public string? CompletionSurfaceId { get; init; }
    public string? ModelId { get; init; }
    public string? SystemPrompt { get; init; }
}

public sealed record ChatSessionLegacyCommitMetadataDto {
    public string? CommitKind { get; init; }
    public string? CommitReason { get; init; }
}

public sealed record ChatSessionLegacyMessageDto {
    public string Kind { get; init; } = string.Empty;
    public string? TimestampUtc { get; init; }
    public string? Content { get; init; }
    public ChatSessionLegacyActionMessageDto? Action { get; init; }
    public ChatSessionLegacyToolResultsMessageDto? ToolResults { get; init; }
    public ChatSessionLegacyContextHeaderDto? ContextHeader { get; init; }
    public ChatSessionLegacyRecapSourceAnchorDto? RecapSourceAnchor { get; init; }
}

public sealed record ChatSessionLegacyActionMessageDto {
    public string? FlattenedText { get; init; }
    public IReadOnlyList<SerializedActionBlock> Blocks { get; init; } = Array.Empty<SerializedActionBlock>();
}

public sealed record ChatSessionLegacyToolResultsMessageDto {
    public IReadOnlyList<ChatSessionLegacyToolResultDto> Results { get; init; } = Array.Empty<ChatSessionLegacyToolResultDto>();
}

public sealed record ChatSessionLegacyToolResultDto {
    public string? ToolName { get; init; }
    public string? ToolCallId { get; init; }
    public string? Status { get; init; }
    public IReadOnlyList<ChatSessionLegacyToolResultBlockDto> Blocks { get; init; } = Array.Empty<ChatSessionLegacyToolResultBlockDto>();
}

public sealed record ChatSessionLegacyToolResultBlockDto {
    public string? Kind { get; init; }
    public string? Content { get; init; }
}

public sealed record ChatSessionLegacyContextHeaderDto {
    public string? SystemPromptFragment { get; init; }
    public string? UserMessage { get; init; }
    public ChatSessionLegacyActionMessageDto? AssistantMessage { get; init; }
}

public sealed record ChatSessionLegacyRecapSourceAnchorDto {
    public string? SourceHeadBeforeCompaction { get; init; }
    public string? SourceBranchName { get; init; }
    public int SourceStartIndex { get; init; }
    public int SourceEndExclusive { get; init; }
    public int SourceMessageCountBefore { get; init; }
    public string? CompactionKind { get; init; }
}

public sealed record ChatSessionLegacySourceRangeDto {
    public int StartIndex { get; init; }
    public int EndExclusive { get; init; }
    public int MessageCountBefore { get; init; }
}

public sealed record ChatSessionLegacySystemPromptChangeDto {
    public string? OldSystemPrompt { get; init; }
    public string? NewSystemPrompt { get; init; }
}

public static class ChatSessionLegacyEventSourceReader {
    public static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static ChatSessionLegacyEventSource Read(string inputJsonPath) {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputJsonPath);
        using var stream = File.OpenRead(inputJsonPath);
        var eventSource = JsonSerializer.Deserialize<ChatSessionLegacyEventSource>(stream, JsonOptions)
                          ?? throw new InvalidDataException("Event source JSON is empty.");
        if (!string.Equals(eventSource.Schema, ChatSessionLegacyEventSourceSchema.SchemaId, StringComparison.Ordinal)) { throw new InvalidDataException($"Unsupported event source schema '{eventSource.Schema}'."); }

        if (eventSource.Events.Count == 0) { throw new InvalidDataException("Event source has no events."); }
        return eventSource;
    }
}

public static class ChatSessionLegacyEventSourceProjection {
    public static IHistoryMessage ToHistoryMessage(ChatSessionLegacyMessageDto message)
        => message.Kind switch {
            MessageRecord.KindObservation => new ObservationMessage(message.Content),
            MessageRecord.KindAction => ToActionMessage(message.Action),
            MessageRecord.KindRecap => new RecapMessage(message.Content, ToRecapSourceAnchor(message.RecapSourceAnchor)),
            MessageRecord.KindToolResults => ToToolResultsMessage(message),
            MessageRecord.KindContextHeader => ToContextHeader(message.ContextHeader),
            _ => throw new InvalidDataException($"Unsupported message kind '{message.Kind}'.")
        };

    public static DateTimeOffset? ParseTimestamp(string? text)
        => string.IsNullOrWhiteSpace(text) ? null : DateTimeOffset.Parse(text, null, System.Globalization.DateTimeStyles.RoundtripKind);

    public static string BuildCommitNote(ChatSessionLegacyReplayEvent replayEvent) {
        if (!ChatSessionCommitMetadata.TryParseKind(replayEvent.Kind, out var kind)) { throw new InvalidDataException($"Unsupported commit kind '{replayEvent.Kind}'."); }
        return ChatSessionCommitMetadata.EncodeNote(kind, replayEvent.CommitMetadata?.CommitReason ?? replayEvent.Kind);
    }

    private static ActionMessage ToActionMessage(ChatSessionLegacyActionMessageDto? action) {
        if (action is null) { return new ActionMessage(Array.Empty<ActionBlock>()); }
        var json = JsonSerializer.Serialize(action.Blocks ?? Array.Empty<SerializedActionBlock>(), ChatSessionLegacyEventSourceReader.JsonOptions);
        return new ActionMessage(ActionMessageSerialization.DeserializeBlocks(json, options: ChatSessionLegacyEventSourceReader.JsonOptions));
    }

    private static ToolResultsMessage ToToolResultsMessage(ChatSessionLegacyMessageDto message)
        => new(
            message.Content,
            (message.ToolResults?.Results ?? Array.Empty<ChatSessionLegacyToolResultDto>()).Select(ToToolResult).ToArray()
        );

    private static ToolResult ToToolResult(ChatSessionLegacyToolResultDto dto)
        => new(
            dto.ToolName ?? string.Empty,
            dto.ToolCallId ?? string.Empty,
            ParseToolExecutionStatus(dto.Status),
            (dto.Blocks ?? Array.Empty<ChatSessionLegacyToolResultBlockDto>()).Select(ToToolResultBlock).ToArray()
        );

    private static ToolResultBlock ToToolResultBlock(ChatSessionLegacyToolResultBlockDto dto)
        => dto.Kind switch {
            "text" => new ToolResultBlock.Text(dto.Content ?? string.Empty),
            _ => new ToolResultBlock.Text(dto.Content ?? string.Empty)
        };

    private static ContextHeader ToContextHeader(ChatSessionLegacyContextHeaderDto? contextHeader)
        => contextHeader is null
            ? new ContextHeader(null, null, null)
            : new ContextHeader(
                contextHeader.SystemPromptFragment,
                contextHeader.UserMessage,
                contextHeader.AssistantMessage is null ? null : ToActionMessage(contextHeader.AssistantMessage)
            );

    private static RecapSourceAnchor? ToRecapSourceAnchor(ChatSessionLegacyRecapSourceAnchorDto? anchor)
        => anchor is null
            ? null
            : new RecapSourceAnchor(
                anchor.SourceHeadBeforeCompaction ?? string.Empty,
                anchor.SourceBranchName ?? string.Empty,
                anchor.SourceStartIndex,
                anchor.SourceEndExclusive,
                anchor.SourceMessageCountBefore,
                anchor.CompactionKind ?? MessageRecord.CompactionKindPrefixSummary
            );

    private static ToolExecutionStatus ParseToolExecutionStatus(string? text)
        => text switch {
            "success" => ToolExecutionStatus.Success,
            "failed" => ToolExecutionStatus.Failed,
            "skipped" => ToolExecutionStatus.Skipped,
            _ => Enum.TryParse<ToolExecutionStatus>(text, ignoreCase: true, out var status) ? status : ToolExecutionStatus.Failed
        };
}

public sealed class ChatSessionLegacyReplayCursor {
    private readonly ChatSessionLegacyEventSource _eventSource;
    private readonly ChatSessionLegacyReplayMode _mode;
    private readonly List<ChatSessionLegacyMessageDto> _currentMessages = [];

    public ChatSessionLegacyReplayCursor(
        ChatSessionLegacyEventSource eventSource,
        ChatSessionLegacyReplayMode mode = ChatSessionLegacyReplayMode.RespectOriginalCompaction
    ) {
        _eventSource = eventSource ?? throw new ArgumentNullException(nameof(eventSource));
        _mode = mode;
    }

    public int Position { get; private set; }
    public ChatSessionLegacyRootMetadataDto? Root { get; private set; }
    public IReadOnlyList<ChatSessionLegacyMessageDto> CurrentMessageDtos => _currentMessages.AsReadOnly();
    public IReadOnlyList<IHistoryMessage> CurrentHistoryMessages => _currentMessages
        .Select(ChatSessionLegacyEventSourceProjection.ToHistoryMessage)
        .ToArray();

    public bool TryStep(out ChatSessionLegacyReplayStep step) {
        if (Position >= _eventSource.Events.Count) {
            step = default!;
            return false;
        }

        var replayEvent = _eventSource.Events[Position];
        if (replayEvent.Ordinal != Position) { throw new InvalidDataException($"Event ordinal mismatch at index {Position}: {replayEvent.Ordinal}."); }

        bool applied = ApplyEvent(replayEvent);
        step = new ChatSessionLegacyReplayStep(
            Event: replayEvent,
            Applied: applied,
            MessageCount: _currentMessages.Count
        );
        Position++;
        return true;
    }

    private bool ApplyEvent(ChatSessionLegacyReplayEvent replayEvent) {
        switch (replayEvent.Kind) {
            case ChatSessionLegacyEventKinds.InitialState:
                ApplyInitialState(replayEvent);
                return true;
            case ChatSessionLegacyEventKinds.ModelTurn:
                RequireMessages(replayEvent.AppendedMessages, replayEvent.Kind).ForEach(_currentMessages.Add);
                return true;
            case ChatSessionLegacyEventKinds.Compaction:
                if (_mode == ChatSessionLegacyReplayMode.IgnoreOriginalCompaction) { return false; }

                ApplyCompaction(replayEvent);
                return true;
            case ChatSessionLegacyEventKinds.UpdateSystemPrompt:
                ApplySystemPromptChange(replayEvent);
                return true;
            case ChatSessionLegacyEventKinds.RedundantSave:
                return false;
            default:
                throw new NotSupportedException($"Event kind '{replayEvent.Kind}' is not supported by the replay cursor.");
        }
    }

    private void ApplyInitialState(ChatSessionLegacyReplayEvent replayEvent) {
        Root = replayEvent.Root ?? throw new InvalidDataException("initial-state event is missing root metadata.");
        _currentMessages.Clear();
        _currentMessages.AddRange(replayEvent.Messages ?? Array.Empty<ChatSessionLegacyMessageDto>());
    }

    private void ApplyCompaction(ChatSessionLegacyReplayEvent replayEvent) {
        var range = replayEvent.SourceRange ?? throw new InvalidDataException("compaction event is missing sourceRange.");
        var recapMessage = replayEvent.RecapMessage ?? throw new InvalidDataException("compaction event is missing recapMessage.");
        if (replayEvent.RecapSourceAnchor is not null) { recapMessage = recapMessage with { RecapSourceAnchor = replayEvent.RecapSourceAnchor }; }

        if (range.StartIndex < 0 || range.EndExclusive < range.StartIndex || range.EndExclusive > _currentMessages.Count) { throw new InvalidDataException($"Invalid compaction source range [{range.StartIndex}, {range.EndExclusive}) for current message count {_currentMessages.Count}."); }

        var recapIndex = replayEvent.RecapIndex ?? 0;
        if (recapIndex < 0 || recapIndex > range.EndExclusive - range.StartIndex) { throw new InvalidDataException($"Invalid compaction recapIndex {recapIndex}."); }

        var prefix = _currentMessages.Take(range.StartIndex + recapIndex).ToArray();
        var suffix = _currentMessages.Skip(range.EndExclusive).ToArray();
        _currentMessages.Clear();
        _currentMessages.AddRange(prefix);
        _currentMessages.Add(recapMessage);
        _currentMessages.AddRange(suffix);
    }

    private void ApplySystemPromptChange(ChatSessionLegacyReplayEvent replayEvent) {
        var change = replayEvent.SystemPromptChange ?? throw new InvalidDataException("update-system-prompt event is missing systemPromptChange.");
        Root = (Root ?? new ChatSessionLegacyRootMetadataDto()) with { SystemPrompt = change.NewSystemPrompt ?? string.Empty };
    }

    private static List<ChatSessionLegacyMessageDto> RequireMessages(IReadOnlyList<ChatSessionLegacyMessageDto>? messages, string eventKind)
        => messages is null ? throw new InvalidDataException($"{eventKind} event is missing messages.") : messages.ToList();
}

public sealed record ChatSessionLegacyReplayStep(
    ChatSessionLegacyReplayEvent Event,
    bool Applied,
    int MessageCount
);
