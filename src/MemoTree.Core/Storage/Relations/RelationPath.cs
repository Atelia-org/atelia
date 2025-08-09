using MemoTree.Core.Types;

namespace MemoTree.Core.Storage.Relations
{
    /// <summary>
    /// 关系路径数据结构
    /// 描述节点间的连接路径和路径属性
    /// </summary>
    public class RelationPath
    {
        /// <summary>
        /// 起始节点ID
        /// </summary>
        public NodeId StartNodeId { get; init; }

        /// <summary>
        /// 目标节点ID
        /// </summary>
        public NodeId EndNodeId { get; init; }

        /// <summary>
        /// 路径中的节点序列
        /// </summary>
        public IReadOnlyList<NodeId> NodePath { get; init; } = Array.Empty<NodeId>();

        /// <summary>
        /// 路径中的关系序列
        /// </summary>
        public IReadOnlyList<NodeRelation> Relations { get; init; } = Array.Empty<NodeRelation>();

        /// <summary>
        /// 路径长度（跳数）
        /// </summary>
        public int Length => Relations.Count;

        /// <summary>
        /// 路径权重（可用于路径排序）
        /// </summary>
        public double Weight { get; init; }

        /// <summary>
        /// 路径类型（直接、间接等）
        /// </summary>
        public PathType Type { get; init; }

        /// <summary>
        /// 路径强度（基于关系类型和权重计算）
        /// </summary>
        public double Strength { get; init; }

        /// <summary>
        /// 路径发现时间
        /// </summary>
        public DateTime DiscoveredAt { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// 路径的置信度
        /// </summary>
        public double Confidence { get; init; } = 1.0;

        /// <summary>
        /// 检查路径是否有效
        /// </summary>
        /// <returns>如果路径有效则返回true</returns>
        public bool IsValid()
        {
            if (NodePath.Count < 2 || Relations.Count != NodePath.Count - 1)
                return false;

            if (NodePath.First() != StartNodeId || NodePath.Last() != EndNodeId)
                return false;

            // 验证路径连续性
            for (int i = 0; i < Relations.Count; i++)
            {
                var relation = Relations[i];
                var currentNode = NodePath[i];
                var nextNode = NodePath[i + 1];

                if (relation.SourceId != currentNode || relation.TargetId != nextNode)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// 获取路径中涉及的关系类型
        /// </summary>
        /// <returns>关系类型集合</returns>
        public IReadOnlySet<RelationType> GetRelationTypes()
        {
            return Relations.Select(r => r.Type).ToHashSet();
        }

        /// <summary>
        /// 获取路径的描述
        /// </summary>
        /// <returns>路径描述字符串</returns>
        public string GetDescription()
        {
            if (!Relations.Any())
                return "空路径";

            var relationDescriptions = Relations.Select(r => r.Type.GetDisplayName());
            return string.Join(" → ", relationDescriptions);
        }

        /// <summary>
        /// 计算路径的复杂度
        /// </summary>
        /// <returns>复杂度分数</returns>
        public double CalculateComplexity()
        {
            if (!Relations.Any())
                return 0;

            // 基于路径长度和关系类型多样性计算复杂度
            var lengthFactor = Math.Log(Length + 1);
            var diversityFactor = GetRelationTypes().Count / (double)Relations.Count;
            
            return lengthFactor * (1 + diversityFactor);
        }

        /// <summary>
        /// 获取路径的反向路径
        /// </summary>
        /// <returns>反向路径</returns>
        public RelationPath GetReversePath()
        {
            var reversedNodes = NodePath.Reverse().ToList();
            var reversedRelations = new List<NodeRelation>();

            // 创建反向关系
            for (int i = Relations.Count - 1; i >= 0; i--)
            {
                var originalRelation = Relations[i];
                var reversedRelation = originalRelation with
                {
                    SourceId = originalRelation.TargetId,
                    TargetId = originalRelation.SourceId
                };
                reversedRelations.Add(reversedRelation);
            }

            return new RelationPath
            {
                StartNodeId = EndNodeId,
                EndNodeId = StartNodeId,
                NodePath = reversedNodes,
                Relations = reversedRelations,
                Weight = Weight,
                Type = Type,
                Strength = Strength,
                Confidence = Confidence
            };
        }
    }

    /// <summary>
    /// 路径类型枚举
    /// </summary>
    public enum PathType
    {
        /// <summary>
        /// 直接路径（一跳）
        /// </summary>
        Direct,

        /// <summary>
        /// 间接路径（多跳）
        /// </summary>
        Indirect,

        /// <summary>
        /// 最短路径
        /// </summary>
        Shortest,

        /// <summary>
        /// 加权路径
        /// </summary>
        Weighted,

        /// <summary>
        /// 强连接路径
        /// </summary>
        StronglyConnected,

        /// <summary>
        /// 弱连接路径
        /// </summary>
        WeaklyConnected
    }
}
