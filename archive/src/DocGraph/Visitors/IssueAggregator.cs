// DocGraph v0.2 - 问题汇总器
// 参考：W-0007 Shape.md §1.2, §2
// 继承 TwoTierAggregatorBase，提供 Issue 特定的解析逻辑
// ID 必填：没有 ID 的条目会被跳过

using Atelia.DocGraph.Core;

namespace Atelia.DocGraph.Visitors;

/// <summary>
/// 问题汇总器：从 issues 字段生成两层输出。
/// - 全局：wish-panels/issues.gen.md
/// - Wish 级别：wish/W-XXXX-slug/project-status/issues.gen.md
/// 遵循 [F-VISITOR-001] 规范。
/// </summary>
public class IssueAggregator : TwoTierAggregatorBase<IssueAggregator.Issue> {
    #region TwoTierAggregatorBase 抽象成员实现

    /// <inheritdoc/>
    protected override string FieldName => "issues";

    /// <inheritdoc/>
    protected override string GlobalOutputPath => "wish-panels/issues.gen.md";

    /// <inheritdoc/>
    protected override string WishOutputFileName => "issues.gen.md";

    /// <inheritdoc/>
    protected override string GlobalTitle => "问题汇总";

    /// <inheritdoc/>
    protected override string EmptyMessage => "*暂无问题记录*";

    /// <inheritdoc/>
    protected override string EmptyActiveMessage => "*暂无活跃问题*";

    /// <inheritdoc/>
    protected override string EmptyResolvedMessage => "*暂无已解决问题*";

    /// <inheritdoc/>
    protected override string ItemCountLabel => "问题数";

    /// <inheritdoc/>
    public override IReadOnlyList<string> RequiredFields => [KnownFrontmatterFields.Issues];

    /// <inheritdoc/>
    protected override List<Issue> ExtractItems(DocumentNode node, object fieldValue) {
        var result = new List<Issue>();

        if (fieldValue is not IEnumerable<object> issuesList) { return result; }

        foreach (var item in issuesList) {
            // 只支持对象格式: {id, description, status?, ...}
            if (item is IDictionary<object, object> dict) {
                var id = dict.TryGetValue("id", out var i) ? i?.ToString() : null;
                var description = dict.TryGetValue("description", out var d) ? d?.ToString() : null;
                var status = dict.TryGetValue("status", out var s) ? s?.ToString() : "open";

                // ID 必填：跳过没有 ID 的条目
                if (string.IsNullOrWhiteSpace(id)) { continue; }

                if (!string.IsNullOrWhiteSpace(description)) {
                    result.Add(
                        new Issue {
                            Id = id,
                            Description = description,
                            Status = status ?? "open",
                            SourceDocument = node.FilePath,
                            SourceNode = node
                        }
                    );
                }
            }
        }

        return result;
    }

    /// <inheritdoc/>
    protected override string GetItemId(Issue item) => item.Id;

    /// <inheritdoc/>
    protected override string GetItemDescription(Issue item) => item.Description;

    /// <inheritdoc/>
    protected override string GetSourceDocument(Issue item) => item.SourceDocument;

    /// <inheritdoc/>
    protected override DocumentNode? GetSourceNode(Issue item) => item.SourceNode;

    /// <inheritdoc/>
    protected override string GetItemStatus(Issue item) => item.Status;

    #endregion

    /// <summary>
    /// 问题数据结构。
    /// </summary>
    public class Issue {
        public string Id { get; set; } = "";
        public string Description { get; set; } = "";
        public string Status { get; set; } = "open";
        public string SourceDocument { get; set; } = "";
        public DocumentNode? SourceNode { get; set; }
    }
}
