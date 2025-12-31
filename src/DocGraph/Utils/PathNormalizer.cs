// DocGraph v0.1 - 路径规范化工具
// 参考：spec.md §3.1-3.3 路径处理约束

namespace Atelia.DocGraph.Utils;

/// <summary>
/// 路径规范化工具。
/// 实现 spec.md [S-PATH-001], [S-PATH-002], [S-PATH-003] 条款。
/// </summary>
public static class PathNormalizer
{
    /// <summary>
    /// 规范化路径。
    /// 遵循 [S-PATH-002]：消解./..，统一分隔符为/，移除末尾/。
    /// </summary>
    /// <param name="path">原始路径。</param>
    /// <returns>规范化后的路径，如果路径无效则返回 null。</returns>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        // 1. 统一分隔符为 /
        path = path.Replace('\\', '/');

        // 2. 分割路径组件
        var components = path.Split('/', StringSplitOptions.None);
        var result = new List<string>();

        // 3. 处理每个组件
        foreach (var component in components)
        {
            if (component == "." || string.IsNullOrEmpty(component))
            {
                // 跳过当前目录引用和空组件
                continue;
            }

            if (component == "..")
            {
                // 上级目录：弹出最后一个组件（如果有）
                if (result.Count > 0)
                {
                    result.RemoveAt(result.Count - 1);
                }
                // 如果没有更多组件可弹出，表示越界
                // 这里不立即报错，让 IsWithinWorkspace 来检查
                continue;
            }

            result.Add(component);
        }

        // 4. 重新组合
        if (result.Count == 0)
        {
            return null; // 空路径
        }

        return string.Join("/", result);
    }

    /// <summary>
    /// 检查路径是否在 workspace 内（不越界）。
    /// 遵循 [S-PATH-003]：规范化后检查路径越界。
    /// </summary>
    /// <param name="path">要检查的路径（workspace 相对路径）。</param>
    /// <param name="workspaceRoot">工作区根目录绝对路径。</param>
    /// <returns>是否在 workspace 内。</returns>
    public static bool IsWithinWorkspace(string? path, string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        // 规范化输入路径
        var normalizedPath = Normalize(path);
        if (normalizedPath == null)
        {
            return false;
        }

        // 检查是否为绝对路径（不允许）
        if (IsAbsolutePath(path))
        {
            return false;
        }

        // 检查原始路径中的 .. 是否会导致越界
        // 计算 .. 的数量和非 .. 组件的数量
        var originalComponents = path.Replace('\\', '/').Split('/');
        int depth = 0;
        int minDepth = 0;

        foreach (var component in originalComponents)
        {
            if (component == ".." )
            {
                depth--;
                minDepth = Math.Min(minDepth, depth);
            }
            else if (component != "." && !string.IsNullOrEmpty(component))
            {
                depth++;
            }
        }

        // 如果最小深度为负，说明路径试图越界
        if (minDepth < 0)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// 将绝对路径转换为 workspace 相对路径。
    /// </summary>
    /// <param name="absolutePath">绝对路径。</param>
    /// <param name="workspaceRoot">工作区根目录绝对路径。</param>
    /// <returns>workspace 相对路径，如果转换失败返回 null。</returns>
    public static string? ToWorkspaceRelative(string absolutePath, string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(absolutePath) || string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return null;
        }

        // 规范化两个路径
        var normalizedAbsolute = Path.GetFullPath(absolutePath).Replace('\\', '/').TrimEnd('/');
        var normalizedRoot = Path.GetFullPath(workspaceRoot).Replace('\\', '/').TrimEnd('/');

        // 检查是否在 workspace 内
        if (!normalizedAbsolute.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // 提取相对路径部分
        if (normalizedAbsolute.Length == normalizedRoot.Length)
        {
            return "."; // 就是根目录本身
        }

        var relativePath = normalizedAbsolute[(normalizedRoot.Length + 1)..];
        return Normalize(relativePath);
    }

    /// <summary>
    /// 将 workspace 相对路径转换为绝对路径。
    /// </summary>
    /// <param name="relativePath">workspace 相对路径。</param>
    /// <param name="workspaceRoot">工作区根目录绝对路径。</param>
    /// <returns>绝对路径，如果转换失败返回 null。</returns>
    public static string? ToAbsolute(string? relativePath, string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return null;
        }

        // 规范化相对路径
        var normalizedPath = Normalize(relativePath);
        if (normalizedPath == null)
        {
            return null;
        }

        // 检查是否越界
        if (!IsWithinWorkspace(relativePath, workspaceRoot))
        {
            return null;
        }

        // 组合路径
        return Path.GetFullPath(Path.Combine(workspaceRoot, normalizedPath));
    }

    /// <summary>
    /// 检查是否为绝对路径。
    /// </summary>
    private static bool IsAbsolutePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        // Windows 风格：C:\ 或 \\
        if (path.Length >= 2 && path[1] == ':')
        {
            return true;
        }

        // UNC 路径
        if (path.StartsWith("\\\\") || path.StartsWith("//"))
        {
            return true;
        }

        // Unix 风格
        if (path.StartsWith('/'))
        {
            return true;
        }

        // URI 格式
        if (path.Contains("://"))
        {
            return true;
        }

        return false;
    }
}
