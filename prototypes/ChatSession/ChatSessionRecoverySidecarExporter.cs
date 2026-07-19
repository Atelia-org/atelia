using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.StateJournal;

namespace Atelia.ChatSession;

public sealed record ChatSessionRecoverySidecarExportOptions(
    bool WriteIndented = true,
    DateTimeOffset? GeneratedAtUtc = null
);

public static class ChatSessionRecoverySidecarExporter {
    private const string SchemaId = "atelia.chat-session.recap-recovery-sidecar.v1";

    private static readonly JsonSerializerOptions CompactJsonOptions = CreateJsonOptions(writeIndented: false);
    private static readonly JsonSerializerOptions IndentedJsonOptions = CreateJsonOptions(writeIndented: true);

    public static string ExportJson(
        ChatSessionLegacyRecapRecoveryReport report,
        string branchName = "main",
        ChatSessionRecoverySidecarExportOptions? options = null
    ) {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);

        options ??= new ChatSessionRecoverySidecarExportOptions();
        var sidecar = BuildSidecar(report, branchName, options.GeneratedAtUtc);
        var jsonOptions = options.WriteIndented ? IndentedJsonOptions : CompactJsonOptions;
        return JsonSerializer.Serialize(sidecar, jsonOptions);
    }

    public static void WriteJsonFile(
        ChatSessionLegacyRecapRecoveryReport report,
        string outputPath,
        string branchName = "main",
        ChatSessionRecoverySidecarExportOptions? options = null
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory)) { Directory.CreateDirectory(directory); }

        File.WriteAllText(outputPath, ExportJson(report, branchName, options));
    }

    private static SidecarDto BuildSidecar(
        ChatSessionLegacyRecapRecoveryReport report,
        string branchName,
        DateTimeOffset? generatedAtUtc
    )
        => new(
            Schema: SchemaId,
            GeneratedAtUtc: generatedAtUtc?.ToUniversalTime().ToString("O"),
            BranchName: branchName,
            Timeline: report.Timeline.Select(ToTimelineEntry).ToArray(),
            Findings: report.Findings.Select(ToFinding).ToArray(),
            Warnings: report.Warnings.ToArray()
        );

    private static TimelineEntryDto ToTimelineEntry(ChatSessionLegacyCommitTimelineEntry entry)
        => new(
            entry.Ordinal,
            entry.Commit,
            FormatSource(entry.Source),
            entry.MessageCount,
            entry.MessageCountDeltaFromPrevious,
            new AttributionDto(FormatAttributionKind(entry.Attribution.Kind), entry.Attribution.Reason),
            entry.Oldest3.ToArray(),
            entry.Newest3.ToArray()
        );

    private static FindingDto ToFinding(ChatSessionLegacyRecapRecoveryFinding finding)
        => new(
            Kind: finding.Confidence == ChatSessionRecapRecoveryConfidence.Unresolved ? "unresolved" : "inferred",
            OldHead: finding.OldHead,
            NewHead: finding.NewHead,
            RecapIndex: finding.RecapIndex,
            SourceRange: new SourceRangeDto(
                finding.SourceStartIndex,
                finding.SourceEndExclusive,
                finding.SourceMessageCountBefore
            ),
            SuffixMatchCount: finding.SuffixMatchCount,
            Confidence: FormatConfidence(finding.Confidence),
            Reason: finding.Reason
        );

    private static string FormatSource(BranchHistoryAddressSource source)
        => source switch {
            BranchHistoryAddressSource.EffectiveHead => "effective-head",
            BranchHistoryAddressSource.EffectiveParent => "effective-parent",
            _ => source.ToString()
        };

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
            ChatSessionCommitAttributionKind.RedundantSave => "redundant-save",
            ChatSessionCommitAttributionKind.Other => "other",
            _ => kind.ToString()
        };

    private static JsonSerializerOptions CreateJsonOptions(bool writeIndented)
        => new() {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = writeIndented
        };

    private sealed record SidecarDto(
        string Schema,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? GeneratedAtUtc,
        string BranchName,
        IReadOnlyList<TimelineEntryDto> Timeline,
        IReadOnlyList<FindingDto> Findings,
        IReadOnlyList<string> Warnings
    );

    private sealed record TimelineEntryDto(
        int Ordinal,
        string Commit,
        string Source,
        int MessageCount,
        int? MessageCountDeltaFromPrevious,
        AttributionDto Attribution,
        IReadOnlyList<string> Oldest3,
        IReadOnlyList<string> Newest3
    );

    private sealed record AttributionDto(
        string Kind,
        string Reason
    );

    private sealed record FindingDto(
        string Kind,
        string OldHead,
        string NewHead,
        int? RecapIndex,
        SourceRangeDto SourceRange,
        int SuffixMatchCount,
        string Confidence,
        string Reason
    );

    private sealed record SourceRangeDto(
        int StartIndex,
        int EndExclusive,
        int MessageCountBefore
    );
}
