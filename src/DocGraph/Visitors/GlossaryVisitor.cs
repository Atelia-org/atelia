// DocGraph v0.1 - 术语表生成器
// 参考：api.md §4.1, spec.md §6.4.1

using System.Text;
using Atelia.DocGraph.Core;
using Atelia.DocGraph.Utils;

namespace Atelia.DocGraph.Visitors;

/// <summary>
/// 术语表生成器：从 defines 字段生成紧凑 Markdown 列表。
/// 遵循 [F-VISITOR-001] 规范。
/// </summary>
public class GlossaryVisitor : IDocumentGraphVisitor {
    /// <inheritdoc/>
    public string Name => "glossary";

    /// <inheritdoc/>
    public string OutputPath => "wish-panels/glossary.gen.md";

    /// <inheritdoc/>
    public IReadOnlyList<string> RequiredFields => [KnownFrontmatterFields.Glossary];

    /// <inheritdoc/>
    public string Generate(DocumentGraph graph) {
        var builder = new StringBuilder();

        // 文件头
        AppendFileHeader(builder);

        builder.AppendLine("# 术语表");
        builder.AppendLine();

        // 按文档分组收集术语
        var termsByDoc = new Dictionary<string, List<(string Term, string Definition)>>();

        graph.ForEachDocument(
            node => {
                if (node.Frontmatter.TryGetValue(KnownFrontmatterFields.Glossary, out var glossaryObj) &&
                    glossaryObj != null) {
                    var terms = ExtractTerms(glossaryObj);
                    if (terms.Count > 0) {
                        termsByDoc[node.FilePath] = terms;
                    }
                }
            }
        );

        if (termsByDoc.Count == 0) {
            builder.AppendLine("*暂无术语定义*");
            return builder.ToString();
        }

        // 按文档路径排序输出
        foreach (var (docPath, terms) in termsByDoc.OrderBy(kv => kv.Key, StringComparer.Ordinal)) {
            // 使用完整 workspace 相对路径（inline code 格式）
            // [格式约定] 便于阅读者直接定位文档，避免重名问题
            builder.AppendLine($"## `{docPath}`");
            builder.AppendLine();

            foreach (var (term, definition) in terms.OrderBy(t => t.Term, StringComparer.Ordinal)) {
                builder.AppendLine($"- **{term}**：{definition}");
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    /// <summary>
    /// 提取术语定义。
    /// 格式：单键映射序列 [{term: definition}]
    /// </summary>
    private static List<(string Term, string Definition)> ExtractTerms(object glossaryObj) {
        var terms = new List<(string, string)>();

        if (glossaryObj is IEnumerable<object> glossaryList) {
            foreach (var item in glossaryList) {
                if (item is IDictionary<object, object> dict && dict.Count == 1) {
                    var kvp = dict.First();
                    var term = kvp.Key?.ToString();
                    var definition = kvp.Value?.ToString();

                    if (!string.IsNullOrWhiteSpace(term) && !string.IsNullOrWhiteSpace(definition)) {
                        terms.Add((term, definition));
                    }
                }
            }
        }

        return terms;
    }

    /// <summary>
    /// 添加文件头。
    /// 遵循 [S-VISITOR-003] 规范。
    /// </summary>
    private static void AppendFileHeader(StringBuilder builder) {
        builder.AppendLine("<!-- 本文档由 DocGraph 工具自动生成，手动编辑无效 -->");
        builder.AppendLine($"<!-- 生成时间：{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC -->");
        builder.AppendLine("<!-- 再生成命令：docgraph generate glossary -->");
        builder.AppendLine();
    }
}

/// <summary>
/// 已知 frontmatter 扩展字段常量。
/// </summary>
public static class KnownFrontmatterFields {
    /// <summary>
    /// 术语表字段（对应 glossary.gen.md）。
    /// </summary>
    public const string Glossary = "glossary";

    /// <summary>
    /// 问题跟踪字段。
    /// </summary>
    public const string Issues = "issues";

    /// <summary>
    /// 目标跟踪字段。
    /// </summary>
    public const string Goals = "goals";
}
