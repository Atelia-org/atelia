using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using Atelia.StateJournal;

namespace Atelia.ChatSession;

internal static class MessageRecord {
    public const string KeyKind = "kind";
    public const string KeyContent = "content";
    public const string KeySystemPromptFragment = "systemPromptFragment";
    public const string KeyUserMessage = "userMessage";
    public const string KeyAssistantActionBlocksJson = "assistantActionBlocksJson";
    public const string KeyActionBlocksJson = "actionBlocksJson";
    public const string KeyResultsJson = "resultsJson";
    public const string KeyTimestampUtc = "timestampUtc";
    public const string KeySourceHeadBeforeCompaction = "sourceHeadBeforeCompaction";
    public const string KeySourceBranchName = "sourceBranchName";
    public const string KeySourceStartIndex = "sourceStartIndex";
    public const string KeySourceEndExclusive = "sourceEndExclusive";
    public const string KeySourceMessageCountBefore = "sourceMessageCountBefore";
    public const string KeyCompactionKind = "compactionKind";

    public const string KindContextHeader = "context-header";
    public const string KindObservation = "observation";
    public const string KindAction = "action";
    public const string KindToolResults = "tool-results";
    public const string KindRecap = "recap";

    public const string CompactionKindPrefixSummary = "prefix-summary";

    private const string BlockKindText = "text";
    private const string BlockKindToolCall = "tool-call";
    private const string BlockKindReasoningText = "reasoning-text";

    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static DurableDict<string> AppendObservation(DurableDeque messages, string? content) {
        var record = CreateRecord(messages.Revision, KindObservation);
        if (content is not null) { record.Upsert(KeyContent, content); }
        messages.PushBack<DurableObject>(record);
        return record;
    }

    public static DurableDict<string> AppendAction(DurableDeque messages, ActionMessage message) {
        var record = CreateRecord(messages.Revision, KindAction);
        record.Upsert(KeyActionBlocksJson, ActionMessageSerialization.SerializeBlocks(message.Blocks));
        messages.PushBack<DurableObject>(record);
        return record;
    }

    public static DurableDict<string> AppendToolResults(DurableDeque messages, ToolResultsMessage message) {
        var record = CreateRecord(messages.Revision, KindToolResults);
        if (!string.IsNullOrEmpty(message.Content)) { record.Upsert(KeyContent, message.Content); }
        var resultDtos = ResultsToDtos(message.Results);
        record.Upsert(KeyResultsJson, JsonSerializer.Serialize(resultDtos, JsonOptions));
        messages.PushBack<DurableObject>(record);
        return record;
    }

    public static DurableDict<string> PrependRecap(DurableDeque messages, string summary, RecapSourceAnchor? sourceAnchor = null) {
        var record = CreateRecord(messages.Revision, KindRecap);
        record.Upsert(KeyContent, summary);
        if (sourceAnchor is not null) {
            record.Upsert(KeySourceHeadBeforeCompaction, sourceAnchor.SourceHeadBeforeCompaction);
            record.Upsert(KeySourceBranchName, sourceAnchor.SourceBranchName);
            record.Upsert(KeySourceStartIndex, sourceAnchor.SourceStartIndex);
            record.Upsert(KeySourceEndExclusive, sourceAnchor.SourceEndExclusive);
            record.Upsert(KeySourceMessageCountBefore, sourceAnchor.SourceMessageCountBefore);
            record.Upsert(KeyCompactionKind, sourceAnchor.CompactionKind);
        }
        messages.PushFront<DurableObject>(record);
        return record;
    }

    public static DurableDict<string> PrependContextHeader(DurableDeque messages, ContextHeader header) {
        var record = CreateRecord(messages.Revision, KindContextHeader);
        if (!string.IsNullOrEmpty(header.SystemPromptFragment)) { record.Upsert(KeySystemPromptFragment, header.SystemPromptFragment); }
        if (!string.IsNullOrEmpty(header.UserMessage)) { record.Upsert(KeyUserMessage, header.UserMessage); }
        if (header.AssistantMessage is not null) { record.Upsert(KeyAssistantActionBlocksJson, ActionMessageSerialization.SerializeBlocks(header.AssistantMessage.Blocks)); }
        messages.PushFront<DurableObject>(record);
        return record;
    }

    public static IHistoryMessage ToHistoryMessage(DurableDict<string> record) {
        record.Get<string>(KeyKind, out var kind);
        return kind switch {
            KindContextHeader => BuildContextHeader(record),
            KindObservation => BuildObservation(record),
            KindAction => BuildAction(record),
            KindToolResults => BuildToolResults(record),
            KindRecap => BuildRecap(record),
            _ => throw new InvalidDataException($"Unknown message kind: {kind}")
        };
    }

    public static List<IHistoryMessage> ToHistoryMessages(DurableDeque messages) {
        var result = new List<IHistoryMessage>(messages.Count);
        for (int i = 0; i < messages.Count; i++) {
            if (messages.TryGetAt<DurableDict<string>>(i, out var record) && record is not null) {
                result.Add(ToHistoryMessage(record));
            }
        }
        return result;
    }

    private static DurableDict<string> CreateRecord(Revision revision, string kind) {
        var record = revision.CreateDict<string>();
        record.Upsert(KeyKind, kind);
        record.Upsert(KeyTimestampUtc, DateTimeOffset.UtcNow.Ticks);
        return record;
    }

    private static ObservationMessage BuildObservation(DurableDict<string> record) {
        record.TryGet<string>(KeyContent, out var content);
        return new ObservationMessage(content);
    }

    private static ContextHeader BuildContextHeader(DurableDict<string> record) {
        record.TryGet<string>(KeySystemPromptFragment, out var systemPromptFragment);
        record.TryGet<string>(KeyUserMessage, out var userMessage);

        ActionMessage? assistantMessage = null;
        if (record.TryGet<string>(KeyAssistantActionBlocksJson, out var json) && !string.IsNullOrEmpty(json)) {
            assistantMessage = new ActionMessage(ActionMessageSerialization.DeserializeBlocks(json));
        }

        return new ContextHeader(systemPromptFragment, userMessage, assistantMessage);
    }

    private static ObservationMessage BuildRecap(DurableDict<string> record) {
        record.TryGet<string>(KeyContent, out var content);
        return new RecapMessage(content, TryBuildRecapSourceAnchor(record));
    }

    private static RecapSourceAnchor? TryBuildRecapSourceAnchor(DurableDict<string> record) {
        if (!record.TryGet<string>(KeySourceHeadBeforeCompaction, out var sourceHeadBeforeCompaction)
            || string.IsNullOrWhiteSpace(sourceHeadBeforeCompaction)) { return null; }

        if (!record.TryGet<string>(KeySourceBranchName, out var sourceBranchName)
            || string.IsNullOrWhiteSpace(sourceBranchName)) { return null; }

        if (!record.TryGet<int>(KeySourceStartIndex, out var sourceStartIndex)) { return null; }
        if (!record.TryGet<int>(KeySourceEndExclusive, out var sourceEndExclusive)) { return null; }
        if (!record.TryGet<int>(KeySourceMessageCountBefore, out var sourceMessageCountBefore)) { return null; }
        if (!record.TryGet<string>(KeyCompactionKind, out var compactionKind)
            || string.IsNullOrWhiteSpace(compactionKind)) { return null; }

        return new RecapSourceAnchor(
            sourceHeadBeforeCompaction,
            sourceBranchName,
            sourceStartIndex,
            sourceEndExclusive,
            sourceMessageCountBefore,
            compactionKind
        );
    }

    private static ActionMessage BuildAction(DurableDict<string> record) {
        if (!record.TryGet<string>(KeyActionBlocksJson, out var json) || string.IsNullOrEmpty(json)) { return new ActionMessage(Array.Empty<ActionBlock>()); }

        var blocks = ActionMessageSerialization.DeserializeBlocks(json);
        return new ActionMessage(blocks);
    }

    private static ToolResultsMessage BuildToolResults(DurableDict<string> record) {
        record.TryGet<string>(KeyContent, out var content);
        if (!record.TryGet<string>(KeyResultsJson, out var json) || string.IsNullOrEmpty(json)) { return new ToolResultsMessage(content, Array.Empty<ToolResult>()); }

        var dtos = JsonSerializer.Deserialize<ToolResultDto[]>(json, JsonOptions)
                   ?? Array.Empty<ToolResultDto>();
        var results = DtosToResults(dtos);
        return new ToolResultsMessage(content, results);
    }

    private static ToolResultDto[] ResultsToDtos(IReadOnlyList<ToolResult> results) {
        var dtos = new ToolResultDto[results.Count];
        for (int i = 0; i < results.Count; i++) {
            var r = results[i];
            dtos[i] = new ToolResultDto(r.ToolName, r.ToolCallId, r.Status, r.GetFlattenedText());
        }
        return dtos;
    }

    private static ToolResult[] DtosToResults(ToolResultDto[] dtos) {
        var results = new ToolResult[dtos.Length];
        for (int i = 0; i < dtos.Length; i++) {
            var dto = dtos[i];
            results[i] = ToolResult.FromText(dto.ToolName, dto.ToolCallId, dto.Status, dto.Content ?? string.Empty);
        }
        return results;
    }

    private sealed record ToolResultDto(
        string ToolName,
        string ToolCallId,
        ToolExecutionStatus Status,
        string? Content
    );
}
