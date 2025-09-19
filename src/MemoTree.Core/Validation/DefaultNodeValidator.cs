using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MemoTree.Core.Types;

namespace MemoTree.Core.Validation {
    /// <summary>
    /// 默认节点验证器实现
    /// 提供基础的节点数据验证功能
    /// </summary>
    public class DefaultNodeValidator : INodeValidator {
        /// <summary>
        /// 验证节点元数据
        /// </summary>
        public Task<ValidationResult> ValidateMetadataAsync(NodeMetadata metadata, CancellationToken cancellationToken = default) {
            var builder = new ValidationResultBuilder()
            .ForObjectType(nameof(NodeMetadata))
            .ForObjectId(metadata.Id.Value);

            // 验证节点ID
            var nodeIdResult = ValidateNodeId(metadata.Id);
            if (!nodeIdResult.IsValid) {
                foreach (var error in nodeIdResult.Errors) {
                    builder.AddError(error);
                }
            }

            // 验证标题
            builder.AddErrorIf(
                string.IsNullOrWhiteSpace(metadata.Title),
                ValidationError.ForRequired(nameof(metadata.Title))
            );

            builder.AddErrorIf(
                metadata.Title.Length > NodeConstraints.MaxTitleLength,
                ValidationError.ForLength(nameof(metadata.Title), metadata.Title.Length, NodeConstraints.MaxTitleLength)
            );

            // 验证标签
            builder.AddErrorIf(
                metadata.Tags.Count > NodeConstraints.MaxTagCount,
                ValidationError.ForRange(nameof(metadata.Tags), metadata.Tags.Count, 0, NodeConstraints.MaxTagCount)
            );

            foreach (var tag in metadata.Tags) {
                builder.AddErrorIf(
                    string.IsNullOrWhiteSpace(tag),
                    ValidationError.ForProperty("Tag", "Tag cannot be empty")
                );

                builder.AddErrorIf(
                    tag.Length > NodeConstraints.MaxTagLength,
                    ValidationError.ForLength("Tag", tag.Length, NodeConstraints.MaxTagLength)
                );
            }

            // 验证自定义属性
            var propertiesResult = ValidateCustomProperties(metadata.CustomProperties);
            if (!propertiesResult.IsValid) {
                foreach (var error in propertiesResult.Errors) {
                    builder.AddError(error);
                }
            }

            return Task.FromResult(builder.Build());
        }

        /// <summary>
        /// 验证节点内容
        /// </summary>
        public Task<ValidationResult> ValidateContentAsync(NodeContent content, CancellationToken cancellationToken = default) {
            var builder = new ValidationResultBuilder()
            .ForObjectType(nameof(NodeContent))
            .ForObjectId(content.Id.Value);

            // 验证节点ID
            var nodeIdResult = ValidateNodeId(content.Id);
            if (!nodeIdResult.IsValid) {
                foreach (var error in nodeIdResult.Errors) {
                    builder.AddError(error);
                }
            }

            // 验证内容长度
            builder.AddErrorIf(
                content.Content.Length > NodeConstraints.MaxContentLength,
                ValidationError.ForLength(nameof(content.Content), content.Content.Length, NodeConstraints.MaxContentLength)
            );

            // 验证LOD级别内容
            var lodResult = ValidateLodContent(LodContent.Create(content.Level, content.Content));
            if (!lodResult.IsValid) {
                foreach (var error in lodResult.Errors) {
                    builder.AddError(error);
                }
            }

            // 验证内容哈希
            builder.AddErrorIf(
                !content.IsHashValid(),
                ValidationError.ForProperty(nameof(content.ContentHash), "Content hash does not match content")
            );

            return Task.FromResult(builder.Build());
        }

        /// <summary>
        /// 验证完整节点
        /// </summary>
        public async Task<ValidationResult> ValidateNodeAsync(CognitiveNode node, CancellationToken cancellationToken = default) {
            var builder = new ValidationResultBuilder()
            .ForObjectType(nameof(CognitiveNode))
            .ForObjectId(node.Id.Value);

            // 验证元数据
            var metadataResult = await ValidateMetadataAsync(node.Metadata, cancellationToken);
            if (!metadataResult.IsValid) {
                foreach (var error in metadataResult.Errors) {
                    builder.AddError(error);
                }
            }

            // 验证所有内容
            foreach (var content in node.Contents.Values) {
                var contentResult = await ValidateContentAsync(content, cancellationToken);
                if (!contentResult.IsValid) {
                    foreach (var error in contentResult.Errors) {
                        builder.AddError(error);
                    }
                }
            }

            // 验证节点一致性
            builder.AddErrorIf(
                !node.IsValid(),
                ValidationError.ForProperty("Consistency", "Node data is inconsistent")
            );

            return builder.Build();
        }

        /// <summary>
        /// 验证节点关系
        /// </summary>
        public Task<ValidationResult> ValidateRelationAsync(NodeRelation relation, CancellationToken cancellationToken = default) {
            var builder = new ValidationResultBuilder()
            .ForObjectType(nameof(NodeRelation))
            .ForObjectId(relation.Id.Value);

            // 验证关系ID
            var relationIdResult = ValidateRelationId(relation.Id);
            if (!relationIdResult.IsValid) {
                foreach (var error in relationIdResult.Errors) {
                    builder.AddError(error);
                }
            }

            // 验证源节点ID
            var sourceIdResult = ValidateNodeId(relation.SourceId);
            if (!sourceIdResult.IsValid) {
                foreach (var error in sourceIdResult.Errors) {
                    builder.AddError(error);
                }
            }

            // 验证目标节点ID
            var targetIdResult = ValidateNodeId(relation.TargetId);
            if (!targetIdResult.IsValid) {
                foreach (var error in targetIdResult.Errors) {
                    builder.AddError(error);
                }
            }

            // 验证自引用
            builder.AddErrorIf(
                relation.SourceId == relation.TargetId,
                ValidationError.ForBusinessRule("SelfReference", "Node cannot have relation to itself")
            );

            // 验证描述长度
            builder.AddErrorIf(
                relation.Description.Length > NodeConstraints.MaxRelationDescriptionLength,
                ValidationError.ForLength(nameof(relation.Description), relation.Description.Length, NodeConstraints.MaxRelationDescriptionLength)
            );

            // 验证关系属性
            var propertiesResult = ValidateCustomProperties(relation.Properties);
            if (!propertiesResult.IsValid) {
                foreach (var error in propertiesResult.Errors) {
                    builder.AddError(error);
                }
            }

            return Task.FromResult(builder.Build());
        }

        /// <summary>
        /// 验证节点关系（通过ID和类型）
        /// </summary>
        public Task<ValidationResult> ValidateRelationAsync(NodeId sourceId, NodeId targetId, RelationType relationType, CancellationToken cancellationToken = default) {
            var builder = new ValidationResultBuilder()
            .ForObjectType("NodeRelation")
            .ForObjectId($"{sourceId}->{targetId}");

            // 验证源节点ID
            var sourceIdResult = ValidateNodeId(sourceId);
            if (!sourceIdResult.IsValid) {
                foreach (var error in sourceIdResult.Errors) {
                    builder.AddError(error);
                }
            }

            // 验证目标节点ID
            var targetIdResult = ValidateNodeId(targetId);
            if (!targetIdResult.IsValid) {
                foreach (var error in targetIdResult.Errors) {
                    builder.AddError(error);
                }
            }

            // 验证自引用
            builder.AddErrorIf(
                sourceId == targetId,
                ValidationError.ForBusinessRule("SelfReference", "Node cannot have relation to itself")
            );

            return Task.FromResult(builder.Build());
        }

        /// <summary>
        /// 验证父子关系信息
        /// </summary>
        public Task<ValidationResult> ValidateHierarchyInfoAsync(HierarchyInfo HierarchyInfo, CancellationToken cancellationToken = default) {
            var builder = new ValidationResultBuilder()
            .ForObjectType(nameof(HierarchyInfo))
            .ForObjectId(HierarchyInfo.ParentId.Value);

            // 验证父节点ID
            var parentIdResult = ValidateNodeId(HierarchyInfo.ParentId);
            if (!parentIdResult.IsValid) {
                foreach (var error in parentIdResult.Errors) {
                    builder.AddError(error);
                }
            }

            // 验证子节点数量
            builder.AddErrorIf(
                HierarchyInfo.ChildCount > NodeConstraints.MaxChildNodeCount,
                ValidationError.ForRange("ChildCount", HierarchyInfo.ChildCount, 0, NodeConstraints.MaxChildNodeCount)
            );

            // 验证每个子节点
            foreach (var child in HierarchyInfo.Children) {
                var childIdResult = ValidateNodeId(child.NodeId);
                if (!childIdResult.IsValid) {
                    foreach (var error in childIdResult.Errors) {
                        builder.AddError(error);
                    }
                }

                // 验证子节点不是父节点自己
                builder.AddErrorIf(
                    child.NodeId == HierarchyInfo.ParentId,
                    ValidationError.ForBusinessRule("SelfParent", "Node cannot be parent of itself")
                );
            }

            // 验证子节点ID唯一性
            var duplicateIds = HierarchyInfo.Children
            .GroupBy(c => c.NodeId)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);

            foreach (var duplicateId in duplicateIds) {
                builder.AddError(ValidationError.ForBusinessRule("DuplicateChild", $"Duplicate child node ID: {duplicateId}"));
            }

            return Task.FromResult(builder.Build());
        }

        /// <summary>
        /// 验证节点ID格式
        /// </summary>
        public ValidationResult ValidateNodeId(NodeId nodeId) {
            var builder = new ValidationResultBuilder()
            .ForObjectType(nameof(NodeId))
            .ForObjectId(nodeId.Value);

            builder.AddErrorIf(
                string.IsNullOrWhiteSpace(nodeId.Value),
                ValidationError.ForRequired("NodeId")
            );

            builder.AddErrorIf(
                nodeId.Value.Length > NodeConstraints.MaxNodeIdLength,
                ValidationError.ForLength("NodeId", nodeId.Value.Length, NodeConstraints.MaxNodeIdLength)
            );

            builder.AddErrorIf(
                !NodeId.IsValidFormat(nodeId.Value),
                ValidationError.ForFormat("NodeId", "Valid GUID encoding format")
            );

            return builder.Build();
        }

        /// <summary>
        /// 验证关系ID格式
        /// </summary>
        public ValidationResult ValidateRelationId(RelationId relationId) {
            var builder = new ValidationResultBuilder()
            .ForObjectType(nameof(RelationId))
            .ForObjectId(relationId.Value);

            builder.AddErrorIf(
                string.IsNullOrWhiteSpace(relationId.Value),
                ValidationError.ForRequired("RelationId")
            );

            builder.AddErrorIf(
                relationId.Value.Length > NodeConstraints.MaxRelationIdLength,
                ValidationError.ForLength("RelationId", relationId.Value.Length, NodeConstraints.MaxRelationIdLength)
            );

            builder.AddErrorIf(
                !RelationId.IsValidFormat(relationId.Value),
                ValidationError.ForFormat("RelationId", "Valid GUID encoding format")
            );

            return builder.Build();
        }

        /// <summary>
        /// 验证LOD内容
        /// </summary>
        public ValidationResult ValidateLodContent(LodContent lodContent) {
            var builder = new ValidationResultBuilder()
            .ForObjectType(nameof(LodContent));

            builder.AddErrorIf(
                !lodContent.IsValidForLevel(),
                ValidationError.ForProperty("Content", $"Content length {lodContent.Length} is invalid for LOD level {lodContent.Level}")
            );

            return builder.Build();
        }

        /// <summary>
        /// 验证自定义属性
        /// </summary>
        public ValidationResult ValidateCustomProperties(IReadOnlyDictionary<string, object> properties) {
            var builder = new ValidationResultBuilder()
            .ForObjectType("CustomProperties");

            builder.AddErrorIf(
                properties.Count > NodeConstraints.MaxCustomPropertyCount,
                ValidationError.ForRange("PropertyCount", properties.Count, 0, NodeConstraints.MaxCustomPropertyCount)
            );

            foreach (var kvp in properties) {
                builder.AddErrorIf(
                    string.IsNullOrWhiteSpace(kvp.Key),
                    ValidationError.ForProperty("PropertyKey", "Property key cannot be empty")
                );

                builder.AddErrorIf(
                    kvp.Key.Length > NodeConstraints.MaxCustomPropertyKeyLength,
                    ValidationError.ForLength("PropertyKey", kvp.Key.Length, NodeConstraints.MaxCustomPropertyKeyLength)
                );

                // 验证字符串值长度
                if (kvp.Value is string stringValue) {
                    builder.AddErrorIf(
                        stringValue.Length > NodeConstraints.MaxCustomPropertyStringValueLength,
                        ValidationError.ForLength($"Property[{kvp.Key}]", stringValue.Length, NodeConstraints.MaxCustomPropertyStringValueLength)
                    );
                }
            }

            return builder.Build();
        }
    }
}
