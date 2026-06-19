using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Agent.Core.History;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.StateJournal;

namespace Atelia.Agent.Core.Persistence;

internal static class AgentWorkspaceRecordCodec {
    public const string KeyKind = "kind";
    public const string KeySystemPrompt = "systemPrompt";

    private const string KeyTimestampUtcTicks = "timestampUtcTicks";
    private const string KeySerial = "serial";
    private const string KeyTokenEstimate = "tokenEstimate";
    private const string KeyNotifications = "notifications";
    private const string KeyContent = "content";
    private const string KeyInsteadSerial = "insteadSerial";
    private const string KeyActionBlocksJson = "actionBlocksJson";
    private const string KeyInjectionBlockKind = "injectionBlockKind";
    private const string KeyInjectionSourceKind = "injectionSourceKind";
    private const string KeyInjectionSourceId = "injectionSourceId";
    private const string KeyInjectionSourceNotes = "injectionSourceNotes";
    private const string KeyResultsJson = "resultsJson";
    private const string KeyInvocationProviderId = "invocationProviderId";
    private const string KeyInvocationApiSpecId = "invocationApiSpecId";
    private const string KeyInvocationModel = "invocationModel";

    private const string KindObservation = "observation";
    private const string KindAction = "action";
    private const string KindInjection = "injection";
    private const string KindToolResults = "tool-results";
    private const string KindRecap = "recap";
    private const string KindPendingToolResult = "pending-tool-result";

    private const string BlockKindText = "text";
    private const string BlockKindToolCall = "tool-call";
    private const string BlockKindReasoning = "reasoning";

    private const string ToolResultBlockKindText = "text";

    private const string KeyResolvedProfile = "resolvedProfile";
    private const string KeyLockedCompactionSplitIndex = "lockedCompactionSplitIndex";

    private const string KeyProviderId = "providerId";
    private const string KeyApiSpecId = "apiSpecId";
    private const string KeyModelId = "modelId";
    private const string KeyName = "name";
    private const string KeySoftContextTokenCap = "softContextTokenCap";

    private const string KeySplitIndex = "splitIndex";
    private const string KeySummarizePrompt = "summarizePrompt";

    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static DurableDict<string> WriteHistoryEntry(Revision revision, HistoryEntry entry) {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(entry);

        var record = revision.CreateDict<string>();
        record.Upsert(KeyKind, GetHistoryKind(entry.Kind));
        record.Upsert(KeyTimestampUtcTicks, entry.Timestamp.UtcDateTime.Ticks);
        record.Upsert(KeySerial, entry.Serial);
        record.Upsert(KeyTokenEstimate, entry.TokenEstimate);

        switch (entry) {
            case ActionEntry actionEntry:
                record.Upsert(KeyInvocationProviderId, actionEntry.Invocation.ProviderId);
                record.Upsert(KeyInvocationApiSpecId, actionEntry.Invocation.ApiSpecId);
                record.Upsert(KeyInvocationModel, actionEntry.Invocation.Model);
                record.Upsert(KeyActionBlocksJson, ActionMessageSerialization.SerializeBlocks(actionEntry.Message.Blocks));
                break;
            case InjectionEntry injectionEntry:
                record.Upsert(KeyContent, injectionEntry.Content);
                record.Upsert(KeyInjectionBlockKind, injectionEntry.BlockKind.ToString());
                record.Upsert(KeyInjectionSourceKind, injectionEntry.Source.Kind.ToString());
                if (!string.IsNullOrWhiteSpace(injectionEntry.Source.SourceId)) {
                    record.Upsert(KeyInjectionSourceId, injectionEntry.Source.SourceId);
                }
                if (!string.IsNullOrWhiteSpace(injectionEntry.Source.Notes)) {
                    record.Upsert(KeyInjectionSourceNotes, injectionEntry.Source.Notes);
                }
                break;
            case ToolResultsEntry toolResultsEntry:
                if (toolResultsEntry.Notifications is not null) {
                    record.Upsert(KeyNotifications, toolResultsEntry.Notifications);
                }
                record.Upsert(KeyResultsJson, JsonSerializer.Serialize(ToolCallResultsToDtos(toolResultsEntry.Results), JsonOptions));
                break;
            case ObservationEntry observationEntry:
                if (observationEntry.Notifications is not null) {
                    record.Upsert(KeyNotifications, observationEntry.Notifications);
                }
                break;
            case RecapEntry recapEntry:
                record.Upsert(KeyContent, recapEntry.Content);
                record.Upsert(KeyInsteadSerial, recapEntry.InsteadSerial);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(entry), entry.Kind, "Unsupported history entry kind.");
        }

        return record;
    }

    public static HistoryEntry ReadHistoryEntry(DurableDict<string> record) {
        ArgumentNullException.ThrowIfNull(record);

        string kind = record.Get<string>(KeyKind, out var kindValue) == GetIssue.None
            ? kindValue!
            : throw new InvalidDataException("History record is missing kind.");

        long ticks = record.Get<long>(KeyTimestampUtcTicks, out var timestampTicks) == GetIssue.None
            ? timestampTicks
            : throw new InvalidDataException("History record is missing timestampUtcTicks.");

        ulong serial = record.Get<ulong>(KeySerial, out var serialValue) == GetIssue.None
            ? serialValue
            : throw new InvalidDataException("History record is missing serial.");

        uint tokenEstimate = record.Get<uint>(KeyTokenEstimate, out var tokenEstimateValue) == GetIssue.None
            ? tokenEstimateValue
            : throw new InvalidDataException("History record is missing tokenEstimate.");

        DateTimeOffset timestamp = new(ticks, TimeSpan.Zero);

        HistoryEntry entry = kind switch {
            KindAction => ReadActionEntry(record, timestamp),
            KindInjection => ReadInjectionEntry(record, timestamp),
            KindObservation => ReadObservationEntry(record, timestamp),
            KindToolResults => ReadToolResultsEntry(record, timestamp),
            KindRecap => ReadRecapEntry(record, timestamp),
            _ => throw new InvalidDataException($"Unknown history entry kind '{kind}'.")
        };

        entry.AssignTokenEstimate(tokenEstimate);
        entry.AssignSerial(serial);
        return entry;
    }

    public static DurableDict<string> WritePendingToolResult(Revision revision, ToolCallExecutionResult result) {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(result);

        var record = revision.CreateDict<string>();
        record.Upsert(KeyKind, KindPendingToolResult);
        record.Upsert(KeyResultsJson, JsonSerializer.Serialize(ToolCallResultsToDtos([result]), JsonOptions));

        return record;
    }

    public static ToolCallExecutionResult ReadPendingToolResult(DurableDict<string> record) {
        ArgumentNullException.ThrowIfNull(record);

        if (record.Get<string>(KeyKind, out var kind) != GetIssue.None || kind != KindPendingToolResult) { throw new InvalidDataException("Pending tool result record has an unexpected kind."); }

        if (!record.TryGet<string>(KeyResultsJson, out var resultsJson) || string.IsNullOrWhiteSpace(resultsJson)) { throw new InvalidDataException("Pending tool result record is missing resultsJson."); }

        var dtos = JsonSerializer.Deserialize<ToolCallExecutionResultDto[]>(resultsJson, JsonOptions)
                   ?? throw new InvalidDataException("Pending tool result record contains invalid resultsJson.");
        if (dtos.Length != 1) { throw new InvalidDataException("Pending tool result record must contain exactly one serialized result."); }

        return DtoToToolCallExecutionResult(dtos[0]);
    }

    public static void WriteTurnRuntime(
        DurableDict<string> turnRuntime,
        LlmProfileCheckpoint? resolvedProfile,
        int? lockedCompactionSplitIndex
    ) {
        ArgumentNullException.ThrowIfNull(turnRuntime);

        if (resolvedProfile is not null) {
            WriteResolvedProfile(turnRuntime, resolvedProfile);
        }

        if (lockedCompactionSplitIndex.HasValue) {
            WriteLockedCompactionSplitIndex(turnRuntime, lockedCompactionSplitIndex.Value);
        }
    }

    public static void WriteResolvedProfile(DurableDict<string> turnRuntime, LlmProfileCheckpoint resolvedProfile) {
        ArgumentNullException.ThrowIfNull(turnRuntime);
        ArgumentNullException.ThrowIfNull(resolvedProfile);

        var profileRecord = turnRuntime.Revision.CreateDict<string>();
        profileRecord.Upsert(KeyProviderId, resolvedProfile.ProviderId);
        profileRecord.Upsert(KeyApiSpecId, resolvedProfile.ApiSpecId);
        profileRecord.Upsert(KeyModelId, resolvedProfile.ModelId);
        profileRecord.Upsert(KeyName, resolvedProfile.Name);
        profileRecord.Upsert(KeySoftContextTokenCap, (long)resolvedProfile.SoftContextTokenCap);
        turnRuntime.Upsert<DurableObject>(KeyResolvedProfile, profileRecord);
    }

    public static void ClearResolvedProfile(DurableDict<string> turnRuntime) {
        ArgumentNullException.ThrowIfNull(turnRuntime);
        turnRuntime.Remove(KeyResolvedProfile);
    }

    public static void WriteLockedCompactionSplitIndex(DurableDict<string> turnRuntime, int lockedCompactionSplitIndex) {
        ArgumentNullException.ThrowIfNull(turnRuntime);
        turnRuntime.Upsert(KeyLockedCompactionSplitIndex, lockedCompactionSplitIndex);
    }

    public static void ClearLockedCompactionSplitIndex(DurableDict<string> turnRuntime) {
        ArgumentNullException.ThrowIfNull(turnRuntime);
        turnRuntime.Remove(KeyLockedCompactionSplitIndex);
    }

    public static (LlmProfileCheckpoint? ResolvedProfile, int? LockedCompactionSplitIndex) ReadTurnRuntime(DurableDict<string> turnRuntime) {
        ArgumentNullException.ThrowIfNull(turnRuntime);

        LlmProfileCheckpoint? resolvedProfile = null;
        if (turnRuntime.TryGet<DurableDict<string>>(KeyResolvedProfile, out var profileRecord) && profileRecord is not null) {
            string providerId = profileRecord.Get<string>(KeyProviderId, out var provider) == GetIssue.None
                ? provider!
                : throw new InvalidDataException("Resolved profile is missing providerId.");
            string apiSpecId = profileRecord.Get<string>(KeyApiSpecId, out var apiSpec) == GetIssue.None
                ? apiSpec!
                : throw new InvalidDataException("Resolved profile is missing apiSpecId.");
            string modelId = profileRecord.Get<string>(KeyModelId, out var model) == GetIssue.None
                ? model!
                : throw new InvalidDataException("Resolved profile is missing modelId.");
            string name = profileRecord.Get<string>(KeyName, out var profileName) == GetIssue.None
                ? profileName!
                : throw new InvalidDataException("Resolved profile is missing name.");
            long capValue = profileRecord.Get<long>(KeySoftContextTokenCap, out var cap) == GetIssue.None
                ? cap
                : throw new InvalidDataException("Resolved profile is missing softContextTokenCap.");

            resolvedProfile = new LlmProfileCheckpoint(
                ProviderId: providerId,
                ApiSpecId: apiSpecId,
                ModelId: modelId,
                Name: name,
                SoftContextTokenCap: checked((uint)capValue)
            );
        }

        int? lockedCompactionSplitIndex = turnRuntime.Get<int>(KeyLockedCompactionSplitIndex, out var splitIndex) == GetIssue.None
            ? splitIndex
            : null;

        return (resolvedProfile, lockedCompactionSplitIndex);
    }

    public static DurableDict<string> WriteCompactionCheckpoint(Revision revision, CompactionCheckpoint checkpoint) {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(checkpoint);

        var record = revision.CreateDict<string>();
        WriteCompactionCheckpointFields(record, checkpoint);
        return record;
    }

    public static void WriteCompactionCheckpointFields(DurableDict<string> record, CompactionCheckpoint checkpoint) {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(checkpoint);

        record.Upsert(KeySplitIndex, checkpoint.SplitIndex);
        record.Upsert(KeySystemPrompt, checkpoint.SystemPrompt);
        record.Upsert(KeySummarizePrompt, checkpoint.SummarizePrompt);
    }

    public static void ClearCompactionCheckpointFields(DurableDict<string> record) {
        ArgumentNullException.ThrowIfNull(record);
        record.Remove(KeySplitIndex);
        record.Remove(KeySystemPrompt);
        record.Remove(KeySummarizePrompt);
    }

    public static CompactionCheckpoint? ReadCompactionCheckpointOrNull(DurableDict<string> record) {
        ArgumentNullException.ThrowIfNull(record);

        bool hasSplitIndex = record.Get<int>(KeySplitIndex, out var split) == GetIssue.None;
        bool hasSystemPrompt = record.TryGet<string>(KeySystemPrompt, out _);
        bool hasSummarizePrompt = record.TryGet<string>(KeySummarizePrompt, out _);

        if (!hasSplitIndex && !hasSystemPrompt && !hasSummarizePrompt) { return null; }
        if (!hasSplitIndex) { throw new InvalidDataException("Compaction checkpoint is missing splitIndex."); }

        string systemPrompt = record.Get<string>(KeySystemPrompt, out var storedSystemPrompt) == GetIssue.None
            ? storedSystemPrompt!
            : throw new InvalidDataException("Compaction checkpoint is missing systemPrompt.");
        string summarizePrompt = record.Get<string>(KeySummarizePrompt, out var storedSummarizePrompt) == GetIssue.None
            ? storedSummarizePrompt!
            : throw new InvalidDataException("Compaction checkpoint is missing summarizePrompt.");

        return new CompactionCheckpoint(split, systemPrompt, summarizePrompt);
    }

    private static ActionEntry ReadActionEntry(DurableDict<string> record, DateTimeOffset timestamp) {
        string providerId = record.Get<string>(KeyInvocationProviderId, out var provider) == GetIssue.None
            ? provider!
            : throw new InvalidDataException("Action record is missing invocationProviderId.");
        string apiSpecId = record.Get<string>(KeyInvocationApiSpecId, out var apiSpec) == GetIssue.None
            ? apiSpec!
            : throw new InvalidDataException("Action record is missing invocationApiSpecId.");
        string model = record.Get<string>(KeyInvocationModel, out var modelId) == GetIssue.None
            ? modelId!
            : throw new InvalidDataException("Action record is missing invocationModel.");

        var blocks = Array.Empty<ActionBlock>();
        if (record.TryGet<string>(KeyActionBlocksJson, out var blocksJson) && !string.IsNullOrWhiteSpace(blocksJson)) {
            blocks = ActionMessageSerialization.DeserializeBlocks(blocksJson);
        }

        return new ActionEntry(
            new ActionMessage(blocks),
            new CompletionDescriptor(providerId, apiSpecId, model)
        ) {
            Timestamp = timestamp
        };
    }

    private static ObservationEntry ReadObservationEntry(DurableDict<string> record, DateTimeOffset timestamp) {
        var entry = new ObservationEntry {
            Timestamp = timestamp
        };

        if (record.TryGet<string>(KeyNotifications, out var notifications) && notifications is not null) {
            entry.AssignNotifications(notifications);
        }

        return entry;
    }

    private static InjectionEntry ReadInjectionEntry(DurableDict<string> record, DateTimeOffset timestamp) {
        string content = record.Get<string>(KeyContent, out var injectionContent) == GetIssue.None
            ? injectionContent!
            : throw new InvalidDataException("Injection record is missing content.");

        var blockKindText = record.Get<string>(KeyInjectionBlockKind, out var injectionBlockKind) == GetIssue.None
            ? injectionBlockKind
            : throw new InvalidDataException("Injection record is missing injectionBlockKind.");
        if (!Enum.TryParse<ActionBlockKind>(blockKindText, ignoreCase: true, out var blockKind)) {
            throw new InvalidDataException($"Unsupported injection block kind '{blockKindText}'.");
        }

        var sourceKindText = record.Get<string>(KeyInjectionSourceKind, out var sourceKindValue) == GetIssue.None
            ? sourceKindValue
            : throw new InvalidDataException("Injection record is missing injectionSourceKind.");
        if (!Enum.TryParse<InjectionSourceKind>(sourceKindText, ignoreCase: true, out var sourceKind)) {
            throw new InvalidDataException($"Unsupported injection source kind '{sourceKindText}'.");
        }

        string? sourceId = record.TryGet<string>(KeyInjectionSourceId, out var sourceIdValue) ? sourceIdValue : null;
        string? sourceNotes = record.TryGet<string>(KeyInjectionSourceNotes, out var sourceNotesValue) ? sourceNotesValue : null;

        return new InjectionEntry(
            content: content,
            blockKind: blockKind,
            source: new InjectionSource(sourceKind, sourceId, sourceNotes)
        ) {
            Timestamp = timestamp
        };
    }

    private static ToolResultsEntry ReadToolResultsEntry(DurableDict<string> record, DateTimeOffset timestamp) {
        var results = Array.Empty<ToolCallExecutionResult>();
        if (record.TryGet<string>(KeyResultsJson, out var resultsJson) && !string.IsNullOrWhiteSpace(resultsJson)) {
            var dtos = JsonSerializer.Deserialize<ToolCallExecutionResultDto[]>(resultsJson, JsonOptions) ?? Array.Empty<ToolCallExecutionResultDto>();
            results = new ToolCallExecutionResult[dtos.Length];
            for (int i = 0; i < dtos.Length; i++) {
                results[i] = DtoToToolCallExecutionResult(dtos[i]);
            }
        }

        var entry = new ToolResultsEntry(results) {
            Timestamp = timestamp
        };

        if (record.TryGet<string>(KeyNotifications, out var notifications) && notifications is not null) {
            entry.AssignNotifications(notifications);
        }

        return entry;
    }

    private static RecapEntry ReadRecapEntry(DurableDict<string> record, DateTimeOffset timestamp) {
        string content = record.Get<string>(KeyContent, out var recapContent) == GetIssue.None
            ? recapContent!
            : throw new InvalidDataException("Recap record is missing content.");
        ulong insteadSerial = record.Get<ulong>(KeyInsteadSerial, out var insteadSerialValue) == GetIssue.None
            ? insteadSerialValue
            : throw new InvalidDataException("Recap record is missing insteadSerial.");

        return new RecapEntry(content, insteadSerial) {
            Timestamp = timestamp
        };
    }

    private static ToolCallExecutionResultDto[] ToolCallResultsToDtos(IReadOnlyList<ToolCallExecutionResult> results) {
        var dtos = new ToolCallExecutionResultDto[results.Count];
        for (int i = 0; i < results.Count; i++) {
            var result = results[i];
            dtos[i] = new ToolCallExecutionResultDto(
                ToolName: result.ToolName,
                ToolCallId: result.ToolCallId,
                RawArgumentsJson: result.RawToolCall.RawArgumentsJson,
                Status: result.ExecuteResult.Status,
                Blocks: ToolResultBlocksToDtos(result.ExecuteResult.Blocks),
                ElapsedTicks: result.Elapsed?.Ticks
            );
        }

        return dtos;
    }

    private static ToolCallExecutionResult DtoToToolCallExecutionResult(ToolCallExecutionResultDto dto) {
        var blocks = DtosToToolResultBlocks(dto.Blocks ?? Array.Empty<ToolResultBlockDto>());
        return new ToolCallExecutionResult(
            rawToolCall: new RawToolCall(dto.ToolName, dto.ToolCallId, dto.RawArgumentsJson),
            executeResult: new ToolExecuteResult(dto.Status, blocks),
            elapsed: dto.ElapsedTicks.HasValue ? TimeSpan.FromTicks(dto.ElapsedTicks.Value) : default
        );
    }

    private static ToolResultBlockDto[] ToolResultBlocksToDtos(IReadOnlyList<ToolResultBlock> blocks) {
        var dtos = new ToolResultBlockDto[blocks.Count];
        for (int i = 0; i < blocks.Count; i++) {
            dtos[i] = blocks[i] switch {
                ToolResultBlock.Text text => new ToolResultBlockDto(ToolResultBlockKindText, text.Content),
                _ => throw new InvalidOperationException($"Unsupported tool result block type '{blocks[i].GetType().FullName}'.")
            };
        }

        return dtos;
    }

    private static ToolResultBlock[] DtosToToolResultBlocks(ToolResultBlockDto[] dtos) {
        var blocks = new ToolResultBlock[dtos.Length];
        for (int i = 0; i < dtos.Length; i++) {
            blocks[i] = dtos[i].Kind switch {
                ToolResultBlockKindText => new ToolResultBlock.Text(dtos[i].Content ?? string.Empty),
                _ => throw new InvalidDataException($"Unsupported tool result block kind '{dtos[i].Kind}'.")
            };
        }

        return blocks;
    }

    private static string GetHistoryKind(HistoryEntryKind kind) {
        return kind switch {
            HistoryEntryKind.Observation => KindObservation,
            HistoryEntryKind.Action => KindAction,
            HistoryEntryKind.Injection => KindInjection,
            HistoryEntryKind.ToolResults => KindToolResults,
            HistoryEntryKind.Recap => KindRecap,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported history entry kind.")
        };
    }

    private sealed record ToolCallExecutionResultDto(
        string ToolName,
        string ToolCallId,
        string RawArgumentsJson,
        ToolExecutionStatus Status,
        ToolResultBlockDto[] Blocks,
        long? ElapsedTicks
    );

    private sealed record ToolResultBlockDto(
        string Kind,
        string? Content
    );
}
