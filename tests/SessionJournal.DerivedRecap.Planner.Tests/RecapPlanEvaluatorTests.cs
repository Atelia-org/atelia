using System.Reflection;
using Atelia.Data;
using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Planner.Tests;

public sealed class RecapPlanEvaluatorTests {
    [Fact]
    public void CadenceRejectsInvalidShapesWithoutCrossUnitLimitRule() {
        Assert.Throws<ArgumentException>(
            () => new RecapCadenceConfig(
                "",
                new HistoryLoadUnit(0),
                new HistoryLoadUnit(1)
            )
        );
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RecapCadenceConfig(
                TestHistoryUnitLoadEstimator.DefaultId,
                new HistoryLoadUnit(0),
                new HistoryLoadUnit(0)
            )
        );
        Assert.Throws<OverflowException>(
            () => new RecapCadenceConfig(
                TestHistoryUnitLoadEstimator.DefaultId,
                new HistoryLoadUnit(long.MaxValue),
                new HistoryLoadUnit(1)
            )
        );

        TestModel model = TestModel.Create();
        RecapProtocolHardCaps.V4.ValidatePlanningAuthority(
            new RecapPlanningInputs(
                model.Inputs.OrderedCatalog,
                new RecapCadenceConfig(
                    model.Inputs.HistoryUnitLoadEstimator.Id,
                    new HistoryLoadUnit(200),
                    new HistoryLoadUnit(300)
                ),
                model.Inputs.HistoryUnitLoadEstimator,
                model.Inputs.Policy
            ),
            new RecapPlanningLimits(
                maxRawGrowthEventCount: 4,
                model.Limits.MaxRouteEndpointsPerBlock,
                model.Limits.MaxMaintainerCallsPerBuild,
                model.Limits.MaxRawEventsPerStep,
                model.Limits.MaxRawEventsPerBuild
            )
        );
    }

    [Fact]
    public void BelowTriggerStopsBeforeSourceOrPolicyPhase() {
        TestModel model = TestModel.Create(
            minimumRecentHistoryLoad: 1,
            recapBuildIntervalHistoryLoad: 1
        );

        RecapSchedulingResult result =
            RecapPlanEvaluator.EvaluateSchedule(
                model.Inputs,
                model.Limits,
                model.Scheduling
            );

        var noBuild =
            Assert.IsType<RecapSchedulingResult.NoBuild>(result);
        Assert.Equal(
            RecapPlanReasons.BelowCadenceThreshold,
            noBuild.Reason
        );
    }

    [Fact]
    public void GrowthIsNormalizedFromExactPublishedBaseline() {
        TestModel model = TestModel.Create();

        var ready = Assert.IsType<RecapSchedulingResult.Ready>(
            RecapPlanEvaluator.EvaluateSchedule(
                model.Inputs,
                model.Limits,
                model.Scheduling
            )
        );

        Assert.Equal(1, ready.Cadence.GrowthHistoryUnitCount);
        Assert.Equal(
            1,
            ready.Cadence.GrowthHistoryLoad.Value
        );
        Assert.Equal(1, ready.Cadence.RawGrowthEventCount);
        Assert.Equal(model.SourceSet, ready.Cadence.Baseline);
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(5, true)]
    public void LoadThresholdUsesExactRPlusBBoundary(
        long growthLoad,
        bool expectsReady
    ) {
        TestModel model = TestModel.Create(
            minimumRecentHistoryLoad: 2,
            recapBuildIntervalHistoryLoad: 3
        );
        RecapHistoryLoadMeasurement original =
            model.Scheduling.HistoryLoadMeasurement;
        var measurement = new RecapHistoryLoadMeasurement(
            original.EstimatorId,
            original.BaselineAddress,
            original.BaselineCompletedUnitCount,
            new HistoryLoadUnit(growthLoad),
            original.RenderedUtf8Bytes,
            [
                new(
                    model.Admission,
                    historyUnitCountSinceBaseline: 1,
                    new HistoryLoadUnit(3)
                )
            ]
        );
        RecapSchedulingFacts facts =
            SchedulingWithMeasurement(model, measurement);

        RecapSchedulingResult result =
            RecapPlanEvaluator.EvaluateSchedule(
                model.Inputs,
                model.Limits,
                facts
            );

        if (expectsReady) {
            var ready =
                Assert.IsType<RecapSchedulingResult.Ready>(result);
            RecapCadenceBoundary candidate =
                Assert.Single(ready.Cadence.AdmissionCandidates);
            Assert.Equal(3, candidate.AbsorbedHistoryLoad.Value);
            Assert.Equal(2, candidate.RecentHistoryLoad.Value);
        }
        else {
            var noBuild =
                Assert.IsType<RecapSchedulingResult.NoBuild>(result);
            Assert.Equal(
                RecapPlanReasons.BelowCadenceThreshold,
                noBuild.Reason
            );
        }
    }

    [Fact]
    public void AbsorbedAndRecentLoadsAreIndependentlyRequired() {
        TestModel model = TestModel.Create(
            minimumRecentHistoryLoad: 3,
            recapBuildIntervalHistoryLoad: 4
        );
        RecapHistoryLoadMeasurement original =
            model.Scheduling.HistoryLoadMeasurement;
        foreach ((long absorbed, bool eligible) in new[] {
                     (3L, false),
                     (7L, true),
                     (8L, false)
                 }) {
            var measurement = new RecapHistoryLoadMeasurement(
                original.EstimatorId,
                original.BaselineAddress,
                original.BaselineCompletedUnitCount,
                new HistoryLoadUnit(10),
                original.RenderedUtf8Bytes,
                [
                    new(
                        model.Admission,
                        historyUnitCountSinceBaseline: 1,
                        new HistoryLoadUnit(absorbed)
                    )
                ]
            );

            RecapSchedulingResult result =
                RecapPlanEvaluator.EvaluateSchedule(
                    model.Inputs,
                    model.Limits,
                    SchedulingWithMeasurement(model, measurement)
                );
            if (eligible) {
                RecapCadenceBoundary candidate = Assert.Single(
                    Assert.IsType<RecapSchedulingResult.Ready>(result)
                        .Cadence.AdmissionCandidates
                );
                Assert.Equal(absorbed,
                    candidate.AbsorbedHistoryLoad.Value);
                Assert.Equal(10 - absorbed,
                    candidate.RecentHistoryLoad.Value);
            }
            else {
                Assert.Equal(
                    RecapPlanReasons.AwaitingReplaySafeAdmission,
                    Assert.IsType<RecapSchedulingResult.NoBuild>(result)
                        .Reason
                );
            }
        }
    }

    [Fact]
    public void HugeRecentUnitDoesNotMakeOlderAdmissionEligible() {
        TestModel model = TestModel.Create(
            minimumRecentHistoryLoad: 90,
            recapBuildIntervalHistoryLoad: 5
        );
        RecapHistoryLoadMeasurement original =
            model.Scheduling.HistoryLoadMeasurement;
        var measurement = new RecapHistoryLoadMeasurement(
            original.EstimatorId,
            original.BaselineAddress,
            original.BaselineCompletedUnitCount,
            new HistoryLoadUnit(100),
            original.RenderedUtf8Bytes,
            [
                new(
                    model.Admission,
                    historyUnitCountSinceBaseline: 1,
                    new HistoryLoadUnit(1)
                )
            ]
        );

        var noBuild = Assert.IsType<RecapSchedulingResult.NoBuild>(
            RecapPlanEvaluator.EvaluateSchedule(
                model.Inputs,
                model.Limits,
                SchedulingWithMeasurement(model, measurement)
            )
        );

        Assert.Equal(
            RecapPlanReasons.AwaitingReplaySafeAdmission,
            noBuild.Reason
        );
    }

    [Fact]
    public void MeasurementIdentityBaselineAndBoundaryShapeAreTypedInvalid() {
        TestModel model = TestModel.Create();
        RecapHistoryLoadMeasurement original =
            model.Scheduling.HistoryLoadMeasurement;
        RecapHistoryLoadMeasurement[] malformed = [
            new(
                "other-estimator",
                original.BaselineAddress,
                original.BaselineCompletedUnitCount,
                original.Growth,
                original.RenderedUtf8Bytes,
                original.ReplaySafeBoundaries
            ),
            new(
                original.EstimatorId,
                model.A11,
                original.BaselineCompletedUnitCount,
                original.Growth,
                original.RenderedUtf8Bytes,
                original.ReplaySafeBoundaries
            ),
            new(
                original.EstimatorId,
                original.BaselineAddress,
                original.BaselineCompletedUnitCount,
                original.Growth,
                original.RenderedUtf8Bytes,
                [
                    new(
                        model.Admission,
                        historyUnitCountSinceBaseline: 0,
                        original.Growth
                    )
                ]
            )
        ];

        foreach (RecapHistoryLoadMeasurement measurement
                 in malformed) {
            AssertDefect(
                RecapPlanEvaluator.EvaluateSchedule(
                    model.Inputs,
                    model.Limits,
                    SchedulingWithMeasurement(model, measurement)
                ),
                RecapPlanDefectCodes.PlanningFactsInvalid
            );
        }
    }

    [Fact]
    public void SharedCompletedCountKeepsExactBoundaryOrdinal() {
        TestModel model = TestModel.Create();
        var window = new RecapHistoryWindowFacts(
            model.Scheduling.HistoryWindow.StartExclusive,
            totalHistoryUnitCount: 4,
            [
                new(model.A1, 1),
                new(model.A5, 2),
                new(model.A11, 3),
                new(model.SourceSet, 3),
                new(model.Admission, 4)
            ]
        );
        RecapHistoryLoadMeasurement exact =
            TestHistoryLoadMeasurement.UnitCountEquivalent(
                window,
                model.A11
            );
        var facts = new RecapSchedulingFacts(
            model.Scheduling.CapturedHead,
            model.Scheduling.HeadToRoot,
            window,
            model.A11,
            model.A11,
            exact
        );

        Assert.IsType<RecapSchedulingResult.Ready>(
            RecapPlanEvaluator.EvaluateSchedule(
                model.Inputs,
                model.Limits,
                facts
            )
        );

        var reordered = new RecapHistoryLoadMeasurement(
            exact.EstimatorId,
            exact.BaselineAddress,
            exact.BaselineCompletedUnitCount,
            exact.Growth,
            exact.RenderedUtf8Bytes,
            [.. exact.ReplaySafeBoundaries.Reverse()]
        );
        AssertDefect(
            RecapPlanEvaluator.EvaluateSchedule(
                model.Inputs,
                model.Limits,
                new RecapSchedulingFacts(
                    facts.CapturedHead,
                    facts.HeadToRoot,
                    facts.HistoryWindow,
                    facts.CadenceBaseline,
                    facts.LatestPublishedSetAnchor,
                    reordered
                )
            ),
            RecapPlanDefectCodes.PlanningFactsInvalid
        );
    }

    [Fact]
    public void RawHardLimitStopsBeforePolicyPhase() {
        TestModel model = TestModel.Create();
        var limits = new RecapPlanningLimits(
            maxRawGrowthEventCount: 1,
            model.Limits.MaxRouteEndpointsPerBlock,
            model.Limits.MaxMaintainerCallsPerBuild,
            model.Limits.MaxRawEventsPerStep,
            model.Limits.MaxRawEventsPerBuild
        );
        var window = new RecapHistoryWindowFacts(
            model.A1,
            totalHistoryUnitCount: 1,
            [
                new(model.A5, 0),
                new(model.A11, 0),
                new(model.SourceSet, 0),
                new(model.Admission, 1)
            ]
        );
        var scheduling = new RecapSchedulingFacts(
            model.Scheduling.CapturedHead,
            model.Scheduling.HeadToRoot,
            window,
            model.A11,
            model.A11,
            TestHistoryLoadMeasurement.UnitCountEquivalent(
                window,
                model.A11
            )
        );

        RecapSchedulingResult result =
            RecapPlanEvaluator.EvaluateSchedule(
                model.Inputs,
                limits,
                scheduling
            );

        AssertDefect(
            result,
            RecapPlanDefectCodes.MaxRawGrowthEventCountExceeded
        );
    }

    [Fact]
    public void MissingOrMismatchedPublishedBaselineIsTypedInvalid() {
        TestModel model = TestModel.Create();
        var mismatched = new RecapSchedulingFacts(
            model.Scheduling.CapturedHead,
            model.Scheduling.HeadToRoot,
            model.Scheduling.HistoryWindow,
            model.A11,
            model.SourceSet,
            model.Scheduling.HistoryLoadMeasurement
        );
        AssertDefect(
            RecapPlanEvaluator.EvaluateSchedule(
                model.Inputs,
                model.Limits,
                mismatched
            ),
            RecapPlanDefectCodes.CadenceBaselineInvalid
        );

        var missingWindow = new RecapHistoryWindowFacts(
            model.Scheduling.HistoryWindow.StartExclusive,
            model.Scheduling.HistoryWindow
                .TotalHistoryUnitCount,
            [
                .. model.Scheduling.HistoryWindow
                    .ReplaySafeBoundaries
                    .Where(boundary =>
                        boundary.Address != model.A11)
            ]
        );
        var missingBoundary = new RecapSchedulingFacts(
            model.Scheduling.CapturedHead,
            model.Scheduling.HeadToRoot,
            missingWindow,
            model.A11,
            model.A11,
            model.Scheduling.HistoryLoadMeasurement
        );
        AssertDefect(
            RecapPlanEvaluator.EvaluateSchedule(
                model.Inputs,
                model.Limits,
                missingBoundary
            ),
            RecapPlanDefectCodes.CadenceBaselineInvalid
        );
    }

    [Fact]
    public void ThresholdWithoutClosedAdmissionWaitsWithoutDefect() {
        TestModel model = TestModel.Create(
            minimumRecentHistoryLoad: 1
        );
        var window = new RecapHistoryWindowFacts(
            model.Scheduling.HistoryWindow.StartExclusive,
            totalHistoryUnitCount: 6,
            [
                new(model.A1, 1),
                new(model.A5, 2),
                new(model.A11, 3),
                new(model.SourceSet, 6),
                new(model.Admission, 6)
            ]
        );
        var scheduling = new RecapSchedulingFacts(
            model.Scheduling.CapturedHead,
            model.Scheduling.HeadToRoot,
            window,
            model.A11,
            model.A11,
            TestHistoryLoadMeasurement.UnitCountEquivalent(
                window,
                model.A11
            )
        );

        var noBuild = Assert.IsType<RecapSchedulingResult.NoBuild>(
            RecapPlanEvaluator.EvaluateSchedule(
                model.Inputs,
                model.Limits,
                scheduling
            )
        );
        Assert.Equal(
            RecapPlanReasons.AwaitingReplaySafeAdmission,
            noBuild.Reason
        );
    }

    [Fact]
    public void RawSafetyUsesOnlyExactLineageDistance() {
        TestModel model = TestModel.Create();
        var lineage = new SessionCurrentLineageSnapshot(
            model.Scheduling.CapturedHead,
            model.Scheduling.HeadToRoot,
            new SessionCurrentLineageDiagnostics(0, 0, 0)
        );

        var safe = Assert.IsType<RecapRawSafetyResult.Safe>(
            RecapPlanEvaluator.EvaluateRawSafety(
                model.Limits,
                lineage,
                model.SourceSet
            )
        );
        Assert.Equal(1, safe.RawGrowthEventCount);

        var limits = new RecapPlanningLimits(
            maxRawGrowthEventCount: 1,
            model.Limits.MaxRouteEndpointsPerBlock,
            model.Limits.MaxMaintainerCallsPerBuild,
            model.Limits.MaxRawEventsPerStep,
            model.Limits.MaxRawEventsPerBuild
        );
        var rejected = Assert.IsType<
            RecapRawSafetyResult.Unavailable
        >(
            RecapPlanEvaluator.EvaluateRawSafety(
                limits,
                lineage,
                model.A11
            )
        );
        Assert.Equal(2, rejected.RawGrowthEventCount);
        Assert.Contains(
            rejected.Defects,
            defect => defect.Code
                == RecapPlanDefectCodes
                    .MaxRawGrowthEventCountExceeded
        );
    }

    [Fact]
    public void EvaluatorRejectsPolicyThatConsumesRecentReserve() {
        TestModel model = TestModel.Create();
        var maliciousPolicy = new StubPolicy(
            new RecapPlanningPolicyDecision.NoBuild("placeholder")
        );
        var inputs = new RecapPlanningInputs(
            model.Inputs.OrderedCatalog,
            new RecapCadenceConfig(
                model.Inputs.HistoryUnitLoadEstimator.Id,
                new HistoryLoadUnit(1),
                new HistoryLoadUnit(1)
            ),
            model.Inputs.HistoryUnitLoadEstimator,
            maliciousPolicy
        );
        var window = new RecapHistoryWindowFacts(
            model.Scheduling.HistoryWindow.StartExclusive,
            totalHistoryUnitCount: 6,
            [
                new(model.A1, 1),
                new(model.A5, 2),
                new(model.A11, 3),
                new(model.SourceSet, 5),
                new(model.Admission, 6)
            ]
        );
        var scheduling = new RecapSchedulingFacts(
            model.Scheduling.CapturedHead,
            model.Scheduling.HeadToRoot,
            window,
            model.A11,
            model.A11,
            TestHistoryLoadMeasurement.UnitCountEquivalent(
                window,
                model.A11
            )
        );
        var ready = Assert.IsType<RecapSchedulingResult.Ready>(
            RecapPlanEvaluator.EvaluateSchedule(
                inputs,
                model.Limits,
                scheduling
            )
        );
        var source = new RecapSourceIntent(
            model.A11,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        );
        var policyFacts = new RecapPolicyFacts(
            emptyReplayStartExclusive: null,
            [
                new RecapBlockSourceIntent(
                    model.ClientId,
                    source,
                    model.A1
                )
            ]
        );
        var malicious = new RecapPlanningPolicyDecision.Build(
            model.Admission,
            [
                new RecapBlockPlanningDecision.Maintain(
                    model.ClientId,
                    new RecapPlanningMaintainSource.Existing(source),
                    [model.SourceSet, model.Admission],
                    EmptyRecapPriorContext.Instance
                )
            ]
        );

        maliciousPolicy.Decision = malicious;
        RecapPlanIntentResult result =
            RecapPlanEvaluator.EvaluateIntent(ready, policyFacts);

        AssertDefect(result, RecapPlanDefectCodes.AdmissionInvalid);
    }

    [Fact]
    public void PlanReadyCannotBeConstructedOutsideEvaluator() {
        ConstructorInfo[] constructors =
            typeof(RecapPlanResult.PlanReady).GetConstructors(
                BindingFlags.Instance | BindingFlags.Public
            );

        Assert.Empty(constructors);
    }

    [Fact]
    public void NullFirstLineageNodeReturnsUnavailable() {
        TestModel model = TestModel.Create();
        var malformed = new RecapSchedulingFacts(
            model.Admission,
            [null!],
            model.Scheduling.HistoryWindow,
            model.SourceSet,
            null,
            model.Scheduling.HistoryLoadMeasurement
        );

        RecapSchedulingResult result =
            RecapPlanEvaluator.EvaluateSchedule(
                model.Inputs,
                model.Limits,
                malformed
            );

        AssertDefect(
            result,
            RecapPlanDefectCodes.PlanningFactsInvalid
        );
    }

    [Fact]
    public void DuplicateCatalogIdentityOrTargetIsRejected() {
        TestModel model = TestModel.Create();
        RecapBlockCatalogEntry entry =
            model.Inputs.OrderedCatalog[0];

        Assert.Throws<ArgumentException>(() =>
            model.NewInputs([entry, entry with { }])
        );
        Assert.Throws<ArgumentException>(() =>
            model.NewInputs([
                entry,
                new RecapBlockCatalogEntry(
                    new RecapBlockId("other"),
                    entry.Target,
                    "other-maintainer",
                    RecapPlannerTestIdentity.CapabilityFingerprint,
                    1024
                )
            ])
        );
    }

    [Fact]
    public void MaintainerIdMayBeSharedAcrossDistinctCatalogEntries() {
        TestModel model = TestModel.Create();
        RecapBlockCatalogEntry first =
            model.Inputs.OrderedCatalog[0];

        RecapPlanningInputs inputs = model.NewInputs([
            first,
            new RecapBlockCatalogEntry(
                new RecapBlockId("roleplay.self"),
                new ContextHeaderBlockPath(
                    ContextHeaderCarrier.System,
                    "roleplay.self"
                ),
                first.MaintainerId,
                RecapPlannerTestIdentity.CapabilityFingerprint,
                1024
            )
        ]);

        Assert.Equal(2, inputs.OrderedCatalog.Count);
    }

    [Fact]
    public void DuplicateOrConflictingSourceFactsAreRejected() {
        TestModel model = TestModel.Create();
        var conflicting = new RecapPolicyFacts(
            emptyReplayStartExclusive: null,
            [
                model.AvailableSource,
                model.AvailableSource with {
                    Source = new RecapSourceIntent(
                        model.SourceSet,
                        new string('b', 64)
                    )
                }
            ]
        );
        var policy = new StubPolicy(model.ValidMaintainIntent());
        RecapSchedulingResult.Ready schedule =
            model.Schedule(policy);

        RecapPlanIntentResult result =
            RecapPlanEvaluator.EvaluateIntent(
                schedule,
                conflicting
            );

        AssertDefect(
            result,
            RecapPlanDefectCodes.PlanningFactsInvalid
        );
        Assert.Equal(0, policy.CallCount);
    }

    [Fact]
    public void MissingCatalogSourceOrNewerCursorIsRejected() {
        TestModel twoBlocks = TestModel.Create(twoBlocks: true);
        var missing = new RecapPolicyFacts(
            emptyReplayStartExclusive: null,
            [twoBlocks.AvailableSource]
        );
        var missingPolicy = new StubPolicy(
            twoBlocks.ValidMaintainIntent()
        );

        RecapPlanIntentResult missingResult =
            RecapPlanEvaluator.EvaluateIntent(
                twoBlocks.Schedule(missingPolicy),
                missing
            );

        TestModel oneBlock = TestModel.Create();
        var newerCursor = new RecapPolicyFacts(
            emptyReplayStartExclusive: null,
            [
                oneBlock.AvailableSource with {
                    AbsorbedThrough = oneBlock.Admission
                }
            ]
        );
        var cursorPolicy = new StubPolicy(
            oneBlock.ValidMaintainIntent()
        );
        RecapPlanIntentResult cursorResult =
            RecapPlanEvaluator.EvaluateIntent(
                oneBlock.Schedule(cursorPolicy),
                newerCursor
            );

        AssertDefect(
            missingResult,
            RecapPlanDefectCodes.PlanningFactsInvalid
        );
        AssertDefect(
            cursorResult,
            RecapPlanDefectCodes.PlanningFactsInvalid
        );
        Assert.Equal(0, missingPolicy.CallCount);
        Assert.Equal(0, cursorPolicy.CallCount);
    }

    [Fact]
    public void FirstBuildAndExistingSourceFactsCannotBeMixed() {
        TestModel model = TestModel.Create();
        var mixed = new RecapPolicyFacts(
            model.A1,
            [model.AvailableSource]
        );
        var policy = new StubPolicy(model.ValidMaintainIntent());

        RecapPlanIntentResult result =
            RecapPlanEvaluator.EvaluateIntent(
                model.Schedule(policy),
                mixed
            );

        AssertDefect(
            result,
            RecapPlanDefectCodes.PlanningFactsInvalid
        );
        Assert.Equal(0, policy.CallCount);
    }

    [Fact]
    public void FirstBuildPolicyMustUseExactAuthorizedEmptySeed() {
        TestModel model = TestModel.Create();
        var firstBuildWindow = new RecapHistoryWindowFacts(
            model.A1,
            totalHistoryUnitCount: 4,
            [
                new(model.A5, 1),
                new(model.A11, 2),
                new(model.SourceSet, 3),
                new(model.Admission, 4)
            ]
        );
        var firstBuildScheduling = new RecapSchedulingFacts(
            model.Scheduling.CapturedHead,
            model.Scheduling.HeadToRoot,
            firstBuildWindow,
            model.A1,
            latestPublishedSetAnchor: null,
            TestHistoryLoadMeasurement.UnitCountEquivalent(
                firstBuildWindow,
                model.A1
            )
        );
        var facts = new RecapPolicyFacts(model.A1, []);
        var decision = new RecapPlanningPolicyDecision.Build(
            model.Admission,
            [
                new RecapBlockPlanningDecision.Maintain(
                    model.ClientId,
                    new RecapPlanningMaintainSource.Empty(model.A5),
                    [model.A11, model.Admission],
                    EmptyRecapPriorContext.Instance
                )
            ]
        );
        var policy = new StubPolicy(decision);
        RecapSchedulingResult.Ready schedule =
            model.Schedule(policy, firstBuildScheduling);

        RecapPlanIntentResult result =
            RecapPlanEvaluator.EvaluateIntent(schedule, facts);

        AssertDefect(result, RecapPlanDefectCodes.SourceInvalid);
    }

    [Fact]
    public void ExistingSourceFactsCannotAuthorizeEmptyReseed() {
        TestModel model = TestModel.Create();
        var decision = new RecapPlanningPolicyDecision.Build(
            model.Admission,
            [
                new RecapBlockPlanningDecision.Maintain(
                    model.ClientId,
                    new RecapPlanningMaintainSource.Empty(model.A1),
                    [model.A5, model.A11, model.Admission],
                    EmptyRecapPriorContext.Instance
                )
            ]
        );

        RecapPlanIntentResult result = model.EvaluateIntent(decision);

        AssertDefect(result, RecapPlanDefectCodes.SourceInvalid);
    }

    [Fact]
    public void PolicyUnavailableDefectsAreMappedWithoutNoBuild() {
        TestModel model = TestModel.Create();
        var policy = new StubPolicy(
            new RecapPlanningPolicyDecision.Unavailable([
                new RecapPlanDefect(
                    RecapPlanDefectCodes.RawBuildLimitExceeded,
                    "bounded policy cannot admit a set"
                )
            ])
        );

        RecapPlanIntentResult result =
            RecapPlanEvaluator.EvaluateIntent(
                model.Schedule(policy),
                model.PolicyFacts()
            );

        AssertDefect(
            result,
            RecapPlanDefectCodes.RawBuildLimitExceeded
        );
        Assert.IsNotType<RecapPlanIntentResult.NoBuild>(result);
    }

    [Fact]
    public void PolicyFailureIsTypedAndCancellationPropagates() {
        TestModel model = TestModel.Create();
        var failed = new ThrowingPolicy(
            new InvalidOperationException("policy broke")
        );

        RecapPlanIntentResult result = RecapPlanEvaluator.EvaluateIntent(
            model.Schedule(failed),
            model.PolicyFacts()
        );

        AssertDefect(result, RecapPlanDefectCodes.PolicyFailed);
        var canceled = new ThrowingPolicy(
            new OperationCanceledException("policy canceled")
        );
        Assert.Throws<OperationCanceledException>(() =>
            RecapPlanEvaluator.EvaluateIntent(
                model.Schedule(canceled),
                model.PolicyFacts()
            )
        );
    }

    [Fact]
    public void InheritAndMaintainAreExplicitIntents() {
        TestModel model = TestModel.Create(twoBlocks: true);
        RecapSourceIntent source = model.AvailableSource.Source;
        var decision = new RecapPlanningPolicyDecision.Build(
            model.Admission,
            [
                new RecapBlockPlanningDecision.Inherit(
                    model.ClientId,
                    source
                ),
                new RecapBlockPlanningDecision.Maintain(
                    model.SelfId,
                    new RecapPlanningMaintainSource.Existing(source),
                    [model.A5, model.A11, model.Admission],
                    EmptyRecapPriorContext.Instance
                )
            ]
        );

        RecapPlanIntentResult result = model.EvaluateIntent(decision);

        var ready =
            Assert.IsType<RecapPlanIntentResult.IntentReady>(result);
        Assert.IsType<RecapBlockPlanningDecision.Inherit>(
            ready.Intent.Blocks[0]
        );
        Assert.IsType<RecapBlockPlanningDecision.Maintain>(
            ready.Intent.Blocks[1]
        );
    }

    [Fact]
    public void RouteMustStrictlyIncreaseAndEndAtAdmission() {
        TestModel model = TestModel.Create();

        RecapPlanIntentResult nonIncreasing = model.EvaluateIntent(
            model.MaintainIntent([
                model.A11,
                model.A5,
                model.Admission
            ])
        );
        RecapPlanIntentResult wrongFinal = model.EvaluateIntent(
            model.MaintainIntent([model.A5, model.A11])
        );

        AssertDefect(nonIncreasing, RecapPlanDefectCodes.RouteInvalid);
        AssertDefect(wrongFinal, RecapPlanDefectCodes.RouteInvalid);
    }

    [Fact]
    public void ExactSourceCursorAndInlinePriorAreValidatedPreflight() {
        TestModel model = TestModel.Create();
        RecapPlanIntentResult.IntentReady intent =
            model.IntentReady(model.ValidMaintainIntent(
                new InlineRecapPriorContext(
                    model.A5,
                    ContextHeaderSnapshot.Empty
                )
            ));

        RecapPlanResult result = RecapPlanEvaluator.ValidatePlan(
            intent,
            model.Preflight()
        );

        AssertDefect(
            result,
            RecapPlanDefectCodes.PriorContextInvalid
        );
    }

    [Fact]
    public void ExistingRouteStartsAtExactSourceCursorNotContainer() {
        TestModel model = TestModel.Create();
        RecapPlanIntentResult.IntentReady intent =
            model.IntentReady(model.ValidMaintainIntent());

        RecapPlanResult result = RecapPlanEvaluator.ValidatePlan(
            intent,
            model.Preflight()
        );

        Assert.IsType<RecapPlanResult.PlanReady>(result);
    }

    [Fact]
    public void RouteAndCallLimitsProvideIntentBackpressure() {
        TestModel routeLimited = TestModel.Create(
            maxRouteEndpoints: 2
        );
        TestModel callLimited = TestModel.Create(
            maxMaintainerCalls: 2
        );

        AssertDefect(
            routeLimited.EvaluateIntent(
                routeLimited.ValidMaintainIntent()
            ),
            RecapPlanDefectCodes.RouteLimitExceeded
        );
        AssertDefect(
            callLimited.EvaluateIntent(
                callLimited.ValidMaintainIntent()
            ),
            RecapPlanDefectCodes.CallLimitExceeded
        );
    }

    [Fact]
    public void RawStepAndBuildCostsProvidePreCallBackpressure() {
        TestModel stepLimited = TestModel.Create(
            maxRawEventsPerStep: 1,
            maxRawEventsPerBuild: 10
        );
        TestModel buildLimited = TestModel.Create(
            maxRawEventsPerStep: 10,
            maxRawEventsPerBuild: 3
        );

        RecapPlanResult step = RecapPlanEvaluator.ValidatePlan(
            stepLimited.IntentReady(
                stepLimited.ValidMaintainIntent()
            ),
            stepLimited.Preflight([2, 1, 1])
        );
        RecapPlanResult build = RecapPlanEvaluator.ValidatePlan(
            buildLimited.IntentReady(
                buildLimited.ValidMaintainIntent()
            ),
            buildLimited.Preflight([2, 1, 1])
        );

        AssertDefect(
            step,
            RecapPlanDefectCodes.RawStepLimitExceeded
        );
        AssertDefect(
            build,
            RecapPlanDefectCodes.RawBuildLimitExceeded
        );
    }

    [Fact]
    public void CatalogContentLimitUsesNeutralHardLimit() {
        TestModel model = TestModel.Create();
        RecapBlockCatalogEntry entry =
            model.Inputs.OrderedCatalog[0];

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RecapBlockCatalogEntry(
                entry.RecapBlockId,
                entry.Target,
                entry.MaintainerId,
                entry.MaintainerCapabilityFingerprint,
                SessionContextContributionContract
                    .MaxContributionUtf8Bytes + 1
            )
        );
    }

    private static void AssertDefect(
        object result,
        string code
    ) {
        IReadOnlyList<RecapPlanDefect> defects = result switch {
            RecapSchedulingResult.Unavailable unavailable =>
                unavailable.Defects,
            RecapPlanIntentResult.Unavailable unavailable =>
                unavailable.Defects,
            RecapPlanResult.Unavailable unavailable =>
                unavailable.Defects,
            _ => throw new Xunit.Sdk.XunitException(
                $"Expected unavailable result, got {result.GetType().Name}."
            )
        };
        Assert.Contains(defects, defect => defect.Code == code);
    }

    private static RecapSchedulingFacts SchedulingWithMeasurement(
        TestModel model,
        RecapHistoryLoadMeasurement measurement
    ) => new(
        model.Scheduling.CapturedHead,
        model.Scheduling.HeadToRoot,
        model.Scheduling.HistoryWindow,
        model.Scheduling.CadenceBaseline,
        model.Scheduling.LatestPublishedSetAnchor,
        measurement
    );

    private sealed class StubPolicy : IRecapPlanningPolicy {
        public StubPolicy(RecapPlanningPolicyDecision decision) {
            Decision = decision;
        }

        public int CallCount { get; private set; }
        public string Id => "evaluator-stub";
        public RecapPlanningPolicyDecision Decision { get; set; }

        public RecapPlanningPolicyDecision Decide(
            RecapPlanningPolicyContext context
        ) {
            CallCount++;
            return Decision;
        }
    }

    private sealed class ThrowingPolicy(Exception exception)
        : IRecapPlanningPolicy {
        public string Id => "throwing-policy";

        public RecapPlanningPolicyDecision Decide(
            RecapPlanningPolicyContext context
        ) => throw exception;
    }

    private sealed class TestModel {
        private const string Envelope =
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        private TestModel(
            RecapPlanningInputs inputs,
            RecapPlanningLimits limits,
            RecapSchedulingFacts scheduling,
            RecapBlockSourceIntent availableSource,
            RecapBlockId clientId,
            RecapBlockId selfId,
            EventAddress admission,
            EventAddress sourceSet,
            EventAddress a11,
            EventAddress a5,
            EventAddress a1
        ) {
            Inputs = inputs;
            Limits = limits;
            Scheduling = scheduling;
            AvailableSource = availableSource;
            ClientId = clientId;
            SelfId = selfId;
            Admission = admission;
            SourceSet = sourceSet;
            A11 = a11;
            A5 = a5;
            A1 = a1;
        }

        public RecapPlanningInputs Inputs { get; }
        public RecapPlanningLimits Limits { get; }
        public RecapSchedulingFacts Scheduling { get; }
        public RecapBlockSourceIntent AvailableSource { get; }
        public RecapBlockId ClientId { get; }
        public RecapBlockId SelfId { get; }
        public EventAddress Admission { get; }
        public EventAddress SourceSet { get; }
        public EventAddress A11 { get; }
        public EventAddress A5 { get; }
        public EventAddress A1 { get; }

        public static TestModel Create(
            long minimumRecentHistoryLoad = 0,
            long recapBuildIntervalHistoryLoad = 1,
            int maxRawGrowthEventCount = 10,
            int maxRouteEndpoints = 3,
            int maxMaintainerCalls = 3,
            int maxRawEventsPerStep = 10,
            int maxRawEventsPerBuild = 30,
            bool twoBlocks = false
        ) {
            EventAddress admission = Address(6);
            EventAddress sourceSet = Address(5);
            EventAddress a11 = Address(4);
            EventAddress a5 = Address(3);
            EventAddress a1 = Address(2);
            EventAddress root = Address(1);
            EventAddress[] addresses = [
                admission,
                sourceSet,
                a11,
                a5,
                a1,
                root
            ];
            SessionCurrentLineageHeader[] lineage = [
                .. addresses.Select((address, index) =>
                    new SessionCurrentLineageHeader(
                        address,
                        index + 1 < addresses.Length
                            ? addresses[index + 1]
                            : null,
                        SessionEventKind.ObservationAccepted
                    ))
            ];

            var clientId = new RecapBlockId("roleplay.client");
            var selfId = new RecapBlockId("roleplay.self");
            var clientTarget = new ContextHeaderBlockPath(
                ContextHeaderCarrier.System,
                "roleplay.client"
            );
            var selfTarget = new ContextHeaderBlockPath(
                ContextHeaderCarrier.System,
                "roleplay.self"
            );
            RecapBlockCatalogEntry[] catalog = twoBlocks
                ? [
                    new(
                        clientId,
                        clientTarget,
                        "roleplay.client-maintainer",
                        RecapPlannerTestIdentity.CapabilityFingerprint,
                        1024
                    ),
                    new(
                        selfId,
                        selfTarget,
                        "roleplay.self-maintainer",
                        RecapPlannerTestIdentity.CapabilityFingerprint,
                        1024
                    )
                ]
                : [
                    new(
                        clientId,
                        clientTarget,
                        "roleplay.client-maintainer",
                        RecapPlannerTestIdentity.CapabilityFingerprint,
                        1024
                    )
                ];
            var policy = new StubPolicy(
                new RecapPlanningPolicyDecision.NoBuild("unused")
            );
            var estimator = new TestHistoryUnitLoadEstimator();
            var inputs = new RecapPlanningInputs(
                catalog,
                new RecapCadenceConfig(
                    estimator.Id,
                    new HistoryLoadUnit(minimumRecentHistoryLoad),
                    new HistoryLoadUnit(
                        recapBuildIntervalHistoryLoad
                    )
                ),
                estimator,
                policy
            );
            var limits = new RecapPlanningLimits(
                maxRawGrowthEventCount,
                maxRouteEndpoints,
                maxMaintainerCalls,
                maxRawEventsPerStep,
                maxRawEventsPerBuild
            );
            var window = new RecapHistoryWindowFacts(
                root,
                totalHistoryUnitCount: 5,
                [
                    new(a1, 1),
                    new(a5, 2),
                    new(a11, 3),
                    new(sourceSet, 4),
                    new(admission, 5)
                ]
            );
            var scheduling = new RecapSchedulingFacts(
                admission,
                lineage,
                window,
                sourceSet,
                sourceSet,
                TestHistoryLoadMeasurement.UnitCountEquivalent(
                    window,
                    sourceSet,
                    estimator.Id
                )
            );
            var availableSource = new RecapBlockSourceIntent(
                clientId,
                new RecapSourceIntent(sourceSet, Envelope),
                a1
            );
            return new TestModel(
                inputs,
                limits,
                scheduling,
                availableSource,
                clientId,
                selfId,
                admission,
                sourceSet,
                a11,
                a5,
                a1
            );
        }

        public RecapPlanningInputs NewInputs(
            IReadOnlyList<RecapBlockCatalogEntry> catalog
        ) => new(
            catalog,
            Inputs.Cadence,
            Inputs.HistoryUnitLoadEstimator,
            Inputs.Policy
        );

        public RecapSchedulingResult.Ready Schedule(
            IRecapPlanningPolicy? policy = null,
            RecapSchedulingFacts? scheduling = null
        )
            => Assert.IsType<RecapSchedulingResult.Ready>(
                RecapPlanEvaluator.EvaluateSchedule(
                    policy is null
                        ? Inputs
                        : new RecapPlanningInputs(
                            Inputs.OrderedCatalog,
                            Inputs.Cadence,
                            Inputs.HistoryUnitLoadEstimator,
                            policy
                        ),
                    Limits,
                    scheduling ?? Scheduling
                )
            );

        public RecapPlanningPolicyDecision.Build MaintainIntent(
            IReadOnlyList<EventAddress> route,
            RecapPriorContext? prior = null
        ) => new(
            Admission,
            [
                new RecapBlockPlanningDecision.Maintain(
                    ClientId,
                    new RecapPlanningMaintainSource.Existing(
                        AvailableSource.Source
                    ),
                    route,
                    prior ?? EmptyRecapPriorContext.Instance
                )
            ]
        );

        public RecapPlanningPolicyDecision.Build ValidMaintainIntent(
            RecapPriorContext? prior = null
        ) => MaintainIntent([A5, A11, Admission], prior);

        public RecapPlanIntentResult EvaluateIntent(
            RecapPlanningPolicyDecision decision
        ) => RecapPlanEvaluator.EvaluateIntent(
            Schedule(new StubPolicy(decision)),
            PolicyFacts()
        );

        public RecapPolicyFacts PolicyFacts() => new(
            emptyReplayStartExclusive: null,
            [
                .. Inputs.OrderedCatalog.Select(entry =>
                    new RecapBlockSourceIntent(
                        entry.RecapBlockId,
                        AvailableSource.Source,
                        AvailableSource.AbsorbedThrough
                    ))
            ]
        );

        public RecapPlanIntentResult.IntentReady IntentReady(
            RecapPlanningPolicyDecision decision
        ) => Assert.IsType<RecapPlanIntentResult.IntentReady>(
            EvaluateIntent(decision)
        );

        public RecapPlanPreflightFacts Preflight(
            IReadOnlyList<int>? costs = null
        ) {
            IReadOnlyList<int> counts = costs ?? [1, 1, 1];
            EventAddress[] starts = [A1, A5, A11];
            EventAddress[] ends = [A5, A11, Admission];
            return new RecapPlanPreflightFacts(
                [
                    new RecapSourceReplayFact(
                        ClientId,
                        AvailableSource.Source,
                        AvailableSource.AbsorbedThrough
                    )
                ],
                [
                    .. counts.Select((count, index) =>
                        new RecapPlannedStepCost(
                            ClientId,
                            starts[index],
                            ends[index],
                            count
                        ))
                ]
            );
        }

        private static EventAddress Address(ulong value)
            => new(
                SizedPtr.FromPacked(value),
                1,
                AddressHint.None
            );
    }
}
