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

    public EnginePanelApp(
        AgentEngine engine,
        string? systemPromptTemplate = null,
        string? summarizePrompt = null
    ) {
        Engine = engine ?? throw new ArgumentNullException(nameof(engine));

        _buildSystemPrompt = BuildSystemPromptFactory(systemPromptTemplate);
        _summarizePrompt = summarizePrompt ?? ContextCompressionPrompts.DefaultSummarizePrompt;

        Tools = new ITool[] {
            MethodToolWrapper.FromDelegate<string?, string?>(CompressAsync),
        };
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

        if (context.HasPendingCompaction) { return BuildPendingWindow(tokens, capValue, percentage, profileName); }
        if (percentage < 60.0) { return null; }

        return BuildActionWindow(tokens, capValue, percentage, remaining, profileName);
    }

    [Tool("ctx_compress",
        "请求压缩当前上下文最旧的约一半（仅在 Observation→Action 边界处切分）。" +
        "压缩在本工具调用返回后的下一步 LLM 推理前自动执行，" +
        "被压缩段会替换为一条 Recap 摘要条目。" +
        "适用时机：当上下文 token 占比较高时主动调用，避免被动截断。" +
        "应在此调用后结束当前轮思考，将控制权交还给引擎以执行压缩。"
    )]
    private ValueTask<LodToolExecuteResult> CompressAsync(
        [ToolParam(
            "希望摘要中重点保留的内容（自然语言描述）。例如：" +
            "\"用户的核心目标与未完成的子任务、所有数字结论、文件路径与 blockId 引用\"。" +
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
            DebugUtil.Info(DebugCategory, "[ctx_compress] Compaction already pending; overwriting with new hints.");
        }

        var keep = string.IsNullOrWhiteSpace(keep_hints)
            ? ContextCompressionPrompts.DefaultKeepHint
            : keep_hints!;

        var forget = string.IsNullOrWhiteSpace(forget_hints)
            ? ContextCompressionPrompts.DefaultForgetHint
            : forget_hints!;

        var systemPrompt = _buildSystemPrompt(keep, forget);

        DebugUtil.Info(DebugCategory, $"[ctx_compress] Requesting compaction. keep='{keep}' forget='{forget}'");

        var ok = Engine.RequestCompaction(systemPrompt, _summarizePrompt);

        if (!ok) {
            DebugUtil.Warning(DebugCategory, "[ctx_compress] Compaction request failed (no valid split point).");
            return Fail(
                "无法压缩：当前历史不足以形成合法切分点（至少需要一组完整的 Observation→Action 往返）。" +
                "请继续工作，待历史增长后再次尝试。"
            );
        }

        return Ok(
            "已请求上下文压缩：将在下一步推理前把最旧的约一半历史替换为 Recap 摘要。" +
            "建议你结束本轮思考，把控制权交还给引擎以执行压缩。"
        );
    }

    private static string? BuildPendingWindow(ulong tokens, ulong capValue, double percentage, string? profileName) {
        var sb = new StringBuilder();
        sb.Append("## ContextCompression\n\n");
        sb.Append("Token 估算: ").Append(tokens).Append(" / ").Append(capValue).Append(" (");
        sb.AppendFormat(CultureInfo.InvariantCulture, "{0:F1}", percentage);
        sb.Append("%)\n");
        sb.Append("状态: 压缩请求已排队——下一步推理前将自动执行上下文压缩。\n");
        if (profileName is not null) {
            sb.Append("Profile: ").Append(profileName).Append('\n');
        }
        sb.Append('\n');
        return sb.ToString();
    }

    private static string? BuildActionWindow(
        ulong tokens, ulong capValue, double percentage, ulong remaining, string? profileName
    ) {
        string statusLine;
        if (percentage >= 100.0) {
            statusLine = "已超软上限——剩余预算为 0，引擎可能在下一步自动触发压缩（若 AutoCompactionOptions 已配置），" +
                "或应立即调用 ctx_compress。";
        }
        else if (percentage >= 90.0) {
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
        sb.Append('\n');
        sb.Append("→ 调用模板:\n");
        sb.Append("ctx_compress(\n");
        sb.Append("  keep_hints=\"描述需重点保留的内容\",\n");
        sb.Append("  forget_hints=\"描述可遗忘的内容\"\n");
        sb.Append(")\n");

        return sb.ToString();
    }

    private static ValueTask<LodToolExecuteResult> Ok(string message)
        => ValueTask.FromResult(
            new LodToolExecuteResult(
                ToolExecutionStatus.Success,
                new LevelOfDetailContent(message)
            )
        );

    private static ValueTask<LodToolExecuteResult> Fail(string message)
        => ValueTask.FromResult(
            new LodToolExecuteResult(
                ToolExecutionStatus.Failed,
                new LevelOfDetailContent(message)
            )
        );

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
