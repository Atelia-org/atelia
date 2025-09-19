using System;

namespace MemoTree.Core.Types {
    /// <summary>
    /// 认知节点的唯一标识符
    /// 使用统一的GUID编码策略，包括根节点的特殊处理
    /// </summary>
    public readonly struct NodeId : IEquatable<NodeId> {
        public string Value {
            get;
        }

        public NodeId(string value) {
            if (string.IsNullOrWhiteSpace(value)) { throw new ArgumentException("NodeId cannot be null or empty", nameof(value)); }
            Value = value;
        }

        /// <summary>
        /// 生成新的NodeId，使用统一的GUID编码
        /// </summary>
        public static NodeId Generate() => new(GuidEncoder.ToIdString(Guid.NewGuid()));

        /// <summary>
        /// 验证ID格式是否有效
        /// </summary>
        public static bool IsValidFormat(string value) {
            if (string.IsNullOrWhiteSpace(value)) { return false; }
            var encodingType = GuidEncoder.DetectEncodingType(value);
            return encodingType != GuidEncodingType.Unknown;
        }

        /// <summary>
        /// 从字符串创建NodeId，验证格式
        /// </summary>
        public static NodeId FromString(string value) {
            if (!IsValidFormat(value)) { throw new ArgumentException($"Invalid NodeId format: {value}", nameof(value)); }
            return new NodeId(value);
        }

        public bool Equals(NodeId other) => Value == other.Value;
        public override bool Equals(object? obj) => obj is NodeId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => Value;

        public static implicit operator string(NodeId nodeId) => nodeId.Value;
        public static explicit operator NodeId(string value) => new(value);

        public static bool operator ==(NodeId left, NodeId right) => left.Equals(right);
        public static bool operator !=(NodeId left, NodeId right) => !left.Equals(right);
    }
}
