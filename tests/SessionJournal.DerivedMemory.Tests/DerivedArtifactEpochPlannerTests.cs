using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Atelia.SessionJournal.DerivedMemory.Tests;

public sealed class DerivedArtifactEpochPlannerTests : IDisposable {
    private readonly List<string> _paths = [];

    [Fact]
    public async Task V2GenerationPersistsCanonicalBranchRefIdAndIgnoresV1() {
        string path = NewPath();
        using var engine = CreateSession(path);
        string legacyRoot = Path.Combine(
            path,
            "derived",
            "memory",
            "v1",
            "planner-configs"
        );
        Directory.CreateDirectory(legacyRoot);
        await File.WriteAllTextAsync(
            Path.Combine(legacyRoot, "legacy.json"),
            """{"schema":"legacy-main-string"}"""
        );

        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(path);
        Assert.EndsWith(
            Path.Combine("derived", "memory", "v2"),
            repository.MemoryRoot,
            StringComparison.Ordinal
        );
        Assert.Empty(
            (await repository.EpochPlanner.ReadInventoryAsync()).Configs
        );

        _ = await repository.EpochPlanner.ConfigureAsync(
            repository.Bind(engine),
            Definition(),
            null
        );
        string configPath = Assert.Single(
            Directory.EnumerateFiles(
                repository.EpochPlanner.ConfigsDirectory
            )
        );
        JsonObject json = JsonNode.Parse(
            await File.ReadAllTextAsync(configPath)
        )!.AsObject();
        Assert.Equal(
            engine.BranchRefId.ToHexString(),
            json["branchRefId"]!.GetValue<string>()
        );
        Assert.False(json.ContainsKey("lineageKey"));
    }

    [Fact]
    public async Task BranchRefIdWireRejectsNonCanonicalHex() {
        string path = NewPath();
        using var engine = CreateSession(path);
        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(path);
        _ = await repository.EpochPlanner.ConfigureAsync(
            repository.Bind(engine),
            Definition(),
            null
        );
        string configPath = Assert.Single(
            Directory.EnumerateFiles(
                repository.EpochPlanner.ConfigsDirectory
            )
        );
        JsonObject json = JsonNode.Parse(
            await File.ReadAllTextAsync(configPath)
        )!.AsObject();
        json["branchRefId"] = "ABCDEFABCDEFABCD";
        await File.WriteAllTextAsync(
            configPath,
            json.ToJsonString()
        );

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await repository.EpochPlanner.ReadInventoryAsync()
        );
    }

    [Fact]
    public async Task ConfigureAndGenesisPlan_AreDeterministicAndIdempotent() {
        string path = NewPath();
        using var engine = CreateSession(path);
        AppendTurns(engine, 5, "first");
        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(path);
        DerivedArtifactPlannerConfigDefinition definition =
            Definition();

        DerivedArtifactPlannerConfig config =
            await repository.EpochPlanner.ConfigureAsync(
                repository.Bind(engine),
                definition,
                expectedCurrentConfigId: null
            );
        DerivedArtifactPlannerConfig configRetry =
            await repository.EpochPlanner.ConfigureAsync(
                repository.Bind(engine),
                definition,
                expectedCurrentConfigId: null
            );
        var request = new DerivedArtifactEpochPlanningRequest(
            "memory-pack",
            ExpectedPreviousEpochId: null,
            InputSetId: null
        );
        DerivedArtifactEpochPlanningResult first =
            await repository.EpochPlanner.PlanAsync(engine, request);
        DerivedArtifactEpochPlanningResult retry =
            await repository.EpochPlanner.PlanAsync(engine, request);

        Assert.Equal(config.ConfigId, configRetry.ConfigId);
        Assert.Null(config.PreviousConfigId);
        Assert.Equal(
            DerivedArtifactEpochPlanningStatus.Planned,
            first.Status
        );
        Assert.Equal(
            DerivedArtifactEpochPlanningStatus.AlreadyPlanned,
            retry.Status
        );
        Assert.Equal(first.Epoch!.EpochId, retry.Epoch!.EpochId);
        Assert.Null(first.Epoch.InputSetId);
        Assert.Null(first.Epoch.PreviousEpochId);
        Assert.True(first.Epoch.MeasuredTokens >= config.EpochTriggerTokens);
        Assert.True(
            first.Epoch.PlanningDiagnostics.RetainedRecentTokens
                >= config.MinimumRecentTokens
        );
        Assert.Single(
            (await repository.EpochPlanner.ReadInventoryAsync()).Epochs
        );
    }

    [Fact]
    public async Task ConfigCutover_IsImmutableAndOnlyAffectsFutureEpoch() {
        string path = NewPath();
        using var engine = CreateSession(path);
        AppendTurns(engine, 5, "first");
        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(path);
        DerivedArtifactPlannerConfig firstConfig =
            await repository.EpochPlanner.ConfigureAsync(
                repository.Bind(engine),
                Definition(),
                null
            );
        DerivedArtifactEpochPlan first =
            (await repository.EpochPlanner.PlanAsync(
                engine,
                new(
                    "memory-pack",
                    null,
                    null
                )
            )).Epoch!;
        DerivedArtifactSet inputSet =
            await PublishInputSetAsync(repository, engine, first);

        DerivedArtifactPlannerConfig secondConfig =
            await repository.EpochPlanner.ConfigureAsync(
                repository.Bind(engine),
                Definition() with {
                    TopologyVersion = "topology-v2",
                    EpochTriggerTokens = 2
                },
                firstConfig.ConfigId
            );
        AppendTurns(engine, 7, "second-longer");
        DerivedArtifactEpochPlan second =
            (await repository.EpochPlanner.PlanAsync(
                engine,
                new(
                    "memory-pack",
                    first.EpochId,
                    inputSet.SetId
                )
            )).Epoch!;
        _ = await repository.EpochPlanner.ConfigureAsync(
            repository.Bind(engine),
            Definition() with {
                TopologyVersion = "topology-v3",
                EpochTriggerTokens = 3
            },
            secondConfig.ConfigId
        );
        DerivedArtifactEpochPlanningResult retry =
            await repository.EpochPlanner.PlanAsync(
                engine,
                new(
                    "memory-pack",
                    first.EpochId,
                    inputSet.SetId
                )
            );

        Assert.Equal(firstConfig.ConfigId, secondConfig.PreviousConfigId);
        Assert.Equal(firstConfig.ConfigId, first.ConfigId);
        Assert.Equal(secondConfig.ConfigId, second.ConfigId);
        Assert.Equal(first.EpochId, second.PreviousEpochId);
        Assert.Equal(first.SourceEndInclusive, second.SourceStartExclusive);
        Assert.Equal(inputSet.SetId, second.InputSetId);
        Assert.NotEqual(first.MeasuredTokens, second.MeasuredTokens);
        Assert.Equal(
            DerivedArtifactEpochPlanningStatus.AlreadyPlanned,
            retry.Status
        );
        Assert.Equal(secondConfig.ConfigId, retry.Config.ConfigId);
        Assert.Equal(
            second.PlanningDiagnostics,
            retry.Diagnostics
        );
    }

    [Fact]
    public async Task NonGenesisRequiresRealExactCoherentInputSet() {
        string path = NewPath();
        using var engine = CreateSession(path);
        AppendTurns(engine, 5, "first");
        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(path);
        _ = await repository.EpochPlanner.ConfigureAsync(
            repository.Bind(engine),
            Definition(),
            null
        );
        DerivedArtifactEpochPlan first =
            (await repository.EpochPlanner.PlanAsync(
                engine,
                new("memory-pack", null, null)
            )).Epoch!;
        _ = await PublishInputSetAsync(repository, engine, first);
        AppendTurns(engine, 5, "second");

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await repository.EpochPlanner.PlanAsync(
                engine,
                new("memory-pack", first.EpochId, null)
            )
        );
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await repository.EpochPlanner.PlanAsync(
                engine,
                new(
                    "memory-pack",
                    first.EpochId,
                    "das_" + new string('a', 64)
                )
            )
        );
    }

    [Fact]
    public async Task ConcurrentGenesisPlanning_ConvergesOnOneEpoch() {
        string path = NewPath();
        using var engine = CreateSession(path);
        AppendTurns(engine, 5, "first");
        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(path);
        _ = await repository.EpochPlanner.ConfigureAsync(
            repository.Bind(engine),
            Definition(),
            null
        );
        var request = new DerivedArtifactEpochPlanningRequest(
            "memory-pack",
            null,
            null
        );
        var firstReady =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReady =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int arrivals = 0;
        repository.EpochPlanner.BeforeLinearizationAsync = async ct => {
            int arrival = Interlocked.Increment(ref arrivals);
            (arrival == 1 ? firstReady : secondReady).SetResult();
            await release.Task.WaitAsync(ct);
        };

        Task<DerivedArtifactEpochPlanningResult> first =
            repository.EpochPlanner.PlanAsync(engine, request).AsTask();
        await firstReady.Task;
        Task<DerivedArtifactEpochPlanningResult> second =
            repository.EpochPlanner.PlanAsync(engine, request).AsTask();
        await secondReady.Task;
        release.SetResult();
        DerivedArtifactEpochPlanningResult[] results =
            await Task.WhenAll(first, second);
        repository.EpochPlanner.BeforeLinearizationAsync = null;

        Assert.Single(
            results.Select(result => result.Epoch!.EpochId).Distinct()
        );
        Assert.Contains(
            results,
            result => result.Status
                == DerivedArtifactEpochPlanningStatus.Planned
        );
        Assert.Contains(
            results,
            result => result.Status
                == DerivedArtifactEpochPlanningStatus.AlreadyPlanned
        );
        Assert.Single(
            (await repository.EpochPlanner.ReadInventoryAsync()).Epochs
        );
    }

    [Theory]
    [InlineData("planned")]
    [InlineData("already-planned")]
    [InlineData("below-trigger")]
    [InlineData("backpressure")]
    public async Task ConfigChangeBeforeLinearization_RejectsEveryTerminalState(
        string terminal
    ) {
        string path = NewPath();
        using var engine = CreateSession(path);
        DerivedArtifactPlannerConfigDefinition definition = terminal switch {
            "below-trigger" => Definition() with {
                MinimumRecentTokens = 10,
                EpochTriggerTokens = 1_000,
                HardLimitTokens = 10_000
            },
            "backpressure" => Definition() with {
                MinimumRecentTokens = 100,
                EpochTriggerTokens = 10,
                SchedulingHeadroomTokens = 1,
                HardLimitTokens = 112
            },
            _ => Definition()
        };
        if (terminal == "backpressure") {
            _ = engine.AppendObservation(new string('o', 210));
            _ = engine.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text(new string('a', 210))
                ]),
                new CompletionDescriptor(
                    "import",
                    "import-v1",
                    "model-A"
                )
            );
        }
        else {
            AppendTurns(
                engine,
                terminal == "below-trigger" ? 1 : 5,
                terminal
            );
        }
        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(path);
        DerivedArtifactPlannerConfig config =
            await repository.EpochPlanner.ConfigureAsync(
                repository.Bind(engine),
                definition,
                null
            );
        var request = new DerivedArtifactEpochPlanningRequest(
            "memory-pack",
            null,
            null
        );
        if (terminal == "already-planned") {
            _ = await repository.EpochPlanner.PlanAsync(
                engine,
                request
            );
        }
        var reached =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.EpochPlanner.BeforeLinearizationAsync = async ct => {
            reached.TrySetResult();
            await release.Task.WaitAsync(ct);
        };

        Task<DerivedArtifactEpochPlanningResult> planning =
            repository.EpochPlanner.PlanAsync(engine, request).AsTask();
        await reached.Task;
        _ = await repository.EpochPlanner.ConfigureAsync(
            repository.Bind(engine),
            definition with {
                TopologyVersion = "topology-after-snapshot"
            },
            config.ConfigId
        );
        release.SetResult();

        await Assert.ThrowsAsync<
            DerivedArtifactEpochConcurrencyException
        >(async () => await planning);
        repository.EpochPlanner.BeforeLinearizationAsync = null;
    }

    [Fact]
    public async Task GrandchildLatest_IsNotTreatedAsDirectRetry() {
        string path = NewPath();
        using var engine = CreateSession(path);
        AppendTurns(engine, 5, "first");
        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(path);
        _ = await repository.EpochPlanner.ConfigureAsync(
            repository.Bind(engine),
            Definition(),
            null
        );
        DerivedArtifactEpochPlan first =
            (await repository.EpochPlanner.PlanAsync(
                engine,
                new("memory-pack", null, null)
            )).Epoch!;
        DerivedArtifactSet input =
            await PublishInputSetAsync(repository, engine, first);
        AppendTurns(engine, 5, "second");
        _ = await repository.EpochPlanner.PlanAsync(
            engine,
            new(
                "memory-pack",
                first.EpochId,
                input.SetId
            )
        );
        SessionJournalReadDiagnostics before =
            engine.CaptureReadDiagnostics();

        await Assert.ThrowsAsync<
            DerivedArtifactEpochConcurrencyException
        >(async () => await repository.EpochPlanner.PlanAsync(
            engine,
            new("memory-pack", null, null)
        ));
        SessionJournalReadDiagnostics after =
            engine.CaptureReadDiagnostics();
        Assert.Equal(
            before.HeaderPreviewReadCount,
            after.HeaderPreviewReadCount
        );
        Assert.Equal(
            before.PayloadReadCount,
            after.PayloadReadCount
        );
        Assert.Equal(
            before.LogicalPayloadByteCount,
            after.LogicalPayloadByteCount
        );
        await Assert.ThrowsAsync<
            DerivedArtifactEpochConcurrencyException
        >(async () => await repository.EpochPlanner.PlanAsync(
            engine,
            new(
                "memory-pack",
                "dae_" + new string('e', 64),
                "das_" + new string('f', 64)
            )
        ));
        SessionJournalReadDiagnostics afterMissingInputs =
            engine.CaptureReadDiagnostics();
        Assert.Equal(after, afterMissingInputs);
    }

    [Fact]
    public async Task DeletedLatestEpochPointer_IsRejectedThenRebuilt() {
        string path = NewPath();
        using var engine = CreateSession(path);
        AppendTurns(engine, 5, "first");
        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(path);
        _ = await repository.EpochPlanner.ConfigureAsync(
            repository.Bind(engine),
            Definition(),
            null
        );
        _ = await repository.EpochPlanner.PlanAsync(
            engine,
            new("memory-pack", null, null)
        );
        File.Delete(Assert.Single(
            Directory.EnumerateFiles(
                repository.EpochPlanner.LatestEpochsDirectory
            )
        ));

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await repository.ValidateBranchAgainstOpenEngineAsync(engine)
        );
        DerivedArtifactEpochPlan? rebuilt =
            await repository.EpochPlanner.RebuildLatestEpochPointerAsync(
                engine,
                "memory-pack"
            );
        Assert.NotNull(rebuilt);
        DerivedMemoryValidationReport report =
            await repository.ValidateBranchAgainstOpenEngineAsync(engine);
        Assert.Equal(1, report.ArtifactEpochCount);
        Assert.Equal(1, report.LatestArtifactEpochCount);
    }

    [Fact]
    public async Task RebuildLatestEpochRejectsMissingConfigWithoutWritingPointer() {
        string path = NewPath();
        using var engine = CreateSession(path);
        AppendTurns(engine, 5, "missing-config");
        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(path);
        _ = await repository.EpochPlanner.ConfigureAsync(
            repository.Bind(engine),
            Definition(),
            null
        );
        _ = await repository.EpochPlanner.PlanAsync(
            engine,
            new("memory-pack", null, null)
        );
        File.Delete(Assert.Single(Directory.EnumerateFiles(
            repository.EpochPlanner.LatestEpochsDirectory
        )));
        File.Delete(Assert.Single(Directory.EnumerateFiles(
            repository.EpochPlanner.ConfigsDirectory
        )));

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await repository.EpochPlanner
                .RebuildLatestEpochPointerAsync(
                    engine,
                    "memory-pack"
                )
        );
        Assert.Empty(Directory.EnumerateFiles(
            repository.EpochPlanner.LatestEpochsDirectory
        ));
    }

    [Fact]
    public async Task RawRewindBehindPreviousEpoch_FailsFast() {
        string path = NewPath();
        EventAddress rawHead;
        DerivedArtifactEpochPlan first;
        DerivedArtifactSet inputSet;
        using (var engine = CreateSession(path)) {
            AppendTurns(engine, 5, "first");
            rawHead = engine.Project().Head!.Value;
            DerivedMemoryRepository repository =
                DerivedMemoryRepository.Open(path);
            _ = await repository.EpochPlanner.ConfigureAsync(
                repository.Bind(engine),
                Definition(),
                null
            );
            first = (await repository.EpochPlanner.PlanAsync(
                engine,
                new("memory-pack", null, null)
            )).Epoch!;
            inputSet = await PublishInputSetAsync(
                repository,
                engine,
                first
            );
        }
        using (var journal =
               EventJournal.EventJournal.OpenExisting(path)) {
            RefId main = journal.OpenBranch(
                SessionJournalDefaults.MainBranchName
            ).Unwrap();
            Assert.True(
                journal.MoveRef(
                    main,
                    rawHead,
                    first.SourceStartExclusive
                ).Unwrap()
            );
        }
        using var reopened = SessionJournalEngine.Open(path);
        DerivedMemoryRepository reopenedRepository =
            DerivedMemoryRepository.Open(path);

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await reopenedRepository.EpochPlanner.PlanAsync(
                reopened,
                new(
                    "memory-pack",
                    first.EpochId,
                    inputSet.SetId
                )
            )
        );
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await reopenedRepository.ValidateBranchAgainstOpenEngineAsync(reopened)
        );
        File.Delete(Assert.Single(
            Directory.EnumerateFiles(
                reopenedRepository.EpochPlanner.LatestEpochsDirectory
            )
        ));
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await reopenedRepository.EpochPlanner
                .RebuildLatestEpochPointerAsync(
                    reopened,
                    "memory-pack"
                )
        );
        Assert.Empty(
            Directory.EnumerateFiles(
                reopenedRepository.EpochPlanner.LatestEpochsDirectory
            )
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RebuildLatestEpochRejectsMissingOrWrongInputSet(
        bool wrongInput
    ) {
        string path = NewPath();
        using var engine = CreateSession(path);
        AppendTurns(engine, 5, "first");
        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(path);
        _ = await repository.EpochPlanner.ConfigureAsync(
            repository.Bind(engine),
            Definition(),
            null
        );
        DerivedArtifactEpochPlan first =
            (await repository.EpochPlanner.PlanAsync(
                engine,
                new("memory-pack", null, null)
            )).Epoch!;
        DerivedArtifactSet correct =
            await PublishInputSetAsync(repository, engine, first);
        AppendTurns(engine, 5, "second");
        DerivedArtifactEpochPlan second =
            (await repository.EpochPlanner.PlanAsync(
                engine,
                new(
                    "memory-pack",
                    first.EpochId,
                    correct.SetId
                )
            )).Epoch!;
        File.Delete(Assert.Single(Directory.EnumerateFiles(
            repository.EpochPlanner.LatestEpochsDirectory
        )));
        if (wrongInput) {
            string secondPath = Path.Combine(
                repository.EpochPlanner.EpochsDirectory,
                second.EpochId + ".json"
            );
            JsonObject root = (JsonObject)JsonNode.Parse(
                await File.ReadAllTextAsync(secondPath)
            )!;
            root["inputSetId"] = "das_" + new string('f', 64);
            string replacementId = RecomputeEpochId(root);
            root["epochId"] = replacementId;
            await File.WriteAllTextAsync(
                Path.Combine(
                    repository.EpochPlanner.EpochsDirectory,
                    replacementId + ".json"
                ),
                root.ToJsonString()
            );
            File.Delete(secondPath);
        }
        else {
            File.Delete(Path.Combine(
                repository.ArtifactSets.SetsDirectory,
                correct.SetId + ".json"
            ));
        }

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await repository.EpochPlanner
                .RebuildLatestEpochPointerAsync(
                    engine,
                    "memory-pack"
                )
        );
        Assert.Empty(Directory.EnumerateFiles(
            repository.EpochPlanner.LatestEpochsDirectory
        ));
    }

    [Fact]
    public async Task ConfigPointerDeletion_RebuildsUniqueImmutableTip() {
        string path = NewPath();
        using var engine = CreateSession(path);
        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(path);
        DerivedArtifactPlannerConfig first =
            await repository.EpochPlanner.ConfigureAsync(
                repository.Bind(engine),
                Definition(),
                null
            );
        DerivedArtifactPlannerConfig second =
            await repository.EpochPlanner.ConfigureAsync(
                repository.Bind(engine),
                Definition() with {
                    TopologyVersion = "topology-v2"
                },
                first.ConfigId
            );
        File.Delete(Assert.Single(
            Directory.EnumerateFiles(
                repository.EpochPlanner.CurrentConfigsDirectory
            )
        ));

        DerivedArtifactPlannerConfig? rebuilt =
            await repository.EpochPlanner.RebuildCurrentConfigPointerAsync(
                repository.Bind(engine),
                "memory-pack"
            );

        Assert.Equal(second.ConfigId, rebuilt!.ConfigId);
        _ = await repository.ValidateBranchAgainstOpenEngineAsync(engine);
    }

    [Fact]
    public async Task BelowTriggerAndHardLimitBackpressure_AreExplicit() {
        string belowPath = NewPath();
        using (var belowEngine = CreateSession(belowPath)) {
            AppendTurns(belowEngine, 1, "small");
            DerivedMemoryRepository repository =
                DerivedMemoryRepository.Open(belowPath);
            _ = await repository.EpochPlanner.ConfigureAsync(
                repository.Bind(belowEngine),
                Definition() with {
                    MinimumRecentTokens = 10,
                    EpochTriggerTokens = 1_000,
                    HardLimitTokens = 10_000
                },
                null
            );
            DerivedArtifactEpochPlanningResult below =
                await repository.EpochPlanner.PlanAsync(
                    belowEngine,
                    new("memory-pack", null, null)
                );
            Assert.Equal(
                DerivedArtifactEpochPlanningStatus.BelowTrigger,
                below.Status
            );
            Assert.Null(below.Epoch);
        }

        string hardPath = NewPath();
        using var hardEngine = CreateSession(hardPath);
        _ = hardEngine.AppendObservation(new string('o', 210));
        _ = hardEngine.AppendImportedAgentAction(
            new ActionMessage([
                new ActionBlock.Text(new string('a', 210))
            ]),
            new CompletionDescriptor("import", "import-v1", "model-A")
        );
        DerivedMemoryRepository hardRepository =
            DerivedMemoryRepository.Open(hardPath);
        _ = await hardRepository.EpochPlanner.ConfigureAsync(
            hardRepository.Bind(hardEngine),
            Definition() with {
                MinimumRecentTokens = 100,
                EpochTriggerTokens = 10,
                SchedulingHeadroomTokens = 1,
                HardLimitTokens = 112
            },
            null
        );
        await Assert.ThrowsAsync<DerivedArtifactEpochBackpressureException>(
            async () => await hardRepository.EpochPlanner.PlanAsync(
                hardEngine,
                new("memory-pack", null, null)
            )
        );
    }

    [Fact]
    public async Task ConfigCasAndForkedConfigInventory_FailFast() {
        string path = NewPath();
        using var engine = CreateSession(path);
        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(path);
        DerivedArtifactPlannerConfig configA =
            await repository.EpochPlanner.ConfigureAsync(
            repository.Bind(engine),
            Definition(),
            null
        );
        await Assert.ThrowsAsync<DerivedArtifactEpochConcurrencyException>(
            async () => await repository.EpochPlanner.ConfigureAsync(
                repository.Bind(engine),
                Definition() with {
                    TopologyVersion = "topology-stale"
                },
                null
            )
        );
        DerivedArtifactPlannerConfig configB =
            await repository.EpochPlanner.ConfigureAsync(
                repository.Bind(engine),
                Definition() with {
                    TopologyVersion = "topology-B"
                },
                configA.ConfigId
            );
        DerivedArtifactPlannerConfig configC =
            await repository.EpochPlanner.ConfigureAsync(
                repository.Bind(engine),
                Definition(),
                configB.ConfigId
            );
        Assert.Equal(configB.ConfigId, configC.PreviousConfigId);
        await Assert.ThrowsAsync<DerivedArtifactEpochConcurrencyException>(
            async () => await repository.EpochPlanner.ConfigureAsync(
                repository.Bind(engine),
                Definition(),
                configA.ConfigId
            )
        );
        File.Delete(Assert.Single(
            Directory.EnumerateFiles(
                repository.EpochPlanner.CurrentConfigsDirectory
            )
        ));
        _ = await repository.EpochPlanner.ConfigureAsync(
            repository.Bind(engine),
            Definition() with {
                TopologyVersion = "topology-fork"
            },
            null
        );

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await repository.ValidateAllActiveBranchesAsync()
        );
    }

    [Fact]
    public async Task ConfigRejectsUnreachableHardLimitAndBudgetOverflow() {
        string path = NewPath();
        using var engine = CreateSession(path);
        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(path);
        DerivedArtifactEpochPlanner planner = repository.EpochPlanner;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await planner.ConfigureAsync(
                repository.Bind(engine),
                Definition() with {
                    MinimumRecentTokens = 10,
                    EpochTriggerTokens = 10,
                    SchedulingHeadroomTokens = 10,
                    HardLimitTokens = 30
                },
                null
            )
        );
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await planner.ConfigureAsync(
                repository.Bind(engine),
                Definition() with {
                    MinimumRecentTokens = long.MaxValue,
                    EpochTriggerTokens = 1,
                    SchedulingHeadroomTokens = 1,
                    HardLimitTokens = long.MaxValue
                },
                null
            )
        );
    }

    [Fact]
    public async Task PlanRejectsEngineFromAnotherRepositoryBeforeAnyMutationOrRawRead() {
        string repositoryAPath = NewPath();
        string repositoryBPath = NewPath();
        using var engineA = CreateSession(repositoryAPath);
        using var engineB = CreateSession(repositoryBPath);
        AppendTurns(engineB, 5, "foreign");
        DerivedMemoryRepository repositoryA =
            DerivedMemoryRepository.Open(repositoryAPath);
        _ = await repositoryA.EpochPlanner.ConfigureAsync(
            repositoryA.Bind(engineA),
            Definition(),
            null
        );
        IReadOnlyDictionary<string, string> filesBefore =
            SnapshotFiles(repositoryA.MemoryRoot);
        SessionCurrentLineageSnapshot rawBefore =
            engineB.ReadCurrentLineageHeaders();
        SessionJournalReadDiagnostics readsBefore =
            engineB.CaptureReadDiagnostics();

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await repositoryA.EpochPlanner.PlanAsync(
                engineB,
                new("memory-pack", null, null)
            )
        );

        Assert.Equal(
            filesBefore,
            SnapshotFiles(repositoryA.MemoryRoot)
        );
        Assert.Empty(Directory.EnumerateFiles(
            repositoryA.EpochPlanner.EpochsDirectory
        ));
        Assert.Empty(Directory.EnumerateFiles(
            repositoryA.EpochPlanner.LatestEpochsDirectory
        ));
        Assert.Equal(
            readsBefore,
            engineB.CaptureReadDiagnostics()
        );
        Assert.Equal(
            rawBefore.CapturedHead,
            engineB.ReadCurrentLineageHeaders().CapturedHead
        );
    }

    [Fact]
    public async Task RestartReturnsDurableEpochWithoutReplanning() {
        string path = NewPath();
        DerivedArtifactEpochPlan first;
        using (var engine = CreateSession(path)) {
            AppendTurns(engine, 5, "first");
            DerivedMemoryRepository repository =
                DerivedMemoryRepository.Open(path);
            _ = await repository.EpochPlanner.ConfigureAsync(
                repository.Bind(engine),
                Definition(),
                null
            );
            first = (await repository.EpochPlanner.PlanAsync(
                engine,
                new("memory-pack", null, null)
            )).Epoch!;
        }
        using var reopened = SessionJournalEngine.Open(path);
        DerivedMemoryRepository reopenedRepository =
            DerivedMemoryRepository.Open(path);
        DerivedArtifactEpochPlanningResult retry =
            await reopenedRepository.EpochPlanner.PlanAsync(
                reopened,
                new("memory-pack", null, null)
            );

        Assert.Equal(
            DerivedArtifactEpochPlanningStatus.AlreadyPlanned,
            retry.Status
        );
        Assert.Equal(first.EpochId, retry.Epoch!.EpochId);
        Assert.Single(
            (await reopenedRepository.EpochPlanner.ReadInventoryAsync())
                .Epochs
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrphanEpochReuse_AdoptsOnlyObservationalDiagnostics(
        bool semanticMismatch
    ) {
        string path = NewPath();
        using var engine = CreateSession(path);
        AppendTurns(engine, 5, "orphan");
        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(path);
        _ = await repository.EpochPlanner.ConfigureAsync(
            repository.Bind(engine),
            Definition(),
            null
        );
        DerivedArtifactEpochPlan first =
            (await repository.EpochPlanner.PlanAsync(
                engine,
                new("memory-pack", null, null)
            )).Epoch!;
        File.Delete(Assert.Single(
            Directory.EnumerateFiles(
                repository.EpochPlanner.LatestEpochsDirectory
            )
        ));
        string epochPath = Assert.Single(
            Directory.EnumerateFiles(
                repository.EpochPlanner.EpochsDirectory
            )
        );
        JsonObject root = (JsonObject)JsonNode.Parse(
            await File.ReadAllTextAsync(epochPath)
        )!;
        JsonObject diagnostics =
            (JsonObject)root["planningDiagnostics"]!;
        if (semanticMismatch) {
            diagnostics["totalTokens"] =
                diagnostics["totalTokens"]!.GetValue<long>() + 1;
            diagnostics["retainedRecentTokens"] =
                diagnostics["retainedRecentTokens"]!
                    .GetValue<long>() + 1;
        }
        else {
            diagnostics["headerVisits"] =
                diagnostics["headerVisits"]!.GetValue<long>() + 1;
        }
        await File.WriteAllTextAsync(
            epochPath,
            root.ToJsonString()
        );

        if (semanticMismatch) {
            await Assert.ThrowsAsync<InvalidDataException>(
                async () => await repository.EpochPlanner.PlanAsync(
                    engine,
                    new("memory-pack", null, null)
                )
            );
            Assert.Empty(Directory.EnumerateFiles(
                repository.EpochPlanner.LatestEpochsDirectory
            ));
        }
        else {
            DerivedArtifactEpochPlanningResult recovered =
                await repository.EpochPlanner.PlanAsync(
                    engine,
                    new("memory-pack", null, null)
                );
            Assert.Equal(first.EpochId, recovered.Epoch!.EpochId);
            Assert.Equal(
                first.PlanningDiagnostics.HeaderVisits + 1,
                recovered.Diagnostics.HeaderVisits
            );
            Assert.Single(Directory.EnumerateFiles(
                repository.EpochPlanner.LatestEpochsDirectory
            ));
        }
    }

    [Theory]
    [InlineData("config-unknown")]
    [InlineData("config-filename")]
    [InlineData("epoch-hash")]
    [InlineData("epoch-cap")]
    public async Task StrictInventoryRejectsPlannerPersistenceCorruption(
        string corruption
    ) {
        string path = NewPath();
        using var engine = CreateSession(path);
        AppendTurns(engine, 5, "first");
        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(path);
        _ = await repository.EpochPlanner.ConfigureAsync(
            repository.Bind(engine),
            Definition(),
            null
        );
        _ = await repository.EpochPlanner.PlanAsync(
            engine,
            new("memory-pack", null, null)
        );
        string configPath = Assert.Single(
            Directory.EnumerateFiles(
                repository.EpochPlanner.ConfigsDirectory
            )
        );
        string epochPath = Assert.Single(
            Directory.EnumerateFiles(
                repository.EpochPlanner.EpochsDirectory
            )
        );
        switch (corruption) {
            case "config-unknown": {
                JsonNode root = JsonNode.Parse(
                    await File.ReadAllTextAsync(configPath)
                )!;
                root["unknown"] = true;
                await File.WriteAllTextAsync(
                    configPath,
                    root.ToJsonString()
                );
                break;
            }
            case "config-filename":
                File.Move(
                    configPath,
                    Path.Combine(
                        repository.EpochPlanner.ConfigsDirectory,
                        "dpc_" + new string('f', 64) + ".json"
                    )
                );
                break;
            case "epoch-hash": {
                JsonNode root = JsonNode.Parse(
                    await File.ReadAllTextAsync(epochPath)
                )!;
                root["measuredTokens"] =
                    root["measuredTokens"]!.GetValue<long>() + 1;
                await File.WriteAllTextAsync(
                    epochPath,
                    root.ToJsonString()
                );
                break;
            }
            case "epoch-cap":
                await File.WriteAllTextAsync(
                    epochPath,
                    new string(
                        'x',
                        checked((int)
                            DerivedArtifactEpochPlanner
                                .MaxEpochFileBytes + 1)
                    )
                );
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption));
        }

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await repository.EpochPlanner
                .ReadInventoryAsync()
        );
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await repository.ValidateAllActiveBranchesAsync()
        );
    }

    [Fact]
    public async Task EmptyPlannerInternalSymlink_IsRejectedBeforeWrite() {
        string path = NewPath();
        using var engine = CreateSession(path);
        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(path);
        string external = NewPath();
        Directory.CreateDirectory(external);
        Directory.CreateDirectory(repository.MemoryRoot);
        try {
            Directory.CreateSymbolicLink(
                repository.EpochPlanner.ConfigsDirectory,
                external
            );
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
                or IOException
                or PlatformNotSupportedException
        ) {
            return;
        }

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await repository.EpochPlanner.ConfigureAsync(
                repository.Bind(engine),
                Definition(),
                null
            )
        );
        Assert.Empty(Directory.EnumerateFiles(external));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlannerPointRead_RejectsEmptyOrDanglingInternalSymlink(
        bool dangling
    ) {
        string path = NewPath();
        using var engine = CreateSession(path);
        DerivedArtifactEpochPlanner planner =
            DerivedMemoryRepository.Open(path).EpochPlanner;
        string external = NewPath();
        if (!dangling) {
            Directory.CreateDirectory(external);
        }
        Directory.CreateDirectory(
            Path.GetDirectoryName(planner.EpochsDirectory)!
        );
        try {
            Directory.CreateSymbolicLink(
                planner.EpochsDirectory,
                external
            );
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
                or IOException
                or PlatformNotSupportedException
        ) {
            return;
        }

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await planner.TryReadEpochAsync(
                "dae_" + new string('a', 64)
            )
        );
    }

    [Fact]
    public async Task MissingLatestPointerCannotHideForkedEpochLineage() {
        string path = NewPath();
        using var engine = CreateSession(path);
        AppendTurns(engine, 5, "first");
        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(path);
        _ = await repository.EpochPlanner.ConfigureAsync(
            repository.Bind(engine),
            Definition(),
            null
        );
        _ = await repository.EpochPlanner.PlanAsync(
            engine,
            new("memory-pack", null, null)
        );
        File.Delete(Assert.Single(
            Directory.EnumerateFiles(
                repository.EpochPlanner.LatestEpochsDirectory
            )
        ));
        AppendTurns(engine, 2, "divergent");
        _ = await repository.EpochPlanner.PlanAsync(
            engine,
            new("memory-pack", null, null)
        );

        Assert.Equal(
            2,
            (await repository.EpochPlanner.ReadInventoryAsync())
                .Epochs.Count
        );
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await repository.ValidateAllActiveBranchesAsync()
        );
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await repository.EpochPlanner
                .RebuildLatestEpochPointerAsync(
                    engine,
                    "memory-pack"
                )
        );
    }

    [Fact]
    public async Task RepositoryValidationRejectsEpochConfigTopologyMismatch() {
        string path = NewPath();
        using var engine = CreateSession(path);
        AppendTurns(engine, 5, "first");
        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(path);
        _ = await repository.EpochPlanner.ConfigureAsync(
            repository.Bind(engine),
            Definition(),
            null
        );
        _ = await repository.EpochPlanner.PlanAsync(
            engine,
            new("memory-pack", null, null)
        );
        string epochPath = Assert.Single(
            Directory.EnumerateFiles(
                repository.EpochPlanner.EpochsDirectory
            )
        );
        JsonObject root = (JsonObject)JsonNode.Parse(
            await File.ReadAllTextAsync(epochPath)
        )!;
        root["topologyVersion"] = "different-topology";
        string newEpochId = RecomputeEpochId(root);
        root["epochId"] = newEpochId;
        string newPath = Path.Combine(
            repository.EpochPlanner.EpochsDirectory,
            newEpochId + ".json"
        );
        await File.WriteAllTextAsync(newPath, root.ToJsonString());
        File.Delete(epochPath);
        string pointerPath = Assert.Single(
            Directory.EnumerateFiles(
                repository.EpochPlanner.LatestEpochsDirectory
            )
        );
        JsonObject pointer = (JsonObject)JsonNode.Parse(
            await File.ReadAllTextAsync(pointerPath)
        )!;
        pointer["epochId"] = newEpochId;
        await File.WriteAllTextAsync(
            pointerPath,
            pointer.ToJsonString()
        );

        DerivedArtifactEpochInventory inventory =
            await repository.EpochPlanner.ReadInventoryAsync();
        Assert.Single(inventory.Epochs);
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await repository.ValidateBranchAgainstOpenEngineAsync(engine)
        );
        File.Delete(pointerPath);
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await repository.EpochPlanner
                .RebuildLatestEpochPointerAsync(
                    engine,
                    "memory-pack"
                )
        );
        Assert.Empty(Directory.EnumerateFiles(
            repository.EpochPlanner.LatestEpochsDirectory
        ));
    }

    [Theory]
    [InlineData("ordering")]
    [InlineData("setup")]
    [InlineData("genesis-skip")]
    [InlineData("selected-boundary")]
    [InlineData("measured-cost")]
    public async Task RepositoryValidationRejectsRehashedRawAuthorityMismatch(
        string mismatch
    ) {
        string path = NewPath();
        using var engine = CreateSession(path);
        AppendTurns(engine, 5, "first");
        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(path);
        _ = await repository.EpochPlanner.ConfigureAsync(
            repository.Bind(engine),
            Definition(),
            null
        );
        _ = await repository.EpochPlanner.PlanAsync(
            engine,
            new("memory-pack", null, null)
        );
        string epochPath = Assert.Single(
            Directory.EnumerateFiles(
                repository.EpochPlanner.EpochsDirectory
            )
        );
        JsonObject root = (JsonObject)JsonNode.Parse(
            await File.ReadAllTextAsync(epochPath)
        )!;
        DerivedArtifactEpochPlan original = Assert.Single(
            (await repository.EpochPlanner.ReadInventoryAsync()).Epochs
        );
        SessionHistoryPlanningWindow originalWindow =
            engine.ReadHistoryPlanningWindowAt(
                original.PlannedAtRawHead
            );
        switch (mismatch) {
            case "ordering":
                root["sourceEndInclusive"] =
                    root["sourceStartExclusive"]!.DeepClone();
                break;
            case "setup": {
                JsonObject setups =
                    (JsonObject)root["rawStartSetups"]!;
                JsonNode runtime =
                    setups["runtimeConfig"]!.DeepClone();
                setups["runtimeConfig"] =
                    setups["systemPrompt"]!.DeepClone();
                setups["systemPrompt"] = runtime;
                break;
            }
            case "genesis-skip": {
                SessionCurrentLineageSnapshot lineage =
                    engine.ReadCurrentLineageHeaders();
                int startIndex = lineage.HeadToRoot
                    .ToList()
                    .FindIndex(header =>
                        header.Address
                            == original.SourceStartExclusive);
                Assert.True(startIndex > 0);
                root["sourceStartExclusive"] =
                    EventAddressTextCodec.Format(
                        lineage.HeadToRoot[startIndex - 1].Address
                    );
                break;
            }
            case "selected-boundary": {
                SessionHistoryPlanningBoundary alternative =
                    originalWindow.ReplaySafeBoundaries.First(
                        boundary =>
                            boundary.CompletedUnitCount > 0
                            && boundary.Address
                                != original.SourceEndInclusive
                    );
                root["sourceEndInclusive"] =
                    EventAddressTextCodec.Format(
                        alternative.Address
                    );
                break;
            }
            case "measured-cost": {
                long measured =
                    root["measuredTokens"]!.GetValue<long>() + 1;
                root["measuredTokens"] = measured;
                ((JsonObject)root["planningDiagnostics"]!)[
                    "eligibleTokens"
                ] = measured;
                JsonObject diagnostics =
                    (JsonObject)root["planningDiagnostics"]!;
                diagnostics["totalTokens"] =
                    diagnostics["totalTokens"]!.GetValue<long>() + 1;
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(mismatch)
                );
        }
        string newEpochId = RecomputeEpochId(root);
        root["epochId"] = newEpochId;
        string newPath = Path.Combine(
            repository.EpochPlanner.EpochsDirectory,
            newEpochId + ".json"
        );
        await File.WriteAllTextAsync(newPath, root.ToJsonString());
        File.Delete(epochPath);
        string pointerPath = Assert.Single(
            Directory.EnumerateFiles(
                repository.EpochPlanner.LatestEpochsDirectory
            )
        );
        JsonObject pointer = (JsonObject)JsonNode.Parse(
            await File.ReadAllTextAsync(pointerPath)
        )!;
        pointer["epochId"] = newEpochId;
        await File.WriteAllTextAsync(
            pointerPath,
            pointer.ToJsonString()
        );

        Assert.Single(
            (await repository.EpochPlanner.ReadInventoryAsync()).Epochs
        );
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await repository.ValidateBranchAgainstOpenEngineAsync(engine)
        );
    }

    [Fact]
    public async Task RepositoryValidationUsesHeaderLineageWithoutProject() {
        string path = NewPath();
        using var engine = CreateSession(path);
        AppendTurns(engine, 5, "first");
        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(path);
        _ = await repository.EpochPlanner.ConfigureAsync(
            repository.Bind(engine),
            Definition(),
            null
        );
        _ = await repository.EpochPlanner.PlanAsync(
            engine,
            new("memory-pack", null, null)
        );
        SessionJournalReadDiagnostics before =
            engine.CaptureReadDiagnostics();

        DerivedMemoryValidationReport report =
            await repository.ValidateBranchAgainstOpenEngineAsync(engine);

        SessionJournalReadDiagnostics after =
            engine.CaptureReadDiagnostics();
        Assert.Equal(1, report.ArtifactEpochCount);
        Assert.True(
            after.HeaderPreviewReadCount
                > before.HeaderPreviewReadCount
        );
        Assert.Equal(
            before.FullProjectionInvocationCount,
            after.FullProjectionInvocationCount
        );
    }

    [Fact]
    public async Task MultiEpochLegacyValidation_BatchesStableSetupHeaderWalk() {
        string path = NewPath();
        using var engine = CreateSession(path);
        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(path);
        _ = await repository.EpochPlanner.ConfigureAsync(
            repository.Bind(engine),
            Definition(),
            null
        );
        DerivedArtifactEpochPlan? previous = null;
        DerivedArtifactSet? input = null;
        for (int epoch = 0; epoch < 4; epoch++) {
            AppendTurns(engine, 5, $"epoch-{epoch}");
            previous = (await repository.EpochPlanner.PlanAsync(
                engine,
                new(
                    "memory-pack",
                    previous?.EpochId,
                    input?.SetId
                )
            )).Epoch!;
            input = await PublishInputSetAsync(
                repository,
                engine,
                previous,
                $"test-profile-{epoch}"
            );
        }
        int lineageLength =
            engine.ReadCurrentLineageHeaders().HeadToRoot.Count;
        SessionJournalReadDiagnostics before =
            engine.CaptureReadDiagnostics();

        _ = await repository.ValidateBranchAgainstOpenEngineAsync(engine);

        SessionJournalReadDiagnostics after =
            engine.CaptureReadDiagnostics();
        long headerDelta = after.HeaderPreviewReadCount
            - before.HeaderPreviewReadCount;
        Assert.True(
            headerDelta < lineageLength * 3L,
            $"Expected one batched lineage setup pass plus incremental windows; got {headerDelta} headers for lineage {lineageLength}."
        );
        Assert.Equal(
            before.FullProjectionInvocationCount,
            after.FullProjectionInvocationCount
        );
    }

    [Fact]
    public async Task RepositoryValidationRejectsRehashedMidToolEpochEnd() {
        string path = NewPath();
        EventAddress firstResult =
            CreateMultiToolSession(path);
        using var engine = SessionJournalEngine.Open(path);
        DerivedMemoryRepository repository =
            DerivedMemoryRepository.Open(path);
        _ = await repository.EpochPlanner.ConfigureAsync(
            repository.Bind(engine),
            Definition() with {
                MinimumRecentTokens = 1,
                EpochTriggerTokens = 1,
                SchedulingHeadroomTokens = 1,
                HardLimitTokens = 1_000
            },
            null
        );
        DerivedArtifactEpochPlan original =
            (await repository.EpochPlanner.PlanAsync(
                engine,
                new("memory-pack", null, null)
            )).Epoch!;
        Assert.DoesNotContain(
            engine.ReadHistoryPlanningWindowAt(
                original.PlannedAtRawHead
            ).ReplaySafeBoundaries,
            boundary => boundary.Address == firstResult
        );
        string epochPath = Path.Combine(
            repository.EpochPlanner.EpochsDirectory,
            original.EpochId + ".json"
        );
        JsonObject root = (JsonObject)JsonNode.Parse(
            await File.ReadAllTextAsync(epochPath)
        )!;
        root["sourceEndInclusive"] =
            EventAddressTextCodec.Format(firstResult);
        string replacementId = RecomputeEpochId(root);
        root["epochId"] = replacementId;
        await File.WriteAllTextAsync(
            Path.Combine(
                repository.EpochPlanner.EpochsDirectory,
                replacementId + ".json"
            ),
            root.ToJsonString()
        );
        File.Delete(epochPath);
        string pointerPath = Assert.Single(
            Directory.EnumerateFiles(
                repository.EpochPlanner.LatestEpochsDirectory
            )
        );
        JsonObject pointer = (JsonObject)JsonNode.Parse(
            await File.ReadAllTextAsync(pointerPath)
        )!;
        pointer["epochId"] = replacementId;
        await File.WriteAllTextAsync(
            pointerPath,
            pointer.ToJsonString()
        );

        Assert.Single(
            (await repository.EpochPlanner.ReadInventoryAsync()).Epochs
        );
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await repository.ValidateBranchAgainstOpenEngineAsync(engine)
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
                // Best-effort cleanup.
            }
        }
    }

    private static SessionJournalEngine CreateSession(string path) =>
        SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        );

    private static EventAddress CreateMultiToolSession(string path) {
        using EventJournal.EventJournal journal =
            EventJournal.EventJournal.CreateNew(path);
        journal.CreateBranch(
            SessionJournalDefaults.MainBranchName,
            startPoint: null
        ).Unwrap();
        EventAddress runtime = CommitRaw(
            journal,
            null,
            SessionEventKind.RuntimeConfigSetup,
            new SessionRuntimeConfiguration(
                "model-A",
                "surface-A",
                SessionJournalDefaults.Schema,
                new(0)
            )
        );
        EventAddress prompt = CommitRaw(
            journal,
            runtime,
            SessionEventKind.SystemPromptSetup,
            new SystemPromptSetupBody("system-A")
        );
        EventAddress created = CommitRaw(
            journal,
            prompt,
            SessionEventKind.SessionCreated,
            new SessionCreatedBody(SessionCreationOrigin.Native)
        );
        EventAddress observation = CommitRaw(
            journal,
            created,
            SessionEventKind.ObservationAccepted,
            new ObservationAcceptedBody("use two tools")
        );
        string correlation =
            $"atelia.session-journal.turn.v1:{EventAddressTextCodec.Format(observation)}";
        var identity = new SessionToolRuntimeIdentity(
            "host",
            "implementations",
            "capabilities"
        );
        EventAddress action = CommitRaw(
            journal,
            observation,
            SessionEventKind.ImportedAgentAction,
            new AgentActionProducedBody(
                new ActionMessage([
                    new ActionBlock.ToolCall(
                        new RawToolCall(
                            "lookup",
                            "call-1",
                            "{}"
                        )
                    ),
                    new ActionBlock.ToolCall(
                        new RawToolCall(
                            "lookup",
                            "call-2",
                            "{}"
                        )
                    )
                ]),
                new CompletionDescriptor(
                    "import",
                    "import-v1",
                    "model-A"
                ),
                correlation,
                new SessionExecutionCheckpoint(0),
                identity
            )
        );
        EventAddress firstStarted = CommitRaw(
            journal,
            action,
            SessionEventKind.ToolExecutionStarted,
            new ToolExecutionStartedBody(
                "call-1",
                "lookup",
                "{}",
                "operation-1",
                1,
                identity
            )
        );
        EventAddress firstResult = CommitRaw(
            journal,
            firstStarted,
            SessionEventKind.ToolResultObserved,
            new ToolResultObservedBody(
                "call-1",
                "lookup",
                1,
                ToolExecutionStatus.Success,
                [new ToolResultBlock.Text("one")]
            )
        );
        EventAddress secondStarted = CommitRaw(
            journal,
            firstResult,
            SessionEventKind.ToolExecutionStarted,
            new ToolExecutionStartedBody(
                "call-2",
                "lookup",
                "{}",
                "operation-2",
                2,
                identity
            )
        );
        _ = CommitRaw(
            journal,
            secondStarted,
            SessionEventKind.ToolResultObserved,
            new ToolResultObservedBody(
                "call-2",
                "lookup",
                2,
                ToolExecutionStatus.Success,
                [new ToolResultBlock.Text("two")]
            )
        );
        return firstResult;
    }

    private static EventAddress CommitRaw(
        EventJournal.EventJournal journal,
        EventAddress? expectedParent,
        SessionEventKind kind,
        object body
    ) => journal.CommitToRef(
        SessionJournalDefaults.MainBranchName,
        expectedParent,
        SessionEventCodec.Encode(kind, body),
        opaqueEventKind: (uint)kind,
        hint: default
    ).Unwrap().EventAddress;

    private static void AppendTurns(
        SessionJournalEngine engine,
        int count,
        string prefix
    ) {
        for (int index = 0; index < count; index++) {
            _ = engine.AppendObservation(
                $"{prefix}-observation-{index}-with-token-cost"
            );
            _ = engine.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text(
                        $"{prefix}-answer-{index}-with-token-cost"
                    )
                ]),
                new CompletionDescriptor(
                    "import",
                    "import-v1",
                    "model-A"
                )
            );
        }
    }

    private static DerivedArtifactPlannerConfigDefinition Definition() =>
        new(
            "memory-pack",
            "topology-v1",
            MinimumRecentTokens: 10,
            EpochTriggerTokens: 10,
            SchedulingHeadroomTokens: 10,
            HardLimitTokens: 1_000
        );

    private static string RecomputeEpochId(JsonObject root) {
        var identity = new JsonObject {
            ["schema"] = root["schema"]!.DeepClone(),
            ["branchRefId"] = root["branchRefId"]!.DeepClone(),
            ["coherenceGroup"] = root["coherenceGroup"]!.DeepClone(),
            ["topologyVersion"] =
                root["topologyVersion"]!.DeepClone(),
            ["configId"] = root["configId"]!.DeepClone(),
            ["previousEpochId"] =
                root["previousEpochId"]?.DeepClone(),
            ["inputSetId"] = root["inputSetId"]?.DeepClone(),
            ["plannedAtRawHead"] =
                root["plannedAtRawHead"]!.DeepClone(),
            ["sourceStartExclusive"] =
                root["sourceStartExclusive"]!.DeepClone(),
            ["sourceEndInclusive"] =
                root["sourceEndInclusive"]!.DeepClone(),
            ["rawStartSetups"] =
                root["rawStartSetups"]!.DeepClone(),
            ["measuredTokens"] =
                root["measuredTokens"]!.DeepClone()
        };
        string canonical = identity.ToJsonString(
            new JsonSerializerOptions {
                WriteIndented = false,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }
        );
        byte[] prefix = Encoding.UTF8.GetBytes(
            "atelia.session-journal.derived-artifact-epoch-id.v2\0"
        );
        byte[] value = Encoding.UTF8.GetBytes(canonical);
        byte[] input = new byte[prefix.Length + value.Length];
        prefix.CopyTo(input, 0);
        value.CopyTo(input, prefix.Length);
        return "dae_" + Convert.ToHexStringLower(
            SHA256.HashData(input)
        );
    }

    private static async ValueTask<DerivedArtifactSet>
        PublishInputSetAsync(
        DerivedMemoryRepository repository,
        SessionJournalEngine engine,
        DerivedArtifactEpochPlan previous,
        string profileId = "test-profile"
    ) {
        var target = new MemoryPackBlockPath(
            MemoryPackCarrier.Observation,
            "memory.test"
        );
        IReadOnlyList<DerivedMemoryArtifactInputMember> inputMembers = [];
        string? previousRoleArtifact = null;
        if (previous.InputSetId is { } inputSetId) {
            DerivedArtifactSet inputSet =
                await repository.ArtifactSets.TryReadExactAsync(
                    inputSetId
                ) ?? throw new Xunit.Sdk.XunitException(
                    $"Missing test input set '{inputSetId}'."
                );
            inputMembers = Array.AsReadOnly([
                .. inputSet.Members
                    .OrderBy(
                        static member => member.RoleId,
                        StringComparer.Ordinal
                    )
                    .Select(static member =>
                        new DerivedMemoryArtifactInputMember(
                            member.RoleId,
                            member.ArtifactId,
                            member.Target,
                            member.ContentSha256
                        ))
            ]);
            previousRoleArtifact = inputSet.Members
                .SingleOrDefault(member => string.Equals(
                    member.RoleId,
                    "test-role",
                    StringComparison.Ordinal
                ))
                ?.ArtifactId;
        }
        var draft = new MemoryPackDraft(new MemoryPack());
        draft.UpsertBlock(target, "test memory");
        const string fingerprint =
            "sha256:2222222222222222222222222222222222222222222222222222222222222222";
        DerivedMemoryArtifact artifact =
            await repository.Artifacts.WriteCandidateAsync(
                new DerivedMemoryArtifactWriteRequest(
                    previous.EpochId,
                    DerivedMemoryMaintainerRunner
                        .GetEpochPlanFingerprint(previous),
                    "test-role",
                    profileId,
                    "tests",
                    fingerprint,
                    fingerprint,
                    fingerprint,
                    "candidate-1",
                    "attempt-1",
                    previous.PlannedAtRawHead,
                    previous.SourceStartExclusive,
                    previous.SourceEndInclusive,
                    previous.SourceEndInclusive,
                    previous.RawStartSetups,
                    engine.ResolveContextAnchorSetupReferences(
                        previous.SourceEndInclusive
                    ),
                    previous.InputSetId,
                    previousRoleArtifact,
                    inputMembers,
                    target,
                    draft.Build()
                )
            );
        var policy = new DerivedArtifactSetPolicy(
            "test-policy",
            "test-policy-v1",
            previous.CoherenceGroup,
            [
                new DerivedArtifactSetRoleRequirement(
                    "test-role",
                    target
                )
            ]
        );
        DerivedMemoryOrchestrationTransaction transaction =
            await DerivedArtifactSetTestFactory
                .CreateSettledTransactionAsync(
                    repository,
                    previous,
                    policy,
                    [artifact]
                );
        return await DerivedArtifactSetTestFactory.FinalizeAndPublishAsync(
            repository,
            engine,
            policy,
            transaction,
            artifact.AnchorSetups,
            [
                new DerivedArtifactSetMemberSelection(
                    "test-role",
                    artifact.ArtifactId
                )
            ]
        );
    }

    private string NewPath() {
        string path = Path.Combine(
            Path.GetTempPath(),
            "atelia-derived-artifact-epoch-tests",
            Guid.NewGuid().ToString("N")
        );
        _paths.Add(path);
        return path;
    }

    private static IReadOnlyDictionary<string, string> SnapshotFiles(
        string root
    ) => Directory.Exists(root)
        ? Directory.EnumerateFiles(
                root,
                "*",
                SearchOption.AllDirectories
            )
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToDictionary(
                path => Path.GetRelativePath(root, path),
                path => Convert.ToBase64String(
                    File.ReadAllBytes(path)
                ),
                StringComparer.Ordinal
            )
        : new Dictionary<string, string>(StringComparer.Ordinal);
}
