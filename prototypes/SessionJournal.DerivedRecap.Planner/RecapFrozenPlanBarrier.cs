using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

internal sealed record RecapFrozenPlanBarrierResult(
    IReadOnlyList<RecapFrozenPlanBarrierDefect> Defects,
    SessionCurrentLineageBeyondPrefix? BeyondPrefix = null
);

internal enum RecapFrozenPlanBarrierDefectKind {
    ExecutionLimit,
    FrozenAuthority,
    StoreUnavailable,
}

internal sealed record RecapFrozenPlanBarrierDefect(
    RecapFrozenPlanBarrierDefectKind Kind,
    string Detail
);

/// <summary>
/// Metadata-and-header-only barrier shared by frozen Resume and Restore. It resolves exact
/// Existing/Inherit source cursors from their envelope-authenticated source publications, proves
/// every frozen setup authority, and proves every possible Maintain route before any Building or
/// Published component payload may be read.
/// </summary>
internal static class RecapFrozenPlanBarrier {
    public static async ValueTask<RecapFrozenPlanBarrierResult> ProveAsync(
        SessionJournalEngine engine,
        DerivedRecapStore store,
        DerivedRecapSetManifest manifest,
        EventAddress expectedRawHead,
        RecapProtocolHardCaps hardCaps,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(hardCaps);

        var defects = new List<RecapFrozenPlanBarrierDefect>();
        var sourceBoundaries = new Dictionary<
            RecapBlockId,
            RecapReplayBoundary
        >();
        foreach (RecapBlockPlan plan in manifest.Blocks) {
            if (plan is not InheritRecapBlockPlan
                && plan is not MaintainRecapBlockPlan {
                    Source: ExistingRecapMaintainSource
                }) {
                continue;
            }
            SourceBoundaryResolution resolved =
                await ResolveSourceBoundaryAsync(
                        store,
                        plan,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            if (resolved.Defect is { } defect) {
                defects.Add(defect);
                continue;
            }
            sourceBoundaries.Add(
                plan.RecapBlockId,
                resolved.Boundary!
            );
        }
        if (defects.Count != 0) {
            return new RecapFrozenPlanBarrierResult(defects);
        }

        var setupBoundaries = new List<RecapReplayBoundary>();
        var transitionProvedAddresses = new HashSet<EventAddress>();
        var routes = new List<PendingMaintainRoute>();
        foreach (RecapBlockPlan plan in manifest.Blocks) {
            if (sourceBoundaries.TryGetValue(
                    plan.RecapBlockId,
                    out RecapReplayBoundary? sourceBoundary
                )) {
                setupBoundaries.Add(sourceBoundary);
            }
            if (plan is not MaintainRecapBlockPlan maintain) {
                continue;
            }
            RecapReplayBoundary start = maintain.Source switch {
                EmptyRecapMaintainSource empty => new(
                    empty.ReplayStartExclusive,
                    empty.ReplayStartSetups
                ),
                ExistingRecapMaintainSource =>
                    sourceBoundaries[maintain.RecapBlockId],
                _ => throw new InvalidDataException(
                    "Unsupported frozen Maintain source."
                )
            };
            setupBoundaries.Add(start);
            foreach (RecapReplayBoundary endpoint
                     in maintain.CatchUpBoundaries) {
                transitionProvedAddresses.Add(endpoint.Address);
            }
            routes.Add(new PendingMaintainRoute(
                maintain,
                start.Address,
                NextEndpointIndex: 0
            ));
        }
        if (!transitionProvedAddresses.Contains(
                manifest.SetAdmissionAnchor
            )) {
            setupBoundaries.Add(new RecapReplayBoundary(
                manifest.SetAdmissionAnchor,
                manifest.SetAdmissionAnchorSetups
            ));
        }

        const int setupProofLimit = 513;
        foreach (RecapReplayBoundary boundary in setupBoundaries
                     .DistinctBy(static item => (
                         item.Address,
                         item.Setups
                     ))) {
            SessionGoverningSetupProofResult proof =
                engine.ProveGoverningSetupAtBounded(
                    boundary.Address,
                    boundary.Setups,
                    setupProofLimit,
                    cancellationToken
                );
            if (proof is SessionGoverningSetupProofResult
                    .BeyondPrefix beyond) {
                return new RecapFrozenPlanBarrierResult(
                    defects,
                    ToLineageEvidence(beyond)
                );
            }
        }

        PreparedRecapPendingWindows routeProof =
            RecapPendingWindowPreparer.Prove(
                engine,
                expectedRawHead,
                routes,
                hardCaps,
                cancellationToken
            );
        return new RecapFrozenPlanBarrierResult(
            [
                .. defects,
                .. routeProof.Defects.Select(defect =>
                    new RecapFrozenPlanBarrierDefect(
                        RecapFrozenPlanBarrierDefectKind.ExecutionLimit,
                        defect.Detail
                    ))
            ],
            routeProof.BeyondPrefix
        );
    }

    private static async ValueTask<SourceBoundaryResolution>
        ResolveSourceBoundaryAsync(
        DerivedRecapStore store,
        RecapBlockPlan plan,
        CancellationToken cancellationToken
    ) {
        (EventAddress anchor, string envelope,
            SessionContextAnchorSetupReferences expectedSetups) =
            plan switch {
                InheritRecapBlockPlan inherit => (
                    inherit.SourceSetAnchor,
                    inherit.SourcePublicationEnvelopeSha256,
                    inherit.SourceAbsorbedThroughSetups
                ),
                MaintainRecapBlockPlan {
                    Source: ExistingRecapMaintainSource existing
                } => (
                    existing.SourceSetAnchor,
                    existing.SourcePublicationEnvelopeSha256,
                    existing.ReplayStartSetups
                ),
                _ => throw new InvalidOperationException(
                    "Frozen source resolution requires Existing or Inherit."
                )
            };
        var descriptor = new PublishedRecapDescriptor(
            store.RefId,
            anchor,
            envelope
        );
        PublishedPlanReadResult read =
            await store.ReadPublishedPlanAsync(
                    descriptor,
                    cancellationToken
                )
                .ConfigureAwait(false);
        if (read is not PublishedPlanReadResult.Available available) {
            return new SourceBoundaryResolution(
                null,
                new RecapFrozenPlanBarrierDefect(
                    read is PublishedPlanReadResult.Unavailable
                        ? RecapFrozenPlanBarrierDefectKind.StoreUnavailable
                        : RecapFrozenPlanBarrierDefectKind.FrozenAuthority,
                    $"Frozen source publication '{descriptor}' is "
                    + $"unavailable during metadata proof ({read.GetType().Name})."
                )
            );
        }
        PublishedPlanSnapshot source = available.Snapshot;
        RecapBlockCommitment? commitment = source.BlockCommitments
            .SingleOrDefault(candidate =>
                candidate.RecapBlockId == plan.RecapBlockId);
        if (commitment is null
            || commitment.Target != plan.Target) {
            return new SourceBoundaryResolution(
                null,
                new RecapFrozenPlanBarrierDefect(
                    RecapFrozenPlanBarrierDefectKind.FrozenAuthority,
                    $"Frozen source publication has no exact commitment "
                    + $"for block '{plan.RecapBlockId}'."
                )
            );
        }
        SessionContextAnchorSetupReferences sourceSetups;
        try {
            sourceSetups = FindSourceCommitmentSetups(
                source.FrozenPlan,
                plan.RecapBlockId,
                commitment.AbsorbedThrough
            );
        }
        catch (InvalidDataException exception) {
            return new SourceBoundaryResolution(
                null,
                new RecapFrozenPlanBarrierDefect(
                    RecapFrozenPlanBarrierDefectKind.FrozenAuthority,
                    exception.Message
                )
            );
        }
        if (sourceSetups != expectedSetups) {
            return new SourceBoundaryResolution(
                null,
                new RecapFrozenPlanBarrierDefect(
                    RecapFrozenPlanBarrierDefectKind.FrozenAuthority,
                    $"Frozen source setup authority for block "
                    + $"'{plan.RecapBlockId}' does not match its exact "
                    + "source publication commitment."
                )
            );
        }
        return new SourceBoundaryResolution(
            new RecapReplayBoundary(
                commitment.AbsorbedThrough,
                sourceSetups
            ),
            null
        );
    }

    private static SessionContextAnchorSetupReferences
        FindSourceCommitmentSetups(
        DerivedRecapSetManifest sourceManifest,
        RecapBlockId blockId,
        EventAddress absorbedThrough
    ) {
        var candidates = new List<
            SessionContextAnchorSetupReferences
        >();
        RecapBlockPlan sourcePlan = sourceManifest.Blocks.Single(
            candidate => candidate.RecapBlockId == blockId
        );
        switch (sourcePlan) {
            case InheritRecapBlockPlan inherit:
                candidates.Add(inherit.SourceAbsorbedThroughSetups);
                break;
            case MaintainRecapBlockPlan:
                // Store final-candidate validation requires every
                // Maintain publication commitment to end at admission.
                // Existing replay starts are intentionally absent from
                // this manifest shape and must never be inferred from
                // SourceSetAnchor.
                if (absorbedThrough
                    != sourceManifest.SetAdmissionAnchor) {
                    throw new InvalidDataException(
                        $"Source Maintain block '{blockId}' commitment "
                        + "does not absorb through its exact source "
                        + "admission anchor."
                    );
                }
                candidates.Add(
                    sourceManifest.SetAdmissionAnchorSetups
                );
                break;
        }
        SessionContextAnchorSetupReferences[] distinct = [
            .. candidates.Distinct()
        ];
        if (distinct.Length != 1) {
            throw new InvalidDataException(
                $"Source block '{blockId}' commitment cursor "
                + $"'{absorbedThrough}' has no unique frozen setup "
                + "authority in its exact source plan."
            );
        }
        return distinct[0];
    }

    private static SessionCurrentLineageBeyondPrefix ToLineageEvidence(
        SessionGoverningSetupProofResult.BeyondPrefix beyond
    ) => beyond.Evidence.ContinuationEvidence;

    private sealed record SourceBoundaryResolution(
        RecapReplayBoundary? Boundary,
        RecapFrozenPlanBarrierDefect? Defect
    );
}
