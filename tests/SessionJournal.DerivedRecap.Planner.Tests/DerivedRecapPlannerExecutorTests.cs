using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Planner.Tests;

public sealed class DerivedRecapPlannerExecutorTests {
    [Fact]
    public async Task FrozenPlanRawValidatorAcceptsExactRawSemantics() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 2
        );
        (
            EventAddress start,
            EventAddress mid,
            EventAddress admission
        ) = fixture.TwoStepRoute();
        MaintainRecapBlockPlan plan = fixture.CreateEmptyPlan(
            fixture.SelfId,
            fixture.SelfTarget,
            "frozen-maintainer",
            start,
            [mid, admission]
        );
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                fixture.Engine.BranchRefId,
                admission,
                RecapPlannerWireTestFacts.SetupsAt(
                    fixture.Engine,
                    admission
                ),
                [plan]
            );

        IReadOnlyList<RecapFrozenPlanRawDefect> defects =
            RecapFrozenPlanRawValidator.ValidateInputDependentBlock(
                manifest,
                new Dictionary<
                    RecapBlockId,
                    DerivedRecapFrozenInput
                >(),
                fixture.Engine.ReadCurrentLineagePrefix(513),
                plan
            );

        Assert.Empty(defects);
    }

    [Fact]
    public async Task PendingWindowPreparerMaterializesOnlySuffix() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 2
        );
        (
            EventAddress start,
            EventAddress mid,
            EventAddress admission
        ) = fixture.TwoStepRoute();
        MaintainRecapBlockPlan plan = fixture.CreateEmptyPlan(
            fixture.SelfId,
            fixture.SelfTarget,
            "self-maintainer",
            start,
            [mid, admission]
        );
        var hardCaps = new RecapProtocolHardCaps(
            maxRawGrowthEventCount: 1000,
            maxRouteEndpointsPerBlock: 2,
            maxMaintainerCallsPerBuild: 2,
            maxRawEventsPerStep: 1000,
            maxRawEventsPerBuild: 1000,
            maxContentUtf8Bytes:
                SessionContextContributionContract
                    .MaxContributionUtf8Bytes,
            maxCatalogEntries:
                SessionContextContributionContract
                    .MaxContributionCount
        );
        var route = new PendingMaintainRoute(plan, mid, 1);
        SessionCurrentLineagePrefix prefix =
            fixture.Engine.ReadLineagePrefixAt(admission, 1000);
        RecapReplayBoundary routeStart = plan.CatchUpBoundaries[0];
        SessionGoverningSetupProof routeStartProof = Assert.IsType<
            SessionGoverningSetupProofResult.Available
        >(fixture.Engine.ReadView.ProveGoverningSetupInPrefix(
            prefix,
            routeStart.Address,
            routeStart.Setups
        )).Proof;
        PreparedRecapPendingWindows proven =
            RecapPendingWindowPreparer.Prove(
                fixture.Engine.ReadView,
                prefix,
                new Dictionary<
                    (EventAddress Address,
                        SessionContextAnchorSetupReferences Setups),
                    SessionGoverningSetupProof
                > {
                    [(routeStart.Address, routeStart.Setups)] =
                        routeStartProof
                },
                admission,
                [route],
                hardCaps,
                CancellationToken.None
            );
        Assert.Empty(proven.Defects);
        Assert.Null(proven.BeyondPrefix);

        PreparedRecapPendingWindows prepared =
            RecapPendingWindowPreparer.Prepare(
                fixture.Engine.ReadView,
                admission,
                [route],
                hardCaps,
                proven.ProofAuthorities,
                CancellationToken.None
            );

        Assert.Empty(prepared.Defects);
        KeyValuePair<
            (RecapBlockId BlockId, int EndpointIndex),
            SessionHistoryPlanningWindow
        > suffix = Assert.Single(prepared.Windows);
        Assert.Equal((fixture.SelfId, 1), suffix.Key);
        Assert.Equal(mid, suffix.Value.StartExclusive);
        Assert.Equal(admission, suffix.Value.ObservedRawHead);
    }

    [Fact]
    public async Task PendingWindowPreparer_ProvesAllStepsBeforePayload() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 3
        );
        SessionHistoryPlanningWindow history =
            fixture.Engine.ReadHistoryPlanningWindow();
        EventAddress[] boundaries = history.ReplaySafeBoundaries
            .Select(static boundary => boundary.Address)
            .ToArray();
        MaintainRecapBlockPlan plan = fixture.CreateEmptyPlan(
            fixture.SelfId,
            fixture.SelfTarget,
            "self-maintainer",
            history.StartExclusive,
            [boundaries[0], boundaries[^1]]
        );
        var hardCaps = new RecapProtocolHardCaps(
            maxRawGrowthEventCount: 1000,
            maxRouteEndpointsPerBlock: 2,
            maxMaintainerCallsPerBuild: 2,
            maxRawEventsPerStep: 2,
            maxRawEventsPerBuild: 1000,
            maxContentUtf8Bytes:
                SessionContextContributionContract
                    .MaxContributionUtf8Bytes,
            maxCatalogEntries:
                SessionContextContributionContract
                    .MaxContributionCount
        );
        var route = new PendingMaintainRoute(
            plan,
            history.StartExclusive,
            0
        );
        SessionCurrentLineagePrefix prefix =
            fixture.Engine.ReadLineagePrefixAt(boundaries[^1], 1000);
        var routeStart = new RecapReplayBoundary(
            history.StartExclusive,
            ((EmptyRecapMaintainSource)plan.Source).ReplayStartSetups
        );
        SessionGoverningSetupProof routeStartProof = Assert.IsType<
            SessionGoverningSetupProofResult.Available
        >(fixture.Engine.ReadView.ProveGoverningSetupInPrefix(
            prefix,
            routeStart.Address,
            routeStart.Setups
        )).Proof;
        SessionJournalReadDiagnostics before =
            fixture.Engine.CaptureReadDiagnostics();

        PreparedRecapPendingWindows prepared =
            RecapPendingWindowPreparer.Prove(
                fixture.Engine.ReadView,
                prefix,
                new Dictionary<
                    (EventAddress Address,
                        SessionContextAnchorSetupReferences Setups),
                    SessionGoverningSetupProof
                > {
                    [(routeStart.Address, routeStart.Setups)] =
                        routeStartProof
                },
                boundaries[^1],
                [route],
                hardCaps,
                CancellationToken.None
            );
        SessionJournalReadDiagnostics after =
            fixture.Engine.CaptureReadDiagnostics();

        Assert.NotNull(prepared.BeyondPrefix);
        Assert.Empty(prepared.Windows);
        Assert.Equal(before.PayloadReadCount, after.PayloadReadCount);
    }

    [Fact]
    public async Task ProvenPendingSuffixWithOldSetupsDoesNotReproveHeaders() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 300
        );
        SessionHistoryPlanningWindow history =
            fixture.Engine.ReadHistoryPlanningWindow();
        EventAddress[] boundaries = history.ReplaySafeBoundaries
            .Select(static boundary => boundary.Address)
            .ToArray();
        EventAddress source = boundaries[^3];
        EventAddress checkpoint = boundaries[^2];
        EventAddress admission = boundaries[^1];
        MaintainRecapBlockPlan plan = fixture.CreateEmptyPlan(
            fixture.SelfId,
            fixture.SelfTarget,
            "self-maintainer",
            source,
            [checkpoint, admission]
        );
        SessionContextAnchorSetupReferences sourceSetups =
            RecapPlannerWireTestFacts.SetupsAt(
                fixture.Engine,
                source
            );
        SessionContextAnchorSetupReferences checkpointSetups =
            RecapPlannerWireTestFacts.SetupsAt(
                fixture.Engine,
                checkpoint
            );
        Assert.IsType<SessionGoverningSetupProofResult.BeyondPrefix>(
            fixture.Engine.ReadView.ProveGoverningSetupAtBounded(
                checkpoint,
                checkpointSetups,
                RecapFrozenPlanBarrier.MaxHeaderCount
            )
        );
        SessionCurrentLineagePrefix prefix =
            fixture.Engine.ReadLineagePrefixAt(
                admission,
                RecapFrozenPlanBarrier.ProofPrefixHeaderCount(
                    RecapProtocolHardCaps.V4
                )
            );
        SessionGoverningSetupProof sourceProof = Assert.IsType<
            SessionGoverningSetupProofResult.Available
        >(fixture.Engine.ReadView.ProveGoverningSetupInPrefix(
            prefix,
            source,
            sourceSetups
        )).Proof;
        PreparedRecapPendingWindows proven =
            RecapPendingWindowPreparer.Prove(
                fixture.Engine.ReadView,
                prefix,
                new Dictionary<
                    (EventAddress Address,
                        SessionContextAnchorSetupReferences Setups),
                    SessionGoverningSetupProof
                > {
                    [(source, sourceSetups)] = sourceProof
                },
                admission,
                [new PendingMaintainRoute(plan, source, 0)],
                RecapProtocolHardCaps.V4,
                CancellationToken.None
            );
        Assert.Empty(proven.Defects);
        Assert.Null(proven.BeyondPrefix);
        SessionJournalReadDiagnostics before =
            fixture.Engine.CaptureReadDiagnostics();

        PreparedRecapPendingWindows prepared =
            RecapPendingWindowPreparer.Prepare(
                fixture.Engine.ReadView,
                admission,
                [new PendingMaintainRoute(plan, checkpoint, 1)],
                RecapProtocolHardCaps.V4,
                proven.ProofAuthorities,
                CancellationToken.None
            );
        SessionJournalReadDiagnostics after =
            fixture.Engine.CaptureReadDiagnostics();

        Assert.Empty(prepared.Defects);
        Assert.Equal((fixture.SelfId, 1), Assert.Single(prepared.Windows).Key);
        long headerDelta = after.HeaderPreviewReadCount
            - before.HeaderPreviewReadCount;
        // The explicit 513-header direct proof above returned Beyond.
        // Materialization may resolve the exact execution boundary, but it
        // must not repeat that governing-setup walk.
        Assert.InRange(headerDelta, 1L, 16L);
        Assert.True(
            headerDelta < RecapFrozenPlanBarrier.MaxHeaderCount
        );
        Assert.True(after.PayloadReadCount > before.PayloadReadCount);
    }

    [Theory]
    [InlineData("block")]
    [InlineData("index")]
    [InlineData("start")]
    [InlineData("start-setups")]
    public async Task PendingWindowProofAuthorityMismatchFailsBeforePayload(
        string mismatch
    ) {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 2
        );
        (
            EventAddress start,
            EventAddress mid,
            EventAddress admission
        ) = fixture.TwoStepRoute();
        MaintainRecapBlockPlan plan = fixture.CreateEmptyPlan(
            fixture.SelfId,
            fixture.SelfTarget,
            "self-maintainer",
            start,
            [mid, admission]
        );
        var startBoundary = new RecapReplayBoundary(
            start,
            RecapPlannerWireTestFacts.SetupsAt(fixture.Engine, start)
        );
        RecapReplayBoundary midBoundary = plan.CatchUpBoundaries[0];
        RecapReplayBoundary admissionBoundary =
            plan.CatchUpBoundaries[1];
        SessionGoverningSetupProof startProof = Assert.IsType<
            SessionGoverningSetupProofResult.Available
        >(fixture.Engine.ReadView.ProveGoverningSetupAtBounded(
            startBoundary.Address,
            startBoundary.Setups,
            RecapFrozenPlanBarrier.MaxHeaderCount
        )).Proof;
        SessionHistoryPlanningWindowProof firstProof = Assert.IsType<
            SessionHistoryPlanningWindowProofResult.Available
        >(fixture.Engine.ReadView.ProveHistoryPlanningWindowAtBounded(
            mid,
            start,
            maxRawEventCount: 1000
        )).Proof;
        SessionGoverningSetupProof midSetupProof =
            fixture.Engine.ReadView.ProveGoverningSetupTransition(
                firstProof,
                startProof,
                midBoundary.Setups
            );
        SessionHistoryPlanningWindowProof secondProof = Assert.IsType<
            SessionHistoryPlanningWindowProofResult.Available
        >(fixture.Engine.ReadView.ProveHistoryPlanningWindowAtBounded(
            admission,
            mid,
            maxRawEventCount: 1000
        )).Proof;
        var firstAuthority = new RecapPendingWindowProofAuthority(
            fixture.SelfId,
            0,
            startBoundary,
            midBoundary,
            firstProof,
            startProof
        );
        var secondAuthority = new RecapPendingWindowProofAuthority(
            fixture.SelfId,
            1,
            midBoundary,
            admissionBoundary,
            secondProof,
            midSetupProof
        );
        secondAuthority = mismatch switch {
            "block" => secondAuthority with {
                BlockId = new RecapBlockId("other")
            },
            "index" => secondAuthority with { EndpointIndex = 0 },
            "start" => secondAuthority with { Start = startBoundary },
            "start-setups" => secondAuthority with {
                Start = midBoundary with {
                    Setups = RecapPlannerWireTestFacts.WrongSetups(
                        midBoundary.Setups
                    )
                }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
        };
        var authorities = new Dictionary<
            (RecapBlockId BlockId, int EndpointIndex),
            RecapPendingWindowProofAuthority
        > {
            [(fixture.SelfId, 0)] = firstAuthority,
            [(fixture.SelfId, 1)] = secondAuthority
        };
        SessionJournalReadDiagnostics before =
            fixture.Engine.CaptureReadDiagnostics();

        InvalidDataException exception = Assert.Throws<
            InvalidDataException
        >(() => RecapPendingWindowPreparer.Prepare(
            fixture.Engine.ReadView,
            admission,
            [new PendingMaintainRoute(plan, start, 0)],
            new RecapProtocolHardCaps(
                maxRawGrowthEventCount: 1000,
                maxRouteEndpointsPerBlock: 2,
                maxMaintainerCallsPerBuild: 2,
                maxRawEventsPerStep: 1000,
                maxRawEventsPerBuild: 1000,
                maxContentUtf8Bytes:
                    SessionContextContributionContract
                        .MaxContributionUtf8Bytes,
                maxCatalogEntries:
                    SessionContextContributionContract
                        .MaxContributionCount
            ),
            authorities,
            CancellationToken.None
        ));
        SessionJournalReadDiagnostics after =
            fixture.Engine.CaptureReadDiagnostics();

        Assert.Contains(
            "bound pre-component proof authority",
            exception.Message,
            StringComparison.Ordinal
        );
        Assert.Equal(before.PayloadReadCount, after.PayloadReadCount);
    }

    [Fact]
    public async Task MaintainerStepRunnerReturnsTypedInvalidResult() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 1
        );
        EventAddress start = fixture.ReplayStart();
        EventAddress admission =
            fixture.Engine.ReadCurrentHead()!.Value;
        MaintainRecapBlockPlan plan = fixture.CreateEmptyPlan(
            fixture.SelfId,
            fixture.SelfTarget,
            "self-maintainer",
            start,
            [admission]
        );
        SessionHistoryPlanningSeedBatch seeds =
            fixture.Engine.ReadHistoryPlanningSeeds([start]);
        SessionHistoryPlanningWindow window =
            fixture.Engine.ReadHistoryPlanningWindowAt(
                admission,
                Assert.Single(seeds.Seeds)
            );
        var maintainer = new InvalidIdentityMaintainer(
            "self-maintainer",
            fixture.SelfTarget
        );

        RecapMaintainerStepResult result =
            await RecapMaintainerStepRunner.RunAsync(
                maintainer,
                plan,
                currentBlock: null,
                window,
                admission,
                CancellationToken.None
            );

        Assert.IsType<RecapMaintainerStepResult.ResultInvalid>(result);
    }

    [Fact]
    public async Task MaintainerStepSourceIdUsesCanonicalEventAddresses() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 1
        );
        EventAddress start = fixture.ReplayStart();
        EventAddress admission = fixture.Engine.ReadCurrentHead()!.Value;
        MaintainRecapBlockPlan plan = fixture.CreateEmptyPlan(
            fixture.SelfId,
            fixture.SelfTarget,
            "self-maintainer",
            start,
            [admission]
        );
        SessionHistoryPlanningSeedBatch seeds =
            fixture.Engine.ReadHistoryPlanningSeeds([start]);
        SessionHistoryPlanningWindow window =
            fixture.Engine.ReadHistoryPlanningWindowAt(
                admission,
                Assert.Single(seeds.Seeds)
            );
        string? observedSourceId = null;
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            (_, request) => {
                observedSourceId = request.RecentHistory.SourceId;
                return "canonical";
            }
        );

        RecapMaintainerStepResult result =
            await RecapMaintainerStepRunner.RunAsync(
                maintainer,
                plan,
                currentBlock: null,
                window,
                admission,
                CancellationToken.None
            );

        Assert.IsType<RecapMaintainerStepResult.Succeeded>(result);
        Assert.Equal(
            EventAddressTextCodec.Format(window.StartExclusive)
            + ".."
            + EventAddressTextCodec.Format(window.ObservedRawHead),
            observedSourceId
        );
    }

    [Fact]
    public async Task BelowTriggerHasNoPolicyOrMaintainerCalls() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 1
        );
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "unused"
        );
        var policy = new DelegatePolicy(_ =>
            throw new InvalidOperationException("must not run"));
        DerivedRecapPlannerExecutor executor = fixture.CreateExecutor(
            policy,
            [maintainer],
            recapBuildIntervalHistoryLoad: 100
        );

        DerivedRecapExecutionResult result =
            await executor.RunAsync();

        Assert.IsType<DerivedRecapExecutionResult.NoBuild>(result);
        Assert.Equal(0, policy.CallCount);
        Assert.Equal(0, maintainer.CallCount);
    }

    [Fact]
    public async Task RawSafetyRejectsBeforeHistoryLoadMeasurement() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 1
        );
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "unused"
        );
        var estimator = new TestHistoryUnitLoadEstimator();
        DerivedRecapPlannerExecutor executor = fixture.CreateExecutor(
            new DelegatePolicy(static _ =>
                throw new Xunit.Sdk.XunitException(
                    "Policy must not run."
                )),
            [maintainer],
            estimator: estimator,
            maxRawGrowthEventCount: 1
        );

        var beyond = Assert.IsType<
            DerivedRecapExecutionResult.BeyondPrefix
        >(await executor.RunAsync());

        Assert.Equal(
            DerivedRecapBeyondPrefixStage.NewPlanningRawGrowth,
            beyond.Stage
        );
        Assert.Null(executor.LastPlanningDiagnostics);
        Assert.Equal(0, estimator.MeasureCallCount);
        Assert.Equal(0, maintainer.CallCount);
    }

    [Fact]
    public async Task PublishedCommitmentBeyondCurrentPrefixIsStagedBeforePlanningPayloads() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 1
        );
        var sourceMaintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "source"
        );
        _ = Assert.IsType<DerivedRecapExecutionResult.Published>(
            await fixture.CreateExecutor(
                    new BoundedMaintainAllRecapPlanningPolicy(),
                    [sourceMaintainer]
                )
                .RunAsync()
        );
        for (int index = 0; index < 257; index++) {
            fixture.AppendPair($"beyond-{index}");
        }
        var estimator = new TestHistoryUnitLoadEstimator();
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "must-not-run"
        );

        var beyond = Assert.IsType<
            DerivedRecapExecutionResult.BeyondPrefix
        >(
            await fixture.CreateExecutor(
                    new BoundedMaintainAllRecapPlanningPolicy(),
                    [maintainer],
                    estimator: estimator
                )
                .RunAsync()
        );

        Assert.Equal(
            DerivedRecapBeyondPrefixStage.NewPlanningSourceAnchor,
            beyond.Stage
        );
        Assert.Equal(0, estimator.MeasureCallCount);
        Assert.Equal(0, maintainer.CallCount);
    }

    [Fact]
    public async Task TypedHistoryLoadFailureHasNoMutationOrCalls() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 1
        );
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "must-not-run"
        );
        var policy = new DelegatePolicy(static _ =>
            throw new Xunit.Sdk.XunitException(
                "Policy must not run."
            ));
        const string defectCode = "TestHistoryLoadUnavailable";
        var estimator = new TestHistoryUnitLoadEstimator(
            TestHistoryUnitLoadEstimator.DefaultId,
            static (_, _) => throw
                new HistoryLoadMeasurementException(
                    defectCode,
                    "synthetic measurement failure"
                )
        );
        DerivedRecapPlannerExecutor executor = fixture.CreateExecutor(
            policy,
            [maintainer],
            estimator: estimator
        );
        EventAddress candidate =
            fixture.Engine.ReadCurrentHead()!.Value;

        var unavailable =
            Assert.IsType<DerivedRecapExecutionResult.Unavailable>(
                await executor.RunAsync()
            );

        Assert.Contains(
            unavailable.Defects,
            defect => defect.Code == defectCode
        );
        Assert.Equal(1, estimator.MeasureCallCount);
        Assert.Equal(0, policy.CallCount);
        Assert.Equal(0, maintainer.CallCount);
        Assert.Null(executor.LastPlanningDiagnostics);
        Assert.IsType<BuildingReadResult.Missing>(
            await fixture.Store.ReadBuildingAsync(candidate)
        );
    }

    [Fact]
    public async Task MissingMaintainerFailsAfterPlanningBeforeBuilding() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 1
        );
        DerivedRecapPlannerExecutor executor = fixture.CreateExecutor(
            new BoundedMaintainAllRecapPlanningPolicy(),
            maintainers: []
        );
        EventAddress candidate =
            fixture.Engine.ReadCurrentHead()!.Value;

        var unavailable =
            Assert.IsType<DerivedRecapExecutionResult.Unavailable>(
                await executor.RunAsync()
            );

        Assert.Contains(
            unavailable.Defects,
            defect => defect.Code
                == DerivedRecapExecutionDefectCodes
                    .MaintainerUnavailable
        );
        Assert.IsType<
            DerivedRecapPlanningDiagnostics.ExactSchedule
        >(executor.LastPlanningDiagnostics);
        Assert.IsType<BuildingReadResult.Missing>(
            await fixture.Store.ReadBuildingAsync(candidate)
        );
    }

    [Fact]
    public async Task ExactNoBuildDiagnosticsExposeBothExactCounts() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 1
        );
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "unused"
        );
        DerivedRecapPlannerExecutor executor = fixture.CreateExecutor(
            new DelegatePolicy(static _ =>
                throw new Xunit.Sdk.XunitException(
                    "Policy must not run."
                )),
            [maintainer],
            recapBuildIntervalHistoryLoad: 3
        );

        _ = Assert.IsType<DerivedRecapExecutionResult.NoBuild>(
            await executor.RunAsync()
        );

        var diagnostics = Assert.IsType<
            DerivedRecapPlanningDiagnostics.ExactSchedule
        >(executor.LastPlanningDiagnostics);
        Assert.Equal(
            2,
            diagnostics.Measurement.GrowthHistoryUnitCount
        );
        Assert.Equal(
            2,
            diagnostics.Measurement.GrowthHistoryLoad.Value
        );
        Assert.True(
            diagnostics.Measurement.RawGrowthEventCount > 0
        );
    }

    [Fact]
    public async Task CatalogMismatchRejectsBeforeCadenceMeasurement() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 1
        );
        var firstMaintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "source"
        );
        _ = Assert.IsType<DerivedRecapExecutionResult.Published>(
            await fixture.CreateExecutor(
                    new BoundedMaintainAllRecapPlanningPolicy(),
                    [firstMaintainer]
                )
                .RunAsync()
        );
        var policy = new DelegatePolicy(static _ =>
            throw new Xunit.Sdk.XunitException(
                "Catalog mismatch must precede cadence."
            ));
        var secondMaintainer = new ScriptedMaintainer(
            "new-maintainer",
            fixture.SelfTarget,
            static (_, _) => "unused"
        );
        DerivedRecapPlannerExecutor executor = fixture.CreateExecutor(
            policy,
            [secondMaintainer],
            recapBuildIntervalHistoryLoad: 100,
            catalog: [
                new RecapBlockCatalogEntry(
                    fixture.SelfId,
                    fixture.SelfTarget,
                    "new-maintainer",
                    RecapPlannerTestIdentity.CapabilityFingerprint,
                    TestFixture.MaxContent - 1
                )
            ]
        );

        var result =
            Assert.IsType<DerivedRecapExecutionResult.Unavailable>(
                await executor.RunAsync()
            );

        Assert.Contains(
            result.Defects,
            static defect => defect.Code
                == DerivedRecapExecutionDefectCodes
                    .CatalogMigrationRequired
        );
        Assert.Null(executor.LastPlanningDiagnostics);
        Assert.Equal(0, policy.CallCount);
        Assert.Equal(0, secondMaintainer.CallCount);
    }

    [Fact]
    public async Task NewPlanningFreezesMaintainerFingerprint() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 1
        );
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "published"
        );

        var published = Assert.IsType<
            DerivedRecapExecutionResult.Published
        >(
            await fixture.CreateExecutor(
                    new BoundedMaintainAllRecapPlanningPolicy(),
                    [maintainer]
                )
                .RunAsync()
        );
        var planRead = Assert.IsType<PublishedPlanReadResult.Available>(
            await fixture.Store.ReadPublishedPlanAsync(
                published.Descriptor
            )
        );
        MaintainRecapBlockPlan plan = Assert.IsType<
            MaintainRecapBlockPlan
        >(Assert.Single(planRead.Snapshot.FrozenPlan.Blocks));

        Assert.Equal(
            maintainer.CapabilityFingerprint,
            plan.MaintainerCapabilityFingerprint
        );
    }

    [Fact]
    public async Task MaintainerChangeWithSameShapeNeedsNoMigration() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 1
        );
        var firstMaintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "source"
        );
        _ = Assert.IsType<DerivedRecapExecutionResult.Published>(
            await fixture.CreateExecutor(
                    new BoundedMaintainAllRecapPlanningPolicy(),
                    [firstMaintainer]
                )
                .RunAsync()
        );
        var replacement = new ScriptedMaintainer(
            "replacement-maintainer",
            fixture.SelfTarget,
            static (_, _) => "unused"
        );
        DerivedRecapPlannerExecutor executor = fixture.CreateExecutor(
            new DelegatePolicy(static _ =>
                throw new Xunit.Sdk.XunitException(
                    "No growth must not call policy."
                )),
            [replacement],
            recapBuildIntervalHistoryLoad: 100,
            catalog: [
                new RecapBlockCatalogEntry(
                    fixture.SelfId,
                    fixture.SelfTarget,
                    "replacement-maintainer",
                    RecapPlannerTestIdentity.CapabilityFingerprint,
                    TestFixture.MaxContent
                )
            ]
        );

        DerivedRecapExecutionResult result =
            await executor.RunAsync();

        Assert.IsType<DerivedRecapExecutionResult.NoBuild>(result);
        Assert.Equal(0, replacement.CallCount);
    }

    [Fact]
    public async Task PlanningBaselineRejectsRawAndExactSourceDrift() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 1
        );
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "source"
        );
        var published =
            Assert.IsType<DerivedRecapExecutionResult.Published>(
                await fixture.CreateExecutor(
                        new BoundedMaintainAllRecapPlanningPolicy(),
                        [maintainer]
                    )
                    .RunAsync()
            );
        DerivedRecapPlannerExecutor executor = fixture.CreateExecutor(
            new BoundedMaintainAllRecapPlanningPolicy(),
            [maintainer],
            recapBuildIntervalHistoryLoad: 100
        );
        EventAddress head =
            fixture.Engine.ReadCurrentLineageHeaders().CapturedHead;
        var wrongExact = new DerivedRecapPlanningBaseline(
            head,
            published.Descriptor.SetAdmissionAnchor,
            published.Descriptor with {
                EnvelopeSha256 = new string('f', 64)
            }
        );

        var sourceDrift =
            Assert.IsType<DerivedRecapExecutionResult.Retryable>(
                await executor.RunAsync(wrongExact)
            );
        Assert.Equal(
            DerivedRecapExecutionDefectCodes.SourceChanged,
            sourceDrift.Code
        );

        var rawBaseline = new DerivedRecapPlanningBaseline(
            head,
            published.Descriptor.SetAdmissionAnchor,
            published.Descriptor
        );
        fixture.AppendPair("drift");
        var rawDrift =
            Assert.IsType<DerivedRecapExecutionResult.Retryable>(
                await executor.RunAsync(rawBaseline)
            );
        Assert.Equal(
            DerivedRecapExecutionDefectCodes.RawHeadChanged,
            rawDrift.Code
        );
    }

    [Fact]
    public async Task AnchorOnlyBaselineAcceptsRestoredExactIdentity() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 1
        );
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "source"
        );
        var published =
            Assert.IsType<DerivedRecapExecutionResult.Published>(
                await fixture.CreateExecutor(
                        new BoundedMaintainAllRecapPlanningPolicy(),
                        [maintainer]
                    )
                    .RunAsync()
            );
        EventAddress head =
            fixture.Engine.ReadCurrentLineageHeaders().CapturedHead;
        var restoredBaseline = new DerivedRecapPlanningBaseline(
            head,
            published.Descriptor.SetAdmissionAnchor,
            expectedLatestPublished: null
        );

        DerivedRecapExecutionResult result =
            await fixture.CreateExecutor(
                    new DelegatePolicy(static _ =>
                        throw new Xunit.Sdk.XunitException(
                            "No growth must not call policy."
                        )),
                    [maintainer],
                    recapBuildIntervalHistoryLoad: 100
                )
                .RunAsync(restoredBaseline);

        Assert.IsType<DerivedRecapExecutionResult.NoBuild>(result);
    }

    [Fact]
    public async Task BoundedMaintainAllBuildsThenCatchesUp() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 2
        );
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (call, request) =>
                $"{request.OldBlock.Text}|step-{call}"
        );
        DerivedRecapPlannerExecutor executor = fixture.CreateExecutor(
            new BoundedMaintainAllRecapPlanningPolicy(),
            [maintainer]
        );

        var first = Assert.IsType<
            DerivedRecapExecutionResult.Published
        >(await executor.RunAsync());
        EventAddress secondHead = fixture.AppendPair("catch-up");
        var second = Assert.IsType<
            DerivedRecapExecutionResult.Published
        >(await executor.RunAsync());

        Assert.Equal(secondHead, second.Descriptor.SetAdmissionAnchor);
        Assert.NotEqual(first.Descriptor, second.Descriptor);
        Assert.Equal(2, maintainer.CallCount);
        PublishedRecapSourceSnapshot source =
            await fixture.ReadSourceAsync(
                second.Descriptor,
                [fixture.SelfId]
            );
        Assert.Equal(
            secondHead,
            Assert.Single(source.FrozenInputs).AbsorbedThrough
        );
    }

    [Fact]
    public async Task CadenceBuildPreservesMinimumRecentHistoryLoad() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 3
        );
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "recap"
        );
        DerivedRecapPlannerExecutor executor = fixture.CreateExecutor(
            new BoundedMaintainAllRecapPlanningPolicy(),
            [maintainer],
            minimumRecentHistoryLoad: 2,
            recapBuildIntervalHistoryLoad: 2
        );

        var published =
            Assert.IsType<DerivedRecapExecutionResult.Published>(
                await executor.RunAsync()
            );
        SessionHistoryPlanningWindow recent =
            fixture.Engine.ReadHistoryPlanningWindowAt(
                fixture.Engine.ReadCurrentHead()!.Value,
                published.Descriptor.SetAdmissionAnchor
            );

        Assert.Equal(2, recent.Units.Count);
        Assert.Equal(1, maintainer.CallCount);
        var diagnostics = Assert.IsType<
            DerivedRecapPlanningDiagnostics.ExactSchedule
        >(executor.LastPlanningDiagnostics);
        Assert.Equal(
            TestHistoryUnitLoadEstimator.DefaultId,
            diagnostics.Measurement.HistoryUnitLoadEstimatorId
        );
        Assert.Equal(
            diagnostics.Measurement.GrowthHistoryLoad.Value,
            diagnostics.Measurement
                .SelectedAbsorbedHistoryLoad!.Value.Value
            + diagnostics.Measurement
                .SelectedRecentHistoryLoad!.Value.Value
        );
        Assert.Equal(
            2,
            diagnostics.Measurement
                .SelectedRecentHistoryLoad.Value.Value
        );
    }

    [Fact]
    public async Task LaggingInheritedCursorDoesNotRecountPublishedUnits() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 2
        );
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "recap"
        );
        DerivedRecapPlannerExecutor firstExecutor =
            fixture.CreateExecutor(
                new BoundedMaintainAllRecapPlanningPolicy(),
                [maintainer],
                minimumRecentHistoryLoad: 1,
                recapBuildIntervalHistoryLoad: 1
            );
        _ = Assert.IsType<DerivedRecapExecutionResult.Published>(
            await firstExecutor.RunAsync()
        );

        fixture.AppendPair("second-cycle");
        var inheritPolicy = new DelegatePolicy(context => {
            RecapCadenceBoundary admission =
                context.Cadence.AdmissionCandidates
                    .OrderByDescending(candidate =>
                        candidate.AbsorbedHistoryLoad.Value)
                    .First();
            RecapSourceIntent source = Assert.Single(
                context.PolicyFacts.AvailableSources
            ).Source;
            return new RecapPlanningPolicyDecision.Build(
                admission.Address,
                [
                    new RecapBlockPlanningDecision.Inherit(
                        fixture.SelfId,
                        source
                    )
                ]
            );
        });
        DerivedRecapPlannerExecutor secondExecutor =
            fixture.CreateExecutor(
                inheritPolicy,
                [maintainer],
                minimumRecentHistoryLoad: 1,
                recapBuildIntervalHistoryLoad: 1
            );
        _ = Assert.IsType<DerivedRecapExecutionResult.Published>(
            await secondExecutor.RunAsync()
        );

        fixture.Engine.AppendRuntimeConfigSetup(
            new SessionRuntimeConfiguration(
                "model-b",
                "surface-b",
                SessionJournalDefaults.Schema,
                new(0)
            )
        );
        fixture.Engine.AppendSystemPromptSetup("system-b");
        var mustNotRun = new DelegatePolicy(static _ =>
            throw new Xunit.Sdk.XunitException(
                "Published units before the cadence baseline must "
                + "not be recounted."
            )
        );
        var estimator = new TestHistoryUnitLoadEstimator();
        DerivedRecapPlannerExecutor thirdExecutor =
            fixture.CreateExecutor(
                mustNotRun,
                [maintainer],
                minimumRecentHistoryLoad: 1,
                recapBuildIntervalHistoryLoad: 1,
                estimator: estimator
            );

        var noBuild =
            Assert.IsType<DerivedRecapExecutionResult.NoBuild>(
                await thirdExecutor.RunAsync()
            );

        Assert.Equal(
            RecapPlanReasons.BelowCadenceThreshold,
            noBuild.Reason
        );
        Assert.Equal(0, mustNotRun.CallCount);
        Assert.Equal(1, estimator.MeasureCallCount);
        var diagnostics = Assert.IsType<
            DerivedRecapPlanningDiagnostics.ExactSchedule
        >(thirdExecutor.LastPlanningDiagnostics);
        Assert.Equal(
            1,
            diagnostics.Measurement.GrowthHistoryUnitCount
        );
        Assert.Equal(
            1,
            diagnostics.Measurement.GrowthHistoryLoad.Value
        );
        Assert.Equal(1, maintainer.CallCount);
    }

    [Fact]
    public async Task MultiBlockLaggingInheritUsesEachBlocksFrozenSetupEpoch() {
        int componentReads = 0;
        int mutations = 0;
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 1,
            hooks: new RecapStoreTestHooks(
                BeforeAtomicFileReplace: _ => mutations++,
                BeforeBuildingComponentRead: () => componentReads++
            )
        );
        var recentId = new RecapBlockId("a-recent");
        var oldId = new RecapBlockId("z-old");
        var recentTarget = new ContextHeaderBlockPath(
            ContextHeaderCarrier.System,
            recentId.Value
        );
        var oldTarget = new ContextHeaderBlockPath(
            ContextHeaderCarrier.System,
            oldId.Value
        );
        var recentMaintainer = new ScriptedMaintainer(
            "recent-maintainer",
            recentTarget,
            static (call, _) => $"recent-{call}"
        );
        var oldMaintainer = new ScriptedMaintainer(
            "old-maintainer",
            oldTarget,
            static (call, _) => $"old-{call}"
        );
        RecapBlockCatalogEntry[] catalog = [
            new(
                recentId,
                recentTarget,
                recentMaintainer.Id,
                recentMaintainer.CapabilityFingerprint,
                TestFixture.MaxContent
            ),
            new(
                oldId,
                oldTarget,
                oldMaintainer.Id,
                oldMaintainer.CapabilityFingerprint,
                TestFixture.MaxContent
            )
        ];
        IRecapBlockMaintainer[] maintainers = [
            recentMaintainer,
            oldMaintainer
        ];
        EventAddress replayStart = fixture.ReplayStart();
        EventAddress firstAdmission =
            fixture.Engine.ReadCurrentHead()!.Value;
        SessionContextAnchorSetupReferences firstEpoch =
            RecapPlannerWireTestFacts.SetupsAt(
                fixture.Engine,
                firstAdmission
            );
        var initialPolicy = new DelegatePolicy(_ =>
            new RecapPlanningPolicyDecision.Build(
                firstAdmission,
                [
                    MaintainEmpty(recentId, replayStart, firstAdmission),
                    MaintainEmpty(oldId, replayStart, firstAdmission)
                ]
            ));
        _ = await RunPublishedAsync(fixture.CreateExecutor(
            initialPolicy,
            maintainers,
            catalog: catalog
        ));

        _ = fixture.Engine.AppendRuntimeConfigSetup(
            new SessionRuntimeConfiguration(
                "model-b",
                "surface-b",
                SessionJournalDefaults.Schema,
                new(0)
            )
        );
        _ = fixture.Engine.AppendSystemPromptSetup("system-b");
        EventAddress secondAdmission = fixture.AppendPair("epoch-b");
        SessionContextAnchorSetupReferences secondEpoch =
            RecapPlannerWireTestFacts.SetupsAt(
                fixture.Engine,
                secondAdmission
            );
        Assert.NotEqual(firstEpoch, secondEpoch);
        var mixedPolicy = new DelegatePolicy(context => {
            RecapBlockSourceIntent recent = Source(context, recentId);
            RecapBlockSourceIntent old = Source(context, oldId);
            return new RecapPlanningPolicyDecision.Build(
                secondAdmission,
                [
                    new RecapBlockPlanningDecision.Maintain(
                        recentId,
                        new RecapPlanningMaintainSource.Existing(
                            recent.Source
                        ),
                        [secondAdmission],
                        EmptyRecapPriorContext.Instance
                    ),
                    new RecapBlockPlanningDecision.Inherit(
                        oldId,
                        old.Source
                    )
                ]
            );
        });
        _ = await RunPublishedAsync(fixture.CreateExecutor(
            mixedPolicy,
            maintainers,
            catalog: catalog
        ));

        _ = fixture.Engine.AppendRuntimeConfigSetup(
            new SessionRuntimeConfiguration(
                "model-c",
                "surface-c",
                SessionJournalDefaults.Schema,
                new(0)
            )
        );
        _ = fixture.Engine.AppendSystemPromptSetup("system-c");
        EventAddress thirdAdmission = fixture.AppendPair("epoch-c");
        var inheritBothPolicy = new DelegatePolicy(context =>
            new RecapPlanningPolicyDecision.Build(
                thirdAdmission,
                [
                    Inherit(context, recentId),
                    Inherit(context, oldId)
                ]
            ));
        DerivedRecapExecutionResult.Published inherited =
            await RunPublishedAsync(fixture.CreateExecutor(
                inheritBothPolicy,
                maintainers,
                catalog: catalog
            ));
        PublishedRecapSourceSnapshot source =
            await fixture.ReadSourceAsync(
                inherited.Descriptor,
                [recentId, oldId]
            );
        DerivedRecapFrozenInput recentInput = source.FrozenInputs.Single(
            input => input.RecapBlockId == recentId
        );
        DerivedRecapFrozenInput oldInput = source.FrozenInputs.Single(
            input => input.RecapBlockId == oldId
        );
        Assert.Equal(secondAdmission, recentInput.AbsorbedThrough);
        Assert.Equal(secondEpoch, recentInput.AbsorbedThroughSetups);
        Assert.Equal(firstAdmission, oldInput.AbsorbedThrough);
        Assert.Equal(firstEpoch, oldInput.AbsorbedThroughSetups);
        Assert.NotEqual(
            recentInput.AbsorbedThroughSetups,
            oldInput.AbsorbedThroughSetups
        );
        InheritRecapBlockPlan recentPlan = Assert.IsType<
            InheritRecapBlockPlan
        >(source.Publication.FrozenPlanSnapshot.Blocks.Single(
            plan => plan.RecapBlockId == recentId
        ));
        InheritRecapBlockPlan oldPlan = Assert.IsType<
            InheritRecapBlockPlan
        >(source.Publication.FrozenPlanSnapshot.Blocks.Single(
            plan => plan.RecapBlockId == oldId
        ));
        Assert.Equal(secondAdmission, recentPlan.SourceSetAnchor);
        Assert.Equal(secondAdmission, oldPlan.SourceSetAnchor);
        Assert.NotEqual(
            recentInput.AbsorbedThrough,
            inherited.Descriptor.SetAdmissionAnchor
        );
        Assert.NotEqual(
            oldInput.AbsorbedThrough,
            inherited.Descriptor.SetAdmissionAnchor
        );
        Assert.Equal(
            recentInput.AbsorbedThroughSetups,
            recentPlan.SourceAbsorbedThroughSetups
        );
        Assert.Equal(
            oldInput.AbsorbedThroughSetups,
            oldPlan.SourceAbsorbedThroughSetups
        );
        EventAddress verificationHead =
            fixture.AppendPair("verify-earliest");
        SessionHistoryPlanningWindow expectedEarliestWindow =
            fixture.Engine.ReadHistoryPlanningWindowAt(
                verificationHead,
                firstAdmission
            );
        SessionHistoryPlanningWindow wrongRecentWindow =
            fixture.Engine.ReadHistoryPlanningWindowAt(
                verificationHead,
                secondAdmission
            );
        SessionHistoryPlanningWindow cadenceGrowthWindow =
            fixture.Engine.ReadHistoryPlanningWindowAt(
                verificationHead,
                thirdAdmission
            );
        Assert.Equal(6, expectedEarliestWindow.Units.Count);
        Assert.Equal(4, wrongRecentWindow.Units.Count);
        Assert.Equal(2, cadenceGrowthWindow.Units.Count);

        componentReads = 0;
        mutations = 0;
        recentMaintainer.Reset();
        oldMaintainer.Reset();
        var estimator = new TestHistoryUnitLoadEstimator();
        var mustNotBuild = new DelegatePolicy(static _ =>
            throw new Xunit.Sdk.XunitException(
                "Below-threshold verification must not call policy."
            ));
        DerivedRecapExecutionResult result =
            await fixture.CreateExecutor(
                    mustNotBuild,
                    maintainers,
                    recapBuildIntervalHistoryLoad: 100,
                    estimator: estimator,
                    catalog: catalog
                )
                .RunAsync();

        Assert.IsType<DerivedRecapExecutionResult.NoBuild>(result);
        Assert.Equal(
            cadenceGrowthWindow.Units.Count,
            estimator.MeasureCallCount
        );
        Assert.Equal(0, mustNotBuild.CallCount);
        Assert.Equal(0, recentMaintainer.CallCount);
        Assert.Equal(0, oldMaintainer.CallCount);
        Assert.Equal(0, componentReads);
        Assert.Equal(0, mutations);

        RecapBlockPlanningDecision.Maintain MaintainEmpty(
            RecapBlockId id,
            EventAddress start,
            EventAddress admission
        ) => new(
            id,
            new RecapPlanningMaintainSource.Empty(start),
            [admission],
            EmptyRecapPriorContext.Instance
        );

        RecapBlockPlanningDecision.Inherit Inherit(
            RecapPlanningPolicyContext context,
            RecapBlockId id
        ) => new(id, Source(context, id).Source);

        static RecapBlockSourceIntent Source(
            RecapPlanningPolicyContext context,
            RecapBlockId id
        ) => context.PolicyFacts.AvailableSources.Single(
            source => source.RecapBlockId == id
        );

        static async ValueTask<
            DerivedRecapExecutionResult.Published
        > RunPublishedAsync(DerivedRecapPlannerExecutor executor) {
            DerivedRecapExecutionResult result =
                await executor.RunAsync();
            if (result is DerivedRecapExecutionResult.Unavailable
                    unavailable) {
                Assert.Fail(
                    "Expected Published, but execution was unavailable: "
                    + string.Join(
                        "; ",
                        unavailable.Defects.Select(static defect =>
                            $"{defect.Code}: {defect.Detail}"
                        )
                    )
                );
            }
            return Assert.IsType<
                DerivedRecapExecutionResult.Published
            >(result);
        }
    }

    [Fact]
    public async Task MaintainMayKeepContentWhileAdvancingCursor() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 2
        );
        EventAddress start = fixture.ReplayStart();
        EventAddress admission = fixture.Engine.ReadCurrentHead()!.Value;
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, request) =>
                request.OldBlock.Text.Length == 0
                    ? "stable"
                    : request.OldBlock.Text
        );
        var policy = new DelegatePolicy(context =>
            new RecapPlanningPolicyDecision.Build(
                admission,
                [
                    new RecapBlockPlanningDecision.Maintain(
                        fixture.SelfId,
                        new RecapPlanningMaintainSource.Empty(start),
                        [admission],
                        EmptyRecapPriorContext.Instance
                    )
                ]
            ));

        DerivedRecapExecutionResult result =
            await fixture.CreateExecutor(policy, [maintainer])
                .RunAsync();

        var published =
            Assert.IsType<DerivedRecapExecutionResult.Published>(
                result
            );
        PublishedRecapSourceSnapshot source =
            await fixture.ReadSourceAsync(
                published.Descriptor,
                [fixture.SelfId]
            );
        DerivedRecapFrozenInput input =
            Assert.Single(source.FrozenInputs);
        Assert.Equal("stable", input.Content);
        Assert.Equal(admission, input.AbsorbedThrough);
        Assert.Equal(1, maintainer.CallCount);
    }

    [Fact]
    public async Task InheritCopiesExactSourceWithoutMaintainerCall() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 1
        );
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "source-content"
        );
        EventAddress firstAdmission =
            fixture.Engine.ReadCurrentHead()!.Value;
        EventAddress start = fixture.ReplayStart();
        var initialPolicy = new DelegatePolicy(_ =>
            new RecapPlanningPolicyDecision.Build(
                firstAdmission,
                [
                    new RecapBlockPlanningDecision.Maintain(
                        fixture.SelfId,
                        new RecapPlanningMaintainSource.Empty(start),
                        [firstAdmission],
                        EmptyRecapPriorContext.Instance
                    )
                ]
            ));
        var first = Assert.IsType<DerivedRecapExecutionResult.Published>(
            await fixture.CreateExecutor(initialPolicy, [maintainer])
                .RunAsync()
        );
        maintainer.Reset();

        EventAddress secondAdmission = fixture.AppendPair("next");
        var inheritPolicy = new DelegatePolicy(context => {
            RecapSourceIntent source =
                Assert.Single(context.PolicyFacts.AvailableSources)
                    .Source;
            return new RecapPlanningPolicyDecision.Build(
                secondAdmission,
                [
                    new RecapBlockPlanningDecision.Inherit(
                        fixture.SelfId,
                        source
                    )
                ]
            );
        });
        var second =
            Assert.IsType<DerivedRecapExecutionResult.Published>(
                await fixture.CreateExecutor(
                        inheritPolicy,
                        [maintainer]
                    )
                    .RunAsync()
            );

        PublishedRecapSourceSnapshot source =
            await fixture.ReadSourceAsync(
                second.Descriptor,
                [fixture.SelfId]
            );
        DerivedRecapFrozenInput input =
            Assert.Single(source.FrozenInputs);
        Assert.Equal("source-content", input.Content);
        Assert.Equal(firstAdmission, input.AbsorbedThrough);
        Assert.Equal(0, maintainer.CallCount);
        Assert.NotEqual(first.Descriptor, second.Descriptor);
    }

    [Fact]
    public async Task PolicyReceivesExactCursorWithoutSourcePayload() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 1
        );
        EventAddress firstAdmission =
            fixture.Engine.ReadCurrentHead()!.Value;
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "private-source-payload"
        );
        var firstPolicy = new DelegatePolicy(_ =>
            new RecapPlanningPolicyDecision.Build(
                firstAdmission,
                [
                    new RecapBlockPlanningDecision.Maintain(
                        fixture.SelfId,
                        new RecapPlanningMaintainSource.Empty(
                            fixture.ReplayStart()
                        ),
                        [firstAdmission],
                        EmptyRecapPriorContext.Instance
                    )
                ]
            ));
        var first = Assert.IsType<
            DerivedRecapExecutionResult.Published
        >(
            await fixture.CreateExecutor(firstPolicy, [maintainer])
                .RunAsync()
        );
        maintainer.Reset();
        fixture.AppendPair("next");
        RecapBlockSourceIntent? observed = null;
        var inspectPolicy = new DelegatePolicy(context => {
            Assert.Null(
                context.PolicyFacts.EmptyReplayStartExclusive
            );
            observed = Assert.Single(
                context.PolicyFacts.AvailableSources
            );
            return new RecapPlanningPolicyDecision.Unavailable([
                new RecapPlanDefect(
                    RecapPlanDefectCodes.RawBuildLimitExceeded,
                    "inspection stop"
                )
            ]);
        });

        DerivedRecapExecutionResult result =
            await fixture.CreateExecutor(inspectPolicy, [maintainer])
                .RunAsync();

        Assert.IsType<DerivedRecapExecutionResult.Unavailable>(
            result
        );
        Assert.NotNull(observed);
        Assert.Equal(fixture.SelfId, observed.RecapBlockId);
        Assert.Equal(firstAdmission, observed.AbsorbedThrough);
        Assert.Equal(
            first.Descriptor.SetAdmissionAnchor,
            observed.Source.SourceSetAnchor
        );
        Assert.Equal(
            first.Descriptor.EnvelopeSha256,
            observed.Source.SourcePublicationEnvelopeSha256
        );
        Assert.Equal(0, maintainer.CallCount);
        Assert.Equal(
            [
                nameof(RecapBlockSourceIntent.AbsorbedThrough),
                nameof(RecapBlockSourceIntent.RecapBlockId),
                nameof(RecapBlockSourceIntent.Source)
            ],
            typeof(RecapBlockSourceIntent)
                .GetProperties()
                .Select(static property => property.Name)
                .Order()
        );
    }

    [Fact]
    public async Task ResumeContinuesAfterHealthyCheckpointSuffix() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 2
        );
        (EventAddress start, EventAddress mid, EventAddress admission) =
            fixture.TwoStepRoute();
        MaintainRecapBlockPlan plan = fixture.CreateEmptyPlan(
            fixture.SelfId,
            fixture.SelfTarget,
            "self-maintainer",
            start,
            [mid, admission]
        );
        await fixture.CreateBuildingAsync(admission, [plan]);
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (call, request) => call switch {
                1 => "checkpoint-one",
                2 => throw new InvalidOperationException("zeta failed"),
                _ => request.OldBlock.Text + "+checkpoint-two"
            }
        );
        DerivedRecapBuildingExecutor executor =
            fixture.CreateBuildingExecutor(
            [maintainer],
            maxRouteEndpointsPerBlock: 2
        );

        var failed =
            Assert.IsType<DerivedRecapExecutionResult.BlockFailed>(
                await executor.ResumeAsync(admission)
            );
        Assert.Equal(fixture.SelfId, failed.RecapBlockId);
        var published =
            Assert.IsType<DerivedRecapExecutionResult.Published>(
                await executor.ResumeAsync(admission)
            );

        Assert.Equal(
            ["", "checkpoint-one", "checkpoint-one"],
            maintainer.OldBlocks
        );
        PublishedRecapSourceSnapshot source =
            await fixture.ReadSourceAsync(
                published.Descriptor,
                [fixture.SelfId]
            );
        Assert.Equal(
            admission,
            Assert.Single(source.FrozenInputs).AbsorbedThrough
        );
    }

    [Fact]
    public async Task ResumeUsesPreComponentProofWhenCheckpointIsBeyondDirectSetupLimit() {
        bool captureComponentPhase = false;
        long? headersAtComponentRead = null;
        SessionJournalEngine? diagnosticEngine = null;
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 300,
            hooks: new RecapStoreTestHooks(
                BeforeBuildingComponentRead: () => {
                    if (captureComponentPhase) {
                        headersAtComponentRead = diagnosticEngine!
                            .CaptureReadDiagnostics()
                            .HeaderPreviewReadCount;
                    }
                }
            )
        );
        diagnosticEngine = fixture.Engine;
        SessionHistoryPlanningWindow history =
            fixture.Engine.ReadHistoryPlanningWindow();
        EventAddress baselineSource =
            history.ReplaySafeBoundaries[^4].Address;
        EventAddress source =
            history.ReplaySafeBoundaries[^3].Address;
        EventAddress checkpoint =
            history.ReplaySafeBoundaries[^2].Address;
        EventAddress admission =
            history.ReplaySafeBoundaries[^1].Address;
        MaintainRecapBlockPlan firstPlan = fixture.CreateEmptyPlan(
            fixture.SelfId,
            fixture.SelfTarget,
            "self-maintainer",
            source,
            [checkpoint, admission]
        );
        Assert.IsType<SessionGoverningSetupProofResult.BeyondPrefix>(
            fixture.Engine.ReadView.ProveGoverningSetupAtBounded(
                checkpoint,
                firstPlan.CatchUpBoundaries[0].Setups,
                RecapFrozenPlanBarrier.MaxHeaderCount
            )
        );
        MaintainRecapBlockPlan baselinePlan = fixture.CreateEmptyPlan(
            fixture.SelfId,
            fixture.SelfTarget,
            "self-maintainer",
            baselineSource,
            [source]
        );
        BuildingSnapshot baseline = await fixture.CreateBuildingAsync(
            source,
            [baselinePlan]
        );
        BuildingBlockInspection baselineInitial =
            await fixture.Store.InspectBuildingBlockAsync(
                baseline.Descriptor,
                baselinePlan.RecapBlockId
            );
        DerivedRecapBlock baselineCandidate =
            DerivedRecapCodec.CreateBlock(
                baselinePlan,
                source,
                "baseline"
            );
        _ = Assert.IsType<CheckpointWriteResult.Updated>(
            await fixture.Store.AdvanceRollingCheckpointAsync(
                baseline.Descriptor,
                baselinePlan.RecapBlockId,
                baselineInitial.Checkpoint.StateToken,
                baselineCandidate
            )
        );
        BuildingBlockInspection baselineCheckpointed =
            await fixture.Store.InspectBuildingBlockAsync(
                baseline.Descriptor,
                baselinePlan.RecapBlockId
            );
        _ = Assert.IsType<FinalBlockWriteResult.Installed>(
            await fixture.Store.EnsureFinalBlockAsync(
                baseline.Descriptor,
                baselinePlan.RecapBlockId,
                baselineCheckpointed.Final.StateToken,
                baselineCandidate
            )
        );
        _ = Assert.IsType<PublishRecapResult.Published>(
            await new DerivedRecapPublisher(
                    fixture.Store,
                    fixture.Engine.ReadView
                )
                .PublishAsync(source)
        );
        BuildingSnapshot building = await fixture.CreateBuildingAsync(
            admission,
            [firstPlan]
        );
        BuildingBlockInspection inspection =
            await fixture.Store.InspectBuildingBlockAsync(
                building.Descriptor,
                firstPlan.RecapBlockId
            );
        _ = Assert.IsType<CheckpointWriteResult.Updated>(
            await fixture.Store.AdvanceRollingCheckpointAsync(
                building.Descriptor,
                firstPlan.RecapBlockId,
                inspection.Checkpoint.StateToken,
                DerivedRecapCodec.CreateBlock(
                    firstPlan,
                    checkpoint,
                    "checkpoint"
                )
            )
        );
        var firstMaintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, request) => request.OldBlock.Text + "+done"
        );
        captureComponentPhase = true;

        DerivedRecapExecutionResult result =
            await fixture.CreateBuildingExecutor(
                    [firstMaintainer],
                    maxRouteEndpointsPerBlock: 2
                )
                .ResumeAsync(admission);

        Assert.True(
            result is DerivedRecapExecutionResult.Published,
            result is DerivedRecapExecutionResult.Unavailable unavailable
                ? string.Join(
                    Environment.NewLine,
                    unavailable.Defects.Select(static defect =>
                        $"{defect.Code}: {defect.Detail}"
                    )
                )
                : $"Unexpected result: {result.GetType().Name}."
        );
        Assert.Equal(1, firstMaintainer.CallCount);
        Assert.NotNull(headersAtComponentRead);
        long headersAfterResume = fixture.Engine
            .CaptureReadDiagnostics()
            .HeaderPreviewReadCount;
        long headerDelta = headersAfterResume
            - headersAtComponentRead.Value;
        // Exact execution-boundary closure is legitimate after the component
        // barrier; neither materialization nor publication may recapture the
        // 513-header lineage authority demonstrated above.
        Assert.InRange(headerDelta, 1L, 16L);
        Assert.True(
            headerDelta < RecapFrozenPlanBarrier.MaxHeaderCount
        );
    }

    [Fact]
    public async Task ResumeRejectsFingerprintDriftBeforeMaintainerCall() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 1
        );
        EventAddress admission = fixture.Engine.ReadCurrentHead()!.Value;
        MaintainRecapBlockPlan plan = fixture.CreateEmptyPlan(
            fixture.SelfId,
            fixture.SelfTarget,
            "self-maintainer",
            fixture.ReplayStart(),
            [admission]
        );
        await fixture.CreateBuildingAsync(admission, [plan]);
        var drifted = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "must-not-run",
            "sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"
        );

        var result = Assert.IsType<
            DerivedRecapExecutionResult.Unavailable
        >(
            await fixture.CreateBuildingExecutor([drifted])
                .ResumeAsync(admission)
        );

        Assert.Contains(
            result.Defects,
            defect => defect.Code
                == DerivedRecapExecutionDefectCodes
                    .MaintainerUnavailable
        );
        Assert.Equal(0, drifted.CallCount);
    }

    [Fact]
    public async Task ResumeSelectsRetainedExactFingerprint() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 1
        );
        EventAddress admission = fixture.Engine.ReadCurrentHead()!.Value;
        MaintainRecapBlockPlan plan = fixture.CreateEmptyPlan(
            fixture.SelfId,
            fixture.SelfTarget,
            "self-maintainer",
            fixture.ReplayStart(),
            [admission]
        );
        await fixture.CreateBuildingAsync(admission, [plan]);
        var retained = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "retained"
        );
        var current = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "must-not-run",
            "sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"
        );

        Assert.IsType<DerivedRecapExecutionResult.Published>(
            await fixture.CreateBuildingExecutor([current, retained])
                .ResumeAsync(admission)
        );

        Assert.Equal(1, retained.CallCount);
        Assert.Equal(0, current.CallCount);
    }

    [Fact]
    public async Task DescriptorPinnedResumeRejectsSameAnchorReplacement() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 1
        );
        EventAddress admission =
            fixture.Engine.ReadCurrentHead()!.Value;
        MaintainRecapBlockPlan originalPlan = fixture.CreateEmptyPlan(
            fixture.SelfId,
            fixture.SelfTarget,
            "self-maintainer",
            fixture.ReplayStart(),
            [admission]
        );
        BuildingSnapshot original =
            await fixture.CreateBuildingAsync(
                admission,
                [originalPlan]
            );
        _ = Assert.IsType<QuarantineBuildingResult.Quarantined>(
            await fixture.Store.QuarantineBuildingAsync(admission)
        );
        MaintainRecapBlockPlan replacementPlan =
            fixture.CreateEmptyPlan(
                fixture.SelfId,
                fixture.SelfTarget,
                "replacement-maintainer",
                fixture.ReplayStart(),
                [admission]
            );
        BuildingSnapshot replacement =
            await fixture.CreateBuildingAsync(
                admission,
                [replacementPlan]
            );
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "must-not-run"
        );

        var result =
            Assert.IsType<DerivedRecapExecutionResult.Retryable>(
                await fixture.CreateBuildingExecutor([maintainer])
                    .ResumeAsync(original.Descriptor)
            );

        Assert.Equal(
            DerivedRecapExecutionDefectCodes.SourceChanged,
            result.Code
        );
        Assert.Contains(
            original.Descriptor.ManifestPayloadSha256,
            result.Detail
        );
        Assert.Contains(
            replacement.Descriptor.ManifestPayloadSha256,
            result.Detail
        );
        Assert.Equal(0, maintainer.CallCount);
        Assert.IsType<BuildingReadResult.Available>(
            await fixture.Store.ReadBuildingAsync(admission)
        );
    }

    [Fact]
    public async Task FinalCheckpointInstallsWithoutMaintainerCall() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 1
        );
        EventAddress admission = fixture.Engine.ReadCurrentHead()!.Value;
        MaintainRecapBlockPlan plan = fixture.CreateEmptyPlan(
            fixture.SelfId,
            fixture.SelfTarget,
            "self-maintainer",
            fixture.ReplayStart(),
            [admission]
        );
        BuildingSnapshot building =
            await fixture.CreateBuildingAsync(admission, [plan]);
        BuildingBlockInspection inspection =
            await fixture.Store.InspectBuildingBlockAsync(
                building.Descriptor,
                fixture.SelfId
            );
        _ = Assert.IsType<CheckpointWriteResult.Updated>(
            await fixture.Store.AdvanceRollingCheckpointAsync(
                building.Descriptor,
                fixture.SelfId,
                inspection.Checkpoint.StateToken,
                DerivedRecapCodec.CreateBlock(
                    plan,
                    admission,
                    "already-finished"
                )
            )
        );
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) =>
                throw new InvalidOperationException("must not run")
        );

        DerivedRecapExecutionResult result =
            await fixture.CreateBuildingExecutor(
                    [maintainer]
                )
                .ResumeAsync(admission);

        Assert.IsType<DerivedRecapExecutionResult.Published>(result);
        Assert.Equal(0, maintainer.CallCount);
    }

    [Fact]
    public async Task LaterBlockFailureResumeSkipsHealthyEarlierBlock() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 1
        );
        EventAddress admission = fixture.Engine.ReadCurrentHead()!.Value;
        EventAddress start = fixture.ReplayStart();
        RecapBlockId alphaId = new("alpha");
        RecapBlockId zetaId = new("zeta");
        ContextHeaderBlockPath alphaTarget = new(
            ContextHeaderCarrier.System,
            "alpha"
        );
        ContextHeaderBlockPath zetaTarget = new(
            ContextHeaderCarrier.System,
            "zeta"
        );
        MaintainRecapBlockPlan alphaPlan = fixture.CreateEmptyPlan(
            alphaId,
            alphaTarget,
            "alpha-maintainer",
            start,
            [admission]
        );
        MaintainRecapBlockPlan zetaPlan = fixture.CreateEmptyPlan(
            zetaId,
            zetaTarget,
            "zeta-maintainer",
            start,
            [admission]
        );
        await fixture.CreateBuildingAsync(
            admission,
            [alphaPlan, zetaPlan]
        );
        var alpha = new ScriptedMaintainer(
            "alpha-maintainer",
            alphaTarget,
            static (_, _) => "alpha-ready"
        );
        var zeta = new ScriptedMaintainer(
            "zeta-maintainer",
            zetaTarget,
            static (call, _) => call == 1
                ? throw new InvalidOperationException("zeta failed")
                : "zeta-ready"
        );
        DerivedRecapBuildingExecutor executor =
            fixture.CreateBuildingExecutor(
                [alpha, zeta],
                maxMaintainerCallsPerBuild: 2
            );

        Assert.IsType<DerivedRecapExecutionResult.BlockFailed>(
            await executor.ResumeAsync(admission)
        );
        Assert.IsType<DerivedRecapExecutionResult.Published>(
            await executor.ResumeAsync(admission)
        );

        Assert.Equal(1, alpha.CallCount);
        Assert.Equal(2, zeta.CallCount);
    }

    [Fact]
    public async Task ResumeLimitFailureHasNoMaintainerCalls() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 2
        );
        (EventAddress start, EventAddress mid, EventAddress admission) =
            fixture.TwoStepRoute();
        MaintainRecapBlockPlan plan = fixture.CreateEmptyPlan(
            fixture.SelfId,
            fixture.SelfTarget,
            "self-maintainer",
            start,
            [mid, admission]
        );
        await fixture.CreateBuildingAsync(admission, [plan]);
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "must-not-run"
        );

        DerivedRecapExecutionResult result =
            await fixture.CreateBuildingExecutor(
                    [maintainer],
                    maxRouteEndpointsPerBlock: 1
                )
                .ResumeAsync(admission);

        Assert.IsType<DerivedRecapExecutionResult.Unavailable>(result);
        Assert.Equal(0, maintainer.CallCount);
    }

    [Fact]
    public async Task PreparedResumeBeyondPrecedesBuildingComponentsAndMutation() {
        int componentReads = 0;
        int mutations = 0;
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 33,
            hooks: new RecapStoreTestHooks(
                BeforeAtomicFileReplace: _ => mutations++,
                BeforeBuildingComponentRead: () => componentReads++
            )
        );
        EventAddress admission =
            fixture.Engine.ReadCurrentHead()!.Value;
        MaintainRecapBlockPlan plan = fixture.CreateEmptyPlan(
            fixture.SelfId,
            fixture.SelfTarget,
            "self-maintainer",
            fixture.ReplayStart(),
            [admission]
        );
        _ = await fixture.CreateBuildingAsync(admission, [plan]);
        var capability = new RecapMaintainerCapabilitySnapshot([
            new RecapProfilePlanningDescriptor(
                "self-profile",
                fixture.SelfId,
                fixture.SelfTarget,
                "self-maintainer",
                RecapPlannerTestIdentity.CapabilityFingerprint
            )
        ]);
        var ready = Assert.IsType<
            DerivedRecapOperationPreparationResult.Ready
        >(await DerivedRecapOperationPreparer.PrepareExactBuildingAsync(
            fixture.Engine.ReadView,
            fixture.Store,
            capability,
            admission
        ));
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "must-not-run"
        );
        componentReads = 0;
        mutations = 0;

        var beyond = Assert.IsType<
            DerivedRecapExecutionResult.BeyondPrefix
        >(await new DerivedRecapPreparedExecutor(
            fixture.Engine.ReadView,
            fixture.Store,
            ready.Authority,
            new RecapBlockMaintainerRegistry([maintainer])
        ).ExecuteAsync());

        Assert.Equal(
            DerivedRecapBeyondPrefixStage.ResumePendingWindow,
            beyond.Stage
        );
        Assert.Equal(0, componentReads);
        Assert.Equal(0, mutations);
        Assert.Equal(0, maintainer.CallCount);
    }

    [Fact]
    public async Task ResumeInlinePriorBeyondV4ProofPrefixStopsBeforePayloadOrComponents() {
        int componentReads = 0;
        int mutations = 0;
        RecapProtocolHardCaps hardCaps = RecapProtocolHardCaps.V4;
        int proofPrefixHeaderCount =
            RecapFrozenPlanBarrier.ProofPrefixHeaderCount(hardCaps);
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: (proofPrefixHeaderCount + 1) / 2 + 2,
            hooks: new RecapStoreTestHooks(
                BeforeAtomicFileReplace: _ => mutations++,
                BeforeBuildingComponentRead: () => componentReads++
            )
        );
        _ = fixture.Engine.AppendRuntimeConfigSetup(
            new SessionRuntimeConfiguration(
                "model-inline-beyond-v4",
                "surface-inline-beyond-v4",
                SessionJournalDefaults.Schema,
                new(0)
            )
        );
        _ = fixture.Engine.AppendSystemPromptSetup(
            "system-inline-beyond-v4"
        );
        EventAddress start = fixture.AppendPair("inline-beyond-v4-start");
        EventAddress admission =
            fixture.AppendPair("inline-beyond-v4-admission");
        SessionCurrentLineageSnapshot full =
            fixture.Engine.ReadCurrentLineageHeaders();
        EventAddress inlineBeyond = full.HeadToRoot[
            proofPrefixHeaderCount
        ].Address;
        var plan = new MaintainRecapBlockPlan(
            fixture.SelfId,
            fixture.SelfTarget,
            "self-maintainer",
            RecapPlannerTestIdentity.CapabilityFingerprint,
            new EmptyRecapMaintainSource(
                start,
                RecapPlannerWireTestFacts.SetupsAt(
                    fixture.Engine,
                    start
                )
            ),
            [new RecapReplayBoundary(
                admission,
                RecapPlannerWireTestFacts.SetupsAt(
                    fixture.Engine,
                    admission
                )
            )],
            new InlineRecapPriorContext(
                inlineBeyond,
                ContextHeaderSnapshot.Empty
            ),
            TestFixture.MaxContent
        );
        _ = await fixture.CreateBuildingAsync(admission, [plan]);
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "must-not-run"
        );
        componentReads = 0;
        mutations = 0;
        SessionJournalReadDiagnostics before =
            fixture.Engine.CaptureReadDiagnostics();

        var beyond = Assert.IsType<
            DerivedRecapExecutionResult.BeyondPrefix
        >(await new DerivedRecapBuildingExecutor(
            fixture.Engine.ReadView,
            fixture.Store,
            new RecapBlockMaintainerRegistry([maintainer])
        ).ResumeAsync(admission));

        SessionJournalReadDiagnostics after =
            fixture.Engine.CaptureReadDiagnostics();
        Assert.Equal(
            DerivedRecapBeyondPrefixStage.ResumePendingWindow,
            beyond.Stage
        );
        Assert.Equal(inlineBeyond, beyond.Evidence.RequiredAnchor);
        Assert.Equal(
            inlineBeyond,
            beyond.Evidence.NextAddress
        );
        Assert.Equal(
            before.PayloadReadCount,
            after.PayloadReadCount
        );
        Assert.Equal(0, componentReads);
        Assert.Equal(0, mutations);
        Assert.Equal(0, maintainer.CallCount);
    }

    [Fact]
    public async Task ResumeOffLineageInlinePriorIsFrozenAuthorityBeforeComponents() {
        int componentReads = 0;
        int mutations = 0;
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 2,
            hooks: new RecapStoreTestHooks(
                BeforeAtomicFileReplace: _ => mutations++,
                BeforeBuildingComponentRead: () => componentReads++
            )
        );
        EventAddress start = fixture.ReplayStart();
        EventAddress admission = fixture.Engine.ReadCurrentHead()!.Value;
        EventAddress offLineage = admission with {
            SegmentNumber = admission.SegmentNumber == uint.MaxValue
                ? uint.MaxValue - 1
                : uint.MaxValue
        };
        MaintainRecapBlockPlan valid = fixture.CreateEmptyPlan(
            fixture.SelfId,
            fixture.SelfTarget,
            "self-maintainer",
            start,
            [admission]
        );
        var plan = new MaintainRecapBlockPlan(
            valid.RecapBlockId,
            valid.Target,
            valid.MaintainerId,
            valid.MaintainerCapabilityFingerprint,
            valid.Source,
            valid.CatchUpBoundaries,
            new InlineRecapPriorContext(
                offLineage,
                ContextHeaderSnapshot.Empty
            ),
            valid.MaxContentUtf8Bytes
        );
        _ = await fixture.CreateBuildingAsync(admission, [plan]);
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "must-not-run"
        );
        componentReads = 0;
        mutations = 0;
        SessionJournalReadDiagnostics before =
            fixture.Engine.CaptureReadDiagnostics();

        var unavailable = Assert.IsType<
            DerivedRecapExecutionResult.Unavailable
        >(await fixture.CreateBuildingExecutor([maintainer])
            .ResumeAsync(admission));

        Assert.Contains(
            unavailable.Defects,
            defect => defect.Code
                    == DerivedRecapExecutionDefectCodes.BuildingInvalid
                && defect.Detail.Contains(
                    "off the captured raw lineage",
                    StringComparison.Ordinal
                )
        );
        Assert.Equal(
            before.PayloadReadCount,
            fixture.Engine.CaptureReadDiagnostics().PayloadReadCount
        );
        Assert.Equal(0, componentReads);
        Assert.Equal(0, mutations);
        Assert.Equal(0, maintainer.CallCount);
    }

    [Fact]
    public async Task ResumeRawMutationDuringSeedFreezeIsRetryable() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 1
        );
        EventAddress admission =
            fixture.Engine.ReadCurrentHead()!.Value;
        MaintainRecapBlockPlan plan = fixture.CreateEmptyPlan(
            fixture.SelfId,
            fixture.SelfTarget,
            "self-maintainer",
            fixture.ReplayStart(),
            [admission]
        );
        await fixture.CreateBuildingAsync(admission, [plan]);
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "must-not-run"
        );
        bool hookInvoked = false;
        var hooks = new DerivedRecapBuildingExecutorTestHooks(
            BeforePendingWindowFreeze: () => {
                hookInvoked = true;
                fixture.Engine.AppendObservation("seed-freeze race");
            }
        );

        var result =
            Assert.IsType<DerivedRecapExecutionResult.Retryable>(
                await fixture.CreateBuildingExecutor(
                        [maintainer],
                        executorHooks: hooks
                    )
                    .ResumeAsync(admission)
            );

        Assert.True(hookInvoked);
        Assert.Equal(
            DerivedRecapExecutionDefectCodes.RawHeadChanged,
            result.Code
        );
        Assert.Contains(admission.ToString(), result.Detail);
        Assert.Contains(
            fixture.Engine.ReadCurrentHead()!.Value.ToString(),
            result.Detail
        );
        Assert.Equal(0, maintainer.CallCount);
        Assert.IsType<BuildingReadResult.Available>(
            await fixture.Store.ReadBuildingAsync(admission)
        );
    }

    [Fact]
    public async Task ResumeMaintainerRawMutationIsRetryableAndNotPublished() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 1
        );
        EventAddress admission = fixture.Engine.ReadCurrentHead()!.Value;
        MaintainRecapBlockPlan plan = fixture.CreateEmptyPlan(
            fixture.SelfId,
            fixture.SelfTarget,
            "self-maintainer",
            fixture.ReplayStart(),
            [admission]
        );
        await fixture.CreateBuildingAsync(admission, [plan]);
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "completed-before-race",
            beforeReturn: () => fixture.Engine.AppendObservation(
                "host-held writer race"
            )
        );

        var result = Assert.IsType<DerivedRecapExecutionResult.Retryable>(
            await fixture.CreateBuildingExecutor([maintainer])
                .ResumeAsync(admission)
        );

        Assert.Equal(
            DerivedRecapExecutionDefectCodes.RawHeadChanged,
            result.Code
        );
        Assert.Equal(1, maintainer.CallCount);
        Assert.IsType<BuildingReadResult.Available>(
            await fixture.Store.ReadBuildingAsync(admission)
        );
        Assert.IsType<PublishedPlanAtAnchorReadResult.Missing>(
            await fixture.Store.ReadPublishedPlanAtAnchorAsync(admission)
        );
    }

    [Fact]
    public async Task PolicyRawMutationReturnsTypedRetryableFailure() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 1
        );
        EventAddress captured = fixture.Engine.ReadCurrentHead()!.Value;
        EventAddress start = fixture.ReplayStart();
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "must-not-run"
        );
        var policy = new DelegatePolicy(_ => {
            fixture.Engine.AppendObservation("concurrent raw write");
            return new RecapPlanningPolicyDecision.Build(
                captured,
                [
                    new RecapBlockPlanningDecision.Maintain(
                        fixture.SelfId,
                        new RecapPlanningMaintainSource.Empty(start),
                        [captured],
                        EmptyRecapPriorContext.Instance
                    )
                ]
            );
        });

        var result =
            Assert.IsType<DerivedRecapExecutionResult.Retryable>(
                await fixture.CreateExecutor(policy, [maintainer])
                    .RunAsync()
            );

        Assert.Equal(
            DerivedRecapExecutionDefectCodes.RawHeadChanged,
            result.Code
        );
        Assert.Equal(0, maintainer.CallCount);
    }

    [Fact]
    public async Task InstallerRawMutationIsRetryableAndLeavesNoBuilding() {
        TestFixture? fixture = null;
        var hooks = new RecapStoreTestHooks(
            BeforeBuildingRawHeadRecheck: () =>
                fixture!.Engine.AppendObservation("installer race")
        );
        using (fixture =
               await TestFixture.CreateAsync(
                   historyPairs: 1,
                   hooks
               )) {
            EventAddress admission =
                fixture.Engine.ReadCurrentHead()!.Value;
            EventAddress start = fixture.ReplayStart();
            var maintainer = new ScriptedMaintainer(
                "self-maintainer",
                fixture.SelfTarget,
                static (_, _) => "must-not-run"
            );
            var policy = new DelegatePolicy(_ =>
                new RecapPlanningPolicyDecision.Build(
                    admission,
                    [
                        new RecapBlockPlanningDecision.Maintain(
                            fixture.SelfId,
                            new RecapPlanningMaintainSource.Empty(start),
                            [admission],
                            EmptyRecapPriorContext.Instance
                        )
                    ]
                ));

            var result =
                Assert.IsType<DerivedRecapExecutionResult.Retryable>(
                    await fixture.CreateExecutor(policy, [maintainer])
                        .RunAsync()
                );

            Assert.Equal(
                DerivedRecapExecutionDefectCodes.RawHeadChanged,
                result.Code
            );
            Assert.Equal(0, maintainer.CallCount);
            Assert.IsType<BuildingReadResult.Missing>(
                await fixture.Store.ReadBuildingAsync(admission)
            );
        }
    }

    [Fact]
    public async Task ActiveBuildingConflictIsRetryableWithoutResume() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 2
        );
        (
            EventAddress start,
            EventAddress existingAnchor,
            EventAddress targetAnchor
        ) = fixture.TwoStepRoute();
        MaintainRecapBlockPlan existingPlan =
            fixture.CreateEmptyPlan(
                fixture.SelfId,
                fixture.SelfTarget,
                "self-maintainer",
                start,
                [existingAnchor]
            );
        await fixture.CreateBuildingAsync(
            existingAnchor,
            [existingPlan]
        );
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "must-not-run"
        );
        var policy = new DelegatePolicy(_ =>
            new RecapPlanningPolicyDecision.Build(
                targetAnchor,
                [
                    new RecapBlockPlanningDecision.Maintain(
                        fixture.SelfId,
                        new RecapPlanningMaintainSource.Empty(start),
                        [targetAnchor],
                        EmptyRecapPriorContext.Instance
                    )
                ]
            ));

        var result =
            Assert.IsType<DerivedRecapExecutionResult.Retryable>(
                await fixture.CreateExecutor(policy, [maintainer])
                    .RunAsync()
            );

        Assert.Equal(
            DerivedRecapExecutionDefectCodes.BuildingRace,
            result.Code
        );
        Assert.Contains(existingAnchor.ToString(), result.Detail);
        Assert.Equal(0, maintainer.CallCount);
        Assert.IsType<BuildingReadResult.Available>(
            await fixture.Store.ReadBuildingAsync(existingAnchor)
        );
        Assert.IsType<BuildingReadResult.Missing>(
            await fixture.Store.ReadBuildingAsync(targetAnchor)
        );
    }

    [Fact]
    public async Task ResumeRejectsStructurallyValidWrongFrozenBoundarySetupsBeforeMaintainer() {
        int componentReads = 0;
        int mutations = 0;
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 2,
            hooks: new RecapStoreTestHooks(
                BeforeAtomicFileReplace: _ => mutations++,
                BeforeBuildingComponentRead: () => componentReads++
            )
        );
        (EventAddress start, EventAddress mid, EventAddress admission) =
            fixture.TwoStepRoute();
        var plan = new MaintainRecapBlockPlan(
            fixture.SelfId,
            fixture.SelfTarget,
            "self-maintainer",
            RecapPlannerTestIdentity.CapabilityFingerprint,
            new EmptyRecapMaintainSource(
                start,
                RecapPlannerWireTestFacts.SetupsAt(
                    fixture.Engine,
                    start
                )
            ),
            [
                new RecapReplayBoundary(
                    mid,
                    RecapPlannerWireTestFacts.WrongSetups(
                        RecapPlannerWireTestFacts.SetupsAt(
                            fixture.Engine,
                            mid
                        )
                    )
                ),
                new RecapReplayBoundary(
                    admission,
                    RecapPlannerWireTestFacts.SetupsAt(
                        fixture.Engine,
                        admission
                    )
                )
            ],
            EmptyRecapPriorContext.Instance,
            TestFixture.MaxContent
        );
        BuildingSnapshot building =
            await fixture.CreateBuildingAsync(admission, [plan]);
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "must-not-run"
        );
        componentReads = 0;
        mutations = 0;

        var unavailable =
            Assert.IsType<DerivedRecapExecutionResult.Unavailable>(
                await fixture.CreateBuildingExecutor([maintainer])
                    .ResumeAsync(admission)
            );

        Assert.Contains(
            unavailable.Defects,
            defect => defect.Detail.Contains(
                "conflicting frozen identity",
                StringComparison.Ordinal
            )
        );
        Assert.Equal(0, maintainer.CallCount);
        Assert.Equal(0, componentReads);
        Assert.Equal(0, mutations);
        BuildingBlockInspection inspection =
            await fixture.Store.InspectBuildingBlockAsync(
                building.Descriptor,
                plan.RecapBlockId
            );
        Assert.IsType<RollingRecapCheckpointHealth.Missing>(
            inspection.Checkpoint
        );
        Assert.IsType<FinalRecapBlockHealth.Missing>(inspection.Final);
    }

    [Fact]
    public async Task ResumeRejectsRepeatedRouteBoundaryBeforeMaintainer() {
        int componentReads = 0;
        int mutations = 0;
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 2,
            hooks: new RecapStoreTestHooks(
                BeforeAtomicFileReplace: _ => mutations++,
                BeforeBuildingComponentRead: () => componentReads++
            )
        );
        (EventAddress start, _, EventAddress admission) =
            fixture.TwoStepRoute();
        MaintainRecapBlockPlan plan = fixture.CreateEmptyPlan(
            fixture.SelfId,
            fixture.SelfTarget,
            "self-maintainer",
            start,
            [admission, admission]
        );
        BuildingSnapshot building =
            await fixture.CreateBuildingAsync(admission, [plan]);
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "must-not-run"
        );
        componentReads = 0;
        mutations = 0;

        var unavailable =
            Assert.IsType<DerivedRecapExecutionResult.Unavailable>(
                await fixture.CreateBuildingExecutor([maintainer])
                    .ResumeAsync(admission)
            );

        Assert.Contains(
            unavailable.Defects,
            defect => defect.Code
                == DerivedRecapExecutionDefectCodes.BuildingInvalid
                && defect.Detail.Contains(
                    "catch-up route is not strictly increasing within "
                        + "its target admission bound",
                    StringComparison.Ordinal
                )
        );
        Assert.Equal(0, maintainer.CallCount);
        Assert.Equal(0, componentReads);
        Assert.Equal(0, mutations);
        BuildingBlockInspection inspection =
            await fixture.Store.InspectBuildingBlockAsync(
                building.Descriptor,
                plan.RecapBlockId
            );
        Assert.IsType<RollingRecapCheckpointHealth.Missing>(
            inspection.Checkpoint
        );
        Assert.IsType<FinalRecapBlockHealth.Missing>(inspection.Final);
    }

    [Fact]
    public async Task ResumeRejectsNonAncestorInlinePriorBeforeMaintainer() {
        int componentReads = 0;
        int mutations = 0;
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 2,
            hooks: new RecapStoreTestHooks(
                BeforeAtomicFileReplace: _ => mutations++,
                BeforeBuildingComponentRead: () => componentReads++
            )
        );
        EventAddress start = fixture.ReplayStart();
        EventAddress admission = fixture.Engine.ReadCurrentHead()!.Value;
        var plan = new MaintainRecapBlockPlan(
            fixture.SelfId,
            fixture.SelfTarget,
            "self-maintainer",
            RecapPlannerTestIdentity.CapabilityFingerprint,
            new EmptyRecapMaintainSource(
                start,
                RecapPlannerWireTestFacts.SetupsAt(
                    fixture.Engine,
                    start
                )
            ),
            [
                new RecapReplayBoundary(
                    admission,
                    RecapPlannerWireTestFacts.SetupsAt(
                        fixture.Engine,
                        admission
                    )
                )
            ],
            new InlineRecapPriorContext(
                admission,
                ContextHeaderSnapshot.Empty
            ),
            TestFixture.MaxContent
        );
        await fixture.CreateBuildingAsync(admission, [plan]);
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "must-not-run"
        );
        componentReads = 0;
        mutations = 0;

        DerivedRecapExecutionResult result =
            await fixture.CreateBuildingExecutor(
                    [maintainer]
                )
                .ResumeAsync(admission);

        Assert.IsType<DerivedRecapExecutionResult.Unavailable>(result);
        Assert.Equal(0, maintainer.CallCount);
        Assert.Equal(0, componentReads);
        Assert.Equal(0, mutations);
    }

    [Fact]
    public async Task StoreWriteFailureReturnsTypedUnavailable() {
        var hooks = new RecapStoreTestHooks(
            BeforeAtomicFileReplace: _ =>
                throw new IOException("injected checkpoint failure")
        );
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 1,
            hooks
        );
        EventAddress admission = fixture.Engine.ReadCurrentHead()!.Value;
        MaintainRecapBlockPlan plan = fixture.CreateEmptyPlan(
            fixture.SelfId,
            fixture.SelfTarget,
            "self-maintainer",
            fixture.ReplayStart(),
            [admission]
        );
        await fixture.CreateBuildingAsync(admission, [plan]);
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "candidate"
        );

        var result =
            Assert.IsType<DerivedRecapExecutionResult.Unavailable>(
                await fixture.CreateBuildingExecutor(
                        [maintainer]
                    )
                    .ResumeAsync(admission)
            );

        Assert.Contains(
            result.Defects,
            defect => defect.Code
                == DerivedRecapExecutionDefectCodes.BuildingInvalid
        );
    }

    [Fact]
    public async Task ResumeRejectsSourceSetNotOlderThanTarget() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 2
        );
        SessionHistoryPlanningWindow raw =
            fixture.Engine.ReadHistoryPlanningWindow();
        EventAddress olderTarget =
            raw.ReplaySafeBoundaries[^2].Address;
        EventAddress sourceAdmission =
            raw.ReplaySafeBoundaries[^1].Address;
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "source-content"
        );
        var sourcePolicy = new DelegatePolicy(_ =>
            new RecapPlanningPolicyDecision.Build(
                sourceAdmission,
                [
                    new RecapBlockPlanningDecision.Maintain(
                        fixture.SelfId,
                        new RecapPlanningMaintainSource.Empty(
                            raw.StartExclusive
                        ),
                        [sourceAdmission],
                        EmptyRecapPriorContext.Instance
                    )
                ]
            ));
        var published =
            Assert.IsType<DerivedRecapExecutionResult.Published>(
                await fixture.CreateExecutor(
                        sourcePolicy,
                        [maintainer]
                    )
                    .RunAsync()
            );
        PublishedRecapSourceSnapshot source =
            await fixture.ReadSourceAsync(
                published.Descriptor,
                [fixture.SelfId]
            );
        DerivedRecapFrozenInput input =
            Assert.Single(source.FrozenInputs);
        var inherit = new InheritRecapBlockPlan(
            fixture.SelfId,
            fixture.SelfTarget,
            sourceAdmission,
            input.AbsorbedThroughSetups,
            published.Descriptor.EnvelopeSha256,
            input.PayloadSha256,
            TestFixture.MaxContent
        );
        await fixture.CreateBuildingAsync(olderTarget, [inherit]);
        maintainer.Reset();

        DerivedRecapExecutionResult result =
            await fixture.CreateBuildingExecutor(
                    [maintainer]
                )
                .ResumeAsync(olderTarget);

        Assert.IsType<DerivedRecapExecutionResult.Unavailable>(result);
        Assert.Equal(0, maintainer.CallCount);
    }

    [Fact]
    public async Task ResumeRejectsOversizedInheritedContent() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 1
        );
        EventAddress sourceAdmission =
            fixture.Engine.ReadCurrentHead()!.Value;
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "0123456789"
        );
        var sourcePolicy = new DelegatePolicy(_ =>
            new RecapPlanningPolicyDecision.Build(
                sourceAdmission,
                [
                    new RecapBlockPlanningDecision.Maintain(
                        fixture.SelfId,
                        new RecapPlanningMaintainSource.Empty(
                            fixture.ReplayStart()
                        ),
                        [sourceAdmission],
                        EmptyRecapPriorContext.Instance
                    )
                ]
            ));
        var published =
            Assert.IsType<DerivedRecapExecutionResult.Published>(
                await fixture.CreateExecutor(
                        sourcePolicy,
                        [maintainer]
                    )
                    .RunAsync()
            );
        PublishedRecapSourceSnapshot source =
            await fixture.ReadSourceAsync(
                published.Descriptor,
                [fixture.SelfId]
            );
        DerivedRecapFrozenInput input =
            Assert.Single(source.FrozenInputs);
        EventAddress target = fixture.AppendPair("target");
        const int smallerLimit = 5;
        var inherit = new InheritRecapBlockPlan(
            fixture.SelfId,
            fixture.SelfTarget,
            sourceAdmission,
            input.AbsorbedThroughSetups,
            published.Descriptor.EnvelopeSha256,
            input.PayloadSha256,
            smallerLimit
        );
        await fixture.CreateBuildingAsync(target, [inherit]);
        maintainer.Reset();

        DerivedRecapExecutionResult result =
            await fixture.CreateBuildingExecutor([maintainer])
                .ResumeAsync(target);

        Assert.IsType<DerivedRecapExecutionResult.Unavailable>(result);
        Assert.Equal(0, maintainer.CallCount);
    }

    [Fact]
    public async Task ResumeRejectsBuildingOlderThanLatestPublished() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 2
        );
        SessionHistoryPlanningWindow raw =
            fixture.Engine.ReadHistoryPlanningWindow();
        EventAddress start = raw.StartExclusive;
        EventAddress oldAdmission =
            raw.ReplaySafeBoundaries[^2].Address;
        EventAddress newAdmission =
            raw.ReplaySafeBoundaries[^1].Address;
        MaintainRecapBlockPlan oldPlan = fixture.CreateEmptyPlan(
            fixture.SelfId,
            fixture.SelfTarget,
            "self-maintainer",
            start,
            [oldAdmission]
        );
        await fixture.CreateBuildingAsync(oldAdmission, [oldPlan]);
        MaintainRecapBlockPlan newPlan = fixture.CreateEmptyPlan(
            fixture.SelfId,
            fixture.SelfTarget,
            "self-maintainer",
            start,
            [newAdmission]
        );
        BuildingSnapshot newer =
            await fixture.CreateBuildingAsync(
                newAdmission,
                [newPlan]
            );
        BuildingBlockInspection initial =
            await fixture.Store.InspectBuildingBlockAsync(
                newer.Descriptor,
                fixture.SelfId
            );
        DerivedRecapBlock candidate = DerivedRecapCodec.CreateBlock(
            newPlan,
            newAdmission,
            "newer-published"
        );
        _ = Assert.IsType<CheckpointWriteResult.Updated>(
            await fixture.Store.AdvanceRollingCheckpointAsync(
                newer.Descriptor,
                fixture.SelfId,
                initial.Checkpoint.StateToken,
                candidate
            )
        );
        BuildingBlockInspection checkpointed =
            await fixture.Store.InspectBuildingBlockAsync(
                newer.Descriptor,
                fixture.SelfId
            );
        _ = Assert.IsType<FinalBlockWriteResult.Installed>(
            await fixture.Store.EnsureFinalBlockAsync(
                newer.Descriptor,
                fixture.SelfId,
                checkpointed.Final.StateToken,
                candidate
            )
        );
        _ = await new DerivedRecapPublisher(
                fixture.Store,
                fixture.Engine.ReadView
            )
            .PublishAsync(newAdmission);
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "must-not-run"
        );

        DerivedRecapExecutionResult result =
            await fixture.CreateBuildingExecutor(
                    [maintainer]
                )
                .ResumeAsync(oldAdmission);

        Assert.IsType<DerivedRecapExecutionResult.Unavailable>(result);
        Assert.Equal(0, maintainer.CallCount);
    }

    [Theory]
    [InlineData("blocks", "FinalBlockReadUnavailable")]
    [InlineData("work", "CheckpointReadUnavailable")]
    public async Task ResumeRejectsOversizedArtifactBeforeMaintainer(
        string artifactDirectory,
        string expectedDefectCode
    ) {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 1
        );
        EventAddress admission =
            fixture.Engine.ReadCurrentHead()!.Value;
        MaintainRecapBlockPlan plan = fixture.CreateEmptyPlan(
            fixture.SelfId,
            fixture.SelfTarget,
            "self-maintainer",
            fixture.ReplayStart(),
            [admission]
        );
        await fixture.CreateBuildingAsync(admission, [plan]);
        string artifactPath = Path.Combine(
            fixture.Store.GetBuildingPathForTest(admission),
            artifactDirectory,
            $"{plan.RecapBlockId.Value}.json"
        );
        using (var stream = new FileStream(
                   artifactPath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None
               )) {
            stream.SetLength(DerivedRecapStore.MaxBlockBytes + 1);
        }
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "must-not-run"
        );

        var unavailable =
            Assert.IsType<DerivedRecapExecutionResult.Unavailable>(
                await fixture.CreateBuildingExecutor([maintainer])
                    .ResumeAsync(admission)
            );

        Assert.Contains(
            unavailable.Defects,
            defect => defect.Code == expectedDefectCode
        );
        Assert.Equal(0, maintainer.CallCount);
    }

    [Theory]
    [InlineData("blocks", "FinalBlockReadUnavailable")]
    [InlineData("work", "CheckpointReadUnavailable")]
    public async Task ResumeRejectsWrongKindArtifactBeforeMaintainer(
        string artifactDirectory,
        string expectedDefectCode
    ) {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 1
        );
        EventAddress admission =
            fixture.Engine.ReadCurrentHead()!.Value;
        MaintainRecapBlockPlan plan = fixture.CreateEmptyPlan(
            fixture.SelfId,
            fixture.SelfTarget,
            "self-maintainer",
            fixture.ReplayStart(),
            [admission]
        );
        await fixture.CreateBuildingAsync(admission, [plan]);
        string artifactPath = Path.Combine(
            fixture.Store.GetBuildingPathForTest(admission),
            artifactDirectory,
            $"{plan.RecapBlockId.Value}.json"
        );
        Directory.CreateDirectory(artifactPath);
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "must-not-run"
        );

        var unavailable =
            Assert.IsType<DerivedRecapExecutionResult.Unavailable>(
                await fixture.CreateBuildingExecutor([maintainer])
                    .ResumeAsync(admission)
            );

        Assert.Contains(
            unavailable.Defects,
            defect => defect.Code == expectedDefectCode
        );
        Assert.Equal(0, maintainer.CallCount);
        Assert.True(Directory.Exists(artifactPath));
    }

    private sealed class DelegatePolicy : IRecapPlanningPolicy {
        private readonly Func<
            RecapPlanningPolicyContext,
            RecapPlanningPolicyDecision
        > _decide;

        public string Id => "executor-delegate";

        public DelegatePolicy(
            Func<
                RecapPlanningPolicyContext,
                RecapPlanningPolicyDecision
            > decide
        ) {
            _decide = decide;
        }

        public int CallCount { get; private set; }

        public RecapPlanningPolicyDecision Decide(
            RecapPlanningPolicyContext context
        ) {
            CallCount++;
            return _decide(context);
        }
    }

    private sealed class ScriptedMaintainer : IRecapBlockMaintainer {
        private readonly Func<
            int,
            RecapBlockMaintenanceRequest,
            string
        > _maintain;
        private readonly Action? _beforeReturn;

        public ScriptedMaintainer(
            string id,
            ContextHeaderBlockPath target,
            Func<int, RecapBlockMaintenanceRequest, string> maintain,
            string capabilityFingerprint =
                RecapPlannerTestIdentity.CapabilityFingerprint,
            Action? beforeReturn = null
        ) {
            Id = id;
            Target = target;
            CapabilityFingerprint = capabilityFingerprint;
            _maintain = maintain;
            _beforeReturn = beforeReturn;
        }

        public string Id { get; }
        public string CapabilityFingerprint { get; }
        public ContextHeaderBlockPath Target { get; }
        public int CallCount { get; private set; }
        public List<string> OldBlocks { get; } = [];

        public ValueTask<RecapBlockMaintenanceResult> MaintainAsync(
            RecapBlockMaintenanceRequest request,
            CancellationToken ct
        ) {
            ct.ThrowIfCancellationRequested();
            CallCount++;
            OldBlocks.Add(request.OldBlock.Text);
            string content = _maintain(CallCount, request);
            _beforeReturn?.Invoke();
            return ValueTask.FromResult(
                new RecapBlockMaintenanceResult(
                    Id,
                    Target,
                    new ContextHeaderBlock(content)
                )
            );
        }

        public void Reset() {
            CallCount = 0;
            OldBlocks.Clear();
        }
    }

    private sealed class InvalidIdentityMaintainer(
        string id,
        ContextHeaderBlockPath target
    ) : IRecapBlockMaintainer {
        public string Id { get; } = id;
        public string CapabilityFingerprint { get; } =
            "sha256:0000000000000000000000000000000000000000000000000000000000000000";
        public ContextHeaderBlockPath Target { get; } = target;

        public ValueTask<RecapBlockMaintenanceResult> MaintainAsync(
            RecapBlockMaintenanceRequest request,
            CancellationToken ct
        ) => ValueTask.FromResult(
            new RecapBlockMaintenanceResult(
                Id + "-wrong",
                Target,
                new ContextHeaderBlock("invalid")
            )
        );
    }

    private sealed class TestFixture : IDisposable {
        public const int MaxContent = 4096;

        private TestFixture(
            string path,
            SessionJournalEngine engine,
            DerivedRecapStore store
        ) {
            Path = path;
            Engine = engine;
            Store = store;
        }

        public string Path { get; }
        public SessionJournalEngine Engine { get; }
        public DerivedRecapStore Store { get; }
        public RecapBlockId SelfId { get; } = new("self");
        public ContextHeaderBlockPath SelfTarget { get; } = new(
            ContextHeaderCarrier.System,
            "self"
        );

        public static async ValueTask<TestFixture> CreateAsync(
            int historyPairs,
            RecapStoreTestHooks? hooks = null
        ) {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "atelia-derived-recap-planner-tests",
                Guid.NewGuid().ToString("N")
            );
            SessionJournalEngine engine = SessionJournalEngine.Create(
                path,
                new SessionCreateOptions(
                    "model-a",
                    "system-a",
                    "surface-a"
                )
            );
            DerivedRecapStore store = hooks is null
                ? DerivedRecapStore.Open(path, engine.BranchRefId)
                : DerivedRecapStore.OpenForTest(
                    path,
                    engine.BranchRefId,
                    hooks
                );
            var fixture = new TestFixture(
                path,
                engine,
                store
            );
            await fixture.Store.CreateAsync();
            for (int index = 0; index < historyPairs; index++) {
                fixture.AppendPair(index.ToString());
            }
            return fixture;
        }

        public EventAddress AppendPair(string suffix) {
            Engine.AppendObservation($"observation {suffix}");
            return Engine.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text($"answer {suffix}")
                ]),
                new CompletionDescriptor(
                    "import",
                    "v1",
                    "model-a"
                )
            );
        }

        public EventAddress ReplayStart()
            => Engine.ReadHistoryPlanningWindow().StartExclusive;

        public (
            EventAddress Start,
            EventAddress Mid,
            EventAddress Admission
        ) TwoStepRoute() {
            SessionHistoryPlanningWindow window =
                Engine.ReadHistoryPlanningWindow();
            EventAddress[] boundaries = window.ReplaySafeBoundaries
                .Select(static item => item.Address)
                .ToArray();
            Assert.True(boundaries.Length >= 2);
            return (
                window.StartExclusive,
                boundaries[^2],
                boundaries[^1]
            );
        }

        public MaintainRecapBlockPlan CreateEmptyPlan(
            RecapBlockId id,
            ContextHeaderBlockPath target,
            string maintainerId,
            EventAddress start,
            IReadOnlyList<EventAddress> route
        ) => new(
            id,
            target,
            maintainerId,
            RecapPlannerTestIdentity.CapabilityFingerprint,
            new EmptyRecapMaintainSource(
                start,
                RecapPlannerWireTestFacts.SetupsAt(Engine, start)
            ),
            [
                .. route.Select(address => new RecapReplayBoundary(
                    address,
                    RecapPlannerWireTestFacts.SetupsAt(
                        Engine,
                        address
                    )
                ))
            ],
            EmptyRecapPriorContext.Instance,
            MaxContent
        );

        public async ValueTask<BuildingSnapshot> CreateBuildingAsync(
            EventAddress admission,
            IReadOnlyList<RecapBlockPlan> plans
        ) {
            var created = Assert.IsType<CreateBuildingResult.Created>(
                await Store.CreateBuildingAsync(
                    DerivedRecapCodec.CreateManifest(
                        Engine.BranchRefId,
                        admission,
                        RecapPlannerWireTestFacts.SetupsAt(
                            Engine,
                            admission
                        ),
                        plans
                    )
                )
            );
            var read = Assert.IsType<BuildingReadResult.Available>(
                await Store.ReadBuildingAsync(admission)
            );
            Assert.Equal(created.Descriptor, read.Snapshot.Descriptor);
            return read.Snapshot;
        }

        public DerivedRecapPlannerExecutor CreateExecutor(
            IRecapPlanningPolicy policy,
            IReadOnlyList<IRecapBlockMaintainer> maintainers,
            int minimumRecentHistoryLoad = 0,
            int recapBuildIntervalHistoryLoad = 1,
            IHistoryUnitLoadEstimator? estimator = null,
            int maxRawGrowthEventCount = 1000,
            int maxRouteEndpointsPerBlock = 4,
            int maxMaintainerCallsPerBuild = 8,
            IReadOnlyList<RecapBlockCatalogEntry>? catalog = null,
            DerivedRecapBuildingExecutorTestHooks? executorHooks = null
        ) {
            estimator ??= new TestHistoryUnitLoadEstimator();
            return new(
            Engine.ReadView,
            Store,
            new RecapPlanningInputs(
                catalog ?? [
                    new RecapBlockCatalogEntry(
                        SelfId,
                        SelfTarget,
                        "self-maintainer",
                        RecapPlannerTestIdentity.CapabilityFingerprint,
                        MaxContent
                    )
                ],
                new RecapCadenceConfig(
                    estimator.Id,
                    new HistoryLoadUnit(
                        minimumRecentHistoryLoad
                    ),
                    new HistoryLoadUnit(
                        recapBuildIntervalHistoryLoad
                    )
                ),
                estimator,
                policy
            ),
            new RecapPlanningLimits(
                maxRawGrowthEventCount,
                maxRouteEndpointsPerBlock,
                maxMaintainerCallsPerBuild,
                maxRawEventsPerStep: 1000,
                maxRawEventsPerBuild: 4000
            ),
            new RecapBlockMaintainerRegistry(maintainers),
            TestHardCaps(
                maxRouteEndpointsPerBlock,
                maxMaintainerCallsPerBuild
            ),
            executorHooks
                ?? new DerivedRecapBuildingExecutorTestHooks()
            );
        }

        public DerivedRecapBuildingExecutor CreateBuildingExecutor(
            IReadOnlyList<IRecapBlockMaintainer> maintainers,
            int maxRouteEndpointsPerBlock = 4,
            int maxMaintainerCallsPerBuild = 8,
            DerivedRecapBuildingExecutorTestHooks? executorHooks = null
        ) => new(
            Engine.ReadView,
            Store,
            new RecapBlockMaintainerRegistry(maintainers),
            TestHardCaps(
                maxRouteEndpointsPerBlock,
                maxMaintainerCallsPerBuild
            ),
            executorHooks
                ?? new DerivedRecapBuildingExecutorTestHooks()
        );

        private static RecapProtocolHardCaps TestHardCaps(
            int maxRouteEndpointsPerBlock,
            int maxMaintainerCallsPerBuild
        ) => new(
            maxRawGrowthEventCount: 1000,
            maxRouteEndpointsPerBlock,
            maxMaintainerCallsPerBuild,
            maxRawEventsPerStep: 1000,
            maxRawEventsPerBuild: 4000,
            maxContentUtf8Bytes:
                SessionContextContributionContract
                    .MaxContributionUtf8Bytes,
            maxCatalogEntries:
                SessionContextContributionContract.MaxContributionCount
        );

        public async ValueTask<PublishedRecapSourceSnapshot>
            ReadSourceAsync(
            PublishedRecapDescriptor descriptor,
            IReadOnlyList<RecapBlockId> blockIds
        ) {
            var available =
                Assert.IsType<
                    PublishedRecapSourceReadResult.Available
                >(
                    await Store.ReadPublishedSourceAsync(
                        descriptor,
                        blockIds
                    )
                );
            return available.Snapshot;
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
