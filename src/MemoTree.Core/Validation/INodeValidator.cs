using System.Threading;
using System.Threading.Tasks;
using MemoTree.Core.Types;

namespace MemoTree.Core.Validation
{
    /// <summary>
    /// 节点验证器接口
    /// 提供节点数据的验证功能
    /// </summary>
    public interface INodeValidator
    {
        /// <summary>
        /// 验证节点元数据
        /// </summary>
        /// <param name="metadata">要验证的节点元数据</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>验证结果</returns>
        Task<ValidationResult> ValidateMetadataAsync(NodeMetadata metadata, CancellationToken cancellationToken = default);

        /// <summary>
        /// 验证节点内容
        /// </summary>
        /// <param name="content">要验证的节点内容</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>验证结果</returns>
        Task<ValidationResult> ValidateContentAsync(NodeContent content, CancellationToken cancellationToken = default);

        /// <summary>
        /// 验证完整节点
        /// </summary>
        /// <param name="node">要验证的认知节点</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>验证结果</returns>
        Task<ValidationResult> ValidateNodeAsync(CognitiveNode node, CancellationToken cancellationToken = default);

        /// <summary>
        /// 验证节点关系
        /// </summary>
        /// <param name="relation">要验证的节点关系</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>验证结果</returns>
        Task<ValidationResult> ValidateRelationAsync(NodeRelation relation, CancellationToken cancellationToken = default);

        /// <summary>
        /// 验证节点关系（通过ID和类型）
        /// </summary>
        /// <param name="sourceId">源节点ID</param>
        /// <param name="targetId">目标节点ID</param>
        /// <param name="relationType">关系类型</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>验证结果</returns>
        Task<ValidationResult> ValidateRelationAsync(NodeId sourceId, NodeId targetId, RelationType relationType, CancellationToken cancellationToken = default);

        /// <summary>
        /// 验证父子关系信息
        /// </summary>
        /// <param name="parentChildrenInfo">要验证的父子关系信息</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>验证结果</returns>
        Task<ValidationResult> ValidateParentChildrenInfoAsync(ParentChildrenInfo parentChildrenInfo, CancellationToken cancellationToken = default);

        /// <summary>
        /// 验证节点ID格式
        /// </summary>
        /// <param name="nodeId">要验证的节点ID</param>
        /// <returns>验证结果</returns>
        ValidationResult ValidateNodeId(NodeId nodeId);

        /// <summary>
        /// 验证关系ID格式
        /// </summary>
        /// <param name="relationId">要验证的关系ID</param>
        /// <returns>验证结果</returns>
        ValidationResult ValidateRelationId(RelationId relationId);

        /// <summary>
        /// 验证LOD内容
        /// </summary>
        /// <param name="lodContent">要验证的LOD内容</param>
        /// <returns>验证结果</returns>
        ValidationResult ValidateLodContent(LodContent lodContent);

        /// <summary>
        /// 验证自定义属性
        /// </summary>
        /// <param name="properties">要验证的自定义属性</param>
        /// <returns>验证结果</returns>
        ValidationResult ValidateCustomProperties(IReadOnlyDictionary<string, object> properties);
    }
}
