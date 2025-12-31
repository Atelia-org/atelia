// DocGraph v0.1 - 文档图模型
// 参考：api.md §2.2 文档图 (DocumentGraph)

namespace Atelia.DocGraph.Core;

/// <summary>
/// 完整的文档关系图。
/// </summary>
public class DocumentGraph
{
    /// <summary>
    /// Root nodes：所有Wish文档。
    /// </summary>
    public IReadOnlyList<DocumentNode> RootNodes { get; }

    /// <summary>
    /// 所有文档节点（包括Wish和产物文档）。
    /// 按FilePath字典序排序，确保遍历确定性。
    /// </summary>
    public IReadOnlyList<DocumentNode> AllNodes { get; }

    /// <summary>
    /// 路径索引：快速查找文档节点。
    /// </summary>
    public IReadOnlyDictionary<string, DocumentNode> ByPath { get; }

    /// <summary>
    /// 创建文档图。
    /// </summary>
    /// <param name="nodes">所有文档节点。</param>
    public DocumentGraph(IEnumerable<DocumentNode> nodes)
    {
        var nodeList = nodes.OrderBy(n => n.FilePath, StringComparer.Ordinal).ToList();

        // [A-DOCGRAPH-004] 对每个节点的边按 FilePath 字典序排序，保证确定性输出
        foreach (var node in nodeList)
        {
            node.SortRelations();
        }

        AllNodes = nodeList;
        RootNodes = nodeList.Where(n => n.Type == DocumentType.Wish).ToList();
        ByPath = nodeList.ToDictionary(n => n.FilePath, StringComparer.Ordinal);
    }

    /// <summary>
    /// 便利方法：遍历所有文档节点。
    /// </summary>
    public void ForEachDocument(Action<DocumentNode> visitor)
    {
        foreach (var node in AllNodes)
        {
            visitor(node);
        }
    }

    /// <summary>
    /// 尝试获取指定路径的文档节点。
    /// </summary>
    public bool TryGetNode(string path, out DocumentNode? node)
    {
        return ByPath.TryGetValue(path, out node);
    }
}
