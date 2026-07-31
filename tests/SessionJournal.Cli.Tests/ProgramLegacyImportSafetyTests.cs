using System.Text;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal.Cli;
using Xunit;
using Xunit.Sdk;
using SJ = Atelia.SessionJournal;
using SJO = Atelia.SessionJournal.Offline;

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
    public async Task InitialStateMessagesAreImportedInsteadOfSilentlyDropped() {
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
        SJO.SessionJournalOfflineValidationReport report =
            await SJO.SessionJournalOfflineValidator.ValidateAsync(
                outputPath
            );
        string expectedObservation =
            SJ.SessionHistorySemanticCommitment
                .ComputeObservationContributionSha256(
                    new ObservationMessage("initial observation")
                );
        string expectedAction =
            SJ.SessionHistorySemanticCommitment
                .ComputeActionContributionSha256(
                    new ActionMessage([
                        new ActionBlock.Text("initial action")
                    ])
                );
        Assert.Equal(
            SJ.SessionHistorySemanticCommitment.ComputeSequenceSha256(
                [expectedObservation, expectedAction]
            ),
            report.HistorySemanticCommitmentSha256
        );
        using var inspection =
            SJ.SessionJournalEngine.OpenReadOnly(outputPath);
        SJ.SessionExecutionBoundaryInspection boundary =
            inspection.InspectExecutionBoundary();
        Assert.Equal(SJ.SessionExecutionPhase.Idle, boundary.Phase);
        SJ.SessionGoverningSetup setup =
            inspection.ResolveGoverningSetup(boundary.Head!.Value);
        Assert.Equal("system-a", setup.SystemPrompt);
        Assert.Equal("model-a", setup.RuntimeConfig.ModelId);
        Assert.Equal(
            "surface-a",
            setup.RuntimeConfig.CompletionSurfaceId
        );
    }

    [Fact]
    public void JsonAndMarkdownReportsShareOneContentFreeImportEvidence() {
        Directory.CreateDirectory(_tempRoot);
        string inputPath = Path.Combine(_tempRoot, "legacy.json");
        string outputPath = Path.Combine(_tempRoot, "session-journal");
        string markdownPath = Path.Combine(_tempRoot, "import.md");
        string jsonPath = Path.Combine(_tempRoot, "import.json");
        const string secret = "SECRET-HISTORY-CONTENT";
        WriteExport(
            inputPath,
            [
                InitialState() with {
                    Messages = [
                        new LegacyChatSessionMessage {
                            Kind = "observation",
                            Content = secret
                        },
                        new LegacyChatSessionMessage {
                            Kind = "action",
                            Action = new LegacyChatSessionAction {
                                Blocks = [
                                    new SerializedActionBlock(
                                        ActionMessageSerialization
                                            .BlockKindText,
                                        secret,
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

        Assert.Equal(0, Program.MainCore(
            [
                "import-legacy-json",
                "--input", inputPath,
                "--output", outputPath,
                "--report-md", markdownPath,
                "--report-json", jsonPath
            ],
            ThrowingCompletionClientFactory.Instance
        ));

        string json = File.ReadAllText(jsonPath);
        string markdown = File.ReadAllText(markdownPath);
        Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, markdown, StringComparison.Ordinal);
        using JsonDocument report = JsonDocument.Parse(json);
        Assert.Equal(
            "atelia.session-journal.legacy-import-report.v1",
            report.RootElement.GetProperty("schema").GetString()
        );
        Assert.Equal(
            LegacyChatSessionExportSchema.SchemaId,
            report.RootElement.GetProperty("sourceSchema").GetString()
        );
        Assert.Equal(
            1,
            report.RootElement.GetProperty("observationCount")
                .GetInt32()
        );
        Assert.Equal(
            1,
            report.RootElement.GetProperty("agentActionCount")
                .GetInt32()
        );
        Assert.Empty(
            report.RootElement.GetProperty("warnings").EnumerateArray()
        );
        Assert.Equal(
            3,
            report.RootElement.GetProperty("mappings")
                .GetArrayLength()
        );
        Assert.Contains(
            $"- finalHead: `"
                + report.RootElement.GetProperty("finalHead").GetString(),
            markdown,
            StringComparison.Ordinal
        );
        Assert.Empty(Directory.EnumerateFiles(
            _tempRoot,
            ".*.tmp",
            SearchOption.AllDirectories
        ));
    }

    [Theory]
    [InlineData("observation")]
    [InlineData("action")]
    public void SameCountsButChangedHistoryTextFailsSemanticVerification(
        string corruption
    ) {
        Directory.CreateDirectory(_tempRoot);
        string sourcePath =
            Path.Combine(_tempRoot, $"source-{corruption}");
        string changedPath =
            Path.Combine(_tempRoot, $"changed-{corruption}");
        SessionJournalLegacyImportResult source =
            SessionJournalLegacyImporter.Import(
                ImportExport(
                    InitialState(),
                    "source observation",
                    "source action"
                ),
                sourcePath,
                force: false
            );
        SessionJournalLegacyImportResult changed =
            SessionJournalLegacyImporter.Import(
                ImportExport(
                    InitialState(),
                    corruption == "observation"
                        ? "changed observation"
                        : "source observation",
                    corruption == "action"
                        ? "changed action"
                        : "source action"
                ),
                changedPath,
                force: false
            );
        Assert.Equal(source.ObservationCount, changed.ObservationCount);
        Assert.Equal(source.AgentActionCount, changed.AgentActionCount);
        Assert.NotEqual(
            source.ExpectedHistorySemanticCommitmentSha256,
            changed.ExpectedHistorySemanticCommitmentSha256
        );
        SessionJournalLegacyImportResult sourceExpectedAtChangedTarget =
            source with {
                FinalHead = changed.FinalHead,
                Mappings = changed.Mappings
            };

        InvalidDataException error =
            Assert.Throws<InvalidDataException>(
                () => SessionJournalLegacyImporter.VerifyImportedRepo(
                    changedPath,
                    sourceExpectedAtChangedTarget
                )
            );

        Assert.Contains(
            "semantic history commitment",
            error.Message,
            StringComparison.Ordinal
        );
    }

    [Theory]
    [InlineData("prompt")]
    [InlineData("config")]
    public void FinalSetupMismatchFailsVerification(string corruption) {
        Directory.CreateDirectory(_tempRoot);
        LegacyChatSessionEvent sourceInitial = InitialState();
        LegacyChatSessionRoot changedRoot =
            sourceInitial.Root! with {
                SystemPrompt = corruption == "prompt"
                    ? "system-b"
                    : sourceInitial.Root!.SystemPrompt,
                ModelId = corruption == "config"
                    ? "model-b"
                    : sourceInitial.Root!.ModelId
            };
        LegacyChatSessionEvent changedInitial =
            sourceInitial with { Root = changedRoot };
        string sourcePath =
            Path.Combine(_tempRoot, $"source-{corruption}");
        string changedPath =
            Path.Combine(_tempRoot, $"changed-{corruption}");
        SessionJournalLegacyImportResult source =
            SessionJournalLegacyImporter.Import(
                ImportExport(
                    sourceInitial,
                    "same observation",
                    "same action"
                ),
                sourcePath,
                force: false
            );
        SessionJournalLegacyImportResult changed =
            SessionJournalLegacyImporter.Import(
                ImportExport(
                    changedInitial,
                    "same observation",
                    "same action"
                ),
                changedPath,
                force: false
            );
        SessionJournalLegacyImportResult sourceExpectedAtChangedTarget =
            source with {
                FinalHead = changed.FinalHead,
                Mappings = changed.Mappings
            };

        InvalidDataException error =
            Assert.Throws<InvalidDataException>(
                () => SessionJournalLegacyImporter.VerifyImportedRepo(
                    changedPath,
                    sourceExpectedAtChangedTarget
                )
            );

        Assert.Contains(
            corruption == "prompt"
                ? "system prompt hash"
                : "runtime configuration",
            error.Message,
            StringComparison.Ordinal
        );
    }

    [Theory]
    [InlineData("head")]
    [InlineData("mapping-kind")]
    public void MappingOrFinalHeadMismatchFailsVerification(
        string corruption
    ) {
        Directory.CreateDirectory(_tempRoot);
        string outputPath =
            Path.Combine(_tempRoot, $"mapping-{corruption}");
        SessionJournalLegacyImportResult result =
            SessionJournalLegacyImporter.Import(
                ImportExport(
                    InitialState(),
                    "observation",
                    "action"
                ),
                outputPath,
                force: false
            );
        SessionJournalLegacyImportResult corrupted;
        if (corruption == "head") {
            corrupted = result with {
                FinalHead = result.Mappings[0].EventAddress
            };
        }
        else {
            SessionJournalLegacyImportMapping[] mappings = [
                .. result.Mappings
            ];
            mappings[^1] = mappings[^1] with {
                SessionEventKind =
                    SJ.SessionEventKind.ObservationAccepted.ToString()
            };
            corrupted = result with {
                Mappings = Array.AsReadOnly(mappings)
            };
        }

        InvalidDataException error =
            Assert.Throws<InvalidDataException>(
                () => SessionJournalLegacyImporter.VerifyImportedRepo(
                    outputPath,
                    corrupted
                )
            );

        Assert.Contains(
            corruption == "head"
                ? "final head"
                : "mapping kind",
            error.Message,
            StringComparison.Ordinal
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
    public void JsonReportPathCannotOverwriteLegacyInput() {
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
                "--report-json", inputPath
            ],
            ThrowingCompletionClientFactory.Instance
        );

        Assert.Equal(1, exitCode);
        Assert.Equal(originalJson, File.ReadAllText(inputPath));
        Assert.False(Directory.Exists(outputPath));
    }

    [Theory]
    [InlineData("report-md")]
    [InlineData("report-json")]
    public void ReportFileOptionRejectsExistingDirectoryBeforeImport(
        string reportOption
    ) {
        Directory.CreateDirectory(_tempRoot);
        string inputPath = Path.Combine(_tempRoot, "legacy.json");
        string outputPath = Path.Combine(_tempRoot, "session-journal");
        string reportPath = Path.Combine(_tempRoot, "existing-report");
        Directory.CreateDirectory(reportPath);
        WriteExport(inputPath, [InitialState()]);

        int exitCode = Program.MainCore(
            [
                "import-legacy-json",
                "--input", inputPath,
                "--output", outputPath,
                $"--{reportOption}", reportPath
            ],
            ThrowingCompletionClientFactory.Instance
        );

        Assert.Equal(1, exitCode);
        Assert.False(Directory.Exists(outputPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(reportPath));
    }

    [Fact]
    public void NestedMarkdownAndJsonReportsFailBeforeImport() {
        Directory.CreateDirectory(_tempRoot);
        string inputPath = Path.Combine(_tempRoot, "legacy.json");
        string outputPath = Path.Combine(_tempRoot, "session-journal");
        string markdownPath = Path.Combine(_tempRoot, "report.md");
        string jsonPath = Path.Combine(markdownPath, "report.json");
        WriteExport(inputPath, [InitialState()]);

        int exitCode = Program.MainCore(
            [
                "import-legacy-json",
                "--input", inputPath,
                "--output", outputPath,
                "--report-md", markdownPath,
                "--report-json", jsonPath
            ],
            ThrowingCompletionClientFactory.Instance
        );

        Assert.Equal(1, exitCode);
        Assert.False(Directory.Exists(outputPath));
        Assert.False(File.Exists(markdownPath));
        Assert.False(Directory.Exists(markdownPath));
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
        using var engine =
            SJ.SessionJournalEngine.OpenReadOnly(outputPath);
        Assert.NotNull(engine.InspectExecutionBoundary().Head);
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

    private static LegacyChatSessionExport ImportExport(
        LegacyChatSessionEvent initialState,
        string observation,
        string action
    ) => new() {
        Schema = LegacyChatSessionExportSchema.SchemaId,
        BranchName = SJ.SessionJournalDefaults.MainBranchName,
        Events = [
            initialState,
            new LegacyChatSessionEvent {
                Ordinal = 1,
                Kind = LegacyChatSessionEventKinds.ModelTurn,
                AppendedMessages = [
                    new LegacyChatSessionMessage {
                        Kind = "observation",
                        Content = observation
                    },
                    new LegacyChatSessionMessage {
                        Kind = "action",
                        Action = new LegacyChatSessionAction {
                            Blocks = [
                                new SerializedActionBlock(
                                    ActionMessageSerialization
                                        .BlockKindText,
                                    action,
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
