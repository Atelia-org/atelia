using MemoTree.Core.Types;

namespace MemoTree.Core.Storage.Relations {
    /// <summary>
    /// 关系统计信息
    /// 提供关系数量、类型分布等统计数据
    /// </summary>
    public class RelationStatistics {
        /// <summary>
        /// 出向关系数量
        /// </summary>
        public int OutgoingCount {
            get; init;
        }

        /// <summary>
        /// 入向关系数量
        /// </summary>
        public int IncomingCount {
            get; init;
        }

        /// <summary>
        /// 总关系数量
        /// </summary>
        public int TotalCount => OutgoingCount + IncomingCount;

        /// <summary>
        /// 按类型分组的出向关系统计
        /// </summary>
        public IReadOnlyDictionary<RelationType, int> OutgoingByType {
            get; init;
        } =
        new Dictionary<RelationType, int>();

        /// <summary>
        /// 按类型分组的入向关系统计
        /// </summary>
        public IReadOnlyDictionary<RelationType, int> IncomingByType {
            get; init;
        } =
        new Dictionary<RelationType, int>();

        /// <summary>
        /// 最后更新时间
        /// </summary>
        public DateTime LastUpdated { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// 节点ID
        /// </summary>
        public NodeId NodeId {
            get; init;
        }

        /// <summary>
        /// 关系强度分布
        /// </summary>
        public RelationStrengthDistribution StrengthDistribution { get; init; } = new();

        /// <summary>
        /// 最常用的关系类型（出向）
        /// </summary>
        public RelationType? MostFrequentOutgoingType =>
        OutgoingByType.OrderByDescending(kvp => kvp.Value).FirstOrDefault().Key;

        /// <summary>
        /// 最常用的关系类型（入向）
        /// </summary>
        public RelationType? MostFrequentIncomingType =>
        IncomingByType.OrderByDescending(kvp => kvp.Value).FirstOrDefault().Key;

        /// <summary>
        /// 关系多样性指数（Shannon熵）
        /// </summary>
        public double DiversityIndex {
            get; init;
        }

        /// <summary>
        /// 获取所有关系类型的统计
        /// </summary>
        /// <returns>关系类型到总数的映射</returns>
        public IReadOnlyDictionary<RelationType, int> GetAllTypeStatistics() {
            var allTypes = new Dictionary<RelationType, int>();

            foreach (var kvp in OutgoingByType) {
                allTypes[kvp.Key] = allTypes.GetValueOrDefault(kvp.Key, 0) + kvp.Value;
            }

            foreach (var kvp in IncomingByType) {
                allTypes[kvp.Key] = allTypes.GetValueOrDefault(kvp.Key, 0) + kvp.Value;
            }

            return allTypes;
        }

        /// <summary>
        /// 获取关系类型的百分比分布
        /// </summary>
        /// <returns>关系类型到百分比的映射</returns>
        public IReadOnlyDictionary<RelationType, double> GetTypePercentages() {
            var allStats = GetAllTypeStatistics();
            var total = allStats.Values.Sum();

            if (total == 0) { return new Dictionary<RelationType, double>(); }
            return allStats.ToDictionary(
                kvp => kvp.Key,
                kvp => (double)kvp.Value / total * 100
            );
        }

        /// <summary>
        /// 检查是否为孤立节点（无关系）
        /// </summary>
        /// <returns>如果是孤立节点则返回true</returns>
        public bool IsIsolated => TotalCount == 0;

        /// <summary>
        /// 检查是否为中心节点（关系数量超过阈值）
        /// </summary>
        /// <param name="threshold">阈值</param>
        /// <returns>如果是中心节点则返回true</returns>
        public bool IsCentralNode(int threshold = 10) => TotalCount >= threshold;

        /// <summary>
        /// 获取关系活跃度分数
        /// </summary>
        /// <returns>活跃度分数（0-1之间）</returns>
        public double GetActivityScore() {
            if (TotalCount == 0) { return 0; }
            var countScore = Math.Min(TotalCount / 20.0, 1.0); // 20个关系为满分
            var diversityScore = DiversityIndex / Math.Log(Enum.GetValues<RelationType>().Length);

            return (countScore + diversityScore) / 2.0;
        }
    }

    /// <summary>
    /// 关系强度分布
    /// </summary>
    public record RelationStrengthDistribution {
        public int WeakRelations {
            get; init;
        }
        public int ModerateRelations {
            get; init;
        }
        public int StrongRelations {
            get; init;
        }
        public double AverageStrength {
            get; init;
        }
        public double MaxStrength {
            get; init;
        }
        public double MinStrength {
            get; init;
        }
    }
}
