// DocGraph v0.1 - 修复结果
// 参考：api.md §2.3 修复结果

namespace Atelia.DocGraph.Core.Fix;

/// <summary>
/// 修复操作结果。
/// </summary>
public class FixResult
{
    /// <summary>
    /// 是否成功。
    /// </summary>
    public bool Success { get; private init; }

    /// <summary>
    /// 错误消息（失败时有值）。
    /// </summary>
    public string? ErrorMessage { get; private init; }

    /// <summary>
    /// 目标路径。
    /// </summary>
    public string? TargetPath { get; private init; }

    /// <summary>
    /// 修复操作类型。
    /// </summary>
    public FixActionType ActionType { get; private init; }

    private FixResult() { }

    /// <summary>
    /// 创建成功结果。
    /// </summary>
    public static FixResult CreateSuccess(string targetPath, FixActionType actionType)
        => new()
        {
            Success = true,
            TargetPath = targetPath,
            ActionType = actionType
        };

    /// <summary>
    /// 创建失败结果。
    /// </summary>
    public static FixResult CreateFailure(string errorMessage, string? targetPath = null, FixActionType actionType = FixActionType.CreateFile)
        => new()
        {
            Success = false,
            ErrorMessage = errorMessage,
            TargetPath = targetPath,
            ActionType = actionType
        };
}

/// <summary>
/// 修复操作类型。
/// </summary>
public enum FixActionType
{
    /// <summary>
    /// 创建文件。
    /// </summary>
    CreateFile,

    /// <summary>
    /// 更新 frontmatter（v1.0规划）。
    /// </summary>
    UpdateFrontmatter,

    /// <summary>
    /// 修复链接（v1.0规划）。
    /// </summary>
    RepairLink
}
