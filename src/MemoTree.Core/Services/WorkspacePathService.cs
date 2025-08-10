using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MemoTree.Core.Configuration;
using MemoTree.Core.Types;

namespace MemoTree.Core.Services
{
    /// <summary>
    /// 工作空间路径管理服务实现
    /// 基于.memotree目录的统一路径管理
    /// </summary>
    public class WorkspacePathService : IWorkspacePathService
    {
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
            ILogger<WorkspacePathService> logger)
        {
            _options = options.Value;
            _storageOptions = storageOptions.Value;
            _logger = logger;

            // 注意：_options.WorkspaceRoot 是项目根目录，不是 .memotree 目录
            _logger.LogDebug("WorkspacePathService initialized with WorkspaceRoot: {WorkspaceRoot}", _options.WorkspaceRoot);
        }

        /// <summary>
        /// 获取工作空间根目录（.memotree目录）
        /// </summary>
        public async Task<string> GetWorkspaceRootAsync()
        {
            if (_cachedWorkspaceRoot != null)
                return _cachedWorkspaceRoot;

            // 基于配置的WorkspaceRoot查找.memotree目录
            var projectRoot = _options.WorkspaceRoot;
            var workspaceDir = Path.Combine(projectRoot, WorkspaceDirectoryName);

            if (!Directory.Exists(workspaceDir))
            {
                _logger.LogWarning("Workspace directory not found at {WorkspaceDir}, will be created when needed", workspaceDir);
            }

            _cachedWorkspaceRoot = workspaceDir;
            _logger.LogDebug("Using workspace directory: {WorkspaceDir}", workspaceDir);
            return workspaceDir;
        }



        /// <summary>
        /// 获取认知节点存储目录
        /// </summary>
        public async Task<string> GetCogNodesDirectoryAsync()
        {
            var workspaceRoot = await GetWorkspaceRootAsync();

            // 检查是否是链接工作空间
            var linkTarget = await GetLinkTargetAsync();
            if (linkTarget != null)
            {
                return Path.Combine(linkTarget, WorkspaceDirectoryName, _options.CogNodesDirectory);
            }
            else
            {
                return Path.Combine(workspaceRoot, _options.CogNodesDirectory);
            }
        }

        /// <summary>
        /// 获取层次关系存储目录
        /// </summary>
        public async Task<string> GetHierarchyDirectoryAsync()
        {
            var workspaceRoot = await GetWorkspaceRootAsync();

            // 检查是否是链接工作空间
            var linkTarget = await GetLinkTargetAsync();
            if (linkTarget != null)
            {
                return Path.Combine(linkTarget, WorkspaceDirectoryName, _options.HierarchyDirectory);
            }
            else
            {
                return Path.Combine(workspaceRoot, _options.HierarchyDirectory);
            }
        }

        /// <summary>
        /// 获取语义关系存储目录
        /// </summary>
        public async Task<string> GetRelationsDirectoryAsync()
        {
            var workspaceRoot = await GetWorkspaceRootAsync();

            // 检查是否是链接工作空间
            var linkTarget = await GetLinkTargetAsync();
            if (linkTarget != null)
            {
                return Path.Combine(linkTarget, WorkspaceDirectoryName, _options.RelationsDirectory);
            }
            else
            {
                return Path.Combine(workspaceRoot, _options.RelationsDirectory);
            }
        }

        /// <summary>
        /// 获取视图状态存储目录
        /// </summary>
        public async Task<string> GetViewsDirectoryAsync()
        {
            var workspaceRoot = await GetWorkspaceRootAsync();

            // 检查是否是链接工作空间
            var linkTarget = await GetLinkTargetAsync();
            if (linkTarget != null)
            {
                return Path.Combine(linkTarget, WorkspaceDirectoryName, ViewsDirectoryName);
            }
            else
            {
                return Path.Combine(workspaceRoot, ViewsDirectoryName);
            }
        }

        /// <summary>
        /// 获取特定节点的存储目录
        /// </summary>
        public async Task<string> GetNodeDirectoryAsync(NodeId nodeId)
        {
            var cogNodesDir = await GetCogNodesDirectoryAsync();
            return Path.Combine(cogNodesDir, nodeId.Value);
        }

        /// <summary>
        /// 获取特定节点的内容文件路径
        /// </summary>
        public async Task<string> GetNodeContentPathAsync(NodeId nodeId, LodLevel level)
        {
            var nodeDir = await GetNodeDirectoryAsync(nodeId);
            var fileName = level switch
            {
                LodLevel.Gist => _storageOptions.GistContentFileName,
                LodLevel.Summary => _storageOptions.SummaryContentFileName,
                LodLevel.Full => _storageOptions.FullContentFileName,
                _ => throw new ArgumentException($"Unsupported LOD level: {level}")
            };
            return Path.Combine(nodeDir, fileName);
        }

        /// <summary>
        /// 获取特定节点的元数据文件路径
        /// </summary>
        public async Task<string> GetNodeMetadataPathAsync(NodeId nodeId)
        {
            var nodeDir = await GetNodeDirectoryAsync(nodeId);
            return Path.Combine(nodeDir, _storageOptions.MetadataFileName);
        }

        /// <summary>
        /// 检查当前目录是否为MemoTree工作空间
        /// </summary>
        public async Task<bool> IsWorkspaceAsync(string? directory = null)
        {
            directory ??= Directory.GetCurrentDirectory();
            var workspaceDir = await FindWorkspaceDirectoryAsync(directory);
            return workspaceDir != null;
        }

        /// <summary>
        /// 检查当前工作空间是否为链接工作空间
        /// </summary>
        public async Task<bool> IsLinkedWorkspaceAsync()
        {
            var linkTarget = await GetLinkTargetAsync();
            return linkTarget != null;
        }

        /// <summary>
        /// 获取链接工作空间的目标路径
        /// </summary>
        public async Task<string?> GetLinkTargetAsync()
        {
            var workspaceRoot = await GetWorkspaceRootAsync();
            var linkConfigPath = Path.Combine(workspaceRoot, LinkConfigFileName);
            
            if (!File.Exists(linkConfigPath))
                return null;

            try
            {
                var linkConfigJson = await File.ReadAllTextAsync(linkConfigPath);
                var linkConfig = JsonSerializer.Deserialize<Dictionary<string, object>>(linkConfigJson);
                
                if (linkConfig?.TryGetValue("target", out var targetObj) == true && 
                    targetObj is JsonElement targetElement)
                {
                    return targetElement.GetString();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read link configuration from {LinkConfigPath}", linkConfigPath);
            }

            return null;
        }

        /// <summary>
        /// 确保所有必要的目录存在
        /// </summary>
        public async Task EnsureDirectoriesExistAsync()
        {
            var directories = new[]
            {
                await GetWorkspaceRootAsync(),
                await GetCogNodesDirectoryAsync(),
                await GetHierarchyDirectoryAsync(),
                await GetRelationsDirectoryAsync(),
                await GetViewsDirectoryAsync()
            };

            foreach (var directory in directories)
            {
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                    _logger.LogDebug("Created directory: {Directory}", directory);
                }
            }
        }

        /// <summary>
        /// 向上查找工作空间目录
        /// </summary>
        private async Task<string?> FindWorkspaceDirectoryAsync(string startDirectory)
        {
            var currentDir = new DirectoryInfo(startDirectory);
            
            while (currentDir != null)
            {
                var workspaceDir = Path.Combine(currentDir.FullName, WorkspaceDirectoryName);
                if (Directory.Exists(workspaceDir))
                {
                    _logger.LogDebug("Found workspace directory: {WorkspaceDir}", workspaceDir);
                    return workspaceDir;
                }
                
                currentDir = currentDir.Parent;
            }

            _logger.LogDebug("Workspace directory not found starting from: {StartDirectory}", startDirectory);
            return null;
        }
    }
}
