using System.Text.Json;
using MemoTree.Core.Json;
using MemoTree.Core.Services;
using MemoTree.Core.Storage.Interfaces;
using MemoTree.Core.Types;
using Microsoft.Extensions.Logging;
using System.Text.Encodings.Web;

namespace MemoTree.Services.Storage;

/// <summary>
/// 基于文件的视图状态存储（MVP）
/// 存放于 Views 目录，命名为 <viewName>.json
/// </summary>
public class FileViewStateStorage : IViewStateStorage
{
    private readonly IWorkspacePathService _paths;
    private readonly ILogger<FileViewStateStorage> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public FileViewStateStorage(
        IWorkspacePathService paths,
        ILogger<FileViewStateStorage> logger)
    {
        _paths = paths;
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        _jsonOptions.Converters.Add(new NodeIdJsonConverter());
    }

    private async Task<string> GetViewFilePathAsync(string viewName)
    {
        var dir = await _paths.GetViewsDirectoryAsync();
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, viewName + ".json");
    }

    public async Task<MemoTreeViewState?> GetViewStateAsync(string viewName, CancellationToken cancellationToken = default)
    {
        var path = await GetViewFilePathAsync(viewName);
        if (!File.Exists(path)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            return JsonSerializer.Deserialize<MemoTreeViewState>(json, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read view state {View}", viewName);
            return null;
        }
    }

    public async Task SaveViewStateAsync(MemoTreeViewState viewState, CancellationToken cancellationToken = default)
    {
        var path = await GetViewFilePathAsync(viewState.Name);
        var updated = viewState with { LastModified = DateTime.UtcNow };
        var json = JsonSerializer.Serialize(updated, _jsonOptions);
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetViewNamesAsync(CancellationToken cancellationToken = default)
    {
        var dir = await _paths.GetViewsDirectoryAsync();
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        var names = Directory.GetFiles(dir, "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Cast<string>()
            .ToList();
        return names;
    }

    public async Task DeleteViewStateAsync(string viewName, CancellationToken cancellationToken = default)
    {
        var path = await GetViewFilePathAsync(viewName);
        if (File.Exists(path)) File.Delete(path);
    }

    public async Task<bool> ViewExistsAsync(string viewName, CancellationToken cancellationToken = default)
    {
        var path = await GetViewFilePathAsync(viewName);
        return File.Exists(path);
    }

    public async Task<MemoTreeViewState> CopyViewStateAsync(string sourceViewName, string targetViewName, CancellationToken cancellationToken = default)
    {
        var src = await GetViewStateAsync(sourceViewName, cancellationToken) ?? new MemoTreeViewState { Name = targetViewName };
        var copy = src with { Name = targetViewName, LastModified = DateTime.UtcNow };
        await SaveViewStateAsync(copy, cancellationToken);
        return copy;
    }

    public async Task RenameViewAsync(string oldName, string newName, CancellationToken cancellationToken = default)
    {
        var oldPath = await GetViewFilePathAsync(oldName);
        var newPath = await GetViewFilePathAsync(newName);
        if (File.Exists(oldPath))
        {
            // 读写以更新内部名称
            var state = await GetViewStateAsync(oldName, cancellationToken) ?? new MemoTreeViewState();
            await SaveViewStateAsync(state with { Name = newName }, cancellationToken);
            if (File.Exists(oldPath)) File.Delete(oldPath);
        }
    }

    public async Task<DateTime?> GetViewLastModifiedAsync(string viewName, CancellationToken cancellationToken = default)
    {
        var path = await GetViewFilePathAsync(viewName);
        return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : (DateTime?)null;
    }

    public async Task<IReadOnlyDictionary<string, MemoTreeViewState>> GetMultipleViewStatesAsync(IEnumerable<string> viewNames, CancellationToken cancellationToken = default)
    {
        var dict = new Dictionary<string, MemoTreeViewState>();
        foreach (var name in viewNames)
        {
            var vs = await GetViewStateAsync(name, cancellationToken);
            if (vs != null) dict[name] = vs;
        }
        return dict;
    }

    public async Task<int> CleanupOldViewsAsync(DateTime olderThan, CancellationToken cancellationToken = default)
    {
        var dir = await _paths.GetViewsDirectoryAsync();
        if (!Directory.Exists(dir)) return 0;
        var count = 0;
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            var last = File.GetLastWriteTimeUtc(file);
            if (last < olderThan)
            {
                File.Delete(file);
                count++;
            }
        }
        return count;
    }

    public async Task<long?> GetViewSizeAsync(string viewName, CancellationToken cancellationToken = default)
    {
        var path = await GetViewFilePathAsync(viewName);
        return File.Exists(path) ? new FileInfo(path).Length : (long?)null;
    }

    public async Task<ViewStorageStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        var dir = await _paths.GetViewsDirectoryAsync();
        if (!Directory.Exists(dir))
        {
            return new ViewStorageStatistics
            {
                TotalViews = 0,
                TotalSize = 0,
                OldestView = DateTime.UtcNow,
                NewestView = DateTime.UtcNow,
                LargestView = null,
                LargestViewSize = 0,
                AverageViewSize = 0,
            };
        }

        var files = Directory.GetFiles(dir, "*.json");
        if (files.Length == 0)
        {
            return new ViewStorageStatistics
            {
                TotalViews = 0,
                TotalSize = 0,
                OldestView = DateTime.UtcNow,
                NewestView = DateTime.UtcNow,
                LargestView = null,
                LargestViewSize = 0,
                AverageViewSize = 0,
            };
        }

        long total = 0;
        string? largest = null;
        long maxSize = 0;
        DateTime oldest = DateTime.MaxValue;
        DateTime newest = DateTime.MinValue;

        foreach (var f in files)
        {
            var size = new FileInfo(f).Length;
            total += size;
            if (size > maxSize) { maxSize = size; largest = Path.GetFileNameWithoutExtension(f); }
            var time = File.GetLastWriteTimeUtc(f);
            if (time < oldest) oldest = time;
            if (time > newest) newest = time;
        }

        return new ViewStorageStatistics
        {
            TotalViews = files.Length,
            TotalSize = total,
            OldestView = oldest,
            NewestView = newest,
            LargestView = largest,
            LargestViewSize = maxSize,
            AverageViewSize = files.Length > 0 ? (double)total / files.Length : 0,
        };
    }
}
