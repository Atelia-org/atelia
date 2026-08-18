using System.Text.Json.Serialization;
using Atelia.EventJournal;

namespace Atelia.SessionJournal.Offline;

public sealed record SessionJournalOfflineEventKindCount(
    [property: JsonConverter(
        typeof(SessionJournalOfflineEventKindJsonConverter))]
    SessionEventKind Kind,
    int Count
);

public sealed record SessionJournalOfflineValidationReport(
    string Schema,
    string RepositoryPath,
    string BranchName,
    string BranchRefId,
    string? Head,
    int EventCount,
    long LogicalPayloadBytes,
    [property: JsonConverter(
        typeof(SessionJournalOfflineExecutionPhaseJsonConverter))]
    SessionExecutionPhase ExecutionPhase,
    [property: JsonConverter(
        typeof(SessionJournalOfflineEventKindJsonConverter))]
    SessionEventKind? HeadKind,
    long ToolExecutionSequenceCheckpoint,
    string? RuntimeConfigSetup,
    string? SystemPromptSetup,
    SessionRuntimeConfiguration? RuntimeConfig,
    string SystemPromptUtf8Sha256CodecId,
    string? SystemPromptUtf8Sha256,
    int PreparedRequestCount,
    int ObservationCount,
    int AgentActionCount,
    int ImportedAgentActionCount,
    int ToolResultHistoryCount,
    int HistoryContributionCount,
    string HistorySemanticCommitmentCodecId,
    string HistorySemanticCommitmentSha256,
    IReadOnlyList<SessionJournalOfflineEventKindCount>
        EventKindCounts,
    SessionJournalAuditScanDiagnostics ScanDiagnostics
);

internal sealed record SessionJournalOfflineFoldResult(
    SessionExecutionState ExecutionState,
    EventAddress? RuntimeConfigSetupAddress,
    SessionRuntimeConfiguration? RuntimeConfig,
    EventAddress? SystemPromptSetupAddress,
    string? SystemPrompt,
    int PreparedRequestCount,
    int ObservationCount,
    int AgentActionCount,
    int ImportedAgentActionCount,
    int ToolResultHistoryCount,
    int HistoryContributionCount,
    string HistorySemanticCommitmentCodecId,
    string HistorySemanticCommitmentSha256,
    IReadOnlyList<SessionJournalOfflineEventKindCount>
        EventKindCounts
);
