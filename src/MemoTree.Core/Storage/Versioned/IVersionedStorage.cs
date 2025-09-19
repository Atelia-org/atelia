namespace MemoTree.Core.Storage.Versioned {
    /// <summary>
    /// 通用版本化存储接口
    /// </summary>
    /// <typeparam name="TKey">键类型</typeparam>
    /// <typeparam name="TValue">值类型</typeparam>
    public interface IVersionedStorage<TKey, TValue>
    where TKey : notnull
    where TValue : class {
        /// <summary>
        /// 获取当前版本号
        /// </summary>
        Task<long> GetCurrentVersionAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取指定键的最新值
        /// </summary>
        Task<TValue?> GetAsync(TKey key, CancellationToken cancellationToken = default);

        /// <summary>
        /// 批量获取多个键的最新值
        /// </summary>
        Task<IReadOnlyDictionary<TKey, TValue>> GetManyAsync(
            IEnumerable<TKey> keys,
            CancellationToken cancellationToken = default
        );

        /// <summary>
        /// 原子性批量更新（Copy-on-Write）
        /// </summary>
        Task<long> UpdateManyAsync(
            IReadOnlyDictionary<TKey, TValue> updates,
            string operationDescription = "",
            CancellationToken cancellationToken = default
        );

        /// <summary>
        /// 获取所有键
        /// </summary>
        Task<IReadOnlyList<TKey>> GetAllKeysAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 检查键是否存在
        /// </summary>
        Task<bool> ContainsKeyAsync(TKey key, CancellationToken cancellationToken = default);

        /// <summary>
        /// 删除指定键（通过设置为null实现）
        /// </summary>
        Task<long> DeleteAsync(TKey key, string operationDescription = "", CancellationToken cancellationToken = default);

        /// <summary>
        /// 批量删除多个键
        /// </summary>
        Task<long> DeleteManyAsync(
            IEnumerable<TKey> keys,
            string operationDescription = "",
            CancellationToken cancellationToken = default
        );
    }
}
