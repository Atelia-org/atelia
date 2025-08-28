using System;

namespace MemoTree.Core.Types {
    /// <summary>
    /// 关系标识符
    /// </summary>
    public readonly struct RelationId : IEquatable<RelationId> {
        public string Value {
            get;
        }

        public RelationId(string value) {
            if (string.IsNullOrWhiteSpace(value)) {
                throw new ArgumentException("RelationId cannot be null or empty", nameof(value));
            }

            Value = value;
        }

        /// <summary>
        /// 生成新的RelationId，使用统一的GUID编码
        /// </summary>
        public static RelationId Generate() => new(GuidEncoder.ToIdString(Guid.NewGuid()));

        /// <summary>
        /// 验证ID格式是否有效
        /// </summary>
        public static bool IsValidFormat(string value) {
            if (string.IsNullOrWhiteSpace(value)) {
                return false;
            }

            var encodingType = GuidEncoder.DetectEncodingType(value);
            return encodingType != GuidEncodingType.Unknown;
        }

        /// <summary>
        /// 从字符串创建RelationId，验证格式
        /// </summary>
        public static RelationId FromString(string value) {
            if (!IsValidFormat(value)) {
                throw new ArgumentException($"Invalid RelationId format: {value}", nameof(value));
            }

            return new RelationId(value);
        }

        public bool Equals(RelationId other) => Value == other.Value;
        public override bool Equals(object? obj) => obj is RelationId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => Value;

        public static implicit operator string(RelationId relationId) => relationId.Value;
        public static explicit operator RelationId(string value) => new(value);

        public static bool operator ==(RelationId left, RelationId right) => left.Equals(right);
        public static bool operator !=(RelationId left, RelationId right) => !left.Equals(right);
    }
}
