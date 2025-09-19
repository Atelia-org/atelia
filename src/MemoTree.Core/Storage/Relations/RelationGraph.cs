using MemoTree.Core.Types;

namespace MemoTree.Core.Storage.Relations {
    /// <summary>
    /// 关系图数据结构
    /// 表示节点间的关系网络和连接信息
    /// </summary>
    public class RelationGraph {
        /// <summary>
        /// 根节点ID
        /// </summary>
        public NodeId RootNodeId {
            get; init;
        }

        /// <summary>
        /// 图中的所有节点
        /// </summary>
        public IReadOnlySet<NodeId> Nodes { get; init; } = new HashSet<NodeId>();

        /// <summary>
        /// 图中的所有关系
        /// </summary>
        public IReadOnlyList<NodeRelation> Relations { get; init; } = Array.Empty<NodeRelation>();

        /// <summary>
        /// 节点的邻接表（出向关系）
        /// </summary>
        public IReadOnlyDictionary<NodeId, IReadOnlyList<NodeRelation>> OutgoingRelations {
            get; init;
        } =
        new Dictionary<NodeId, IReadOnlyList<NodeRelation>>();

        /// <summary>
        /// 节点的邻接表（入向关系）
        /// </summary>
        public IReadOnlyDictionary<NodeId, IReadOnlyList<NodeRelation>> IncomingRelations {
            get; init;
        } =
        new Dictionary<NodeId, IReadOnlyList<NodeRelation>>();

        /// <summary>
        /// 图的深度
        /// </summary>
        public int MaxDepth {
            get; init;
        }

        /// <summary>
        /// 创建时间
        /// </summary>
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// 图的统计信息
        /// </summary>
        public GraphStatistics Statistics { get; init; } = new();

        /// <summary>
        /// 获取节点的所有邻居（出向+入向）
        /// </summary>
        /// <param name="nodeId">节点ID</param>
        /// <returns>邻居节点ID集合</returns>
        public IReadOnlySet<NodeId> GetNeighbors(NodeId nodeId) {
            var neighbors = new HashSet<NodeId>();

            if (OutgoingRelations.TryGetValue(nodeId, out var outgoing)) {
                foreach (var relation in outgoing) {
                    neighbors.Add(relation.TargetId);
                }
            }

            if (IncomingRelations.TryGetValue(nodeId, out var incoming)) {
                foreach (var relation in incoming) {
                    neighbors.Add(relation.SourceId);
                }
            }

            return neighbors;
        }

        /// <summary>
        /// 获取两个节点之间的直接关系
        /// </summary>
        /// <param name="sourceId">源节点ID</param>
        /// <param name="targetId">目标节点ID</param>
        /// <returns>直接关系列表</returns>
        public IReadOnlyList<NodeRelation> GetDirectRelations(NodeId sourceId, NodeId targetId) {
            var directRelations = new List<NodeRelation>();

            if (OutgoingRelations.TryGetValue(sourceId, out var outgoing)) {
                directRelations.AddRange(outgoing.Where(r => r.TargetId == targetId));
            }

            return directRelations;
        }

        /// <summary>
        /// 检查两个节点是否直接连接
        /// </summary>
        /// <param name="nodeId1">节点1 ID</param>
        /// <param name="nodeId2">节点2 ID</param>
        /// <returns>如果直接连接则返回true</returns>
        public bool AreDirectlyConnected(NodeId nodeId1, NodeId nodeId2) {
            return GetDirectRelations(nodeId1, nodeId2).Any() ||
            GetDirectRelations(nodeId2, nodeId1).Any();
        }

        /// <summary>
        /// 获取节点的度数（连接数）
        /// </summary>
        /// <param name="nodeId">节点ID</param>
        /// <returns>度数</returns>
        public int GetDegree(NodeId nodeId) {
            var outDegree = OutgoingRelations.TryGetValue(nodeId, out var outgoing) ? outgoing.Count : 0;
            var inDegree = IncomingRelations.TryGetValue(nodeId, out var incoming) ? incoming.Count : 0;
            return outDegree + inDegree;
        }

        /// <summary>
        /// 获取指定类型的关系
        /// </summary>
        /// <param name="relationType">关系类型</param>
        /// <returns>指定类型的所有关系</returns>
        public IReadOnlyList<NodeRelation> GetRelationsByType(RelationType relationType) {
            return Relations.Where(r => r.Type == relationType).ToList();
        }
    }

    /// <summary>
    /// 图统计信息
    /// </summary>
    public record GraphStatistics {
        public int NodeCount {
            get; init;
        }
        public int RelationCount {
            get; init;
        }
        public double Density {
            get; init;
        }
        public int MaxDegree {
            get; init;
        }
        public double AverageDegree {
            get; init;
        }
        public IReadOnlyDictionary<RelationType, int> RelationTypeDistribution {
            get; init;
        } =
        new Dictionary<RelationType, int>();
    }
}
