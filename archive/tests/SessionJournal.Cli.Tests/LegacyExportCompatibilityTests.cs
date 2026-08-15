using System.Text.Json;
using Atelia.ChatSession;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal.Cli;
using Xunit;
using SJ = Atelia.SessionJournal;
using SJO = Atelia.SessionJournal.Offline;

namespace Atelia.SessionJournal.Cli.Tests;

public sealed class LegacyExportCompatibilityTests : IDisposable {
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "atelia-legacy-export-compatibility",
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
    public async Task ChatSessionExporterJsonImportsIntoSessionJournalCli() {
        Directory.CreateDirectory(_tempRoot);
        string sourceJson = Path.Combine(_tempRoot, "source.json");
        string legacyRepo = Path.Combine(_tempRoot, "legacy-repo");
        string exportedJson = Path.Combine(_tempRoot, "exported.json");
        string sessionJournalRepo =
            Path.Combine(_tempRoot, "session-journal");
        string importReport =
            Path.Combine(_tempRoot, "import-report.md");
        File.WriteAllText(
            sourceJson,
            """
            {
              "schema": "atelia.chat-session.legacy-upgrade-export.v1",
              "branchName": "main",
              "events": [
                {
                  "ordinal": 0,
                  "commit": "initial",
                  "kind": "initial-state",
                  "root": {
                    "kind": "chat-session",
                    "schemaVersion": 2,
                    "apiSpecId": "legacy-upgrade-export",
                    "completionSurfaceId": "surface-a",
                    "modelId": "model-a",
                    "systemPrompt": "system-a"
                  },
                  "messages": []
                },
                {
                  "ordinal": 1,
                  "commit": "turn-1",
                  "kind": "model-turn",
                  "appendedMessages": [
                    {
                      "kind": "observation",
                      "content": "hello"
                    },
                    {
                      "kind": "action",
                      "action": {
                        "flattenedText": "world",
                        "blocks": [
                          {
                            "kind": "text",
                            "content": "world"
                          }
                        ]
                      }
                    }
                  ]
                }
              ]
            }
            """
        );
        _ = ChatSessionLegacyEventSourceImporter.Import(
            sourceJson,
            legacyRepo
        );
        File.WriteAllText(
            exportedJson,
            ChatSessionLegacyUpgradeExporter.ExportJson(legacyRepo)
        );

        int exitCode = Program.MainCore(
            [
                "import-legacy-json",
                "--input", exportedJson,
                "--output", sessionJournalRepo,
                "--report-md", importReport
            ],
            ThrowingCompletionClientFactory.Instance
        );

        Assert.Equal(0, exitCode);
        SJO.SessionJournalOfflineValidationReport report =
            await SJO.SessionJournalOfflineValidator.ValidateAsync(
                sessionJournalRepo
            );
        int runtimeBodySchemaVersion;
        int runtimeNthPrevious;
        using (var engine = SJ.SessionJournalEngine.OpenReadOnly(
            sessionJournalRepo
        )) {
            SJ.SessionExecutionBoundaryInspection boundary =
                engine.InspectExecutionBoundary();
            Assert.Equal(SJ.SessionExecutionPhase.Idle, boundary.Phase);
            Assert.Equal(
                SJ.SessionEventKind.ImportedAgentAction,
                boundary.HeadKind
            );
            SJ.SessionGoverningSetup governing =
                engine.ResolveGoverningSetup(
                    boundary.Head
                    ?? throw new InvalidDataException(
                        "Imported SessionJournal has no head."
                    )
                );
            Assert.Equal("model-a", governing.RuntimeConfig.ModelId);
            Assert.Equal(
                "surface-a",
                governing.RuntimeConfig.CompletionSurfaceId
            );
            Assert.Equal("system-a", governing.SystemPrompt);
            runtimeNthPrevious =
                governing.RuntimeConfig.DerivedContext.NthPrevious;
            using JsonDocument runtimePayload = JsonDocument.Parse(
                engine.ReadPayloadBytes(
                    governing.RuntimeConfigSetupAddress
                )
            );
            runtimeBodySchemaVersion = runtimePayload.RootElement
                .GetProperty("v")
                .GetInt32();
        }
        Assert.Equal(2, runtimeBodySchemaVersion);
        Assert.Equal(0, runtimeNthPrevious);
        Assert.NotNull(report.RuntimeConfig);
        Assert.Equal(
            0,
            report.RuntimeConfig.DerivedContext.NthPrevious
        );
        string observationHash =
            SJ.SessionHistorySemanticCommitment
                .ComputeObservationContributionSha256(
                    new ObservationMessage("hello")
                );
        string actionHash =
            SJ.SessionHistorySemanticCommitment
                .ComputeActionContributionSha256(
                    new ActionMessage([
                        new ActionBlock.Text("world")
                    ])
                );
        Assert.Equal(
            SJ.SessionHistorySemanticCommitment.ComputeSequenceSha256(
                [observationHash, actionHash]
            ),
            report.HistorySemanticCommitmentSha256
        );
        string reportMarkdown = File.ReadAllText(importReport);
        Assert.DoesNotContain(
            "system-a",
            reportMarkdown,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "hello",
            reportMarkdown,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "world",
            reportMarkdown,
            StringComparison.Ordinal
        );
        using EventJournal.EventJournal journal =
            EventJournal.EventJournal.OpenReadOnlyExisting(
                sessionJournalRepo
            );
        RefId main = journal.OpenBranch(
            SJ.SessionJournalDefaults.MainBranchName
        ).Unwrap();
        EventAddress head = journal.GetHead(main)!.Value;
        Assert.All(
            journal.ReadChronologicalChain(head, checkedRead: true).Unwrap(),
            address => Assert.True(
                Enum.IsDefined(
                    typeof(SJ.SessionEventKind),
                    journal.ReadEventHeaderPreview(address)
                        .Unwrap()
                        .OpaqueEventKind
                ),
                $"Imported raw event at {address} has an unknown kind."
            )
        );
    }

    private sealed class ThrowingCompletionClientFactory
        : ICompletionClientFactory {
        public static ThrowingCompletionClientFactory Instance { get; } =
            new();

        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) => throw new InvalidOperationException(
            $"Import must not create completion client '{connection.Id}'."
        );
    }
}
