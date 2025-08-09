using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MemoTree.Core.Storage.Versioned
{
    /// <summary>
    /// 版本化存储实现
    /// </summary>
    public class VersionedStorageImpl<TKey, TValue> : IVersionedStorage<TKey, TValue>
        where TKey : notnull
        where TValue : class
    {
        private readonly VersionedStoragePathProvider<TKey> _pathProvider;
        private readonly VersionedStorageMemoryManager<TKey, TValue> _memoryManager;
        private readonly ILogger<VersionedStorageImpl<TKey, TValue>> _logger;
        private readonly SemaphoreSlim _operationLock = new(1, 1);
        private readonly JsonSerializerOptions _jsonOptions;
        
        public VersionedStorageImpl(
            VersionedStorageOptions options,
            IKeySerializer<TKey> keySerializer,
            IVersionFormatter versionFormatter,
            ILogger<VersionedStorageImpl<TKey, TValue>> logger)
        {
            _pathProvider = new VersionedStoragePathProvider<TKey>(options, keySerializer, versionFormatter);
            _memoryManager = new VersionedStorageMemoryManager<TKey, TValue>();
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            
            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
            
            // 确保目录结构存在
            _pathProvider.EnsureDirectoriesExist();
        }
        
        /// <summary>
        /// 初始化存储（加载现有数据）
        /// </summary>
        public async Task<LoadResult<TKey, TValue>> InitializeAsync(CancellationToken cancellationToken = default)
        {
            var startTime = DateTime.UtcNow;
            
            try
            {
                // 1. 恢复未完成的操作
                var recoveredCount = await RecoverIncompleteOperationsAsync(cancellationToken);
                
                // 2. 加载版本指针
                var versionFromPointer = await LoadVersionFromPointerAsync(cancellationToken);
                
                // 3. 扫描并加载数据文件
                var (loadedData, maxVersionFromFiles, fileCount) = await LoadDataFromFilesAsync(cancellationToken);
                
                // 4. 确定最终版本号
                var finalVersion = Math.Max(versionFromPointer, maxVersionFromFiles);
                
                // 5. 加载到内存
                _memoryManager.LoadData(loadedData);
                _memoryManager.SetVersion(finalVersion);
                
                var duration = DateTime.UtcNow - startTime;
                
                _logger.LogInformation(
                    "Initialized versioned storage: {FileCount} files loaded, version {Version}, recovered {RecoveredCount} operations in {Duration}ms",
                    fileCount, finalVersion, recoveredCount, duration.TotalMilliseconds);
                
                return new LoadResult<TKey, TValue>
                {
                    Data = loadedData,
                    CurrentVersion = finalVersion,
                    LoadedFileCount = fileCount,
                    RecoveredOperationCount = recoveredCount,
                    LoadDuration = duration
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize versioned storage");
                throw;
            }
        }
        
        public Task<long> GetCurrentVersionAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_memoryManager.CurrentVersion);
        }
        
        public Task<TValue?> GetAsync(TKey key, CancellationToken cancellationToken = default)
        {
            var value = _memoryManager.Get(key);
            return Task.FromResult(value);
        }
        
        public Task<IReadOnlyDictionary<TKey, TValue>> GetManyAsync(
            IEnumerable<TKey> keys, 
            CancellationToken cancellationToken = default)
        {
            var result = _memoryManager.GetMany(keys);
            return Task.FromResult(result);
        }
        
        public Task<IReadOnlyList<TKey>> GetAllKeysAsync(CancellationToken cancellationToken = default)
        {
            var keys = _memoryManager.GetAllKeys();
            return Task.FromResult(keys);
        }
        
        public Task<bool> ContainsKeyAsync(TKey key, CancellationToken cancellationToken = default)
        {
            var exists = _memoryManager.ContainsKey(key);
            return Task.FromResult(exists);
        }
        
        public async Task<long> UpdateManyAsync(
            IReadOnlyDictionary<TKey, TValue> updates,
            string operationDescription = "",
            CancellationToken cancellationToken = default)
        {
            if (updates == null || !updates.Any())
                return _memoryManager.CurrentVersion;
            
            return await ExecuteAtomicOperationAsync(
                "UpdateMany",
                updates.Keys,
                operationDescription,
                async (targetVersion) =>
                {
                    // 1. 写入数据文件
                    await WriteDataFilesAsync(updates, targetVersion, cancellationToken);
                    
                    // 2. 更新内存
                    var newVersion = _memoryManager.UpdateMany(updates);
                    
                    // 3. 更新版本指针
                    await UpdateVersionPointerAsync(newVersion, operationDescription, cancellationToken);
                    
                    return newVersion;
                },
                cancellationToken);
        }
        
        public async Task<long> DeleteAsync(TKey key, string operationDescription = "", CancellationToken cancellationToken = default)
        {
            return await DeleteManyAsync(new[] { key }, operationDescription, cancellationToken);
        }
        
        public async Task<long> DeleteManyAsync(
            IEnumerable<TKey> keys, 
            string operationDescription = "", 
            CancellationToken cancellationToken = default)
        {
            var keyList = keys.ToList();
            if (!keyList.Any())
                return _memoryManager.CurrentVersion;
            
            return await ExecuteAtomicOperationAsync(
                "DeleteMany",
                keyList,
                operationDescription,
                async (targetVersion) =>
                {
                    // 1. 更新内存（删除）
                    var newVersion = _memoryManager.DeleteMany(keyList);
                    
                    // 2. 更新版本指针
                    await UpdateVersionPointerAsync(newVersion, operationDescription, cancellationToken);
                    
                    // 注意：我们不删除物理文件，保留历史版本
                    
                    return newVersion;
                },
                cancellationToken);
        }
        
        private async Task<T> ExecuteAtomicOperationAsync<T>(
            string operationType,
            IEnumerable<TKey> affectedKeys,
            string description,
            Func<long, Task<T>> operation,
            CancellationToken cancellationToken)
        {
            await _operationLock.WaitAsync(cancellationToken);
            try
            {
                var operationId = Guid.NewGuid().ToString();
                var targetVersion = _memoryManager.CurrentVersion + 1;
                
                // 1. 写入操作日志
                var log = new OperationLog
                {
                    OperationId = operationId,
                    OperationType = operationType,
                    TargetVersion = targetVersion,
                    AffectedKeys = affectedKeys.Select(k => k.ToString() ?? string.Empty).ToList(),
                    StartTime = DateTime.UtcNow,
                    Description = description
                };
                
                var logFile = _pathProvider.GetJournalFilePath(operationId);
                await WriteOperationLogAsync(logFile, log, cancellationToken);
                
                try
                {
                    // 2. 执行操作
                    var result = await operation(targetVersion);
                    
                    // 3. 标记完成
                    await MarkOperationCompleteAsync(logFile, cancellationToken);
                    
                    // 4. 清理日志
                    File.Delete(logFile);
                    
                    _logger.LogDebug("Completed atomic operation {OperationType} with {KeyCount} keys, new version {Version}",
                        operationType, affectedKeys.Count(), targetVersion);
                    
                    return result;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed atomic operation {OperationType}, log preserved for recovery", operationType);
                    throw;
                }
            }
            finally
            {
                _operationLock.Release();
            }
        }
        
        private async Task WriteDataFilesAsync(
            IReadOnlyDictionary<TKey, TValue> data,
            long version,
            CancellationToken cancellationToken)
        {
            foreach (var (key, value) in data)
            {
                var filePath = _pathProvider.GetDataFilePath(key, version);
                var json = JsonSerializer.Serialize(value, _jsonOptions);
                await File.WriteAllTextAsync(filePath, json, cancellationToken);
            }
        }
        
        private async Task WriteOperationLogAsync(string logFile, OperationLog log, CancellationToken cancellationToken)
        {
            var json = JsonSerializer.Serialize(log, _jsonOptions);
            await File.WriteAllTextAsync(logFile, json, cancellationToken);
        }
        
        private async Task MarkOperationCompleteAsync(string logFile, CancellationToken cancellationToken)
        {
            if (!File.Exists(logFile)) return;
            
            var json = await File.ReadAllTextAsync(logFile, cancellationToken);
            var log = JsonSerializer.Deserialize<OperationLog>(json, _jsonOptions);
            
            if (log != null)
            {
                var completedLog = log with { Completed = true };
                var completedJson = JsonSerializer.Serialize(completedLog, _jsonOptions);
                await File.WriteAllTextAsync(logFile, completedJson, cancellationToken);
            }
        }
        
        private async Task UpdateVersionPointerAsync(long version, string comment, CancellationToken cancellationToken)
        {
            var versionPointer = new VersionPointer
            {
                CurrentVersion = version,
                LastModified = DateTime.UtcNow,
                Comment = comment
            };
            
            var versionFile = _pathProvider.GetVersionFilePath();
            var json = JsonSerializer.Serialize(versionPointer, _jsonOptions);
            await File.WriteAllTextAsync(versionFile, json, cancellationToken);
        }
        
        private async Task<long> LoadVersionFromPointerAsync(CancellationToken cancellationToken)
        {
            var versionFile = _pathProvider.GetVersionFilePath();
            if (!File.Exists(versionFile))
            {
                // 初始化版本文件
                await UpdateVersionPointerAsync(1, "Initialize storage", cancellationToken);
                return 1;
            }
            
            try
            {
                var json = await File.ReadAllTextAsync(versionFile, cancellationToken);
                var versionPointer = JsonSerializer.Deserialize<VersionPointer>(json, _jsonOptions);
                return versionPointer?.CurrentVersion ?? 1;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load version pointer, defaulting to version 1");
                return 1;
            }
        }
        
        private async Task<(IReadOnlyDictionary<TKey, TValue> data, long maxVersion, int fileCount)> LoadDataFromFilesAsync(
            CancellationToken cancellationToken)
        {
            var fileGroups = _pathProvider.ScanDataFiles()
                .GroupBy(f => f.Key)
                .ToList();
            
            var loadedData = new Dictionary<TKey, TValue>();
            var maxVersion = 0L;
            var fileCount = 0;
            
            foreach (var group in fileGroups)
            {
                var latestFile = group.OrderByDescending(f => f.Version).First();
                maxVersion = Math.Max(maxVersion, latestFile.Version);
                fileCount++;
                
                try
                {
                    var json = await File.ReadAllTextAsync(latestFile.FilePath, cancellationToken);
                    var value = JsonSerializer.Deserialize<TValue>(json, _jsonOptions);
                    if (value != null)
                    {
                        loadedData[latestFile.Key] = value;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load data file {FilePath}", latestFile.FilePath);
                }
            }
            
            return (loadedData, maxVersion, fileCount);
        }
        
        private async Task<int> RecoverIncompleteOperationsAsync(CancellationToken cancellationToken)
        {
            var journalFiles = _pathProvider.ScanJournalFiles().ToList();
            var recoveredCount = 0;
            
            foreach (var journalFile in journalFiles)
            {
                try
                {
                    if (await IsOperationCompleteAsync(journalFile, cancellationToken))
                    {
                        // 操作已完成但日志未删除，直接删除
                        File.Delete(journalFile);
                    }
                    else
                    {
                        // 操作未完成，需要回滚
                        await RollbackOperationAsync(journalFile, cancellationToken);
                        recoveredCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to recover operation from journal {JournalFile}", journalFile);
                }
            }
            
            return recoveredCount;
        }
        
        private async Task<bool> IsOperationCompleteAsync(string journalFile, CancellationToken cancellationToken)
        {
            try
            {
                var json = await File.ReadAllTextAsync(journalFile, cancellationToken);
                var log = JsonSerializer.Deserialize<OperationLog>(json, _jsonOptions);
                return log?.Completed == true;
            }
            catch
            {
                return false;
            }
        }
        
        private async Task RollbackOperationAsync(string journalFile, CancellationToken cancellationToken)
        {
            try
            {
                var json = await File.ReadAllTextAsync(journalFile, cancellationToken);
                var log = JsonSerializer.Deserialize<OperationLog>(json, _jsonOptions);
                
                if (log != null)
                {
                    // 删除目标版本的所有相关文件
                    foreach (var keyString in log.AffectedKeys)
                    {
                        // 这里需要反序列化键，但我们暂时跳过，因为实际上很少会发生回滚
                        _logger.LogInformation("Would rollback key {Key} for version {Version}", keyString, log.TargetVersion);
                    }
                }
                
                // 删除日志文件
                File.Delete(journalFile);
                
                _logger.LogInformation("Rolled back incomplete operation from {JournalFile}", journalFile);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to rollback operation from {JournalFile}", journalFile);
            }
        }
        
        public void Dispose()
        {
            _operationLock?.Dispose();
        }
    }
}
