using Atelia.SessionJournal.DerivedRecap.Maintainers;
using Atelia.SessionJournal.DerivedRecap.Planner;
using Atelia.SessionJournal.DerivedRecap.Store;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.Cli;

internal abstract record RecapOperationReadinessResult {
    private RecapOperationReadinessResult() {
    }

    internal sealed record Ready(
        PreparedRecapOperationAuthority Authority,
        RecapMaintainerProfileCatalog CapabilityCatalog,
        ResolvedRecapPlannerComposition? Composition
    ) : RecapOperationReadinessResult {
        internal SJ.SessionCurrentLineageSnapshot Lineage =>
            Authority.Lineage;
    }

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
/// CLI-only concrete capability/report adapter over the public Building-first
/// preparer. It contains no Store/config planning decisions.
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

        RecapMaintainerProfileCatalog concreteCapabilities;
        RecapMaintainerCapabilitySnapshot planningCapabilities;
        try {
            concreteCapabilities = RecapMaintainerProfileCatalog.BuiltIn;
            planningCapabilities =
                RecapCliCompositionResolver.ProjectCapabilities(
                    concreteCapabilities
                );
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or InvalidOperationException
                or ArgumentException
                or IOException
                or UnauthorizedAccessException
        ) {
            return Blocked(
                DerivedRecapExecutionDefectCodes
                    .MaintainerUnavailable,
                exception.Message
            );
        }

        var source =
            new RepositoryRecapActivePlanningConfigurationSource(
                store.SessionRepositoryPath,
                RecapPlannerConfigResolutionCatalog.BuiltIn,
                planningCapabilities
            );
        DerivedRecapOperationPreparationResult prepared =
            await DerivedRecapOperationPreparer.PrepareAsync(
                    engine,
                    store,
                    planningCapabilities,
                    source,
                    cancellationToken
                )
                .ConfigureAwait(false);

        return prepared switch {
            DerivedRecapOperationPreparationResult.Ready ready =>
                Ready(
                    ready.Authority,
                    concreteCapabilities
                ),
            DerivedRecapOperationPreparationResult.Retryable retryable =>
                Blocked(
                    retryable.Code,
                    retryable.Detail,
                    Enrich(
                        retryable.Configuration,
                        concreteCapabilities
                    )
                ),
            DerivedRecapOperationPreparationResult.Unavailable
                unavailable => new RecapOperationReadinessResult.Blocked(
                    Array.AsReadOnly([
                        .. unavailable.Defects.Select(static defect =>
                            new RecapOperationReadinessDefect(
                                defect.Code,
                                defect.Detail
                            )
                        )
                    ]),
                    Enrich(
                        unavailable.Configuration,
                        concreteCapabilities
                    )
                ),
            _ => throw new InvalidDataException(
                "Unknown DerivedRecap preparation result."
            )
        };
    }

    private static RecapOperationReadinessResult.Ready Ready(
        PreparedRecapOperationAuthority authority,
        RecapMaintainerProfileCatalog concreteCapabilities
    ) => new(
        authority,
        concreteCapabilities,
        authority is PreparedRecapOperationAuthority.NewPlanning planning
            ? RecapCliCompositionResolver.Enrich(
                planning.Configuration,
                concreteCapabilities
            )
            : null
    );

    private static ResolvedRecapPlannerComposition? Enrich(
        ResolvedRecapPlanningConfiguration? configuration,
        RecapMaintainerProfileCatalog concreteCapabilities
    ) => configuration is null
        ? null
        : RecapCliCompositionResolver.Enrich(
            configuration,
            concreteCapabilities
        );

    private static RecapOperationReadinessResult.Blocked Blocked(
        string code,
        string detail,
        ResolvedRecapPlannerComposition? composition = null
    ) => new(
        [new RecapOperationReadinessDefect(code, detail)],
        composition
    );
}
