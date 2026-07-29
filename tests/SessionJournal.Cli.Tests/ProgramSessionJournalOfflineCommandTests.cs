using System.Security.Cryptography;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.DerivedMemory;
using Atelia.SessionJournal.Offline;
using Atelia.SessionJournal.Cli;
using SJ = Atelia.SessionJournal;
using Xunit;

namespace Atelia.SessionJournal.Cli.Tests;

public sealed class ProgramSessionJournalOfflineCommandTests : IDisposable {
    private static readonly JsonSerializerOptions WebJsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "atelia-session-offline-cli-tests",
        Guid.NewGuid().ToString("N")
    );

    public void Dispose() {
        try {
            if (Directory.Exists(_tempRoot)) {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch {
            // Best-effort cleanup for temp test directories.
        }
    }

    [Fact]
    public void ValidateSessionJournal_IsReadOnlyAndReportsRawState() {
        string repoPath = CreateJournal();
        string reportPath = Path.Combine(_tempRoot, "validation.json");
        IReadOnlyDictionary<string, string> before =
            CaptureRepositoryFileHashes(repoPath);

        int exitCode = Program.MainCore(
            [
                "validate",
                "--input", repoPath,
                "--report-json", reportPath
            ],
            ThrowingCompletionClientFactory.Instance
        );

        Assert.Equal(0, exitCode);
        Assert.Equal(before, CaptureRepositoryFileHashes(repoPath));
        Assert.True(File.Exists(reportPath));
        string reportJson = File.ReadAllText(reportPath);
        Assert.Contains(
            "\"preparedRequestCount\"",
            reportJson,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "\"preparedPolicyCounts\"",
            reportJson,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "\"executionState\"",
            reportJson,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "\"systemPrompt\"",
            reportJson,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "system-A",
            reportJson,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "\"rawArgumentsJson\"",
            reportJson,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "\"operationId\"",
            reportJson,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "\"activeCorrelationId\"",
            reportJson,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "\"historyCommitmentSha256\"",
            reportJson,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "\"historySemanticCommitmentCodecId\":"
                + "\"atelia.session-journal."
                + "history-semantic-commitment.v1\"",
            reportJson,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "\"systemPromptUtf8Sha256CodecId\":"
                + "\"atelia.utf8-text.sha256.v1\"",
            reportJson,
            StringComparison.Ordinal
        );
        SessionJournalOfflineValidationReport report =
            JsonSerializer.Deserialize<SessionJournalOfflineValidationReport>(
                reportJson,
                WebJsonOptions
            ) ?? throw new Xunit.Sdk.XunitException(
                "Validation report did not deserialize."
            );
        Assert.Equal(SessionExecutionPhase.Idle, report.ExecutionPhase);
        Assert.True(report.EventCount >= 5);
        Assert.True(report.LogicalPayloadBytes > 0);
        Assert.Equal(0, report.PreparedRequestCount);
    }

    [Fact]
    public void ValidateSessionJournal_SelectsExactNonMainBranch() {
        string repoPath = CreateJournal();
        EventAddress mainHead;
        RefId featureRef;
        using (EventJournal.EventJournal journal =
               EventJournal.EventJournal.OpenExisting(repoPath)) {
            RefId main = journal.OpenBranch(
                SessionJournalDefaults.MainBranchName
            ).Unwrap();
            mainHead = journal.GetHead(main)!.Value;
            featureRef = journal.CreateBranch(
                "feature",
                mainHead
            ).Unwrap();
        }
        EventAddress featureHead;
        using (SessionJournalEngine feature =
               SessionJournalEngine.Open(repoPath, "feature")) {
            feature.AppendObservation("feature observation");
            featureHead = feature.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text("feature action")
                ]),
                new CompletionDescriptor(
                    "import",
                    "import-v1",
                    "model-A"
                )
            );
        }
        string reportPath = Path.Combine(
            _tempRoot,
            "feature-validation.json"
        );

        int exitCode = Program.MainCore(
            [
                "validate",
                "--input", repoPath,
                "--branch", "feature",
                "--report-json", reportPath
            ],
            ThrowingCompletionClientFactory.Instance
        );

        Assert.Equal(0, exitCode);
        SessionJournalOfflineValidationReport report =
            JsonSerializer.Deserialize<
                SessionJournalOfflineValidationReport
            >(
                File.ReadAllText(reportPath),
                WebJsonOptions
            ) ?? throw new Xunit.Sdk.XunitException(
                "Validation report did not deserialize."
            );
        Assert.Equal("feature", report.BranchName);
        Assert.Equal(featureRef.ToHexString(), report.BranchRefId);
        Assert.Equal(
            EventAddressTextCodec.Format(featureHead),
            report.Head
        );
        Assert.NotEqual(
            EventAddressTextCodec.Format(mainHead),
            report.Head
        );
        Assert.Equal(2, report.ObservationCount);
        Assert.Equal(2, report.AgentActionCount);
    }

    [Fact]
    public async Task ValidateSessionJournal_ReportDoesNotExposeSensitiveReplayState() {
        const string systemPrompt = "secret-system-prompt-value";
        const string rawArguments =
            """{"secretArgument":"private-value"}""";
        string repoPath = Path.Combine(
            _tempRoot,
            Guid.NewGuid().ToString("N")
        );
        using var cancellation = new CancellationTokenSource();
        var tool = new CancellingTool(cancellation);
        var runtime = new SessionRuntime(
            NeverCalledCompletionClient.Instance,
            new ToolRegistry([tool]).CreateSession(),
            ToolRuntimeIdentity: new SessionToolRuntimeIdentity(
                "privacy-host",
                "privacy-implementations-v1",
                "privacy-capabilities-v1"
            )
        );
        using (var engine = SessionJournalEngine.Create(
            repoPath,
            new SessionCreateOptions(
                "model-A",
                systemPrompt,
                "surface-A"
            ),
            runtime
        )) {
            engine.AppendObservation("run private tool");
            engine.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.ToolCall(
                        new RawToolCall(
                            "privacy-tool",
                            "private-call-id",
                            rawArguments
                        )
                    )
                ]),
                new CompletionDescriptor(
                    "private-provider",
                    "private-api-v1",
                    "model-A"
                )
            );

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => engine.ResumeAsync(cancellation.Token)
            );
        }

        string? capturedPrompt = null;
        string? capturedRawArguments = null;
        string? capturedOperationId = null;
        string? capturedCorrelationId = null;
        using (SessionJournalEngine inspection =
               SessionJournalEngine.OpenReadOnly(repoPath)) {
            inspection.ScanCheckedAuditEvents(
                auditEvent => {
                    switch (auditEvent.Fact) {
                        case SessionJournalAuditSystemPromptFact prompt:
                            capturedPrompt = prompt.SystemPrompt;
                            break;
                        case SessionJournalAuditActionFact action:
                            capturedCorrelationId = action.CorrelationId;
                            break;
                        case SessionJournalAuditToolExecutionStartedFact started:
                            capturedRawArguments = started.RawArgumentsJson;
                            capturedOperationId = started.OperationId;
                            break;
                    }
                }
            );
        }

        Assert.Equal(systemPrompt, capturedPrompt);
        Assert.Equal(rawArguments, capturedRawArguments);
        Assert.False(string.IsNullOrWhiteSpace(capturedOperationId));
        Assert.False(string.IsNullOrWhiteSpace(capturedCorrelationId));

        string reportPath = Path.Combine(
            _tempRoot,
            "privacy-validation.json"
        );
        int exitCode = Program.MainCore(
            [
                "validate",
                "--input", repoPath,
                "--report-json", reportPath
            ],
            ThrowingCompletionClientFactory.Instance
        );

        Assert.Equal(0, exitCode);
        string reportJson = File.ReadAllText(reportPath);
        Assert.DoesNotContain(systemPrompt, reportJson, StringComparison.Ordinal);
        Assert.DoesNotContain(
            rawArguments,
            reportJson,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            capturedOperationId,
            reportJson,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            capturedCorrelationId,
            reportJson,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "\"systemPrompt\"",
            reportJson,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "\"rawArgumentsJson\"",
            reportJson,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "\"operationId\"",
            reportJson,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "\"activeCorrelationId\"",
            reportJson,
            StringComparison.Ordinal
        );
    }

    private string CreateJournal() {
        string repoPath = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        using var engine = SessionJournalEngine.Create(
            repoPath,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        );
        engine.AppendObservation("old observation");
        EventAddress anchor = engine.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("old action")]),
            new CompletionDescriptor("import", "import-v1", "model-A")
        );
        _ = anchor;
        return repoPath;
    }

    private static IReadOnlyDictionary<string, string> CaptureRepositoryFileHashes(
        string path
    ) {
        if (!Directory.Exists(path)) {
            return new SortedDictionary<string, string>(
                StringComparer.Ordinal
            );
        }
        return Directory.EnumerateFiles(
                path,
                "*",
                SearchOption.AllDirectories
            )
            .ToDictionary(
                file => Path.GetRelativePath(path, file),
                file => Convert.ToHexStringLower(
                    SHA256.HashData(File.ReadAllBytes(file))
                ),
                StringComparer.Ordinal
            );
    }

    private sealed class ThrowingCompletionClientFactory
        : ICompletionClientFactory {
        public static ThrowingCompletionClientFactory Instance { get; } = new();

        public ICompletionClient Create(CompletionConnectionConfig connection)
            => throw new InvalidOperationException(
                $"Offline command must not create completion client '{connection.Id}'."
            );
    }

    private sealed class NeverCalledCompletionClient : ICompletionClient {
        public static NeverCalledCompletionClient Instance { get; } = new();

        public string Name => "never-called";

        public string ApiSpecId => "never-called-v1";

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException(
            "Pending-tool privacy fixture must stop before completion."
        );
    }

    private sealed class CancellingTool(
        CancellationTokenSource cancellation
    ) : ITool {
        public ToolDefinition Definition { get; } = new(
            "privacy-tool",
            "Privacy regression fixture.",
            new ToolSchema.Object()
        );

        public ValueTask<ToolExecuteResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken
        ) {
            _ = context;
            _ = cancellationToken;
            cancellation.Cancel();
            return ValueTask.FromResult(
                ToolExecuteResult.FromText(
                    ToolExecutionStatus.Success,
                    "secret-tool-result"
                )
            );
        }
    }
}
