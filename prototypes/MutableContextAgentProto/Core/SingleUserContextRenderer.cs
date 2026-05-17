using System.Text;

namespace Atelia.MutableContextAgentProto.Core;

public sealed class SingleUserContextRenderer {
    private readonly SingleUserContextRendererOptions _options;

    public SingleUserContextRenderer()
        : this(new SingleUserContextRendererOptions()) {
    }

    public SingleUserContextRenderer(SingleUserContextRendererOptions options) {
        _options = options;
    }

    public string Render(WorkingContext context) {
        ArgumentNullException.ThrowIfNull(context);

        var builder = new StringBuilder();

        builder.AppendLine("# 当前任务上下文");
        builder.AppendLine();
        builder.AppendLine("## 最初目标");
        builder.AppendLine(context.InitialGoal);
        builder.AppendLine();

        AppendActionLog(builder, context);
        AppendMemories(builder, context);
        AppendTransientViews(builder, context);
        AppendTools(builder, context);

        builder.AppendLine("## 下一步指令");
        builder.AppendLine(_options.NextStepInstruction);

        return builder.ToString().TrimEnd();
    }

    private void AppendActionLog(StringBuilder builder, WorkingContext context) {
        builder.AppendLine("## 近期行动日志");
        var actions = context.ActionLog
            .TakeLast(Math.Max(0, _options.MaxRecentActions))
            .ToArray();

        if (actions.Length == 0) {
            builder.AppendLine("- 暂无行动记录。");
        }
        else {
            foreach (var item in actions) {
                var detail = string.IsNullOrWhiteSpace(item.Detail) ? string.Empty : $": {item.Detail}";
                builder.AppendLine($"- [{item.Status}] {item.Title}{detail}");
            }
        }

        builder.AppendLine();
    }

    private void AppendMemories(StringBuilder builder, WorkingContext context) {
        builder.AppendLine("## 当前已知信息");
        var memories = context.Memories
            .TakeLast(Math.Max(0, _options.MaxMemories))
            .ToArray();

        if (memories.Length == 0) {
            builder.AppendLine("- 暂无已知信息。");
        }
        else {
            foreach (var item in memories) {
                var source = string.IsNullOrWhiteSpace(item.Source) ? string.Empty : $" (source: {item.Source})";
                builder.AppendLine($"- [{item.Kind}] {item.Content}{source}");
            }
        }

        builder.AppendLine();
    }

    private static void AppendTransientViews(StringBuilder builder, WorkingContext context) {
        builder.AppendLine("## 临时视图");
        if (context.TransientViews.Count == 0) {
            builder.AppendLine("- 暂无临时视图。");
        }
        else {
            foreach (var view in context.TransientViews) {
                var source = string.IsNullOrWhiteSpace(view.Source) ? string.Empty : $" (source: {view.Source})";
                builder.AppendLine($"### {view.Title} [{view.Id}]{source}");
                builder.AppendLine(view.Content);
                builder.AppendLine();
            }
        }

        builder.AppendLine();
    }

    private static void AppendTools(StringBuilder builder, WorkingContext context) {
        builder.AppendLine("## 可用工具");
        if (context.AvailableTools.Count == 0) {
            builder.AppendLine("- 暂无可用工具。");
        }
        else {
            foreach (var tool in context.AvailableTools) {
                var usage = string.IsNullOrWhiteSpace(tool.Usage) ? string.Empty : $" Usage: {tool.Usage}";
                builder.AppendLine($"- `{tool.Name}`: {tool.Description}{usage}");
            }
        }

        builder.AppendLine();
    }
}
