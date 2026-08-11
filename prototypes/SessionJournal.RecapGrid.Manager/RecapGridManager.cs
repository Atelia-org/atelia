using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Store;

namespace Atelia.SessionJournal.RecapGrid.Manager;

public sealed partial class RecapGridManager {
    private readonly HistoryTimelineBuildReadSession _timeline;
    private readonly RecapGridControlReaderHandle _control;
    private readonly RecapGridStoreHandle _store;
    private readonly ManagerLifetime _lifetime;
    private readonly TimeProvider _timeProvider;
    private readonly ManagerTestHooks _testHooks;

    internal RecapGridManager(
        HistoryTimelineBuildReadSession timeline,
        RecapGridControlReaderHandle control,
        RecapGridStoreHandle store,
        ManagerLifetime lifetime,
        TimeProvider timeProvider,
        ManagerTestHooks testHooks
    ) {
        _timeline = timeline;
        _control = control;
        _store = store;
        _lifetime = lifetime;
        _timeProvider = timeProvider;
        _testHooks = testHooks;
    }

    public async ValueTask<RecapGridBuildResult> BuildAsync(
        RecapGridBuildRequest request,
        IRecapCellBatchExecutor executor,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(executor);
        using ManagerLifetime.Operation? operation = _lifetime.TryEnter();
        if (operation is null) {
            return new RecapGridBuildResult.Disposed();
        }
        return await BuildCoreAsync(
            request,
            executor,
            cancellationToken
        ).ConfigureAwait(false);
    }

    private ValueTask<RecapGridBuildResult> BuildCoreAsync(
        RecapGridBuildRequest request,
        IRecapCellBatchExecutor executor,
        CancellationToken cancellationToken
    ) => RunWavefrontAsync(request, executor, cancellationToken);
}
