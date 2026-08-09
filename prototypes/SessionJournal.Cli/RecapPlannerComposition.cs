using Atelia.SessionJournal.DerivedRecap.Maintainers;
using Atelia.SessionJournal.DerivedRecap.Planner;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.Cli;

internal sealed class ResolvedRecapPlannerComposition {
    internal ResolvedRecapPlannerComposition(
        RecapEpochPlanningConfiguration configuration,
        RecapEpochOperationLimits operationLimits,
        DerivedRecapEpochStoreLimits storeLimits,
        RecapMaintainerProfileCatalog capabilityCatalog
    ) {
        Configuration = configuration;
        OperationLimits = operationLimits;
        StoreLimits = storeLimits;
        CapabilityCatalog = capabilityCatalog;
    }

    internal RecapEpochPlanningConfiguration Configuration { get; }
    internal RecapEpochOperationLimits OperationLimits { get; }
    internal DerivedRecapEpochStoreLimits StoreLimits { get; }
    internal RecapMaintainerProfileCatalog CapabilityCatalog { get; }
}

internal static class BuiltInRecapPlannerConfig {
    private static readonly Lazy<ResolvedRecapPlannerComposition>
        ResolvedSnapshot = new(
            CreateResolved,
            LazyThreadSafetyMode.ExecutionAndPublication
        );

    internal static ResolvedRecapPlannerComposition Composition =>
        ResolvedSnapshot.Value;

    internal static ResolvedRecapPlannerComposition Load(
        string repositoryRoot
    ) => RecapEpochConfigLoader.TryLoad(
        repositoryRoot,
        out RecapEpochConfigDocument document
    )
        ? Resolve(document)
        : Composition;

    internal static RecapEpochConfigDocument Document { get; } = new(
        RecapEpochConfigCodec.SchemaV3,
        MaintainCompleteRosterEpochPolicy.PolicyId,
        new RecapEpochCadenceConfigDocument(
            O200kBaseHistoryUnitLoadEstimator.EstimatorId,
            MinimumRecentHistoryLoad: 18_000,
            RecapBuildIntervalHistoryLoad: 21_000
        ),
        Array.AsReadOnly([
            new RecapEpochCatalogEntryDocument(
                RecapMaintainerProfileCatalog.WorldUnderstandingRewrite,
                MaxContentUtf8Bytes: 32_768
            ),
            new RecapEpochCatalogEntryDocument(
                RecapMaintainerProfileCatalog.AutobiographicalRewrite,
                MaxContentUtf8Bytes: 32_768
            )
        ]),
        new RecapEpochLimitsDocument(
            MaxRawGrowthEventCount: 512,
            MaxRawEventsPerEpoch: 512,
            MaxMaintainerCallsPerEpoch: 2,
            MaxEpochsPerOperation: 4,
            MaxMaintainerCallsPerOperation: 8,
            MaxRecapBlockCount: 2,
            MaxRebuildForwardRangeEventCount: 65_536,
            MaxTotalRecapPackUtf8Bytes: 2 * 1024 * 1024,
            MaxCanonicalPriorPackBytes: 5 * 1024 * 1024,
            MaxEpochInputBytes: 8 * 1024 * 1024,
            MaxManifestBytes: 2 * 1024 * 1024,
            MaxFinalBlockBytes: 512 * 1024,
            MaxPublicationBytes: 3 * 1024 * 1024
        )
    );

    internal static ResolvedRecapPlannerComposition Resolve(
        RecapEpochConfigDocument document
    ) {
        _ = RecapEpochConfigCodec.Encode(document);
        RecapMaintainerProfileCatalog capabilities =
            RecapMaintainerProfileCatalog.BuiltIn;
        RecapBlockCatalogEntry[] roster = [
            .. document.Catalog
                .Select(entry => (
                    Entry: entry,
                    Descriptor: capabilities.Resolve(entry.ProfileName)
                ))
                .Select(static pair => new RecapBlockCatalogEntry(
                    new RecapBlockId(pair.Descriptor.RecapBlockIdValue),
                    pair.Descriptor.Target,
                    pair.Descriptor.MaintainerId,
                    pair.Descriptor.CapabilityFingerprint,
                    pair.Entry.MaxContentUtf8Bytes
                ))
                .OrderBy(static entry => entry.Target.Carrier)
                .ThenBy(
                    static entry => entry.Target.BlockKey,
                    StringComparer.Ordinal
                )
        ];
        RecapEpochLimitsDocument configured = document.Limits;
        var operationLimits = new RecapEpochOperationLimits(
            configured.MaxRawGrowthEventCount,
            configured.MaxRawEventsPerEpoch,
            configured.MaxMaintainerCallsPerEpoch,
            configured.MaxEpochsPerOperation,
            configured.MaxMaintainerCallsPerOperation,
            configured.MaxRecapBlockCount,
            configured.MaxRebuildForwardRangeEventCount
        );
        var storeLimits = new DerivedRecapEpochStoreLimits(
            configured.MaxRecapBlockCount,
            configured.MaxTotalRecapPackUtf8Bytes,
            configured.MaxCanonicalPriorPackBytes,
            configured.MaxEpochInputBytes,
            configured.MaxManifestBytes,
            configured.MaxFinalBlockBytes,
            configured.MaxPublicationBytes
        );
        DerivedRecapEpochStoreLimits hardStoreLimits =
            CreateStoreLimits(Document.Limits);
        if (storeLimits != hardStoreLimits) {
            throw new InvalidDataException(
                "Configured recap Store aggregate limits must match the "
                + "binary hard limits; changing them requires an explicit "
                + "Store generation/reset decision."
            );
        }
        return new ResolvedRecapPlannerComposition(
            new RecapEpochPlanningConfiguration(
                roster,
                new RecapCadenceConfig(
                    document.Cadence.HistoryUnitLoadEstimatorId,
                    new HistoryLoadUnit(
                        document.Cadence.MinimumRecentHistoryLoad
                    ),
                    new HistoryLoadUnit(
                        document.Cadence.RecapBuildIntervalHistoryLoad
                    )
                ),
                new O200kBaseHistoryUnitLoadEstimator()
            ),
            operationLimits,
            storeLimits,
            capabilities
        );
    }

    private static ResolvedRecapPlannerComposition CreateResolved()
        => Resolve(Document);

    private static DerivedRecapEpochStoreLimits CreateStoreLimits(
        RecapEpochLimitsDocument limits
    ) => new(
        limits.MaxRecapBlockCount,
        limits.MaxTotalRecapPackUtf8Bytes,
        limits.MaxCanonicalPriorPackBytes,
        limits.MaxEpochInputBytes,
        limits.MaxManifestBytes,
        limits.MaxFinalBlockBytes,
        limits.MaxPublicationBytes
    );
}
