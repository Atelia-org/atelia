using System;
using System.Threading;
using System.Threading.Tasks;
using MemoTree.Core.Types;

namespace MemoTree.Core.Validation
{
    /// <summary>
    /// 默认业务规则验证器实现
    /// 提供基础的业务逻辑验证功能
    /// </summary>
    public class DefaultBusinessRuleValidator : IBusinessRuleValidator
    {
        /// <summary>
        /// 验证节点创建规则
        /// </summary>
        public Task<ValidationResult> ValidateNodeCreationAsync(NodeType type, NodeId? parentId, CancellationToken cancellationToken = default)
        {
            var builder = new ValidationResultBuilder()
                .ForObjectType("NodeCreation")
                .ForObjectId($"{type}");

            // 验证根节点创建规则
            if (parentId == null || parentId.Value.IsRoot)
            {
                // 只有容器类型可以作为根节点
                builder.AddWarningIf(
                    type != NodeType.Container,
                    ValidationWarning.ForBestPractice($"Creating root node of type {type}. Consider using Container type for better organization.")
                );
            }

            // 验证节点类型兼容性
            if (parentId != null && !parentId.Value.IsRoot)
            {
                // 这里需要实际的父节点类型，暂时跳过具体验证
                // 在实际实现中，需要从存储层获取父节点信息
                builder.AddWarning(ValidationWarning.Create("PARENT_TYPE_CHECK_SKIPPED", 
                    "Parent node type compatibility check requires storage access", "ParentType"));
            }

            return Task.FromResult(builder.Build());
        }

        /// <summary>
        /// 验证节点删除规则
        /// </summary>
        public Task<ValidationResult> ValidateNodeDeletionAsync(NodeId nodeId, bool recursive, CancellationToken cancellationToken = default)
        {
            var builder = new ValidationResultBuilder()
                .ForObjectType("NodeDeletion")
                .ForObjectId(nodeId.Value);

            // 验证根节点删除
            builder.AddErrorIf(
                nodeId.IsRoot,
                ValidationError.ForBusinessRule("RootNodeDeletion", "Root node cannot be deleted")
            );

            // 如果不是递归删除，需要检查是否有子节点
            if (!recursive)
            {
                // 这里需要实际的子节点检查，暂时添加警告
                builder.AddWarning(ValidationWarning.Create("CHILDREN_CHECK_SKIPPED", 
                    "Child nodes existence check requires storage access", "HasChildren"));
            }

            return Task.FromResult(builder.Build());
        }

        /// <summary>
        /// 验证节点移动规则
        /// </summary>
        public Task<ValidationResult> ValidateNodeMoveAsync(NodeId nodeId, NodeId? newParentId, CancellationToken cancellationToken = default)
        {
            var builder = new ValidationResultBuilder()
                .ForObjectType("NodeMove")
                .ForObjectId(nodeId.Value);

            // 验证根节点移动
            builder.AddErrorIf(
                nodeId.IsRoot,
                ValidationError.ForBusinessRule("RootNodeMove", "Root node cannot be moved")
            );

            // 验证移动到自己
            if (newParentId != null)
            {
                builder.AddErrorIf(
                    nodeId == newParentId,
                    ValidationError.ForBusinessRule("SelfParent", "Node cannot be moved to itself")
                );
            }

            // 循环引用检查需要存储层支持
            if (newParentId != null)
            {
                builder.AddWarning(ValidationWarning.Create("CIRCULAR_REFERENCE_CHECK_SKIPPED", 
                    "Circular reference check requires storage access", "CircularReference"));
            }

            return Task.FromResult(builder.Build());
        }

        /// <summary>
        /// 验证循环引用
        /// </summary>
        public Task<ValidationResult> ValidateCircularReferenceAsync(NodeId sourceId, NodeId targetId, CancellationToken cancellationToken = default)
        {
            var builder = new ValidationResultBuilder()
                .ForObjectType("CircularReference")
                .ForObjectId($"{sourceId}->{targetId}");

            // 直接自引用
            builder.AddErrorIf(
                sourceId == targetId,
                ValidationError.ForBusinessRule("DirectSelfReference", "Node cannot reference itself directly")
            );

            // 间接循环引用检查需要图遍历，需要存储层支持
            builder.AddWarning(ValidationWarning.Create("INDIRECT_CIRCULAR_CHECK_SKIPPED", 
                "Indirect circular reference check requires graph traversal", "IndirectCircular"));

            return Task.FromResult(builder.Build());
        }

        /// <summary>
        /// 验证关系创建规则
        /// </summary>
        public Task<ValidationResult> ValidateRelationCreationAsync(NodeId sourceId, NodeId targetId, RelationType relationType, CancellationToken cancellationToken = default)
        {
            var builder = new ValidationResultBuilder()
                .ForObjectType("RelationCreation")
                .ForObjectId($"{sourceId}-{relationType}->{targetId}");

            // 验证自引用
            builder.AddErrorIf(
                sourceId == targetId,
                ValidationError.ForBusinessRule("SelfRelation", "Node cannot have relation to itself")
            );

            // 验证关系类型的合理性
            switch (relationType)
            {
                case RelationType.InheritsFrom:
                    builder.AddWarning(ValidationWarning.ForBestPractice(
                        "Inheritance relations should be used carefully to maintain clear hierarchies"));
                    break;
                
                case RelationType.ComposedOf:
                    builder.AddWarning(ValidationWarning.ForBestPractice(
                        "Composition relations create strong coupling between nodes"));
                    break;
            }

            return Task.FromResult(builder.Build());
        }

        /// <summary>
        /// 验证节点层次深度
        /// </summary>
        public Task<ValidationResult> ValidateHierarchyDepthAsync(NodeId nodeId, NodeId? newParentId, CancellationToken cancellationToken = default)
        {
            var builder = new ValidationResultBuilder()
                .ForObjectType("HierarchyDepth")
                .ForObjectId(nodeId.Value);

            // 深度检查需要遍历整个层次结构，需要存储层支持
            builder.AddWarning(ValidationWarning.Create("DEPTH_CHECK_SKIPPED", 
                "Hierarchy depth check requires storage access", "Depth"));

            return Task.FromResult(builder.Build());
        }

        /// <summary>
        /// 验证子节点数量限制
        /// </summary>
        public Task<ValidationResult> ValidateChildrenCountAsync(NodeId parentId, CancellationToken cancellationToken = default)
        {
            var builder = new ValidationResultBuilder()
                .ForObjectType("ChildrenCount")
                .ForObjectId(parentId.Value);

            // 子节点数量检查需要存储层支持
            builder.AddWarning(ValidationWarning.Create("CHILDREN_COUNT_CHECK_SKIPPED", 
                "Children count check requires storage access", "ChildrenCount"));

            return Task.FromResult(builder.Build());
        }

        /// <summary>
        /// 验证节点类型兼容性
        /// </summary>
        public ValidationResult ValidateNodeTypeCompatibility(NodeType parentType, NodeType childType)
        {
            var builder = new ValidationResultBuilder()
                .ForObjectType("NodeTypeCompatibility");

            // 定义基本的类型兼容性规则
            var isCompatible = (parentType, childType) switch
            {
                // 容器可以包含任何类型
                (NodeType.Container, _) => true,
                
                // 概念可以包含相关的子概念
                (NodeType.Concept, NodeType.Concept) => true,
                (NodeType.Concept, NodeType.Note) => true,
                
                // 实体可以包含属性和注释
                (NodeType.Entity, NodeType.Property) => true,
                (NodeType.Entity, NodeType.Note) => true,
                
                // 过程可以包含步骤（其他过程）和注释
                (NodeType.Process, NodeType.Process) => true,
                (NodeType.Process, NodeType.Note) => true,
                
                // 问题可以包含解决方案
                (NodeType.Issue, NodeType.Solution) => true,
                (NodeType.Issue, NodeType.Note) => true,
                
                // 默认情况下允许，但给出警告
                _ => true
            };

            if (!isCompatible)
            {
                builder.AddError(ValidationError.ForBusinessRule("IncompatibleNodeTypes", 
                    $"Node type {childType} is not compatible with parent type {parentType}"));
            }
            else if ((parentType, childType) is not (NodeType.Container, _) and not (_, NodeType.Note))
            {
                // 对于非明确兼容的组合给出警告
                builder.AddWarning(ValidationWarning.ForBestPractice(
                    $"Consider the appropriateness of placing {childType} under {parentType}"));
            }

            return builder.Build();
        }

        /// <summary>
        /// 验证关系类型兼容性
        /// </summary>
        public ValidationResult ValidateRelationTypeCompatibility(NodeType sourceType, NodeType targetType, RelationType relationType)
        {
            var builder = new ValidationResultBuilder()
                .ForObjectType("RelationTypeCompatibility");

            // 定义关系类型兼容性规则
            var isRecommended = (sourceType, targetType, relationType) switch
            {
                // 继承关系通常在相同类型间
                (var s, var t, RelationType.InheritsFrom) when s == t => true,
                
                // 实现关系：实体实现概念
                (NodeType.Entity, NodeType.Concept, RelationType.Implements) => true,
                
                // 依赖关系：过程依赖实体或概念
                (NodeType.Process, NodeType.Entity, RelationType.DependsOn) => true,
                (NodeType.Process, NodeType.Concept, RelationType.DependsOn) => true,
                
                // 引用关系：任何类型都可以引用其他类型
                (_, _, RelationType.References) => true,
                
                // 关联关系：通用关系
                (_, _, RelationType.AssociatedWith) => true,
                
                // 默认允许但不推荐
                _ => false
            };

            if (!isRecommended)
            {
                builder.AddWarning(ValidationWarning.ForBestPractice(
                    $"Relation {relationType} between {sourceType} and {targetType} may not be the most appropriate choice"));
            }

            return builder.Build();
        }

        /// <summary>
        /// 验证重复关系
        /// </summary>
        public Task<ValidationResult> ValidateDuplicateRelationAsync(NodeId sourceId, NodeId targetId, RelationType relationType, CancellationToken cancellationToken = default)
        {
            var builder = new ValidationResultBuilder()
                .ForObjectType("DuplicateRelation")
                .ForObjectId($"{sourceId}-{relationType}->{targetId}");

            // 重复关系检查需要存储层支持
            builder.AddWarning(ValidationWarning.Create("DUPLICATE_CHECK_SKIPPED", 
                "Duplicate relation check requires storage access", "Duplicate"));

            return Task.FromResult(builder.Build());
        }

        /// <summary>
        /// 验证节点权限
        /// </summary>
        public Task<ValidationResult> ValidateNodePermissionAsync(NodeId nodeId, NodeOperation operation, CancellationToken cancellationToken = default)
        {
            var builder = new ValidationResultBuilder()
                .ForObjectType("NodePermission")
                .ForObjectId($"{nodeId}-{operation}");

            // 权限检查需要安全上下文，MVP阶段跳过
            builder.AddWarning(ValidationWarning.Create("PERMISSION_CHECK_SKIPPED", 
                "Permission check requires security context (MVP phase)", "Permission"));

            return Task.FromResult(builder.Build());
        }
    }
}
