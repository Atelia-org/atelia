using Atelia.ChatSession;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.Cli;
using Xunit;
using SJ = Atelia.SessionJournal;

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
    public void ChatSessionExporterJsonImportsIntoSessionJournalCli() {
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
        using var engine = SJ.SessionJournalEngine.Open(
            sessionJournalRepo
        );
        SJ.SessionProjection projection = engine.Project();
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
