// DocGraph v0.1 - 文档节点模型
// 参考：api.md §2.1 文档节点 (DocumentNode)

namespace Atelia.DocGraph.Core;

/// <summary>
/// 文档图中的节点，表示一个文档。
/// </summary>
public class DocumentNode {
    private readonly List<DocumentNode> _produces = [];
    private readonly List<DocumentNode> _producedBy = [];

    /// <summary>
    /// 文件路径（workspace相对路径，使用'/'分隔符）。
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// 文档标识。
    /// - Wish文档：从文件名推导（wish-0001.md → W-0001）
    /// - 产物文档：frontmatter中显式声明
    /// </summary>
    public string DocId { get; }

    /// <summary>
    /// 文档标题（必填字段）。
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// 文档状态。
    /// - Wish文档：从文件夹推导（active/ → "active", biding/ → "biding", completed/ → "completed"）
    /// - 产物文档：null（不适用）
    /// </summary>
    public string? Status { get; }

    /// <summary>
    /// 文档类型。
    /// </summary>
    public DocumentType Type { get; }

    /// <summary>
    /// 文档frontmatter（原始YAML解析结果）。
    /// 采用开放schema模式：核心字段严格验证，扩展字段自由使用。
    /// </summary>
    public IReadOnlyDictionary<string, object?> Frontmatter { get; }

    /// <summary>
    /// 出边关系：本文档产生的文档列表。
    /// 仅Wish文档有此关系。
    /// </summary>
    public IReadOnlyList<DocumentNode> Produces => _produces;

    /// <summary>
    /// 入边关系：产生本文档的Wish文档列表。
    /// 仅产物文档有此关系。
    /// </summary>
    public IReadOnlyList<DocumentNode> ProducedBy => _producedBy;

    /// <summary>
    /// 原始 produce 路径列表（用于关系构建）。
    /// </summary>
    internal IReadOnlyList<string> ProducePaths { get; }

    /// <summary>
    /// 原始 produce_by 路径列表（用于关系构建）。
    /// </summary>
    internal IReadOnlyList<string> ProducedByPaths { get; }

    /// <summary>
    /// 创建文档节点。
    /// </summary>
    public DocumentNode(
        string filePath,
        string docId,
        string title,
        string? status,
        DocumentType type,
        IReadOnlyDictionary<string, object?> frontmatter,
        IReadOnlyList<string> producePaths,
        IReadOnlyList<string> producedByPaths
    ) {
        FilePath = filePath;
        DocId = docId;
        Title = title;
        Status = status;
        Type = type;
        Frontmatter = frontmatter;
        ProducePaths = producePaths;
        ProducedByPaths = producedByPaths;
    }

    /// <summary>
    /// 添加 produce 关系（仅供 DocumentGraphBuilder 内部使用）。
    /// </summary>
    internal void AddProducesRelation(DocumentNode target) {
        if (!_produces.Contains(target)) {
            _produces.Add(target);
        }
    }

    /// <summary>
    /// 添加 produced_by 关系（仅供 DocumentGraphBuilder 内部使用）。
    /// </summary>
    internal void AddProducedByRelation(DocumentNode source) {
        if (!_producedBy.Contains(source)) {
            _producedBy.Add(source);
        }
    }

    /// <summary>
    /// 对关系列表按 FilePath 字典序排序，确保确定性输出。
    /// 遵循 [A-DOCGRAPH-004]：边按 TargetPath 字典序排序。
    /// 仅供 DocumentGraphBuilder 内部使用。
    /// </summary>
    internal void SortRelations() {
        _produces.Sort((a, b) => string.Compare(a.FilePath, b.FilePath, StringComparison.Ordinal));
        _producedBy.Sort((a, b) => string.Compare(a.FilePath, b.FilePath, StringComparison.Ordinal));
    }
}

/// <summary>
/// 文档类型。
/// </summary>
public enum DocumentType {
    /// <summary>
    /// Wish 文档（需求/愿望文档）。
    /// </summary>
    Wish,

    /// <summary>
    /// 产物文档（由 Wish 文档产生的文档）。
    /// </summary>
    Product
}
