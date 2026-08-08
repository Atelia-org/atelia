using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Diagnostics;
using Atelia.SessionJournal;
using Atelia.SessionJournal.DerivedRecap.Abstractions;
using Atelia.SessionJournal.DerivedRecap.Maintainers;
using Atelia.SessionJournal.DerivedRecap.Planner;
using Atelia.SessionJournal.DerivedRecap.Runtime;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.Galatea.Server;

internal sealed record GalateaPreparedRecap(
    DerivedRecapStore Store,
    PreparedRecapOperationAuthority Authority,
    RecapMaintainerProfileCatalog CapabilityCatalog
);

/// <summary>
/// Galatea's thin composition root over the public Building-first contracts.
/// It projects concrete profile metadata before creating either an agent client
/// or a concrete Maintainer; the latter remain lazy until an actual binding is
/// requested by the lifecycle executor.
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
            RecapMaintainerProfileCatalog catalog =
                RecapMaintainerProfileCatalog.BuiltIn;
            RecapMaintainerCapabilitySnapshot capabilities =
                ProjectCapabilities(catalog);
            DerivedRecapStore store = DerivedRecapStore.Open(
                engine.Path,
                engine.BranchRefId
            );
            var source =
                new RepositoryRecapActivePlanningConfigurationSource(
                    store.SessionRepositoryPath,
                    capabilities
                );
            DerivedRecapPlanningProgressInspectionResult result =
                await DerivedRecapPlanningProgressInspector.InspectAsync(
                        engine.ReadView,
                        store,
                        capabilities,
                        source,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            return MapPlanningInspection(result);
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
                "Read-only DerivedRecap planning inspection failed.",
                exception
            );
            return UnavailablePlanningSnapshot(
                "recap-planning-inspection-failed",
                "DerivedRecap进度暂时不可用。"
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
        RecapMaintainerCapabilitySnapshot capabilities =
            ProjectCapabilities(catalog);
        DerivedRecapStore store = DerivedRecapStore.Open(
            engine.Path,
            engine.BranchRefId
        );
        var source =
            new RepositoryRecapActivePlanningConfigurationSource(
                store.SessionRepositoryPath,
                capabilities
            );
        DerivedRecapOperationPreparationResult result =
            await DerivedRecapOperationPreparer.PrepareAsync(
                    engine.ReadView,
                    store,
                    capabilities,
                    source,
                    cancellationToken
                )
                .ConfigureAwait(false);

        return result switch {
            DerivedRecapOperationPreparationResult.Ready ready =>
                new GalateaPreparedRecap(
                    store,
                    ready.Authority,
                    catalog
                ),
            DerivedRecapOperationPreparationResult.Retryable retryable =>
                throw new GalateaTurnException(
                    "会话在准备前情提要时发生并发变化，请重试。",
                    retryable.Code
                ),
            DerivedRecapOperationPreparationResult.Unavailable unavailable =>
                throw new GalateaTurnException(
                    FormatUnavailableMessage(unavailable.Defects),
                    unavailable.Defects[0].Code
                ),
            DerivedRecapOperationPreparationResult.BeyondPrefix beyond =>
                throw RecapBeyondPrefix(beyond),
            _ => throw new InvalidDataException(
                "Unknown DerivedRecap preparation result."
            )
        };
    }

    private static string FormatUnavailableMessage(
        IReadOnlyList<DerivedRecapOperationPreparationDefect> defects
    ) {
        string message = "会话的前情提要配置或存储当前不可用："
            + string.Join(
                "; ",
                defects.Select(static defect =>
                    $"{defect.Code}: {defect.Detail}"
                )
            );
        if (defects.Any(static defect =>
                defect.Detail.StartsWith(
                    "Unsupported publication schema ",
                    StringComparison.Ordinal
                )
                || defect.Detail.StartsWith(
                    "Unsupported manifest schema ",
                    StringComparison.Ordinal
                ))) {
            message += "。检测到旧版DerivedRecap sidecar；请先用"
                + "SessionJournal.Cli的recap inspect确认，再按Store契约"
                + "显式执行recap reset与recap run重建。";
        }
        return message;
    }

    private static GalateaTurnException RecapBeyondPrefix(
        DerivedRecapOperationPreparationResult.BeyondPrefix beyond
    ) => new(
        "会话的前情提要所需raw lineage超出bounded prefix："
        + $"stage={beyond.Stage}; requiredAnchor="
        + EventAddressTextCodec.FormatNullable(
            beyond.Evidence.RequiredAnchor
        )
        + "; capturedHead="
        + EventAddressTextCodec.Format(beyond.Evidence.CapturedHead)
        + $"; headerCount={beyond.Evidence.HeaderCount}; nextAddress="
        + EventAddressTextCodec.Format(beyond.Evidence.NextAddress)
        + ".",
        "recap-beyond-prefix"
    );

    private static RecapPlanningSnapshotDto MapPlanningInspection(
        DerivedRecapPlanningProgressInspectionResult result
    ) {
        ArgumentNullException.ThrowIfNull(result);
        return result switch {
            DerivedRecapPlanningProgressInspectionResult
                .BelowCadenceThreshold below => MapProgress(
                    below.Snapshot,
                    BelowCadenceThresholdState
                ),
            DerivedRecapPlanningProgressInspectionResult
                .AwaitingReplaySafeAdmission awaiting => MapProgress(
                    awaiting.Snapshot,
                    AwaitingReplaySafeAdmissionState
                ),
            DerivedRecapPlanningProgressInspectionResult
                .CadenceReady ready => MapProgress(
                    ready.Snapshot,
                    CadenceReadyState
                ),
            DerivedRecapPlanningProgressInspectionResult
                .FrozenBuilding frozen => new RecapPlanningSnapshotDto(
                    ExactFreshness,
                    FrozenBuildingState,
                    ObservedRawHead: EventAddressTextCodec.Format(
                        frozen.CapturedRawHead
                    ),
                    Detail: "存在frozen Building；下次lifecycle将尝试恢复。"
                ),
            DerivedRecapPlanningProgressInspectionResult
                .RawSafetyRejected rejected => new RecapPlanningSnapshotDto(
                    ExactFreshness,
                    RawSafetyRejectedState,
                    ObservedRawHead: EventAddressTextCodec.Format(
                        rejected.CapturedRawHead
                    ),
                    CadenceBaseline: EventAddressTextCodec.Format(
                        rejected.CadenceBaseline
                    ),
                    MinimumRecentHistoryLoad: rejected.Cadence
                        .MinimumRecentHistoryLoad.Value,
                    RecapBuildIntervalHistoryLoad: rejected.Cadence
                        .RecapBuildIntervalHistoryLoad.Value,
                    BuildThresholdHistoryLoad: rejected.Cadence
                        .BuildThresholdHistoryLoad.Value,
                    Code: FirstDefectCode(rejected.Defects),
                    Detail: "DerivedRecap进度因raw safety限制而不可用。"
                ),
            DerivedRecapPlanningProgressInspectionResult.Retryable
                retryable => UnavailablePlanningSnapshot(
                    retryable.Code,
                    retryable.Kind switch {
                        DerivedRecapOperationPreparationRetryKind
                            .RawHeadChanged =>
                            "会话边界在DerivedRecap进度检查期间发生变化，请重试。",
                        DerivedRecapOperationPreparationRetryKind
                            .SourceChanged =>
                            "DerivedRecap来源在进度检查期间发生变化，请重试。",
                        _ => "DerivedRecap进度检查需要重试。"
                    }
                ),
            DerivedRecapPlanningProgressInspectionResult.Unavailable
                unavailable => unavailable.Snapshot is { } snapshot
                    ? MapProgress(
                        snapshot,
                        UnavailableState,
                        FirstDefectCode(unavailable.Defects),
                        "DerivedRecap进度当前不可用。"
                    )
                    : UnavailablePlanningSnapshot(
                        FirstDefectCode(unavailable.Defects),
                        "DerivedRecap进度当前不可用。"
                    ),
            DerivedRecapPlanningProgressInspectionResult.BeyondPrefix
                beyond => new RecapPlanningSnapshotDto(
                    ExactFreshness,
                    UnavailableState,
                    ObservedRawHead: EventAddressTextCodec.Format(
                        beyond.Evidence.CapturedHead
                    ),
                    Code: "recap-beyond-prefix",
                    Detail: "DerivedRecap进度所需raw lineage超出bounded prefix"
                        + $"（stage={beyond.Stage}）。"
                ),
            _ => throw new InvalidDataException(
                "Unknown DerivedRecap planning inspection result."
            )
        };
    }

    private static RecapPlanningSnapshotDto MapProgress(
        DerivedRecapPlanningProgressSnapshot snapshot,
        string state,
        string? code = null,
        string? detail = null
    ) => new(
        ExactFreshness,
        state,
        ObservedRawHead: EventAddressTextCodec.Format(
            snapshot.CapturedRawHead
        ),
        CadenceBaseline: EventAddressTextCodec.Format(
            snapshot.CadenceBaseline
        ),
        RecentHistoryUnitCount: snapshot.Measurement
            .GrowthHistoryUnitCount,
        RecentHistoryLoad: snapshot.Measurement.GrowthHistoryLoad.Value,
        MinimumRecentHistoryLoad: snapshot.Cadence
            .MinimumRecentHistoryLoad.Value,
        RecapBuildIntervalHistoryLoad: snapshot.Cadence
            .RecapBuildIntervalHistoryLoad.Value,
        BuildThresholdHistoryLoad: snapshot.BuildThresholdHistoryLoad.Value,
        RemainingHistoryLoad: snapshot.RemainingHistoryLoad.Value,
        Code: code,
        Detail: detail
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

    private static string FirstDefectCode(
        IReadOnlyList<DerivedRecapExecutionDefect> defects
    ) => defects.FirstOrDefault()?.Code
        ?? "recap-planning-unavailable";

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
        return DerivedRecapOnlineLifecycleCoordinator.Create(
            engine.ReadView,
            prepared.Store,
            prepared.Authority,
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
            () => {
                return new RecapBlockMaintainerRegistry([
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
                ]);
            }
        );
    }

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
                "Validated Galatea recap maintainer routing is missing "
                + $"maintainer '{descriptor.MaintainerId}'."
            );
        }
        if (!connections.TryGet(
                connectionId,
                out CompletionConnectionConfig? connection
            )) {
            throw new InvalidOperationException(
                "Validated Galatea recap maintainer routing references "
                + $"unknown connection '{connectionId}'."
            );
        }
        return connection;
    }

    internal static SessionCompletionTargetIdentity
        CreateCompletionTarget(
        CompletionConnectionConfig connection,
        ICompletionClient client
    ) {
        CompletionDispatchIdentity identity =
            CompletionDispatchIdentityFactory.Create(
                connection,
                client
            );
        return new SessionCompletionTargetIdentity(
            identity.ConnectionId,
            identity.Kind,
            identity.ConnectionFingerprint,
            identity.RequestAdapterFingerprint
        );
    }

    private static RecapMaintainerCapabilitySnapshot
        ProjectCapabilities(RecapMaintainerProfileCatalog catalog)
        => new([
            .. catalog.All.Select(static descriptor =>
                new RecapProfilePlanningDescriptor(
                    descriptor.ProfileName,
                    new RecapBlockId(descriptor.RecapBlockIdValue),
                    descriptor.Target,
                    descriptor.MaintainerId,
                    descriptor.CapabilityFingerprint
                )
            )
        ]);
}
