using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal;
using Atelia.SessionJournal.DerivedRecap.Maintainers;
using Atelia.SessionJournal.DerivedRecap.Planner;
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
                    engine,
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
                    "会话的前情提要配置或存储当前不可用："
                    + string.Join(
                        "; ",
                        unavailable.Defects.Select(static defect =>
                            $"{defect.Code}: {defect.Detail}"
                        )
                    ),
                    unavailable.Defects[0].Code
                ),
            _ => throw new InvalidDataException(
                "Unknown DerivedRecap preparation result."
            )
        };
    }

    internal static DerivedRecapOnlineLifecycleCoordinator
        CreateLifecycle(
        SessionJournalEngine engine,
        GalateaPreparedRecap prepared,
        CompletionConnectionConfig connection,
        ICompletionClient sharedInnerClient,
        string? callLogDirectory
    ) {
        var maintainers = new DeferredRecapBlockMaintainerRegistry(
            () => new RecapBlockMaintainerRegistry([
                .. prepared.CapabilityCatalog.All.Select(descriptor =>
                    descriptor.Create(
                        GalateaCompletionLogging.CreateMaintainerClient(
                            sharedInnerClient,
                            connection,
                            callLogDirectory,
                            descriptor
                        ),
                        connection.ModelId
                    )
                )
            ])
        );
        return DerivedRecapOnlineLifecycleCoordinator.Create(
            engine,
            prepared.Store,
            prepared.Authority,
            maintainers
        );
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
