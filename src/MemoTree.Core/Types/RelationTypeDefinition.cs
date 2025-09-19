using System.Collections.Generic;

namespace MemoTree.Core.Types {
    /// <summary>
    /// 关系类型定义
    /// 定义关系类型的元数据和行为特征
    /// </summary>
    public record RelationTypeDefinition {
        /// <summary>
        /// 关系类型
        /// </summary>
        public RelationType Type {
            get; init;
        }

        /// <summary>
        /// 关系类型名称
        /// </summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>
        /// 关系类型描述
        /// </summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>
        /// 是否为双向关系
        /// </summary>
        public bool IsBidirectional { get; init; } = false;

        /// <summary>
        /// 关系显示颜色（十六进制格式）
        /// </summary>
        public string Color { get; init; } = "#000000";

        /// <summary>
        /// 关系类型的扩展元数据
        /// </summary>
        public IReadOnlyDictionary<string, object> Metadata {
            get; init;
        } =
        new Dictionary<string, object>();

        /// <summary>
        /// 创建时间
        /// </summary>
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// 最后修改时间
        /// </summary>
        public DateTime LastModified { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// 是否为系统内置关系类型
        /// </summary>
        public bool IsBuiltIn { get; init; } = false;

        /// <summary>
        /// 关系权重（用于排序和优先级）
        /// </summary>
        public double Weight { get; init; } = 1.0;
    }
}
