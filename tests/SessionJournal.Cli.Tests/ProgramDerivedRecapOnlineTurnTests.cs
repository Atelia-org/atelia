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
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "atelia-derived-recap-online-cli-tests",
        Guid.NewGuid().ToString("N")
    );

    [Fact]
    public async Task IdleRunsRecapMaintenanceThenAgentCompletion() {
        PublishedFixture fixture =
            await CreatePublishedFixtureAsync("idle");

        Assert.Equal(1, fixture.Factory.CreateCallCount);
        Assert.Equal(3, fixture.Factory.CallCount);
        Assert.Equal(
            SJ.SessionExecutionPhase.Idle,
            ReadBoundary(fixture.Path).Phase
        );
        Assert.True(File.Exists(fixture.OutputPath));
        using JsonDocument report = JsonDocument.Parse(
            File.ReadAllText(fixture.OutputPath)
        );
        Assert.Equal(
            "atelia.session-journal.online-turn-run.v4",
            report.RootElement.GetProperty("schema").GetString()
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
            "HeaderNegative",
            report.RootElement
                .GetProperty("planning")
                .GetProperty("measurementKind")
                .GetString()
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
        PublishedFixture fixture =
            await CreatePublishedFixtureAsync("prepared");
        PreparedBoundary prepared = await LeavePendingAsync(
            fixture,
            SJ.SessionJournalFailpoint.AfterRequestPreparedCommitted
        );
        DeleteRecapAuthority(fixture.Path);

        string output = Path.Combine(_tempRoot, "prepared-reopen.json");
        string calls = Path.Combine(_tempRoot, "prepared-reopen-calls");
        var recovery = new ScriptedCompletionClientFactory(
            "prepared recovered answer"
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
        AssertReportHasNoRecapAuthority(output);
    }

    [Fact]
    public async Task StartedDefaultsToRefuseThenExplicitRestartCallsOnce() {
        PublishedFixture fixture =
            await CreatePublishedFixtureAsync("started");
        _ = await LeavePendingAsync(
            fixture,
            SJ.SessionJournalFailpoint
                .AfterCompletionAttemptStartedCommitted
        );
        DeleteRecapAuthority(fixture.Path);

        string output = Path.Combine(_tempRoot, "started-reopen.json");
        string calls = Path.Combine(_tempRoot, "started-reopen-calls");
        var refusal = new ScriptedCompletionClientFactory("must not run");
        Assert.Equal(1, Program.MainCore([
            .. BaseArgs(fixture, output, calls)
        ], refusal));
        Assert.Equal(0, refusal.CreateCallCount);
        Assert.Equal(0, refusal.CallCount);
        AssertRecapAuthorityAbsent(fixture.Path);
        Assert.False(File.Exists(output));
        Assert.False(Directory.Exists(calls));

        var restart = new ScriptedCompletionClientFactory(
            "started recovered answer"
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
        AssertReportHasNoRecapAuthority(output);
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
                            new EmptyRecapMaintainSource(
                                replayStart
                            ),
                            [admissionAnchor],
                            EmptyRecapPriorContext.Instance,
                            entry.MaxContentUtf8Bytes
                        )
                    )
            ]);
            Assert.IsType<CreateBuildingResult.Created>(
                await store.CreateBuildingAsync(
                    DerivedRecapCodec.CreateManifest(
                        branchRefId,
                        admissionAnchor,
                        plans
                    )
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
        Assert.IsType<BuildingReadResult.Missing>(
            await DerivedRecapStore.Open(path, branchRefId)
                .ReadBuildingAsync(admissionAnchor)
        );
        using (var engine = SJ.SessionJournalEngine.OpenReadOnly(path)) {
            Assert.IsType<DerivedRecapSelection.Selected>(
                await DerivedRecapStore.Open(path, branchRefId)
                    .SelectNthPreviousAsync(
                        engine.ReadCurrentLineageHeaders(),
                        0
                    )
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
        using (var engine = SJ.SessionJournalEngine.Create(
                   path,
                   new SJ.SessionCreateOptions(
                       "model-a",
                       "system-a",
                       "surface-a"
                   ) {
                       Origin = SJ.SessionCreationOrigin.LegacyImport
                   }
               )) {
            for (int index = 0; index < 16; index++) {
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
            InitializePlannerConfig(path);
        }
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
        CompletionRequest request = Assert.Single(factory.Requests);
        Assert.Equal(33, request.Context.Count);
        Assert.Equal(
            "observation 0",
            Assert.IsType<ObservationMessage>(request.Context[0]).Content
        );
        Assert.Equal(
            "action 0",
            Assert.IsType<ActionMessage>(request.Context[1])
                .GetFlattenedText()
        );
        Assert.Equal(
            "new online observation",
            Assert.IsType<ObservationMessage>(request.Context[^1]).Content
        );
        using var reopened =
            SJ.SessionJournalEngine.OpenReadOnly(path);
        DerivedRecapSelection selection =
            await DerivedRecapStore.Open(path, branchRefId)
                .SelectNthPreviousAsync(
                    reopened.ReadCurrentLineageHeaders(),
                    0
                );
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
            "HeaderNegative",
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
                   ),
                   runtime
               )) {
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
        string name
    ) {
        Directory.CreateDirectory(_tempRoot);
        string path = Path.Combine(_tempRoot, name);
        string connections = WriteConnections(name);
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
            "derived recap or agent answer"
        );
        Assert.Equal(0, Program.MainCore([
            "run-online-turn",
            "--input", path,
            "--branch", branchName,
            "--connections", connections,
            "--output", output,
            "--call-log-dir", calls,
            "--message", "new online observation"
        ], factory));

        PublishedRecapDescriptor descriptor;
        using (var engine =
            SJ.SessionJournalEngine.OpenReadOnly(path, branchName)) {
            DerivedRecapSelection selection =
                await DerivedRecapStore.Open(path, branchRefId)
                    .SelectNthPreviousAsync(
                        engine.ReadCurrentLineageHeaders(),
                        0
                    );
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
        SJ.SessionJournalFailpoint failpoint
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
                engine
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
                    "pending recovery observation",
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

    private static void InitializePlannerConfig(string path) {
        Assert.IsType<RecapPlannerConfigInitializeResult.Initialized>(
            RecapPlannerConfigInitializer.Initialize(
                path,
                RecapCliComposition.DefaultComposition
                    .Snapshot.Document
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

    private string WriteConnections(string name) {
        string path = Path.Combine(
            _tempRoot,
            $"{name}-connections.json"
        );
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                new CompletionConnectionsFileConfig(
                    [Connection()],
                    "scripted"
                )
            )
        );
        return path;
    }

    private static CompletionConnectionConfig Connection() => new(
        "scripted",
        "scripted",
        "model-a",
        "surface-a",
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

        public int CreateCallCount { get; private set; }
        public int CallCount => _client.CallCount;
        public IReadOnlyList<CompletionRequest> Requests =>
            _client.Requests;

        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) {
            CreateCallCount++;
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
