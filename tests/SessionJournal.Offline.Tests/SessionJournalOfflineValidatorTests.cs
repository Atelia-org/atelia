using System.Security.Cryptography;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Offline.Tests;

public sealed class SessionJournalOfflineValidatorTests
    : IDisposable {
    private readonly List<string> _paths = [];

    [Fact]
    public async Task EmptyBranch_IsValidWithoutSetupOrHistory() {
        string path = NewPath();
        using (EventJournal.EventJournal journal =
               EventJournal.EventJournal.CreateNew(path)) {
            _ = journal.CreateBranch(
                SessionJournalDefaults.MainBranchName,
                startPoint: null
            ).Unwrap();
        }

        SessionJournalOfflineValidationReport report =
            await SessionJournalOfflineValidator.ValidateAsync(path);

        Assert.Null(report.Head);
        Assert.Equal(SessionExecutionPhase.Empty, report.ExecutionPhase);
        Assert.Equal(0, report.EventCount);
        Assert.Equal(0, report.HistoryContributionCount);
        Assert.Equal(
            SessionHistorySemanticCommitment.ComputeSequenceSha256(
                []
            ),
            report.HistorySemanticCommitmentSha256
        );
        Assert.Equal(
            SessionHistorySemanticCommitment.CodecId,
            report.HistorySemanticCommitmentCodecId
        );
        Assert.Empty(report.EventKindCounts);
        Assert.Equal(0, report.ScanDiagnostics.RepositoryEventReadCount);
    }

    [Fact]
    public async Task SelectedBranch_ReportsExactRefHeadAndHistory() {
        string path = NewPath();
        EventAddress forkPoint;
        using (var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-main",
                "system-main",
                "surface-main"
            )
        )) {
            forkPoint = engine.InspectExecutionBoundary().Head!.Value;
            engine.AppendObservation("main observation");
            _ = engine.AppendImportedAgentAction(
                TextAction("main action"),
                ImportDescriptor()
            );
        }
        RefId featureRef;
        using (EventJournal.EventJournal journal =
               EventJournal.EventJournal.OpenExisting(path)) {
            featureRef = journal.CreateBranch(
                "feature",
                forkPoint
            ).Unwrap();
        }
        EventAddress featureHead;
        using (var featureEngine = SessionJournalEngine.Open(
            path,
            "feature"
        )) {
            featureEngine.AppendObservation("feature observation");
            featureHead = featureEngine.AppendImportedAgentAction(
                TextAction("feature action"),
                ImportDescriptor()
            );
        }

        SessionJournalOfflineValidationReport main =
            await SessionJournalOfflineValidator.ValidateAsync(path);
        SessionJournalOfflineValidationReport feature =
            await SessionJournalOfflineValidator.ValidateAsync(
                path,
                "feature"
            );

        Assert.Equal(
            SessionJournalDefaults.MainBranchName,
            main.BranchName
        );
        Assert.Equal("feature", feature.BranchName);
        Assert.Equal(featureRef.ToHexString(), feature.BranchRefId);
        Assert.Equal(
            EventAddressTextCodec.Format(featureHead),
            feature.Head
        );
        Assert.Equal(1, feature.ObservationCount);
        Assert.Equal(1, feature.AgentActionCount);
        Assert.Equal(1, feature.ImportedAgentActionCount);
        Assert.Equal(2, feature.HistoryContributionCount);
        Assert.Equal(SessionExecutionPhase.Idle, feature.ExecutionPhase);
        Assert.NotEqual(main.Head, feature.Head);
        Assert.NotEqual(
            main.HistorySemanticCommitmentSha256,
            feature.HistorySemanticCommitmentSha256
        );
        Assert.Equal(
            feature.EventCount,
            feature.ScanDiagnostics.RepositoryEventReadCount
        );
    }

    [Fact]
    public async Task ReportCommitment_IsDeterministicAcrossReopen() {
        string path = NewPath();
        using (var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        )) {
            engine.AppendObservation("observation");
            _ = engine.AppendImportedAgentAction(
                TextAction("action"),
                ImportDescriptor()
            );
        }

        SessionJournalOfflineValidationReport first =
            await SessionJournalOfflineValidator.ValidateAsync(path);
        SessionJournalOfflineValidationReport second =
            await SessionJournalOfflineValidator.ValidateAsync(path);

        Assert.Equal(
            first.HistorySemanticCommitmentSha256,
            second.HistorySemanticCommitmentSha256
        );
        Assert.Equal(
            first.EventKindCounts,
            second.EventKindCounts
        );
        Assert.Equal(first.ExecutionPhase, second.ExecutionPhase);
        Assert.Equal(first.HeadKind, second.HeadKind);
        Assert.Equal(
            first.ToolExecutionSequenceCheckpoint,
            second.ToolExecutionSequenceCheckpoint
        );
    }

    [Fact]
    public async Task SemanticCommitment_IgnoresAddressAndRawMetadata() {
        string firstPath = NewPath();
        string shiftedPath = NewPath();
        string changedPath = NewPath();
        CreateSemanticHistory(
            firstPath,
            shiftAddresses: false,
            actionText: "same action",
            providerId: "provider-A"
        );
        CreateSemanticHistory(
            shiftedPath,
            shiftAddresses: true,
            actionText: "same action",
            providerId: "provider-B"
        );
        CreateSemanticHistory(
            changedPath,
            shiftAddresses: true,
            actionText: "changed action",
            providerId: "provider-B"
        );

        SessionJournalOfflineValidationReport first =
            await SessionJournalOfflineValidator.ValidateAsync(
                firstPath
            );
        SessionJournalOfflineValidationReport shifted =
            await SessionJournalOfflineValidator.ValidateAsync(
                shiftedPath
            );
        SessionJournalOfflineValidationReport changed =
            await SessionJournalOfflineValidator.ValidateAsync(
                changedPath
            );

        Assert.NotEqual(first.Head, shifted.Head);
        Assert.Equal(
            first.HistorySemanticCommitmentSha256,
            shifted.HistorySemanticCommitmentSha256
        );
        Assert.NotEqual(
            first.HistorySemanticCommitmentSha256,
            changed.HistorySemanticCommitmentSha256
        );
    }

    [Fact]
    public async Task TenThousandColdSetupEvents_AreReadOnceEach() {
        const int setupUpdateCount = 10_000;
        string path = NewPath();
        byte[] setupPayload;
        EventAddress head;
        using (var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-A",
                "system-0",
                "surface-A"
            )
        )) {
            SessionCurrentLineageHeader setup =
                Assert.Single(
                    engine.ReadCurrentLineageHeaders()
                        .HeadToRoot,
                    static entry =>
                        entry.Kind
                            == SessionEventKind.SystemPromptSetup
                );
            setupPayload = engine.ReadPayloadBytes(setup.Address);
            head = engine.InspectExecutionBoundary().Head!.Value;
        }
        using (EventJournal.EventJournal journal =
               EventJournal.EventJournal.OpenExisting(path)) {
            RefId main = journal.OpenBranch(
                SessionJournalDefaults.MainBranchName
            ).Unwrap();
            EventAddress originalHead = head;
            for (int index = 0; index < setupUpdateCount; index++) {
                head = journal.AppendEventFrame(
                    head,
                    setupPayload,
                    opaqueEventKind:
                        (uint)SessionEventKind.SystemPromptSetup
                ).Unwrap();
            }
            Assert.True(
                journal.MoveRef(main, originalHead, head).Unwrap()
            );
        }

        SessionJournalOfflineValidationReport report =
            await SessionJournalOfflineValidator.ValidateAsync(path);

        Assert.Equal(setupUpdateCount + 3, report.EventCount);
        Assert.Equal(
            report.EventCount,
            report.ScanDiagnostics.RepositoryEventReadCount
        );
        Assert.Equal(
            report.EventCount,
            report.ScanDiagnostics.CapturedEventCount
        );
        Assert.Equal(
            0,
            report.ScanDiagnostics.PreparedReconstructionCount
        );
        Assert.Equal(
            SessionJournalOfflineValidator
                .SystemPromptUtf8Sha256CodecId,
            report.SystemPromptUtf8Sha256CodecId
        );
        Assert.Equal(
            Convert.ToHexStringLower(
                SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes("system-0")
                )
            ),
            report.SystemPromptUtf8Sha256
        );
    }

    public void Dispose() {
        foreach (string path in _paths) {
            try {
                if (Directory.Exists(path)) {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch {
                // Best-effort cleanup for test-owned repositories.
            }
        }
    }

    private string NewPath() {
        string tempRoot = Directory.Exists("/dev/shm")
            ? "/dev/shm"
            : Path.GetTempPath();
        string path = Path.Combine(
            tempRoot,
            "atelia-session-offline-tests",
            Guid.NewGuid().ToString("N")
        );
        _paths.Add(path);
        return path;
    }

    private static ActionMessage TextAction(string text) =>
        new([new ActionBlock.Text(text)]);

    private static CompletionDescriptor ImportDescriptor() =>
        new("import", "import-v1", "model-A");

    private static void CreateSemanticHistory(
        string path,
        bool shiftAddresses,
        string actionText,
        string providerId
    ) {
        using var engine = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        );
        if (shiftAddresses) {
            _ = engine.AppendSystemPromptSetup("system-A");
        }
        engine.AppendObservation("same observation");
        _ = engine.AppendImportedAgentAction(
            TextAction(actionText),
            new CompletionDescriptor(
                providerId,
                "different-api-metadata",
                "different-model-metadata"
            )
        );
    }
}
