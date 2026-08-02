using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

internal sealed record PendingMaintainRoute(
    MaintainRecapBlockPlan Plan,
    EventAddress StartExclusive,
    int NextEndpointIndex
);

internal sealed record RecapPendingWindowDefect(string Detail);

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
        IReadOnlyList<SessionGoverningSetupProof>? setupProofs = null
    ) {
        Defects = defects;
        Windows = windows;
        BeyondPrefix = beyondPrefix;
        SetupProofs = setupProofs
            ?? Array.Empty<SessionGoverningSetupProof>();
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
        SessionJournalEngine engine,
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
            setupProofs: setupProofs
        );
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

    public static PreparedRecapPendingWindows Prepare(
        SessionJournalEngine engine,
        EventAddress expectedRawHead,
        IReadOnlyList<PendingMaintainRoute> pendingRoutes,
        RecapProtocolHardCaps hardCaps,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(pendingRoutes);
        ArgumentNullException.ThrowIfNull(hardCaps);

        var windows = new Dictionary<
            (RecapBlockId BlockId, int EndpointIndex),
            SessionHistoryPlanningWindow
        >();
        if (pendingRoutes.Count == 0) {
            return new PreparedRecapPendingWindows([], windows);
        }

        foreach (PendingMaintainRoute route in pendingRoutes) {
            if (route.NextEndpointIndex < 0
                || route.NextEndpointIndex
                    > route.Plan.CatchUpBoundaries.Count) {
                throw new InvalidDataException(
                    $"Block '{route.Plan.RecapBlockId}' has an invalid "
                    + "pending endpoint index."
                );
            }
            RecapReplayBoundary previous = StartBoundary(route);
            if (previous.Address != route.StartExclusive) {
                throw new InvalidDataException(
                    $"Block '{route.Plan.RecapBlockId}' pending start "
                    + "does not match its frozen replay boundary."
                );
            }
        }
        if (pendingRoutes.All(route =>
                route.NextEndpointIndex
                    == route.Plan.CatchUpBoundaries.Count)) {
            return new PreparedRecapPendingWindows([], windows);
        }

        EventAddress observedRawHead = engine.ReadCurrentHead()
            ?? throw new InvalidDataException(
                "Pending replay-window preparation requires a non-empty SessionJournal."
            );
        if (observedRawHead != expectedRawHead) {
            throw new RecapRawHeadChangedException(
                expectedRawHead,
                observedRawHead
            );
        }
        var defects = new List<RecapPendingWindowDefect>();
        var proofs = new Dictionary<
            (RecapBlockId BlockId, int EndpointIndex),
            (SessionHistoryPlanningWindowProof Proof,
                RecapReplayBoundary Start,
                RecapReplayBoundary End)
        >();
        long rawEvents = 0;
        foreach (PendingMaintainRoute route in pendingRoutes) {
            RecapReplayBoundary previous = StartBoundary(route);
            for (int index = route.NextEndpointIndex;
                 index < route.Plan.CatchUpBoundaries.Count;
                 index++) {
                RecapReplayBoundary endpoint =
                    route.Plan.CatchUpBoundaries[index];
                SessionHistoryPlanningWindowProofResult proofResult =
                    engine.ProveHistoryPlanningWindowAtBounded(
                        endpoint.Address,
                        previous.Address,
                        hardCaps.MaxRawEventsPerStep,
                        cancellationToken
                    );
                if (proofResult
                    is SessionHistoryPlanningWindowProofResult
                        .BeyondPrefix beyond) {
                    return new PreparedRecapPendingWindows(
                        defects,
                        windows,
                        beyond.Evidence
                    );
                }
                SessionHistoryPlanningWindowProof proof =
                    ((SessionHistoryPlanningWindowProofResult.Available)
                        proofResult).Proof;
                engine.ValidateGoverningSetupTransition(
                    proof,
                    previous.Setups,
                    endpoint.Setups
                );
                rawEvents = checked(rawEvents + proof.RawEventCount);
                proofs.Add(
                    (route.Plan.RecapBlockId, index),
                    (proof, previous, endpoint)
                );
                previous = endpoint;
            }
        }
        if (rawEvents > hardCaps.MaxRawEventsPerBuild) {
            defects.Add(new RecapPendingWindowDefect(
                $"Building requires {rawEvents} raw events; limit is "
                + $"{hardCaps.MaxRawEventsPerBuild}."
            ));
        }
        if (defects.Count != 0) {
            return new PreparedRecapPendingWindows(defects, windows);
        }

        EventAddress? afterProofHead = engine.ReadCurrentHead();
        if (afterProofHead != expectedRawHead) {
            throw new RecapRawHeadChangedException(
                expectedRawHead,
                afterProofHead ?? default
            );
        }

        foreach ((
            (RecapBlockId BlockId, int EndpointIndex) key,
            (SessionHistoryPlanningWindowProof Proof,
                RecapReplayBoundary Start,
                RecapReplayBoundary End) step
        ) in proofs) {
            SessionHistoryPlanningSeed seed =
                engine.CreateHistoryPlanningSeed(
                    step.Start.Address,
                    step.Start.Setups,
                    cancellationToken
                );
            SessionHistoryPlanningWindow window =
                engine.MaterializeHistoryPlanningWindow(
                    step.Proof,
                    seed,
                    cancellationToken
                );
            ValidateExactStepWindow(window, step.End, seed);
            windows.Add(key, window);
        }
        return new PreparedRecapPendingWindows(defects, windows);
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
        SessionJournalEngine engine,
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
