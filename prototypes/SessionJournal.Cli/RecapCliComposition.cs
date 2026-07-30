using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.DerivedRecap.Maintainers;
using Atelia.SessionJournal.DerivedRecap.Planner;
using Atelia.SessionJournal.DerivedRecap.Store;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.Cli;

internal static class RecapCliComposition {
    internal static IReadOnlyList<RecapBlockCatalogEntry>
        CreateCatalog() => Array.AsReadOnly([
        CatalogEntry(WorldUnderstandingRewriteProfiles.Default),
        CatalogEntry(AutobiographicalRewriteProfiles.Default)
    ]);

    internal static RecapPlannerConfig CreateConfig() => new(
        CreateCatalog(),
        rawGrowthTrigger: 32,
        rawGrowthHardLimit: 512,
        maxRouteEndpointsPerBlock: 4,
        maxMaintainerCallsPerBuild: 8,
        maxRawEventsPerStep: 64,
        maxRawEventsPerBuild: 512
    );

    /// <summary>
    /// Opt-in runtime composition. Callers must complete Store readiness
    /// checks before invoking this method because logging construction creates
    /// the call-log directory.
    /// </summary>
    internal static RecapCliMaintainerComposition CreateMaintainers(
        CompletionConnectionConfig connection,
        ICompletionClient sharedInnerClient,
        string callLogDirectory,
        string command
    ) {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(sharedInnerClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(callLogDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        IReadOnlyList<RecapBlockCatalogEntry> catalog =
            CreateCatalog();
        LoggingCompletionClient[] loggingClients = [
            LoggingClient(
                sharedInnerClient,
                connection,
                callLogDirectory,
                command,
                catalog[0]
            ),
            LoggingClient(
                sharedInnerClient,
                connection,
                callLogDirectory,
                command,
                catalog[1]
            )
        ];
        IRecapBlockMaintainer[] maintainers = [
            RecapMaintainerProfileCatalog.Resolve(
                    RecapMaintainerProfileCatalog
                        .WorldUnderstandingRewrite
                )
                .Create(loggingClients[0], connection.ModelId),
            RecapMaintainerProfileCatalog.Resolve(
                    RecapMaintainerProfileCatalog
                        .AutobiographicalRewrite
                )
                .Create(loggingClients[1], connection.ModelId)
        ];
        return new RecapCliMaintainerComposition(
            new RecapBlockMaintainerRegistry(maintainers),
            Array.AsReadOnly(loggingClients)
        );
    }

    private static LoggingCompletionClient LoggingClient(
        ICompletionClient inner,
        CompletionConnectionConfig connection,
        string callLogDirectory,
        string command,
        RecapBlockCatalogEntry entry
    ) => new(
        inner,
        connection,
        callLogDirectory,
        new CompletionCallLogContext(
            Command: command,
            MaintainerId: entry.MaintainerId,
            TargetCarrier:
                SJ.ContextHeaderCarrierTokens.ToStorageToken(
                    entry.Target.Carrier
                ),
            TargetBlockId: entry.Target.BlockKey
        )
    );

    private static RecapBlockCatalogEntry CatalogEntry(
        RecapRewriteProfile profile
    ) => new(
        new RecapBlockId(profile.Target.BlockKey),
        profile.Target,
        profile.Id,
        maxContentUtf8Bytes: 32_768
    );
}

internal sealed record RecapCliMaintainerComposition(
    IRecapBlockMaintainerRegistry Registry,
    IReadOnlyList<LoggingCompletionClient> LoggingClients
);
