using MemoTree.Core.Types;

namespace MemoTree.Core.Storage.Interfaces
{
    /// <summary>
    /// 关系类型定义存储接口
    /// 管理关系类型的元数据和定义
    /// </summary>
    public interface IRelationTypeStorage
    {
        /// <summary>
        /// 获取关系类型定义
        /// </summary>
        /// <param name="relationType">关系类型</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>关系类型定义，如果不存在则返回null</returns>
        Task<RelationTypeDefinition?> GetRelationTypeAsync(
            RelationType relationType, 
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 保存关系类型定义
        /// </summary>
        /// <param name="definition">关系类型定义</param>
        /// <param name="cancellationToken">取消令牌</param>
        Task SaveRelationTypeAsync(
            RelationTypeDefinition definition, 
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取所有关系类型定义
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>所有关系类型定义列表</returns>
        Task<IReadOnlyList<RelationTypeDefinition>> GetAllRelationTypesAsync(
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 删除关系类型定义
        /// </summary>
        /// <param name="relationType">关系类型</param>
        /// <param name="cancellationToken">取消令牌</param>
        Task DeleteRelationTypeAsync(
            RelationType relationType, 
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 检查关系类型是否存在
        /// </summary>
        /// <param name="relationType">关系类型</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>如果关系类型存在则返回true</returns>
        Task<bool> ExistsAsync(
            RelationType relationType, 
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取内置关系类型定义
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>所有内置关系类型定义</returns>
        Task<IReadOnlyList<RelationTypeDefinition>> GetBuiltInRelationTypesAsync(
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取自定义关系类型定义
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>所有自定义关系类型定义</returns>
        Task<IReadOnlyList<RelationTypeDefinition>> GetCustomRelationTypesAsync(
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 批量保存关系类型定义
        /// </summary>
        /// <param name="definitions">关系类型定义集合</param>
        /// <param name="cancellationToken">取消令牌</param>
        Task SaveBatchAsync(
            IEnumerable<RelationTypeDefinition> definitions, 
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 初始化默认关系类型
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        Task InitializeDefaultTypesAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取关系类型使用统计
        /// </summary>
        /// <param name="relationType">关系类型</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>使用次数</returns>
        Task<int> GetUsageCountAsync(
            RelationType relationType, 
            CancellationToken cancellationToken = default);
    }
}
