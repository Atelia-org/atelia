using MemoTree.Core.Types;

namespace MemoTree.Core.Services
{
    /// <summary>
    /// 工作空间路径管理服务接口
    /// 提供统一的路径解析和管理，支持本地工作空间和链接工作空间
    /// </summary>
    public interface IWorkspacePathService
    {
        // 同步（只读路径）API —— 开发早期为降低复杂度而提供
        string GetWorkspaceRoot();
        string GetCogNodesDirectory();
        string GetHierarchyDirectory();
        string GetRelationsDirectory();
        string GetViewsDirectory();
        string GetNodeDirectory(NodeId nodeId);
        string GetNodeContentPath(NodeId nodeId, LodLevel level);
        string GetNodeMetadataPath(NodeId nodeId);
        bool IsWorkspace(string? directory = null);
        bool IsLinkedWorkspace();
        string? GetLinkTarget();

    /// <summary>
    /// 确保所有必要的目录存在（同步）
    /// </summary>
    void EnsureDirectoriesExist();
    }
}
