using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

/// <summary>
/// Executes exactly one preparer-issued DerivedRecap authority. Callers
/// cannot inject active planning inputs, a raw baseline, or a Building
/// anchor through this public surface.
/// </summary>
public sealed class DerivedRecapPreparedExecutor {
    private readonly Func<
        CancellationToken,
        ValueTask<DerivedRecapExecutionResult>
    > _execute;
    private readonly Func<DerivedRecapPlanningDiagnostics?>
        _getPlanningDiagnostics;

    public DerivedRecapPreparedExecutor(
        SessionJournalEngine engine,
        DerivedRecapStore store,
        PreparedRecapOperationAuthority authority,
        IRecapBlockMaintainerRegistry maintainers
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(maintainers);
        if (!authority.Binding.Matches(
                engine.Path,
                engine.BranchRefId
            )
            || !authority.Binding.Matches(
                store.SessionRepositoryPath,
                store.RefId
            )) {
            throw new ArgumentException(
                "Prepared DerivedRecap authority, Store, and "
                + "SessionJournalEngine must bind the same repository "
                + "and RefId.",
                nameof(authority)
            );
        }

        switch (authority) {
            case PreparedRecapOperationAuthority.FrozenBuilding frozen:
                var building = new DerivedRecapBuildingExecutor(
                    engine,
                    store,
                    maintainers
                );
                _execute = cancellationToken => building.ResumeAsync(
                    frozen.Descriptor,
                    cancellationToken
                );
                _getPlanningDiagnostics = static () => null;
                break;
            case PreparedRecapOperationAuthority.NewPlanning planning:
                var planner = new DerivedRecapPlannerExecutor(
                    engine,
                    store,
                    planning.Configuration.PlanningInputs,
                    planning.Configuration.PlanningLimits,
                    maintainers
                );
                _execute = cancellationToken => planner.RunAsync(
                    planning.Baseline,
                    cancellationToken
                );
                _getPlanningDiagnostics =
                    () => planner.LastPlanningDiagnostics;
                break;
            default:
                throw new InvalidDataException(
                    "Unknown prepared DerivedRecap authority."
                );
        }
    }

    public DerivedRecapPlanningDiagnostics? LastPlanningDiagnostics =>
        _getPlanningDiagnostics();

    public ValueTask<DerivedRecapExecutionResult> ExecuteAsync(
        CancellationToken cancellationToken = default
    ) => _execute(cancellationToken);
}
