using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Diagnostics;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.DerivedRecap.Abstractions;
using Atelia.SessionJournal.DerivedRecap.Maintainers;
using Atelia.SessionJournal.DerivedRecap.Planner;
using Atelia.SessionJournal.DerivedRecap.Runtime;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.Galatea.Server;

internal sealed record GalateaPreparedRecap(
    DerivedRecapEpochStore Store,
    Func<RecapEpochActiveConfiguration> ConfigurationFactory,
    RecapEpochOperationLimits Limits,
    RecapMaintainerProfileCatalog CapabilityCatalog,
    EventAddress CapturedRawHead
);

/// <summary>
/// Galatea's v8 shared-epoch composition root. Runtime clients remain lazy;
/// preparation only validates/creates the direct-final Store and freezes the
/// complete built-in roster/configuration for this online operation.
/// </summary>
internal static class GalateaRecapComposition {
    internal const string ExactFreshness = "exact";
    internal const string StaleFreshness = "stale";
    internal const string BelowCadenceThresholdState =
        "below-cadence-threshold";
    internal const string AwaitingReplaySafeAdmissionState =
        "awaiting-replay-safe-admission";
    internal const string CadenceReadyState = "cadence-ready";
    internal const string FrozenBuildingState = "frozen-building";
    internal const string RawSafetyRejectedState = "raw-safety-rejected";
    internal const string UnavailableState = "unavailable";
    internal const string NotObservedState = "not-observed";

    internal static async ValueTask<RecapPlanningSnapshotDto>
        InspectPlanningAsync(
        SessionJournalEngine engine,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        try {
            DerivedRecapEpochStore store = DerivedRecapEpochStore.Open(
                engine.Path,
                engine.BranchRefId
            );
            RecapEpochBuildingSelectionResult building =
                await store.SelectBuildingAsync(cancellationToken)
                    .ConfigureAwait(false);
            if (building is RecapEpochBuildingSelectionResult.Selected selected) {
                return new RecapPlanningSnapshotDto(
                    ExactFreshness,
                    FrozenBuildingState,
                    ObservedRawHead: EventAddressTextCodec.FormatNullable(
                        engine.ReadView.ReadCurrentHead()
                    ),
                    CadenceBaseline: EventAddressTextCodec.Format(
                        selected.Snapshot.EpochInput.StartBoundary.Address
                    ),
                    Detail: "存在v8 shared-epoch Building；下次lifecycle将恢复健康final并补齐pending roster。"
                );
            }
            if (building is RecapEpochBuildingSelectionResult.Invalid invalid) {
                return UnavailablePlanningSnapshot(
                    "building-invalid",
                    invalid.Detail
                );
            }
            return new RecapPlanningSnapshotDto(
                ExactFreshness,
                NotObservedState,
                ObservedRawHead: EventAddressTextCodec.FormatNullable(
                    engine.ReadView.ReadCurrentHead()
                ),
                Detail: "v8 Store可用；精确HistoryLoad由下一次shared-epoch planning pass判定。"
            );
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested
        ) {
            throw;
        }
        catch (Exception exception) when (
            GalateaExceptionClassifier.IsNonFatal(exception)
        ) {
            DebugUtil.Warning(
                "Galatea.Recap",
                "Read-only v8 DerivedRecap inspection failed.",
                exception
            );
            return UnavailablePlanningSnapshot(
                "recap-planning-inspection-failed",
                "DerivedRecap v8进度暂时不可用。"
            );
        }
    }

    internal static async ValueTask<GalateaPreparedRecap> PrepareAsync(
        SessionJournalEngine engine,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        RecapMaintainerProfileCatalog catalog =
            RecapMaintainerProfileCatalog.BuiltIn;
        RecapEpochConfigDocument defaults = CreateDefaultDocument();
        RecapEpochOperationLimits recoveryLimits =
            CreateOperationLimits(defaults);
        DerivedRecapEpochStoreLimits storeLimits =
            CreateStoreLimits(defaults);
        EventAddress capturedRawHead = engine.ReadView.ReadCurrentHead()
            ?? throw new InvalidOperationException(
                "DerivedRecap preparation requires a non-empty raw head."
            );
        DerivedRecapEpochStore store = DerivedRecapEpochStore.Open(
            engine.Path,
            engine.BranchRefId,
            storeLimits
        );
        await store.EnsureCreatedAsync(cancellationToken)
            .ConfigureAwait(false);
        if (engine.ReadView.ReadCurrentHead() != capturedRawHead) {
            throw new InvalidOperationException(
                "Raw head changed while preparing DerivedRecap v8."
            );
        }
        return new GalateaPreparedRecap(
            store,
            () => {
                RecapEpochConfigDocument active =
                    RecapEpochConfigLoader.TryLoad(
                        engine.Path,
                        out RecapEpochConfigDocument loaded
                    )
                        ? loaded
                        : defaults;
                return new RecapEpochActiveConfiguration(
                    CreatePlanningConfiguration(catalog, active),
                    CreateOperationLimits(active),
                    CreateStoreLimits(active)
                );
            },
            recoveryLimits,
            catalog,
            capturedRawHead
        );
    }

    internal static DerivedRecapOnlineLifecycleCoordinator
        CreateLifecycle(
        SessionJournalEngine engine,
        GalateaPreparedRecap prepared,
        CompletionConnectionRegistry connections,
        CompletionConnectionConfig agentConnection,
        IReadOnlyDictionary<string, string>?
            recapMaintainerConnections,
        string? callLogDirectory,
        RecapExecutionLaneInterner lanes,
        RecapRuntimeGroupInterner groups
    ) {
        IRecapBlockMaintainerRegistry maintainers =
            CreateMaintainerRegistry(
                prepared.CapabilityCatalog,
                connections,
                agentConnection,
                recapMaintainerConnections,
                callLogDirectory,
                lanes,
                groups
            );
        return new DerivedRecapOnlineLifecycleCoordinator(
            engine.ReadView,
            prepared.Store,
            prepared.ConfigurationFactory,
            prepared.Limits,
            maintainers
        );
    }

    internal static IRecapBlockMaintainerRegistry
        CreateMaintainerRegistry(
        RecapMaintainerProfileCatalog capabilityCatalog,
        CompletionConnectionRegistry connections,
        CompletionConnectionConfig agentConnection,
        IReadOnlyDictionary<string, string>?
            recapMaintainerConnections,
        string? callLogDirectory,
        RecapExecutionLaneInterner lanes,
        RecapRuntimeGroupInterner groups
    ) {
        ArgumentNullException.ThrowIfNull(capabilityCatalog);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(agentConnection);
        ArgumentNullException.ThrowIfNull(lanes);
        ArgumentNullException.ThrowIfNull(groups);
        return new DeferredRecapBlockMaintainerRegistry(
            () => new RecapBlockMaintainerRegistry([
                .. capabilityCatalog.All.Select(descriptor => {
                CompletionConnectionConfig connection =
                    ResolveMaintainerConnection(
                        descriptor,
                        connections,
                        agentConnection,
                        recapMaintainerConnections
                    );
                RecapExecutionLane lane = GalateaCompletionLogging
                    .CreateMaintainerLane(
                        lanes,
                        connections.GetClient(connection.Id),
                        connection,
                        callLogDirectory
                    );
                return groups.GetOrAdd(
                        lane,
                        descriptor.Definition.Family
                    )
                    .Bind(descriptor.Definition);
            })
            ])
        );
    }

    internal static RecapEpochPlanningConfiguration
        CreatePlanningConfiguration(
        RecapMaintainerProfileCatalog catalog,
        RecapEpochConfigDocument? document = null
    ) {
        document ??= CreateDefaultDocument();
        RecapBlockCatalogEntry[] roster = [
            .. document.Catalog
                .Select(entry => (
                    Entry: entry,
                    Descriptor: catalog.Resolve(entry.ProfileName)
                ))
                .Select(static pair => new RecapBlockCatalogEntry(
                    new RecapBlockId(pair.Descriptor.RecapBlockIdValue),
                    pair.Descriptor.Target,
                    pair.Descriptor.MaintainerId,
                    pair.Descriptor.CapabilityFingerprint,
                    pair.Entry.MaxContentUtf8Bytes
                ))
                .OrderBy(static entry => entry.Target.Carrier)
                .ThenBy(static entry => entry.Target.BlockKey,
                    StringComparer.Ordinal)
        ];
        return new RecapEpochPlanningConfiguration(
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
        );
    }

    internal static RecapEpochOperationLimits CreateOperationLimits(
        RecapEpochConfigDocument document
    ) => new(
        document.Limits.MaxRawGrowthEventCount,
        document.Limits.MaxRawEventsPerEpoch,
        document.Limits.MaxMaintainerCallsPerEpoch,
        document.Limits.MaxEpochsPerOperation,
        document.Limits.MaxMaintainerCallsPerOperation,
        document.Limits.MaxRecapBlockCount,
        document.Limits.MaxRebuildForwardRangeEventCount
    );

    internal static DerivedRecapEpochStoreLimits CreateStoreLimits(
        RecapEpochConfigDocument document
    ) => new(
        document.Limits.MaxRecapBlockCount,
        document.Limits.MaxTotalRecapPackUtf8Bytes,
        document.Limits.MaxCanonicalPriorPackBytes,
        document.Limits.MaxEpochInputBytes,
        document.Limits.MaxManifestBytes,
        document.Limits.MaxFinalBlockBytes,
        document.Limits.MaxPublicationBytes
    );

    private static RecapEpochConfigDocument CreateDefaultDocument()
        => new(
            RecapEpochConfigCodec.SchemaV3,
            MaintainCompleteRosterEpochPolicy.PolicyId,
            new RecapEpochCadenceConfigDocument(
                O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                18_000,
                21_000
            ),
            Array.AsReadOnly([
                new RecapEpochCatalogEntryDocument(
                    RecapMaintainerProfileCatalog
                        .WorldUnderstandingRewrite,
                    32_768
                ),
                new RecapEpochCatalogEntryDocument(
                    RecapMaintainerProfileCatalog
                        .AutobiographicalRewrite,
                    32_768
                )
            ]),
            new RecapEpochLimitsDocument(
                512,
                512,
                2,
                4,
                8,
                2,
                65_536,
                2 * 1024 * 1024,
                5 * 1024 * 1024,
                8 * 1024 * 1024,
                2 * 1024 * 1024,
                512 * 1024,
                3 * 1024 * 1024
            )
        );

    private static RecapPlanningSnapshotDto UnavailablePlanningSnapshot(
        string code,
        string detail
    ) => new(
        StaleFreshness,
        UnavailableState,
        Code: code,
        Detail: detail
    );

    private static CompletionConnectionConfig
        ResolveMaintainerConnection(
        RecapMaintainerProfileDescriptor descriptor,
        CompletionConnectionRegistry connections,
        CompletionConnectionConfig agentConnection,
        IReadOnlyDictionary<string, string>?
            recapMaintainerConnections
    ) {
        if (recapMaintainerConnections is null) {
            return agentConnection;
        }
        if (!recapMaintainerConnections.TryGetValue(
                descriptor.MaintainerId,
                out string? connectionId
            )) {
            throw new InvalidOperationException(
                "Validated Galatea recap routing is missing maintainer "
                + $"'{descriptor.MaintainerId}'."
            );
        }
        return connections.TryGet(
            connectionId,
            out CompletionConnectionConfig? connection
        )
            ? connection
            : throw new InvalidOperationException(
                "Validated Galatea recap routing references unknown "
                + $"connection '{connectionId}'."
            );
    }

    internal static SessionCompletionTargetIdentity
        CreateCompletionTarget(
        CompletionConnectionConfig connection,
        ICompletionClient client
    ) {
        CompletionDispatchIdentity identity =
            CompletionDispatchIdentityFactory.Create(connection, client);
        return new SessionCompletionTargetIdentity(
            identity.ConnectionId,
            identity.Kind,
            identity.ConnectionFingerprint,
            identity.RequestAdapterFingerprint
        );
    }
}
