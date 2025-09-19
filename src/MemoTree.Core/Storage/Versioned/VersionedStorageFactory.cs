using MemoTree.Core.Types;
using Microsoft.Extensions.Logging;

namespace MemoTree.Core.Storage.Versioned {
    /// <summary>
    /// 版本化存储工厂
    /// </summary>
    public static class VersionedStorageFactory {
        /// <summary>
        /// 创建NodeId到HierarchyInfo的版本化存储
        /// </summary>
        public static async Task<IVersionedStorage<NodeId, HierarchyInfo>> CreateHierarchyStorageAsync(
            string hierarchyStorageRoot,
            ILogger<VersionedStorageImpl<NodeId, HierarchyInfo>> logger,
            IVersionFormatter? versionFormatter = null
        ) {
            var options = new VersionedStorageOptions {
                StorageRoot = hierarchyStorageRoot,
                DataDirectory = "data",
                VersionFile = "version.json",
                JournalsDirectory = "journals",
                KeepVersionCount = 10,
                FileExtension = ".json",
                EnableConcurrency = false // MVP阶段单会话使用
            };

            var keySerializer = new NodeIdKeySerializer();
            var formatter = versionFormatter ?? new HexVersionFormatter();
            var storage = new VersionedStorageImpl<NodeId, HierarchyInfo>(options, keySerializer, formatter, logger);

            // 初始化存储
            await storage.InitializeAsync();

            return storage;
        }

        /// <summary>
        /// 创建通用版本化存储
        /// </summary>
        public static async Task<IVersionedStorage<TKey, TValue>> CreateStorageAsync<TKey, TValue>(
            VersionedStorageOptions options,
            IKeySerializer<TKey> keySerializer,
            ILogger<VersionedStorageImpl<TKey, TValue>> logger,
            IVersionFormatter? versionFormatter = null
        )
        where TKey : notnull
        where TValue : class {
            var formatter = versionFormatter ?? new HexVersionFormatter();
            var storage = new VersionedStorageImpl<TKey, TValue>(options, keySerializer, formatter, logger);
            await storage.InitializeAsync();
            return storage;
        }

        /// <summary>
        /// 创建预配置的存储选项
        /// </summary>
        public static VersionedStorageOptions CreateOptions(
            string workspaceRoot,
            string storageSubDirectory,
            int keepVersionCount = 10,
            bool enableConcurrency = false
        ) {
            return new VersionedStorageOptions {
                StorageRoot = Path.Combine(workspaceRoot, storageSubDirectory),
                DataDirectory = "data",
                VersionFile = "version.json",
                JournalsDirectory = "journals",
                KeepVersionCount = keepVersionCount,
                FileExtension = ".json",
                EnableConcurrency = enableConcurrency
            };
        }
    }
}
