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
            RecapFrozenPlanRawValidator.ValidateBlock(
                fixture.Engine,
                manifest,
                new Dictionary<
                    RecapBlockId,
                    DerivedRecapFrozenInput
                >(),
                fixture.Engine.ReadCurrentLineageHeaders(),
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

        PreparedRecapPendingWindows prepared =
            RecapPendingWindowPreparer.Prepare(
                fixture.Engine,
                admission,
                [new PendingMaintainRoute(plan, mid, 1)],
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

        _ = Assert.IsType<DerivedRecapExecutionResult.Unavailable>(
            await executor.RunAsync()
        );

        var diagnostics = Assert.IsType<
            DerivedRecapPlanningDiagnostics.RawSafetyRejected
        >(executor.LastPlanningDiagnostics);
        Assert.True(diagnostics.RawGrowthEventCount > 1);
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
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 2
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

        var unavailable =
            Assert.IsType<DerivedRecapExecutionResult.Unavailable>(
                await fixture.CreateBuildingExecutor([maintainer])
                    .ResumeAsync(admission)
            );

        Assert.Contains(
            unavailable.Defects,
            defect => defect.Detail.Contains(
                "setups do not match raw authority",
                StringComparison.Ordinal
            )
        );
        Assert.Equal(0, maintainer.CallCount);
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
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 2
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
                    "route is not strictly increasing from its exact "
                        + "source cursor",
                    StringComparison.Ordinal
                )
        );
        Assert.Equal(0, maintainer.CallCount);
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
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 2
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

        DerivedRecapExecutionResult result =
            await fixture.CreateBuildingExecutor(
                    [maintainer]
                )
                .ResumeAsync(admission);

        Assert.IsType<DerivedRecapExecutionResult.Unavailable>(result);
        Assert.Equal(0, maintainer.CallCount);
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
                fixture.Engine
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

        public ScriptedMaintainer(
            string id,
            ContextHeaderBlockPath target,
            Func<int, RecapBlockMaintenanceRequest, string> maintain,
            string capabilityFingerprint =
                RecapPlannerTestIdentity.CapabilityFingerprint
        ) {
            Id = id;
            Target = target;
            CapabilityFingerprint = capabilityFingerprint;
            _maintain = maintain;
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
            Engine,
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
            Engine,
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
