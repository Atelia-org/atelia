// DocGraph v0.1 - Visitor 接口
// 参考：api.md §3.2

using Atelia.DocGraph.Core;

namespace Atelia.DocGraph.Visitors;

/// <summary>
/// 文档图访问者接口，用于生成汇总文档。
/// </summary>
public interface IDocumentGraphVisitor
{
    /// <summary>
    /// Visitor 名称（用于输出文件命名）。
    /// </summary>
    string Name { get; }

    /// <summary>
    /// 输出文件路径（相对 workspace）。
    /// 默认：{Name}.gen.md
    /// </summary>
    string OutputPath { get; }

    /// <summary>
    /// 依赖的 frontmatter 字段列表（用于自文档化和编译期检查）。
    /// 示例：["defines", "issues"]
    /// </summary>
    IReadOnlyList<string> RequiredFields { get; }

    /// <summary>
    /// 生成汇总文档。
    /// </summary>
    /// <param name="graph">完整的文档图。</param>
    /// <returns>生成的文档内容。</returns>
    string Generate(DocumentGraph graph);
}
