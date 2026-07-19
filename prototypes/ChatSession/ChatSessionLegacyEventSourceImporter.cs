using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using Atelia.StateJournal;

namespace Atelia.ChatSession;

public sealed record ChatSessionLegacyEventSourceImportResult(
    string OutputRepoDir,
    string BranchName,
    int EventCount,
    CommitAddress? HeadAddress
);

public static class ChatSessionLegacyEventSourceImporter {
    private const string SchemaId = "atelia.chat-session.legacy-upgrade-export.v1";

    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static ChatSessionLegacyEventSourceImportResult Import(
        string inputJsonPath,
        string outputRepoDir,
        string branchName = "main"
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputJsonPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRepoDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);

        if (Directory.Exists(outputRepoDir)) { throw new IOException($"Output repo directory already exists: {outputRepoDir}"); }

        var eventSource = ReadEventSource(inputJsonPath);
        if (!string.Equals(eventSource.Schema, SchemaId, StringComparison.Ordinal)) { throw new InvalidDataException($"Unsupported event source schema '{eventSource.Schema}'."); }
        if (eventSource.Events.Count == 0) { throw new InvalidDataException("Event source has no events."); }

        using var repo = Repository.Create(outputRepoDir).Unwrap();
        var revision = repo.CreateBranch(branchName).Unwrap();
        var root = revision.CreateDict<string>();
        var messages = revision.CreateDeque();
        root.Upsert<DurableObject>(ChatSessionStorageSchema.KeyMessages, messages);

        var state = new ReplayState(root, messages, new List<MessageDto>());
        for (int eventIndex = 0; eventIndex < eventSource.Events.Count; eventIndex++) {
            var replayEvent = eventSource.Events[eventIndex];
            if (replayEvent.Ordinal != eventIndex) { throw new InvalidDataException($"Event ordinal mismatch at index {eventIndex}: {replayEvent.Ordinal}."); }

            ApplyEvent(state, replayEvent);
            RewriteMessages(state.Messages, state.CurrentMessages);
            repo.Commit(state.Root, BuildCommitNote(replayEvent)).Unwrap();
        }

        repo.TryGetBranchHeadAddress(branchName, out var head);
        return new ChatSessionLegacyEventSourceImportResult(outputRepoDir, branchName, eventSource.Events.Count, head);
    }

    private static UpgradeExportDto ReadEventSource(string inputJsonPath) {
        using var stream = File.OpenRead(inputJsonPath);
        return JsonSerializer.Deserialize<UpgradeExportDto>(stream, JsonOptions)
               ?? throw new InvalidDataException("Event source JSON is empty.");
    }

    private static void ApplyEvent(ReplayState state, EventDto replayEvent) {
        switch (replayEvent.Kind) {
            case "initial-state":
                ApplyInitialState(state, replayEvent);
                break;
            case "model-turn":
                RequireMessages(replayEvent.AppendedMessages, replayEvent.Kind).ForEach(state.CurrentMessages.Add);
                break;
            case "compaction":
                ApplyCompaction(state, replayEvent);
                break;
            case "update-system-prompt":
                ApplySystemPromptChange(state, replayEvent);
                break;
            case "redundant-save":
                break;
            default:
                throw new NotSupportedException($"Event kind '{replayEvent.Kind}' is not supported by the first importer implementation.");
        }
    }

    private static void ApplyInitialState(ReplayState state, EventDto replayEvent) {
        var root = replayEvent.Root ?? throw new InvalidDataException("initial-state event is missing root metadata.");
        state.Root.Upsert(ChatSessionStorageSchema.KeyKind, root.Kind ?? ChatSessionStorageSchema.RootKind);
        state.Root.Upsert(ChatSessionStorageSchema.KeySchemaVersion, root.SchemaVersion ?? ChatSessionStorageSchema.SchemaVersion);
        if (root.ApiSpecId is not null) { state.Root.Upsert(ChatSessionStorageSchema.KeyApiSpecId, root.ApiSpecId); }
        if (root.CompletionSurfaceId is not null) { state.Root.Upsert(ChatSessionStorageSchema.KeyCompletionSurfaceId, root.CompletionSurfaceId); }
        if (root.ModelId is not null) { state.Root.Upsert(ChatSessionStorageSchema.KeyModelId, root.ModelId); }
        state.Root.Upsert(ChatSessionStorageSchema.KeySystemPrompt, root.SystemPrompt ?? string.Empty);

        state.CurrentMessages.Clear();
        state.CurrentMessages.AddRange(replayEvent.Messages ?? Array.Empty<MessageDto>());
    }

    private static void ApplyCompaction(ReplayState state, EventDto replayEvent) {
        var range = replayEvent.SourceRange ?? throw new InvalidDataException("compaction event is missing sourceRange.");
        var recapMessage = replayEvent.RecapMessage ?? throw new InvalidDataException("compaction event is missing recapMessage.");
        if (replayEvent.RecapSourceAnchor is not null) { recapMessage = recapMessage with { RecapSourceAnchor = replayEvent.RecapSourceAnchor }; }

        if (range.StartIndex < 0 || range.EndExclusive < range.StartIndex || range.EndExclusive > state.CurrentMessages.Count) { throw new InvalidDataException($"Invalid compaction source range [{range.StartIndex}, {range.EndExclusive}) for current message count {state.CurrentMessages.Count}."); }

        var recapIndex = replayEvent.RecapIndex ?? 0;
        if (recapIndex < 0 || recapIndex > range.EndExclusive - range.StartIndex) { throw new InvalidDataException($"Invalid compaction recapIndex {recapIndex}."); }

        var prefix = state.CurrentMessages.Take(range.StartIndex + recapIndex).ToArray();
        var suffix = state.CurrentMessages.Skip(range.EndExclusive).ToArray();
        state.CurrentMessages.Clear();
        state.CurrentMessages.AddRange(prefix);
        state.CurrentMessages.Add(recapMessage);
        state.CurrentMessages.AddRange(suffix);
    }

    private static void ApplySystemPromptChange(ReplayState state, EventDto replayEvent) {
        var change = replayEvent.SystemPromptChange ?? throw new InvalidDataException("update-system-prompt event is missing systemPromptChange.");
        state.Root.Upsert(ChatSessionStorageSchema.KeySystemPrompt, change.NewSystemPrompt ?? string.Empty);
    }

    private static List<MessageDto> RequireMessages(IReadOnlyList<MessageDto>? messages, string eventKind)
        => messages is null ? throw new InvalidDataException($"{eventKind} event is missing messages.") : messages.ToList();

    private static void RewriteMessages(DurableDeque messages, IReadOnlyList<MessageDto> currentMessages) {
        while (messages.Count > 0) { messages.PopBack<DurableObject>(out _); }
        for (int messageIndex = 0; messageIndex < currentMessages.Count; messageIndex++) {
            var message = currentMessages[messageIndex];
            MessageRecord.AppendHistoryMessage(messages, ToHistoryMessage(message), ParseTimestamp(message.TimestampUtc));
        }
    }

    private static IHistoryMessage ToHistoryMessage(MessageDto message)
        => message.Kind switch {
            MessageRecord.KindObservation => new ObservationMessage(message.Content),
            MessageRecord.KindAction => ToActionMessage(message.Action),
            MessageRecord.KindRecap => new RecapMessage(message.Content, ToRecapSourceAnchor(message.RecapSourceAnchor)),
            MessageRecord.KindToolResults => ToToolResultsMessage(message),
            MessageRecord.KindContextHeader => ToContextHeader(message.ContextHeader),
            _ => throw new InvalidDataException($"Unsupported message kind '{message.Kind}'.")
        };

    private static ActionMessage ToActionMessage(ActionMessageDto? action) {
        if (action is null) { return new ActionMessage(Array.Empty<ActionBlock>()); }
        var json = JsonSerializer.Serialize(action.Blocks ?? Array.Empty<SerializedActionBlock>(), JsonOptions);
        return new ActionMessage(ActionMessageSerialization.DeserializeBlocks(json, options: JsonOptions));
    }

    private static ToolResultsMessage ToToolResultsMessage(MessageDto message)
        => new(
            message.Content,
            (message.ToolResults?.Results ?? Array.Empty<ToolResultDto>()).Select(ToToolResult).ToArray()
        );

    private static ToolResult ToToolResult(ToolResultDto dto)
        => new(
            dto.ToolName ?? string.Empty,
            dto.ToolCallId ?? string.Empty,
            ParseToolExecutionStatus(dto.Status),
            (dto.Blocks ?? Array.Empty<ToolResultBlockDto>()).Select(ToToolResultBlock).ToArray()
        );

    private static ToolResultBlock ToToolResultBlock(ToolResultBlockDto dto)
        => dto.Kind switch {
            "text" => new ToolResultBlock.Text(dto.Content ?? string.Empty),
            _ => new ToolResultBlock.Text(dto.Content ?? string.Empty)
        };

    private static ContextHeader ToContextHeader(ContextHeaderDto? contextHeader)
        => contextHeader is null
            ? new ContextHeader(null, null, null)
            : new ContextHeader(
                contextHeader.SystemPromptFragment,
                contextHeader.UserMessage,
                contextHeader.AssistantMessage is null ? null : ToActionMessage(contextHeader.AssistantMessage)
            );

    private static RecapSourceAnchor? ToRecapSourceAnchor(RecapSourceAnchorDto? anchor)
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

    private static DateTimeOffset? ParseTimestamp(string? text)
        => string.IsNullOrWhiteSpace(text) ? null : DateTimeOffset.Parse(text, null, System.Globalization.DateTimeStyles.RoundtripKind);

    private static ToolExecutionStatus ParseToolExecutionStatus(string? text)
        => text switch {
            "success" => ToolExecutionStatus.Success,
            "failed" => ToolExecutionStatus.Failed,
            "skipped" => ToolExecutionStatus.Skipped,
            _ => Enum.TryParse<ToolExecutionStatus>(text, ignoreCase: true, out var status) ? status : ToolExecutionStatus.Failed
        };

    private static string BuildCommitNote(EventDto replayEvent) {
        if (!ChatSessionCommitMetadata.TryParseKind(replayEvent.Kind, out var kind)) { throw new InvalidDataException($"Unsupported commit kind '{replayEvent.Kind}'."); }
        return ChatSessionCommitMetadata.EncodeNote(kind, replayEvent.CommitMetadata?.CommitReason ?? replayEvent.Kind);
    }

    private sealed record ReplayState(
        DurableDict<string> Root,
        DurableDeque Messages,
        List<MessageDto> CurrentMessages
    );

    private sealed record UpgradeExportDto {
        public string? Schema { get; init; }
        public IReadOnlyList<EventDto> Events { get; init; } = Array.Empty<EventDto>();
    }

    private sealed record EventDto {
        public int Ordinal { get; init; }
        public string? Commit { get; init; }
        public string Kind { get; init; } = string.Empty;
        public CommitMetadataDto? CommitMetadata { get; init; }
        public RootMetadataDto? Root { get; init; }
        public IReadOnlyList<MessageDto>? Messages { get; init; }
        public IReadOnlyList<MessageDto>? AppendedMessages { get; init; }
        public SourceRangeDto? SourceRange { get; init; }
        public int? RecapIndex { get; init; }
        public MessageDto? RecapMessage { get; init; }
        public RecapSourceAnchorDto? RecapSourceAnchor { get; init; }
        public SystemPromptChangeDto? SystemPromptChange { get; init; }
    }

    private sealed record RootMetadataDto {
        public string? Kind { get; init; }
        public long? SchemaVersion { get; init; }
        public string? ApiSpecId { get; init; }
        public string? CompletionSurfaceId { get; init; }
        public string? ModelId { get; init; }
        public string? SystemPrompt { get; init; }
    }

    private sealed record CommitMetadataDto {
        public string? CommitKind { get; init; }
        public string? CommitReason { get; init; }
    }

    private sealed record MessageDto {
        public string Kind { get; init; } = string.Empty;
        public string? TimestampUtc { get; init; }
        public string? Content { get; init; }
        public ActionMessageDto? Action { get; init; }
        public ToolResultsMessageDto? ToolResults { get; init; }
        public ContextHeaderDto? ContextHeader { get; init; }
        public RecapSourceAnchorDto? RecapSourceAnchor { get; init; }
    }

    private sealed record ActionMessageDto {
        public string? FlattenedText { get; init; }
        public IReadOnlyList<SerializedActionBlock> Blocks { get; init; } = Array.Empty<SerializedActionBlock>();
    }

    private sealed record ToolResultsMessageDto {
        public IReadOnlyList<ToolResultDto> Results { get; init; } = Array.Empty<ToolResultDto>();
    }

    private sealed record ToolResultDto {
        public string? ToolName { get; init; }
        public string? ToolCallId { get; init; }
        public string? Status { get; init; }
        public IReadOnlyList<ToolResultBlockDto> Blocks { get; init; } = Array.Empty<ToolResultBlockDto>();
    }

    private sealed record ToolResultBlockDto {
        public string? Kind { get; init; }
        public string? Content { get; init; }
    }

    private sealed record ContextHeaderDto {
        public string? SystemPromptFragment { get; init; }
        public string? UserMessage { get; init; }
        public ActionMessageDto? AssistantMessage { get; init; }
    }

    private sealed record RecapSourceAnchorDto {
        public string? SourceHeadBeforeCompaction { get; init; }
        public string? SourceBranchName { get; init; }
        public int SourceStartIndex { get; init; }
        public int SourceEndExclusive { get; init; }
        public int SourceMessageCountBefore { get; init; }
        public string? CompactionKind { get; init; }
    }

    private sealed record SourceRangeDto {
        public int StartIndex { get; init; }
        public int EndExclusive { get; init; }
        public int MessageCountBefore { get; init; }
    }

    private sealed record SystemPromptChangeDto {
        public string? OldSystemPrompt { get; init; }
        public string? NewSystemPrompt { get; init; }
    }
}
