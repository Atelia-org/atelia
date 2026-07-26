using System.Security.Cryptography;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.Derived;
using ChatSessionBacktestCli;
using SJ = Atelia.SessionJournal;
using Xunit;

namespace Atelia.ChatSession.BacktestCli.Tests;

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
    public async Task ValidateSessionJournal_IsReadOnlyAndReportsMissingCheckpoint() {
        string repoPath = await CreateJournalWithArtifactsAsync(
            writeArtifacts: false
        );
        string reportPath = Path.Combine(_tempRoot, "validation.json");
        IReadOnlyDictionary<string, string> before =
            CaptureRepositoryFileHashes(repoPath);

        int exitCode = Program.MainCore(
            [
                "validate-session-journal",
                "--input", repoPath,
                "--report-json", reportPath
            ],
            ThrowingCompletionClientFactory.Instance
        );

        Assert.Equal(0, exitCode);
        Assert.Equal(before, CaptureRepositoryFileHashes(repoPath));
        Assert.True(File.Exists(reportPath));
        SessionJournalOfflineValidationReport report =
            JsonSerializer.Deserialize<SessionJournalOfflineValidationReport>(
                File.ReadAllText(reportPath),
                WebJsonOptions
            ) ?? throw new Xunit.Sdk.XunitException(
                "Validation report did not deserialize."
            );
        Assert.Equal(
            SessionJournalOfflineReadiness.NeedsArtifactSetCheckpoint,
            report.Readiness
        );
        Assert.Null(report.ActiveArtifactSet);
        Assert.Equal(SessionExecutionPhase.Idle, report.ExecutionPhase);
        Assert.True(report.EventCount >= 5);
        Assert.True(report.LogicalPayloadBytes > 0);
    }

    [Fact]
    public async Task CheckpointArtifactSet_AppendsExactlyOneEventAndPreservesDerivedFiles() {
        (
            string repoPath,
            DerivedRecapArtifact autobiography,
            DerivedRecapArtifact world
        ) = await CreateJournalWithTwoArtifactsAsync();
        SessionJournalOfflineValidationReport before =
            await SessionJournalOfflineValidator.ValidateAsync(repoPath);
        IReadOnlyDictionary<string, string> derivedBefore =
            CaptureRepositoryFileHashes(
                Path.Combine(repoPath, "derived")
            );

        int exitCode = Program.MainCore(
            [
                "checkpoint-artifact-set-session-journal",
                "--input", repoPath,
                "--member", $"autobiography={autobiography.ArtifactId}",
                "--member", $"world-understanding={world.ArtifactId}"
            ],
            ThrowingCompletionClientFactory.Instance
        );

        Assert.Equal(0, exitCode);
        SessionJournalOfflineValidationReport after =
            await SessionJournalOfflineValidator.ValidateAsync(repoPath);
        Assert.Equal(before.EventCount + 1, after.EventCount);
        Assert.NotEqual(before.Head, after.Head);
        Assert.Equal(
            SessionJournalOfflineReadiness.ActiveCoherent,
            after.Readiness
        );
        SessionJournalOfflineArtifactSetReport active =
            Assert.IsType<SessionJournalOfflineArtifactSetReport>(
                after.ActiveArtifactSet
            );
        Assert.True(active.IsUsable);
        Assert.Equal(
            ["autobiography", "world-understanding"],
            active.Members.Select(static member => member.RoleId)
        );
        Assert.All(active.Members, static member => Assert.True(member.Available));
        Assert.Equal(
            derivedBefore,
            CaptureRepositoryFileHashes(Path.Combine(repoPath, "derived"))
        );
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate-role")]
    [InlineData("bad-member")]
    public async Task CheckpointArtifactSet_InvalidMembersFailWithoutAppend(
        string failure
    ) {
        (
            string repoPath,
            DerivedRecapArtifact autobiography,
            DerivedRecapArtifact world
        ) = await CreateJournalWithTwoArtifactsAsync();
        SessionJournalOfflineValidationReport before =
            await SessionJournalOfflineValidator.ValidateAsync(repoPath);
        string[] args = failure switch {
            "missing" => [
                "checkpoint-artifact-set-session-journal",
                "--input", repoPath,
                "--member", $"autobiography={autobiography.ArtifactId}",
                "--member", "world-understanding=missing-artifact"
            ],
            "duplicate-role" => [
                "checkpoint-artifact-set-session-journal",
                "--input", repoPath,
                "--member", $"memory={autobiography.ArtifactId}",
                "--member", $"memory={world.ArtifactId}"
            ],
            "bad-member" => [
                "checkpoint-artifact-set-session-journal",
                "--input", repoPath,
                "--member", $"autobiography={autobiography.ArtifactId}",
                "--member", "not-an-assignment"
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        };

        int exitCode = Program.MainCore(
            args,
            ThrowingCompletionClientFactory.Instance
        );

        Assert.Equal(1, exitCode);
        SessionJournalOfflineValidationReport after =
            await SessionJournalOfflineValidator.ValidateAsync(repoPath);
        Assert.Equal(before.Head, after.Head);
        Assert.Equal(before.EventCount, after.EventCount);
        Assert.Null(after.ActiveArtifactSet);
    }

    private async Task<string> CreateJournalWithArtifactsAsync(
        bool writeArtifacts
    ) {
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
        if (writeArtifacts) {
            _ = await WriteArtifactAsync(
                repoPath,
                anchor,
                engine.ResolveGoverningSetup(anchor),
                "autobiography-profile",
                new SJ.MemoryPackBlockPath(
                    SJ.MemoryPackCarrier.Action,
                    "autobiography"
                ),
                "autobiography text"
            );
        }
        return repoPath;
    }

    private async Task<(
        string RepoPath,
        DerivedRecapArtifact Autobiography,
        DerivedRecapArtifact World
    )> CreateJournalWithTwoArtifactsAsync() {
        string repoPath = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        EventAddress anchor;
        SessionGoverningSetup setup;
        using (var engine = SessionJournalEngine.Create(
            repoPath,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        )) {
            engine.AppendObservation("old observation");
            anchor = engine.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("old action")]),
                new CompletionDescriptor("import", "import-v1", "model-A")
            );
            setup = engine.ResolveGoverningSetup(anchor);
        }

        DerivedRecapArtifact autobiography = await WriteArtifactAsync(
            repoPath,
            anchor,
            setup,
            "autobiography-profile",
            new SJ.MemoryPackBlockPath(
                SJ.MemoryPackCarrier.Action,
                "autobiography"
            ),
            "autobiography text"
        );
        DerivedRecapArtifact world = await WriteArtifactAsync(
            repoPath,
            anchor,
            setup,
            "world-profile",
            new SJ.MemoryPackBlockPath(
                SJ.MemoryPackCarrier.Observation,
                "world-understanding"
            ),
            "world text"
        );
        return (repoPath, autobiography, world);
    }

    private static async ValueTask<DerivedRecapArtifact> WriteArtifactAsync(
        string repoPath,
        EventAddress anchor,
        SessionGoverningSetup setup,
        string profileId,
        SJ.MemoryPackBlockPath target,
        string content
    ) {
        var memoryPack = new SJ.MemoryPack();
        var draft = new SJ.MemoryPackDraft(memoryPack);
        draft.UpsertBlock(target, content);
        memoryPack = draft.Build();
        return await DerivedRecapStore.Open(repoPath).WriteProducedAsync(
            new DerivedRecapWriteRequest(
                ArtifactKind: DerivedRecapArtifactKinds.RollingSummary,
                ProfileId: profileId,
                Producer: "offline-cli-tests",
                ProducerFingerprint: "offline-cli-tests-v1",
                SourceRawHead: anchor,
                SourceStartExclusive: null,
                SourceEndInclusive: anchor,
                AnchorRawEvent: anchor,
                GoverningRuntimeConfigSetup:
                    setup.RuntimeConfigSetupAddress,
                GoverningSystemPromptSetup:
                    setup.SystemPromptSetupAddress,
                PreviousArtifact: null,
                Target: target,
                MemoryPack: memoryPack
            )
        );
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
}
