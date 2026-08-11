using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Getter;
using Atelia.SessionJournal.RecapGrid.Manager;
using Atelia.SessionJournal.RecapGrid.Store;

namespace Atelia.Galatea.Server;

internal static class GalateaRecapGridReadiness {
    internal const string ExactFreshness = "exact";
    internal const string StaleFreshness = "stale";
    internal static readonly AsyncLocal<Action?> BeforeFinalRawFenceForTest =
        new();

    private static readonly RecapGridBuildBudget ProgressBudget = new(
        maximumSelectedRows: 4_096,
        maximumRecipeRowSteps: 65_536,
        maximumNewCalls:
            RecapGridBuildProgressLimits.MaximumFrontierAssignments,
        maximumElapsed: TimeSpan.FromSeconds(5)
    );

    internal static RecapGridReadinessSnapshotDto Inspect(
        SessionJournalReadView selectedRef,
        EventAddress capturedRawHead,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(selectedRef);
        cancellationToken.ThrowIfCancellationRequested();
        RecapGridContextOpenResult opened =
            RecapGridContextFactory.Open(selectedRef);
        if (opened is not RecapGridContextOpenResult.Opened available) {
            return RequireRawHead(
                selectedRef,
                capturedRawHead,
                MapGetterOpen(opened, capturedRawHead)
            );
        }
        using RecapGridContextHandle getter = available.Handle;
        RecapGridContextResolveResult resolved = getter.Resolve(
            capturedRawHead,
            nthPrevious: 0,
            cancellationToken
        );
        RecapGridReadinessSnapshotDto result = resolved switch {
            RecapGridContextResolveResult.RawHistoryAuthorized
                => Exact("raw-only", capturedRawHead),
            RecapGridContextResolveResult.Selected selected
                => new RecapGridReadinessSnapshotDto(
                    ExactFreshness,
                    "ready",
                    Format(capturedRawHead),
                    Authority(selected.Selection)
                ),
            RecapGridContextResolveResult.Unfulfilled
                => InspectUnfulfilled(
                    selectedRef,
                    capturedRawHead,
                    cancellationToken
                ),
            RecapGridContextResolveResult.OrdinalUnavailable
                => Exact(
                    "invalid",
                    capturedRawHead,
                    code: "current-row-unavailable"
                ),
            RecapGridContextResolveResult.LimitExceeded limit
                => Exact(
                    "limited",
                    capturedRawHead,
                    code: limit.Limit
                ),
            RecapGridContextResolveResult.Stale stale
                => Stale(capturedRawHead, stale.Detail),
            RecapGridContextResolveResult.NotOnSelectedPath missing
                => Exact(
                    "invalid",
                    capturedRawHead,
                    code: "row-not-on-selected-path",
                    detail: missing.RowId.Value
                ),
            RecapGridContextResolveResult.Busy busy
                => Exact(
                    "busy",
                    capturedRawHead,
                    code: busy.Component.ToString()
                ),
            RecapGridContextResolveResult.Disposed disposed
                => Exact(
                    "unavailable",
                    capturedRawHead,
                    code: $"{disposed.Component}-disposed"
                ),
            RecapGridContextResolveResult.UnsupportedSchema schema
                => Exact(
                    "invalid",
                    capturedRawHead,
                    code: $"{schema.Component}-schema-{schema.SchemaVersion}"
                ),
            RecapGridContextResolveResult.Invalid invalid
                => Exact(
                    "invalid",
                    capturedRawHead,
                    code: $"{invalid.Component}:{invalid.Code}",
                    detail: invalid.Detail
                ),
            _ => Exact(
                "invalid",
                capturedRawHead,
                code: "getter-outcome-unknown"
            )
        };
        return RequireRawHead(selectedRef, capturedRawHead, result);
    }

    private static RecapGridReadinessSnapshotDto InspectUnfulfilled(
        SessionJournalReadView selectedRef,
        EventAddress capturedRawHead,
        CancellationToken cancellationToken
    ) {
        RecapGridManagerOpenResult opened = RecapGridManagerFactory.Open(
            selectedRef,
            new O200kBaseHistoryUnitLoadEstimator()
        );
        if (opened is not RecapGridManagerOpenResult.Opened available) {
            return MapManagerOpen(opened, capturedRawHead);
        }
        using RecapGridManagerHandle handle = available.Handle;
        RecapGridBuildProgressResult progress =
            handle.Manager.InspectBuildProgress(
                new RecapGridBuildRequest(
                    new RecapGridBuildSelection.LiveActive(),
                    throughRowId: null,
                    ProgressBudget
                ),
                cancellationToken
            );
        RecapGridReadinessMetricsDto metrics = new(
            progress.Metrics.SelectedRows,
            progress.Metrics.RecipeRowSteps,
            progress.Metrics.ExaminedAssignments,
            progress.Metrics.MissingAssignments
        );
        return progress switch {
            RecapGridBuildProgressResult.Complete complete
                => new RecapGridReadinessSnapshotDto(
                    ExactFreshness,
                    complete.FulfillmentPresent
                        ? "ready"
                        : "fulfillment-missing",
                    Format(capturedRawHead),
                    Authority(complete.Authority),
                    metrics
                ),
            RecapGridBuildProgressResult.Frontier frontier
                => new RecapGridReadinessSnapshotDto(
                    ExactFreshness,
                    "frontier",
                    Format(capturedRawHead),
                    Authority(frontier.Authority),
                    metrics,
                    [.. frontier.OrderedMissing.Select(static value =>
                        new RecapGridMissingAssignmentDto(
                            value.Ordinal,
                            value.RowId.Value!,
                            value.RecipeDigest.Value!,
                            value.LogicalColumnId.Value!,
                            value.EvaluationKey.Value!
                        ))]
                ),
            RecapGridBuildProgressResult.Blocked blocked
                => new RecapGridReadinessSnapshotDto(
                    ExactFreshness,
                    "blocked",
                    Format(capturedRawHead),
                    Authority(blocked.Authority),
                    metrics,
                    Code: blocked.Code,
                    Detail: blocked.Detail
                ),
            RecapGridBuildProgressResult.NoRows noRows
                => Exact(
                    "no-rows",
                    capturedRawHead,
                    metrics: metrics,
                    code: noRows.RecipeDigest.Value
                ),
            RecapGridBuildProgressResult.NoActiveRecipe
                => Exact("no-active", capturedRawHead, metrics),
            RecapGridBuildProgressResult.RecipeAbsent absent
                => Exact(
                    "invalid",
                    capturedRawHead,
                    metrics,
                    "recipe-absent",
                    absent.RecipeDigest.Value
                ),
            RecapGridBuildProgressResult.ThroughRowNotSelected missing
                => Exact(
                    "invalid",
                    capturedRawHead,
                    metrics,
                    "through-row-not-selected",
                    missing.RowId.Value
                ),
            RecapGridBuildProgressResult.BudgetExceeded limit
                => Exact(
                    "limited",
                    capturedRawHead,
                    metrics,
                    limit.Kind.ToString(),
                    limit.AtRow?.Value
                ),
            RecapGridBuildProgressResult.Cancelled
                => Exact("cancelled", capturedRawHead, metrics),
            RecapGridBuildProgressResult.Unavailable unavailable
                => Exact(
                    "unavailable",
                    capturedRawHead,
                    metrics,
                    $"{unavailable.Dependency}:{unavailable.Code}",
                    unavailable.Detail
                ),
            RecapGridBuildProgressResult.StaleTimelineHead
                => Stale(capturedRawHead, "Timeline head changed.", metrics),
            RecapGridBuildProgressResult.StaleControlAuthority
                => Stale(capturedRawHead, "Control head changed.", metrics),
            RecapGridBuildProgressResult.Disposed
                => Exact(
                    "unavailable",
                    capturedRawHead,
                    metrics,
                    "manager-disposed"
                ),
            RecapGridBuildProgressResult.Invalid invalid
                => Exact(
                    "invalid",
                    capturedRawHead,
                    metrics,
                    invalid.Code,
                    invalid.Detail
                ),
            _ => Exact(
                "invalid",
                capturedRawHead,
                metrics,
                "progress-outcome-unknown"
            )
        };
    }

    private static RecapGridReadinessSnapshotDto MapGetterOpen(
        RecapGridContextOpenResult result,
        EventAddress rawHead
    ) => result switch {
        RecapGridContextOpenResult.TimelineAbsent
            => Exact("unprovisioned", rawHead, code: "timeline-absent"),
        RecapGridContextOpenResult.ControlAbsent
            => Exact("unprovisioned", rawHead, code: "control-absent"),
        RecapGridContextOpenResult.Busy busy
            => Exact("busy", rawHead, code: busy.Component.ToString()),
        RecapGridContextOpenResult.UnsupportedSchema schema
            => Exact(
                "invalid",
                rawHead,
                code: $"{schema.Component}-schema-{schema.SchemaVersion}"
            ),
        RecapGridContextOpenResult.DisposedRawAuthority
            => Exact("unavailable", rawHead, code: "raw-disposed"),
        RecapGridContextOpenResult.Invalid invalid
            => Exact(
                "invalid",
                rawHead,
                code: $"{invalid.Component}:{invalid.Code}",
                detail: invalid.Detail
            ),
        _ => Exact("invalid", rawHead, code: "getter-open-unknown")
    };

    private static RecapGridReadinessSnapshotDto MapManagerOpen(
        RecapGridManagerOpenResult result,
        EventAddress rawHead
    ) => result switch {
        RecapGridManagerOpenResult.Absent absent
            => Exact(
                "unprovisioned",
                rawHead,
                code: $"{absent.Dependency}-absent"
            ),
        RecapGridManagerOpenResult.Busy busy
            => Exact("busy", rawHead, code: busy.Dependency.ToString()),
        RecapGridManagerOpenResult.UnsupportedSchema schema
            => Exact(
                "invalid",
                rawHead,
                code: $"{schema.Dependency}-schema-{schema.SchemaVersion}"
            ),
        RecapGridManagerOpenResult.PlatformUnsupported unsupported
            => Exact(
                "invalid",
                rawHead,
                code: $"{unsupported.Dependency}-platform"
            ),
        RecapGridManagerOpenResult.Invalid invalid
            => Exact(
                "invalid",
                rawHead,
                code: $"{invalid.Dependency}:{invalid.Code}",
                detail: invalid.Detail
            ),
        _ => Exact("invalid", rawHead, code: "manager-open-unknown")
    };

    private static RecapGridReadinessSnapshotDto RequireRawHead(
        SessionJournalReadView selectedRef,
        EventAddress expected,
        RecapGridReadinessSnapshotDto result
    ) {
        try {
            BeforeFinalRawFenceForTest.Value?.Invoke();
            return selectedRef.ReadCurrentHead() == expected
                ? result
                : Stale(expected, "Raw head changed during readiness read.");
        }
        catch (ObjectDisposedException) {
            return Stale(expected, "Raw authority was disposed.");
        }
    }

    private static RecapGridReadinessAuthorityDto Authority(
        RecapGridContextSelection selection
    ) => Authority(
        selection.TimelineHead,
        selection.ControlHead,
        selection.StoreIdentity,
        selection.Recipe.Digest,
        selection.SelectedRowId,
        selection.SelectedDescriptorDigest
    );

    private static RecapGridReadinessAuthorityDto Authority(
        RecapGridBuildProgressAuthority authority
    ) => Authority(
        authority.TimelineHead,
        authority.ControlHead,
        authority.StoreIdentity,
        authority.RecipeDigest,
        authority.ThroughRowId,
        authority.ThroughDescriptorDigest
    );

    private static RecapGridReadinessAuthorityDto Authority(
        TimelineHeadRef timeline,
        ControlHeadRef control,
        RecapGridStoreIdentity store,
        GridBuildRecipeDigest recipe,
        HistoryRowId through,
        HistorySegmentDescriptorDigest descriptor
    ) => new(
        timeline.RefId.ToHexString(),
        timeline.TimelineId.Value!,
        timeline.Generation,
        timeline.HeadRowId?.Value,
        control.Generation,
        control.StateDigest.Value!,
        store.InstanceId.Value!,
        store.SchemaVersion,
        recipe.Value!,
        through.Value!,
        descriptor.Value!
    );

    private static RecapGridReadinessSnapshotDto Exact(
        string state,
        EventAddress rawHead,
        RecapGridReadinessMetricsDto? metrics = null,
        string? code = null,
        string? detail = null
    ) => new(
        ExactFreshness,
        state,
        Format(rawHead),
        Metrics: metrics,
        Code: code,
        Detail: detail
    );

    private static RecapGridReadinessSnapshotDto Stale(
        EventAddress rawHead,
        string detail,
        RecapGridReadinessMetricsDto? metrics = null
    ) => new(
        StaleFreshness,
        "stale",
        Format(rawHead),
        Metrics: metrics,
        Code: "authority-changed",
        Detail: detail
    );

    private static string Format(EventAddress address)
        => EventAddressTextCodec.Format(address);
}
