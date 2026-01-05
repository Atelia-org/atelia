// DocGraph v0.2 - 可达文档目录生成器
// 目的：输出从 Root Nodes 可达的完整闭包文件列表，供迁移/重构时“顺藤摸瓜”更新 frontmatter 路径。

using System.Text;
using Atelia.DocGraph.Core;

namespace Atelia.DocGraph.Visitors;

/// <summary>
/// 可达文档目录生成器：列出 DocumentGraph 中的全部节点（即从 Root Nodes 可达的闭包）。
/// 输出为 Markdown 列表，按路径字典序排序，便于人工与工具消费。
/// </summary>
public sealed class ReachableDocumentsVisitor : IDocumentGraphVisitor
{
    public string Name => "reachable-documents";

    // 方案 2：面板产物独立目录，不与 wish 实例混放
    public string OutputPath => "wish-panels/reachable-documents.gen.md";

    public IReadOnlyList<string> RequiredFields => [];

    public string Generate(DocumentGraph graph)
    {
        var builder = new StringBuilder();

        builder.AppendLine("<!-- 本文档由 DocGraph 工具自动生成，手动编辑无效 -->");
        // Intentionally omit timestamps to keep the output stable and diff-friendly.
        builder.AppendLine("<!-- 再生成命令：docgraph -->");
        builder.AppendLine();

        builder.AppendLine("# Reachable Documents");
        builder.AppendLine();
        builder.AppendLine($"- Wish roots: {graph.RootNodes.Count}");
        builder.AppendLine($"- Total reachable documents: {graph.AllNodes.Count}");
        builder.AppendLine();

        builder.AppendLine("## Wish Roots");
        builder.AppendLine();
        foreach (var wish in graph.RootNodes.OrderBy(n => n.FilePath, StringComparer.Ordinal))
        {
            builder.AppendLine($"- `{wish.FilePath}` ({wish.DocId})");
        }

        builder.AppendLine();
        builder.AppendLine("## All Nodes (Closure)");
        builder.AppendLine();

        foreach (var node in graph.AllNodes.OrderBy(n => n.FilePath, StringComparer.Ordinal))
        {
            var type = node.Type == DocumentType.Wish ? "Wish" : "Product";
            builder.AppendLine($"- `{node.FilePath}`  ");
            builder.AppendLine($"  - type: {type}");
            builder.AppendLine($"  - docId: {node.DocId}");
        }

        return builder.ToString();
    }
}
