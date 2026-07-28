using System.Text;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal.Cli;
using Xunit;
using Xunit.Sdk;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.Cli.Tests;

public sealed class ProgramLegacyImportSafetyTests : IDisposable {
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "atelia-session-journal-import-safety",
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
    public void UnsupportedRevertTurnFailsBeforeReplacingTarget() {
        Directory.CreateDirectory(_tempRoot);
        string inputPath = Path.Combine(_tempRoot, "legacy.json");
        string outputPath = Path.Combine(_tempRoot, "existing-repo");
        Directory.CreateDirectory(outputPath);
        string markerPath = Path.Combine(outputPath, "keep.txt");
        File.WriteAllText(markerPath, "keep");
        WriteExport(
            inputPath,
            [
                InitialState(),
                new LegacyChatSessionEvent {
                    Ordinal = 1,
                    Kind = "revert-turn"
                }
            ]
        );

        int exitCode = Program.MainCore(
            [
                "import-legacy-json",
                "--input", inputPath,
                "--output", outputPath,
                "--force"
            ],
            ThrowingCompletionClientFactory.Instance
        );

        Assert.Equal(1, exitCode);
        Assert.Equal("keep", File.ReadAllText(markerPath));
    }

    [Fact]
    public void InitialStateMessagesAreImportedInsteadOfSilentlyDropped() {
        Directory.CreateDirectory(_tempRoot);
        string inputPath = Path.Combine(_tempRoot, "legacy.json");
        string outputPath = Path.Combine(_tempRoot, "session-journal");
        LegacyChatSessionEvent initial = InitialState() with {
            Messages = [
                new LegacyChatSessionMessage {
                    Kind = "observation",
                    Content = "initial observation"
                },
                new LegacyChatSessionMessage {
                    Kind = "action",
                    Action = new LegacyChatSessionAction {
                        Blocks = [
                            new SerializedActionBlock(
                                ActionMessageSerialization.BlockKindText,
                                "initial action",
                                ToolName: null,
                                ToolCallId: null,
                                RawArgumentsJson: null,
                                Reasoning: null
                            )
                        ]
                    }
                }
            ]
        };
        WriteExport(inputPath, [initial]);

        int exitCode = Program.MainCore(
            [
                "import-legacy-json",
                "--input", inputPath,
                "--output", outputPath
            ],
            ThrowingCompletionClientFactory.Instance
        );

        Assert.Equal(0, exitCode);
        Assert.Equal(
            """{"v":2,"body":{"origin":"legacy-import"}}""",
            ReadPayloadJson(
                outputPath,
                SJ.SessionEventKind.SessionCreated
            )
        );
        using var engine = SJ.SessionJournalEngine.Open(outputPath);
        Assert.Collection(
            engine.Project().Context,
            message => Assert.Equal(
                "initial observation",
                Assert.IsType<ObservationMessage>(message).Content
            ),
            message => Assert.Equal(
                "initial action",
                Assert.IsType<ActionMessage>(message)
                    .GetFlattenedText()
            )
        );
    }

    [Fact]
    public void ForceRejectsSymlinkTargetWithoutDeletingTarget() {
        Directory.CreateDirectory(_tempRoot);
        string inputPath = Path.Combine(_tempRoot, "legacy.json");
        WriteExport(inputPath, [InitialState()]);
        string realTarget = Path.Combine(_tempRoot, "real-target");
        Directory.CreateDirectory(realTarget);
        string markerPath = Path.Combine(realTarget, "keep.txt");
        File.WriteAllText(markerPath, "keep");
        string targetAlias = Path.Combine(_tempRoot, "target-alias");
        try {
            Directory.CreateSymbolicLink(targetAlias, realTarget);
        }
        catch (Exception ex) when (
            ex is IOException
                or NotSupportedException
                or UnauthorizedAccessException
        ) {
            throw SkipException.ForSkip(
                $"Directory symbolic links are unavailable: {ex.Message}"
            );
        }

        int exitCode = Program.MainCore(
            [
                "import-legacy-json",
                "--input", inputPath,
                "--output", targetAlias,
                "--force"
            ],
            ThrowingCompletionClientFactory.Instance
        );

        Assert.Equal(1, exitCode);
        Assert.Equal("keep", File.ReadAllText(markerPath));
    }

    [Fact]
    public void ToolCallHistoryFailsBeforeReplacingTarget() {
        Directory.CreateDirectory(_tempRoot);
        string inputPath = Path.Combine(_tempRoot, "legacy.json");
        string outputPath = Path.Combine(_tempRoot, "existing-repo");
        Directory.CreateDirectory(outputPath);
        string markerPath = Path.Combine(outputPath, "keep.txt");
        File.WriteAllText(markerPath, "keep");
        WriteExport(
            inputPath,
            [
                InitialState(),
                new LegacyChatSessionEvent {
                    Ordinal = 1,
                    Kind = LegacyChatSessionEventKinds.ModelTurn,
                    AppendedMessages = [
                        new LegacyChatSessionMessage {
                            Kind = "observation",
                            Content = "use a tool"
                        },
                        new LegacyChatSessionMessage {
                            Kind = "action",
                            Action = new LegacyChatSessionAction {
                                Blocks = [
                                    new SerializedActionBlock(
                                        ActionMessageSerialization
                                            .BlockKindToolCall,
                                        Content: null,
                                        ToolName: "workspace.echo",
                                        ToolCallId: "call-1",
                                        RawArgumentsJson: "{}",
                                        Reasoning: null
                                    )
                                ]
                            }
                        }
                    ]
                }
            ]
        );

        int exitCode = Program.MainCore(
            [
                "import-legacy-json",
                "--input", inputPath,
                "--output", outputPath,
                "--force"
            ],
            ThrowingCompletionClientFactory.Instance
        );

        Assert.Equal(1, exitCode);
        Assert.Equal("keep", File.ReadAllText(markerPath));
    }

    [Fact]
    public void MalformedActionBlockFailsBeforeReplacingTarget() {
        Directory.CreateDirectory(_tempRoot);
        string inputPath = Path.Combine(_tempRoot, "legacy.json");
        string outputPath = Path.Combine(_tempRoot, "existing-repo");
        Directory.CreateDirectory(outputPath);
        string markerPath = Path.Combine(outputPath, "keep.txt");
        File.WriteAllText(markerPath, "keep");
        WriteExport(
            inputPath,
            [
                InitialState(),
                new LegacyChatSessionEvent {
                    Ordinal = 1,
                    Kind = LegacyChatSessionEventKinds.ModelTurn,
                    AppendedMessages = [
                        new LegacyChatSessionMessage {
                            Kind = "observation",
                            Content = "future action"
                        },
                        new LegacyChatSessionMessage {
                            Kind = "action",
                            Action = new LegacyChatSessionAction {
                                Blocks = [
                                    new SerializedActionBlock(
                                        "future-block-kind",
                                        Content: null,
                                        ToolName: null,
                                        ToolCallId: null,
                                        RawArgumentsJson: null,
                                        Reasoning: null
                                    )
                                ]
                            }
                        }
                    ]
                }
            ]
        );

        int exitCode = Program.MainCore(
            [
                "import-legacy-json",
                "--input", inputPath,
                "--output", outputPath,
                "--force"
            ],
            ThrowingCompletionClientFactory.Instance
        );

        Assert.Equal(1, exitCode);
        Assert.Equal("keep", File.ReadAllText(markerPath));
    }

    [Fact]
    public void ForceFalseDoesNotReplaceNonEmptyTarget() {
        Directory.CreateDirectory(_tempRoot);
        string inputPath = Path.Combine(_tempRoot, "legacy.json");
        string outputPath = Path.Combine(_tempRoot, "existing-repo");
        Directory.CreateDirectory(outputPath);
        string markerPath = Path.Combine(outputPath, "keep.txt");
        File.WriteAllText(markerPath, "keep");
        WriteExport(inputPath, [InitialState()]);

        int exitCode = Program.MainCore(
            [
                "import-legacy-json",
                "--input", inputPath,
                "--output", outputPath,
                "--force", "false"
            ],
            ThrowingCompletionClientFactory.Instance
        );

        Assert.Equal(1, exitCode);
        Assert.Equal("keep", File.ReadAllText(markerPath));
    }

    [Fact]
    public void ReportPathCannotOverwriteLegacyInput() {
        Directory.CreateDirectory(_tempRoot);
        string inputPath = Path.Combine(_tempRoot, "legacy.json");
        string outputPath = Path.Combine(_tempRoot, "session-journal");
        WriteExport(inputPath, [InitialState()]);
        string originalJson = File.ReadAllText(inputPath);

        int exitCode = Program.MainCore(
            [
                "import-legacy-json",
                "--input", inputPath,
                "--output", outputPath,
                "--report-md", inputPath
            ],
            ThrowingCompletionClientFactory.Instance
        );

        Assert.Equal(1, exitCode);
        Assert.Equal(originalJson, File.ReadAllText(inputPath));
        Assert.False(Directory.Exists(outputPath));
    }

    [Fact]
    public void ReportPathCannotBeAnAncestorOfOutput() {
        Directory.CreateDirectory(_tempRoot);
        string inputPath = Path.Combine(_tempRoot, "legacy.json");
        string reportPath = Path.Combine(_tempRoot, "report.md");
        string outputPath = Path.Combine(reportPath, "session-journal");
        WriteExport(inputPath, [InitialState()]);

        int exitCode = Program.MainCore(
            [
                "import-legacy-json",
                "--input", inputPath,
                "--output", outputPath,
                "--report-md", reportPath
            ],
            ThrowingCompletionClientFactory.Instance
        );

        Assert.Equal(1, exitCode);
        Assert.False(Directory.Exists(reportPath));
        Assert.False(Directory.Exists(outputPath));
    }

    [Fact]
    public void ForcePublishesVerifiedRepositoryOverExistingTarget() {
        Directory.CreateDirectory(_tempRoot);
        string inputPath = Path.Combine(_tempRoot, "legacy.json");
        string outputPath = Path.Combine(_tempRoot, "existing-repo");
        Directory.CreateDirectory(outputPath);
        string markerPath = Path.Combine(outputPath, "replace-me.txt");
        File.WriteAllText(markerPath, "replace me");
        WriteExport(inputPath, [InitialState()]);

        int exitCode = Program.MainCore(
            [
                "import-legacy-json",
                "--input", inputPath,
                "--output", outputPath,
                "--force"
            ],
            ThrowingCompletionClientFactory.Instance
        );

        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(markerPath));
        using var engine = SJ.SessionJournalEngine.Open(outputPath);
        Assert.NotNull(engine.Project().Head);
        Assert.Empty(
            Directory.EnumerateDirectories(
                _tempRoot,
                ".existing-repo.*",
                SearchOption.TopDirectoryOnly
            )
        );
    }

    private static LegacyChatSessionEvent InitialState()
        => new() {
            Ordinal = 0,
            Kind = LegacyChatSessionEventKinds.InitialState,
            Root = new LegacyChatSessionRoot {
                ApiSpecId = "legacy-upgrade-export",
                CompletionSurfaceId = "surface-a",
                ModelId = "model-a",
                SystemPrompt = "system-a"
            }
        };

    private static void WriteExport(
        string path,
        IReadOnlyList<LegacyChatSessionEvent> events
    ) {
        var export = new LegacyChatSessionExport {
            Schema = LegacyChatSessionExportSchema.SchemaId,
            BranchName = "main",
            Events = events
        };
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                export,
                LegacyChatSessionExportReader.JsonOptions
            )
        );
    }

    private static string ReadPayloadJson(
        string path,
        SJ.SessionEventKind kind
    ) {
        using var journal =
            Atelia.EventJournal.EventJournal.OpenExisting(path);
        RefId main = journal.OpenBranch(
            SJ.SessionJournalDefaults.MainBranchName
        ).Unwrap();
        EventAddress head = journal.GetHead(main)
            ?? throw new InvalidDataException(
                "Imported SessionJournal has no head."
            );
        EventAddress address = journal.ReadChronologicalChain(
                head,
                checkedRead: true
            )
            .Unwrap()
            .Single(candidate =>
                journal.ReadEventHeaderPreview(candidate)
                    .Unwrap()
                    .OpaqueEventKind == (uint)kind
            );
        using EventFrame frame = journal.ReadEvent(address).Unwrap();
        return Encoding.UTF8.GetString(frame.Payload);
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
