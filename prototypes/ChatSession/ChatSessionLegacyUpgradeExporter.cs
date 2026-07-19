using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

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
        return ExportJson(report, branchName, options);
    }

    public static string ExportJson(
        ChatSessionLegacyRecapRecoveryReport report,
        string branchName = "main",
        ChatSessionLegacyUpgradeExportOptions? options = null
    ) {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);

        options ??= new ChatSessionLegacyUpgradeExportOptions();
        var export = BuildExport(report, branchName, options.GeneratedAtUtc);
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
        DateTimeOffset? generatedAtUtc
    )
        => new(
            Schema: SchemaId,
            GeneratedAtUtc: generatedAtUtc?.ToUniversalTime().ToString("O"),
            BranchName: branchName,
            Timeline: report.Timeline.Select(ToTimelineEntry).ToArray(),
            RecapMappings: report.Findings.Select(finding => ToRecapMapping(finding, branchName)).ToArray(),
            Warnings: report.Warnings.ToArray()
        );

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
        IReadOnlyList<string> Warnings
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
}
