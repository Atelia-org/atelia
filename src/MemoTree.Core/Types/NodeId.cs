using System;

namespace MemoTree.Core.Types
{
    /// <summary>
    /// 认知节点的唯一标识符
    /// 使用统一的GUID编码策略，包括根节点的特殊处理
    /// </summary>
    public readonly struct NodeId : IEquatable<NodeId>
    {
        public string Value { get; }

        public NodeId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("NodeId cannot be null or empty", nameof(value));
            Value = value;
        }

        /// <summary>
        /// 生成新的NodeId，使用统一的GUID编码
        /// </summary>
        public static NodeId Generate() => new(GuidEncoder.ToIdString(Guid.NewGuid()));

        /// <summary>
        /// 根节点的特殊ID - 使用Guid.Empty确保唯一性
        /// 编码结果由 GuidEncoder.ToIdString 决定（当前默认：Base4096‑CJK；兼容：Base64）
        /// 优势: 1) 零冲突风险 2) 格式一致性 3) 简化验证逻辑
        /// </summary>
        public static NodeId Root => new(RootValue);

        /// <summary>
        /// 根节点ID的字符串值（缓存以提高性能）
        /// </summary>
        private static readonly string RootValue = GuidEncoder.ToIdString(Guid.Empty);

        /// <summary>
        /// 检查当前NodeId是否为根节点
        /// </summary>
        public bool IsRoot => Value == RootValue;

        /// <summary>
        /// 验证ID格式是否有效（支持向后兼容）
        /// </summary>
        public static bool IsValidFormat(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            // 检测编码类型并验证
            var encodingType = GuidEncoder.DetectEncodingType(value);
            if (encodingType != GuidEncodingType.Unknown)
                return true;

            // 兼容旧的"root"字符串（迁移期间）
            if (value == "root")
                return true;

            return false;
        }

        /// <summary>
        /// 从字符串创建NodeId，验证格式
        /// </summary>
        public static NodeId FromString(string value)
        {
            if (!IsValidFormat(value))
                throw new ArgumentException($"Invalid NodeId format: {value}", nameof(value));
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
