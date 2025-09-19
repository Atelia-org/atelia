using System.Collections.Concurrent;

namespace MemoTree.Core.Storage.Versioned {
    /// <summary>
    /// 版本化存储内存管理器
    /// </summary>
    public class VersionedStorageMemoryManager<TKey, TValue>
    where TKey : notnull
    where TValue : class {
        private readonly ConcurrentDictionary<TKey, TValue> _data = new();
        private long _currentVersion = 1;

        /// <summary>
        /// 获取当前版本
        /// </summary>
        public long CurrentVersion => Interlocked.Read(ref _currentVersion);

        /// <summary>
        /// 获取数据项数量
        /// </summary>
        public int Count => _data.Count;

        /// <summary>
        /// 获取值
        /// </summary>
        public TValue? Get(TKey key)
        => _data.TryGetValue(key, out var value) ? value : null;

        /// <summary>
        /// 批量获取值
        /// </summary>
        public IReadOnlyDictionary<TKey, TValue> GetMany(IEnumerable<TKey> keys) {
            var result = new Dictionary<TKey, TValue>();
            foreach (var key in keys) {
                if (_data.TryGetValue(key, out var value)) {
                    result[key] = value;
                }
            }
            return result;
        }

        /// <summary>
        /// 获取所有键
        /// </summary>
        public IReadOnlyList<TKey> GetAllKeys()
        => _data.Keys.ToList();

        /// <summary>
        /// 获取所有数据
        /// </summary>
        public IReadOnlyDictionary<TKey, TValue> GetAllData()
        => new Dictionary<TKey, TValue>(_data);

        /// <summary>
        /// 检查键是否存在
        /// </summary>
        public bool ContainsKey(TKey key)
        => _data.ContainsKey(key);

        /// <summary>
        /// 批量更新并返回新版本号
        /// </summary>
        public long UpdateMany(IReadOnlyDictionary<TKey, TValue> updates) {
            var newVersion = Interlocked.Increment(ref _currentVersion);

            foreach (var (key, value) in updates) {
                _data.AddOrUpdate(key, value, (_, _) => value);
            }

            return newVersion;
        }

        /// <summary>
        /// 删除指定键
        /// </summary>
        public long Delete(TKey key) {
            var newVersion = Interlocked.Increment(ref _currentVersion);
            _data.TryRemove(key, out _);
            return newVersion;
        }

        /// <summary>
        /// 批量删除多个键
        /// </summary>
        public long DeleteMany(IEnumerable<TKey> keys) {
            var newVersion = Interlocked.Increment(ref _currentVersion);

            foreach (var key in keys) {
                _data.TryRemove(key, out _);
            }

            return newVersion;
        }

        /// <summary>
        /// 设置版本号（用于启动时加载）
        /// </summary>
        public void SetVersion(long version) {
            Interlocked.Exchange(ref _currentVersion, version);
        }

        /// <summary>
        /// 加载数据（用于启动时加载）
        /// </summary>
        public void LoadData(IReadOnlyDictionary<TKey, TValue> data) {
            _data.Clear();
            foreach (var (key, value) in data) {
                _data.TryAdd(key, value);
            }
        }

        /// <summary>
        /// 清空所有数据
        /// </summary>
        public void Clear() {
            _data.Clear();
            Interlocked.Exchange(ref _currentVersion, 1);
        }

        /// <summary>
        /// 获取内存使用统计
        /// </summary>
        public MemoryStats GetMemoryStats() {
            return new MemoryStats {
                ItemCount = _data.Count,
                CurrentVersion = Interlocked.Read(ref _currentVersion),
                EstimatedMemoryUsage = EstimateMemoryUsage()
            };
        }

        private long EstimateMemoryUsage() {
            // 简单估算：每个键值对大约占用的内存
            // 这是一个粗略估算，实际使用可能需要更精确的计算
            const long averageBytesPerItem = 1024; // 1KB per item
            return _data.Count * averageBytesPerItem;
        }
    }

    /// <summary>
    /// 内存使用统计
    /// </summary>
    public record MemoryStats {
        public int ItemCount {
            get; init;
        }
        public long CurrentVersion {
            get; init;
        }
        public long EstimatedMemoryUsage {
            get; init;
        }
        public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    }
}
