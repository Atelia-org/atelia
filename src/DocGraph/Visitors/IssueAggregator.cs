// DocGraph v0.1 - 问题汇总器
// 参考：api.md §4.2, spec.md §6.4.2

using System.Text;
using Atelia.DocGraph.Core;

namespace Atelia.DocGraph.Visitors;

/// <summary>
/// 问题汇总器：从 issues 字段生成分类表格。
/// 遵循 [F-VISITOR-001] 规范。
/// </summary>
public class IssueAggregator : IDocumentGraphVisitor
{
    /// <inheritdoc/>
    public string Name => "issues";

    /// <inheritdoc/>
    public string OutputPath => "docs/issues.gen.md";

    /// <inheritdoc/>
    public IReadOnlyList<string> RequiredFields => [KnownFrontmatterFields.Issues];

    /// <inheritdoc/>
    public string Generate(DocumentGraph graph)
    {
        var builder = new StringBuilder();

        // 文件头
        AppendFileHeader(builder);

        builder.AppendLine("# 问题汇总");
        builder.AppendLine();

        // 收集所有问题
        var allIssues = new List<Issue>();

        graph.ForEachDocument(node =>
        {
            if (node.Frontmatter.TryGetValue(KnownFrontmatterFields.Issues, out var issuesObj) &&
                issuesObj != null)
            {
                var docIssues = ExtractIssues(node, issuesObj);
                allIssues.AddRange(docIssues);
            }
        });

        if (allIssues.Count == 0)
        {
            builder.AppendLine("*暂无问题记录*");
            return builder.ToString();
        }

        // 生成统计概览
        builder.AppendLine("## 统计概览");
        builder.AppendLine();
        builder.AppendLine($"- 总问题数：{allIssues.Count}");
        builder.AppendLine("- 按状态分布：");

        foreach (var group in allIssues.GroupBy(i => i.Status).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            builder.AppendLine($"  - {group.Key}：{group.Count()}个");
        }

        builder.AppendLine();

        // 按来源文档分组输出（子弹列表格式）
        // [格式约定] 节约 Token，同一文档多条目不重复路径；属性灵活，额外信息作为子列表
        var issuesByDoc = allIssues
            .GroupBy(i => i.SourceDocument)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var docGroup in issuesByDoc)
        {
            // 使用完整 workspace 相对路径（inline code 格式）
            builder.AppendLine($"## `{docGroup.Key}`");
            builder.AppendLine();

            foreach (var issue in docGroup.OrderBy(i => i.Status, StringComparer.Ordinal))
            {
                // 主行：问题描述
                builder.AppendLine($"- {issue.Description}");

                // 子列表：状态和负责人
                builder.AppendLine($"  - 状态：{issue.Status}");
                if (!string.IsNullOrEmpty(issue.Assignee))
                {
                    builder.AppendLine($"  - 负责人：{issue.Assignee}");
                }
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    /// <summary>
    /// 提取问题信息。
    /// </summary>
    private static List<Issue> ExtractIssues(DocumentNode node, object issuesObj)
    {
        var result = new List<Issue>();

        if (issuesObj is IEnumerable<object> issuesList)
        {
            foreach (var item in issuesList)
            {
                if (item is IDictionary<object, object> dict)
                {
                    var description = dict.TryGetValue("description", out var d) ? d?.ToString() : null;
                    var status = dict.TryGetValue("status", out var s) ? s?.ToString() : "open";
                    var assignee = dict.TryGetValue("assignee", out var a) ? a?.ToString() : null;

                    if (!string.IsNullOrWhiteSpace(description))
                    {
                        result.Add(new Issue
                        {
                            Description = description,
                            Status = status ?? "open",
                            Assignee = assignee,
                            SourceDocument = node.FilePath
                        });
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 添加文件头。
    /// </summary>
    private static void AppendFileHeader(StringBuilder builder)
    {
        builder.AppendLine("<!-- 本文档由 DocGraph 工具自动生成，手动编辑无效 -->");
        builder.AppendLine($"<!-- 生成时间：{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC -->");
        builder.AppendLine("<!-- 再生成命令：docgraph generate issues -->");
        builder.AppendLine();
    }

    /// <summary>
    /// 问题数据结构。
    /// </summary>
    private class Issue
    {
        public string Description { get; set; } = "";
        public string Status { get; set; } = "open";
        public string? Assignee { get; set; }
        public string SourceDocument { get; set; } = "";
    }
}
