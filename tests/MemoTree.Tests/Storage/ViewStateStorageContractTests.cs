using System.Threading;
using MemoTree.Core.Storage.Interfaces;
using MemoTree.Core.Types;

namespace MemoTree.Tests.Storage;

public class ViewStateStorageContractTests
{
    private sealed class DummyViewStateStorage : IViewStateStorage
    {
        public Task<MemoTreeViewState?> GetViewStateAsync(string viewName, CancellationToken cancellationToken = default) => Task.FromResult<MemoTreeViewState?>(null);
        public Task SaveViewStateAsync(MemoTreeViewState viewState, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetViewNamesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task DeleteViewStateAsync(string viewName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> ViewExistsAsync(string viewName, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<MemoTreeViewState> CopyViewStateAsync(string sourceViewName, string targetViewName, CancellationToken cancellationToken = default) => Task.FromResult(new MemoTreeViewState());
        public Task RenameViewAsync(string oldName, string newName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DateTime?> GetViewLastModifiedAsync(string viewName, CancellationToken cancellationToken = default) => Task.FromResult<DateTime?>(null);
        public Task<IReadOnlyDictionary<string, MemoTreeViewState>> GetMultipleViewStatesAsync(IEnumerable<string> viewNames, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyDictionary<string, MemoTreeViewState>>(new Dictionary<string, MemoTreeViewState>());
        public Task<int> CleanupOldViewsAsync(DateTime olderThan, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<long?> GetViewSizeAsync(string viewName, CancellationToken cancellationToken = default) => Task.FromResult<long?>(null);
        public Task<ViewStorageStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ViewStorageStatistics());
    }

    [Fact]
    public async Task Interface_Signature_Should_Be_Implementable()
    {
        IViewStateStorage storage = new DummyViewStateStorage();
        var exists = await storage.ViewExistsAsync("default");
        var names = await storage.GetViewNamesAsync();
        Assert.NotNull(names);
        Assert.False(exists);
    }
}

