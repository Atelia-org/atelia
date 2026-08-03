using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

internal sealed record PendingMaintainRoute(
    MaintainRecapBlockPlan Plan,
    EventAddress StartExclusive,
    int NextEndpointIndex
);

internal sealed record RecapPendingWindowDefect(string Detail);

internal sealed record RecapPendingWindowProofAuthority(
    RecapBlockId BlockId,
    int EndpointIndex,
    RecapReplayBoundary Start,
    RecapReplayBoundary Endpoint,
    SessionHistoryPlanningWindowProof WindowProof,
    SessionGoverningSetupProof StartSetupProof
);

internal sealed class RecapRawHeadChangedException(
    EventAddress expected,
    EventAddress observed
) : Exception(
    "Raw head changed while freezing Building replay windows."
) {
    public EventAddress Expected { get; } = expected;
    public EventAddress Observed { get; } = observed;
}

internal sealed class PreparedRecapPendingWindows {
    public PreparedRecapPendingWindows(
        IReadOnlyList<RecapPendingWindowDefect> defects,
        IReadOnlyDictionary<
            (RecapBlockId BlockId, int EndpointIndex),
            SessionHistoryPlanningWindow
        > windows,
        SessionCurrentLineageBeyondPrefix? beyondPrefix = null,
        IReadOnlyList<SessionGoverningSetupProof>? setupProofs = null,
        IReadOnlyDictionary<
            (RecapBlockId BlockId, int EndpointIndex),
            RecapPendingWindowProofAuthority
        >? proofAuthorities = null
    ) {
        Defects = defects;
        Windows = windows;
        BeyondPrefix = beyondPrefix;
        SetupProofs = setupProofs
            ?? Array.Empty<SessionGoverningSetupProof>();
        ProofAuthorities = proofAuthorities
            ?? new Dictionary<
                (RecapBlockId BlockId, int EndpointIndex),
                RecapPendingWindowProofAuthority
            >();
    }

    public IReadOnlyList<RecapPendingWindowDefect> Defects { get; }
    public IReadOnlyDictionary<
        (RecapBlockId BlockId, int EndpointIndex),
        SessionHistoryPlanningWindow
    > Windows { get; }
    public SessionCurrentLineageBeyondPrefix? BeyondPrefix { get; }
    public IReadOnlyList<SessionGoverningSetupProof> SetupProofs {
        get;
    }
    public IReadOnlyDictionary<
        (RecapBlockId BlockId, int EndpointIndex),
        RecapPendingWindowProofAuthority
    > ProofAuthorities { get; }
}

/// <summary>
/// Freezes exact raw windows only for pending Maintain route suffixes.
/// Store inspection and component writes remain phase-specific.
/// </summary>
internal static class RecapPendingWindowPreparer {
    /// <summary>
    /// Header-only proof for every possible pending route. This deliberately returns no
    /// materialized windows; callers enter the content phase separately after all other
    /// metadata/setup barriers have succeeded.
    /// </summary>
    public static PreparedRecapPendingWindows Prove(
        SessionJournalReadView engine,
        SessionCurrentLineagePrefix prefix,
        IReadOnlyDictionary<
            (EventAddress Address,
                SessionContextAnchorSetupReferences Setups),
            SessionGoverningSetupProof
        > directSetupProofs,
        EventAddress expectedRawHead,
        IReadOnlyList<PendingMaintainRoute> pendingRoutes,
        RecapProtocolHardCaps hardCaps,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(directSetupProofs);
        ArgumentNullException.ThrowIfNull(pendingRoutes);
        ArgumentNullException.ThrowIfNull(hardCaps);
        var windows = new Dictionary<
            (RecapBlockId BlockId, int EndpointIndex),
            SessionHistoryPlanningWindow
        >();
        var setupProofs = new List<SessionGoverningSetupProof>();
        var proofAuthorities = new Dictionary<
            (RecapBlockId BlockId, int EndpointIndex),
            RecapPendingWindowProofAuthority
        >();
        if (prefix.CapturedHead != expectedRawHead) {
            throw new ArgumentException(
                "Captured lineage prefix does not match the expected raw head.",
                nameof(prefix)
            );
        }
        IReadOnlyList<RecapPendingWindowDefect> routeDefects =
            ValidatePendingRouteLimits(pendingRoutes, hardCaps);
        if (routeDefects.Count != 0) {
            return new PreparedRecapPendingWindows(
                routeDefects,
                windows,
                setupProofs: setupProofs
            );
        }
        if (pendingRoutes.Count == 0
            || pendingRoutes.All(route =>
                route.NextEndpointIndex
                    == route.Plan.CatchUpBoundaries.Count)) {
            return new PreparedRecapPendingWindows(
                [],
                windows,
                setupProofs: setupProofs
            );
        }
        long rawEvents = 0;
        foreach (PendingMaintainRoute route in pendingRoutes) {
            cancellationToken.ThrowIfCancellationRequested();
            RecapReplayBoundary previous = StartBoundary(route);
            if (!directSetupProofs.TryGetValue(
                    (previous.Address, previous.Setups),
                    out SessionGoverningSetupProof? previousSetupProof
                )) {
                throw new InvalidDataException(
                    $"Block '{route.Plan.RecapBlockId}' has no direct "
                    + "governing proof for its frozen route start."
                );
            }
            if (previous.Address != route.StartExclusive) {
                throw new InvalidDataException(
                    $"Block '{route.Plan.RecapBlockId}' pending start "
                    + "does not match its frozen replay boundary."
                );
            }
            for (int index = route.NextEndpointIndex;
                 index < route.Plan.CatchUpBoundaries.Count;
                 index++) {
                cancellationToken.ThrowIfCancellationRequested();
                RecapReplayBoundary endpoint =
                    route.Plan.CatchUpBoundaries[index];
                SessionHistoryPlanningWindowProofResult proofResult =
                    engine.ProveHistoryPlanningWindowInPrefix(
                        prefix,
                        endpoint.Address,
                        previous.Address,
                        hardCaps.MaxRawEventsPerStep
                    );
                if (proofResult is SessionHistoryPlanningWindowProofResult
                        .BeyondPrefix beyond) {
                    return new PreparedRecapPendingWindows(
                        [],
                        windows,
                        beyond.Evidence,
                        setupProofs
                    );
                }
                SessionHistoryPlanningWindowProof proof =
                    ((SessionHistoryPlanningWindowProofResult.Available)
                        proofResult).Proof;
                setupProofs.Add(
                    engine.ProveGoverningSetupTransition(
                        proof,
                        previousSetupProof,
                        endpoint.Setups
                    )
                );
                proofAuthorities.Add(
                    (route.Plan.RecapBlockId, index),
                    new RecapPendingWindowProofAuthority(
                        route.Plan.RecapBlockId,
                        index,
                        previous,
                        endpoint,
                        proof,
                        previousSetupProof
                    )
                );
                previousSetupProof = setupProofs[^1];
                rawEvents = checked(rawEvents + proof.RawEventCount);
                previous = endpoint;
            }
        }
        if (rawEvents > hardCaps.MaxRawEventsPerBuild) {
            return new PreparedRecapPendingWindows(
                [new RecapPendingWindowDefect(
                    $"Building requires {rawEvents} raw events; limit is "
                    + $"{hardCaps.MaxRawEventsPerBuild}."
                )],
                windows,
                setupProofs: setupProofs
            );
        }
        return new PreparedRecapPendingWindows(
            [],
            windows,
            setupProofs: setupProofs,
            proofAuthorities: proofAuthorities
        );
    }

    public static PreparedRecapPendingWindows Prepare(
        SessionJournalReadView engine,
        EventAddress expectedRawHead,
        IReadOnlyList<PendingMaintainRoute> pendingRoutes,
        RecapProtocolHardCaps hardCaps,
        IReadOnlyDictionary<
            (RecapBlockId BlockId, int EndpointIndex),
            RecapPendingWindowProofAuthority
        > proofAuthorities,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(pendingRoutes);
        ArgumentNullException.ThrowIfNull(hardCaps);
        ArgumentNullException.ThrowIfNull(proofAuthorities);

        var windows = new Dictionary<
            (RecapBlockId BlockId, int EndpointIndex),
            SessionHistoryPlanningWindow
        >();
        IReadOnlyList<RecapPendingWindowDefect> defects =
            ValidatePendingRouteLimits(pendingRoutes, hardCaps);
        if (defects.Count != 0) {
            return new PreparedRecapPendingWindows(defects, windows);
        }
        EventAddress observedRawHead = engine.ReadCurrentHead()
            ?? throw new InvalidDataException(
                "Pending replay-window materialization requires a non-empty SessionJournal."
            );
        if (observedRawHead != expectedRawHead) {
            throw new RecapRawHeadChangedException(
                expectedRawHead,
                observedRawHead
            );
        }

        var provenSteps = new List<(
            (RecapBlockId BlockId, int EndpointIndex) Key,
            RecapPendingWindowProofAuthority Authority
        )>();
        foreach (PendingMaintainRoute route in pendingRoutes) {
            RecapReplayBoundary pendingStart = StartBoundary(route);
            if (pendingStart.Address != route.StartExclusive) {
                throw new InvalidDataException(
                    $"Block '{route.Plan.RecapBlockId}' pending start "
                    + "does not match its frozen replay boundary."
                );
            }
            for (int index = route.NextEndpointIndex;
                 index < route.Plan.CatchUpBoundaries.Count;
                 index++) {
                cancellationToken.ThrowIfCancellationRequested();
                var key = (route.Plan.RecapBlockId, index);
                if (!proofAuthorities.TryGetValue(
                        key,
                        out RecapPendingWindowProofAuthority? authority
                    )) {
                    throw new InvalidDataException(
                        $"Block '{route.Plan.RecapBlockId}' endpoint "
                        + $"'{index}' has no pre-component proof authority."
                    );
                }
                RecapReplayBoundary start = index == 0
                    ? pendingStart
                    : route.Plan.CatchUpBoundaries[index - 1];
                RecapReplayBoundary endpoint =
                    route.Plan.CatchUpBoundaries[index];
                if (authority.BlockId != route.Plan.RecapBlockId
                    || authority.EndpointIndex != index
                    || authority.Start != start
                    || authority.Endpoint != endpoint
                    || authority.WindowProof.StartExclusive
                        != start.Address
                    || authority.WindowProof.CapturedHead
                        != endpoint.Address
                    || authority.StartSetupProof.Boundary
                        != start.Address
                    || authority.StartSetupProof.ExpectedSetups
                        != start.Setups) {
                    throw new InvalidDataException(
                        $"Block '{route.Plan.RecapBlockId}' endpoint "
                        + $"'{index}' differs from its bound "
                        + "pre-component proof authority."
                    );
                }
                provenSteps.Add((key, authority));
            }
        }

        // Validate every authority binding before reading any raw payload.
        // A forged later step therefore cannot partially materialize an
        // earlier step before the route fails closed.
        foreach ((
            (RecapBlockId BlockId, int EndpointIndex) key,
            RecapPendingWindowProofAuthority authority
        ) in provenSteps) {
            SessionHistoryPlanningSeed seed =
                engine.MaterializeHistoryPlanningSeed(
                    authority.StartSetupProof,
                    cancellationToken
                );
            SessionHistoryPlanningWindow window =
                engine.MaterializeHistoryPlanningWindow(
                    authority.WindowProof,
                    seed,
                    cancellationToken
                );
            ValidateExactStepWindow(window, authority.Endpoint, seed);
            windows.Add(key, window);
        }

        EventAddress? afterMaterializationHead =
            engine.ReadCurrentHead();
        if (afterMaterializationHead != expectedRawHead) {
            throw new RecapRawHeadChangedException(
                expectedRawHead,
                afterMaterializationHead ?? default
            );
        }
        return new PreparedRecapPendingWindows([], windows);
    }

    public static IReadOnlyList<RecapPendingWindowDefect>
        ValidateFrozenRouteLimits(
        IEnumerable<MaintainRecapBlockPlan> plans,
        RecapProtocolHardCaps hardCaps
    ) {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(hardCaps);

        var defects = new List<RecapPendingWindowDefect>();
        long calls = 0;
        foreach (MaintainRecapBlockPlan plan in plans) {
            ArgumentNullException.ThrowIfNull(plan);
            calls += plan.CatchUpBoundaries.Count;
            if (plan.CatchUpBoundaries.Count
                > hardCaps.MaxRouteEndpointsPerBlock) {
                defects.Add(new RecapPendingWindowDefect(
                    $"Block '{plan.RecapBlockId}' exceeds the "
                    + "route limit."
                ));
            }
        }
        if (calls > hardCaps.MaxMaintainerCallsPerBuild) {
            defects.Add(new RecapPendingWindowDefect(
                $"Frozen plan requires {calls} Maintainer calls; limit is "
                + $"{hardCaps.MaxMaintainerCallsPerBuild}."
            ));
        }
        return defects;
    }

    public static IReadOnlyList<RecapPendingWindowDefect>
        ValidatePendingRouteLimits(
        IEnumerable<PendingMaintainRoute> routes,
        RecapProtocolHardCaps hardCaps
    ) {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(hardCaps);

        var defects = new List<RecapPendingWindowDefect>();
        long calls = 0;
        foreach (PendingMaintainRoute route in routes) {
            ArgumentNullException.ThrowIfNull(route);
            if (route.NextEndpointIndex < 0
                || route.NextEndpointIndex
                    > route.Plan.CatchUpBoundaries.Count) {
                throw new InvalidDataException(
                    $"Block '{route.Plan.RecapBlockId}' has an invalid "
                    + "pending endpoint index."
                );
            }
            if (route.Plan.CatchUpBoundaries.Count
                > hardCaps.MaxRouteEndpointsPerBlock) {
                defects.Add(new RecapPendingWindowDefect(
                    $"Block '{route.Plan.RecapBlockId}' exceeds the "
                    + "route limit."
                ));
            }
            calls += route.Plan.CatchUpBoundaries.Count
                - route.NextEndpointIndex;
        }
        if (calls > hardCaps.MaxMaintainerCallsPerBuild) {
            defects.Add(new RecapPendingWindowDefect(
                $"Restore requires {calls} Maintainer calls; limit is "
                + $"{hardCaps.MaxMaintainerCallsPerBuild}."
            ));
        }
        return defects;
    }

    private static RecapReplayBoundary StartBoundary(
        PendingMaintainRoute route
    ) {
        if (route.NextEndpointIndex > 0) {
            return route.Plan.CatchUpBoundaries[
                route.NextEndpointIndex - 1
            ];
        }
        SessionContextAnchorSetupReferences setups =
            route.Plan.Source switch {
                ExistingRecapMaintainSource existing =>
                    existing.ReplayStartSetups,
                EmptyRecapMaintainSource empty =>
                    empty.ReplayStartSetups,
                _ => throw new InvalidDataException(
                    "Unsupported Maintain source."
                )
            };
        return new RecapReplayBoundary(route.StartExclusive, setups);
    }

    private static void ValidateExactStepWindow(
        SessionHistoryPlanningWindow window,
        RecapReplayBoundary endpoint,
        SessionHistoryPlanningSeed seed
    ) {
        if (window.StartExclusive != seed.Address
            || window.StartSetups != seed.Setups
            || window.ObservedRawHead != endpoint.Address
            || window.EndSetups != endpoint.Setups
            || !window.ReplaySafeBoundarySetups.TryGetValue(
                endpoint.Address,
                out SessionContextAnchorSetupReferences? boundarySetups
            )
            || boundarySetups != endpoint.Setups
            || !window.ReplaySafeBoundaries.Any(
                boundary => boundary.Address == endpoint.Address)) {
            throw new InvalidDataException(
                "Raw planning window is not the requested exact "
                + "replay-safe interval."
            );
        }
    }

    internal static SessionHistoryPlanningWindow ReadExactStepWindow(
        SessionJournalReadView engine,
        RecapReplayBoundary endpoint,
        SessionHistoryPlanningSeed seed,
        int maxRawEventCount,
        CancellationToken cancellationToken
    ) {
        SessionHistoryPlanningWindowProofResult proofResult =
            engine.ProveHistoryPlanningWindowAtBounded(
                endpoint.Address,
                seed.Address,
                maxRawEventCount,
                cancellationToken
            );
        if (proofResult
            is SessionHistoryPlanningWindowProofResult.BeyondPrefix) {
            throw new InvalidDataException(
                "Exact replay step exceeds its bounded raw-event prefix."
            );
        }
        SessionHistoryPlanningWindow window =
            engine.MaterializeHistoryPlanningWindow(
                ((SessionHistoryPlanningWindowProofResult.Available)
                    proofResult).Proof,
                seed,
                cancellationToken
            );
        ValidateExactStepWindow(window, endpoint, seed);
        return window;
    }
}
