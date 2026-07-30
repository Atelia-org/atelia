using System.Diagnostics;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Planner.Tests;

public sealed class DerivedRecapAcceptanceTests {
    private const int MaxContent = 4096;

    [Fact]
    public async Task GalateaMultiCursorPublishesOnlySetAdmissions() {
        using AcceptanceFixture fixture =
            await AcceptanceFixture.CreateAsync();
        EventAddress[] a = fixture.AppendNumberedPairs(20);
        EventAddress replayStart =
            fixture.Engine.ReadHistoryPlanningWindow().StartExclusive;
        var id = new RecapBlockId("roleplay.client");
        var target = new ContextHeaderBlockPath(
            ContextHeaderCarrier.System,
            id.Value
        );
        var maintainer = new DeterministicMaintainer(
            "roleplay.client-maintainer",
            target
        );
        int phase = 0;
        var policy = new DelegatePolicy(context => {
            phase++;
            return phase switch {
                1 => new RecapPlanningPolicyDecision.Build(
                    a[1],
                    [
                        new RecapBlockPlanningDecision.Maintain(
                            id,
                            new RecapPlanningMaintainSource.Empty(
                                replayStart
                            ),
                            [a[1]],
                            EmptyRecapPriorContext.Instance
                        )
                    ]
                ),
                2 => Inherit(context, a[8]),
                3 => Inherit(context, a[12]),
                4 => MaintainExisting(
                    context,
                    a[20],
                    [a[5], a[11], a[20]]
                ),
                _ => throw new InvalidOperationException(
                    "Unexpected Galatea planning phase."
                )
            };

            RecapPlanningPolicyDecision.Build Inherit(
                RecapPlanningPolicyContext current,
                EventAddress admission
            ) => new(
                admission,
                [
                    new RecapBlockPlanningDecision.Inherit(
                        id,
                        Assert.Single(
                            current.PolicyFacts.AvailableSources
                        ).Source
                    )
                ]
            );

            RecapPlanningPolicyDecision.Build MaintainExisting(
                RecapPlanningPolicyContext current,
                EventAddress admission,
                IReadOnlyList<EventAddress> route
            ) => new(
                admission,
                [
                    new RecapBlockPlanningDecision.Maintain(
                        id,
                        new RecapPlanningMaintainSource.Existing(
                            Assert.Single(
                                current.PolicyFacts.AvailableSources
                            ).Source
                        ),
                        route,
                        EmptyRecapPriorContext.Instance
                    )
                ]
            );
        });
        DerivedRecapPlannerExecutor executor =
            fixture.CreateExecutor(
                [new RecapBlockCatalogEntry(
                    id,
                    target,
                    maintainer.Id,
                    MaxContent
                )],
                policy,
                [maintainer]
            );

        PublishedRecapDescriptor atA1 =
            await RunPublishedAsync(executor);
        Assert.Equal(a[1], atA1.SetAdmissionAnchor);
        Assert.Equal(1, maintainer.CallCount);

        PublishedRecapDescriptor atA8 =
            await RunPublishedAsync(executor);
        PublishedRecapDescriptor atA12 =
            await RunPublishedAsync(executor);
        Assert.Equal(a[8], atA8.SetAdmissionAnchor);
        Assert.Equal(a[12], atA12.SetAdmissionAnchor);
        Assert.Equal(1, maintainer.CallCount);
        Assert.Equal(
            a[1],
            await ReadCursorAsync(fixture.Store, atA8, id)
        );
        Assert.Equal(
            a[1],
            await ReadCursorAsync(fixture.Store, atA12, id)
        );

        PublishedRecapDescriptor atA20 =
            await RunPublishedAsync(executor);
        Assert.Equal(a[20], atA20.SetAdmissionAnchor);
        Assert.Equal(4, maintainer.CallCount);
        PublishedRecapSourceSnapshot final =
            await ReadSourceAsync(fixture.Store, atA20, id);
        Assert.Equal(
            a[20],
            Assert.Single(final.FrozenInputs).AbsorbedThrough
        );
        MaintainRecapBlockPlan finalPlan =
            Assert.IsType<MaintainRecapBlockPlan>(
                Assert.Single(
                    final.Publication.FrozenPlanSnapshot.Blocks
                )
            );
        ExistingRecapMaintainSource finalSource =
            Assert.IsType<ExistingRecapMaintainSource>(
                finalPlan.Source
            );
        Assert.Equal(a[12], finalSource.SourceSetAnchor);
        Assert.Equal([a[5], a[11], a[20]], finalPlan.CatchUpThrough);

        SessionCurrentLineageSnapshot lineage =
            fixture.Engine.ReadCurrentLineageHeaders();
        EventAddress[] expectedOrdinals = [
            a[20],
            a[12],
            a[8],
            a[1]
        ];
        for (int ordinal = 0;
             ordinal < expectedOrdinals.Length;
             ordinal++) {
            var selected =
                Assert.IsType<DerivedRecapSelection.Selected>(
                    await fixture.Store.SelectNthPreviousAsync(
                        lineage,
                        ordinal
                    )
                );
            Assert.Equal(
                expectedOrdinals[ordinal],
                selected.Descriptor.SetAdmissionAnchor
            );
        }
        foreach (EventAddress progressOnly in new[] { a[5], a[11] }) {
            Assert.IsType<PublishedRecapSourceReadResult.Missing>(
                await fixture.Store.ReadPublishedSourceAsync(
                    new PublishedRecapDescriptor(
                        fixture.Engine.BranchRefId,
                        progressOnly,
                        new string('a', 64)
                    ),
                    [id]
                )
            );
        }
    }

    [Theory]
    [InlineData(1, 1, 2)]
    [InlineData(3, 3, 0)]
    public async Task ProcessCrashResumesOnlyMissingRouteSuffix(
        int failAfterCheckpoint,
        int callsBeforeReopen,
        int callsAddedAfterReopen
    ) {
        if (!OperatingSystem.IsLinux()) {
            return;
        }
        using AcceptanceFixture fixture =
            await AcceptanceFixture.CreateAsync();
        EventAddress[] a = fixture.AppendNumberedPairs(20);
        var id = new RecapBlockId("roleplay.client");
        var target = new ContextHeaderBlockPath(
            ContextHeaderCarrier.System,
            id.Value
        );
        var plan = new MaintainRecapBlockPlan(
            id,
            target,
            "roleplay.client-maintainer",
            new EmptyRecapMaintainSource(a[1]),
            [a[5], a[11], a[20]],
            EmptyRecapPriorContext.Instance,
            MaxContent
        );
        CreateBuildingResult.Created created =
            Assert.IsType<CreateBuildingResult.Created>(
                await fixture.Store.CreateBuildingAsync(
                    DerivedRecapCodec.CreateManifest(
                        fixture.Engine.BranchRefId,
                        a[20],
                        [plan]
                    )
                )
            );
        fixture.CloseEngine();

        string failpoint =
            $"executor-work-after-{failAfterCheckpoint}";
        await RunHarnessAsync(
            fixture.Path,
            failpoint,
            expectCrash: true
        );
        Assert.Equal(callsBeforeReopen, fixture.ReadCallCount());

        string output = await RunHarnessAsync(
            fixture.Path,
            "none",
            expectCrash: false
        );
        Assert.Contains(
            "executor-result:Published",
            output,
            StringComparison.Ordinal
        );
        Assert.Equal(
            callsBeforeReopen + callsAddedAfterReopen,
            fixture.ReadCallCount()
        );
        Assert.Equal(3, fixture.ReadCallCount());

        fixture.ReopenEngine();
        var selected =
            Assert.IsType<DerivedRecapSelection.Selected>(
                await fixture.Store.SelectNthPreviousAsync(
                    fixture.Engine.ReadCurrentLineageHeaders(),
                    0
                )
            );
        Assert.Equal(a[20], selected.Descriptor.SetAdmissionAnchor);
        Assert.IsType<BuildingReadResult.Missing>(
            await fixture.Store.ReadBuildingAsync(
                created.Descriptor.SetAdmissionAnchor
            )
        );
        foreach (EventAddress progressOnly in new[] { a[5], a[11] }) {
            Assert.IsType<PublishedRecapSourceReadResult.Missing>(
                await fixture.Store.ReadPublishedSourceAsync(
                    new PublishedRecapDescriptor(
                        fixture.Engine.BranchRefId,
                        progressOnly,
                        new string('a', 64)
                    ),
                    [id]
                )
            );
        }
    }

    [Fact]
    public async Task ReopenDoesNotRerunHealthyAlphaAfterZetaFailure() {
        if (!OperatingSystem.IsLinux()) {
            return;
        }
        using AcceptanceFixture fixture =
            await AcceptanceFixture.CreateAsync();
        EventAddress[] a = fixture.AppendNumberedPairs(3);
        EventAddress admission = a[3];
        EventAddress replayStart =
            fixture.Engine.ReadHistoryPlanningWindow().StartExclusive;
        RecapBlockPlan[] plans = [
            CreatePlan("alpha", "alpha-maintainer"),
            CreatePlan("zeta", "zeta-maintainer")
        ];
        _ = Assert.IsType<CreateBuildingResult.Created>(
            await fixture.Store.CreateBuildingAsync(
                DerivedRecapCodec.CreateManifest(
                    fixture.Engine.BranchRefId,
                    admission,
                    plans
                )
            )
        );
        fixture.CloseEngine();

        string first = await RunHarnessAsync(
            fixture.Path,
            "none",
            expectCrash: false
        );
        Assert.Contains(
            "executor-result:BlockFailed",
            first,
            StringComparison.Ordinal
        );
        Assert.Equal(
            1,
            fixture.ReadCallCount("alpha-maintainer")
        );
        Assert.Equal(
            1,
            fixture.ReadCallCount("zeta-maintainer")
        );

        string second = await RunHarnessAsync(
            fixture.Path,
            "none",
            expectCrash: false
        );
        Assert.Contains(
            "executor-result:Published",
            second,
            StringComparison.Ordinal
        );
        Assert.Equal(
            1,
            fixture.ReadCallCount("alpha-maintainer")
        );
        Assert.Equal(
            2,
            fixture.ReadCallCount("zeta-maintainer")
        );

        RecapBlockPlan CreatePlan(
            string blockId,
            string maintainerId
        ) {
            var id = new RecapBlockId(blockId);
            return new MaintainRecapBlockPlan(
                id,
                new ContextHeaderBlockPath(
                    ContextHeaderCarrier.System,
                    id.Value
                ),
                maintainerId,
                new EmptyRecapMaintainSource(replayStart),
                [admission],
                EmptyRecapPriorContext.Instance,
                MaxContent
            );
        }
    }

    private static async ValueTask<PublishedRecapDescriptor>
        RunPublishedAsync(DerivedRecapPlannerExecutor executor) {
        var published =
            Assert.IsType<DerivedRecapExecutionResult.Published>(
                await executor.RunAsync()
            );
        return published.Descriptor;
    }

    private static async ValueTask<EventAddress> ReadCursorAsync(
        DerivedRecapStore store,
        PublishedRecapDescriptor descriptor,
        RecapBlockId blockId
    ) => Assert.Single(
        (await ReadSourceAsync(store, descriptor, blockId))
            .FrozenInputs
    ).AbsorbedThrough;

    private static async ValueTask<PublishedRecapSourceSnapshot>
        ReadSourceAsync(
        DerivedRecapStore store,
        PublishedRecapDescriptor descriptor,
        RecapBlockId blockId
    ) {
        var available =
            Assert.IsType<PublishedRecapSourceReadResult.Available>(
                await store.ReadPublishedSourceAsync(
                    descriptor,
                    [blockId]
                )
            );
        return available.Snapshot;
    }

    private static async Task<string> RunHarnessAsync(
        string repositoryPath,
        string failpoint,
        bool expectCrash
    ) {
        var startInfo = new ProcessStartInfo("dotnet") {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = repositoryPath
        };
        startInfo.ArgumentList.Add(GetCrashHarnessPath());
        startInfo.ArgumentList.Add("executor-resume");
        startInfo.ArgumentList.Add(failpoint);
        startInfo.ArgumentList.Add(repositoryPath);
        startInfo.Environment["COMPlus_DbgEnableMiniDump"] = "0";
        startInfo.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Failed to start DerivedRecap acceptance harness."
            );
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync()
            .WaitAsync(TimeSpan.FromSeconds(30));
        string output = await stdout;
        string error = await stderr;
        if (expectCrash) {
            Assert.NotEqual(0, process.ExitCode);
            Assert.NotEqual(3, process.ExitCode);
            Assert.Contains(
                $"Intentional DerivedRecap crash at '{failpoint}'",
                output + error,
                StringComparison.Ordinal
            );
        }
        else {
            Assert.True(
                process.ExitCode == 0,
                $"Harness failed ({process.ExitCode}): "
                + output + error
            );
        }
        return output + error;
    }

    private static string GetCrashHarnessPath() {
        string repositoryRoot = FindRepositoryRoot();
        string configuration =
            Directory.GetParent(
                Path.TrimEndingDirectorySeparator(
                    AppContext.BaseDirectory
                )
            )?.Name ?? "Debug";
        return Path.Combine(
            repositoryRoot,
            "tests",
            "SessionJournal.DerivedRecap.Store.CrashHarness",
            "bin",
            configuration,
            "net10.0",
            "Atelia.SessionJournal.DerivedRecap.Store.CrashHarness.dll"
        );
    }

    private static string FindRepositoryRoot() {
        DirectoryInfo? cursor = new(AppContext.BaseDirectory);
        while (cursor is not null) {
            if (File.Exists(Path.Combine(cursor.FullName, "Atelia.sln"))) {
                return cursor.FullName;
            }
            cursor = cursor.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate the Atelia repository root."
        );
    }

    private sealed class DelegatePolicy : IRecapPlanningPolicy {
        private readonly Func<
            RecapPlanningPolicyContext,
            RecapPlanningPolicyDecision
        > _decide;

        public DelegatePolicy(
            Func<
                RecapPlanningPolicyContext,
                RecapPlanningPolicyDecision
            > decide
        ) {
            _decide = decide;
        }

        public RecapPlanningPolicyDecision Decide(
            RecapPlanningPolicyContext context
        ) => _decide(context);
    }

    private sealed class DeterministicMaintainer
        : IRecapBlockMaintainer {
        public DeterministicMaintainer(
            string id,
            ContextHeaderBlockPath target
        ) {
            Id = id;
            Target = target;
        }

        public string Id { get; }
        public ContextHeaderBlockPath Target { get; }
        public int CallCount { get; private set; }

        public ValueTask<RecapBlockMaintenanceResult> MaintainAsync(
            RecapBlockMaintenanceRequest request,
            CancellationToken ct
        ) {
            ct.ThrowIfCancellationRequested();
            CallCount++;
            return ValueTask.FromResult(
                new RecapBlockMaintenanceResult(
                    Id,
                    Target,
                    new ContextHeaderBlock(
                        request.OldBlock.Text
                        + $"|review:{CallCount}"
                    )
                )
            );
        }
    }

    private sealed class AcceptanceFixture : IDisposable {
        private AcceptanceFixture(
            string path,
            SessionJournalEngine engine,
            DerivedRecapStore store
        ) {
            Path = path;
            Engine = engine;
            Store = store;
        }

        public string Path { get; }
        public SessionJournalEngine Engine { get; private set; }
        public DerivedRecapStore Store { get; private set; }

        public static async ValueTask<AcceptanceFixture>
            CreateAsync() {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "atelia-derived-recap-acceptance",
                Guid.NewGuid().ToString("N")
            );
            SessionJournalEngine engine =
                SessionJournalEngine.Create(
                    path,
                    new SessionCreateOptions(
                        "model-a",
                        "system-a",
                        "surface-a"
                    )
                );
            DerivedRecapStore store = DerivedRecapStore.Open(
                path,
                engine.BranchRefId
            );
            await store.CreateAsync();
            return new AcceptanceFixture(path, engine, store);
        }

        public EventAddress[] AppendNumberedPairs(int count) {
            var addresses = new EventAddress[count + 1];
            for (int index = 1; index <= count; index++) {
                Engine.AppendObservation($"A{index} observation");
                addresses[index] =
                    Engine.AppendImportedAgentAction(
                        new ActionMessage([
                            new ActionBlock.Text(
                                $"A{index} answer"
                            )
                        ]),
                        new CompletionDescriptor(
                            "import",
                            "v1",
                            "model-a"
                        )
                    );
            }
            return addresses;
        }

        public DerivedRecapPlannerExecutor CreateExecutor(
            IReadOnlyList<RecapBlockCatalogEntry> catalog,
            IRecapPlanningPolicy policy,
            IReadOnlyList<IRecapBlockMaintainer> maintainers
        ) => new(
            Engine,
            Store,
            new RecapPlannerConfig(
                catalog,
                new RecapCadenceConfig(
                    minimumRecentHistoryUnitCount: 0,
                    recapBuildIntervalUnitCount: 1
                ),
                maxRawGrowthEventCount: 10_000,
                maxRouteEndpointsPerBlock: 16,
                maxMaintainerCallsPerBuild: 32,
                maxRawEventsPerStep: 10_000,
                maxRawEventsPerBuild: 50_000
            ),
            policy,
            new RecapBlockMaintainerRegistry(maintainers)
        );

        public void CloseEngine() => Engine.Dispose();

        public void ReopenEngine() {
            Engine = SessionJournalEngine.Open(Path);
            Store = DerivedRecapStore.Open(
                Path,
                Engine.BranchRefId
            );
        }

        public int ReadCallCount() {
            string path = System.IO.Path.Combine(
                Path,
                "recap-maintainer-calls.jsonl"
            );
            return File.Exists(path)
                ? File.ReadLines(path).Count()
                : 0;
        }

        public int ReadCallCount(string maintainerId) {
            string path = System.IO.Path.Combine(
                Path,
                "recap-maintainer-calls.jsonl"
            );
            return File.Exists(path)
                ? File.ReadLines(path).Count(line =>
                    line.Contains(
                        $"\"MaintainerId\":\"{maintainerId}\"",
                        StringComparison.Ordinal
                    ))
                : 0;
        }

        public void Dispose() {
            Engine.Dispose();
            try {
                if (Directory.Exists(Path)) {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch {
            }
        }
    }
}
