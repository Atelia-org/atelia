// DocGraph v0.2 - 目标汇总器
// 参考：W-0007 Shape.md §1.1, §2
// 继承 TwoTierAggregatorBase，提供 Goal 特定的解析逻辑
// ID 必填：没有 ID 的条目会被跳过

using Atelia.DocGraph.Core;

namespace Atelia.DocGraph.Visitors;

/// <summary>
/// 目标汇总器：从 goals 字段生成两层输出。
/// - 全局：wish-panels/goals.gen.md
/// - Wish 级别：wish/W-XXXX-slug/project-status/goals.gen.md
/// 遵循 [F-VISITOR-001] 规范。
/// </summary>
public class GoalAggregator : TwoTierAggregatorBase<GoalAggregator.Goal> {
    #region TwoTierAggregatorBase 抽象成员实现

    /// <inheritdoc/>
    protected override string FieldName => "goals";

    /// <inheritdoc/>
    protected override string GlobalOutputPath => "wish-panels/goals.gen.md";

    /// <inheritdoc/>
    protected override string WishOutputFileName => "goals.gen.md";

    /// <inheritdoc/>
    protected override string GlobalTitle => "目标汇总";

    /// <inheritdoc/>
    protected override string EmptyMessage => "*暂无目标记录*";

    /// <inheritdoc/>
    protected override string EmptyActiveMessage => "*暂无活跃目标*";

    /// <inheritdoc/>
    protected override string EmptyResolvedMessage => "*暂无已完成目标*";

    /// <inheritdoc/>
    protected override string ItemCountLabel => "目标数";

    /// <inheritdoc/>
    public override IReadOnlyList<string> RequiredFields => [KnownFrontmatterFields.Goals];

    /// <inheritdoc/>
    protected override List<Goal> ExtractItems(DocumentNode node, object fieldValue) {
        var result = new List<Goal>();

        if (fieldValue is not IEnumerable<object> goalsList) { return result; }

        foreach (var item in goalsList) {
            // 只支持对象格式: {id: "X-ID", description: "描述", status?: "open"|"resolved"}
            if (item is IDictionary<object, object> goalDict) {
                var id = goalDict.TryGetValue("id", out var idValue) ? idValue?.ToString() : null;
                var description = goalDict.TryGetValue("description", out var descValue) ? descValue?.ToString() : null;
                var status = goalDict.TryGetValue("status", out var statusValue) ? statusValue?.ToString() : "open";

                // ID 必填
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(description)) {
                    result.Add(
                        new Goal {
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
    protected override string GetItemId(Goal item) => item.Id;

    /// <inheritdoc/>
    protected override string GetItemDescription(Goal item) => item.Description;

    /// <inheritdoc/>
    protected override string GetSourceDocument(Goal item) => item.SourceDocument;

    /// <inheritdoc/>
    protected override DocumentNode? GetSourceNode(Goal item) => item.SourceNode;

    /// <inheritdoc/>
    protected override string GetItemStatus(Goal item) => item.Status;

    #endregion

    /// <summary>
    /// 目标数据结构。
    /// </summary>
    public class Goal {
        public string Id { get; set; } = "";
        public string Description { get; set; } = "";
        public string Status { get; set; } = "open";
        public string SourceDocument { get; set; } = "";
        public DocumentNode? SourceNode { get; set; }
    }
}
