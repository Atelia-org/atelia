// DocGraph v0.2 - 两级聚合器基类
// 提供 Issues/Goals 等聚合器的共享实现
// 输出模式：全局（wish-panels/）+ Wish 级别（project-status/）

using System.Text;
using System.Text.RegularExpressions;
using Atelia.DocGraph.Core;

namespace Atelia.DocGraph.Visitors;

/// <summary>
/// 两级聚合器基类：提供全局 + Wish 级别输出的共享实现。
/// 子类实现条目提取逻辑，基类处理分组、输出格式。
/// </summary>
/// <typeparam name="TItem">条目类型（Issue, Goal 等）。</typeparam>
public abstract partial class TwoTierAggregatorBase<TItem> : IDocumentGraphVisitor
    where TItem : class {
    /// <summary>
    /// Wish 路径正则：从文件路径提取 wish/W-XXXX-slug 部分。
    /// </summary>
    [GeneratedRegex(@"^(wish/W-\d{4}-[^/]+)/")]
    private static partial Regex WishPathRegex();

    /// <summary>
    /// Wish ID 提取正则：只匹配 W-XXXX 部分。
    /// </summary>
    [GeneratedRegex(@"wish/(W-\d{4})-[^/]+/?")]
    private static partial Regex WishIdRegex();

    #region 子类必须实现的抽象成员

    /// <summary>
    /// Frontmatter 字段名（如 "issues", "goals"）。
    /// </summary>
    protected abstract string FieldName { get; }

    /// <summary>
    /// 全局输出文件路径（如 "docs/issues.gen.md"）。
    /// </summary>
    protected abstract string GlobalOutputPath { get; }

    /// <summary>
    /// Wish 级别输出文件名（如 "issues.md"）。
    /// </summary>
    protected abstract string WishOutputFileName { get; }

    /// <summary>
    /// 全局输出标题（如 "问题汇总"）。
    /// </summary>
    protected abstract string GlobalTitle { get; }

    /// <summary>
    /// 无条目时的提示文本（如 "*暂无问题记录*"）。
    /// </summary>
    protected abstract string EmptyMessage { get; }

    /// <summary>
    /// Active 区域无条目时的提示文本（如 "*暂无活跃问题*"）。
    /// </summary>
    protected abstract string EmptyActiveMessage { get; }

    /// <summary>
    /// Resolved 区域无条目时的提示文本（如 "*暂无已解决问题*"）。
    /// </summary>
    protected abstract string EmptyResolvedMessage { get; }

    /// <summary>
    /// 统计概览中的条目类型名称（如 "问题数"、"目标数"）。
    /// </summary>
    protected abstract string ItemCountLabel { get; }

    /// <summary>
    /// 从文档节点和字段值提取条目列表。
    /// </summary>
    protected abstract List<TItem> ExtractItems(DocumentNode node, object fieldValue);

    /// <summary>
    /// 获取条目 ID（必填，非空）。
    /// </summary>
    protected abstract string GetItemId(TItem item);

    /// <summary>
    /// 获取条目描述。
    /// </summary>
    protected abstract string GetItemDescription(TItem item);

    /// <summary>
    /// 获取条目来源文档路径。
    /// </summary>
    protected abstract string GetSourceDocument(TItem item);

    /// <summary>
    /// 获取条目来源节点（用于 ProducedBy 关系查询）。
    /// </summary>
    protected abstract DocumentNode? GetSourceNode(TItem item);

    /// <summary>
    /// 获取条目状态（"open" 或 "resolved"）。
    /// </summary>
    protected abstract string GetItemStatus(TItem item);

    #endregion

    #region IDocumentGraphVisitor 实现

    /// <inheritdoc/>
    public string Name => FieldName;

    /// <inheritdoc/>
    public string OutputPath => GlobalOutputPath;

    /// <inheritdoc/>
    public abstract IReadOnlyList<string> RequiredFields { get; }

    /// <inheritdoc/>
    public string Generate(DocumentGraph graph) {
        var (activeItems, resolvedItems) = CollectAllItems(graph);
        return GenerateGlobalOutput(graph, activeItems, resolvedItems);
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string>? GenerateMultiple(DocumentGraph graph) {
        var (activeItems, resolvedItems) = CollectAllItems(graph);
        var outputs = new Dictionary<string, string>();

        // 全局输出
        outputs[GlobalOutputPath] = GenerateGlobalOutput(graph, activeItems, resolvedItems);

        // Wish 级别输出
        var allItemsWithStatus = activeItems.Select(i => (Item: i, IsResolved: false))
            .Concat(resolvedItems.Select(i => (Item: i, IsResolved: true)))
            .ToList();

        var itemsByWish = allItemsWithStatus
            .Select(x => (x.Item, x.IsResolved, WishPath: GetOwningWishPath(x.Item)))
            .Where(x => x.WishPath != null)
            .GroupBy(x => x.WishPath!)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var group in itemsByWish) {
            var wishPath = group.Key;
            var wishId = ExtractWishId(wishPath);
            var outputPath = $"{wishPath}/project-status/{WishOutputFileName}";
            var wishActive = group.Where(x => !x.IsResolved).Select(x => x.Item).ToList();
            var wishResolved = group.Where(x => x.IsResolved).Select(x => x.Item).ToList();
            outputs[outputPath] = GenerateWishOutput(wishActive, wishResolved, wishId, wishPath);
        }

        return outputs;
    }

    #endregion

    #region 数据收集

    /// <summary>
    /// 从所有文档收集条目，按 status 分流。
    /// </summary>
    private (List<TItem> Active, List<TItem> Resolved) CollectAllItems(DocumentGraph graph) {
        var activeItems = new List<TItem>();
        var resolvedItems = new List<TItem>();

        graph.ForEachDocument(
            node => {
                if (node.Frontmatter.TryGetValue(FieldName, out var fieldValue) && fieldValue != null) {
                    var docItems = ExtractItems(node, fieldValue);
                    foreach (var item in docItems) {
                        var status = GetItemStatus(item);
                        if (status == "resolved") {
                            resolvedItems.Add(item);
                        }
                        else {
                            activeItems.Add(item);
                        }
                    }
                }
            }
        );

        return (activeItems, resolvedItems);
    }

    /// <summary>
    /// 获取条目所属的 Wish 路径。
    /// 优先从 ProducedBy 关系获取，其次从文件路径提取。
    /// </summary>
    protected string? GetOwningWishPath(TItem item) {
        var sourceNode = GetSourceNode(item);

        // 优先从 ProducedBy 关系获取（只接受 wish/ 路径的节点）
        if (sourceNode != null && sourceNode.ProducedBy.Count > 0) {
            var wishNode = sourceNode.ProducedBy
                .FirstOrDefault(p => WishPathRegex().IsMatch(p.FilePath));
            if (wishNode != null) {
                var match = WishPathRegex().Match(wishNode.FilePath);
                if (match.Success) { return match.Groups[1].Value; }
            }
        }

        // 其次从文件路径提取
        var sourceDocument = GetSourceDocument(item);
        var pathMatch = WishPathRegex().Match(sourceDocument);
        if (pathMatch.Success) { return pathMatch.Groups[1].Value; }

        return null;
    }

    /// <summary>
    /// 从 Wish 路径提取 Wish ID（如 W-0007）。
    /// </summary>
    protected static string ExtractWishId(string wishPath) {
        var match = WishIdRegex().Match(wishPath);
        return match.Success ? match.Groups[1].Value : wishPath;
    }

    #endregion

    #region 输出生成

    /// <summary>
    /// 生成全局输出（按源文件分组的子弹列表格式）。
    /// </summary>
    protected virtual string GenerateGlobalOutput(DocumentGraph graph, List<TItem> activeItems, List<TItem> resolvedItems) {
        var builder = new StringBuilder();

        // 文件头
        AppendFileHeader(builder);

        builder.AppendLine($"# {GlobalTitle}");
        builder.AppendLine();

        var totalCount = activeItems.Count + resolvedItems.Count;
        if (totalCount == 0) {
            builder.AppendLine(EmptyMessage);
            return builder.ToString();
        }

        // 生成统计概览
        builder.AppendLine("## 统计概览");
        builder.AppendLine();
        builder.AppendLine($"- 总{ItemCountLabel}：{totalCount}");
        builder.AppendLine($"- Active：{activeItems.Count}");
        builder.AppendLine($"- Resolved：{resolvedItems.Count}");
        builder.AppendLine();

        // 只渲染 Active 条目（Resolved 条目只计入统计）
        if (activeItems.Count == 0) {
            builder.AppendLine(EmptyActiveMessage);
            return builder.ToString();
        }

        var itemsBySourceFile = activeItems
            .GroupBy(i => GetSourceDocument(i))
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var fileGroup in itemsBySourceFile) {
            var sourceFile = fileGroup.Key;

            builder.AppendLine($"## `{sourceFile}`");
            builder.AppendLine();

            foreach (var item in fileGroup.OrderBy(i => GetItemId(i), StringComparer.Ordinal)) {
                var id = GetItemId(item);
                var description = GetItemDescription(item);
                builder.AppendLine($"- {id}: {description}");
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    /// <summary>
    /// 生成 Wish 级别输出（按源文件分组的子弹列表格式）。
    /// </summary>
    protected virtual string GenerateWishOutput(List<TItem> activeItems, List<TItem> resolvedItems, string wishId, string wishBasePath) {
        var builder = new StringBuilder();

        // 文件头
        AppendFileHeader(builder);

        builder.AppendLine($"# {wishId} {char.ToUpper(FieldName[0])}{FieldName.Substring(1)}");
        builder.AppendLine();

        // Active 区域
        builder.AppendLine($"## Active {char.ToUpper(FieldName[0])}{FieldName.Substring(1)}");
        builder.AppendLine();

        if (activeItems.Count == 0) {
            builder.AppendLine(EmptyActiveMessage);
        }
        else {
            AppendItemsBySource(builder, activeItems, wishId);
        }

        builder.AppendLine();

        // Resolved 区域
        builder.AppendLine($"## Resolved {char.ToUpper(FieldName[0])}{FieldName.Substring(1)}");
        builder.AppendLine();

        if (resolvedItems.Count == 0) {
            builder.AppendLine(EmptyResolvedMessage);
        }
        else {
            AppendItemsBySource(builder, resolvedItems, wishId);
        }

        builder.AppendLine();

        return builder.ToString();
    }

    /// <summary>
    /// 按源文件分组添加条目列表。
    /// </summary>
    private void AppendItemsBySource(StringBuilder builder, List<TItem> items, string wishId) {
        var itemsBySource = items
            .GroupBy(i => GetRelativeSourcePath(GetSourceDocument(i), wishId))
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var fileGroup in itemsBySource) {
            var sourcePath = fileGroup.Key;
            builder.AppendLine($"### `{sourcePath}`");
            builder.AppendLine();

            foreach (var item in fileGroup.OrderBy(i => GetItemId(i), StringComparer.Ordinal)) {
                var id = GetItemId(item);
                var description = GetItemDescription(item);
                builder.AppendLine($"- {id}: {description}");
            }

            builder.AppendLine();
        }
    }

    /// <summary>
    /// 获取相对于 Wish 目录的源文件路径。
    /// </summary>
    private string GetRelativeSourcePath(string sourcePath, string wishId) {
        // 检查是否在 Wish 目录下
        if (sourcePath.StartsWith("wish/") && (sourcePath.Contains($"/{wishId}") || sourcePath.Contains($"-{wishId.Substring(2)}"))) {
            var wishMatch = WishPathRegex().Match(sourcePath);
            if (wishMatch.Success) {
                var wishDir = wishMatch.Groups[1].Value;
                return sourcePath.Substring(wishDir.Length + 1); // +1 for the trailing slash
            }
        }

        return sourcePath;
    }

    /// <summary>
    /// 添加文件头。
    /// </summary>
    private static void AppendFileHeader(StringBuilder builder) {
        builder.AppendLine("<!-- 本文档由 DocGraph 工具自动生成，手动编辑无效 -->");
        builder.AppendLine("<!-- 再生成命令：docgraph -->");
        builder.AppendLine();
    }

    #endregion
}
