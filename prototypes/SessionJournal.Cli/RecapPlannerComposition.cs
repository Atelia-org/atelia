using Atelia.SessionJournal.DerivedRecap.Maintainers;
using Atelia.SessionJournal.DerivedRecap.Planner;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.Cli;

internal sealed record ResolvedActiveRecapProfile(
    string ProfileName,
    RecapMaintainerProfileDescriptor Capability,
    RecapBlockCatalogEntry CatalogEntry
);

/// <summary>
/// CLI-only enrichment of the neutral public planning configuration with the
/// complete concrete capability catalog used for reports and execution.
/// </summary>
internal sealed class ResolvedRecapPlannerComposition {
    internal ResolvedRecapPlannerComposition(
        ResolvedRecapPlanningConfiguration configuration,
        IReadOnlyList<ResolvedActiveRecapProfile> activeProfiles,
        RecapMaintainerProfileCatalog capabilityCatalog
    ) {
        Configuration = configuration
            ?? throw new ArgumentNullException(nameof(configuration));
        ArgumentNullException.ThrowIfNull(activeProfiles);
        ActiveProfiles = Array.AsReadOnly([.. activeProfiles]);
        CapabilityCatalog = capabilityCatalog
            ?? throw new ArgumentNullException(
                nameof(capabilityCatalog)
            );
    }

    internal ResolvedRecapPlanningConfiguration Configuration {
        get;
    }
    internal RecapPlannerConfigSnapshot Snapshot =>
        Configuration.Snapshot;
    internal RecapPlanningInputs PlanningInputs =>
        Configuration.PlanningInputs;
    internal RecapPlanningLimits PlanningLimits =>
        Configuration.PlanningLimits;
    internal IReadOnlyList<ResolvedActiveRecapProfile>
        ActiveProfiles { get; }
    internal RecapMaintainerProfileCatalog CapabilityCatalog { get; }
}

internal abstract record RecapCliCompositionResolveResult {
    private RecapCliCompositionResolveResult() {
    }

    internal sealed record Resolved(
        ResolvedRecapPlannerComposition Composition
    ) : RecapCliCompositionResolveResult;

    internal sealed record Invalid(
        IReadOnlyList<RecapPlannerConfigResolveDefect> Defects
    ) : RecapCliCompositionResolveResult;

    internal sealed record Unavailable(string Reason)
        : RecapCliCompositionResolveResult;
}

internal abstract record RecapPlannerCompositionLoadResult {
    private RecapPlannerCompositionLoadResult() {
    }

    internal sealed record Resolved(
        ResolvedRecapPlannerComposition Composition
    ) : RecapPlannerCompositionLoadResult;

    internal sealed record Missing(string Path)
        : RecapPlannerCompositionLoadResult;

    internal sealed record Invalid(
        string Path,
        IReadOnlyList<RecapPlannerCompositionLoadDefect> Defects,
        RecapPlannerConfigSnapshot? Snapshot = null
    ) : RecapPlannerCompositionLoadResult;

    internal sealed record Unavailable(
        string Path,
        string Reason,
        RecapPlannerConfigSnapshot? Snapshot = null
    ) : RecapPlannerCompositionLoadResult;
}

internal sealed record RecapPlannerCompositionLoadDefect(
    string Code,
    string Detail
);

internal static class RecapPlannerCompositionLoader {
    internal static RecapPlannerCompositionLoadResult Load(
        string repositoryRoot
    ) {
        RecapPlannerConfigLoadResult loaded =
            RecapPlannerConfigLoader.Load(repositoryRoot);
        return loaded switch {
            RecapPlannerConfigLoadResult.Available available =>
                Resolve(available),
            RecapPlannerConfigLoadResult.Missing missing =>
                new RecapPlannerCompositionLoadResult.Missing(
                    missing.Path
                ),
            RecapPlannerConfigLoadResult.Invalid invalid =>
                new RecapPlannerCompositionLoadResult.Invalid(
                    invalid.Path,
                    Map(invalid.Defects)
                ),
            RecapPlannerConfigLoadResult.Unavailable unavailable =>
                new RecapPlannerCompositionLoadResult.Unavailable(
                    unavailable.Path,
                    unavailable.Reason
                ),
            _ => throw new InvalidDataException(
                "Unknown planner config load result."
            )
        };
    }

    private static RecapPlannerCompositionLoadResult Resolve(
        RecapPlannerConfigLoadResult.Available available
    ) {
        RecapPlannerConfigSnapshot snapshot =
            RecapPlannerConfigSnapshot.FromAvailable(available);
        RecapCliCompositionResolveResult resolved =
            RecapCliCompositionResolver.Resolve(snapshot);
        return resolved switch {
            RecapCliCompositionResolveResult.Resolved success =>
                new RecapPlannerCompositionLoadResult.Resolved(
                    success.Composition
                ),
            RecapCliCompositionResolveResult.Invalid invalid =>
                new RecapPlannerCompositionLoadResult.Invalid(
                    available.Path,
                    Map(invalid.Defects),
                    snapshot
                ),
            RecapCliCompositionResolveResult.Unavailable unavailable =>
                new RecapPlannerCompositionLoadResult.Unavailable(
                    available.Path,
                    unavailable.Reason,
                    snapshot
                ),
            _ => throw new InvalidDataException(
                "Unknown planner config resolve result."
            )
        };
    }

    private static IReadOnlyList<RecapPlannerCompositionLoadDefect> Map(
        IEnumerable<RecapPlannerConfigDefect> defects
    ) => Array.AsReadOnly([
        .. defects.Select(static defect =>
            new RecapPlannerCompositionLoadDefect(
                defect.Code,
                defect.Detail
            )
        )
    ]);

    private static IReadOnlyList<RecapPlannerCompositionLoadDefect> Map(
        IEnumerable<RecapPlannerConfigResolveDefect> defects
    ) => Array.AsReadOnly([
        .. defects.Select(static defect =>
            new RecapPlannerCompositionLoadDefect(
                defect.Code,
                defect.Detail
            )
        )
    ]);
}

/// <summary>
/// Thin CLI adapter: projects concrete Maintainers metadata into the public
/// resolver, then joins resolved profile names back to concrete descriptors.
/// </summary>
internal static class RecapCliCompositionResolver {
    internal static RecapCliCompositionResolveResult Resolve(
        RecapPlannerConfigSnapshot snapshot
    ) {
        ArgumentNullException.ThrowIfNull(snapshot);
        try {
            return Resolve(
                snapshot,
                RecapMaintainerProfileCatalog.BuiltIn
            );
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or InvalidOperationException
                or ArgumentException
                or IOException
                or UnauthorizedAccessException
        ) {
            return new RecapCliCompositionResolveResult.Unavailable(
                exception.Message
            );
        }
    }

    internal static RecapCliCompositionResolveResult Resolve(
        RecapPlannerConfigSnapshot snapshot,
        RecapMaintainerProfileCatalog concreteCapabilities
    ) {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(concreteCapabilities);

        RecapMaintainerCapabilitySnapshot neutralCapabilities =
            ProjectCapabilities(concreteCapabilities);
        RecapPlannerConfigResolveResult resolved =
            RecapPlannerConfigResolver.Resolve(
                snapshot,
                RecapPlannerConfigResolutionCatalog.BuiltIn,
                neutralCapabilities
            );
        return resolved switch {
            RecapPlannerConfigResolveResult.Resolved success =>
                new RecapCliCompositionResolveResult.Resolved(
                    Enrich(
                        success.Configuration,
                        concreteCapabilities
                    )
                ),
            RecapPlannerConfigResolveResult.Invalid invalid =>
                new RecapCliCompositionResolveResult.Invalid(
                    invalid.Defects
                ),
            _ => throw new InvalidDataException(
                "Unknown public planner config resolve result."
            )
        };
    }

    internal static RecapMaintainerCapabilitySnapshot
        ProjectCapabilities(
        RecapMaintainerProfileCatalog concreteCapabilities
    ) => new([
        .. concreteCapabilities.All.Select(static descriptor =>
            new RecapProfilePlanningDescriptor(
                descriptor.ProfileName,
                new RecapBlockId(descriptor.RecapBlockIdValue),
                descriptor.Target,
                descriptor.MaintainerId,
                descriptor.CapabilityFingerprint
            )
        )
    ]);

    internal static ResolvedRecapPlannerComposition Enrich(
        ResolvedRecapPlanningConfiguration configuration,
        RecapMaintainerProfileCatalog concreteCapabilities
    ) {
        var active = new List<ResolvedActiveRecapProfile>(
            configuration.ActiveProfiles.Count
        );
        foreach (Atelia.SessionJournal.DerivedRecap.Planner
            .ResolvedActiveRecapProfile profile
            in configuration.ActiveProfiles) {
            if (!concreteCapabilities.TryResolveProfileName(
                    profile.ProfileName,
                    out RecapMaintainerProfileDescriptor descriptor
                )) {
                throw new InvalidDataException(
                    "Concrete capability catalog changed while "
                    + $"resolving profile '{profile.ProfileName}'."
                );
            }
            active.Add(new ResolvedActiveRecapProfile(
                profile.ProfileName,
                descriptor,
                profile.CatalogEntry
            ));
        }
        return new ResolvedRecapPlannerComposition(
            configuration,
            active,
            concreteCapabilities
        );
    }
}

internal static class BuiltInRecapPlannerConfig {
    private static readonly Lazy<ResolvedRecapPlannerComposition>
        ResolvedSnapshot = new(
            CreateResolved,
            LazyThreadSafetyMode.ExecutionAndPublication
        );

    internal static RecapPlannerConfigDocument Document { get; } = new(
        RecapPlannerConfigCodec.SchemaV2,
        RecapPlanningPolicyIds.BoundedMaintainAllV1,
        new RecapCadenceConfigDocument(
            O200kBaseHistoryUnitLoadEstimator.EstimatorId,
            MinimumRecentHistoryLoad: 18_000,
            RecapBuildIntervalHistoryLoad: 21_000
        ),
        Array.AsReadOnly([
            new RecapPlannerCatalogEntryDocument(
                RecapMaintainerProfileCatalog
                    .WorldUnderstandingRewrite,
                MaxContentUtf8Bytes: 32_768
            ),
            new RecapPlannerCatalogEntryDocument(
                RecapMaintainerProfileCatalog
                    .AutobiographicalRewrite,
                MaxContentUtf8Bytes: 32_768
            )
        ]),
        new RecapPlannerLimitsDocument(
            MaxRawGrowthEventCount: 512,
            MaxRouteEndpointsPerBlock: 4,
            MaxMaintainerCallsPerBuild: 8,
            MaxRawEventsPerStep: 64,
            MaxRawEventsPerBuild: 512
        )
    );

    internal static ResolvedRecapPlannerComposition Composition =>
        ResolvedSnapshot.Value;

    private static ResolvedRecapPlannerComposition CreateResolved() {
        RecapCliCompositionResolveResult resolved =
            RecapCliCompositionResolver.Resolve(
                RecapPlannerConfigSnapshot.FromDocument(Document)
            );
        return resolved switch {
            RecapCliCompositionResolveResult.Resolved success =>
                success.Composition,
            RecapCliCompositionResolveResult.Invalid invalid =>
                throw new InvalidOperationException(
                    "Built-in recap planner config is invalid: "
                    + string.Join(
                        "; ",
                        invalid.Defects.Select(static defect =>
                            $"{defect.Code}: {defect.Detail}"
                        )
                    )
                ),
            RecapCliCompositionResolveResult.Unavailable unavailable =>
                throw new InvalidOperationException(
                    "Built-in recap planner capabilities are "
                    + $"unavailable: {unavailable.Reason}"
                ),
            _ => throw new InvalidOperationException(
                "Unknown built-in recap planner resolution result."
            )
        };
    }
}
