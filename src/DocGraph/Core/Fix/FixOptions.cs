// DocGraph v0.1 - 修复选项
// 参考：api.md §2.3 修复选项

namespace Atelia.DocGraph.Core.Fix;

/// <summary>
/// 修复选项。
/// </summary>
public class FixOptions
{
    /// <summary>
    /// 是否启用修复模式。
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 是否只预览不执行（dry-run）。
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// 是否自动确认（跳过用户确认）。
    /// </summary>
    public bool AutoConfirm { get; set; }

    /// <summary>
    /// 修复范围（v0.1仅支持CreateMissing）。
    /// </summary>
    public FixScope Scope { get; set; } = FixScope.CreateMissing;

    /// <summary>
    /// 创建默认修复选项（禁用）。
    /// </summary>
    public static FixOptions Disabled => new();

    /// <summary>
    /// 创建启用的修复选项。
    /// </summary>
    public static FixOptions Enable(bool dryRun = false, bool autoConfirm = false)
        => new()
        {
            Enabled = true,
            DryRun = dryRun,
            AutoConfirm = autoConfirm
        };
}

/// <summary>
/// 修复范围。
/// </summary>
public enum FixScope
{
    /// <summary>
    /// 创建缺失的文件（v0.1支持）。
    /// </summary>
    CreateMissing,

    /// <summary>
    /// 注入frontmatter到现有文件（v1.0规划）。
    /// </summary>
    InjectFrontmatter,

    /// <summary>
    /// 修复链接关系（v1.0规划）。
    /// </summary>
    RepairLinks,

    /// <summary>
    /// 所有修复类型。
    /// </summary>
    All
}
