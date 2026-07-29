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
                "--output", sessionJournalRepo
            ],
            ThrowingCompletionClientFactory.Instance
        );

        Assert.Equal(0, exitCode);
        SJ.SessionProjection projection;
        int runtimeBodySchemaVersion;
        int runtimeNthPrevious;
        using (var engine = SJ.SessionJournalEngine.Open(
            sessionJournalRepo
        )) {
            projection = engine.Project();
            SJ.SessionGoverningSetup governing =
                engine.ResolveGoverningSetup(
                    projection.Head
                    ?? throw new InvalidDataException(
                        "Imported SessionJournal has no head."
                    )
                );
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
        Assert.NotNull(projection.Config);
        Assert.Equal(0, projection.Config.DerivedContext.NthPrevious);
        Assert.Collection(
            projection.Context,
            message => Assert.Equal(
                "hello",
                Assert.IsType<ObservationMessage>(message).Content
            ),
            message => Assert.Equal(
                "world",
                Assert.IsType<ActionMessage>(message)
                    .GetFlattenedText()
            )
        );
        _ = await SJO.SessionJournalOfflineValidator.ValidateAsync(
            sessionJournalRepo
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
