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
            _ => {
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
    public async Task EmptyBootstrapRemainsReadyWithoutRestore() {
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

        Assert.Equal(SessionContextLifecycleStatus.Ready, result.Status);
        Assert.Equal(["S0", "Run", "S0"], script.Trace);
    }

    [Fact]
    public async Task BuildLimitOnlyMapsToBackpressure() {
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
            _ => {
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
            CancellationToken _
        ) {
            Trace.Add("Run");
            return ValueTask.FromResult(_runs.Dequeue());
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
