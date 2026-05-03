using System.Globalization;
using System.Text;
using Atelia.Agent.Core.Apps;
using Atelia.Agent.Core.History;
using Atelia.Agent.Core.Tool;
using Atelia.Completion.Abstractions;
using Atelia.Diagnostics;

namespace Atelia.Agent.Core.App;

/// <summary>
/// 由 <see cref="AgentEngine"/> 自身创建并托管的运行时控制面板 App。
/// </summary>
/// <remarks>
/// 当前 panel 先承载上下文压缩控制面：向 Agent 暴露 <c>ctx_compress</c> 工具，
/// 并在接近软上限时以 window 形式展示 Token 用量与行动建议。
/// 当后续确实出现第二类 engine-owned panel 时，再重新抽离公共抽象会更合适；
/// 目前直接保留单一 concrete type 能避免为了未来可能性而维持空基类。
/// </remarks>
public sealed class EnginePanelApp : IApp {
    private const string DebugCategory = "ContextCompression";
    private const string KeepToken = "{KEEP_HINTS}";
    private const string ForgetToken = "{FORGET_HINTS}";

    private readonly Func<string, string, string> _buildSystemPrompt;
    private readonly string _summarizePrompt;
    private readonly ITool _compressTool;

    public EnginePanelApp(
        AgentEngine engine,
        string? systemPromptTemplate = null,
        string? summarizePrompt = null
    ) {
        Engine = engine ?? throw new ArgumentNullException(nameof(engine));

        _buildSystemPrompt = BuildSystemPromptFactory(systemPromptTemplate);
        _summarizePrompt = summarizePrompt ?? ContextCompressionPrompts.DefaultSummarizePrompt;

        _compressTool = MethodToolWrapper.FromDelegate<string?, string?>(CompressAsync);
        // 工具初始对 LLM 不可见；在 RenderWindow 计算阈值后再决定是否可见，
        // 保证工具出现时机与提示窗口完全对齐，避免模型在低 token 占比时被不必要地分心。
        _compressTool.Visible = false;
        Tools = new ITool[] { _compressTool };
    }

    /// <summary>
    /// 宿主引擎实例。仅供 panel 读取运行时状态或发起宿主级操作。
    /// </summary>
    public AgentEngine Engine { get; }

    public string Name => "EnginePanel";

    public string Description =>
        "Agent 运行时控制面板。当前提供上下文压缩与 Token 用量监视能力。";

    public IReadOnlyList<ITool> Tools { get; }

    public string? RenderWindow(AppRenderContext context) {
        var profile = context.CurrentProfile;
        if (profile is null) { return null; }

        var tokens = context.EstimatedContextTokens;
        var capValue = (ulong)profile.SoftContextTokenCap;
        var percentage = (double)tokens / capValue * 100.0;
        var remaining = tokens >= capValue ? 0UL : capValue - tokens;
        var profileName = profile.Name;

        if (percentage < 85.0) {
            _compressTool.Visible = false;
            return null;
        }

        _compressTool.Visible = true;
        DebugUtil.Info(DebugCategory, $"[ctx_compress] Window visible. tokens={tokens}/{capValue} ({percentage:F1}%) remaining={remaining} profile={profileName}");
        return BuildActionWindow(tokens, capValue, percentage, remaining, profileName, context.EstimatedCompactionPreview);
    }

    [Tool("ctx_compress",
        "压缩当前上下文最旧的约一半（仅在 Observation→Action 边界处切分）。" +
        "若当前上下文已给出预计压缩范围，请先阅读，再用 keep_hints 说明这段旧历史里哪些信息即使暂时看似普通也必须保留。" +
        "若你在当前模型输出中调用本工具，引擎会复用该次决策参考的边界执行，保证所见即所得。" +
        "本工具会在当前工具执行阶段立即完成压缩，" +
        "被压缩段会替换为一条 Recap 摘要条目，并把实际释放的容量信息作为工具结果返回。" +
        "适用时机：当上下文 token 占比较高时主动调用，避免被动截断。" +
        "为简化执行语义，本轮若存在多个工具调用，当前实现仍按模型给出的原始顺序依次执行。"
    )]
    private async ValueTask<LodToolExecuteResult> CompressAsync(
        [ToolParam(
            "希望摘要中重点保留的内容（自然语言描述）。例如：" +
            "\"用户的核心目标与未完成的子任务、所有数字结论、文件路径与 blockId 引用\"。" +
            "若当前上下文已给出预计压缩范围，请优先说明那段旧历史里哪些信息虽然现在不显眼、但对当前目标仍关键。" +
            "留空则使用通用保留策略（核心目标、决策、未解决项）。"
        )] string? keep_hints = null,
        [ToolParam(
            "希望摘要中主动遗忘或淡化的内容（自然语言描述）。例如：" +
            "\"已废弃的探索分支、已被覆盖的中间假设、冗长的工具原始输出\"。" +
            "留空则使用通用淡化策略（重复试错、已修正的错误推理）。"
        )] string? forget_hints = null,
        CancellationToken cancellationToken = default
    ) {
        cancellationToken.ThrowIfCancellationRequested();

        if (Engine.HasPendingCompaction) {
            DebugUtil.Warning(DebugCategory, "[ctx_compress] Compaction already pending; immediate tool execution aborted.");
            return Fail("当前已有待执行的上下文压缩请求，请先让引擎完成该请求后再试。");
        }

        var keep = string.IsNullOrWhiteSpace(keep_hints)
            ? ContextCompressionPrompts.DefaultKeepHint
            : keep_hints!;

        var forget = string.IsNullOrWhiteSpace(forget_hints)
            ? ContextCompressionPrompts.DefaultForgetHint
            : forget_hints!;

        var systemPrompt = _buildSystemPrompt(keep, forget);

        DebugUtil.Info(DebugCategory, $"[ctx_compress] Requesting compaction. keep='{keep}' forget='{forget}'");

        // TODO: If engine-owned tool priorities are introduced, ctx_compress may want an explicit "execute last in batch" priority.
        // For now we keep the model-emitted order unchanged and execute compaction immediately when this tool is reached.
        var outcome = await Engine.ExecuteCompactionImmediateAsync(systemPrompt, _summarizePrompt, cancellationToken).ConfigureAwait(false);

        if (!outcome.Applied) {
            DebugUtil.Warning(DebugCategory, $"[ctx_compress] Immediate compaction failed reason={outcome.FailureReason?.ToString() ?? "unknown"}.");
            return Fail(BuildCompactionFailureMessage(outcome));
        }

        DebugUtil.Info(DebugCategory, $"[ctx_compress] Succeeded. before={FormatPercent(outcome.BeforeCapacityRatio)} after={FormatPercent(outcome.AfterCapacityRatio)} released={FormatPercent(outcome.ReleasedCapacityRatio)} history={outcome.HistoryCountBefore}→{outcome.HistoryCountAfter} summaryLen={outcome.SummaryLength}");
        return Ok(BuildCompactionSuccessMessage(outcome));
    }

    private static string? BuildActionWindow(
        ulong tokens,
        ulong capValue,
        double percentage,
        ulong remaining,
        string? profileName,
        CompactionPreview? compactionPreview
    ) {
        string statusLine;
        if (percentage >= 100.0) {
            statusLine = "已超软上限——剩余预算为 0，引擎可能在下一步自动触发压缩（若 AutoCompactionOptions 已配置），" +
                "或应立即调用 ctx_compress。";
        }
        else if (percentage >= 95.0) {
            statusLine = "逼近软上限——剩余预算仅约 " + remaining + " tokens，强烈建议立即调用 ctx_compress。";
        }
        else {
            statusLine = "接近软上限——建议在 1-2 轮内调用 ctx_compress，" +
                "以免后续工具调用或长回复导致被动截断。";
        }

        var sb = new StringBuilder();
        sb.Append("## ContextCompression\n\n");
        sb.Append("Token 估算: ").Append(tokens).Append(" / ").Append(capValue).Append(" (");
        sb.AppendFormat(CultureInfo.InvariantCulture, "{0:F1}", percentage);
        sb.Append("%)\n");
        sb.Append("剩余预算: ").Append(remaining).Append('\n');
        sb.Append("状态: ").Append(statusLine).Append('\n');
        if (profileName is not null) {
            sb.Append("Profile: ").Append(profileName).Append('\n');
        }
        AppendCompactionPreview(sb, compactionPreview, tokens);
        sb.Append('\n');
        sb.Append("→ 调用模板:\n");
        sb.Append("ctx_compress(\n");
        sb.Append("  keep_hints=\"结合预计压缩范围，描述那段旧历史中必须保留的内容\",\n");
        sb.Append("  forget_hints=\"描述可遗忘的内容\"\n");
        sb.Append(")\n");

        return sb.ToString();
    }

    private static LodToolExecuteResult Ok(string message)
        => new(
            ToolExecutionStatus.Success,
            new LevelOfDetailContent(message)
        );

    private static LodToolExecuteResult Fail(string message)
        => new(
            ToolExecutionStatus.Failed,
            new LevelOfDetailContent(message)
        );

    private static string BuildCompactionSuccessMessage(AgentEngine.CompactionExecutionResult outcome) {
        return "上下文压缩成功：估算占用从 "
            + FormatPercent(outcome.BeforeCapacityRatio)
            + " 降到 "
            + FormatPercent(outcome.AfterCapacityRatio)
            + "，释放了约 "
            + FormatPercent(outcome.ReleasedCapacityRatio)
            + " 的软上限预算。"
            + " 历史条目 "
            + outcome.HistoryCountBefore
            + " → "
            + outcome.HistoryCountAfter
            + "，摘要长度 "
            + outcome.SummaryLength
            + " 字符。";
    }

    private static string BuildCompactionFailureMessage(AgentEngine.CompactionExecutionResult outcome) {
        return outcome.FailureReason switch {
            AgentEngine.CompactionFailureReason.NoValidSplitPoint => "无法压缩：当前历史不足以形成合法切分点（至少需要 Observation→Action 边界）。请继续工作后再试。",
            AgentEngine.CompactionFailureReason.InvalidSplitPoint => "无法压缩：本次锁定的压缩边界在执行时已不再合法，请稍后重试。",
            AgentEngine.CompactionFailureReason.EmptySummary => "无法压缩：摘要模型返回了空结果，请稍后重试。",
            _ => "无法压缩：内部压缩执行失败，请稍后重试。"
        };
    }

    private static string FormatPercent(double ratio)
        => string.Format(CultureInfo.InvariantCulture, "{0:F1}%", ratio * 100.0);

    private static void AppendCompactionPreview(
        StringBuilder sb,
        CompactionPreview? compactionPreview,
        ulong estimatedContextTokens
    ) {
        if (!compactionPreview.HasValue) { return; }

        var preview = compactionPreview.Value;
        double contextRatio = estimatedContextTokens == 0
            ? 0.0
            : (double)preview.PrefixTokenEstimate / estimatedContextTokens;

        sb.Append("预计压缩范围: 最旧 ")
            .Append(preview.PrefixEntryCount)
            .Append(" 条历史，约占当前上下文 ")
            .Append(FormatPercent(contextRatio))
            .Append("。\n");
        sb.Append("边界前最后一条: ").Append(preview.PrefixEndPreview).Append('\n');
        sb.Append("边界后第一条保留内容: ").Append(preview.SuffixStartPreview).Append('\n');
        sb.Append("保证: 若你现在直接调用 ctx_compress，本次执行会复用这里显示的边界。\n");
        sb.Append("提示: 写 keep_hints 时，请用当前全局重要性判断这段旧历史里哪些信息即使现在看似普通也必须保留。\n");
    }

    private static Func<string, string, string> BuildSystemPromptFactory(string? template) {
        if (template is null) { template = ContextCompressionPrompts.DefaultSystemPromptTemplate; }

        if (!template.Contains(KeepToken) || !template.Contains(ForgetToken)) {
            throw new ArgumentException(
                $"自定义 SystemPrompt 模板必须包含 {KeepToken} 和 {ForgetToken} 两个标记。" +
                "请使用 ContextCompressionPrompts.DefaultSystemPromptTemplate 作为参考。",
                nameof(template)
            );
        }

        var captured = template;
        return (keep, forget) => captured
            .Replace(KeepToken, keep)
            .Replace(ForgetToken, forget);
    }
}
