using System.Collections.Generic;

namespace MemoTree.Core.Types
{
    /// <summary>
    /// 节点在视图中的状态
    /// </summary>
    public record NodeViewState
    {
        /// <summary>
        /// 节点ID
        /// </summary>
        public NodeId Id { get; init; }

        /// <summary>
        /// 当前显示的LOD级别
        /// </summary>
        public LodLevel CurrentLevel { get; init; } = LodLevel.Summary;

        /// <summary>
        /// 是否展开显示子节点
        /// </summary>
        public bool IsExpanded { get; init; } = false;

        /// <summary>
        /// 是否在视图中可见
        /// </summary>
        public bool IsVisible { get; init; } = true;

        /// <summary>
        /// 在视图中的显示顺序
        /// </summary>
        public int Order { get; init; } = 0;

        /// <summary>
        /// 节点在画布中的位置（可选）
        /// </summary>
        public (double X, double Y)? Position { get; init; }

        /// <summary>
        /// 节点的视觉样式设置
        /// </summary>
        public IReadOnlyDictionary<string, object> StyleSettings { get; init; } =
            new Dictionary<string, object>();
    }

    /// <summary>
    /// 认知画布视图状态
    /// </summary>
    public record CanvasViewState
    {
        /// <summary>
        /// 视图名称
        /// </summary>
        public string Name { get; init; } = "default";

        /// <summary>
        /// 最后修改时间
        /// </summary>
        public DateTime LastModified { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// 视图中所有节点的状态
        /// </summary>
        public IReadOnlyList<NodeViewState> NodeStates { get; init; } = Array.Empty<NodeViewState>();

        /// <summary>
        /// 当前聚焦的节点ID
        /// </summary>
        public NodeId? FocusedNodeId { get; init; }

        /// <summary>
        /// 视图的全局设置
        /// </summary>
        public IReadOnlyDictionary<string, object> ViewSettings { get; init; } = 
            new Dictionary<string, object>();

        /// <summary>
        /// 视图的缩放级别
        /// </summary>
        public double ZoomLevel { get; init; } = 1.0;

        /// <summary>
        /// 视图的中心点位置
        /// </summary>
        public (double X, double Y) CenterPosition { get; init; } = (0, 0);

        /// <summary>
        /// 视图的描述信息
        /// </summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>
        /// 视图创建时间
        /// </summary>
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    }
}
