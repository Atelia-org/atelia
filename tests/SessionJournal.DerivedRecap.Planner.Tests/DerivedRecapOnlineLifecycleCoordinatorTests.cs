using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Planner.Tests;

public sealed class DerivedRecapOnlineLifecycleCoordinatorTests {
    [Fact]
    public async Task RepairsLatestThenConfiguredExactAnchorOnce() {
        using LifecycleFixture fixture =
            LifecycleFixture.Create(nthPrevious: 1, historyPairs: 2);
        EventAddress latest = fixture.Lineage.HeadToRoot[1].Address;
        EventAddress middle = fixture.Lineage.HeadToRoot[3].Address;
        var script = new LifecycleScript(
            [
                Invalid(latest),
                Selected(fixture, latest),
                Invalid(middle),
                Selected(fixture, middle)
            ],
            [
                Restored(fixture, latest),
                Restored(fixture, middle)
            ],
            [new DerivedRecapExecutionResult.NoBuild("not-needed")]
        );

        SessionContextLifecycleResult result =
            await fixture.Coordinator(script).PrepareAsync(
                fixture.Engine,
                fixture.Request(),
                CancellationToken.None
            );

        Assert.Equal(SessionContextLifecycleStatus.Ready, result.Status);
        Assert.Equal(
            [
                "S0",
                $"R:{latest}",
                "S0",
                "Run",
                "S1",
                $"R:{middle}",
                "S1"
            ],
            script.Trace
        );
        DerivedRecapPlanningBaseline baseline =
            Assert.Single(script.Baselines);
        Assert.Equal(fixture.Boundary, baseline.CapturedRawHead);
        Assert.Equal(latest, baseline.ExpectedLatestAnchor);
        Assert.Null(baseline.ExpectedLatestPublished);
    }

    [Fact]
    public async Task SecondInvalidConfiguredSelectionStops() {
        using LifecycleFixture fixture =
            LifecycleFixture.Create(nthPrevious: 2, historyPairs: 2);
        EventAddress latest = fixture.Lineage.HeadToRoot[1].Address;
        EventAddress middle = fixture.Lineage.HeadToRoot[3].Address;
        var script = new LifecycleScript(
            [
                Selected(fixture, latest),
                Invalid(middle),
                Invalid(middle)
            ],
            [Restored(fixture, middle)],
            [new DerivedRecapExecutionResult.NoBuild("not-needed")]
        );

        SessionContextLifecycleResult result =
            await fixture.Coordinator(script).PrepareAsync(
                fixture.Engine,
                fixture.Request(),
                CancellationToken.None
            );

        Assert.Equal(
            SessionContextLifecycleStatus.Unavailable,
            result.Status
        );
        Assert.Equal(
            [
                "S0",
                "Run",
                "S2",
                $"R:{middle}",
                "S2"
            ],
            script.Trace
        );
    }

    [Fact]
    public async Task ConfiguredOrdinalIsSelectedAfterNewTipRun() {
        using LifecycleFixture fixture =
            LifecycleFixture.Create(nthPrevious: 1, historyPairs: 1);
        EventAddress oldLatest =
            fixture.Lineage.HeadToRoot[1].Address;
        EventAddress newTip = fixture.Lineage.CapturedHead;
        bool published = false;
        var trace = new List<string>();
        int selectionCount = 0;
        var coordinator = fixture.Coordinator(
            (
                _,
                ordinal,
                _
            ) => {
                trace.Add($"S{ordinal}");
                selectionCount++;
                if (selectionCount == 1) {
                    Assert.False(published);
                    return ValueTask.FromResult<DerivedRecapSelection>(
                        Selected(fixture, oldLatest)
                    );
                }
                Assert.True(published);
                Assert.Equal(1, ordinal);
                return ValueTask.FromResult<DerivedRecapSelection>(
                    Selected(fixture, oldLatest)
                );
            },
            (_, _, _) => throw new Xunit.Sdk.XunitException(
                "Restore must not run."
            ),
            (_, _) => {
                trace.Add("Run");
                published = true;
                return ValueTask.FromResult<DerivedRecapExecutionResult>(
                    new DerivedRecapExecutionResult.Published(
                        Descriptor(fixture, newTip)
                    )
                );
            }
        );

        SessionContextLifecycleResult result =
            await coordinator.PrepareAsync(
                fixture.Engine,
                fixture.Request(),
                CancellationToken.None
            );

        Assert.Equal(SessionContextLifecycleStatus.Ready, result.Status);
        Assert.Equal(["S0", "Run", "S1"], trace);
    }

    [Fact]
    public async Task PublicCoordinatorSelectsOldSetAtStrictOrdinalAfterBuild() {
        using PublicLifecycleFixture fixture =
            await PublicLifecycleFixture.CreateAsync(
                nthPrevious: 1,
                historyPairs: 1
            );
        MaintainRecapBlockPlan oldPlan =
            fixture.CreateMaintainPlan();
        PublishedRecapDescriptor oldPublished =
            await fixture.PublishAsync(oldPlan, "old recap");
        EventAddress newHead = fixture.AppendPair("growth");
        var maintainer = fixture.CreateMaintainer();
        var policy = new DelegatePolicy(context =>
            new RecapPlanningPolicyDecision.Build(
                context.Scheduling.CapturedHead,
                [
                    new RecapBlockPlanningDecision.Inherit(
                        fixture.BlockId,
                        Assert.Single(
                            context.PolicyFacts.AvailableSources
                        ).Source
                    )
                ]
            )
        );
        DerivedRecapOnlineLifecycleCoordinator coordinator =
            fixture.CreateCoordinator(
                recapBuildIntervalHistoryLoad: 1,
                policy,
                maintainer
            );
        SessionContextLifecycleRequest request = fixture.Request();

        SessionContextLifecycleResult result =
            await coordinator.PrepareAsync(
                fixture.Engine,
                request,
                CancellationToken.None
            );

        Assert.Equal(SessionContextLifecycleStatus.Ready, result.Status);
        Assert.Equal(1, policy.CallCount);
        Assert.Equal(0, maintainer.CallCount);
        SessionCurrentLineageSnapshot lineage =
            fixture.Engine.ReadCurrentLineageHeaders();
        var latest = Assert.IsType<DerivedRecapSelection.Selected>(
            await fixture.Store.SelectNthPreviousAsync(lineage, 0)
        );
        Assert.Equal(
            newHead,
            latest.Descriptor.SetAdmissionAnchor
        );
        var previous = Assert.IsType<DerivedRecapSelection.Selected>(
            await fixture.Store.SelectNthPreviousAsync(lineage, 1)
        );
        Assert.Equal(oldPublished, previous.Descriptor);
        SessionContextCandidateSelection neutral =
            await coordinator.SelectAsync(
                request.Selection,
                CancellationToken.None
            );
        Assert.Equal(
            SessionContextCandidateSelectionStatus.Selected,
            neutral.Status
        );
        Assert.Equal(
            oldPublished.SetAdmissionAnchor,
            Assert.IsType<SessionContextCandidateDescriptor>(
                neutral.Candidate
            ).SetAdmissionAnchor
        );
    }

    [Fact]
    public async Task FrozenBuildingCoordinatorHandlesExactBuildingOnce() {
        using PublicLifecycleFixture fixture =
            await PublicLifecycleFixture.CreateAsync(
                nthPrevious: 0,
                historyPairs: 1
            );
        MaintainRecapBlockPlan plan =
            fixture.CreateMaintainPlan();
        CreateBuildingResult.Created created =
            Assert.IsType<CreateBuildingResult.Created>(
            await fixture.Store.CreateBuildingAsync(
                DerivedRecapCodec.CreateManifest(
                    fixture.Engine.BranchRefId,
                    plan.CatchUpThrough[^1],
                    [plan]
                )
            )
        );
        CountingMaintainer maintainer = fixture.CreateMaintainer();
        DerivedRecapOnlineLifecycleCoordinator coordinator =
            DerivedRecapOnlineLifecycleCoordinator
                .CreateForFrozenBuilding(
                    fixture.Engine,
                    fixture.Store,
                    created.Descriptor,
                    new RecapBlockMaintainerRegistry([maintainer])
                );

        SessionContextLifecycleResult first =
            await coordinator.PrepareAsync(
                fixture.Engine,
                fixture.Request(),
                CancellationToken.None
            );
        SessionContextLifecycleResult second =
            await coordinator.PrepareAsync(
                fixture.Engine,
                fixture.Request(),
                CancellationToken.None
            );

        Assert.Equal(SessionContextLifecycleStatus.Ready, first.Status);
        Assert.Equal(SessionContextLifecycleStatus.Ready, second.Status);
        Assert.Equal(1, maintainer.CallCount);
        Assert.Null(coordinator.LastPlanningDiagnostics);
        Assert.IsType<BuildingReadResult.Missing>(
            await fixture.Store.ReadBuildingAsync(
                plan.CatchUpThrough[^1]
            )
        );
    }

    [Fact]
    public async Task PublicCoordinatorRepairsDamagedLatestOnce() {
        using PublicLifecycleFixture fixture =
            await PublicLifecycleFixture.CreateAsync(
                nthPrevious: 0,
                historyPairs: 1
            );
        MaintainRecapBlockPlan plan =
            fixture.CreateMaintainPlan();
        PublishedRecapDescriptor published =
            await fixture.PublishAsync(plan, "committed");
        await fixture.DamageFinalAndRemoveCheckpointAsync(plan);
        var maintainer = fixture.CreateMaintainer();
        var policy = new DelegatePolicy(static _ =>
            throw new Xunit.Sdk.XunitException(
                "Below-trigger execution must not invoke policy."
            )
        );
        var estimator = new TestHistoryUnitLoadEstimator(
            TestHistoryUnitLoadEstimator.DefaultId,
            static (_, _) => throw new Xunit.Sdk.XunitException(
                "Restore must not measure cadence."
            )
        );
        DerivedRecapOnlineLifecycleCoordinator coordinator =
            fixture.CreateCoordinator(
                recapBuildIntervalHistoryLoad: 100,
                policy,
                maintainer,
                estimator
            );
        SessionContextLifecycleRequest request = fixture.Request();

        SessionContextLifecycleResult result =
            await coordinator.PrepareAsync(
                fixture.Engine,
                request,
                CancellationToken.None
            );

        Assert.Equal(SessionContextLifecycleStatus.Ready, result.Status);
        Assert.Equal(0, policy.CallCount);
        Assert.Equal(0, estimator.MeasureCallCount);
        Assert.Equal(1, maintainer.CallCount);
        var repaired = Assert.IsType<DerivedRecapSelection.Selected>(
            await fixture.Store.SelectNthPreviousAsync(
                fixture.Engine.ReadCurrentLineageHeaders(),
                0
            )
        );
        Assert.Equal(
            published.SetAdmissionAnchor,
            repaired.Descriptor.SetAdmissionAnchor
        );
        SessionContextCandidateSelection neutral =
            await coordinator.SelectAsync(
                request.Selection,
                CancellationToken.None
            );
        Assert.Equal(
            SessionContextCandidateSelectionStatus.Selected,
            neutral.Status
        );
        Assert.Equal(
            published.SetAdmissionAnchor,
            Assert.IsType<SessionContextCandidateDescriptor>(
                neutral.Candidate
            ).SetAdmissionAnchor
        );
    }

    [Fact]
    public async Task ForgedOrdinalDoesNoLifecycleWork() {
        using LifecycleFixture fixture =
            LifecycleFixture.Create(nthPrevious: 1, historyPairs: 1);
        var script = new LifecycleScript([], [], []);
        DerivedRecapOnlineLifecycleCoordinator coordinator =
            fixture.Coordinator(script);
        var forged = new SessionContextLifecycleRequest(
            new SessionContextSelectionRequest(
                fixture.Boundary,
                NthPrevious: 0
            ),
            fixture.Phase
        );

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await coordinator.PrepareAsync(
                fixture.Engine,
                forged,
                CancellationToken.None
            )
        );

        Assert.Empty(script.Trace);
    }

    [Fact]
    public async Task WrongCallbackEngineDoesNoLifecycleWork() {
        using LifecycleFixture fixture =
            LifecycleFixture.Create(nthPrevious: 0, historyPairs: 1);
        using LifecycleFixture other =
            LifecycleFixture.Create(nthPrevious: 0, historyPairs: 1);
        var script = new LifecycleScript([], [], []);

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await fixture.Coordinator(script).PrepareAsync(
                other.Engine,
                other.Request(),
                CancellationToken.None
            )
        );

        Assert.Empty(script.Trace);
    }

    [Fact]
    public async Task StalePhaseDoesNoLifecycleWork() {
        using LifecycleFixture fixture =
            LifecycleFixture.Create(nthPrevious: 0, historyPairs: 1);
        var script = new LifecycleScript([], [], []);
        var stale = new SessionContextLifecycleRequest(
            new SessionContextSelectionRequest(
                fixture.Boundary,
                fixture.NthPrevious
            ),
            SessionExecutionPhase.AwaitingAgentAction
        );

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await fixture.Coordinator(script).PrepareAsync(
                fixture.Engine,
                stale,
                CancellationToken.None
            )
        );

        Assert.Empty(script.Trace);
    }

    [Fact]
    public async Task EmptyLineageNoBuildAuthorizesRawHistory() {
        using LifecycleFixture fixture =
            LifecycleFixture.Create(nthPrevious: 0, historyPairs: 0);
        var script = new LifecycleScript(
            [
                new DerivedRecapSelection.EmptyLineage(),
                new DerivedRecapSelection.EmptyLineage()
            ],
            [],
            [new DerivedRecapExecutionResult.NoBuild("below-trigger")]
        );

        SessionContextLifecycleResult result =
            await fixture.Coordinator(script).PrepareAsync(
                fixture.Engine,
                fixture.Request(),
                CancellationToken.None
            );

        Assert.Equal(
            SessionContextLifecycleStatus.RawHistoryReady,
            result.Status
        );
        Assert.Equal(["S0", "Run", "S0"], script.Trace);
    }

    [Fact]
    public async Task PublishedResultCannotAuthorizeRemainingEmptyLineage() {
        using LifecycleFixture fixture =
            LifecycleFixture.Create(nthPrevious: 0, historyPairs: 1);
        var script = new LifecycleScript(
            [
                new DerivedRecapSelection.EmptyLineage(),
                new DerivedRecapSelection.EmptyLineage()
            ],
            [],
            [
                new DerivedRecapExecutionResult.Published(
                    Descriptor(fixture, fixture.Lineage.CapturedHead)
                )
            ]
        );

        SessionContextLifecycleResult result =
            await fixture.Coordinator(script).PrepareAsync(
                fixture.Engine,
                fixture.Request(),
                CancellationToken.None
            );

        Assert.Equal(
            SessionContextLifecycleStatus.Unavailable,
            result.Status
        );
        Assert.Equal(["S0", "Run", "S0"], script.Trace);
    }

    [Fact]
    public async Task DisappearingSelectedLineageCannotUseNoBuildAuthorization() {
        using LifecycleFixture fixture =
            LifecycleFixture.Create(nthPrevious: 0, historyPairs: 1);
        EventAddress latest =
            fixture.Lineage.HeadToRoot[1].Address;
        var script = new LifecycleScript(
            [
                Selected(fixture, latest),
                new DerivedRecapSelection.EmptyLineage()
            ],
            [],
            [new DerivedRecapExecutionResult.NoBuild("below-trigger")]
        );

        SessionContextLifecycleResult result =
            await fixture.Coordinator(script).PrepareAsync(
                fixture.Engine,
                fixture.Request(),
                CancellationToken.None
            );

        Assert.Equal(
            SessionContextLifecycleStatus.Unavailable,
            result.Status
        );
        Assert.Equal(["S0", "Run", "S0"], script.Trace);
    }

    [Theory]
    [InlineData(RecapPlanDefectCodes.RawBuildLimitExceeded)]
    [InlineData(
        RecapPlanDefectCodes.MaxRawGrowthEventCountExceeded
    )]
    public async Task BuildLimitOnlyMapsToBackpressure(string code) {
        using LifecycleFixture fixture =
            LifecycleFixture.Create(nthPrevious: 0, historyPairs: 1);
        EventAddress latest = fixture.Lineage.HeadToRoot[1].Address;
        var script = new LifecycleScript(
            [Selected(fixture, latest)],
            [],
            [
                new DerivedRecapExecutionResult.Unavailable([
                    new DerivedRecapExecutionDefect(
                        code,
                        "bounded"
                    )
                ])
            ]
        );

        SessionContextLifecycleResult result =
            await fixture.Coordinator(script).PrepareAsync(
                fixture.Engine,
                fixture.Request(),
                CancellationToken.None
            );

        Assert.Equal(
            SessionContextLifecycleStatus.Backpressure,
            result.Status
        );
        Assert.Equal(["S0", "Run"], script.Trace);
    }

    [Fact]
    public async Task MixedBuildDefectsMapToUnavailable() {
        using LifecycleFixture fixture =
            LifecycleFixture.Create(nthPrevious: 0, historyPairs: 1);
        EventAddress latest = fixture.Lineage.HeadToRoot[1].Address;
        var script = new LifecycleScript(
            [Selected(fixture, latest)],
            [],
            [
                new DerivedRecapExecutionResult.Unavailable([
                    new DerivedRecapExecutionDefect(
                        RecapPlanDefectCodes.RawBuildLimitExceeded,
                        "bounded"
                    ),
                    new DerivedRecapExecutionDefect(
                        RecapPlanDefectCodes.RouteInvalid,
                        "invalid"
                    )
                ])
            ]
        );

        SessionContextLifecycleResult result =
            await fixture.Coordinator(script).PrepareAsync(
                fixture.Engine,
                fixture.Request(),
                CancellationToken.None
            );

        Assert.Equal(
            SessionContextLifecycleStatus.Unavailable,
            result.Status
        );
    }

    [Fact]
    public async Task RetryableRestoreAndExecutionLimitAreBackpressure() {
        using LifecycleFixture retryFixture =
            LifecycleFixture.Create(nthPrevious: 0, historyPairs: 1);
        EventAddress retryAnchor =
            retryFixture.Lineage.HeadToRoot[1].Address;
        var retryScript = new LifecycleScript(
            [Invalid(retryAnchor)],
            [
                new DerivedRecapRestoreResult.Retryable(
                    DerivedRecapRestoreDefectCodes
                        .ConcurrentPublishedChange,
                    "race"
                )
            ],
            []
        );

        SessionContextLifecycleResult retry =
            await retryFixture.Coordinator(retryScript).PrepareAsync(
                retryFixture.Engine,
                retryFixture.Request(),
                CancellationToken.None
            );

        Assert.Equal(
            SessionContextLifecycleStatus.Backpressure,
            retry.Status
        );

        using LifecycleFixture limitFixture =
            LifecycleFixture.Create(nthPrevious: 0, historyPairs: 1);
        EventAddress limitAnchor =
            limitFixture.Lineage.HeadToRoot[1].Address;
        var limitScript = new LifecycleScript(
            [Invalid(limitAnchor)],
            [
                new DerivedRecapRestoreResult.Unavailable([
                    new DerivedRecapRestoreDefect(
                        DerivedRecapRestoreDefectCodes
                            .ExecutionLimitExceeded,
                        "bounded"
                    )
                ])
            ],
            []
        );

        SessionContextLifecycleResult limit =
            await limitFixture.Coordinator(limitScript).PrepareAsync(
                limitFixture.Engine,
                limitFixture.Request(),
                CancellationToken.None
            );

        Assert.Equal(
            SessionContextLifecycleStatus.Backpressure,
            limit.Status
        );
    }

    [Fact]
    public async Task MixedRestoreDefectsMapToUnavailable() {
        using LifecycleFixture fixture =
            LifecycleFixture.Create(nthPrevious: 0, historyPairs: 1);
        EventAddress anchor = fixture.Lineage.HeadToRoot[1].Address;
        var script = new LifecycleScript(
            [Invalid(anchor)],
            [
                new DerivedRecapRestoreResult.Unavailable([
                    new DerivedRecapRestoreDefect(
                        DerivedRecapRestoreDefectCodes
                            .ExecutionLimitExceeded,
                        "bounded"
                    ),
                    new DerivedRecapRestoreDefect(
                        DerivedRecapRestoreDefectCodes.FrozenPlanInvalid,
                        "invalid"
                    )
                ])
            ],
            []
        );

        SessionContextLifecycleResult result =
            await fixture.Coordinator(script).PrepareAsync(
                fixture.Engine,
                fixture.Request(),
                CancellationToken.None
            );

        Assert.Equal(
            SessionContextLifecycleStatus.Unavailable,
            result.Status
        );
    }

    [Fact]
    public async Task StructuralRestoreAndBlockFailureAreUnavailable() {
        using LifecycleFixture unavailableFixture =
            LifecycleFixture.Create(nthPrevious: 0, historyPairs: 1);
        EventAddress unavailableAnchor =
            unavailableFixture.Lineage.HeadToRoot[1].Address;
        var unavailableScript = new LifecycleScript(
            [Invalid(unavailableAnchor)],
            [
                new DerivedRecapRestoreResult.Unavailable([
                    new DerivedRecapRestoreDefect(
                        DerivedRecapRestoreDefectCodes.FrozenPlanInvalid,
                        "invalid"
                    )
                ])
            ],
            []
        );

        SessionContextLifecycleResult unavailable =
            await unavailableFixture.Coordinator(unavailableScript)
                .PrepareAsync(
                    unavailableFixture.Engine,
                    unavailableFixture.Request(),
                    CancellationToken.None
                );

        Assert.Equal(
            SessionContextLifecycleStatus.Unavailable,
            unavailable.Status
        );

        using LifecycleFixture failedFixture =
            LifecycleFixture.Create(nthPrevious: 0, historyPairs: 1);
        EventAddress failedAnchor =
            failedFixture.Lineage.HeadToRoot[1].Address;
        var failedScript = new LifecycleScript(
            [Selected(failedFixture, failedAnchor)],
            [],
            [
                new DerivedRecapExecutionResult.BlockFailed(
                    failedAnchor,
                    new RecapBlockId("self"),
                    DerivedRecapExecutionDefectCodes.MaintainerFailed,
                    "failed"
                )
            ]
        );

        SessionContextLifecycleResult failed =
            await failedFixture.Coordinator(failedScript).PrepareAsync(
                failedFixture.Engine,
                failedFixture.Request(),
                CancellationToken.None
            );

        Assert.Equal(
            SessionContextLifecycleStatus.Unavailable,
            failed.Status
        );
    }

    [Fact]
    public async Task BuildRetryableAndRestoreBlockFailureMapExactly() {
        using LifecycleFixture retryFixture =
            LifecycleFixture.Create(nthPrevious: 0, historyPairs: 1);
        EventAddress retryAnchor =
            retryFixture.Lineage.HeadToRoot[1].Address;
        var retryScript = new LifecycleScript(
            [Selected(retryFixture, retryAnchor)],
            [],
            [
                new DerivedRecapExecutionResult.Retryable(
                    DerivedRecapExecutionDefectCodes.BuildingRace,
                    "race"
                )
            ]
        );

        SessionContextLifecycleResult retry =
            await retryFixture.Coordinator(retryScript).PrepareAsync(
                retryFixture.Engine,
                retryFixture.Request(),
                CancellationToken.None
            );

        Assert.Equal(
            SessionContextLifecycleStatus.Backpressure,
            retry.Status
        );

        using LifecycleFixture failedFixture =
            LifecycleFixture.Create(nthPrevious: 0, historyPairs: 1);
        EventAddress failedAnchor =
            failedFixture.Lineage.HeadToRoot[1].Address;
        var failedScript = new LifecycleScript(
            [Invalid(failedAnchor)],
            [
                new DerivedRecapRestoreResult.BlockFailed(
                    new RecapBlockId("self"),
                    DerivedRecapRestoreDefectCodes.MaintainerFailed,
                    "failed"
                )
            ],
            []
        );

        SessionContextLifecycleResult failed =
            await failedFixture.Coordinator(failedScript).PrepareAsync(
                failedFixture.Engine,
                failedFixture.Request(),
                CancellationToken.None
            );

        Assert.Equal(
            SessionContextLifecycleStatus.Unavailable,
            failed.Status
        );
    }

    [Fact]
    public async Task RawHeadDriftCannotReturnReady() {
        using LifecycleFixture fixture =
            LifecycleFixture.Create(nthPrevious: 0, historyPairs: 1);
        EventAddress latest = fixture.Lineage.HeadToRoot[1].Address;
        var coordinator = fixture.Coordinator(
            (_, _, _) => ValueTask.FromResult<DerivedRecapSelection>(
                Selected(fixture, latest)
            ),
            (_, _, _) => throw new Xunit.Sdk.XunitException(
                "Restore must not run."
            ),
            (_, _) => {
                fixture.Engine.AppendObservation("drift");
                return ValueTask.FromResult<DerivedRecapExecutionResult>(
                    new DerivedRecapExecutionResult.NoBuild("stale")
                );
            }
        );

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await coordinator.PrepareAsync(
                fixture.Engine,
                fixture.Request(),
                CancellationToken.None
            )
        );
    }

    [Fact]
    public async Task PinnedPlanningIsUsedOnceThenCurrentPlanningRuns() {
        using LifecycleFixture fixture =
            LifecycleFixture.Create(nthPrevious: 0, historyPairs: 1);
        EventAddress latest = fixture.Lineage.HeadToRoot[1].Address;
        PublishedRecapDescriptor descriptor =
            Descriptor(fixture, latest);
        var pinned = new DerivedRecapPlanningBaseline(
            fixture.Boundary,
            latest,
            descriptor
        );
        int pinnedCalls = 0;
        int currentCalls = 0;
        var coordinator =
            new DerivedRecapOnlineLifecycleCoordinator(
                fixture.Engine,
                new ThrowingCandidateSource(),
                (_, _, _) =>
                    ValueTask.FromResult<DerivedRecapSelection>(
                        new DerivedRecapSelection.Selected(descriptor)
                    ),
                (_, _, _) => throw new Xunit.Sdk.XunitException(
                    "Restore must not run."
                ),
                (baseline, _) => {
                    pinnedCalls++;
                    Assert.Same(pinned, baseline);
                    return ValueTask.FromResult<
                        DerivedRecapExecutionResult
                    >(new DerivedRecapExecutionResult.NoBuild(
                        "first"
                    ));
                },
                getLastPlanningDiagnostics: null,
                isFrozenBuildingMode: false,
                pinnedPlanningBaseline: pinned,
                runCurrentPlanning: _ => {
                    currentCalls++;
                    return ValueTask.FromResult<
                        DerivedRecapExecutionResult
                    >(new DerivedRecapExecutionResult.NoBuild(
                        "second"
                    ));
                }
            );

        _ = await coordinator.PrepareAsync(
            fixture.Engine,
            fixture.Request(),
            CancellationToken.None
        );
        fixture.Engine.AppendObservation("second lifecycle");
        EventAddress secondBoundary =
            fixture.Engine.ReadCurrentLineageHeaders().CapturedHead;
        var secondRequest = new SessionContextLifecycleRequest(
            new SessionContextSelectionRequest(
                secondBoundary,
                fixture.NthPrevious
            ),
            fixture.Engine.InspectExecutionBoundary().Phase
        );
        _ = await coordinator.PrepareAsync(
            fixture.Engine,
            secondRequest,
            CancellationToken.None
        );

        Assert.Equal(1, pinnedCalls);
        Assert.Equal(1, currentCalls);
    }

    private static DerivedRecapSelection Invalid(
        EventAddress anchor
    ) => new DerivedRecapSelection.ExactPublishedSetInvalid(
        anchor,
        [new RecapStructuralDefect("Damaged", "invalid")]
    );

    private static DerivedRecapSelection Selected(
        LifecycleFixture fixture,
        EventAddress anchor
    ) => new DerivedRecapSelection.Selected(
        Descriptor(fixture, anchor)
    );

    private static PublishedRecapDescriptor Descriptor(
        LifecycleFixture fixture,
        EventAddress anchor
    ) => new(
        fixture.Engine.BranchRefId,
        anchor,
        new string('a', 64)
    );

    private static DerivedRecapRestoreResult Restored(
        LifecycleFixture fixture,
        EventAddress anchor
    ) => new DerivedRecapRestoreResult.Restored(
        Descriptor(fixture, anchor)
    );

    private sealed class LifecycleScript {
        private readonly Queue<DerivedRecapSelection> _selections;
        private readonly Queue<DerivedRecapRestoreResult> _restores;
        private readonly Queue<DerivedRecapExecutionResult> _runs;

        public LifecycleScript(
            IEnumerable<DerivedRecapSelection> selections,
            IEnumerable<DerivedRecapRestoreResult> restores,
            IEnumerable<DerivedRecapExecutionResult> runs
        ) {
            _selections = new Queue<DerivedRecapSelection>(selections);
            _restores =
                new Queue<DerivedRecapRestoreResult>(restores);
            _runs = new Queue<DerivedRecapExecutionResult>(runs);
        }

        public List<string> Trace { get; } = [];
        public List<DerivedRecapPlanningBaseline> Baselines { get; } =
            [];

        public ValueTask<DerivedRecapSelection> SelectAsync(
            SessionCurrentLineageSnapshot lineage,
            int ordinal,
            CancellationToken cancellationToken
        ) {
            Trace.Add($"S{ordinal}");
            return ValueTask.FromResult(_selections.Dequeue());
        }

        public ValueTask<DerivedRecapRestoreResult> RestoreAsync(
            EventAddress anchor,
            EventAddress expectedRawHead,
            CancellationToken cancellationToken
        ) {
            Trace.Add($"R:{anchor}");
            return ValueTask.FromResult(_restores.Dequeue());
        }

        public ValueTask<DerivedRecapExecutionResult> RunAsync(
            DerivedRecapPlanningBaseline baseline,
            CancellationToken __
        ) {
            Trace.Add("Run");
            Baselines.Add(baseline);
            return ValueTask.FromResult(_runs.Dequeue());
        }
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

        public int CallCount { get; private set; }

        public RecapPlanningPolicyDecision Decide(
            RecapPlanningPolicyContext context
        ) {
            CallCount++;
            return _decide(context);
        }
    }

    private sealed class CountingMaintainer(
        string id,
        ContextHeaderBlockPath target
    ) : IRecapBlockMaintainer {
        public string Id { get; } = id;
        public string CapabilityFingerprint { get; } =
            "sha256:0000000000000000000000000000000000000000000000000000000000000000";
        public ContextHeaderBlockPath Target { get; } = target;
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
                    new ContextHeaderBlock("restored")
                )
            );
        }
    }

    private sealed class PublicLifecycleFixture : IDisposable {
        private const int MaxContent = 4096;
        private const string MaintainerId = "self-maintainer";

        private PublicLifecycleFixture(
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
        public RecapBlockId BlockId { get; } = new("self");
        public ContextHeaderBlockPath Target { get; } = new(
            ContextHeaderCarrier.System,
            "self"
        );

        public static async ValueTask<PublicLifecycleFixture>
            CreateAsync(
            int nthPrevious,
            int historyPairs
        ) {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "atelia-derived-recap-public-lifecycle-tests",
                Guid.NewGuid().ToString("N")
            );
            SessionJournalEngine engine = SessionJournalEngine.Create(
                path,
                new SessionCreateOptions(
                    "model-a",
                    "system-a",
                    "surface-a",
                    DerivedContextNthPrevious: nthPrevious
                )
            );
            var store = DerivedRecapStore.Open(
                path,
                engine.BranchRefId
            );
            var fixture =
                new PublicLifecycleFixture(path, engine, store);
            await store.CreateAsync();
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

        public MaintainRecapBlockPlan CreateMaintainPlan() {
            SessionHistoryPlanningWindow window =
                Engine.ReadHistoryPlanningWindow();
            EventAddress anchor = window.ReplaySafeBoundaries[^1]
                .Address;
            return new MaintainRecapBlockPlan(
                BlockId,
                Target,
                MaintainerId,
                RecapPlannerTestIdentity.CapabilityFingerprint,
                new EmptyRecapMaintainSource(
                    window.StartExclusive
                ),
                [anchor],
                EmptyRecapPriorContext.Instance,
                MaxContent
            );
        }

        public async ValueTask<PublishedRecapDescriptor>
            PublishAsync(
            MaintainRecapBlockPlan plan,
            string content
        ) {
            EventAddress anchor = plan.CatchUpThrough[^1];
            CreateBuildingResult.Created created =
                Assert.IsType<CreateBuildingResult.Created>(
                    await Store.CreateBuildingAsync(
                        DerivedRecapCodec.CreateManifest(
                            Engine.BranchRefId,
                            anchor,
                            [plan]
                        )
                    )
                );
            BuildingBlockInspection initial =
                await Store.InspectBuildingBlockAsync(
                    created.Descriptor,
                    BlockId
                );
            DerivedRecapBlock block =
                DerivedRecapCodec.CreateBlock(
                    plan,
                    anchor,
                    content
                );
            _ = Assert.IsType<CheckpointWriteResult.Updated>(
                await Store.AdvanceRollingCheckpointAsync(
                    created.Descriptor,
                    BlockId,
                    initial.Checkpoint.StateToken,
                    block
                )
            );
            BuildingBlockInspection checkpointed =
                await Store.InspectBuildingBlockAsync(
                    created.Descriptor,
                    BlockId
                );
            _ = Assert.IsType<FinalBlockWriteResult.Installed>(
                await Store.EnsureFinalBlockAsync(
                    created.Descriptor,
                    BlockId,
                    checkpointed.Final.StateToken,
                    block
                )
            );
            return await new DerivedRecapPublisher(Store, Engine)
                .PublishAsync(anchor);
        }

        public async ValueTask
            DamageFinalAndRemoveCheckpointAsync(
            MaintainRecapBlockPlan plan
        ) {
            EventAddress anchor = plan.CatchUpThrough[^1];
            string publishedPath =
                Store.GetPublishedPathForTest(anchor);
            await File.WriteAllTextAsync(
                System.IO.Path.Combine(
                    publishedPath,
                    "blocks",
                    $"{BlockId.Value}.json"
                ),
                "damaged"
            );
            File.Delete(
                System.IO.Path.Combine(
                    publishedPath,
                    "work",
                    $"{BlockId.Value}.json"
                )
            );
        }

        public CountingMaintainer CreateMaintainer()
            => new(MaintainerId, Target);

        public DerivedRecapOnlineLifecycleCoordinator
            CreateCoordinator(
            int recapBuildIntervalHistoryLoad,
            IRecapPlanningPolicy policy,
            IRecapBlockMaintainer maintainer,
            IHistoryUnitLoadEstimator? estimator = null
        ) {
            estimator ??= new TestHistoryUnitLoadEstimator();
            return new(
            Engine,
            Store,
            new RecapPlanningInputs(
                [
                    new RecapBlockCatalogEntry(
                        BlockId,
                        Target,
                        MaintainerId,
                        RecapPlannerTestIdentity.CapabilityFingerprint,
                        MaxContent
                    )
                ],
                new RecapCadenceConfig(
                    estimator.Id,
                    new HistoryLoadUnit(0),
                    new HistoryLoadUnit(
                        recapBuildIntervalHistoryLoad
                    )
                ),
                estimator,
                policy
            ),
            new RecapPlanningLimits(
                maxRawGrowthEventCount: 512,
                maxRouteEndpointsPerBlock: 4,
                maxMaintainerCallsPerBuild: 1,
                maxRawEventsPerStep: 64,
                maxRawEventsPerBuild: 512
            ),
            new RecapBlockMaintainerRegistry([maintainer])
            );
        }

        public SessionContextLifecycleRequest Request() {
            EventAddress boundary =
                Engine.ReadCurrentLineageHeaders().CapturedHead;
            return new SessionContextLifecycleRequest(
                new SessionContextSelectionRequest(
                    boundary,
                    Engine.ResolveGoverningSetup(boundary)
                        .RuntimeConfig.DerivedContext.NthPrevious
                ),
                Engine.InspectExecutionBoundary().Phase
            );
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

    private sealed class LifecycleFixture : IDisposable {
        private LifecycleFixture(
            string path,
            SessionJournalEngine engine,
            int nthPrevious
        ) {
            Path = path;
            Engine = engine;
            NthPrevious = nthPrevious;
            Lineage = engine.ReadCurrentLineageHeaders();
            Boundary = Lineage.CapturedHead;
            Phase = engine.InspectExecutionBoundary().Phase;
        }

        public string Path { get; }
        public SessionJournalEngine Engine { get; }
        public int NthPrevious { get; }
        public SessionCurrentLineageSnapshot Lineage { get; }
        public EventAddress Boundary { get; }
        public SessionExecutionPhase Phase { get; }

        public static LifecycleFixture Create(
            int nthPrevious,
            int historyPairs
        ) {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "atelia-derived-recap-lifecycle-tests",
                Guid.NewGuid().ToString("N")
            );
            SessionJournalEngine engine = SessionJournalEngine.Create(
                path,
                new SessionCreateOptions(
                    "model-a",
                    "system-a",
                    "surface-a",
                    DerivedContextNthPrevious: nthPrevious
                )
            );
            for (int index = 0; index < historyPairs; index++) {
                engine.AppendObservation($"observation {index}");
                engine.AppendImportedAgentAction(
                    new ActionMessage([
                        new ActionBlock.Text($"answer {index}")
                    ]),
                    new CompletionDescriptor(
                        "import",
                        "v1",
                        "model-a"
                    )
                );
            }
            return new LifecycleFixture(path, engine, nthPrevious);
        }

        public SessionContextLifecycleRequest Request() => new(
            new SessionContextSelectionRequest(
                Boundary,
                NthPrevious
            ),
            Phase
        );

        public DerivedRecapOnlineLifecycleCoordinator Coordinator(
            LifecycleScript script
        ) => Coordinator(
            script.SelectAsync,
            script.RestoreAsync,
            script.RunAsync
        );

        public DerivedRecapOnlineLifecycleCoordinator Coordinator(
            Func<
                SessionCurrentLineageSnapshot,
                int,
                CancellationToken,
                ValueTask<DerivedRecapSelection>
            > select,
            Func<
                EventAddress,
                EventAddress,
                CancellationToken,
                ValueTask<DerivedRecapRestoreResult>
            > restore,
            Func<
                DerivedRecapPlanningBaseline,
                CancellationToken,
                ValueTask<DerivedRecapExecutionResult>
            > run
        ) => new(
            Engine,
            new ThrowingCandidateSource(),
            select,
            restore,
            run
        );

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

    private sealed class ThrowingCandidateSource
        : ICoherentContextCandidateSource {
        public ValueTask<SessionContextCandidateSelection> SelectAsync(
            SessionContextSelectionRequest request,
            CancellationToken cancellationToken
        ) => throw new Xunit.Sdk.XunitException(
            "Candidate selection is not part of lifecycle preparation."
        );

        public ValueTask<SessionContextCandidate> MaterializeAsync(
            SessionContextCandidateDescriptor descriptor,
            CancellationToken cancellationToken
        ) => throw new Xunit.Sdk.XunitException(
            "Candidate materialization is not part of lifecycle "
            + "preparation."
        );
    }
}
