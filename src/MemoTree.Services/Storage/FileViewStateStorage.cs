using System.Text.Json;
using MemoTree.Core.Json;
using MemoTree.Core.Services;
using MemoTree.Core.Storage.Interfaces;
using MemoTree.Core.Types;
using Microsoft.Extensions.Logging;
using System.Text.Encodings.Web;
using MemoTree.Core.Exceptions; // added

namespace MemoTree.Services.Storage;

/// <summary>
/// 基于文件的视图状态存储（MVP）
/// 存放于 Views 目录，命名为 &lt;viewName&gt;.json
/// </summary>
public class FileViewStateStorage : IViewStateStorage {
    private readonly IWorkspacePathService _paths;
    private readonly ILogger<FileViewStateStorage> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public FileViewStateStorage(
        IWorkspacePathService paths,
        ILogger<FileViewStateStorage> logger
    ) {
        _paths = paths;
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        _jsonOptions.Converters.Add(new NodeIdJsonConverter());
    }

    private string GetViewFilePath(string viewName) {
        var dir = _paths.GetViewsDirectory();
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, viewName + ".json");
    }

    public async Task<MemoTreeViewState?> GetViewStateAsync(string viewName, CancellationToken cancellationToken = default) {
        var path = GetViewFilePath(viewName);
        if (!File.Exists(path)) { return null; }
        try {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            return JsonSerializer.Deserialize<MemoTreeViewState>(json, _jsonOptions);
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Failed to read view state {View}", viewName);
            throw new StorageException($"Failed to read view state '{viewName}'", ex)
            .WithContext("ViewName", viewName)
            .WithContext("Path", path);
        }
    }

    public async Task SaveViewStateAsync(MemoTreeViewState viewState, CancellationToken cancellationToken = default) {
        var path = GetViewFilePath(viewState.Name);
        var updated = viewState with {
            LastModified = DateTime.UtcNow
        };
        var json = JsonSerializer.Serialize(updated, _jsonOptions);
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }

    public Task<IReadOnlyList<string>> GetViewNamesAsync(CancellationToken cancellationToken = default) {
        var dir = _paths.GetViewsDirectory();
        if (!Directory.Exists(dir)) { return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()); }
        var names = Directory.GetFiles(dir, "*.json")
        .Select(f => Path.GetFileNameWithoutExtension(f))
        .Where(n => !string.IsNullOrWhiteSpace(n))
        .Cast<string>()
        .ToList();
        return Task.FromResult<IReadOnlyList<string>>(names);
    }

    public Task DeleteViewStateAsync(string viewName, CancellationToken cancellationToken = default) {
        var path = GetViewFilePath(viewName);
        if (File.Exists(path)) {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public Task<bool> ViewExistsAsync(string viewName, CancellationToken cancellationToken = default) {
        var path = GetViewFilePath(viewName);
        return Task.FromResult(File.Exists(path));
    }

    public async Task<MemoTreeViewState> CopyViewStateAsync(string sourceViewName, string targetViewName, CancellationToken cancellationToken = default) {
        var src = await GetViewStateAsync(sourceViewName, cancellationToken) ?? new MemoTreeViewState { Name = targetViewName };
        var copy = src with {
            Name = targetViewName,
            LastModified = DateTime.UtcNow
        };
        await SaveViewStateAsync(copy, cancellationToken);
        return copy;
    }

    public async Task RenameViewAsync(string oldName, string newName, CancellationToken cancellationToken = default) {
        var oldPath = GetViewFilePath(oldName);
        var newPath = GetViewFilePath(newName);
        if (!File.Exists(oldPath)) { return; /* 原文件不存在，直接返回 */ }
        if (File.Exists(newPath)) {
            _logger.LogWarning("Skip rename: target view already exists. {Old} -> {New}", oldName, newName);
            return;
        }

        // 读取旧状态并更新内部名称。若读取失败（极罕见返回 null），保留其他字段默认值
        var state = await GetViewStateAsync(oldName, cancellationToken) ?? new MemoTreeViewState { Name = newName };
        var updated = state with { Name = newName };

        // 先写入新文件，再删除旧文件；如果写入失败，不删除旧文件，保证至少保留旧版本
        await SaveViewStateAsync(
            updated,
            cancellationToken
        );

        try {
            if (File.Exists(oldPath)) {
                File.Delete(oldPath);
            }
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Failed to delete old view file after rename {Old} -> {New}", oldName, newName);
        }
    }

    public Task<DateTime?> GetViewLastModifiedAsync(string viewName, CancellationToken cancellationToken = default) {
        var path = GetViewFilePath(viewName);
        return Task.FromResult(File.Exists(path) ? File.GetLastWriteTimeUtc(path) : (DateTime?)null);
    }

    public async Task<IReadOnlyDictionary<string, MemoTreeViewState>> GetMultipleViewStatesAsync(IEnumerable<string> viewNames, CancellationToken cancellationToken = default) {
        var dict = new Dictionary<string, MemoTreeViewState>();
        foreach (var name in viewNames) {
            var vs = await GetViewStateAsync(name, cancellationToken);
            if (vs != null) {
                dict[name] = vs;
            }
        }
        return dict;
    }

    public Task<int> CleanupOldViewsAsync(DateTime olderThan, CancellationToken cancellationToken = default) {
        var dir = _paths.GetViewsDirectory();
        if (!Directory.Exists(dir)) { return Task.FromResult(0); }
        var count = 0;
        foreach (var file in Directory.GetFiles(dir, "*.json")) {
            var last = File.GetLastWriteTimeUtc(file);
            if (last < olderThan) {
                File.Delete(file);
                count++;
            }
        }
        return Task.FromResult(count);
    }

    public Task<long?> GetViewSizeAsync(string viewName, CancellationToken cancellationToken = default) {
        var path = GetViewFilePath(viewName);
        return Task.FromResult(File.Exists(path) ? new FileInfo(path).Length : (long?)null);
    }

    public Task<ViewStorageStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default) {
        var dir = _paths.GetViewsDirectory();
        if (!Directory.Exists(dir)) {
            return Task.FromResult(
                new ViewStorageStatistics {
                    TotalViews = 0,
                    TotalSize = 0,
                    OldestView = DateTime.UtcNow,
                    NewestView = DateTime.UtcNow,
                    LargestView = null,
                    LargestViewSize = 0,
                    AverageViewSize = 0,
                }
            );
        }

        var files = Directory.GetFiles(dir, "*.json");
        if (files.Length == 0) {
            return Task.FromResult(
                new ViewStorageStatistics {
                    TotalViews = 0,
                    TotalSize = 0,
                    OldestView = DateTime.UtcNow,
                    NewestView = DateTime.UtcNow,
                    LargestView = null,
                    LargestViewSize = 0,
                    AverageViewSize = 0,
                }
            );
        }

        long total = 0;
        string? largest = null;
        long maxSize = 0;
        DateTime oldest = DateTime.MaxValue;
        DateTime newest = DateTime.MinValue;

        foreach (var f in files) {
            var size = new FileInfo(f).Length;
            total += size;
            if (size > maxSize) {
                maxSize = size;
                largest = Path.GetFileNameWithoutExtension(f);
            }
            var time = File.GetLastWriteTimeUtc(f);
            if (time < oldest) {
                oldest = time;
            }

            if (time > newest) {
                newest = time;
            }
        }

        return Task.FromResult(
            new ViewStorageStatistics {
                TotalViews = files.Length,
                TotalSize = total,
                OldestView = oldest,
                NewestView = newest,
                LargestView = largest,
                LargestViewSize = maxSize,
                AverageViewSize = files.Length > 0 ? (double)total / files.Length : 0,
            }
        );
    }
}
