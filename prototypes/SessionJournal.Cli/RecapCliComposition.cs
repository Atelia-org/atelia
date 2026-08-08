using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.DerivedRecap.Abstractions;
using Atelia.SessionJournal.DerivedRecap.Maintainers;
using Atelia.SessionJournal.DerivedRecap.Planner;
using Atelia.SessionJournal.DerivedRecap.Runtime;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.Cli;

internal static class RecapCliComposition {
    internal static ResolvedRecapPlannerComposition
        DefaultComposition =>
        BuiltInRecapPlannerConfig.Composition;

    /// <summary>
    /// Opt-in runtime composition. Callers must complete Store readiness
    /// checks before invoking this method because logging construction may
    /// attempt to initialize the best-effort call-log directory.
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
        var lanes = new RecapExecutionLaneInterner();
        RecapExecutionLane lane = lanes.GetOrAddWithLogging(
            connection,
            sharedInnerClient,
            connection,
            callLogDirectory,
            command
        );
        var groups = new RecapRuntimeGroupInterner();
        IRecapBlockMaintainer[] maintainers = [
            .. capabilities.Select(descriptor =>
                groups.GetOrAdd(
                        lane,
                        descriptor.Definition.Family
                    )
                    .Bind(descriptor.Definition)
            )
        ];
        return new RecapCliMaintainerComposition(
            new RecapBlockMaintainerRegistry(maintainers),
            [lane]
        );
    }
}

internal sealed record RecapCliMaintainerComposition(
    IRecapBlockMaintainerRegistry Registry,
    IReadOnlyList<RecapExecutionLane> Lanes
);
