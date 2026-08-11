using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Abstractions;
using Atelia.SessionJournal.DerivedRecap.Store;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Planner.Tests;

public sealed class DerivedRecapOnlineLifecycleCoordinatorTests {
    [Fact]
    public async Task NoBuildAuthorizesRawHistoryWithoutResolvingMaintainers() {
        string path = Path.Combine(
            Path.GetTempPath(),
            "atelia-recap-lifecycle-v8",
            Guid.NewGuid().ToString("N")
        );
        try {
            using SessionJournalEngine engine = SessionJournalEngine.Create(
                path,
                new SessionCreateOptions("model", "system", "surface")
            );
            engine.AppendObservation("short history");
            _ = engine.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("answer")]),
                new CompletionDescriptor("import", "v1", "model")
            );
            DerivedRecapEpochStore store = DerivedRecapEpochStore.Open(
                path,
                engine.BranchRefId,
                new DerivedRecapEpochStoreLimits(maxRecapBlockCount: 1)
            );
            await store.CreateAsync();
            RecapBlockCatalogEntry[] roster = [new(
                new RecapBlockId("self"),
                new ContextHeaderBlockPath(
                    ContextHeaderCarrier.System,
                    "self"
                ),
                "self",
                RecapPlannerTestIdentity.CapabilityFingerprint,
                1024
            )];
            var configuration = new RecapEpochPlanningConfiguration(
                roster,
                new RecapCadenceConfig(
                    TestHistoryUnitLoadEstimator.DefaultId,
                    new HistoryLoadUnit(100),
                    new HistoryLoadUnit(100)
                ),
                new TestHistoryUnitLoadEstimator()
            );
            var limits = new RecapEpochOperationLimits(
                64,
                64,
                1,
                1,
                1,
                1
            );
            int registryLoads = 0;
            var maintainers = new DeferredRecapBlockMaintainerRegistry(() => {
                registryLoads++;
                throw new InvalidOperationException(
                    "NoBuild must not resolve Maintainers."
                );
            });
            var lifecycle = new DerivedRecapOnlineLifecycleCoordinator(
                engine.ReadView,
                store,
                configuration,
                limits,
                maintainers
            );
            EventAddress head = engine.ReadCurrentLineageHeaders()
                .CapturedHead;
            SessionContextLifecycleResult result =
                await lifecycle.PrepareAsync(
                    engine.ReadView,
                    new SessionContextLifecycleRequest(
                        new SessionContextSelectionRequest(head, 0),
                        SessionExecutionPhase.Idle,
                        SessionContextLifecycleTrigger.PreObservation,
                        "pending"
                    ),
                    CancellationToken.None
                );
            Assert.Equal(
                SessionContextLifecycleStatus.RawHistoryAuthorized,
                result.Status
            );
            Assert.Equal(0, registryLoads);
            SessionContextCandidateSelection selection =
                await lifecycle.SelectAsync(
                    new SessionContextSelectionRequest(head, 0),
                    CancellationToken.None
                );
            Assert.Equal(
                SessionContextCandidateSelectionStatus.EmptyLineage,
                selection.Status
            );
            _ = engine.AppendObservation("stale-boundary");
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await lifecycle.PrepareAsync(
                    engine.ReadView,
                    new SessionContextLifecycleRequest(
                        new SessionContextSelectionRequest(head, 0),
                        SessionExecutionPhase.Idle,
                        SessionContextLifecycleTrigger.PreObservation,
                        "pending"
                    ),
                    CancellationToken.None
                )
            );
        }
        finally {
            try {
                if (Directory.Exists(path)) {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch {
            }
        }
    }

    [Fact]
    public void TypedCampaignOutcomesMapToLifecycleBackpressureAndUnavailable() {
        var descriptor = new PublishedRecapEpochDescriptor(
            RefId.ParseHex(
                "00000000000000000000000000000001"
            ).Value,
            EventAddressTextCodec.Parse(
                "ej1:00000000000000010000000100000000"
            ),
            new string('a', 64)
        );
        SessionContextLifecycleResult pending =
            DerivedRecapOnlineLifecycleCoordinator.MapOperationResult(
                new DerivedRecapEpochOperationResult.MoreWorkPending(
                    descriptor,
                    1,
                    2
                )
            );
        Assert.Equal(
            SessionContextLifecycleStatus.Backpressure,
            pending.Status
        );

        SessionContextLifecycleResult rebuild =
            DerivedRecapOnlineLifecycleCoordinator.MapOperationResult(
                new DerivedRecapEpochOperationResult.FullRebuildRequired(
                    RecapEpochFullRebuildReason.RawGrowthLimitExceeded,
                    descriptor.AdmissionAnchor,
                    "raw cap"
                )
            );
        Assert.Equal(
            SessionContextLifecycleStatus.Unavailable,
            rebuild.Status
        );
        Assert.Contains("FullRebuildRequired", rebuild.Detail);

        SessionContextLifecycleResult failed =
            DerivedRecapOnlineLifecycleCoordinator.MapOperationResult(
                new DerivedRecapEpochOperationResult.BlockFailed(
                    descriptor.AdmissionAnchor,
                    new RecapBlockId("self"),
                    "MaintainerFailed",
                    "provider failed"
                )
            );
        Assert.Equal(
            SessionContextLifecycleStatus.Unavailable,
            failed.Status
        );
        Assert.Contains("MaintainerFailed", failed.Detail);
    }
}
