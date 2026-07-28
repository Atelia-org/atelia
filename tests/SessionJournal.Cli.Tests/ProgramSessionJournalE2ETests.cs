using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal.Cli;
using Atelia.SessionJournal.DerivedMemory;
using Xunit;

namespace Atelia.SessionJournal.Cli.Tests;

public sealed class ProgramSessionJournalE2ETests : IDisposable {
    private static readonly JsonSerializerOptions WebJsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "atelia-session-journal-cli-e2e",
        Guid.NewGuid().ToString("N")
    );

    [Fact]
    public async Task RunMemoryMaintainer_ConsumesExactEpochAndWritesV2Candidate() {
        Directory.CreateDirectory(_tempRoot);
        string repoPath = Path.Combine(_tempRoot, "journal");
        string connectionsPath =
            Path.Combine(_tempRoot, "connections.json");
        string outputPath = Path.Combine(_tempRoot, "report.json");
        string callsPath = Path.Combine(_tempRoot, "calls");
        WriteConnections(connectionsPath);
        using var engine = SessionJournalEngine.Create(
            repoPath,
            new SessionCreateOptions(
                "model-a",
                "system-a",
                "surface-a"
            )
        );
        AppendTurns(engine, 5);
        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(repoPath);
        _ = await repository.EpochPlanner.ConfigureAsync(
            new DerivedArtifactPlannerConfigDefinition(
                "main",
                "memory-pack",
                "topology-v1",
                10,
                10,
                10,
                1_000
            ),
            null
        );
        DerivedArtifactEpochPlan epoch =
            (await repository.EpochPlanner.PlanAsync(
                engine,
                new("main", "memory-pack", null, null)
            )).Epoch!;
        EventAddress rawHeadBefore =
            engine.ReadCurrentLineageHeaders().CapturedHead;
        engine.Dispose();
        var factory =
            new ScriptedCompletionClientFactory("derived autobiography");

        int exitCode = Program.MainCore(
            Command(
                repoPath,
                connectionsPath,
                outputPath,
                callsPath,
                epoch.EpochId,
                "candidate-a"
            ),
            factory
        );

        Assert.Equal(0, exitCode);
        Assert.Equal(1, factory.CompletionCallCount);
        MemoryMaintainerRunRecord record =
            JsonSerializer.Deserialize<MemoryMaintainerRunRecord>(
                await File.ReadAllTextAsync(outputPath),
                WebJsonOptions
            )!;
        Assert.Equal(
            "atelia.session-journal.memory-maintainer-run.v2",
            record.Schema
        );
        Assert.Equal(epoch.EpochId, record.EpochId);
        Assert.Equal("autobiography", record.RoleId);
        Assert.Equal("candidate-a", record.CandidateId);
        Assert.Equal(
            EventAddressTextCodec.Format(epoch.SourceStartExclusive),
            record.SourceStartExclusive
        );
        Assert.Equal(
            EventAddressTextCodec.Format(epoch.SourceEndInclusive),
            record.SourceEndInclusive
        );
        Assert.True(File.Exists(record.ArtifactPath));
        Assert.True(File.Exists(Assert.Single(record.CallLogPaths)));
        DerivedMemoryArtifact artifact =
            await repository.Artifacts.TryReadArtifactAsync(
                record.ArtifactId
            ) ?? throw new Xunit.Sdk.XunitException(
                "Expected candidate artifact."
            );
        Assert.Equal(
            DerivedMemoryArtifactKinds.MemoryBlock,
            artifact.ArtifactKind
        );
        Assert.Equal(epoch.EpochId, artifact.EpochId);
        Assert.Equal(
            "derived autobiography",
            artifact.Content
        );
        using (SessionJournalEngine reopened =
               SessionJournalEngine.Open(repoPath)) {
            Assert.Equal(
                rawHeadBefore,
                reopened.ReadCurrentLineageHeaders().CapturedHead
            );
        }
        Assert.False(Directory.Exists(
            repository.ArtifactSets.LatestPointersDirectory
        ));
    }

    [Fact]
    public async Task AlternativeCandidateDoesNotOverwriteFirstCandidate() {
        Directory.CreateDirectory(_tempRoot);
        string repoPath = Path.Combine(_tempRoot, "journal");
        string connectionsPath =
            Path.Combine(_tempRoot, "connections.json");
        WriteConnections(connectionsPath);
        using var engine = SessionJournalEngine.Create(
            repoPath,
            new SessionCreateOptions(
                "model-a",
                "system-a",
                "surface-a"
            )
        );
        AppendTurns(engine, 5);
        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(repoPath);
        _ = await repository.EpochPlanner.ConfigureAsync(
            new(
                "main",
                "memory-pack",
                "topology-v1",
                10,
                10,
                10,
                1_000
            ),
            null
        );
        DerivedArtifactEpochPlan epoch =
            (await repository.EpochPlanner.PlanAsync(
                engine,
                new("main", "memory-pack", null, null)
            )).Epoch!;
        engine.Dispose();
        var factory = new ScriptedCompletionClientFactory("same text");

        Assert.Equal(0, Program.MainCore(
            Command(
                repoPath,
                connectionsPath,
                Path.Combine(_tempRoot, "a.json"),
                Path.Combine(_tempRoot, "calls-a"),
                epoch.EpochId,
                "candidate-a"
            ),
            factory
        ));
        Assert.Equal(0, Program.MainCore(
            Command(
                repoPath,
                connectionsPath,
                Path.Combine(_tempRoot, "b.json"),
                Path.Combine(_tempRoot, "calls-b"),
                epoch.EpochId,
                "candidate-b"
            ),
            factory
        ));

        Assert.Equal(
            2,
            Directory.EnumerateFiles(
                repository.Artifacts.ArtifactsDirectory,
                "*.json"
            ).Count()
        );
    }

    [Fact]
    public void RetiredThresholdOptionIsRejectedBeforeCompletion() {
        Directory.CreateDirectory(_tempRoot);
        string connectionsPath =
            Path.Combine(_tempRoot, "connections.json");
        WriteConnections(connectionsPath);
        var factory =
            new ScriptedCompletionClientFactory("must-not-run");

        int exitCode = Program.MainCore(
            [
                "run-memory-maintainer",
                "--input", _tempRoot,
                "--epoch", "dae_" + new string('1', 64),
                "--profile", "autobiographical-rewrite",
                "--output", Path.Combine(_tempRoot, "report.json"),
                "--connections", connectionsPath,
                "--threshold-tokens", "1"
            ],
            factory
        );

        Assert.Equal(1, exitCode);
        Assert.Equal(0, factory.CompletionCallCount);
    }

    [Theory]
    [InlineData("same")]
    [InlineData("output-inside-calls")]
    [InlineData("calls-inside-output")]
    public void OutputAndCallLogOverlapIsRejectedBeforeSideEffects(
        string shape
    ) {
        string repoPath = Path.Combine(_tempRoot, "journal");
        string external = Path.Combine(_tempRoot, "external");
        string outputPath = shape switch {
            "same" => Path.Combine(external, "same"),
            "output-inside-calls" =>
                Path.Combine(external, "calls", "report.json"),
            _ => Path.Combine(external, "report")
        };
        string callsPath = shape switch {
            "same" => outputPath,
            "output-inside-calls" =>
                Path.Combine(external, "calls"),
            _ => Path.Combine(outputPath, "calls")
        };
        var factory =
            new ScriptedCompletionClientFactory("must-not-run");

        int exitCode = Program.MainCore(
            Command(
                repoPath,
                Path.Combine(_tempRoot, "connections.json"),
                outputPath,
                callsPath,
                "dae_" + new string('1', 64),
                "candidate"
            ),
            factory
        );

        Assert.Equal(1, exitCode);
        Assert.Equal(0, factory.CompletionCallCount);
        Assert.False(Directory.Exists(
            Path.Combine(repoPath, "derived")
        ));
        Assert.False(File.Exists(outputPath));
        Assert.False(Directory.Exists(callsPath));
    }

    [Fact]
    public async Task ExistingOutputDirectoryIsRejectedBeforeSideEffects() {
        Directory.CreateDirectory(_tempRoot);
        string repoPath = Path.Combine(_tempRoot, "journal");
        string connectionsPath =
            Path.Combine(_tempRoot, "connections.json");
        string outputPath = Path.Combine(_tempRoot, "existing-output");
        string callsPath = Path.Combine(_tempRoot, "calls");
        WriteConnections(connectionsPath);
        Directory.CreateDirectory(outputPath);
        using var engine = SessionJournalEngine.Create(
            repoPath,
            new SessionCreateOptions(
                "model-a",
                "system-a",
                "surface-a"
            )
        );
        AppendTurns(engine, 5);
        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(repoPath);
        _ = await repository.EpochPlanner.ConfigureAsync(
            new(
                "main",
                "memory-pack",
                "topology-v1",
                10,
                10,
                10,
                1_000
            ),
            null
        );
        DerivedArtifactEpochPlan epoch =
            (await repository.EpochPlanner.PlanAsync(
                engine,
                new("main", "memory-pack", null, null)
            )).Epoch!;
        EventAddress rawHeadBefore =
            engine.ReadCurrentLineageHeaders().CapturedHead;
        engine.Dispose();
        var factory =
            new ScriptedCompletionClientFactory("must-not-run");

        int exitCode = Program.MainCore(
            Command(
                repoPath,
                connectionsPath,
                outputPath,
                callsPath,
                epoch.EpochId,
                "candidate"
            ),
            factory
        );

        Assert.Equal(1, exitCode);
        Assert.Equal(0, factory.CompletionCallCount);
        Assert.False(Directory.Exists(
            repository.Artifacts.ArtifactsDirectory
        ));
        Assert.False(Directory.Exists(callsPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outputPath));
        using SessionJournalEngine reopened =
            SessionJournalEngine.Open(repoPath);
        Assert.Equal(
            rawHeadBefore,
            reopened.ReadCurrentLineageHeaders().CapturedHead
        );
    }

    public void Dispose() {
        try {
            if (Directory.Exists(_tempRoot)) {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch {
            // Best-effort cleanup.
        }
    }

    private static string[] Command(
        string repositoryPath,
        string connectionsPath,
        string outputPath,
        string callsPath,
        string epochId,
        string candidateId
    ) => [
        "run-memory-maintainer",
        "--input", repositoryPath,
        "--epoch", epochId,
        "--profile", "autobiographical-rewrite",
        "--output", outputPath,
        "--connections", connectionsPath,
        "--connection", "scripted",
        "--call-log-dir", callsPath,
        "--candidate-id", candidateId,
        "--attempt-id", "attempt-1"
    ];

    private static void AppendTurns(
        SessionJournalEngine engine,
        int count
    ) {
        for (int index = 0; index < count; index++) {
            _ = engine.AppendObservation(
                $"observation-{index}-with-token-cost"
            );
            _ = engine.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text(
                        $"answer-{index}-with-token-cost"
                    )
                ]),
                new CompletionDescriptor(
                    "import",
                    "import-v1",
                    "model-a"
                )
            );
        }
    }

    private static void WriteConnections(string path) {
        var config = new CompletionConnectionsFileConfig(
            [
                new CompletionConnectionConfig(
                    "scripted",
                    "scripted",
                    "model-a",
                    "surface-a",
                    "http://localhost/"
                )
            ],
            "scripted"
        );
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(config, WebJsonOptions)
        );
    }

    private sealed class ScriptedCompletionClientFactory(
        string responseText
    ) : ICompletionClientFactory {
        private readonly ScriptedCompletionClient _client =
            new(responseText);
        public int CompletionCallCount => _client.CallCount;
        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) => _client;
    }

    private sealed class ScriptedCompletionClient(string responseText)
        : ICompletionClient {
        public string Name => "scripted";
        public string ApiSpecId => "test-api-v1";
        public int CallCount { get; private set; }

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(new CompletionResult(
                new ActionMessage([
                    new ActionBlock.Text(responseText)
                ]),
                CompletionDescriptor.From(this, request)
            ));
        }
    }
}
