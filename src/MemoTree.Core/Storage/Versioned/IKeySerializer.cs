namespace MemoTree.Core.Storage.Versioned {
    /// <summary>
    /// 键序列化器接口
    /// </summary>
    /// <typeparam name="TKey">键类型</typeparam>
    public interface IKeySerializer<TKey> {
        /// <summary>
        /// 将键序列化为文件名安全的字符串
        /// </summary>
        string Serialize(TKey key);

        /// <summary>
        /// 从字符串反序列化键
        /// </summary>
        TKey Deserialize(string serialized);
    }
}
