using System;
using System.Collections.Generic;
using System.Linq;

namespace MemoTree.Core.Types {
    /// <summary>
    /// 父子关系信息（独立存储）
    /// </summary>
    public record HierarchyInfo {
        public NodeId ParentId {
            get; init;
        }
        public IReadOnlyList<ChildNodeInfo> Children { get; init; } = Array.Empty<ChildNodeInfo>();
        public DateTime LastModified { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// 子节点数量
        /// </summary>
        public int ChildCount => Children.Count;

        /// <summary>
        /// 是否有子节点
        /// </summary>
        public bool HasChildren => Children.Count > 0;

        /// <summary>
        /// 创建新的父子关系信息
        /// </summary>
        public static HierarchyInfo Create(NodeId parentId, IEnumerable<ChildNodeInfo>? children = null) {
            return new HierarchyInfo {
                ParentId = parentId,
                Children = children?.ToList() ?? new List<ChildNodeInfo>(),
                LastModified = DateTime.UtcNow
            };
        }

        /// <summary>
        /// 添加子节点
        /// </summary>
        public HierarchyInfo AddChild(NodeId childId, int? order = null) {
            if (HasChild(childId)) {
                return this;
            }

            var newOrder = order ?? GetNextOrder();
            var newChild = new ChildNodeInfo {
                NodeId = childId,
                CreatedAt = DateTime.UtcNow,
                Order = newOrder
            };

            var newChildren = Children.ToList();
            newChildren.Add(newChild);

            return this with {
                Children = newChildren.OrderBy(c => c.Order).ToList(),
                LastModified = DateTime.UtcNow
            };
        }

        /// <summary>
        /// 移除子节点
        /// </summary>
        public HierarchyInfo RemoveChild(NodeId childId) {
            if (!HasChild(childId)) {
                return this;
            }

            var newChildren = Children.Where(c => c.NodeId != childId).ToList();

            return this with {
                Children = newChildren,
                LastModified = DateTime.UtcNow
            };
        }

        /// <summary>
        /// 更新子节点顺序
        /// </summary>
        public HierarchyInfo UpdateChildOrder(NodeId childId, int newOrder) {
            var childIndex = Children.ToList().FindIndex(c => c.NodeId == childId);
            if (childIndex == -1) {
                return this;
            }

            var newChildren = Children.ToList();
            newChildren[childIndex] = newChildren[childIndex] with {
                Order = newOrder
            };

            return this with {
                Children = newChildren.OrderBy(c => c.Order).ToList(),
                LastModified = DateTime.UtcNow
            };
        }

        /// <summary>
        /// 重新排序所有子节点
        /// </summary>
        public HierarchyInfo ReorderChildren(IEnumerable<NodeId> orderedChildIds) {
            var childDict = Children.ToDictionary(c => c.NodeId, c => c);
            var newChildren = new List<ChildNodeInfo>();
            var order = 0;

            foreach (var childId in orderedChildIds) {
                if (childDict.TryGetValue(childId, out var child)) {
                    newChildren.Add(
                        child with {
                            Order = order++
                        }
                    );
                }
            }

            // 添加未在新顺序中指定的子节点
            foreach (var child in Children) {
                if (!orderedChildIds.Contains(child.NodeId)) {
                    newChildren.Add(
                        child with {
                            Order = order++
                        }
                    );
                }
            }

            return this with {
                Children = newChildren,
                LastModified = DateTime.UtcNow
            };
        }

        /// <summary>
        /// 检查是否包含指定子节点
        /// </summary>
        public bool HasChild(NodeId childId) {
            return Children.Any(c => c.NodeId == childId);
        }

        /// <summary>
        /// 获取子节点信息
        /// </summary>
        public ChildNodeInfo? GetChild(NodeId childId) {
            return Children.FirstOrDefault(c => c.NodeId == childId);
        }

        /// <summary>
        /// 获取所有子节点ID（按顺序）
        /// </summary>
        public IEnumerable<NodeId> GetChildIds() {
            return Children.OrderBy(c => c.Order).Select(c => c.NodeId);
        }

        /// <summary>
        /// 获取下一个可用的顺序号
        /// </summary>
        private int GetNextOrder() {
            return Children.Count > 0 ? Children.Max(c => c.Order) + 1 : 0;
        }

        /// <summary>
        /// 获取子节点在指定位置的信息
        /// </summary>
        public ChildNodeInfo? GetChildAtPosition(int position) {
            var orderedChildren = Children.OrderBy(c => c.Order).ToList();
            return position >= 0 && position < orderedChildren.Count ? orderedChildren[position] : null;
        }

        /// <summary>
        /// 获取子节点的位置
        /// </summary>
        public int GetChildPosition(NodeId childId) {
            var orderedChildren = Children.OrderBy(c => c.Order).ToList();
            return orderedChildren.FindIndex(c => c.NodeId == childId);
        }

        /// <summary>
        /// 移动子节点到指定位置
        /// </summary>
        public HierarchyInfo MoveChildToPosition(NodeId childId, int newPosition) {
            if (!HasChild(childId) || newPosition < 0 || newPosition >= Children.Count) {
                return this;
            }

            var orderedChildren = Children.OrderBy(c => c.Order).ToList();
            var currentPosition = orderedChildren.FindIndex(c => c.NodeId == childId);

            if (currentPosition == newPosition) {
                return this;
            }

            // 移除并重新插入
            var child = orderedChildren[currentPosition];
            orderedChildren.RemoveAt(currentPosition);
            orderedChildren.Insert(newPosition, child);

            // 重新分配顺序号
            for (int i = 0; i < orderedChildren.Count; i++) {
                orderedChildren[i] = orderedChildren[i] with {
                    Order = i
                };
            }

            return this with {
                Children = orderedChildren,
                LastModified = DateTime.UtcNow
            };
        }
    }

    /// <summary>
    /// 子节点信息
    /// </summary>
    public record ChildNodeInfo {
        public NodeId NodeId {
            get; init;
        }
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
        public int Order { get; init; } = 0;

        /// <summary>
        /// 创建新的子节点信息
        /// </summary>
        public static ChildNodeInfo Create(NodeId nodeId, int order = 0) {
            return new ChildNodeInfo {
                NodeId = nodeId,
                CreatedAt = DateTime.UtcNow,
                Order = order
            };
        }
    }
}
