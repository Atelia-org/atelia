using MemoTree.Core.Types;

namespace MemoTree.Core.Storage.Relations
{
    /// <summary>
    /// 创建关系请求
    /// 封装关系创建所需的参数信息
    /// </summary>
    public class CreateRelationRequest
    {
        /// <summary>
        /// 源节点ID
        /// </summary>
        public NodeId SourceId { get; init; }

        /// <summary>
        /// 目标节点ID
        /// </summary>
        public NodeId TargetId { get; init; }

        /// <summary>
        /// 关系类型
        /// </summary>
        public RelationType RelationType { get; init; }

        /// <summary>
        /// 关系描述
        /// </summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>
        /// 关系属性
        /// 
        /// 类型约定与 NodeMetadata.CustomProperties 相同：
        /// - 支持基本类型：string, int, long, double, bool, DateTime
        /// - 支持集合类型：string[], List&lt;string&gt;
        /// - 使用 CustomPropertiesExtensions 提供的安全访问方法
        /// </summary>
        public IReadOnlyDictionary<string, object> Properties { get; init; } = 
            new Dictionary<string, object>();

        /// <summary>
        /// 关系权重（用于路径计算）
        /// </summary>
        public double Weight { get; init; } = 1.0;

        /// <summary>
        /// 关系强度（0-1之间）
        /// </summary>
        public double Strength { get; init; } = 1.0;

        /// <summary>
        /// 是否验证关系的有效性
        /// </summary>
        public bool ValidateRelation { get; init; } = true;

        /// <summary>
        /// 创建者信息
        /// </summary>
        public string? CreatedBy { get; init; }

        /// <summary>
        /// 创建原因
        /// </summary>
        public string? Reason { get; init; }
    }

    /// <summary>
    /// 关系验证结果
    /// 包含验证状态和错误信息
    /// </summary>
    public class RelationValidationResult
    {
        /// <summary>
        /// 验证是否通过
        /// </summary>
        public bool IsValid { get; init; }

        /// <summary>
        /// 验证错误信息
        /// </summary>
        public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

        /// <summary>
        /// 验证警告信息
        /// </summary>
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

        /// <summary>
        /// 验证时间
        /// </summary>
        public DateTime ValidatedAt { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// 验证的节点ID
        /// </summary>
        public NodeId NodeId { get; init; }

        /// <summary>
        /// 验证的关系数量
        /// </summary>
        public int ValidatedRelationCount { get; init; }

        /// <summary>
        /// 验证详情
        /// </summary>
        public IReadOnlyList<RelationValidationDetail> Details { get; init; } = 
            Array.Empty<RelationValidationDetail>();

        /// <summary>
        /// 验证建议
        /// </summary>
        public IReadOnlyList<string> Suggestions { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// 关系验证详情
    /// </summary>
    public record RelationValidationDetail
    {
        public RelationId RelationId { get; init; }
        public string Issue { get; init; } = string.Empty;
        public ValidationSeverity Severity { get; init; }
        public string? Suggestion { get; init; }
    }

    /// <summary>
    /// 验证严重级别
    /// </summary>
    public enum ValidationSeverity
    {
        Info,
        Warning,
        Error,
        Critical
    }

    /// <summary>
    /// 关系模式分析结果
    /// 提供关系模式的分析和识别
    /// </summary>
    public class RelationPatternAnalysis
    {
        /// <summary>
        /// 分析的节点集合
        /// </summary>
        public IReadOnlySet<NodeId> AnalyzedNodes { get; init; } = new HashSet<NodeId>();

        /// <summary>
        /// 发现的关系模式
        /// </summary>
        public IReadOnlyList<RelationPattern> Patterns { get; init; } = Array.Empty<RelationPattern>();

        /// <summary>
        /// 关系密度（关系数/可能的最大关系数）
        /// </summary>
        public double RelationDensity { get; init; }

        /// <summary>
        /// 聚类系数
        /// </summary>
        public double ClusteringCoefficient { get; init; }

        /// <summary>
        /// 分析时间
        /// </summary>
        public DateTime AnalyzedAt { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// 网络直径（最长最短路径）
        /// </summary>
        public int NetworkDiameter { get; init; }

        /// <summary>
        /// 平均路径长度
        /// </summary>
        public double AveragePathLength { get; init; }

        /// <summary>
        /// 连通组件数量
        /// </summary>
        public int ConnectedComponents { get; init; }

        /// <summary>
        /// 分析统计信息
        /// </summary>
        public PatternAnalysisStatistics Statistics { get; init; } = new();
    }

    /// <summary>
    /// 模式分析统计信息
    /// </summary>
    public record PatternAnalysisStatistics
    {
        public int TotalNodes { get; init; }
        public int TotalRelations { get; init; }
        public int UniquePatterns { get; init; }
        public double AnalysisTime { get; init; }
        public IReadOnlyDictionary<string, int> PatternFrequency { get; init; } = 
            new Dictionary<string, int>();
    }

    /// <summary>
    /// 关系模式定义
    /// 描述常见的关系模式和规则
    /// </summary>
    public class RelationPattern
    {
        /// <summary>
        /// 模式名称
        /// </summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>
        /// 模式描述
        /// </summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>
        /// 涉及的关系类型
        /// </summary>
        public IReadOnlySet<RelationType> RelationTypes { get; init; } = new HashSet<RelationType>();

        /// <summary>
        /// 模式中的节点数量
        /// </summary>
        public int NodeCount { get; init; }

        /// <summary>
        /// 模式出现频率
        /// </summary>
        public int Frequency { get; init; }

        /// <summary>
        /// 模式强度（0-1之间）
        /// </summary>
        public double Strength { get; init; }

        /// <summary>
        /// 模式的重要性分数
        /// </summary>
        public double Importance { get; init; }

        /// <summary>
        /// 模式实例
        /// </summary>
        public IReadOnlyList<PatternInstance> Instances { get; init; } = Array.Empty<PatternInstance>();
    }

    /// <summary>
    /// 模式实例
    /// </summary>
    public record PatternInstance
    {
        public IReadOnlySet<NodeId> Nodes { get; init; } = new HashSet<NodeId>();
        public IReadOnlyList<RelationId> Relations { get; init; } = Array.Empty<RelationId>();
        public double MatchScore { get; init; }
    }
}
