using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.DerivedRecap.Maintainers;
using Atelia.SessionJournal.DerivedRecap.Planner;
using Atelia.SessionJournal.DerivedRecap.Store;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.Cli;

internal static class RecapCliComposition {
    internal static ResolvedRecapPlannerComposition
        ProductionComposition =>
        BuiltInRecapPlannerConfig.Composition;

    /// <summary>
    /// Opt-in runtime composition. Callers must complete Store readiness
    /// checks before invoking this method because logging construction creates
    /// the call-log directory.
    /// </summary>
    internal static RecapCliMaintainerComposition CreateMaintainers(
        RecapMaintainerProfileCatalog capabilityCatalog,
        CompletionConnectionConfig connection,
        ICompletionClient sharedInnerClient,
        string callLogDirectory,
        string command
    ) {
        ArgumentNullException.ThrowIfNull(capabilityCatalog);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(sharedInnerClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(callLogDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        RecapMaintainerProfileDescriptor[] capabilities = [
            .. capabilityCatalog.All
        ];
        LoggingCompletionClient[] loggingClients = [
            .. capabilities.Select(descriptor => LoggingClient(
                sharedInnerClient,
                connection,
                callLogDirectory,
                command,
                descriptor
            ))
        ];
        IRecapBlockMaintainer[] maintainers = [
            .. capabilities.Select((descriptor, index) =>
                descriptor.Create(
                    loggingClients[index],
                    connection.ModelId
                )
            )
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
        RecapMaintainerProfileDescriptor descriptor
    ) => new(
        inner,
        connection,
        callLogDirectory,
        new CompletionCallLogContext(
            Command: command,
            MaintainerId: descriptor.MaintainerId,
            TargetCarrier:
                SJ.ContextHeaderCarrierTokens.ToStorageToken(
                    descriptor.Target.Carrier
                ),
            TargetBlockId: descriptor.Target.BlockKey
        )
    );
}

internal sealed record RecapCliMaintainerComposition(
    IRecapBlockMaintainerRegistry Registry,
    IReadOnlyList<LoggingCompletionClient> LoggingClients
);
