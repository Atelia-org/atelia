using System.Collections.Immutable;
using Atelia.SessionJournal.DerivedRecap.Maintainers;
using Atelia.SessionJournal.DerivedRecap.Planner;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.Cli;

internal sealed class RecapPlannerConfigSnapshot {
    private RecapPlannerConfigSnapshot(
        string? canonicalPath,
        RecapPlannerConfigDocument document,
        ImmutableArray<byte> canonicalBytes,
        string configSha256
    ) {
        CanonicalPath = canonicalPath;
        Document = document;
        CanonicalBytes = canonicalBytes;
        ConfigSha256 = configSha256;
    }

    internal string? CanonicalPath { get; }
    internal RecapPlannerConfigDocument Document { get; }
    internal ImmutableArray<byte> CanonicalBytes { get; }
    internal string ConfigSha256 { get; }

    internal static RecapPlannerConfigSnapshot FromDocument(
        RecapPlannerConfigDocument document
    ) {
        byte[] canonical =
            RecapPlannerConfigCodec.EncodeCanonical(document);
        return new RecapPlannerConfigSnapshot(
            null,
            document,
            ImmutableArray.CreateRange(canonical),
            RecapPlannerConfigCodec.ComputeSha256(canonical)
        );
    }

    internal static RecapPlannerConfigSnapshot FromAvailable(
        RecapPlannerConfigLoadResult.Available available
    ) => new(
        available.Path,
        available.Document,
        available.CanonicalBytes,
        available.ConfigSha256
    );
}

internal sealed record ResolvedActiveRecapProfile(
    string ProfileName,
    RecapMaintainerProfileDescriptor Capability,
    RecapBlockCatalogEntry CatalogEntry
);

internal sealed class ResolvedRecapPlannerComposition {
    internal ResolvedRecapPlannerComposition(
        RecapPlannerConfigSnapshot snapshot,
        RecapPlanningInputs planningInputs,
        RecapPlanningLimits planningLimits,
        IReadOnlyList<ResolvedActiveRecapProfile> activeProfiles,
        RecapMaintainerProfileCatalog capabilityCatalog
    ) {
        Snapshot = snapshot
            ?? throw new ArgumentNullException(nameof(snapshot));
        PlanningInputs = planningInputs
            ?? throw new ArgumentNullException(nameof(planningInputs));
        PlanningLimits = planningLimits
            ?? throw new ArgumentNullException(nameof(planningLimits));
        ArgumentNullException.ThrowIfNull(activeProfiles);
        ActiveProfiles = Array.AsReadOnly([.. activeProfiles]);
        CapabilityCatalog = capabilityCatalog
            ?? throw new ArgumentNullException(
                nameof(capabilityCatalog)
            );
    }

    internal RecapPlannerConfigSnapshot Snapshot { get; }
    internal RecapPlanningInputs PlanningInputs { get; }
    internal RecapPlanningLimits PlanningLimits { get; }
    internal IReadOnlyList<ResolvedActiveRecapProfile>
        ActiveProfiles { get; }
    internal RecapMaintainerProfileCatalog CapabilityCatalog { get; }
}

internal sealed record RecapPlannerConfigResolveDefect(
    string Code,
    string Detail
);

internal static class RecapPlannerConfigResolveDefectCodes {
    internal const string UnknownPolicy = nameof(UnknownPolicy);
    internal const string UnknownEstimator = nameof(UnknownEstimator);
    internal const string UnknownProfile = nameof(UnknownProfile);
    internal const string DuplicateResolvedBlock =
        nameof(DuplicateResolvedBlock);
    internal const string DuplicateResolvedTarget =
        nameof(DuplicateResolvedTarget);
    internal const string InvalidPlanningAuthority =
        nameof(InvalidPlanningAuthority);
}

internal abstract record RecapPlannerConfigResolveResult {
    private RecapPlannerConfigResolveResult() {
    }

    internal sealed record Resolved(
        ResolvedRecapPlannerComposition Composition
    ) : RecapPlannerConfigResolveResult;

    internal sealed record Invalid(
        IReadOnlyList<RecapPlannerConfigResolveDefect> Defects
    ) : RecapPlannerConfigResolveResult;

    internal sealed record Unavailable(string Reason)
        : RecapPlannerConfigResolveResult;
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
        RecapPlannerConfigResolveResult resolved =
            RecapPlannerCompositionResolver.Resolve(snapshot);
        return resolved switch {
            RecapPlannerConfigResolveResult.Resolved success =>
                new RecapPlannerCompositionLoadResult.Resolved(
                    success.Composition
                ),
            RecapPlannerConfigResolveResult.Invalid invalid =>
                new RecapPlannerCompositionLoadResult.Invalid(
                    available.Path,
                    Map(invalid.Defects),
                    snapshot
                ),
            RecapPlannerConfigResolveResult.Unavailable unavailable =>
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

internal static class RecapPlanningPolicyRegistry {
    internal static bool TryResolve(
        string policyId,
        out IRecapPlanningPolicy policy
    ) {
        if (string.Equals(
                policyId,
                RecapPlanningPolicyIds.BoundedMaintainAllV1,
                StringComparison.Ordinal
            )) {
            policy = new BoundedMaintainAllRecapPlanningPolicy();
            return true;
        }

        policy = null!;
        return false;
    }
}

internal static class RecapPlannerCompositionResolver {
    internal static RecapPlannerConfigResolveResult Resolve(
        RecapPlannerConfigSnapshot snapshot
    ) {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!RecapPlanningPolicyRegistry.TryResolve(
                snapshot.Document.PlanningPolicy,
                out IRecapPlanningPolicy policy
            )) {
            return UnknownPolicy(snapshot);
        }
        HistoryUnitLoadEstimatorResolutionResult estimatorResolution =
            HistoryUnitLoadEstimatorRegistry.Resolve(
                snapshot.Document.Cadence
                    .HistoryUnitLoadEstimatorId
            );
        if (estimatorResolution
            is HistoryUnitLoadEstimatorResolutionResult.Invalid
                invalidEstimator) {
            return Invalid(
                RecapPlannerConfigResolveDefectCodes.UnknownEstimator,
                invalidEstimator.Defect.Detail
            );
        }
        var estimator =
            (HistoryUnitLoadEstimatorResolutionResult.Resolved)
                estimatorResolution;
        RecapMaintainerProfileCatalog capabilities;
        try {
            capabilities = RecapMaintainerProfileCatalog.BuiltIn;
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or InvalidOperationException
                or ArgumentException
                or IOException
                or UnauthorizedAccessException
        ) {
            return new RecapPlannerConfigResolveResult.Unavailable(
                exception.Message
            );
        }
        return ResolveCore(
            snapshot,
            capabilities,
            estimator.Estimator,
            policy
        );
    }

    internal static RecapPlannerConfigResolveResult Resolve(
        RecapPlannerConfigSnapshot snapshot,
        RecapMaintainerProfileCatalog capabilities
    ) {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (!RecapPlanningPolicyRegistry.TryResolve(
                snapshot.Document.PlanningPolicy,
                out IRecapPlanningPolicy policy
            )) {
            return UnknownPolicy(snapshot);
        }
        HistoryUnitLoadEstimatorResolutionResult estimatorResolution =
            HistoryUnitLoadEstimatorRegistry.Resolve(
                snapshot.Document.Cadence
                    .HistoryUnitLoadEstimatorId
            );
        if (estimatorResolution
            is HistoryUnitLoadEstimatorResolutionResult.Invalid
                invalidEstimator) {
            return Invalid(
                RecapPlannerConfigResolveDefectCodes.UnknownEstimator,
                invalidEstimator.Defect.Detail
            );
        }
        return ResolveCore(
            snapshot,
            capabilities,
            ((HistoryUnitLoadEstimatorResolutionResult.Resolved)
                estimatorResolution).Estimator,
            policy
        );
    }

    private static RecapPlannerConfigResolveResult ResolveCore(
        RecapPlannerConfigSnapshot snapshot,
        RecapMaintainerProfileCatalog capabilities,
        IHistoryUnitLoadEstimator historyUnitLoadEstimator,
        IRecapPlanningPolicy policy
    ) {
        var active = new List<ResolvedActiveRecapProfile>(
            snapshot.Document.Catalog.Count
        );
        var blockIds = new HashSet<RecapBlockId>();
        var targets = new HashSet<ContextHeaderBlockPath>();
        foreach (RecapPlannerCatalogEntryDocument entry
                 in snapshot.Document.Catalog) {
            if (!capabilities.TryResolveProfileName(
                    entry.MaintainerProfile,
                    out RecapMaintainerProfileDescriptor descriptor
                )) {
                return Invalid(
                    RecapPlannerConfigResolveDefectCodes.UnknownProfile,
                    $"Unknown recap maintainer profile "
                    + $"'{entry.MaintainerProfile}'."
                );
            }

            var blockId = new RecapBlockId(
                descriptor.RecapBlockIdValue
            );
            if (!blockIds.Add(blockId)) {
                return Invalid(
                    RecapPlannerConfigResolveDefectCodes
                        .DuplicateResolvedBlock,
                    $"Profile '{entry.MaintainerProfile}' resolves to "
                    + $"duplicate RecapBlockId '{blockId}'."
                );
            }
            if (!targets.Add(descriptor.Target)) {
                return Invalid(
                    RecapPlannerConfigResolveDefectCodes
                        .DuplicateResolvedTarget,
                    $"Profile '{entry.MaintainerProfile}' resolves to "
                    + $"duplicate target '{descriptor.Target}'."
                );
            }

            var catalogEntry = new RecapBlockCatalogEntry(
                blockId,
                descriptor.Target,
                descriptor.MaintainerId,
                entry.MaxContentUtf8Bytes
            );
            active.Add(new ResolvedActiveRecapProfile(
                entry.MaintainerProfile,
                descriptor,
                catalogEntry
            ));
        }

        try {
            var cadence = new RecapCadenceConfig(
                snapshot.Document.Cadence
                    .HistoryUnitLoadEstimatorId,
                new HistoryLoadUnit(
                snapshot.Document.Cadence
                    .MinimumRecentHistoryLoad
                ),
                new HistoryLoadUnit(
                snapshot.Document.Cadence
                    .RecapBuildIntervalHistoryLoad
                )
            );
            var inputs = new RecapPlanningInputs(
                Array.AsReadOnly([
                    .. active.Select(
                        static binding => binding.CatalogEntry
                    )
                ]),
                cadence,
                historyUnitLoadEstimator,
                policy
            );
            RecapPlannerLimitsDocument sourceLimits =
                snapshot.Document.Limits;
            var limits = new RecapPlanningLimits(
                sourceLimits.MaxRawGrowthEventCount,
                sourceLimits.MaxRouteEndpointsPerBlock,
                sourceLimits.MaxMaintainerCallsPerBuild,
                sourceLimits.MaxRawEventsPerStep,
                sourceLimits.MaxRawEventsPerBuild
            );
            RecapProtocolHardCaps.V4.ValidatePlanningAuthority(
                inputs,
                limits
            );
            return new RecapPlannerConfigResolveResult.Resolved(
                new ResolvedRecapPlannerComposition(
                    snapshot,
                    inputs,
                    limits,
                    active,
                    capabilities
                )
            );
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or OverflowException
        ) {
            return Invalid(
                RecapPlannerConfigResolveDefectCodes
                    .InvalidPlanningAuthority,
                exception.Message
            );
        }
    }

    private static RecapPlannerConfigResolveResult.Invalid Invalid(
        string code,
        string detail
    ) => new(Array.AsReadOnly([
        new RecapPlannerConfigResolveDefect(code, detail)
    ]));

    private static RecapPlannerConfigResolveResult.Invalid
        UnknownPolicy(RecapPlannerConfigSnapshot snapshot) =>
        Invalid(
            RecapPlannerConfigResolveDefectCodes.UnknownPolicy,
            $"Unknown recap planning policy "
            + $"'{snapshot.Document.PlanningPolicy}'."
        );
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
        RecapPlannerConfigResolveResult resolved =
            RecapPlannerCompositionResolver.Resolve(
                RecapPlannerConfigSnapshot.FromDocument(Document)
            );
        return resolved switch {
            RecapPlannerConfigResolveResult.Resolved success =>
                success.Composition,
            RecapPlannerConfigResolveResult.Invalid invalid =>
                throw new InvalidOperationException(
                    "Built-in recap planner config is invalid: "
                    + string.Join(
                        "; ",
                        invalid.Defects.Select(static defect =>
                            $"{defect.Code}: {defect.Detail}"
                        )
                    )
                ),
            RecapPlannerConfigResolveResult.Unavailable unavailable =>
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
