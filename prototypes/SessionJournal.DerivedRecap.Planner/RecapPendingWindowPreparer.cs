using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

internal sealed record RecapExecutionLimits(
    int MaxRouteEndpointsPerBlock,
    int MaxMaintainerCallsPerBuild,
    int MaxRawEventsPerStep,
    int MaxRawEventsPerBuild
) {
    public static RecapExecutionLimits From(RecapPlannerConfig config) {
        ArgumentNullException.ThrowIfNull(config);
        return new RecapExecutionLimits(
            config.MaxRouteEndpointsPerBlock,
            config.MaxMaintainerCallsPerBuild,
            config.MaxRawEventsPerStep,
            config.MaxRawEventsPerBuild
        );
    }
}

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
        RecapExecutionLimits limits
    ) {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(limits);

        var defects = new List<RecapPendingWindowDefect>();
        long calls = 0;
        foreach (MaintainRecapBlockPlan plan in plans) {
            ArgumentNullException.ThrowIfNull(plan);
            calls += plan.CatchUpThrough.Count;
            if (plan.CatchUpThrough.Count
                > limits.MaxRouteEndpointsPerBlock) {
                defects.Add(new RecapPendingWindowDefect(
                    $"Block '{plan.RecapBlockId}' exceeds the "
                    + "route limit."
                ));
            }
        }
        if (calls > limits.MaxMaintainerCallsPerBuild) {
            defects.Add(new RecapPendingWindowDefect(
                $"Building requires {calls} Maintainer calls; limit is "
                + $"{limits.MaxMaintainerCallsPerBuild}."
            ));
        }
        return defects;
    }

    public static IReadOnlyList<RecapPendingWindowDefect>
        ValidatePendingRouteLimits(
        IEnumerable<PendingMaintainRoute> routes,
        RecapExecutionLimits limits
    ) {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(limits);

        var defects = new List<RecapPendingWindowDefect>();
        long calls = 0;
        foreach (PendingMaintainRoute route in routes) {
            ArgumentNullException.ThrowIfNull(route);
            if (route.NextEndpointIndex < 0
                || route.NextEndpointIndex
                    > route.Plan.CatchUpThrough.Count) {
                throw new InvalidDataException(
                    $"Block '{route.Plan.RecapBlockId}' has an invalid "
                    + "pending endpoint index."
                );
            }
            if (route.Plan.CatchUpThrough.Count
                > limits.MaxRouteEndpointsPerBlock) {
                defects.Add(new RecapPendingWindowDefect(
                    $"Block '{route.Plan.RecapBlockId}' exceeds the "
                    + "route limit."
                ));
            }
            calls += route.Plan.CatchUpThrough.Count
                - route.NextEndpointIndex;
        }
        if (calls > limits.MaxMaintainerCallsPerBuild) {
            defects.Add(new RecapPendingWindowDefect(
                $"Restore requires {calls} Maintainer calls; limit is "
                + $"{limits.MaxMaintainerCallsPerBuild}."
            ));
        }
        return defects;
    }

    public static PreparedRecapPendingWindows Prepare(
        SessionJournalEngine engine,
        EventAddress expectedRawHead,
        IReadOnlyList<PendingMaintainRoute> pendingRoutes,
        RecapExecutionLimits limits,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(pendingRoutes);
        ArgumentNullException.ThrowIfNull(limits);

        var windows = new Dictionary<
            (RecapBlockId BlockId, int EndpointIndex),
            SessionHistoryPlanningWindow
        >();
        if (pendingRoutes.Count == 0) {
            return new PreparedRecapPendingWindows([], windows);
        }

        var starts = new List<EventAddress>();
        foreach (PendingMaintainRoute route in pendingRoutes) {
            if (route.NextEndpointIndex < 0
                || route.NextEndpointIndex
                    > route.Plan.CatchUpThrough.Count) {
                throw new InvalidDataException(
                    $"Block '{route.Plan.RecapBlockId}' has an invalid "
                    + "pending endpoint index."
                );
            }
            EventAddress previous = route.NextEndpointIndex == 0
                ? route.StartExclusive
                : route.Plan.CatchUpThrough[
                    route.NextEndpointIndex - 1
                ];
            for (int index = route.NextEndpointIndex;
                 index < route.Plan.CatchUpThrough.Count;
                 index++) {
                starts.Add(previous);
                previous = route.Plan.CatchUpThrough[index];
            }
        }
        if (starts.Count == 0) {
            return new PreparedRecapPendingWindows([], windows);
        }

        SessionHistoryPlanningSeedBatch seedBatch =
            engine.ReadHistoryPlanningSeeds(
                starts.Distinct(),
                cancellationToken
            );
        if (seedBatch.Lineage.CapturedHead != expectedRawHead) {
            throw new RecapRawHeadChangedException(
                expectedRawHead,
                seedBatch.Lineage.CapturedHead
            );
        }
        Dictionary<EventAddress, SessionHistoryPlanningSeed> seeds =
            seedBatch.Seeds.ToDictionary(static seed => seed.Address);
        var defects = new List<RecapPendingWindowDefect>();
        long rawEvents = 0;
        foreach (PendingMaintainRoute route in pendingRoutes) {
            EventAddress previous = route.NextEndpointIndex == 0
                ? route.StartExclusive
                : route.Plan.CatchUpThrough[
                    route.NextEndpointIndex - 1
                ];
            for (int index = route.NextEndpointIndex;
                 index < route.Plan.CatchUpThrough.Count;
                 index++) {
                EventAddress endpoint =
                    route.Plan.CatchUpThrough[index];
                SessionHistoryPlanningWindow window =
                    ReadExactStepWindow(
                        engine,
                        endpoint,
                        seeds[previous],
                        cancellationToken
                    );
                if (window.RawAddresses.Count
                    > limits.MaxRawEventsPerStep) {
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
        if (rawEvents > limits.MaxRawEventsPerBuild) {
            defects.Add(new RecapPendingWindowDefect(
                $"Building requires {rawEvents} raw events; limit is "
                + $"{limits.MaxRawEventsPerBuild}."
            ));
        }
        return new PreparedRecapPendingWindows(defects, windows);
    }

    internal static SessionHistoryPlanningWindow ReadExactStepWindow(
        SessionJournalEngine engine,
        EventAddress endpoint,
        SessionHistoryPlanningSeed seed,
        CancellationToken cancellationToken
    ) {
        SessionHistoryPlanningWindow window =
            engine.ReadHistoryPlanningWindowAt(
                endpoint,
                seed,
                cancellationToken
            );
        if (window.StartExclusive != seed.Address
            || window.ObservedRawHead != endpoint
            || !window.ReplaySafeBoundaries.Any(
                boundary => boundary.Address == endpoint)) {
            throw new InvalidDataException(
                "Raw planning window is not the requested exact "
                + "replay-safe interval."
            );
        }
        return window;
    }
}
