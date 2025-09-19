using MemoTree.Core.Types;

namespace MemoTree.Core.Storage.Versioned {
    /// <summary>
    /// NodeId键序列化器
    /// </summary>
    public class NodeIdKeySerializer : IKeySerializer<NodeId> {
        /// <summary>
        /// 将NodeId序列化为文件名安全的字符串
        /// </summary>
        public string Serialize(NodeId key) {
            return key.Value;
        }

        /// <summary>
        /// 从字符串反序列化NodeId
        /// </summary>
        public NodeId Deserialize(string serialized) {
            if (string.IsNullOrWhiteSpace(serialized)) { throw new ArgumentException("Serialized value cannot be null or empty", nameof(serialized)); }
            return new NodeId(serialized);
        }
    }
}
