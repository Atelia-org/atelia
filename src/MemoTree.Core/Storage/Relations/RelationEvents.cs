using MemoTree.Core.Types;

namespace MemoTree.Core.Storage.Relations {
    /// <summary>
    /// 节点关系变更事件
    /// 支持关系变更的事件通知
    /// </summary>
    public class NodeRelationChangedEvent {
        /// <summary>
        /// 事件ID
        /// </summary>
        public string EventId { get; init; } = Guid.NewGuid().ToString();

        /// <summary>
        /// 变更类型
        /// </summary>
        public RelationChangeType ChangeType {
            get; init;
        }

        /// <summary>
        /// 涉及的关系
        /// </summary>
        public NodeRelation Relation { get; init; } = null!;

        /// <summary>
        /// 变更前的关系状态（更新操作时使用）
        /// </summary>
        public NodeRelation? PreviousRelation {
            get; init;
        }

        /// <summary>
        /// 事件发生时间
        /// </summary>
        public DateTime Timestamp { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// 触发变更的用户或系统
        /// </summary>
        public string? TriggeredBy {
            get; init;
        }

        /// <summary>
        /// 变更原因或上下文
        /// </summary>
        public string? Reason {
            get; init;
        }

        /// <summary>
        /// 事件的严重级别
        /// </summary>
        public EventSeverity Severity { get; init; } = EventSeverity.Information;

        /// <summary>
        /// 相关的元数据
        /// </summary>
        public IReadOnlyDictionary<string, object> Metadata {
            get; init;
        } =
        new Dictionary<string, object>();
    }

    /// <summary>
    /// 节点层次结构变更事件
    /// 支持父子关系变更通知
    /// </summary>
    public class NodeHierarchyChangedEvent {
        /// <summary>
        /// 事件ID
        /// </summary>
        public string EventId { get; init; } = Guid.NewGuid().ToString();

        /// <summary>
        /// 变更的节点ID
        /// </summary>
        public NodeId NodeId {
            get; init;
        }

        /// <summary>
        /// 变更类型
        /// </summary>
        public HierarchyChangeType ChangeType {
            get; init;
        }

        /// <summary>
        /// 原父节点ID（移动操作时使用）
        /// </summary>
        public NodeId? OldParentId {
            get; init;
        }

        /// <summary>
        /// 新父节点ID（移动操作时使用）
        /// </summary>
        public NodeId? NewParentId {
            get; init;
        }

        /// <summary>
        /// 原顺序位置（重排序操作时使用）
        /// </summary>
        public int? OldOrder {
            get; init;
        }

        /// <summary>
        /// 新顺序位置（重排序操作时使用）
        /// </summary>
        public int? NewOrder {
            get; init;
        }

        /// <summary>
        /// 事件发生时间
        /// </summary>
        public DateTime Timestamp { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// 触发变更的用户或系统
        /// </summary>
        public string? TriggeredBy {
            get; init;
        }

        /// <summary>
        /// 变更原因或上下文
        /// </summary>
        public string? Reason {
            get; init;
        }

        /// <summary>
        /// 事件的严重级别
        /// </summary>
        public EventSeverity Severity { get; init; } = EventSeverity.Information;

        /// <summary>
        /// 影响的子节点数量（对于批量操作）
        /// </summary>
        public int AffectedChildrenCount {
            get; init;
        }
    }

    /// <summary>
    /// 关系变更类型
    /// </summary>
    public enum RelationChangeType {
        /// <summary>
        /// 关系创建
        /// </summary>
        Created,

        /// <summary>
        /// 关系更新
        /// </summary>
        Updated,

        /// <summary>
        /// 关系删除
        /// </summary>
        Deleted,

        /// <summary>
        /// 批量操作
        /// </summary>
        BatchOperation
    }

    /// <summary>
    /// 层次结构变更类型
    /// </summary>
    public enum HierarchyChangeType {
        /// <summary>
        /// 节点被添加到父节点
        /// </summary>
        ChildAdded,

        /// <summary>
        /// 节点从父节点移除
        /// </summary>
        ChildRemoved,

        /// <summary>
        /// 节点被移动到新父节点
        /// </summary>
        NodeMoved,

        /// <summary>
        /// 子节点顺序被重新排列
        /// </summary>
        ChildrenReordered
    }

    /// <summary>
    /// 事件严重级别
    /// </summary>
    public enum EventSeverity {
        /// <summary>
        /// 信息级别
        /// </summary>
        Information,

        /// <summary>
        /// 警告级别
        /// </summary>
        Warning,

        /// <summary>
        /// 错误级别
        /// </summary>
        Error,

        /// <summary>
        /// 严重错误级别
        /// </summary>
        Critical
    }
}
