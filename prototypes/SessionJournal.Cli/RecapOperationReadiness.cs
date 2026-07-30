using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Maintainers;
using Atelia.SessionJournal.DerivedRecap.Planner;
using Atelia.SessionJournal.DerivedRecap.Store;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.Cli;

internal abstract record PreparedRecapOperationAuthority {
    private PreparedRecapOperationAuthority() {
    }

    internal sealed record FrozenBuilding(
        BuildingSnapshot Snapshot,
        RecapMaintainerProfileCatalog CapabilityCatalog
    ) : PreparedRecapOperationAuthority;

    internal sealed record NewPlanning(
        ResolvedRecapPlannerComposition Composition,
        DerivedRecapPlanningBaseline Baseline
    ) : PreparedRecapOperationAuthority;
}

internal abstract record RecapOperationReadinessResult {
    private RecapOperationReadinessResult() {
    }

    internal sealed record Ready(
        PreparedRecapOperationAuthority Authority,
        SJ.SessionCurrentLineageSnapshot Lineage
    ) : RecapOperationReadinessResult;

    internal sealed record Blocked(
        IReadOnlyList<RecapOperationReadinessDefect> Defects,
        ResolvedRecapPlannerComposition? Composition = null
    ) : RecapOperationReadinessResult;
}

internal sealed record RecapOperationReadinessDefect(
    string Code,
    string Detail
);

/// <summary>
/// Performs every Store/config read that must precede completion-client and
/// call-log construction for one new-request or recap-run operation.
/// Frozen Building authority is selected before the repo config is touched.
/// </summary>
internal static class RecapOperationReadiness {
    internal static async ValueTask<RecapOperationReadinessResult>
        PrepareAsync(
        SJ.SessionJournalEngine engine,
        DerivedRecapStore store,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(store);

        SJ.SessionCurrentLineageSnapshot lineage;
        try {
            lineage = engine.ReadCurrentLineageHeaders(
                cancellationToken
            );
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or IOException
                or UnauthorizedAccessException
        ) {
            return Blocked("RawLineageUnavailable", exception.Message);
        }

        CurrentLineageBuildingSelection building;
        try {
            building =
                await store.SelectCurrentLineageBuildingAsync(
                        lineage,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or IOException
                or UnauthorizedAccessException
        ) {
            return Blocked("StoreUnavailable", exception.Message);
        }
        switch (building) {
            case CurrentLineageBuildingSelection.Available available:
                RecapMaintainerProfileCatalog capabilities =
                    RecapMaintainerProfileCatalog.BuiltIn;
                foreach (MaintainRecapBlockPlan plan
                    in available.Snapshot.Manifest.Blocks
                        .OfType<MaintainRecapBlockPlan>()) {
                    if (!capabilities.TryResolveFrozen(
                            plan.MaintainerId,
                            plan.Target,
                            out _
                        )) {
                        return Blocked(
                            DerivedRecapExecutionDefectCodes
                                .MaintainerUnavailable,
                            "Frozen Building maintainer binding for "
                            + $"'{plan.RecapBlockId}' is unavailable."
                        );
                    }
                }
                return CurrentHeadMatches(engine, lineage.CapturedHead)
                    ? new RecapOperationReadinessResult.Ready(
                        new PreparedRecapOperationAuthority
                            .FrozenBuilding(
                                available.Snapshot,
                                capabilities
                            ),
                        lineage
                    )
                    : RawHeadChanged(lineage.CapturedHead);
            case CurrentLineageBuildingSelection.Invalid invalid:
                return Blocked(invalid.Defects);
            case CurrentLineageBuildingSelection.Stale stale:
                return Blocked(
                    "StaleCurrentLineageBuilding",
                    $"Building '{stale.SetAdmissionAnchor}' is not "
                    + "strictly newer than latest Published "
                    + $"'{stale.LatestPublishedAnchor}'. Explicitly "
                    + "abandon the stale Building."
                );
            case CurrentLineageBuildingSelection.Multiple multiple:
                return Blocked(
                    "MultipleCurrentLineageBuildings",
                    "Current raw lineage has multiple Building "
                    + "memberships: "
                    + string.Join(
                        ", ",
                        multiple.SetAdmissionAnchors.Select(
                            SJ.EventAddressTextCodec.Format
                        )
                    )
                    + ". Use exact resume or abandon-building."
                );
            case CurrentLineageBuildingSelection.StoreUnavailable
                unavailable:
                return Blocked(
                    "StoreUnavailable",
                    unavailable.Reason
                );
            case CurrentLineageBuildingSelection.None:
                break;
            default:
                throw new InvalidDataException(
                    "Unknown current-lineage Building selection."
                );
        }

        RecapPlannerCompositionLoadResult loaded =
            RecapPlannerCompositionLoader.Load(
                store.SessionRepositoryPath
            );
        if (loaded
            is not RecapPlannerCompositionLoadResult.Resolved resolved) {
            return loaded switch {
                RecapPlannerCompositionLoadResult.Missing missing =>
                    Blocked(
                        "PlannerConfigMissing",
                        $"Recap planner config is missing: "
                        + missing.Path
                    ),
                RecapPlannerCompositionLoadResult.Invalid invalid =>
                    Blocked(invalid.Defects),
                RecapPlannerCompositionLoadResult.Unavailable unavailable =>
                    Blocked(
                        "PlannerConfigUnavailable",
                        unavailable.Reason
                    ),
                _ => throw new InvalidDataException(
                    "Unknown planner composition load result."
                )
            };
        }

        ResolvedRecapPlannerComposition composition =
            resolved.Composition;
        DerivedRecapSelection latest;
        try {
            latest = await store.SelectNthPreviousAsync(
                    lineage,
                    nthPrevious: 0,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or IOException
                or UnauthorizedAccessException
        ) {
            return Blocked(
                "StoreUnavailable",
                exception.Message,
                composition
            );
        }

        FrozenCatalogReadResult frozenCatalog =
            await ReadLatestFrozenCatalogAsync(
                    store,
                    latest,
                    cancellationToken
                )
                .ConfigureAwait(false);
        if (frozenCatalog
            is FrozenCatalogReadResult.Unavailable catalogUnavailable) {
            return new RecapOperationReadinessResult.Blocked(
                catalogUnavailable.Defects,
                composition
            );
        }
        if (frozenCatalog
            is FrozenCatalogReadResult.Retryable catalogChanged) {
            return Blocked(
                catalogChanged.Code,
                catalogChanged.Detail,
                composition
            );
        }
        if (frozenCatalog
            is FrozenCatalogReadResult.Available catalogAvailable) {
            RecapCatalogShapeComparison comparison =
                RecapCatalogShape.Compare(
                    RecapCatalogShape.ProjectActive(
                        composition.PlanningInputs.OrderedCatalog
                    ),
                    RecapCatalogShape.ProjectFrozen(
                        catalogAvailable.Blocks
                    )
                );
            if (!comparison.IsExactMatch) {
                return Blocked(
                    DerivedRecapExecutionDefectCodes
                        .CatalogMigrationRequired,
                    comparison.Detail,
                    composition
                );
            }
        }

        if (!CurrentHeadMatches(engine, lineage.CapturedHead)) {
            return RawHeadChanged(
                lineage.CapturedHead,
                composition
            );
        }
        try {
            return new RecapOperationReadinessResult.Ready(
                new PreparedRecapOperationAuthority.NewPlanning(
                    composition,
                    DerivedRecapPlanningBaseline.FromSelection(
                        lineage.CapturedHead,
                        latest
                    )
                ),
                lineage
            );
        }
        catch (ArgumentException exception) {
            return Blocked(
                "LatestPublishedUnavailable",
                exception.Message,
                composition
            );
        }
    }

    private static async ValueTask<FrozenCatalogReadResult>
        ReadLatestFrozenCatalogAsync(
        DerivedRecapStore store,
        DerivedRecapSelection latest,
        CancellationToken cancellationToken
    ) {
        switch (latest) {
            case DerivedRecapSelection.EmptyLineage:
                return new FrozenCatalogReadResult.Empty();
            case DerivedRecapSelection.Selected selected:
                PublishedPlanReadResult plan =
                    await store.ReadPublishedPlanAsync(
                            selected.Descriptor,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                return plan switch {
                    PublishedPlanReadResult.Available available =>
                        new FrozenCatalogReadResult.Available(
                            available.Snapshot.FrozenPlan.Blocks
                        ),
                    PublishedPlanReadResult.Changed changed =>
                        new FrozenCatalogReadResult.Retryable(
                            DerivedRecapExecutionDefectCodes
                                .SourceChanged,
                            "Latest Published plan changed during "
                            + "readiness: expected "
                            + $"'{changed.Expected.EnvelopeSha256}', "
                            + "observed "
                            + $"'{changed.Observed.EnvelopeSha256}'."
                        ),
                    PublishedPlanReadResult.Unavailable unavailable =>
                        FrozenCatalogUnavailable(
                            unavailable.Defects
                        ),
                    _ => throw new InvalidDataException(
                        "Unknown Published plan read result."
                    )
                };
            case DerivedRecapSelection.ExactPublishedSetInvalid invalid:
                PublishedPlanAtAnchorReadResult atAnchor =
                    await store.ReadPublishedPlanAtAnchorAsync(
                            invalid.SetAdmissionAnchor,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                return atAnchor switch {
                    PublishedPlanAtAnchorReadResult.Available available =>
                        new FrozenCatalogReadResult.Available(
                            available.Snapshot.FrozenPlan.Blocks
                        ),
                    PublishedPlanAtAnchorReadResult.Missing missing =>
                        FrozenCatalogUnavailable(
                            "PublishedPlanMissing",
                            "Latest Published plan disappeared at "
                            + $"'{SJ.EventAddressTextCodec.Format(
                                missing.SetAdmissionAnchor
                            )}'."
                        ),
                    PublishedPlanAtAnchorReadResult.Changed changed =>
                        new FrozenCatalogReadResult.Retryable(
                            DerivedRecapExecutionDefectCodes
                                .SourceChanged,
                            "Latest Published plan changed during "
                            + "readiness: before "
                            + $"'{changed.Before.EnvelopeSha256}', after "
                            + $"'{changed.After?.EnvelopeSha256
                                ?? "<missing>"}'."
                        ),
                    PublishedPlanAtAnchorReadResult.Unavailable
                        unavailable => FrozenCatalogUnavailable(
                            unavailable.Defects
                        ),
                    _ => throw new InvalidDataException(
                        "Unknown Published plan-at-anchor read result."
                    )
                };
            case DerivedRecapSelection.StoreUnavailable unavailable:
                return FrozenCatalogUnavailable(
                    "StoreUnavailable",
                    unavailable.Reason
                );
            case DerivedRecapSelection.OrdinalUnavailable:
                return FrozenCatalogUnavailable(
                    "LatestPublishedUnavailable",
                    "Latest strict Published ordinal is unavailable."
                );
            default:
                return FrozenCatalogUnavailable(
                    "LatestPublishedUnavailable",
                    $"Unsupported latest selection "
                    + $"'{latest.GetType().Name}'."
                );
        }
    }

    private static FrozenCatalogReadResult.Unavailable
        FrozenCatalogUnavailable(
        string code,
        string detail
    ) => new(Array.AsReadOnly([
        new RecapOperationReadinessDefect(code, detail)
    ]));

    private static FrozenCatalogReadResult.Unavailable
        FrozenCatalogUnavailable(
        IEnumerable<RecapStructuralDefect> defects
    ) => new(Array.AsReadOnly([
        .. defects.Select(static defect =>
            new RecapOperationReadinessDefect(
                defect.Code,
                defect.Detail
            )
        )
    ]));

    private static bool CurrentHeadMatches(
        SJ.SessionJournalEngine engine,
        EventAddress expected
    ) => engine.ReadCurrentHead() == expected;

    private static RecapOperationReadinessResult RawHeadChanged(
        EventAddress expected,
        ResolvedRecapPlannerComposition? composition = null
    ) => Blocked(
        DerivedRecapExecutionDefectCodes.RawHeadChanged,
        $"Raw SessionJournal head changed after recap readiness "
        + $"capture '{SJ.EventAddressTextCodec.Format(expected)}'.",
        composition
    );

    private static RecapOperationReadinessResult.Blocked Blocked(
        string code,
        string detail,
        ResolvedRecapPlannerComposition? composition = null
    ) => new(
        Array.AsReadOnly([
            new RecapOperationReadinessDefect(code, detail)
        ]),
        composition
    );

    private abstract record FrozenCatalogReadResult {
        private FrozenCatalogReadResult() {
        }

        internal sealed record Empty : FrozenCatalogReadResult;

        internal sealed record Available(
            IReadOnlyList<RecapBlockPlan> Blocks
        ) : FrozenCatalogReadResult;

        internal sealed record Unavailable(
            IReadOnlyList<RecapOperationReadinessDefect> Defects
        ) : FrozenCatalogReadResult;

        internal sealed record Retryable(string Code, string Detail)
            : FrozenCatalogReadResult;
    }

    private static RecapOperationReadinessResult.Blocked Blocked(
        IEnumerable<RecapStructuralDefect> defects,
        ResolvedRecapPlannerComposition? composition = null
    ) => new(
        Array.AsReadOnly([
            .. defects.Select(static defect =>
                new RecapOperationReadinessDefect(
                    defect.Code,
                    defect.Detail
                )
            )
        ]),
        composition
    );

    private static RecapOperationReadinessResult.Blocked Blocked(
        IEnumerable<RecapPlannerCompositionLoadDefect> defects,
        ResolvedRecapPlannerComposition? composition = null
    ) => new(
        Array.AsReadOnly([
            .. defects.Select(static defect =>
                new RecapOperationReadinessDefect(
                    defect.Code,
                    defect.Detail
                )
            )
        ]),
        composition
    );
}
