using System;
using System.Collections.Generic;

namespace MemoTree.Core.Types
{
    /// <summary>
    /// 语义关系定义（集中存储版本，不包括父子关系）
    /// </summary>
    public record NodeRelation
    {
        public RelationId Id { get; init; }
        public NodeId SourceId { get; init; }
        public NodeId TargetId { get; init; }
        public RelationType Type { get; init; }
        public string Description { get; init; } = string.Empty;
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// 关系属性字典
        ///
        /// 类型约定与 NodeMetadata.CustomProperties 相同：
        /// - 支持基本类型：string, int, long, double, bool, DateTime
        /// - 支持集合类型：string[], List&lt;string&gt;
        /// - 使用 CustomPropertiesExtensions 提供的安全访问方法
        ///
        /// 常见关系属性示例：
        /// - "weight": 关系权重 (double)
        /// - "confidence": 置信度 (double, 0.0-1.0)
        /// - "bidirectional": 是否双向关系 (bool)
        /// - "created_by": 创建者 (string)
        /// </summary>
        public IReadOnlyDictionary<string, object> Properties { get; init; } =
            new Dictionary<string, object>();

        /// <summary>
        /// 创建新的节点关系
        /// </summary>
        public static NodeRelation Create(NodeId sourceId, NodeId targetId, RelationType type, string? description = null)
        {
            return new NodeRelation
            {
                Id = RelationId.Generate(),
                SourceId = sourceId,
                TargetId = targetId,
                Type = type,
                Description = description ?? string.Empty,
                CreatedAt = DateTime.UtcNow
            };
        }

        /// <summary>
        /// 更新描述
        /// </summary>
        public NodeRelation WithDescription(string newDescription)
        {
            return this with
            {
                Description = newDescription ?? string.Empty
            };
        }

        /// <summary>
        /// 更新关系属性
        /// </summary>
        public NodeRelation WithProperties(IReadOnlyDictionary<string, object> newProperties)
        {
            return this with
            {
                Properties = newProperties ?? new Dictionary<string, object>()
            };
        }

        /// <summary>
        /// 设置关系属性
        /// </summary>
        public NodeRelation SetProperty(string key, object value)
        {
            if (string.IsNullOrWhiteSpace(key))
                return this;

            var newProperties = new Dictionary<string, object>(Properties)
            {
                [key] = value
            };

            return this with
            {
                Properties = newProperties
            };
        }

        /// <summary>
        /// 移除关系属性
        /// </summary>
        public NodeRelation RemoveProperty(string key)
        {
            if (string.IsNullOrWhiteSpace(key) || !Properties.ContainsKey(key))
                return this;

            var newProperties = new Dictionary<string, object>(Properties);
            newProperties.Remove(key);

            return this with
            {
                Properties = newProperties
            };
        }

        /// <summary>
        /// 设置关系权重
        /// </summary>
        public NodeRelation WithWeight(double weight)
        {
            return SetProperty("weight", weight);
        }

        /// <summary>
        /// 获取关系权重
        /// </summary>
        public double GetWeight()
        {
            return Properties.GetDouble("weight", 1.0);
        }

        /// <summary>
        /// 设置置信度
        /// </summary>
        public NodeRelation WithConfidence(double confidence)
        {
            var clampedConfidence = Math.Max(0.0, Math.Min(1.0, confidence));
            return SetProperty("confidence", clampedConfidence);
        }

        /// <summary>
        /// 获取置信度
        /// </summary>
        public double GetConfidence()
        {
            return Properties.GetDouble("confidence", 1.0);
        }

        /// <summary>
        /// 设置是否为双向关系
        /// </summary>
        public NodeRelation WithBidirectional(bool bidirectional)
        {
            return SetProperty("bidirectional", bidirectional);
        }

        /// <summary>
        /// 检查是否为双向关系
        /// </summary>
        public bool IsBidirectional()
        {
            return Properties.GetBoolean("bidirectional", false);
        }

        /// <summary>
        /// 设置创建者
        /// </summary>
        public NodeRelation WithCreatedBy(string createdBy)
        {
            return SetProperty("created_by", createdBy ?? string.Empty);
        }

        /// <summary>
        /// 获取创建者
        /// </summary>
        public string GetCreatedBy()
        {
            return Properties.GetString("created_by", string.Empty);
        }

        /// <summary>
        /// 检查关系是否涉及指定节点
        /// </summary>
        public bool InvolveNode(NodeId nodeId)
        {
            return SourceId == nodeId || TargetId == nodeId;
        }

        /// <summary>
        /// 获取关系的另一端节点
        /// </summary>
        public NodeId? GetOtherNode(NodeId nodeId)
        {
            if (SourceId == nodeId) return TargetId;
            if (TargetId == nodeId) return SourceId;
            return null;
        }

        /// <summary>
        /// 检查关系方向（从指定节点的角度）
        /// </summary>
        public RelationDirection GetDirection(NodeId nodeId)
        {
            if (SourceId == nodeId) return RelationDirection.Outgoing;
            if (TargetId == nodeId) return RelationDirection.Incoming;
            return RelationDirection.None;
        }

        /// <summary>
        /// 创建反向关系
        /// </summary>
        public NodeRelation CreateReverse()
        {
            var reverseType = Type.GetInverse() ?? Type;
            return new NodeRelation
            {
                Id = RelationId.Generate(),
                SourceId = TargetId,
                TargetId = SourceId,
                Type = reverseType,
                Description = $"Reverse of: {Description}",
                CreatedAt = DateTime.UtcNow,
                Properties = Properties
            };
        }
    }

    /// <summary>
    /// 关系方向枚举
    /// </summary>
    public enum RelationDirection
    {
        None,       // 不涉及指定节点
        Incoming,   // 指向指定节点
        Outgoing    // 从指定节点出发
    }
}
