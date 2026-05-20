// DocGraph v0.1 - Visitor 接口
// 参考：api.md §3.2

using Atelia.DocGraph.Core;

namespace Atelia.DocGraph.Visitors;

/// <summary>
/// 文档图访问者接口，用于生成汇总文档。
/// </summary>
public interface IDocumentGraphVisitor {
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

    /// <summary>
    /// 生成多个输出文件（可选）。
    /// 如果返回非 null 且非空，则忽略 <see cref="Generate"/> 和 <see cref="OutputPath"/>，使用此方法的返回值。
    /// 如果返回空 Dictionary，等价于返回 null，回退到单输出模式。
    /// </summary>
    /// <param name="graph">完整的文档图。</param>
    /// <returns>路径到内容的映射，或 null/空 Dictionary 表示使用单输出模式。</returns>
    /// <remarks>
    /// <para><b>路径规则：</b></para>
    /// <list type="bullet">
    ///   <item>Key 必须是相对路径（不能以 / 或盘符开头）</item>
    ///   <item>Key 不能包含 .. 路径穿越</item>
    ///   <item>Key 不能是空字符串或空白</item>
    ///   <item>不同 visitor 或同一 visitor 的多个输出不能有重复路径</item>
    /// </list>
    /// <para>违反上述规则会导致生成阶段报错。</para>
    /// </remarks>
    IReadOnlyDictionary<string, string>? GenerateMultiple(DocumentGraph graph) => null;
}
