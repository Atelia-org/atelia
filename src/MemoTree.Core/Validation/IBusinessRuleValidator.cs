using System.Threading;
using System.Threading.Tasks;
using MemoTree.Core.Types;

namespace MemoTree.Core.Validation {
    /// <summary>
    /// 业务规则验证器接口
    /// 提供业务逻辑相关的验证功能
    /// </summary>
    public interface IBusinessRuleValidator {
        /// <summary>
        /// 验证节点创建规则
        /// </summary>
        /// <param name="type">节点类型</param>
        /// <param name="parentId">父节点ID（可选）</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>验证结果</returns>
        Task<ValidationResult> ValidateNodeCreationAsync(NodeType type, NodeId? parentId, CancellationToken cancellationToken = default);

        /// <summary>
        /// 验证节点删除规则
        /// </summary>
        /// <param name="nodeId">要删除的节点ID</param>
        /// <param name="recursive">是否递归删除子节点</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>验证结果</returns>
        Task<ValidationResult> ValidateNodeDeletionAsync(NodeId nodeId, bool recursive, CancellationToken cancellationToken = default);

        /// <summary>
        /// 验证节点移动规则
        /// </summary>
        /// <param name="nodeId">要移动的节点ID</param>
        /// <param name="newParentId">新父节点ID（可选）</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>验证结果</returns>
        Task<ValidationResult> ValidateNodeMoveAsync(NodeId nodeId, NodeId? newParentId, CancellationToken cancellationToken = default);

        /// <summary>
        /// 验证循环引用
        /// </summary>
        /// <param name="sourceId">源节点ID</param>
        /// <param name="targetId">目标节点ID</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>验证结果</returns>
        Task<ValidationResult> ValidateCircularReferenceAsync(NodeId sourceId, NodeId targetId, CancellationToken cancellationToken = default);

        /// <summary>
        /// 验证关系创建规则
        /// </summary>
        /// <param name="sourceId">源节点ID</param>
        /// <param name="targetId">目标节点ID</param>
        /// <param name="relationType">关系类型</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>验证结果</returns>
        Task<ValidationResult> ValidateRelationCreationAsync(NodeId sourceId, NodeId targetId, RelationType relationType, CancellationToken cancellationToken = default);

        /// <summary>
        /// 验证节点层次深度
        /// </summary>
        /// <param name="nodeId">节点ID</param>
        /// <param name="newParentId">新父节点ID（可选）</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>验证结果</returns>
        Task<ValidationResult> ValidateHierarchyDepthAsync(NodeId nodeId, NodeId? newParentId, CancellationToken cancellationToken = default);

        /// <summary>
        /// 验证子节点数量限制
        /// </summary>
        /// <param name="parentId">父节点ID</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>验证结果</returns>
        Task<ValidationResult> ValidateChildrenCountAsync(NodeId parentId, CancellationToken cancellationToken = default);

        /// <summary>
        /// 验证节点类型兼容性
        /// </summary>
        /// <param name="parentType">父节点类型</param>
        /// <param name="childType">子节点类型</param>
        /// <returns>验证结果</returns>
        ValidationResult ValidateNodeTypeCompatibility(NodeType parentType, NodeType childType);

        /// <summary>
        /// 验证关系类型兼容性
        /// </summary>
        /// <param name="sourceType">源节点类型</param>
        /// <param name="targetType">目标节点类型</param>
        /// <param name="relationType">关系类型</param>
        /// <returns>验证结果</returns>
        ValidationResult ValidateRelationTypeCompatibility(NodeType sourceType, NodeType targetType, RelationType relationType);

        /// <summary>
        /// 验证重复关系
        /// </summary>
        /// <param name="sourceId">源节点ID</param>
        /// <param name="targetId">目标节点ID</param>
        /// <param name="relationType">关系类型</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>验证结果</returns>
        Task<ValidationResult> ValidateDuplicateRelationAsync(NodeId sourceId, NodeId targetId, RelationType relationType, CancellationToken cancellationToken = default);

        /// <summary>
        /// 验证节点权限
        /// </summary>
        /// <param name="nodeId">节点ID</param>
        /// <param name="operation">操作类型</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>验证结果</returns>
        Task<ValidationResult> ValidateNodePermissionAsync(NodeId nodeId, NodeOperation operation, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// 节点操作类型
    /// </summary>
    public enum NodeOperation {
        /// <summary>
        /// 读取
        /// </summary>
        Read,

        /// <summary>
        /// 创建
        /// </summary>
        Create,

        /// <summary>
        /// 更新
        /// </summary>
        Update,

        /// <summary>
        /// 删除
        /// </summary>
        Delete,

        /// <summary>
        /// 移动
        /// </summary>
        Move,

        /// <summary>
        /// 创建关系
        /// </summary>
        CreateRelation,

        /// <summary>
        /// 删除关系
        /// </summary>
        DeleteRelation
    }
}
