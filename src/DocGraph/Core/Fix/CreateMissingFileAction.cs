// DocGraph v0.1 - 创建缺失文件修复动作
// 参考：spec.md [S-DOCGRAPH-FIX-SCOPE-V01]

namespace Atelia.DocGraph.Core.Fix;

/// <summary>
/// 创建缺失的产物文件修复动作。
/// v0.1 唯一支持的修复类型。
/// </summary>
public class CreateMissingFileAction : IFixAction
{
    private readonly string _targetPath;
    private readonly string _sourceDocPath;
    private readonly string _sourceDocId;

    /// <summary>
    /// 创建修复动作实例。
    /// </summary>
    /// <param name="targetPath">要创建的目标文件路径（workspace相对路径）。</param>
    /// <param name="sourceDocPath">源文档路径（引用此文件的 Wish 文档）。</param>
    /// <param name="sourceDocId">源文档 ID。</param>
    public CreateMissingFileAction(string targetPath, string sourceDocPath, string sourceDocId)
    {
        _targetPath = targetPath;
        _sourceDocPath = sourceDocPath;
        _sourceDocId = sourceDocId;
    }

    /// <summary>
    /// 获取目标路径。
    /// </summary>
    public string TargetPath => _targetPath;

    /// <summary>
    /// 获取源文档路径。
    /// </summary>
    public string SourceDocPath => _sourceDocPath;

    /// <inheritdoc/>
    public bool CanExecute(FixContext context)
    {
        // 目标文件不存在且源文档存在于图中
        var absolutePath = Path.Combine(context.WorkspaceRoot, _targetPath);
        return !File.Exists(absolutePath) && context.Graph.ByPath.ContainsKey(_sourceDocPath);
    }

    /// <inheritdoc/>
    public string Describe()
    {
        return $"创建文件: {_targetPath} (由 {_sourceDocId} 引用)";
    }

    /// <inheritdoc/>
    public string Preview()
    {
        var template = GenerateTemplate();
        var lines = template.Split('\n');
        var preview = lines.Length > 15
            ? string.Join('\n', lines.Take(15)) + "\n... (内容已截断)"
            : template;

        return $"""
            ┌─────────────────────────────────────────────────────────────
            │ 📄 将创建文件: {_targetPath}
            │    来源: {_sourceDocPath} ({_sourceDocId})
            │    操作: CreateFile
            ├─────────────────────────────────────────────────────────────
            {preview}
            └─────────────────────────────────────────────────────────────
            """;
    }

    /// <summary>
    /// 获取修复建议（三层建议结构）。
    /// </summary>
    public FixSuggestion GetSuggestion()
    {
        return new FixSuggestion
        {
            Quick = $"运行 `docgraph fix` 自动创建 {_targetPath}",
            Detailed = $"""
                问题：Wish 文档 {_sourceDocId} 引用了不存在的文件 {_targetPath}
                
                自动修复方案：
                  docgraph fix --yes          # 自动创建所有缺失文件
                  docgraph fix --dry-run      # 预览将创建的文件
                
                手动修复方案：
                  1. 创建文件 {_targetPath}
                  2. 添加 frontmatter:
                     ---
                     docId: "{DeriveDocId(_targetPath)}"
                     title: "文档标题"
                     produce_by: ["{_sourceDocPath}"]
                     ---
                """,
            Reference = "https://github.com/example/docgraph/docs/fix-actions.md"
        };
    }

    /// <inheritdoc/>
    public FixResult Execute(string workspaceRoot)
    {
        try
        {
            var absolutePath = Path.Combine(workspaceRoot, _targetPath);

            // 检查目标文件是否已存在
            if (File.Exists(absolutePath))
            {
                return FixResult.CreateFailure(
                    $"目标文件已存在: {_targetPath}",
                    _targetPath,
                    FixActionType.CreateFile);
            }

            // 确保目录存在
            var directory = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 生成模板内容
            var template = GenerateTemplate();

            // 原子写入：先写临时文件，再重命名
            var tempPath = absolutePath + ".tmp";
            File.WriteAllText(tempPath, template);
            File.Move(tempPath, absolutePath);

            return FixResult.CreateSuccess(_targetPath, FixActionType.CreateFile);
        }
        catch (Exception ex)
        {
            return FixResult.CreateFailure(
                $"创建文件失败: {ex.Message}",
                _targetPath,
                FixActionType.CreateFile);
        }
    }

    /// <summary>
    /// 生成文件模板。
    /// 遵循 spec.md [A-WRITE-001] 极简原则。
    /// </summary>
    private string GenerateTemplate()
    {
        var docId = DeriveDocId(_targetPath);
        return $"""
            ---
            docId: "{docId}"
            title: "待填写"
            produce_by: ["{_sourceDocPath}"]
            ---

            # {docId}

            > 本文档由 DocGraph 工具自动创建，请填写具体内容。
            > 创建来源：{_sourceDocPath}

            ## 概述

            待补充...
            """;
    }

    /// <summary>
    /// 从文件路径推导 docId。
    /// </summary>
    private static string DeriveDocId(string filePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        return fileName ?? "unknown";
    }
}

/// <summary>
/// 修复建议（三层建议结构）。
/// </summary>
public class FixSuggestion
{
    /// <summary>
    /// 快速建议（5秒能理解）。
    /// </summary>
    public required string Quick { get; init; }

    /// <summary>
    /// 详细建议（30秒能修复）。
    /// </summary>
    public required string Detailed { get; init; }

    /// <summary>
    /// 参考链接（按需深入）。
    /// </summary>
    public string? Reference { get; init; }
}
