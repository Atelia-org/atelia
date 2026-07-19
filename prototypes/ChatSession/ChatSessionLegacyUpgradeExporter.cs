using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using Atelia.StateJournal;

namespace Atelia.ChatSession;

public sealed record ChatSessionLegacyUpgradeExportOptions(
    bool WriteIndented = true,
    DateTimeOffset? GeneratedAtUtc = null
);

public static class ChatSessionLegacyUpgradeExporter {
    private const string SchemaId = "atelia.chat-session.legacy-upgrade-export.v1";

    private static readonly JsonSerializerOptions CompactJsonOptions = CreateJsonOptions(writeIndented: false);
    private static readonly JsonSerializerOptions IndentedJsonOptions = CreateJsonOptions(writeIndented: true);

    public static string ExportJson(
        string repoDir,
        string branchName = "main",
        ChatSessionLegacyUpgradeExportOptions? options = null
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoDir);
        var report = ChatSessionLegacyRecapRecovery.Analyze(repoDir, branchName);
        var snapshots = ReadSnapshots(repoDir, branchName);
        return ExportJson(report, branchName, options, snapshots);
    }

    public static string ExportJson(
        ChatSessionLegacyRecapRecoveryReport report,
        string branchName = "main",
        ChatSessionLegacyUpgradeExportOptions? options = null
    ) {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);

        return ExportJson(report, branchName, options, snapshots: null);
    }

    private static string ExportJson(
        ChatSessionLegacyRecapRecoveryReport report,
        string branchName,
        ChatSessionLegacyUpgradeExportOptions? options,
        IReadOnlyList<CommitSnapshot>? snapshots
    ) {
        options ??= new ChatSessionLegacyUpgradeExportOptions();
        var export = BuildExport(report, branchName, options.GeneratedAtUtc, snapshots);
        var jsonOptions = options.WriteIndented ? IndentedJsonOptions : CompactJsonOptions;
        return JsonSerializer.Serialize(export, jsonOptions);
    }

    public static void WriteJsonFile(
        string repoDir,
        string outputPath,
        string branchName = "main",
        ChatSessionLegacyUpgradeExportOptions? options = null
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory)) { Directory.CreateDirectory(directory); }

        File.WriteAllText(outputPath, ExportJson(repoDir, branchName, options));
    }

    private static UpgradeExportDto BuildExport(
        ChatSessionLegacyRecapRecoveryReport report,
        string branchName,
        DateTimeOffset? generatedAtUtc,
        IReadOnlyList<CommitSnapshot>? snapshots
    )
        => new(
            Schema: SchemaId,
            GeneratedAtUtc: generatedAtUtc?.ToUniversalTime().ToString("O"),
            BranchName: branchName,
            Timeline: report.Timeline.Select(ToTimelineEntry).ToArray(),
            RecapMappings: report.Findings.Select(finding => ToRecapMapping(finding, branchName)).ToArray(),
            Events: snapshots is null ? Array.Empty<EventDto>() : BuildEvents(report, branchName, snapshots),
            Warnings: report.Warnings.ToArray()
        );

    private static IReadOnlyList<EventDto> BuildEvents(
        ChatSessionLegacyRecapRecoveryReport report,
        string branchName,
        IReadOnlyList<CommitSnapshot> snapshots
    ) {
        var snapshotsByCommit = snapshots.ToDictionary(static snapshot => snapshot.Commit, StringComparer.Ordinal);
        var findingsByNewHead = report.Findings.ToDictionary(static finding => finding.NewHead, StringComparer.Ordinal);
        var events = new EventDto[report.Timeline.Count];

        for (int timelineIndex = 0; timelineIndex < report.Timeline.Count; timelineIndex++) {
            var timeline = report.Timeline[timelineIndex];
            snapshotsByCommit.TryGetValue(timeline.Commit, out var currentSnapshot);
            CommitSnapshot? previousSnapshot = timelineIndex == 0 ? null : snapshotsByCommit.GetValueOrDefault(report.Timeline[timelineIndex - 1].Commit);
            findingsByNewHead.TryGetValue(timeline.Commit, out var finding);
            events[timelineIndex] = ToEvent(timeline, previousSnapshot, currentSnapshot, finding, branchName);
        }

        return events;
    }

    private static EventDto ToEvent(
        ChatSessionLegacyCommitTimelineEntry timeline,
        CommitSnapshot? previousSnapshot,
        CommitSnapshot? currentSnapshot,
        ChatSessionLegacyRecapRecoveryFinding? finding,
        string branchName
    ) {
        var kind = FormatAttributionKind(timeline.Attribution.Kind);
        var metadata = new CommitMetadataDto(
            kind,
            timeline.Attribution.Reason,
            FormatAttributionSource(timeline.Attribution.Source)
        );

        return timeline.Attribution.Kind switch {
            ChatSessionCommitAttributionKind.InitialState => new EventDto(
                timeline.Ordinal,
                timeline.Commit,
                kind,
                metadata,
                Root: currentSnapshot is null ? null : ToRootMetadata(currentSnapshot),
                Messages: currentSnapshot?.Records.Select(ToMessage).ToArray()
            ),
            ChatSessionCommitAttributionKind.ModelTurn => new EventDto(
                timeline.Ordinal,
                timeline.Commit,
                kind,
                metadata,
                AppendedMessages: GetAppendedMessages(previousSnapshot, currentSnapshot)
            ),
            ChatSessionCommitAttributionKind.Compaction => new EventDto(
                timeline.Ordinal,
                timeline.Commit,
                kind,
                metadata,
                OldHead: finding?.OldHead,
                NewHead: finding?.NewHead,
                SourceRange: finding is null ? null : ToSourceRange(finding),
                SourceMessages: GetSourceMessages(previousSnapshot, finding),
                RecapIndex: finding?.RecapIndex,
                RecapMessage: GetRecapMessage(currentSnapshot, finding),
                RecapSourceAnchor: finding is null ? null : ToRecapSourceAnchor(finding, branchName),
                SuffixMatchCount: finding?.SuffixMatchCount,
                Confidence: finding is null ? null : FormatConfidence(finding.Confidence),
                Reason: finding?.Reason
            ),
            ChatSessionCommitAttributionKind.UpdateSystemPrompt => new EventDto(
                timeline.Ordinal,
                timeline.Commit,
                kind,
                metadata,
                SystemPromptChange: previousSnapshot is null || currentSnapshot is null
                    ? null
                    : new SystemPromptChangeDto(previousSnapshot.SystemPrompt, currentSnapshot.SystemPrompt)
            ),
            ChatSessionCommitAttributionKind.RevertTurn => new EventDto(
                timeline.Ordinal,
                timeline.Commit,
                kind,
                metadata,
                RemovedMessages: GetRemovedMessages(previousSnapshot, currentSnapshot)
            ),
            _ => new EventDto(
                timeline.Ordinal,
                timeline.Commit,
                kind,
                metadata,
                Reason: timeline.Attribution.Reason
            )
        };
    }

    private static TimelineEntryDto ToTimelineEntry(ChatSessionLegacyCommitTimelineEntry entry)
        => new(
            Ordinal: entry.Ordinal,
            Commit: entry.Commit,
            MessageCount: entry.MessageCount,
            MessageCountDeltaFromPrevious: entry.MessageCountDeltaFromPrevious,
            CommitMetadata: new CommitMetadataDto(
                CommitKind: FormatAttributionKind(entry.Attribution.Kind),
                CommitReason: entry.Attribution.Reason,
                MetadataSource: FormatAttributionSource(entry.Attribution.Source)
            )
        );

    private static IReadOnlyList<CommitSnapshot> ReadSnapshots(string repoDir, string branchName) {
        using var repo = Repository.Open(repoDir).Unwrap();
        var history = RepositoryHistoryReader.EnumerateBranchEffectiveCommitAddresses(repo, branchName);
        var snapshots = new List<CommitSnapshot>(history.Addresses.Count);

        for (int addressIndex = history.Addresses.Count - 1; addressIndex >= 0; addressIndex--) {
            var historyAddress = history.Addresses[addressIndex];
            var rootResult = repo.LoadRootAtCommit(historyAddress.Address);
            if (rootResult.IsFailure || rootResult.Value is not DurableDict<string> root) { continue; }

            ChatSessionStorageSchema.ValidateRoot(root);
            snapshots.Add(ReadSnapshot(historyAddress.Address.ToString(), root));
        }

        return snapshots;
    }

    private static CommitSnapshot ReadSnapshot(string commit, DurableDict<string> root) {
        var messages = ChatSessionStorageSchema.GetMessages(root);
        var records = new List<MessageSnapshot>(messages.Count);
        for (int messageIndex = 0; messageIndex < messages.Count; messageIndex++) {
            if (!messages.TryGetAt<DurableDict<string>>(messageIndex, out var record) || record is null) { throw new InvalidDataException($"Message record at index {messageIndex} is not a durable message dict."); }

            records.Add(
                new MessageSnapshot(
                    MessageRecord.GetKind(record),
                    MessageRecord.GetTimestampUtc(record),
                    MessageRecord.ToHistoryMessage(record)
                )
            );
        }

        return new CommitSnapshot(
            Commit: commit,
            Kind: ReadRequiredString(root, ChatSessionStorageSchema.KeyKind),
            SchemaVersion: ReadRequiredLong(root, ChatSessionStorageSchema.KeySchemaVersion),
            ApiSpecId: ReadOptionalString(root, ChatSessionStorageSchema.KeyApiSpecId),
            CompletionSurfaceId: ReadOptionalString(root, ChatSessionStorageSchema.KeyCompletionSurfaceId),
            ModelId: ReadOptionalString(root, ChatSessionStorageSchema.KeyModelId),
            SystemPrompt: ReadRequiredString(root, ChatSessionStorageSchema.KeySystemPrompt),
            Records: records
        );
    }

    private static RecapMappingDto ToRecapMapping(ChatSessionLegacyRecapRecoveryFinding finding, string branchName) {
        var isResolved = finding.Confidence != ChatSessionRecapRecoveryConfidence.Unresolved
                         && finding.RecapIndex is not null;

        return new RecapMappingDto(
            Kind: isResolved ? "inferred" : "unresolved",
            MappingSource: "legacy-inferred",
            OldHead: finding.OldHead,
            NewHead: finding.NewHead,
            RecapIndex: finding.RecapIndex,
            RecapSourceAnchor: isResolved
                ? new RecapSourceAnchorDto(
                    SourceHeadBeforeCompaction: finding.OldHead,
                    SourceBranchName: branchName,
                    SourceStartIndex: finding.SourceStartIndex,
                    SourceEndExclusive: finding.SourceEndExclusive,
                    SourceMessageCountBefore: finding.SourceMessageCountBefore,
                    CompactionKind: MessageRecord.CompactionKindPrefixSummary
                )
                : null,
            SourceRange: new SourceRangeDto(
                StartIndex: finding.SourceStartIndex,
                EndExclusive: finding.SourceEndExclusive,
                MessageCountBefore: finding.SourceMessageCountBefore
            ),
            SuffixMatchCount: finding.SuffixMatchCount,
            Confidence: FormatConfidence(finding.Confidence),
            Reason: finding.Reason
        );
    }

    private static RootMetadataDto ToRootMetadata(CommitSnapshot snapshot)
        => new(
            snapshot.Kind,
            snapshot.SchemaVersion,
            snapshot.ApiSpecId,
            snapshot.CompletionSurfaceId,
            snapshot.ModelId,
            snapshot.SystemPrompt
        );

    private static IReadOnlyList<MessageDto>? GetAppendedMessages(CommitSnapshot? previousSnapshot, CommitSnapshot? currentSnapshot) {
        if (previousSnapshot is null || currentSnapshot is null) { return null; }
        if (currentSnapshot.Records.Count < previousSnapshot.Records.Count) { return Array.Empty<MessageDto>(); }
        return currentSnapshot.Records.Skip(previousSnapshot.Records.Count).Select(ToMessage).ToArray();
    }

    private static IReadOnlyList<MessageDto>? GetSourceMessages(CommitSnapshot? previousSnapshot, ChatSessionLegacyRecapRecoveryFinding? finding) {
        if (previousSnapshot is null || finding is null) { return null; }
        return previousSnapshot.Records
            .Skip(finding.SourceStartIndex)
            .Take(finding.SourceEndExclusive - finding.SourceStartIndex)
            .Select(ToMessage)
            .ToArray();
    }

    private static MessageDto? GetRecapMessage(CommitSnapshot? currentSnapshot, ChatSessionLegacyRecapRecoveryFinding? finding) {
        if (currentSnapshot is null || finding?.RecapIndex is not { } recapIndex) { return null; }
        if (recapIndex < 0 || recapIndex >= currentSnapshot.Records.Count) { return null; }
        return ToMessage(currentSnapshot.Records[recapIndex]);
    }

    private static IReadOnlyList<MessageDto>? GetRemovedMessages(CommitSnapshot? previousSnapshot, CommitSnapshot? currentSnapshot) {
        if (previousSnapshot is null || currentSnapshot is null) { return null; }
        if (previousSnapshot.Records.Count < currentSnapshot.Records.Count) { return Array.Empty<MessageDto>(); }
        return previousSnapshot.Records.Skip(currentSnapshot.Records.Count).Select(ToMessage).ToArray();
    }

    private static MessageDto ToMessage(MessageSnapshot snapshot) {
        var timestampUtc = snapshot.TimestampUtc?.ToUniversalTime().ToString("O");
        return snapshot.Message switch {
            RecapMessage recap => new MessageDto(snapshot.Kind, timestampUtc, recap.Content, RecapSourceAnchor: ToRecapSourceAnchor(recap.SourceAnchor)),
            ActionMessage action => new MessageDto(snapshot.Kind, timestampUtc,
                Action: new ActionMessageDto(
                    action.GetFlattenedText(),
                    ActionMessageSerialization.ToSerializedBlocks(action.Blocks)
                )
            ),
            ToolResultsMessage toolResults => new MessageDto(snapshot.Kind, timestampUtc, toolResults.Content,
                ToolResults: new ToolResultsMessageDto(
                    toolResults.Results.Select(ToToolResult).ToArray()
                )
            ),
            ObservationMessage observation => new MessageDto(snapshot.Kind, timestampUtc, observation.Content),
            ContextHeader contextHeader => new MessageDto(snapshot.Kind, timestampUtc,
                ContextHeader: new ContextHeaderDto(
                    contextHeader.SystemPromptFragment,
                    contextHeader.UserMessage,
                    contextHeader.AssistantMessage is null
                    ? null
                    : new ActionMessageDto(
                        contextHeader.AssistantMessage.GetFlattenedText(),
                        ActionMessageSerialization.ToSerializedBlocks(contextHeader.AssistantMessage.Blocks)
                    )
                )
            ),
            _ => new MessageDto(snapshot.Kind, timestampUtc)
        };
    }

    private static ToolResultDto ToToolResult(ToolResult result)
        => new(
            result.ToolName,
            result.ToolCallId,
            FormatToolExecutionStatus(result.Status),
            result.GetFlattenedText(),
            result.Blocks.Select(ToToolResultBlock).ToArray()
        );

    private static ToolResultBlockDto ToToolResultBlock(ToolResultBlock block)
        => block switch {
            ToolResultBlock.Text text => new ToolResultBlockDto("text", text.Content),
            _ => new ToolResultBlockDto(block.Kind.ToString(), null)
        };

    private static SourceRangeDto ToSourceRange(ChatSessionLegacyRecapRecoveryFinding finding)
        => new(finding.SourceStartIndex, finding.SourceEndExclusive, finding.SourceMessageCountBefore);

    private static RecapSourceAnchorDto? ToRecapSourceAnchor(ChatSessionLegacyRecapRecoveryFinding finding, string branchName) {
        if (finding.Confidence == ChatSessionRecapRecoveryConfidence.Unresolved || finding.RecapIndex is null) { return null; }
        return new RecapSourceAnchorDto(
            SourceHeadBeforeCompaction: finding.OldHead,
            SourceBranchName: branchName,
            SourceStartIndex: finding.SourceStartIndex,
            SourceEndExclusive: finding.SourceEndExclusive,
            SourceMessageCountBefore: finding.SourceMessageCountBefore,
            CompactionKind: MessageRecord.CompactionKindPrefixSummary
        );
    }

    private static RecapSourceAnchorDto? ToRecapSourceAnchor(RecapSourceAnchor? anchor)
        => anchor is null
            ? null
            : new RecapSourceAnchorDto(
                anchor.SourceHeadBeforeCompaction,
                anchor.SourceBranchName,
                anchor.SourceStartIndex,
                anchor.SourceEndExclusive,
                anchor.SourceMessageCountBefore,
                anchor.CompactionKind
            );

    private static string FormatConfidence(ChatSessionRecapRecoveryConfidence confidence)
        => confidence switch {
            ChatSessionRecapRecoveryConfidence.High => "high",
            ChatSessionRecapRecoveryConfidence.Medium => "medium",
            ChatSessionRecapRecoveryConfidence.Low => "low",
            ChatSessionRecapRecoveryConfidence.Unresolved => "unresolved",
            _ => confidence.ToString()
        };

    private static string FormatAttributionKind(ChatSessionCommitAttributionKind kind)
        => kind switch {
            ChatSessionCommitAttributionKind.InitialState => "initial-state",
            ChatSessionCommitAttributionKind.ModelTurn => "model-turn",
            ChatSessionCommitAttributionKind.Compaction => "compaction",
            ChatSessionCommitAttributionKind.RevertTurn => "revert-turn",
            ChatSessionCommitAttributionKind.UpdateSystemPrompt => "update-system-prompt",
            ChatSessionCommitAttributionKind.UpdateContextHeader => "update-context-header",
            ChatSessionCommitAttributionKind.RedundantSave => "redundant-save",
            ChatSessionCommitAttributionKind.Other => "other",
            _ => kind.ToString()
        };

    private static string FormatAttributionSource(ChatSessionCommitAttributionSource source)
        => source switch {
            ChatSessionCommitAttributionSource.Explicit => "explicit",
            ChatSessionCommitAttributionSource.LegacyInferred => "legacy-inferred",
            _ => source.ToString()
        };

    private static string FormatToolExecutionStatus(ToolExecutionStatus status)
        => status switch {
            ToolExecutionStatus.Success => "success",
            ToolExecutionStatus.Failed => "failed",
            ToolExecutionStatus.Skipped => "skipped",
            _ => status.ToString()
        };

    private static string ReadRequiredString(DurableDict<string> root, string key) {
        if (root.Get<string>(key, out var value) != GetIssue.None || value is null) { throw new InvalidDataException($"Root is missing {key}."); }
        return value;
    }

    private static string? ReadOptionalString(DurableDict<string> root, string key)
        => root.TryGet<string>(key, out var value) ? value : null;

    private static long ReadRequiredLong(DurableDict<string> root, string key) {
        if (root.Get<long>(key, out var value) != GetIssue.None) { throw new InvalidDataException($"Root is missing {key}."); }
        return value;
    }

    private static JsonSerializerOptions CreateJsonOptions(bool writeIndented)
        => new() {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = writeIndented
        };

    private sealed record UpgradeExportDto(
        string Schema,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? GeneratedAtUtc,
        string BranchName,
        IReadOnlyList<TimelineEntryDto> Timeline,
        IReadOnlyList<RecapMappingDto> RecapMappings,
        IReadOnlyList<EventDto> Events,
        IReadOnlyList<string> Warnings
    );

    private sealed record EventDto(
        int Ordinal,
        string Commit,
        string Kind,
        CommitMetadataDto CommitMetadata,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        RootMetadataDto? Root = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<MessageDto>? Messages = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<MessageDto>? AppendedMessages = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? OldHead = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? NewHead = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        SourceRangeDto? SourceRange = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<MessageDto>? SourceMessages = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? RecapIndex = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        MessageDto? RecapMessage = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        RecapSourceAnchorDto? RecapSourceAnchor = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? SuffixMatchCount = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Confidence = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        SystemPromptChangeDto? SystemPromptChange = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<MessageDto>? RemovedMessages = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Reason = null
    );

    private sealed record RootMetadataDto(
        string Kind,
        long SchemaVersion,
        string? ApiSpecId,
        string? CompletionSurfaceId,
        string? ModelId,
        string SystemPrompt
    );

    private sealed record MessageDto(
        string Kind,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? TimestampUtc = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Content = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ActionMessageDto? Action = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ToolResultsMessageDto? ToolResults = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ContextHeaderDto? ContextHeader = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        RecapSourceAnchorDto? RecapSourceAnchor = null
    );

    private sealed record ActionMessageDto(
        string FlattenedText,
        IReadOnlyList<SerializedActionBlock> Blocks
    );

    private sealed record ToolResultsMessageDto(
        IReadOnlyList<ToolResultDto> Results
    );

    private sealed record ToolResultDto(
        string ToolName,
        string ToolCallId,
        string Status,
        string FlattenedText,
        IReadOnlyList<ToolResultBlockDto> Blocks
    );

    private sealed record ToolResultBlockDto(
        string Kind,
        string? Content
    );

    private sealed record ContextHeaderDto(
        string? SystemPromptFragment,
        string? UserMessage,
        ActionMessageDto? AssistantMessage
    );

    private sealed record SystemPromptChangeDto(
        string OldSystemPrompt,
        string NewSystemPrompt
    );

    private sealed record TimelineEntryDto(
        int Ordinal,
        string Commit,
        int MessageCount,
        int? MessageCountDeltaFromPrevious,
        CommitMetadataDto CommitMetadata
    );

    private sealed record CommitMetadataDto(
        string CommitKind,
        string CommitReason,
        string MetadataSource
    );

    private sealed record RecapMappingDto(
        string Kind,
        string MappingSource,
        string OldHead,
        string NewHead,
        int? RecapIndex,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        RecapSourceAnchorDto? RecapSourceAnchor,
        SourceRangeDto SourceRange,
        int SuffixMatchCount,
        string Confidence,
        string Reason
    );

    private sealed record RecapSourceAnchorDto(
        string SourceHeadBeforeCompaction,
        string SourceBranchName,
        int SourceStartIndex,
        int SourceEndExclusive,
        int SourceMessageCountBefore,
        string CompactionKind
    );

    private sealed record SourceRangeDto(
        int StartIndex,
        int EndExclusive,
        int MessageCountBefore
    );

    private sealed record CommitSnapshot(
        string Commit,
        string Kind,
        long SchemaVersion,
        string? ApiSpecId,
        string? CompletionSurfaceId,
        string? ModelId,
        string SystemPrompt,
        IReadOnlyList<MessageSnapshot> Records
    );

    private sealed record MessageSnapshot(
        string Kind,
        DateTimeOffset? TimestampUtc,
        IHistoryMessage Message
    );
}
