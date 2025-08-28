namespace MemoTree.Cli.Services;

/// <summary>
/// 工作空间管理器
/// 负责.memotree目录的创建、检测和管理
/// </summary>
public class WorkspaceManager {
    private const string WorkspaceDirectoryName = ".memotree";
    private const string ConfigFileName = "config.json";
    private const string ViewStateFileName = "viewstate.json";

    /// <summary>
    /// 检查当前目录是否是MemoTree工作空间
    /// </summary>
    public bool IsWorkspace(string? directory = null) {
        directory ??= Directory.GetCurrentDirectory();
        return Directory.Exists(Path.Combine(directory, WorkspaceDirectoryName));
    }

    /// <summary>
    /// 查找最近的MemoTree工作空间目录
    /// </summary>
    public string? FindWorkspaceRoot(string? startDirectory = null) {
        startDirectory ??= Directory.GetCurrentDirectory();
        var current = new DirectoryInfo(startDirectory);

        while (current != null) {
            if (Directory.Exists(Path.Combine(current.FullName, WorkspaceDirectoryName))) {
                return current.FullName;
            }
            current = current.Parent;
        }

        return null;
    }

    /// <summary>
    /// 初始化新的MemoTree工作空间
    /// </summary>
    public async Task<string> InitializeWorkspaceAsync(string? directory = null) {
        directory ??= Directory.GetCurrentDirectory();
        var workspaceDir = Path.Combine(directory, WorkspaceDirectoryName);

        if (Directory.Exists(workspaceDir)) {
            throw new InvalidOperationException($"MemoTree workspace already exists at {directory}");
        }

        // 创建.memotree目录结构
        Directory.CreateDirectory(workspaceDir);
        Directory.CreateDirectory(Path.Combine(workspaceDir, "views"));

        // 创建默认配置文件
        var configPath = Path.Combine(workspaceDir, ConfigFileName);
        var defaultConfig = new {
            version = "1.0",
            created = DateTime.UtcNow,
            defaultView = "default",
            encoding = "Base4096-CJK"
        };

        await File.WriteAllTextAsync(configPath, System.Text.Json.JsonSerializer.Serialize(defaultConfig, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        return directory;
    }

    /// <summary>
    /// 创建连接到远程工作空间的软链接
    /// </summary>
    public async Task<string> ConnectWorkspaceAsync(string targetWorkspacePath, string? directory = null) {
        directory ??= Directory.GetCurrentDirectory();
        var workspaceDir = Path.Combine(directory, WorkspaceDirectoryName);

        if (Directory.Exists(workspaceDir)) {
            throw new InvalidOperationException($"MemoTree workspace already exists at {directory}");
        }

        if (!Directory.Exists(targetWorkspacePath)) {
            throw new DirectoryNotFoundException($"Target workspace not found: {targetWorkspacePath}");
        }

        var targetMemoTreeDir = Path.Combine(targetWorkspacePath, WorkspaceDirectoryName);
        if (!Directory.Exists(targetMemoTreeDir)) {
            throw new InvalidOperationException($"Target directory is not a MemoTree workspace: {targetWorkspacePath}");
        }

        // 创建软链接 (在Windows上需要管理员权限，这里先用简单的配置文件方式)
        Directory.CreateDirectory(workspaceDir);

        var linkConfigPath = Path.Combine(workspaceDir, "link.json");
        var linkConfig = new {
            type = "link",
            target = Path.GetFullPath(targetWorkspacePath),
            created = DateTime.UtcNow
        };

        await File.WriteAllTextAsync(linkConfigPath, System.Text.Json.JsonSerializer.Serialize(linkConfig, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        return directory;
    }

    /// <summary>
    /// 获取工作空间的实际数据目录
    /// </summary>
    public async Task<string> GetDataDirectoryAsync(string? workspaceRoot = null) {
        workspaceRoot ??= FindWorkspaceRoot() ?? throw new InvalidOperationException("Not in a MemoTree workspace");

        var workspaceDir = Path.Combine(workspaceRoot, WorkspaceDirectoryName);
        var linkConfigPath = Path.Combine(workspaceDir, "link.json");

        // 检查是否是链接工作空间
        if (File.Exists(linkConfigPath)) {
            var linkConfigJson = await File.ReadAllTextAsync(linkConfigPath);
            var linkConfig = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(linkConfigJson);

            if (linkConfig?.TryGetValue("target", out var targetObj) == true && targetObj is System.Text.Json.JsonElement targetElement) {
                var targetPath = targetElement.GetString();
                if (!string.IsNullOrEmpty(targetPath)) {
                    return Path.Combine(targetPath, WorkspaceDirectoryName, "data");
                }
            }
        }

        // 本地工作空间
        return Path.Combine(workspaceDir, "data");
    }

    /// <summary>
    /// 获取视图状态目录
    /// </summary>
    public async Task<string> GetViewStateDirectoryAsync(string? workspaceRoot = null) {
        workspaceRoot ??= FindWorkspaceRoot() ?? throw new InvalidOperationException("Not in a MemoTree workspace");

        var workspaceDir = Path.Combine(workspaceRoot, WorkspaceDirectoryName);
        var linkConfigPath = Path.Combine(workspaceDir, "link.json");

        // 检查是否是链接工作空间
        if (File.Exists(linkConfigPath)) {
            var linkConfigJson = await File.ReadAllTextAsync(linkConfigPath);
            var linkConfig = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(linkConfigJson);

            if (linkConfig?.TryGetValue("target", out var targetObj) == true && targetObj is System.Text.Json.JsonElement targetElement) {
                var targetPath = targetElement.GetString();
                if (!string.IsNullOrEmpty(targetPath)) {
                    return Path.Combine(targetPath, WorkspaceDirectoryName, "views");
                }
            }
        }

        // 本地工作空间
        return Path.Combine(workspaceDir, "views");
    }
}
