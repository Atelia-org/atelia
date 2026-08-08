using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Planner;
using Atelia.SessionJournal.DerivedRecap.Store;
using Xunit;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.Cli.Tests;

public sealed class ProgramDerivedRecapOnlineTurnTests : IDisposable {
    // Test-only cadence for compact synthetic histories. Production
    // thresholds are asserted separately by the execution CLI tests.
    private static RecapPlannerConfigDocument FastCadenceConfig =>
        RecapCliComposition.DefaultComposition.Snapshot.Document with {
            Cadence = new RecapCadenceConfigDocument(
                O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                MinimumRecentHistoryLoad: 180,
                RecapBuildIntervalHistoryLoad: 200
            )
        };

    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "atelia-derived-recap-online-cli-tests",
        Guid.NewGuid().ToString("N")
    );

    [Fact]
    public async Task IdleRunsRecapMaintenanceThenAgentCompletion() {
        const string observationSentinel =
            "online-report-fresh-observation-sentinel";
        const string answerSentinel =
            "online-report-fresh-provider-answer-sentinel";
        const string apiKeySentinel =
            "online-report-fresh-api-key-secret-sentinel";
        PublishedFixture fixture =
            await CreatePublishedFixtureAsync(
                "idle",
                initialMessage: observationSentinel,
                initialResponseText: answerSentinel,
                apiKey: apiKeySentinel
            );

        Assert.Equal(1, fixture.Factory.CreateCallCount);
        Assert.Equal(3, fixture.Factory.CallCount);
        Assert.Equal(
            SJ.SessionExecutionPhase.Idle,
            ReadBoundary(fixture.Path).Phase
        );
        Assert.True(File.Exists(fixture.OutputPath));
        AssertContentFreeOnlineReport(
            fixture.OutputPath,
            answerSentinel,
            observationSentinel,
            apiKeySentinel
        );
        using JsonDocument report = JsonDocument.Parse(
            File.ReadAllText(fixture.OutputPath)
        );
        JsonElement config =
            report.RootElement.GetProperty("config");
        Assert.Equal(
            RecapPlannerConfigLoader.GetCanonicalPath(fixture.Path),
            config.GetProperty("path").GetString()
        );
        Assert.False(string.IsNullOrWhiteSpace(
            config.GetProperty("configSha256").GetString()
        ));
        Assert.Equal(
            O200kBaseHistoryUnitLoadEstimator.EstimatorId,
            config.GetProperty("historyUnitLoadEstimatorId")
                .GetString()
        );
        JsonElement planning =
            report.RootElement.GetProperty("planning");
        Assert.Equal(
            "ExactSchedule",
            planning.GetProperty("measurementKind").GetString()
        );
        Assert.Equal(
            O200kBaseHistoryUnitLoadEstimator.EstimatorId,
            planning.GetProperty("historyUnitLoadEstimatorId")
                .GetString()
        );
        Assert.True(
            planning.GetProperty("growthHistoryLoad")
                .GetInt64() >= 0
        );
        Assert.Equal(
            2,
            Directory.EnumerateFiles(
                fixture.ExactPublishedPath,
                "*.json",
                SearchOption.AllDirectories
            ).Count(static path =>
                path.Contains(
                    $"{Path.DirectorySeparatorChar}blocks"
                    + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal
                ))
        );
        Assert.DoesNotContain(
            "derived recap",
            File.ReadAllText(fixture.OutputPath),
            StringComparison.OrdinalIgnoreCase
        );
    }

    [Fact]
    public async Task PreparedRecoveryUsesDurableRequestWithoutRecapRef() {
        const string observationSentinel =
            "online-report-prepared-observation-sentinel";
        const string answerSentinel =
            "online-report-prepared-provider-answer-sentinel";
        PublishedFixture fixture =
            await CreatePublishedFixtureAsync("prepared");
        PreparedBoundary prepared = await LeavePendingAsync(
            fixture,
            SJ.SessionJournalFailpoint.AfterRequestPreparedCommitted,
            observationSentinel
        );
        DeleteRecapAuthority(fixture.Path);

        string output = Path.Combine(_tempRoot, "prepared-reopen.json");
        string calls = Path.Combine(_tempRoot, "prepared-reopen-calls");
        var recovery = new ScriptedCompletionClientFactory(
            answerSentinel
        );

        Assert.Equal(0, Program.MainCore([
            .. BaseArgs(fixture, output, calls)
        ], recovery));

        Assert.Equal(1, recovery.CreateCallCount);
        Assert.Equal(1, recovery.CallCount);
        CompletionRequest request =
            Assert.Single(recovery.Requests);
        Assert.Equal(
            prepared.CanonicalSha256,
            Sha256(SJ.SessionRequestCanonicalizer.Canonicalize(request))
        );
        AssertRecapAuthorityAbsent(fixture.Path);
        Assert.Equal(
            SJ.SessionExecutionPhase.Idle,
            ReadBoundary(fixture.Path).Phase
        );
        AssertContentFreeOnlineReport(
            output,
            answerSentinel,
            observationSentinel
        );
        AssertReportHasNoRecapAuthority(output);
    }

    [Fact]
    public async Task StartedDefaultsToRefuseThenExplicitRestartCallsOnce() {
        const string observationSentinel =
            "online-report-started-observation-sentinel";
        const string answerSentinel =
            "online-report-started-provider-answer-sentinel";
        PublishedFixture fixture =
            await CreatePublishedFixtureAsync("started");
        _ = await LeavePendingAsync(
            fixture,
            SJ.SessionJournalFailpoint
                .AfterCompletionAttemptStartedCommitted,
            observationSentinel
        );
        DeleteRecapAuthority(fixture.Path);

        string output = Path.Combine(_tempRoot, "started-reopen.json");
        string calls = Path.Combine(_tempRoot, "started-reopen-calls");
        var refusal = new ScriptedCompletionClientFactory("must not run");
        File.WriteAllText(
            fixture.ConnectionsPath,
            "{not valid and must not be read"
        );
        Assert.Equal(1, Program.MainCore([
            .. BaseArgs(fixture, output, calls)
        ], refusal));
        Assert.Equal(0, refusal.CreateCallCount);
        Assert.Equal(0, refusal.CallCount);
        AssertRecapAuthorityAbsent(fixture.Path);
        Assert.False(File.Exists(output));
        Assert.False(Directory.Exists(calls));

        File.WriteAllText(
            fixture.ConnectionsPath,
            JsonSerializer.Serialize(
                new CompletionConnectionsFileConfig(
                    [Connection()],
                    "scripted"
                )
            )
        );

        var restart = new ScriptedCompletionClientFactory(
            answerSentinel
        );
        Assert.Equal(0, Program.MainCore([
            .. BaseArgs(fixture, output, calls),
            "--uncertain-recovery", "restart-new-attempt"
        ], restart));
        Assert.Equal(1, restart.CallCount);
        AssertRecapAuthorityAbsent(fixture.Path);
        Assert.Equal(
            SJ.SessionExecutionPhase.Idle,
            ReadBoundary(fixture.Path).Phase
        );
        AssertContentFreeOnlineReport(
            output,
            answerSentinel,
            observationSentinel
        );
        AssertReportHasNoRecapAuthority(output);
    }

    [Fact]
    public async Task PreparedRecoveryIgnoresNewDefaultAndBindsDurableConnection() {
        PublishedFixture fixture =
            await CreatePublishedFixtureAsync("prepared-default-switch");
        _ = await LeavePendingAsync(
            fixture,
            SJ.SessionJournalFailpoint.AfterRequestPreparedCommitted
        );
        DeleteRecapAuthority(fixture.Path);
        string switchedConnections = WriteConnections(
            "prepared-default-switch-v2",
            [
                Connection(),
                Connection(
                    id: "scripted-b",
                    modelId: "model-b",
                    completionSurfaceId: "surface-b"
                )
            ],
            defaultConnectionId: "scripted-b"
        );
        string output = Path.Combine(
            _tempRoot,
            "prepared-default-switch.json"
        );
        string calls = Path.Combine(
            _tempRoot,
            "prepared-default-switch-calls"
        );
        var recovery = new ScriptedCompletionClientFactory(
            "durable A recovered"
        );

        Assert.Equal(0, Program.MainCore([
            "run-online-turn",
            "--input", fixture.Path,
            "--branch", fixture.BranchName,
            "--connections", switchedConnections,
            "--output", output,
            "--call-log-dir", calls
        ], recovery));

        Assert.Equal(["scripted"], recovery.CreatedConnectionIds);
        using var reopened = SJ.SessionJournalEngine.OpenReadOnly(
            fixture.Path
        );
        SJ.SessionGoverningSetup setup = reopened.ResolveGoverningSetup(
            reopened.InspectExecutionBoundary().Head!.Value
        );
        Assert.Equal("model-a", setup.RuntimeConfig.ModelId);
        Assert.Equal("surface-a", setup.RuntimeConfig.CompletionSurfaceId);
    }

    [Fact]
    public async Task MissingPreparedConnectionDoesNotFallbackOrMutateRaw() {
        PublishedFixture fixture =
            await CreatePublishedFixtureAsync("prepared-missing");
        _ = await LeavePendingAsync(
            fixture,
            SJ.SessionJournalFailpoint.AfterRequestPreparedCommitted
        );
        DeleteRecapAuthority(fixture.Path);
        RawSnapshot before = ReadRawSnapshot(fixture.Path);
        string switchedConnections = WriteConnections(
            "prepared-missing-v2",
            [Connection(
                id: "scripted-b",
                modelId: "model-b",
                completionSurfaceId: "surface-b"
            )],
            defaultConnectionId: "scripted-b"
        );
        string output = Path.Combine(_tempRoot, "prepared-missing.json");
        string calls = Path.Combine(
            _tempRoot,
            "prepared-missing-calls"
        );
        var recovery = new ScriptedCompletionClientFactory("must not run");

        Assert.Equal(1, Program.MainCore([
            "run-online-turn",
            "--input", fixture.Path,
            "--branch", fixture.BranchName,
            "--connections", switchedConnections,
            "--output", output,
            "--call-log-dir", calls
        ], recovery));

        Assert.Equal(0, recovery.CreateCallCount);
        Assert.Equal(0, recovery.CallCount);
        Assert.Equal(before, ReadRawSnapshot(fixture.Path));
        Assert.False(File.Exists(output));
        Assert.False(Directory.Exists(calls));
    }

    [Fact]
    public async Task IdleConnectionSwitchReconcilesSetupBeforeNewTurn() {
        PublishedFixture fixture =
            await CreatePublishedFixtureAsync("idle-switch");
        string switchedConnections = WriteConnections(
            "idle-switch-v2",
            [
                Connection(),
                Connection(
                    id: "scripted-b",
                    modelId: "model-b",
                    completionSurfaceId: "surface-b"
                )
            ],
            defaultConnectionId: "scripted-b"
        );
        string output = Path.Combine(_tempRoot, "idle-switch.json");
        string calls = Path.Combine(_tempRoot, "idle-switch-calls");
        var factory = new ScriptedCompletionClientFactory("answer from B");

        Assert.Equal(0, Program.MainCore([
            "run-online-turn",
            "--input", fixture.Path,
            "--branch", fixture.BranchName,
            "--connections", switchedConnections,
            "--output", output,
            "--call-log-dir", calls,
            "--message", "switch to B"
        ], factory));

        Assert.Equal(["scripted-b"], factory.CreatedConnectionIds);
        CompletionRequest agentRequest = factory.Requests[^1];
        Assert.Equal("model-b", agentRequest.ModelId);
        using var reopened = SJ.SessionJournalEngine.OpenReadOnly(
            fixture.Path
        );
        SJ.SessionCurrentLineageSnapshot lineage =
            reopened.ReadCurrentLineageHeaders();
        SJ.SessionGoverningSetup setup = reopened.ResolveGoverningSetup(
            lineage.CapturedHead
        );
        Assert.Equal("model-b", setup.RuntimeConfig.ModelId);
        Assert.Equal("surface-b", setup.RuntimeConfig.CompletionSurfaceId);
        Assert.Equal("system-a", setup.SystemPrompt);
        int runtimeIndex = lineage.HeadToRoot
            .Select((entry, index) => (entry, index))
            .Single(pair =>
                pair.entry.Kind == SJ.SessionEventKind.RuntimeConfigSetup
                && pair.entry.Address == setup.RuntimeConfigSetupAddress
            ).index;
        int observationIndex = lineage.HeadToRoot
            .Select((entry, index) => (entry, index))
            .First(pair =>
                pair.entry.Kind == SJ.SessionEventKind.ObservationAccepted
            ).index;
        Assert.True(observationIndex < runtimeIndex);
    }

    [Fact]
    public async Task ConnectionSwitchIntentSurvivesLaterRecapReadinessFailure() {
        PublishedFixture fixture =
            await CreatePublishedFixtureAsync("switch-before-recap-failure");
        string configPath =
            RecapPlannerConfigLoader.GetCanonicalPath(fixture.Path);
        File.WriteAllText(configPath, "{\"schema\":");
        RawSnapshot before = ReadRawSnapshot(fixture.Path);
        string switchedConnections = WriteConnections(
            "switch-before-recap-failure-v2",
            [Connection(
                id: "scripted-b",
                modelId: "model-b",
                completionSurfaceId: "surface-b"
            )],
            defaultConnectionId: "scripted-b"
        );
        string output = Path.Combine(
            _tempRoot,
            "switch-before-recap-failure.json"
        );
        string calls = Path.Combine(
            _tempRoot,
            "switch-before-recap-failure-calls"
        );
        var factory = new ScriptedCompletionClientFactory("must not run");

        Assert.Equal(1, Program.MainCore([
            "run-online-turn",
            "--input", fixture.Path,
            "--branch", fixture.BranchName,
            "--connections", switchedConnections,
            "--output", output,
            "--call-log-dir", calls,
            "--message", "must remain uncommitted"
        ], factory));

        RawSnapshot after = ReadRawSnapshot(fixture.Path);
        Assert.NotEqual(before.Head, after.Head);
        Assert.Equal(before.EventCount + 1, after.EventCount);
        Assert.Equal(SJ.SessionExecutionPhase.Idle, after.Phase);
        Assert.Equal(0, factory.CreateCallCount);
        Assert.Equal(0, factory.CallCount);
        Assert.False(File.Exists(output));
        Assert.False(Directory.Exists(calls));
        using var reopened = SJ.SessionJournalEngine.OpenReadOnly(
            fixture.Path
        );
        Assert.Equal(
            SJ.SessionEventKind.RuntimeConfigSetup,
            reopened.InspectExecutionBoundary().HeadKind
        );
        SJ.SessionGoverningSetup setup = reopened.ResolveGoverningSetup(
            after.Head
        );
        Assert.Equal("model-b", setup.RuntimeConfig.ModelId);
        Assert.Equal("surface-b", setup.RuntimeConfig.CompletionSurfaceId);
        Assert.Equal("system-a", setup.SystemPrompt);
    }

    [Fact]
    public async Task AcceptedObservationRejectsConnectionDriftBeforeStoreOrClient() {
        PublishedFixture fixture =
            await CreatePublishedFixtureAsync("observation-drift");
        using (var engine = SJ.SessionJournalEngine.Open(
                   fixture.Path,
                   fixture.BranchName
               )) {
            engine.AppendObservation("already accepted under A");
        }
        RawSnapshot before = ReadRawSnapshot(fixture.Path);
        string switchedConnections = WriteConnections(
            "observation-drift-v2",
            [Connection(
                id: "scripted-b",
                modelId: "model-b",
                completionSurfaceId: "surface-b"
            )],
            defaultConnectionId: "scripted-b"
        );
        string output = Path.Combine(_tempRoot, "observation-drift.json");
        string calls = Path.Combine(_tempRoot, "observation-drift-calls");
        var factory = new ScriptedCompletionClientFactory("must not run");

        Assert.Equal(1, Program.MainCore([
            "run-online-turn",
            "--input", fixture.Path,
            "--branch", fixture.BranchName,
            "--connections", switchedConnections,
            "--output", output,
            "--call-log-dir", calls
        ], factory));

        Assert.Equal(0, factory.CreateCallCount);
        Assert.Equal(0, factory.CallCount);
        Assert.Equal(before, ReadRawSnapshot(fixture.Path));
        Assert.False(File.Exists(output));
        Assert.False(Directory.Exists(calls));
    }

    [Fact]
    public void AbsentRepositoryIsUnavailableWithoutAutoProvisioning() {
        Directory.CreateDirectory(_tempRoot);
        string path = Path.Combine(_tempRoot, "absent-session-repo");
        string connections = WriteConnections("absent-session-repo");
        string output = Path.Combine(_tempRoot, "absent-output.json");
        string calls = Path.Combine(_tempRoot, "absent-calls");
        var factory = new ScriptedCompletionClientFactory("must not run");

        Assert.Equal(1, Program.MainCore([
            "run-online-turn",
            "--input", path,
            "--branch", SJ.SessionJournalDefaults.MainBranchName,
            "--connections", connections,
            "--output", output,
            "--call-log-dir", calls,
            "--message", "must not provision"
        ], factory));

        Assert.False(Directory.Exists(path));
        Assert.Equal(0, factory.CreateCallCount);
        Assert.False(File.Exists(output));
        Assert.False(Directory.Exists(calls));
    }

    [Fact]
    public void EmptyJournalShellIsUnavailableWithoutCompletingProvisioning() {
        Directory.CreateDirectory(_tempRoot);
        string path = Path.Combine(_tempRoot, "empty-journal-shell");
        using (EventJournal.EventJournal journal =
               EventJournal.EventJournal.CreateNew(path)) {
            _ = journal.CreateBranch(
                SJ.SessionJournalDefaults.MainBranchName,
                startPoint: null
            ).Unwrap();
        }
        string[] before = ReadRepositoryFileSnapshot(path);
        string connections = WriteConnections("empty-journal-shell");
        string output = Path.Combine(_tempRoot, "empty-shell-output.json");
        string calls = Path.Combine(_tempRoot, "empty-shell-calls");
        var factory = new ScriptedCompletionClientFactory("must not run");

        Assert.Equal(1, Program.MainCore([
            "run-online-turn",
            "--input", path,
            "--branch", SJ.SessionJournalDefaults.MainBranchName,
            "--connections", connections,
            "--output", output,
            "--call-log-dir", calls,
            "--message", "must not complete provisioning"
        ], factory));

        Assert.Equal(before, ReadRepositoryFileSnapshot(path));
        Assert.Equal(0, factory.CreateCallCount);
        Assert.False(Directory.Exists(Path.Combine(path, "config")));
        Assert.False(Directory.Exists(Path.Combine(path, "derived")));
        Assert.False(File.Exists(output));
        Assert.False(Directory.Exists(calls));
    }

    [Fact]
    public void ValidRawRepoWithoutRecapStoreIsReadOnlyUnavailable() {
        Directory.CreateDirectory(_tempRoot);
        string path = Path.Combine(_tempRoot, "missing-recap-store");
        using (SJ.SessionJournalEngine.Create(
                   path,
                   new SJ.SessionCreateOptions(
                       "model-a",
                       "system-a",
                       "surface-a"
                   )
               )) {
        }
        InitializePlannerConfig(path);
        string[] before = ReadRepositoryFileSnapshot(path);
        string connections = WriteConnections("missing-recap-store");
        string output = Path.Combine(
            _tempRoot,
            "missing-recap-store-output.json"
        );
        string calls = Path.Combine(
            _tempRoot,
            "missing-recap-store-calls"
        );
        var factory = new ScriptedCompletionClientFactory("must not run");

        Assert.Equal(1, Program.MainCore([
            "run-online-turn",
            "--input", path,
            "--branch", SJ.SessionJournalDefaults.MainBranchName,
            "--connections", connections,
            "--output", output,
            "--call-log-dir", calls,
            "--message", "must not create Store"
        ], factory));

        Assert.Equal(before, ReadRepositoryFileSnapshot(path));
        Assert.False(Directory.Exists(Path.Combine(path, "derived")));
        Assert.Equal(0, factory.CreateCallCount);
        Assert.Equal(0, factory.CallCount);
        Assert.False(File.Exists(output));
        Assert.False(Directory.Exists(calls));
    }

    [Fact]
    public async Task TurnFailedRejectsBeforeReadingConnectionsOrOpeningStore() {
        Directory.CreateDirectory(_tempRoot);
        string path = Path.Combine(_tempRoot, "turn-failed");
        var failing = new KnownFailureCompletionClient();
        var connection = Connection();
        var runtime = new SJ.SessionRuntime(
            failing,
            CompletionTarget:
                CompletionTargetIdentityFactory.Create(
                    connection,
                    failing
                ),
            ContextCandidateSource:
                new EmptyContextCandidateSource()
        );
        using (var engine = SJ.SessionJournalEngine.Create(
                   path,
                   new SJ.SessionCreateOptions(
                       "model-a",
                       "system-a",
                       "surface-a"
                   )
               )) {
            engine.UseRuntime(runtime);
            await Assert.ThrowsAsync<SJ.SessionJournalTurnAbortedException>(
                () => engine.SendAsync(
                    "known failure",
                    CancellationToken.None
                )
            );
            Assert.Equal(
                SJ.SessionExecutionPhase.TurnFailed,
                engine.InspectExecutionBoundary().Phase
            );
            var requirement = Assert.IsType<
                SJ.SessionRuntimeRecoveryRequirements
                    .FailedTurnMustBeAbandoned
            >(engine.InspectRuntimeRecoveryRequirements());
            Assert.Equal(
                engine.ReadCurrentHead()!.Value,
                requirement.FailedHead
            );
        }
        RawSnapshot before = ReadRawSnapshot(path);
        string connections = WriteConnections("turn-failed");
        File.WriteAllText(connections, "{not valid json");
        string output = Path.Combine(_tempRoot, "turn-failed-output.json");
        string calls = Path.Combine(_tempRoot, "turn-failed-calls");
        var factory = new ScriptedCompletionClientFactory("must not run");

        Assert.Equal(1, Program.MainCore([
            "run-online-turn",
            "--input", path,
            "--branch", SJ.SessionJournalDefaults.MainBranchName,
            "--connections", connections,
            "--output", output,
            "--call-log-dir", calls,
            "--message", "must abandon first"
        ], factory));

        Assert.Equal(before, ReadRawSnapshot(path));
        Assert.Equal(0, factory.CreateCallCount);
        Assert.Equal(0, factory.CallCount);
        Assert.False(Directory.Exists(Path.Combine(path, "derived")));
        Assert.False(File.Exists(output));
        Assert.False(Directory.Exists(calls));
    }

    [Fact]
    public async Task MessageAndRetiredOptionsFailBeforeClientOrWrites() {
        PublishedFixture fixture =
            await CreatePublishedFixtureAsync("options");
        string output = Path.Combine(_tempRoot, "invalid-options.json");
        string calls = Path.Combine(_tempRoot, "invalid-options-calls");
        var factory = new ScriptedCompletionClientFactory("must not run");

        Assert.Equal(1, Program.MainCore([
            .. BaseArgs(fixture, output, calls),
            "--message", "not valid while prepared",
            "--role", "required:autobiographical-rewrite:produce"
        ], factory));

        Assert.Equal(0, factory.CreateCallCount);
        Assert.Equal(0, factory.CallCount);
        Assert.False(File.Exists(output));
        Assert.False(Directory.Exists(calls));
    }

    [Fact]
    public async Task ExistingBuildingResumesWithoutConfigAndIsNotRunTwice() {
        Directory.CreateDirectory(_tempRoot);
        string path = Path.Combine(_tempRoot, "frozen-building");
        string connections = WriteConnections("frozen-building");
        string output =
            Path.Combine(_tempRoot, "frozen-building-online.json");
        string calls =
            Path.Combine(_tempRoot, "frozen-building-calls");
        RefId branchRefId;
        EventAddress admissionAnchor;
        using (var engine = SJ.SessionJournalEngine.Create(
                   path,
                   new SJ.SessionCreateOptions(
                       "model-a",
                       "system-a",
                       "surface-a"
                   )
               )) {
            for (int index = 0; index < 22; index++) {
                engine.AppendObservation($"observation {index}");
                _ = engine.AppendImportedAgentAction(
                    new ActionMessage([
                        new ActionBlock.Text($"action {index}")
                    ]),
                    new CompletionDescriptor(
                        "import",
                        "import-v1",
                        "model-a"
                    )
                );
            }
            branchRefId = engine.BranchRefId;
            SJ.SessionCurrentLineageSnapshot lineage =
                engine.ReadCurrentLineageHeaders();
            admissionAnchor = lineage.CapturedHead;
            EventAddress replayStart =
                engine.ReadHistoryPlanningWindow()
                    .StartExclusive;
            DerivedRecapStore store =
                DerivedRecapStore.Open(path, branchRefId);
            await store.CreateAsync();
            IReadOnlyList<RecapBlockPlan> plans = Array.AsReadOnly([
                .. RecapCliComposition.DefaultComposition
                    .PlanningInputs.OrderedCatalog.Select(entry =>
                        new MaintainRecapBlockPlan(
                            entry.RecapBlockId,
                            entry.Target,
                            entry.MaintainerId,
                            entry.MaintainerCapabilityFingerprint,
                            new EmptyRecapMaintainSource(
                                replayStart,
                                engine.ResolveContextAnchorSetupReferences(
                                    replayStart
                                )
                            ),
                            [
                                new RecapReplayBoundary(
                                    admissionAnchor,
                                    engine.ResolveContextAnchorSetupReferences(
                                        admissionAnchor
                                    )
                                )
                            ],
                            DerivedRecapCodec
                                .ComputePriorContextPayloadSha256(
                                    EmptyRecapPriorContext.Instance
                                ),
                            entry.MaxContentUtf8Bytes
                        )
                    )
            ]);
            Assert.IsType<CreateBuildingResult.Created>(
                await new DerivedRecapBuildingInstaller(
                    store,
                    engine.ReadView
                )
                    .InstallAsync(
                        DerivedRecapCodec.CreateManifest(
                            branchRefId,
                            admissionAnchor,
                            engine.ResolveContextAnchorSetupReferences(
                                admissionAnchor
                            ),
                            EmptyRecapPriorContext.Instance,
                            plans
                        ),
                        admissionAnchor
                    )
            );
        }
        Assert.False(File.Exists(
            RecapPlannerConfigLoader.GetCanonicalPath(path)
        ));
        var factory = new ScriptedCompletionClientFactory(
            "frozen recap or agent answer"
        );

        Assert.Equal(0, Program.MainCore([
            "run-online-turn",
            "--input", path,
            "--branch", SJ.SessionJournalDefaults.MainBranchName,
            "--connections", connections,
            "--output", output,
            "--call-log-dir", calls,
            "--message", "new online observation"
        ], factory));

        Assert.Equal(1, factory.CreateCallCount);
        Assert.Equal(3, factory.CallCount);
        Assert.False(File.Exists(
            RecapPlannerConfigLoader.GetCanonicalPath(path)
        ));
        Assert.IsType<BuildingPlanReadResult.Missing>(
            await DerivedRecapStore.Open(path, branchRefId)
                .ReadBuildingPlanAsync(admissionAnchor)
        );
        using (var engine = SJ.SessionJournalEngine.OpenReadOnly(path)) {
            DerivedRecapStore store =
                DerivedRecapStore.Open(path, branchRefId);
            Assert.IsType<DerivedRecapSelection.Selected>(
                await DerivedRecapLineageView
                    .Capture(store, engine.ReadView)
                    .SelectNthPreviousAsync(0)
            );
        }
        AssertReportHasNoRecapAuthority(output);
    }

    [Fact]
    public async Task ObservationAcceptedResumesWithoutMessage() {
        PublishedFixture fixture =
            await CreatePublishedFixtureAsync("observation");
        using (var engine = SJ.SessionJournalEngine.Open(
                   fixture.Path,
                   fixture.BranchName
               )) {
            engine.AppendObservation("already durable observation");
            Assert.Equal(
                SJ.SessionExecutionPhase.AwaitingAgentAction,
                engine.InspectExecutionBoundary().Phase
            );
        }
        string output =
            Path.Combine(_tempRoot, "observation-resume.json");
        string calls =
            Path.Combine(_tempRoot, "observation-resume-calls");
        var factory = new ScriptedCompletionClientFactory(
            "observation resumed answer"
        );

        Assert.Equal(0, Program.MainCore([
            .. BaseArgs(fixture, output, calls)
        ], factory));

        Assert.True(factory.CallCount >= 1);
        Assert.Equal(
            SJ.SessionExecutionPhase.Idle,
            ReadBoundary(fixture.Path).Phase
        );
        Assert.True(File.Exists(output));
    }

    [Fact]
    public async Task BelowCadenceLegacyRepoUsesRawHistoryWithoutPublishing() {
        Directory.CreateDirectory(_tempRoot);
        string path = Path.Combine(_tempRoot, "below-cadence");
        string connections = WriteConnections("below-cadence");
        string output =
            Path.Combine(_tempRoot, "below-cadence-online.json");
        string calls =
            Path.Combine(_tempRoot, "below-cadence-calls");
        RefId branchRefId;
        using (var writer = SJ.SessionJournalLegacyImportWriter.Create(
                   path,
                   new SJ.SessionCreateOptions(
                       "model-a",
                       "system-a",
                       "surface-a"
                   )
               )) {
            for (int index = 0; index < 16; index++) {
                writer.AppendObservation($"observation {index}");
                _ = writer.AppendImportedAgentAction(
                    new ActionMessage([
                        new ActionBlock.Text($"action {index}")
                    ]),
                    new CompletionDescriptor(
                        "import",
                        "import-v1",
                        "model-a"
                    )
                );
            }
        }
        using (var engine = SJ.SessionJournalEngine.OpenReadOnly(path)) {
            branchRefId = engine.BranchRefId;
        }
        await DerivedRecapStore.Open(path, branchRefId)
            .CreateAsync();
        InitializePlannerConfig(path);
        var factory = new ScriptedCompletionClientFactory(
            "raw history answer"
        );

        Assert.Equal(0, Program.MainCore([
            "run-online-turn",
            "--input", path,
            "--branch", SJ.SessionJournalDefaults.MainBranchName,
            "--connections", connections,
            "--output", output,
            "--call-log-dir", calls,
            "--message", "new online observation"
        ], factory));

        Assert.Equal(1, factory.CreateCallCount);
        Assert.Equal(1, factory.CallCount);
        string callLog = Assert.Single(
            Directory.EnumerateFiles(
                calls,
                "*.json",
                SearchOption.TopDirectoryOnly
            )
        );
        using (JsonDocument call = JsonDocument.Parse(
                   File.ReadAllText(callLog)
               )) {
            JsonElement context =
                call.RootElement.GetProperty("context");
            Assert.Equal(
                ["command"],
                context.EnumerateObject()
                    .Select(static property => property.Name)
                    .ToArray()
            );
            Assert.Equal(
                "run-online-turn/agent",
                context.GetProperty("command").GetString()
            );
        }
        CompletionRequest request = Assert.Single(factory.Requests);
        Assert.Equal(33, request.PromptPrefix.SharedContextMessages.Length);
        Assert.Equal(
            "observation 0",
            Assert.IsType<ObservationMessage>(request.PromptPrefix.SharedContextMessages[0]).Content
        );
        Assert.Equal(
            "action 0",
            Assert.IsType<ActionMessage>(request.PromptPrefix.SharedContextMessages[1])
                .GetFlattenedText()
        );
        Assert.Equal(
            "new online observation",
            Assert.IsType<ObservationMessage>(request.PromptPrefix.SharedContextMessages[^1]).Content
        );
        using var reopened =
            SJ.SessionJournalEngine.OpenReadOnly(path);
        DerivedRecapStore store =
            DerivedRecapStore.Open(path, branchRefId);
        DerivedRecapSelection selection =
            await DerivedRecapLineageView
                .Capture(store, reopened.ReadView)
                .SelectNthPreviousAsync(0);
        Assert.IsType<DerivedRecapSelection.EmptyLineage>(selection);
        Assert.Equal(
            SJ.SessionExecutionPhase.Idle,
            reopened.InspectExecutionBoundary().Phase
        );
        Assert.True(File.Exists(output));
        using JsonDocument report = JsonDocument.Parse(
            File.ReadAllText(output)
        );
        Assert.NotEqual(
            JsonValueKind.Null,
            report.RootElement.GetProperty("config").ValueKind
        );
        Assert.Equal(
            "ExactSchedule",
            report.RootElement
                .GetProperty("planning")
                .GetProperty("measurementKind")
                .GetString()
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrInvalidConfigBlocksBeforeClientLogOrRaw(
        bool invalid
    ) {
        Directory.CreateDirectory(_tempRoot);
        string name = invalid ? "invalid-config" : "missing-config";
        string path = Path.Combine(_tempRoot, name);
        string connections = WriteConnections(name);
        string output = Path.Combine(_tempRoot, $"{name}-online.json");
        string calls = Path.Combine(_tempRoot, $"{name}-calls");
        RefId branchRefId;
        using (var engine = SJ.SessionJournalEngine.Create(
                   path,
                   new SJ.SessionCreateOptions(
                       "model-a",
                       "system-a",
                       "surface-a"
                   )
               )) {
            for (int index = 0; index < 22; index++) {
                engine.AppendObservation($"observation {index}");
                _ = engine.AppendImportedAgentAction(
                    new ActionMessage([
                        new ActionBlock.Text($"action {index}")
                    ]),
                    new CompletionDescriptor(
                        "import",
                        "import-v1",
                        "model-a"
                    )
                );
            }
            branchRefId = engine.BranchRefId;
            await DerivedRecapStore.Open(path, branchRefId)
                .CreateAsync();
        }
        string configPath =
            RecapPlannerConfigLoader.GetCanonicalPath(path);
        if (invalid) {
            Directory.CreateDirectory(
                Path.GetDirectoryName(configPath)!
            );
            File.WriteAllText(configPath, "{\"schema\":");
        }
        RawSnapshot before = ReadRawSnapshot(path);
        var factory =
            new ScriptedCompletionClientFactory("must not run");

        Assert.Equal(1, Program.MainCore([
            "run-online-turn",
            "--input", path,
            "--branch", SJ.SessionJournalDefaults.MainBranchName,
            "--connections", connections,
            "--output", output,
            "--call-log-dir", calls,
            "--message", "must not append"
        ], factory));

        Assert.Equal(0, factory.CreateCallCount);
        Assert.Equal(0, factory.CallCount);
        Assert.Equal(before, ReadRawSnapshot(path));
        Assert.False(File.Exists(output));
        Assert.False(Directory.Exists(calls));
    }

    [Fact]
    public void AwaitingToolExecutionIsRejectedWithoutStoreOrClient() {
        Directory.CreateDirectory(_tempRoot);
        string path = Path.Combine(_tempRoot, "tool-phase");
        string connections = WriteConnections("tool-phase");
        string output = Path.Combine(_tempRoot, "tool-output.json");
        string calls = Path.Combine(_tempRoot, "tool-calls");
        var client = new ScriptedCompletionClient("unused");
        var toolSession = new ToolRegistry([
            new FixedTool("lookup")
        ]).CreateSession();
        var runtime = new SJ.SessionRuntime(
            client,
            ToolSession: toolSession,
            ToolRuntimeIdentity: new SJ.SessionToolRuntimeIdentity(
                "test-tools",
                "implementations-v1",
                "capabilities-v1"
            )
        );
        using (var engine = SJ.SessionJournalEngine.Create(
                   path,
                   new SJ.SessionCreateOptions(
                       "model-a",
                       "system-a",
                       "surface-a"
                   )
               )) {
            engine.UseRuntime(runtime);
            engine.AppendObservation("use a tool");
            _ = engine.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.ToolCall(
                        new RawToolCall("lookup", "call-1", "{}")
                    )
                ]),
                new CompletionDescriptor(
                    "import",
                    "import-v1",
                    "model-a"
                )
            );
            Assert.Equal(
                SJ.SessionExecutionPhase.AwaitingToolExecution,
                engine.InspectExecutionBoundary().Phase
            );
        }
        var factory = new ScriptedCompletionClientFactory("must not run");

        Assert.Equal(1, Program.MainCore([
            "run-online-turn",
            "--input", path,
            "--branch", SJ.SessionJournalDefaults.MainBranchName,
            "--connections", connections,
            "--output", output,
            "--call-log-dir", calls
        ], factory));

        Assert.Equal(0, factory.CreateCallCount);
        Assert.Equal(0, factory.CallCount);
        Assert.False(Directory.Exists(
            Path.Combine(path, "derived", "recap", "v4")
        ));
        Assert.False(File.Exists(output));
        Assert.False(Directory.Exists(calls));
    }

    [Fact]
    public async Task ToolResultObservedIsRejectedWithoutStoreOrClient() {
        Directory.CreateDirectory(_tempRoot);
        string path = Path.Combine(_tempRoot, "tool-result-phase");
        string connections = WriteConnections("tool-result-phase");
        string output =
            Path.Combine(_tempRoot, "tool-result-output.json");
        string calls =
            Path.Combine(_tempRoot, "tool-result-calls");
        var toolSession = new ToolRegistry([
            new FixedTool("lookup")
        ]).CreateSession();
        var runtime = new SJ.SessionRuntime(
            new ToolCallCompletionClient("lookup", "call-1"),
            ToolSession: toolSession,
            CompletionTarget:
                new SJ.SessionCompletionTargetIdentity(
                    "tool-test",
                    "scripted",
                    "tool-test-connection-v1",
                    "tool-test-adapter-v1"
                ),
            ToolRuntimeIdentity: new SJ.SessionToolRuntimeIdentity(
                "test-tools",
                "implementations-v1",
                "capabilities-v1"
            ),
            ContextCandidateSource:
                new EmptyContextCandidateSource()
        );
        using (var engine =
               SJ.SessionJournalEngine.CreateForTest(
                   path,
                   new SJ.SessionCreateOptions(
                       "model-a",
                       "system-a",
                       "surface-a"
                   ),
                   runtime,
                   new SJ.SessionJournalTestHooks(
                       SJ.SessionJournalFailpoint
                           .AfterToolResultCommitted
                   )
               )) {
            SJ.SessionJournalFailpointException failure =
                await Assert.ThrowsAsync<
                    SJ.SessionJournalFailpointException
                >(() => engine.SendAsync(
                    "use a tool",
                    CancellationToken.None
                ));
            Assert.Equal(
                SJ.SessionJournalFailpoint.AfterToolResultCommitted,
                failure.Failpoint
            );
            Assert.Equal(
                SJ.SessionExecutionPhase.AwaitingAgentAction,
                engine.InspectExecutionBoundary().Phase
            );
            Assert.Equal(
                SJ.SessionEventKind.ToolResultObserved,
                engine.InspectExecutionBoundary().HeadKind
            );
        }
        var factory =
            new ScriptedCompletionClientFactory("must not run");

        Assert.Equal(1, Program.MainCore([
            "run-online-turn",
            "--input", path,
            "--branch", SJ.SessionJournalDefaults.MainBranchName,
            "--connections", connections,
            "--output", output,
            "--call-log-dir", calls
        ], factory));

        Assert.Equal(0, factory.CreateCallCount);
        Assert.Equal(0, factory.CallCount);
        Assert.False(Directory.Exists(
            Path.Combine(path, "derived", "recap", "v4")
        ));
        Assert.False(File.Exists(output));
        Assert.False(Directory.Exists(calls));
    }

    public void Dispose() {
        try {
            if (Directory.Exists(_tempRoot)) {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch {
            // Best-effort cleanup for test-owned repositories.
        }
    }

    private async Task<PublishedFixture> CreatePublishedFixtureAsync(
        string name,
        string initialMessage = "new online observation",
        string initialResponseText = "derived recap or agent answer",
        string? apiKey = null
    ) {
        Directory.CreateDirectory(_tempRoot);
        string path = Path.Combine(_tempRoot, name);
        string connections = WriteConnections(
            name,
            [Connection() with { ApiKey = apiKey }],
            defaultConnectionId: "scripted"
        );
        string output = Path.Combine(_tempRoot, $"{name}-online.json");
        string calls = Path.Combine(_tempRoot, $"{name}-online-calls");
        string branchName;
        RefId branchRefId;
        using (var engine = SJ.SessionJournalEngine.Create(
                   path,
                   new SJ.SessionCreateOptions(
                       "model-a",
                       "system-a",
                       "surface-a"
                   )
               )) {
            for (int index = 0; index < 22; index++) {
                engine.AppendObservation($"observation {index}");
                _ = engine.AppendImportedAgentAction(
                    new ActionMessage([
                        new ActionBlock.Text($"action {index}")
                    ]),
                    new CompletionDescriptor(
                        "import",
                        "import-v1",
                        "model-a"
                    )
                );
            }
            branchName = engine.BranchName;
            branchRefId = engine.BranchRefId;
            await DerivedRecapStore.Open(path, branchRefId)
                .CreateAsync();
            InitializePlannerConfig(path);
        }
        var factory = new ScriptedCompletionClientFactory(
            initialResponseText
        );
        Assert.Equal(0, Program.MainCore([
            "run-online-turn",
            "--input", path,
            "--branch", branchName,
            "--connections", connections,
            "--output", output,
            "--call-log-dir", calls,
            "--message", initialMessage
        ], factory));

        PublishedRecapDescriptor descriptor;
        using (var engine =
            SJ.SessionJournalEngine.OpenReadOnly(path, branchName)) {
            DerivedRecapStore store =
                DerivedRecapStore.Open(path, branchRefId);
            DerivedRecapSelection selection =
                await DerivedRecapLineageView
                    .Capture(store, engine.ReadView)
                    .SelectNthPreviousAsync(0);
            descriptor = Assert
                .IsType<DerivedRecapSelection.Selected>(selection)
                .Descriptor;
        }
        string exactPublishedPath = Path.Combine(
            path,
            "derived",
            "recap",
            "v4",
            "refs",
            branchRefId.ToHexString(),
            "published",
            EventAddressFileNameCodec.Format(
                descriptor.SetAdmissionAnchor
            )
        );
        Assert.True(Directory.Exists(exactPublishedPath));
        return new PublishedFixture(
            path,
            branchName,
            connections,
            output,
            calls,
            branchRefId,
            descriptor,
            exactPublishedPath,
            factory
        );
    }

    private static async Task<PreparedBoundary> LeavePendingAsync(
        PublishedFixture fixture,
        SJ.SessionJournalFailpoint failpoint,
        string observation = "pending recovery observation"
    ) {
        CompletionConnectionConfig connection = Connection();
        var client = new ScriptedCompletionClient("must not run");
        var placeholder = new SJ.SessionRuntime(client);
        EventAddress pendingAddress;
        using (var engine = SJ.SessionJournalEngine.OpenForTest(
                   fixture.Path,
                   fixture.BranchName,
                   placeholder,
                   new SJ.SessionJournalTestHooks(failpoint)
               )) {
            DerivedRecapStore store = DerivedRecapStore.Open(
                fixture.Path,
                fixture.BranchRefId
            );
            var source = new DerivedRecapContextCandidateSource(
                store,
                engine.ReadView
            );
            engine.UseRuntime(new SJ.SessionRuntime(
                client,
                CompletionTarget:
                    CompletionTargetIdentityFactory.Create(
                        connection,
                        client
                    ),
                MaxTokens: connection.MaxTokens,
                ContextCandidateSource: source
            ));
            SJ.SessionJournalFailpointException failure =
                await Assert.ThrowsAsync<
                    SJ.SessionJournalFailpointException
                >(() => engine.SendAsync(
                    observation,
                    CancellationToken.None
                ));
            Assert.Equal(failpoint, failure.Failpoint);
            Assert.Equal(0, client.CallCount);
            pendingAddress = engine
                .InspectExecutionBoundary()
                .Head!.Value;
        }

        EventAddress preparedAddress =
            failpoint
                == SJ.SessionJournalFailpoint
                    .AfterRequestPreparedCommitted
            ? pendingAddress
            : FindLatestPrepared(fixture.Path);
        using EventJournal.EventJournal journal =
            EventJournal.EventJournal.OpenReadOnlyExisting(
                fixture.Path
            );
        byte[] canonical =
            SJ.SessionPreparedRequestReconstructor.Reconstruct(
                journal,
                preparedAddress
            ).CanonicalBytes;
        return new PreparedBoundary(
            preparedAddress,
            Sha256(canonical)
        );
    }

    private static EventAddress FindLatestPrepared(string path) {
        using EventJournal.EventJournal journal =
            EventJournal.EventJournal.OpenReadOnlyExisting(path);
        RefId branch = journal.OpenBranch(
            SJ.SessionJournalDefaults.MainBranchName
        ).Unwrap();
        EventAddress head = journal.GetHead(branch)!.Value;
        return journal
            .ReadChronologicalChain(head, checkedRead: true)
            .Unwrap()
            .Last(address =>
                journal.ReadEventHeaderPreview(address)
                    .Unwrap()
                    .OpaqueEventKind
                == (uint)SJ.SessionEventKind
                    .CompletionRequestPrepared);
    }

    private static SJ.SessionExecutionBoundaryInspection ReadBoundary(
        string path
    ) {
        using var engine = SJ.SessionJournalEngine.OpenReadOnly(path);
        return engine.InspectExecutionBoundary();
    }

    private static RawSnapshot ReadRawSnapshot(string path) {
        using var engine = SJ.SessionJournalEngine.OpenReadOnly(path);
        SJ.SessionCurrentLineageSnapshot lineage =
            engine.ReadCurrentLineageHeaders();
        return new RawSnapshot(
            lineage.CapturedHead,
            lineage.HeadToRoot.Count,
            engine.InspectExecutionBoundary().Phase
        );
    }

    private static string[] ReadRepositoryFileSnapshot(string path)
        => Directory.EnumerateFiles(
                path,
                "*",
                SearchOption.AllDirectories
            )
            .Order(StringComparer.Ordinal)
            .Select(file =>
                Path.GetRelativePath(path, file)
                + ":"
                + new FileInfo(file).Length
                + ":"
                + Sha256(File.ReadAllBytes(file))
            )
            .ToArray();

    private static void InitializePlannerConfig(string path) {
        Assert.IsType<RecapPlannerConfigInitializeResult.Initialized>(
            RecapPlannerConfigInitializer.Initialize(
                path,
                FastCadenceConfig
            )
        );
    }

    private static void DeleteRecapAuthority(string path) {
        string configRoot = Path.Combine(path, "config");
        if (Directory.Exists(configRoot)) {
            Directory.Delete(configRoot, recursive: true);
        }
        string derivedRoot =
            Path.Combine(path, "derived", "recap", "v4");
        if (Directory.Exists(derivedRoot)) {
            Directory.Delete(derivedRoot, recursive: true);
        }
        AssertRecapAuthorityAbsent(path);
    }

    private static void AssertRecapAuthorityAbsent(string path) {
        Assert.False(Directory.Exists(
            Path.Combine(path, "config")
        ));
        Assert.False(Directory.Exists(
            Path.Combine(path, "derived", "recap", "v4")
        ));
    }

    private static void AssertContentFreeOnlineReport(
        string outputPath,
        string providerAnswer,
        params string[] forbiddenValues
    ) {
        string json = File.ReadAllText(outputPath);
        using JsonDocument report = JsonDocument.Parse(json);
        JsonElement root = report.RootElement;

        Assert.Equal(
            "atelia.session-journal.online-turn-run.v6",
            root.GetProperty("schema").GetString()
        );
        Assert.Equal(
            [
                "schema",
                "branchName",
                "branchRefId",
                "head",
                "phase",
                "providerId",
                "apiSpecId",
                "model",
                "errorCount",
                "config",
                "planning"
            ],
            root.EnumerateObject()
                .Select(static property => property.Name)
                .ToArray()
        );

        foreach (string forbiddenProperty in new[] {
                     "actionSha256",
                     "action",
                     "message",
                     "request",
                     "response",
                     "content",
                     "apiKey",
                     "secret"
                 }) {
            Assert.False(
                root.TryGetProperty(forbiddenProperty, out _),
                $"Online report must not expose '{forbiddenProperty}'."
            );
        }
        Assert.False(
            root.EnumerateObject().Any(static property =>
                property.Name.StartsWith(
                    "callLog",
                    StringComparison.Ordinal
                )),
            "Online report must not expose call-log fields."
        );

        foreach (string sensitiveValue in forbiddenValues.Prepend(
                     providerAnswer
                 )) {
            Assert.DoesNotContain(
                sensitiveValue,
                json,
                StringComparison.Ordinal
            );
            string legacyContentDigest = Sha256(
                System.Text.Encoding.UTF8.GetBytes(sensitiveValue)
            );
            Assert.DoesNotContain(
                legacyContentDigest,
                json,
                StringComparison.OrdinalIgnoreCase
            );
        }
    }

    private static void AssertReportHasNoRecapAuthority(
        string outputPath
    ) {
        using JsonDocument report = JsonDocument.Parse(
            File.ReadAllText(outputPath)
        );
        Assert.Equal(
            JsonValueKind.Null,
            report.RootElement.GetProperty("config").ValueKind
        );
        Assert.Equal(
            JsonValueKind.Null,
            report.RootElement.GetProperty("planning").ValueKind
        );
    }

    private string[] BaseArgs(
        PublishedFixture fixture,
        string output,
        string calls
    ) => [
        "run-online-turn",
        "--input", fixture.Path,
        "--branch", fixture.BranchName,
        "--connections", fixture.ConnectionsPath,
        "--output", output,
        "--call-log-dir", calls
    ];

    private string WriteConnections(string name) => WriteConnections(
        name,
        [Connection()],
        defaultConnectionId: "scripted"
    );

    private string WriteConnections(
        string name,
        IReadOnlyList<CompletionConnectionConfig> connections,
        string defaultConnectionId
    ) {
        string path = Path.Combine(
            _tempRoot,
            $"{name}-connections.json"
        );
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                new CompletionConnectionsFileConfig(
                    connections,
                    defaultConnectionId
                )
            )
        );
        return path;
    }

    private static CompletionConnectionConfig Connection(
        string id = "scripted",
        string modelId = "model-a",
        string completionSurfaceId = "surface-a"
    ) => new(
        id,
        "scripted",
        modelId,
        completionSurfaceId,
        "http://localhost/"
    );

    private static string Sha256(ReadOnlySpan<byte> bytes)
        => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed record PublishedFixture(
        string Path,
        string BranchName,
        string ConnectionsPath,
        string OutputPath,
        string CallsPath,
        RefId BranchRefId,
        PublishedRecapDescriptor Descriptor,
        string ExactPublishedPath,
        ScriptedCompletionClientFactory Factory
    );

    private sealed record PreparedBoundary(
        EventAddress PreparedAddress,
        string CanonicalSha256
    );

    private sealed record RawSnapshot(
        EventAddress Head,
        int EventCount,
        SJ.SessionExecutionPhase Phase
    );

    private sealed class ScriptedCompletionClientFactory(
        string responseText
    ) : ICompletionClientFactory {
        private readonly ScriptedCompletionClient _client =
            new(responseText);
        private readonly ConcurrentQueue<string> _createdConnectionIds =
            new();

        public int CreateCallCount { get; private set; }
        public int CallCount => _client.CallCount;
        public IReadOnlyList<CompletionRequest> Requests =>
            _client.Requests;
        public IReadOnlyList<string> CreatedConnectionIds =>
            _createdConnectionIds.ToArray();

        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) {
            CreateCallCount++;
            _createdConnectionIds.Enqueue(connection.Id);
            return _client;
        }
    }

    private sealed class ScriptedCompletionClient(
        string responseText
    ) : ICompletionClient {
        private readonly ConcurrentQueue<CompletionRequest> _requests =
            new();
        private int _callCount;

        public string Name => "scripted";
        public string ApiSpecId => "test-api-v1";
        public int CallCount => Volatile.Read(ref _callCount);
        public IReadOnlyList<CompletionRequest> Requests =>
            _requests.ToArray();

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            _requests.Enqueue(request);
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(new CompletionResult(
                new ActionMessage([
                    new ActionBlock.Text(responseText)
                ]),
                CompletionDescriptor.From(this, request)
            ));
        }
    }

    private sealed class KnownFailureCompletionClient
        : ICompletionClient {
        public string Name => "scripted";

        public string ApiSpecId => "test-api-v1";

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new CompletionResult(
                new ActionMessage([new ActionBlock.Text("partial")]),
                CompletionDescriptor.From(this, request),
                termination: CompletionTermination.Failed("known")
            ));
        }
    }

    private sealed class ToolCallCompletionClient(
        string toolName,
        string callId
    ) : ICompletionClient {
        public string Name => "scripted";
        public string ApiSpecId => "test-api-v1";

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new CompletionResult(
                new ActionMessage([
                    new ActionBlock.ToolCall(
                        new RawToolCall(toolName, callId, "{}")
                    )
                ]),
                new CompletionDescriptor(
                    Name,
                    ApiSpecId,
                    request.ModelId
                )
            ));
        }
    }

    private sealed class EmptyContextCandidateSource
        : SJ.ICoherentContextCandidateSource {
        public ValueTask<SJ.SessionContextCandidateSelection>
            SelectAsync(
            SJ.SessionContextSelectionRequest request,
            CancellationToken cancellationToken
        ) {
            request.ValidateShape();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new SJ.SessionContextCandidateSelection(
                    SJ.SessionContextCandidateSelectionStatus
                        .EmptyLineage,
                    Candidate: null
                )
            );
        }

        public ValueTask<SJ.SessionContextCandidate>
            MaterializeAsync(
            SJ.SessionContextCandidateDescriptor descriptor,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException(
            "Fresh bootstrap must not materialize a candidate."
        );
    }

    private sealed class FixedTool(string name) : ITool {
        public ToolDefinition Definition { get; } =
            new(name, $"Tool {name}.", new ToolSchema.Object());

        public ValueTask<ToolExecuteResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(
            ToolExecuteResult.FromText(
                ToolExecutionStatus.Success,
                "unused"
            )
        );
    }
}
