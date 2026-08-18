using System.Security.Cryptography;
using System.Text;

namespace Atelia.SessionJournal.Offline;

/// <summary>
/// Full, read-only validation for administration, migration, and repair
/// tooling. It consumes normalized facts from the core checked audit scan and
/// never materializes an LLM context or addressed message history.
/// </summary>
public static class SessionJournalOfflineValidator {
    public const string ReportSchema =
        "atelia.session-journal.offline-validation.v3";
    public const string SystemPromptUtf8Sha256CodecId =
        "atelia.utf8-text.sha256.v1";

    public static ValueTask<SessionJournalOfflineValidationReport>
        ValidateAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default
    ) => ValidateAsync(
        repositoryPath,
        SessionJournalDefaults.MainBranchName,
        cancellationToken
    );

    public static ValueTask<SessionJournalOfflineValidationReport>
        ValidateAsync(
        string repositoryPath,
        string branchName,
        CancellationToken cancellationToken = default
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        string fullPath = Path.GetFullPath(repositoryPath);

        using SessionJournalEngine engine =
            SessionJournalEngine.OpenReadOnly(fullPath, branchName);
        var fold = new SessionJournalOfflineForwardFold();
        SessionJournalAuditScanResult scan =
            engine.ScanCheckedAuditEvents(
                fold.Accept,
                cancellationToken
            );
        SessionJournalOfflineFoldResult folded = fold.Complete();

        if (folded.ExecutionState
            != scan.ExecutionStateAtCapturedHead) {
            throw new InvalidDataException(
                "Forward SessionJournal offline audit fold and "
                + "tail execution resolver disagree at captured head "
                + $"{EventAddressTextCodec.FormatNullable(scan.CapturedHead)}."
            );
        }

        SessionGoverningSetup? governingSetup = null;
        if (scan.CapturedHead is { } capturedHead) {
            governingSetup = engine.ResolveGoverningSetup(
                capturedHead,
                cancellationToken
            );
            if (folded.RuntimeConfig
                    != governingSetup.RuntimeConfig
                || !string.Equals(
                    folded.SystemPrompt,
                    governingSetup.SystemPrompt,
                    StringComparison.Ordinal
                )
                || folded.RuntimeConfigSetupAddress
                    != governingSetup.RuntimeConfigSetupAddress
                || folded.SystemPromptSetupAddress
                    != governingSetup.SystemPromptSetupAddress) {
                throw new InvalidDataException(
                    "Forward SessionJournal offline audit fold and "
                    + "authoritative governing setup resolver disagree "
                    + $"at captured head {capturedHead}."
                );
            }
        }

        return ValueTask.FromResult(
            new SessionJournalOfflineValidationReport(
                ReportSchema,
                fullPath,
                scan.BranchName,
                scan.BranchRefId.ToHexString(),
                EventAddressTextCodec.FormatNullable(
                    scan.CapturedHead
                ),
                scan.EventCount,
                scan.LogicalPayloadBytes,
                folded.ExecutionState.Phase,
                folded.ExecutionState.HeadKind,
                folded.ExecutionState
                    .ToolExecutionSequenceCheckpoint,
                governingSetup is null
                    ? null
                    : EventAddressTextCodec.Format(
                        governingSetup.RuntimeConfigSetupAddress
                    ),
                governingSetup is null
                    ? null
                    : EventAddressTextCodec.Format(
                        governingSetup.SystemPromptSetupAddress
                    ),
                governingSetup?.RuntimeConfig,
                SystemPromptUtf8Sha256CodecId,
                governingSetup is null
                    ? null
                    : Convert.ToHexStringLower(
                        SHA256.HashData(
                            Encoding.UTF8.GetBytes(
                                governingSetup.SystemPrompt
                            )
                        )
                    ),
                folded.PreparedRequestCount,
                folded.ObservationCount,
                folded.AgentActionCount,
                folded.ImportedAgentActionCount,
                folded.ToolResultHistoryCount,
                folded.HistoryContributionCount,
                folded.HistorySemanticCommitmentCodecId,
                folded.HistorySemanticCommitmentSha256,
                folded.EventKindCounts,
                scan.Diagnostics
            )
        );
    }
}
