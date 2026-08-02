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

internal sealed record PreparedRecapPendingWindows(
    IReadOnlyList<RecapPendingWindowDefect> Defects,
    IReadOnlyDictionary<
        (RecapBlockId BlockId, int EndpointIndex),
        SessionHistoryPlanningWindow
    > Windows
);

/// <summary>
/// Freezes exact raw windows only for pending Maintain route suffixes.
/// Store inspection and component writes remain phase-specific.
/// </summary>
internal static class RecapPendingWindowPreparer {
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

        EventAddress observedRawHead =
            engine.ReadCurrentLineageHeaders(cancellationToken)
                .CapturedHead;
        if (observedRawHead != expectedRawHead) {
            throw new RecapRawHeadChangedException(
                expectedRawHead,
                observedRawHead
            );
        }
        var defects = new List<RecapPendingWindowDefect>();
        long rawEvents = 0;
        foreach (PendingMaintainRoute route in pendingRoutes) {
            RecapReplayBoundary previous = StartBoundary(route);
            for (int index = route.NextEndpointIndex;
                 index < route.Plan.CatchUpBoundaries.Count;
                 index++) {
                RecapReplayBoundary endpoint =
                    route.Plan.CatchUpBoundaries[index];
                SessionHistoryPlanningSeed seed =
                    engine.CreateHistoryPlanningSeed(
                        previous.Address,
                        previous.Setups,
                        cancellationToken
                    );
                SessionHistoryPlanningWindow window =
                    ReadExactStepWindow(
                        engine,
                        endpoint,
                        seed,
                        cancellationToken
                    );
                if (window.RawAddresses.Count
                    > hardCaps.MaxRawEventsPerStep) {
                    defects.Add(new RecapPendingWindowDefect(
                        $"Block '{route.Plan.RecapBlockId}' step "
                        + $"{index} exceeds the raw step limit."
                    ));
                }
                rawEvents += window.RawAddresses.Count;
                windows.Add(
                    (route.Plan.RecapBlockId, index),
                    window
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

    internal static SessionHistoryPlanningWindow ReadExactStepWindow(
        SessionJournalEngine engine,
        RecapReplayBoundary endpoint,
        SessionHistoryPlanningSeed seed,
        CancellationToken cancellationToken
    ) {
        SessionHistoryPlanningWindow window =
            engine.ReadHistoryPlanningWindowAt(
                endpoint.Address,
                seed,
                cancellationToken
            );
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
        return window;
    }
}
