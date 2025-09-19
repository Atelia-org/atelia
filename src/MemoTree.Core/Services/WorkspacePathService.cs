using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MemoTree.Core.Configuration;
using MemoTree.Core.Types;

namespace MemoTree.Core.Services {
    /// <summary>
    /// 工作空间路径管理服务实现
    /// 基于.memotree目录的统一路径管理
    /// </summary>
    public class WorkspacePathService : IWorkspacePathService {
        private readonly MemoTreeOptions _options;
        private readonly StorageOptions _storageOptions;
        private readonly ILogger<WorkspacePathService> _logger;

        private const string WorkspaceDirectoryName = ".memotree";
        private const string ViewsDirectoryName = "views";
        private const string LinkConfigFileName = "link.json";

        private string? _cachedWorkspaceRoot;

        public WorkspacePathService(
            IOptions<MemoTreeOptions> options,
            IOptions<StorageOptions> storageOptions,
            ILogger<WorkspacePathService> logger
        ) {
            _options = options.Value;
            _storageOptions = storageOptions.Value;
            _logger = logger;

            // 注意：_options.WorkspaceRoot 是项目根目录，不是 .memotree 目录
            _logger.LogDebug("WorkspacePathService initialized with WorkspaceRoot: {WorkspaceRoot}", _options.WorkspaceRoot);
        }

        // 同步（只读路径）API
        public string GetWorkspaceRoot() {
            if (_cachedWorkspaceRoot != null) { return _cachedWorkspaceRoot; }
            var projectRoot = _options.WorkspaceRoot;
            var workspaceDir = Path.Combine(projectRoot, WorkspaceDirectoryName);
            if (!Directory.Exists(workspaceDir)) {
                _logger.LogWarning("Workspace directory not found at {WorkspaceDir}, will be created when needed", workspaceDir);
            }
            _cachedWorkspaceRoot = workspaceDir;
            _logger.LogDebug("Using workspace directory: {WorkspaceDir}", workspaceDir);
            return workspaceDir;
        }

        public string GetCogNodesDirectory() {
            var workspaceRoot = GetWorkspaceRoot();
            var linkTarget = GetLinkTarget();
            return linkTarget != null
            ? Path.Combine(linkTarget, WorkspaceDirectoryName, _options.CogNodesDirectory)
            : Path.Combine(workspaceRoot, _options.CogNodesDirectory);
        }

        public string GetHierarchyDirectory() {
            var workspaceRoot = GetWorkspaceRoot();
            var linkTarget = GetLinkTarget();
            return linkTarget != null
            ? Path.Combine(linkTarget, WorkspaceDirectoryName, _options.HierarchyDirectory)
            : Path.Combine(workspaceRoot, _options.HierarchyDirectory);
        }

        public string GetRelationsDirectory() {
            var workspaceRoot = GetWorkspaceRoot();
            var linkTarget = GetLinkTarget();
            return linkTarget != null
            ? Path.Combine(linkTarget, WorkspaceDirectoryName, _options.RelationsDirectory)
            : Path.Combine(workspaceRoot, _options.RelationsDirectory);
        }

        public string GetViewsDirectory() {
            var workspaceRoot = GetWorkspaceRoot();
            var linkTarget = GetLinkTarget();
            return linkTarget != null
            ? Path.Combine(linkTarget, WorkspaceDirectoryName, ViewsDirectoryName)
            : Path.Combine(workspaceRoot, ViewsDirectoryName);
        }

        public string GetNodeDirectory(NodeId nodeId) {
            var cogNodesDir = GetCogNodesDirectory();
            return Path.Combine(cogNodesDir, nodeId.Value);
        }

        public string GetNodeContentPath(NodeId nodeId, LodLevel level) {
            var nodeDir = GetNodeDirectory(nodeId);
            var fileName = level switch {
                LodLevel.Gist => _storageOptions.GistContentFileName,
                LodLevel.Summary => _storageOptions.SummaryContentFileName,
                LodLevel.Full => _storageOptions.FullContentFileName,
                _ => throw new ArgumentException($"Unsupported LOD level: {level}")
            };
            return Path.Combine(nodeDir, fileName);
        }

        public string GetNodeMetadataPath(NodeId nodeId) {
            var nodeDir = GetNodeDirectory(nodeId);
            return Path.Combine(nodeDir, _storageOptions.MetadataFileName);
        }

        public bool IsWorkspace(string? directory = null) {
            directory ??= Directory.GetCurrentDirectory();
            var workspaceDir = FindWorkspaceDirectory(directory);
            return workspaceDir != null;
        }

        public bool IsLinkedWorkspace() {
            var linkTarget = GetLinkTarget();
            return linkTarget != null;
        }

        public string? GetLinkTarget() {
            var workspaceRoot = GetWorkspaceRoot();
            var linkConfigPath = Path.Combine(workspaceRoot, LinkConfigFileName);
            if (!File.Exists(linkConfigPath)) { return null; }
            try {
                var linkConfigJson = File.ReadAllText(linkConfigPath);
                var linkConfig = JsonSerializer.Deserialize<Dictionary<string, object>>(linkConfigJson);
                if (linkConfig?.TryGetValue("target", out var targetObj) == true && targetObj is JsonElement targetElement) { return targetElement.GetString(); }
            }
            catch (Exception ex) {
                _logger.LogWarning(ex, "Failed to read link configuration from {LinkConfigPath}", linkConfigPath);
            }
            return null;
        }

        private string? FindWorkspaceDirectory(string startDirectory) {
            var currentDir = new DirectoryInfo(startDirectory);
            while (currentDir != null) {
                var workspaceDir = Path.Combine(currentDir.FullName, WorkspaceDirectoryName);
                if (Directory.Exists(workspaceDir)) {
                    _logger.LogDebug("Found workspace directory: {WorkspaceDir}", workspaceDir);
                    return workspaceDir;
                }
                currentDir = currentDir.Parent;
            }
            _logger.LogDebug("Workspace directory not found starting from: {StartDirectory}", startDirectory);
            return null;
        }

        // 下面移除了异步API，统一使用同步版本



        /// <summary>
        /// 获取认知节点存储目录
        /// </summary>
        // 原异步API已删除，使用同步版本 GetCogNodesDirectory()

        /// <summary>
        /// 获取层次关系存储目录
        /// </summary>
        // 原异步API已删除，使用同步版本 GetHierarchyDirectory()

        /// <summary>
        /// 获取语义关系存储目录
        /// </summary>
        // 原异步API已删除，使用同步版本 GetRelationsDirectory()

        /// <summary>
        /// 获取视图状态存储目录
        /// </summary>
        // 原异步API已删除，使用同步版本 GetViewsDirectory()

        /// <summary>
        /// 获取特定节点的存储目录
        /// </summary>
        // 原异步API已删除，使用同步版本 GetNodeDirectory()

        /// <summary>
        /// 获取特定节点的内容文件路径
        /// </summary>
        // 原异步API已删除，使用同步版本 GetNodeContentPath()

        /// <summary>
        /// 获取特定节点的元数据文件路径
        /// </summary>
        // 原异步API已删除，使用同步版本 GetNodeMetadataPath()

        /// <summary>
        /// 检查当前目录是否为MemoTree工作空间
        /// </summary>
        // 原异步API已删除，使用同步版本 IsWorkspace()

        /// <summary>
        /// 检查当前工作空间是否为链接工作空间
        /// </summary>
        // 原异步API已删除，使用同步版本 IsLinkedWorkspace()

        /// <summary>
        /// 获取链接工作空间的目标路径
        /// </summary>
        // 原异步API已删除，使用同步版本 GetLinkTarget()

        /// <summary>
        /// 确保所有必要的目录存在
        /// </summary>
        public void EnsureDirectoriesExist() {
            var directories = new[]
            {
                GetWorkspaceRoot(),
                GetCogNodesDirectory(),
                GetHierarchyDirectory(),
                GetRelationsDirectory(),
                GetViewsDirectory()
 };

            foreach (var directory in directories) {
                if (!Directory.Exists(directory)) {
                    Directory.CreateDirectory(directory);
                    _logger.LogDebug("Created directory: {Directory}", directory);
                }
            }
        }

        // 移除异步查找方法，使用同步 FindWorkspaceDirectory()
    }
}
