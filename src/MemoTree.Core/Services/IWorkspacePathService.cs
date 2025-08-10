using MemoTree.Core.Types;

namespace MemoTree.Core.Services
{
    /// <summary>
    /// 工作空间路径管理服务接口
    /// 提供统一的路径解析和管理，支持本地工作空间和链接工作空间
    /// </summary>
    public interface IWorkspacePathService
    {
        /// <summary>
        /// 获取工作空间根目录（.memotree目录）
        /// </summary>
        /// <returns>工作空间根目录的绝对路径</returns>
        Task<string> GetWorkspaceRootAsync();

        /// <summary>
        /// 获取认知节点存储目录
        /// </summary>
        /// <returns>CogNodes目录的绝对路径</returns>
        Task<string> GetCogNodesDirectoryAsync();

        /// <summary>
        /// 获取层次关系存储目录
        /// </summary>
        /// <returns>hierarchy目录的绝对路径</returns>
        Task<string> GetHierarchyDirectoryAsync();

        /// <summary>
        /// 获取语义关系存储目录
        /// </summary>
        /// <returns>relations目录的绝对路径</returns>
        Task<string> GetRelationsDirectoryAsync();

        /// <summary>
        /// 获取视图状态存储目录
        /// </summary>
        /// <returns>views目录的绝对路径</returns>
        Task<string> GetViewsDirectoryAsync();

        /// <summary>
        /// 获取特定节点的存储目录
        /// </summary>
        /// <param name="nodeId">节点ID</param>
        /// <returns>节点存储目录的绝对路径</returns>
        Task<string> GetNodeDirectoryAsync(NodeId nodeId);

        /// <summary>
        /// 获取特定节点的内容文件路径
        /// </summary>
        /// <param name="nodeId">节点ID</param>
        /// <param name="level">LOD级别</param>
        /// <returns>内容文件的绝对路径</returns>
        Task<string> GetNodeContentPathAsync(NodeId nodeId, LodLevel level);

        /// <summary>
        /// 获取特定节点的元数据文件路径
        /// </summary>
        /// <param name="nodeId">节点ID</param>
        /// <returns>元数据文件的绝对路径</returns>
        Task<string> GetNodeMetadataPathAsync(NodeId nodeId);

        /// <summary>
        /// 检查当前目录是否为MemoTree工作空间
        /// </summary>
        /// <param name="directory">要检查的目录，null表示当前目录</param>
        /// <returns>如果是工作空间则返回true</returns>
        Task<bool> IsWorkspaceAsync(string? directory = null);

        /// <summary>
        /// 检查当前工作空间是否为链接工作空间
        /// </summary>
        /// <returns>如果是链接工作空间则返回true</returns>
        Task<bool> IsLinkedWorkspaceAsync();

        /// <summary>
        /// 获取链接工作空间的目标路径
        /// </summary>
        /// <returns>目标工作空间路径，如果不是链接工作空间则返回null</returns>
        Task<string?> GetLinkTargetAsync();

        /// <summary>
        /// 确保所有必要的目录存在
        /// </summary>
        Task EnsureDirectoriesExistAsync();
    }
}
