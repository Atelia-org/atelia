using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

internal sealed record RecapFrozenPlanBarrierResult(
    IReadOnlyList<RecapFrozenPlanBarrierDefect> Defects,
    SessionCurrentLineageBeyondPrefix? BeyondPrefix = null,
    IReadOnlyDictionary<
        (RecapBlockId BlockId, int EndpointIndex),
        RecapPendingWindowProofAuthority
    >? ProvenPendingWindows = null
) {
    public IReadOnlyDictionary<
        (RecapBlockId BlockId, int EndpointIndex),
        RecapPendingWindowProofAuthority
    > PendingWindowProofs => ProvenPendingWindows
        ?? new Dictionary<
            (RecapBlockId BlockId, int EndpointIndex),
            RecapPendingWindowProofAuthority
        >();
}

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
/// Metadata, one bounded lineage prefix, and authenticated setup payload barrier shared by frozen
/// Resume and Restore. Every raw anchor and route is proved before setup payloads are read; every
/// setup payload is authenticated before any Building or Published component payload may be read.
/// </summary>
internal static class RecapFrozenPlanBarrier {
    internal static int ProofPrefixHeaderCount(
        RecapProtocolHardCaps hardCaps
    ) {
        ArgumentNullException.ThrowIfNull(hardCaps);
        // The frozen route itself may consume the full per-Build raw budget;
        // retain one Store-bounded direct-setup proof horizon at its start.
        return checked(
            hardCaps.MaxRawEventsPerBuild
            + DerivedRecapLineageView.MaxPrefixHeaderCount
        );
    }

    public static async ValueTask<RecapFrozenPlanBarrierResult> ProveAsync(
        SessionJournalReadView engine,
        DerivedRecapStore store,
        DerivedRecapSetManifest manifest,
        SessionCurrentLineagePrefix prefix,
        EventAddress expectedRawHead,
        RecapProtocolHardCaps hardCaps,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(hardCaps);

        var defects = new List<RecapFrozenPlanBarrierDefect>();
        var sourceAuthorities = new Dictionary<
            RecapBlockId,
            ResolvedSourceAuthority
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
            sourceAuthorities.Add(
                plan.RecapBlockId,
                resolved.Authority!
            );
        }
        if (defects.Count != 0) {
            return new RecapFrozenPlanBarrierResult(defects);
        }

        RequireExpectedRawHead(engine, expectedRawHead);
        if (prefix.CapturedHead != expectedRawHead) {
            throw new ArgumentException(
                "Frozen barrier prefix does not match the expected raw head.",
                nameof(prefix)
            );
        }

        var membership = new List<FrozenAnchorRequirement> {
            new(
                manifest.SetAdmissionAnchor,
                "target set admission"
            )
        };
        var directBoundaries = new List<RecapReplayBoundary>();
        var sourceAdmissions = new List<FrozenAnchorRequirement>();
        var sourceCommitments = new List<FrozenAnchorRequirement>();
        var inlinePriors = new List<FrozenAnchorRequirement>();
        var emptyStarts = new List<FrozenAnchorRequirement>();
        var endpoints = new List<FrozenAnchorRequirement>();
        var routes = new List<PendingMaintainRoute>();
        if (manifest.PriorContext is InlineRecapPriorContext inlinePrior) {
            inlinePriors.Add(new FrozenAnchorRequirement(
                inlinePrior.AdmissionAnchor,
                "manifest inline prior"
            ));
        }
        foreach (RecapBlockPlan plan in manifest.Blocks) {
            if (sourceAuthorities.TryGetValue(
                    plan.RecapBlockId,
                    out ResolvedSourceAuthority? sourceAuthority
                )) {
                sourceAdmissions.Add(new FrozenAnchorRequirement(
                    sourceAuthority.SourceSetBoundary.Address,
                    $"block '{plan.RecapBlockId}' source set admission"
                ));
                sourceCommitments.Add(new FrozenAnchorRequirement(
                    sourceAuthority.CommitmentBoundary.Address,
                    $"block '{plan.RecapBlockId}' source commitment"
                ));
                directBoundaries.Add(
                    sourceAuthority.SourceSetBoundary
                );
                directBoundaries.Add(
                    sourceAuthority.CommitmentBoundary
                );
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
                    sourceAuthorities[maintain.RecapBlockId]
                        .CommitmentBoundary,
                _ => throw new InvalidDataException(
                    "Unsupported frozen Maintain source."
                )
            };
            if (maintain.Source is EmptyRecapMaintainSource) {
                emptyStarts.Add(new FrozenAnchorRequirement(
                    start.Address,
                    $"block '{plan.RecapBlockId}' empty replay start"
                ));
                directBoundaries.Add(start);
            }
            foreach (RecapReplayBoundary endpoint
                     in maintain.CatchUpBoundaries) {
                endpoints.Add(new FrozenAnchorRequirement(
                    endpoint.Address,
                    $"block '{plan.RecapBlockId}' catch-up endpoint"
                ));
            }
            routes.Add(new PendingMaintainRoute(
                maintain,
                start.Address,
                NextEndpointIndex: 0
            ));
        }
        directBoundaries.Add(new RecapReplayBoundary(
            manifest.SetAdmissionAnchor,
            manifest.SetAdmissionAnchorSetups
        ));
        membership.AddRange(sourceAdmissions);
        membership.AddRange(sourceCommitments);
        membership.AddRange(inlinePriors);
        membership.AddRange(emptyStarts);
        membership.AddRange(endpoints);

        var lineageIndexes = new Dictionary<EventAddress, int>();
        foreach (FrozenAnchorRequirement required in membership
                     .DistinctBy(static item => item.Address)) {
            switch (prefix.Lookup(required.Address)) {
                case SessionCurrentLineageAnchorLookup.Found found:
                    lineageIndexes.Add(required.Address, found.Index);
                    break;
                case SessionCurrentLineageAnchorLookup.BeyondPrefix beyond:
                    return new RecapFrozenPlanBarrierResult(
                        defects,
                        beyond.Evidence
                    );
                case SessionCurrentLineageAnchorLookup.OffLineage:
                    return FrozenAuthority(
                        $"Frozen {required.Purpose} anchor "
                        + $"'{required.Address}' is off the captured "
                        + "raw lineage."
                    );
            }
        }
        RecapFrozenPlanBarrierResult? orderDefect =
            ValidateFrozenOrder(
                manifest,
                sourceAuthorities,
                lineageIndexes
            );
        if (orderDefect is not null) {
            return orderDefect;
        }

        var setupProofs = new List<SessionGoverningSetupProof>();
        IReadOnlyDictionary<
            (RecapBlockId BlockId, int EndpointIndex),
            RecapPendingWindowProofAuthority
        > provenPendingWindows = new Dictionary<
            (RecapBlockId BlockId, int EndpointIndex),
            RecapPendingWindowProofAuthority
        >();
        try {
            var directSetupProofs = new Dictionary<
                (EventAddress Address,
                    SessionContextAnchorSetupReferences Setups),
                SessionGoverningSetupProof
            >();
            foreach (RecapReplayBoundary boundary in directBoundaries
                         .DistinctBy(static item => (
                             item.Address,
                             item.Setups
                         ))) {
                SessionGoverningSetupProofResult result =
                    engine.ProveGoverningSetupInPrefix(
                        prefix,
                        boundary.Address,
                        boundary.Setups
                    );
                if (result is SessionGoverningSetupProofResult
                        .BeyondPrefix beyond) {
                    return new RecapFrozenPlanBarrierResult(
                        defects,
                        ToLineageEvidence(beyond)
                    );
                }
                SessionGoverningSetupProof proof =
                    ((SessionGoverningSetupProofResult.Available)
                        result).Proof;
                directSetupProofs.Add(
                    (boundary.Address, boundary.Setups),
                    proof
                );
                setupProofs.Add(proof);
            }

            PreparedRecapPendingWindows routeProof =
                RecapPendingWindowPreparer.Prove(
                    engine,
                    prefix,
                    directSetupProofs,
                    expectedRawHead,
                    routes,
                    hardCaps,
                    cancellationToken
                );
            if (routeProof.Defects.Count != 0
                || routeProof.BeyondPrefix is not null) {
                return new RecapFrozenPlanBarrierResult(
                    [
                        .. defects,
                        .. routeProof.Defects.Select(defect =>
                            new RecapFrozenPlanBarrierDefect(
                                RecapFrozenPlanBarrierDefectKind
                                    .ExecutionLimit,
                                defect.Detail
                            ))
                    ],
                    routeProof.BeyondPrefix
                );
            }
            setupProofs.AddRange(routeProof.SetupProofs);
            provenPendingWindows = routeProof.ProofAuthorities;

            RequireExpectedRawHead(engine, expectedRawHead);
            engine.ValidateGoverningSetupPayloads(
                setupProofs,
                cancellationToken
            );
            RequireExpectedRawHead(engine, expectedRawHead);
        }
        catch (InvalidDataException exception) {
            return FrozenAuthority(exception.Message);
        }
        return new RecapFrozenPlanBarrierResult(
            [],
            ProvenPendingWindows: provenPendingWindows
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
            new ResolvedSourceAuthority(
                new RecapReplayBoundary(
                    source.FrozenPlan.SetAdmissionAnchor,
                    source.FrozenPlan.SetAdmissionAnchorSetups
                ),
                new RecapReplayBoundary(
                    commitment.AbsorbedThrough,
                    sourceSetups
                )
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

    private static void RequireExpectedRawHead(
        SessionJournalReadView engine,
        EventAddress expectedRawHead
    ) {
        EventAddress observed = engine.ReadCurrentHead()
            ?? throw new InvalidDataException(
                "Frozen plan proof requires a non-empty SessionJournal."
            );
        if (observed != expectedRawHead) {
            throw new RecapRawHeadChangedException(
                expectedRawHead,
                observed
            );
        }
    }

    private static RecapFrozenPlanBarrierResult FrozenAuthority(
        string detail
    ) => new([
        new RecapFrozenPlanBarrierDefect(
            RecapFrozenPlanBarrierDefectKind.FrozenAuthority,
            detail
        )
    ]);

    private static RecapFrozenPlanBarrierResult?
        ValidateFrozenOrder(
        DerivedRecapSetManifest manifest,
        IReadOnlyDictionary<RecapBlockId, ResolvedSourceAuthority>
            sourceAuthorities,
        IReadOnlyDictionary<EventAddress, int> lineageIndexes
    ) {
        int admissionIndex =
            lineageIndexes[manifest.SetAdmissionAnchor];
        foreach (RecapBlockPlan plan in manifest.Blocks) {
            if (sourceAuthorities.TryGetValue(
                    plan.RecapBlockId,
                    out ResolvedSourceAuthority? source
                )) {
                int sourceSetIndex = lineageIndexes[
                    source.SourceSetBoundary.Address
                ];
                int commitmentIndex = lineageIndexes[
                    source.CommitmentBoundary.Address
                ];
                if (sourceSetIndex <= admissionIndex) {
                    return FrozenAuthority(
                        $"Block '{plan.RecapBlockId}' source set "
                        + "admission is not a strict ancestor of its "
                        + "target admission."
                    );
                }
                if (commitmentIndex < sourceSetIndex) {
                    return FrozenAuthority(
                        $"Block '{plan.RecapBlockId}' source "
                        + "commitment is newer than its source set "
                        + "admission."
                    );
                }
            }
            if (plan is not MaintainRecapBlockPlan maintain) {
                continue;
            }
            EventAddress start = maintain.Source switch {
                EmptyRecapMaintainSource empty =>
                    empty.ReplayStartExclusive,
                ExistingRecapMaintainSource =>
                    sourceAuthorities[plan.RecapBlockId]
                        .CommitmentBoundary.Address,
                _ => throw new InvalidDataException(
                    "Unsupported frozen Maintain source."
                )
            };
            int startIndex = lineageIndexes[start];
            if (startIndex < admissionIndex) {
                return FrozenAuthority(
                    $"Block '{plan.RecapBlockId}' replay start is "
                    + "newer than its target admission."
                );
            }
            if (manifest.PriorContext
                    is InlineRecapPriorContext inline
                && lineageIndexes[inline.AdmissionAnchor]
                    < startIndex) {
                return FrozenAuthority(
                    $"Block '{plan.RecapBlockId}' inline prior is "
                    + "newer than its exact replay start."
                );
            }
            int previousIndex = startIndex;
            foreach (RecapReplayBoundary endpoint
                     in maintain.CatchUpBoundaries) {
                int endpointIndex = lineageIndexes[endpoint.Address];
                if (endpointIndex >= previousIndex
                    || endpointIndex < admissionIndex) {
                    return FrozenAuthority(
                        $"Block '{plan.RecapBlockId}' catch-up route "
                        + "is not strictly increasing within its "
                        + "target admission bound."
                    );
                }
                previousIndex = endpointIndex;
            }
        }
        return null;
    }

    private sealed record FrozenAnchorRequirement(
        EventAddress Address,
        string Purpose
    );

    private sealed record ResolvedSourceAuthority(
        RecapReplayBoundary SourceSetBoundary,
        RecapReplayBoundary CommitmentBoundary
    );

    private sealed record SourceBoundaryResolution(
        ResolvedSourceAuthority? Authority,
        RecapFrozenPlanBarrierDefect? Defect
    );
}
