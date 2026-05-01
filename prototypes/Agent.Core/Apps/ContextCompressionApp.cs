using System.Globalization;
using System.Text;
using Atelia.Agent.Core.History;
using Atelia.Agent.Core.Tool;
using Atelia.Completion.Abstractions;
using Atelia.Diagnostics;

namespace Atelia.Agent.Core.Apps;

/// <summary>
/// 上下文压缩 App：向 LLM Agent 暴露主动压缩工具，并在超过软上限阈值时通过 Window 展示 Token 用量与行动建议。
/// </summary>
/// <remarks>
/// <para>
/// <b>事件驱动 Window</b>：安全态（&lt;60%）静默无输出；仅在接近/超过 <see cref="LlmProfile.SoftContextTokenCap"/>
/// 时展开带因果说明的用量信息与可抄改的 <c>ctx_compress</c> 调用模板。
/// 这避免了长期 Agent 执行中的 banner blindness。
/// </para>
/// <para>
/// 提供 <c>ctx_compress</c> 工具，让 LLM Agent 根据当前任务语境主动请求压缩"最旧的一半"上下文。
/// 压缩在工具调用返回后的下一次 <see cref="AgentEngine.StepAsync"/> 中自动执行，
/// 被压缩段替换为一条 Recap 摘要条目。
/// </para>
/// <para>
/// 与 <see cref="AutoCompactionOptions"/> 的关系：
/// 自动压缩（被动触发）和本工具（主动触发）最终都走 <see cref="AgentEngine.RequestCompaction"/>
/// → ProcessCompactingAsync 同一执行通道。工具版携带 LLM 当时注入的 keep/forget 提示，
/// 比 fallback 固定 prompt 更有信息量；若 LLM 在自动触发已生效后再调用本工具，
/// 自动版的 prompt 会被工具版覆盖（这是合理行为）。
/// </para>
/// <para>
/// <b>演化方向</b>：当前压缩策略固定为"最旧一半 Recap"。
/// 本工具的参数设计为意图层（keep/forget），不绑定当前实现机制，
/// 未来后端可从"最旧一半 Recap"演化为分层记忆搬运、选择性遗忘、按主题压缩等策略。
/// </para>
/// </remarks>
public sealed class ContextCompressionApp : IApp {
    private const string DebugCategory = "ContextCompression";

    private readonly AgentEngine _engine;
    private readonly Func<LlmProfile?> _profileAccessor;
    private readonly Func<string, string, string> _buildSystemPrompt;

    private const string KeepToken = "{KEEP_HINTS}";
    private const string ForgetToken = "{FORGET_HINTS}";

    /// <summary>
    /// 初始化 <see cref="ContextCompressionApp"/> 的新实例。
    /// </summary>
    /// <param name="engine">
    /// 目标 Agent 引擎。本 App 通过它发起压缩请求。
    /// </param>
    /// <param name="profileAccessor">
    /// 返回"当前活动 <see cref="LlmProfile"/> 的访问器，用于 Window 显示软上限与占比。
    /// 宿主代码通常在每次 <see cref="AgentEngine.StepAsync"/> 前更新当前 profile 变量，
    /// 然后传入 <c>() =&gt; currentProfile</c>。
    /// 返回 <c>null</c> 表示"未知 profile"，此时 Window 不显示百分比、只显示绝对值。
    /// </param>
    /// <param name="systemPromptTemplate">
    /// 可选的 SystemPrompt 模板覆盖。模板必须包含 <c>{KEEP_HINTS}</c> 和 <c>{FORGET_HINTS}</c> 两个标记。
    /// 传 <c>null</c> 使用 <see cref="ContextCompressionPrompts.DefaultSystemPromptTemplate"/>。
    /// </param>
    /// <param name="summarizePrompt">
    /// 可选的 SummarizePrompt 覆盖——追加在待摘要历史末尾的请求消息。
    /// 传 <c>null</c> 使用 <see cref="ContextCompressionPrompts.DefaultSummarizePrompt"/>。
    /// </param>
    /// <exception cref="ArgumentException">
    /// 自定义模板缺少 <c>{KEEP_HINTS}</c> 或 <c>{FORGET_HINTS}</c> 标记。
    /// </exception>
    public ContextCompressionApp(
        AgentEngine engine,
        Func<LlmProfile?> profileAccessor,
        string? systemPromptTemplate = null,
        string? summarizePrompt = null
    ) {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _profileAccessor = profileAccessor ?? throw new ArgumentNullException(nameof(profileAccessor));
        _buildSystemPrompt = BuildSystemPromptFactory(systemPromptTemplate);
        _summarizePrompt = summarizePrompt ?? ContextCompressionPrompts.DefaultSummarizePrompt;

        Tools = new ITool[] {
            MethodToolWrapper.FromDelegate<string?, string?>(CompressAsync),
        };
    }

    /// <inheritdoc/>
    public string Name => "ContextCompression";

    /// <inheritdoc/>
    public string Description =>
        "上下文压缩与 Token 用量监视。" +
        "提供 ctx_compress 工具：当上下文 token 占比较高时主动调用，以带任务焦点提示的方式压缩最旧的历史。";

    /// <inheritdoc/>
    public IReadOnlyList<ITool> Tools { get; }

    private readonly string _summarizePrompt;

    // ─────────────────────────────────────────
    // RenderWindow
    // ─────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// <para>输出策略：<b>事件驱动，而非每轮常驻</b>。安全态（&lt;60%）返回 <c>null</c> 零开销；
    /// 仅当接近或超过软上限时才展开详情与可抄改的 <c>ctx_compress</c> 调用模板。</para>
    /// <para>阈值依据：<c>SoftContextTokenCap</c> 远低于模型物理窗口（如 128K vs 256K+），
    /// 60% 是为后续多轮常规对话与工具调用预留充足余量；90% 表示余量已极度紧张。</para>
    /// <para>直接读者是 LLM Agent 自身——不输出 ascii 字符画或 TUI 装饰。</para>
    /// </remarks>
    public string? RenderWindow() {
        var tokens = _engine.EstimateCurrentContextTokens();
        var profile = _profileAccessor();
        var cap = profile?.SoftContextTokenCap;

        // ── 无软上限：极简报（Agent 无法据此决策压缩，但绝对数值仍有参考价值）──
        if (cap is null or 0) { return BuildNoCapWindow(tokens, profile?.Name); }

        var capValue = cap.Value;
        var percentage = (double)tokens / capValue * 100.0;
        var remaining = tokens >= capValue ? 0UL : capValue - tokens;
        var profileName = profile?.Name;

        // ── 已有待处理压缩请求 ──
        if (_engine.HasPendingCompaction) { return BuildPendingWindow(tokens, capValue, percentage, profileName); }

        // ── 安全态：静默 ──
        if (percentage < 60.0) { return null; }

        // ── 接近 / 临界 / 已超：展开详情 + 调用模板 ──
        return BuildActionWindow(tokens, capValue, percentage, remaining, profileName);
    }

    private static string? BuildNoCapWindow(ulong tokens, string? profileName) {
        var sb = new StringBuilder();
        sb.Append("## ContextCompression\n\n");
        sb.Append("Token 估算: ").Append(tokens).Append("（软上限未配置）\n");
        sb.Append("状态: 无软上限，由引擎自动管理\n");
        if (profileName is not null) {
            sb.Append("Profile: ").Append(profileName).Append('\n');
        }
        sb.Append('\n');
        sb.Append("工具: ctx_compress — 当上下文过长时主动调用以压缩最旧历史。\n");
        return sb.ToString();
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

    // ─────────────────────────────────────────
    // Tool: ctx_compress
    // ─────────────────────────────────────────

    /// <summary>
    /// 请求压缩当前上下文最旧的约一半。
    /// </summary>
    /// <remarks>
    /// 压缩在工具调用返回后的下一次 LLM 推理前异步发生，被压缩段替换为一条 Recap 摘要条目。
    /// </remarks>
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
        )] string? keep_hints,
        [ToolParam(
            "希望摘要中主动遗忘或淡化的内容（自然语言描述）。例如：" +
            "\"已废弃的探索分支、已被覆盖的中间假设、冗长的工具原始输出\"。" +
            "留空则使用通用淡化策略（重复试错、已修正的错误推理）。"
        )] string? forget_hints,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();

        if (_engine.HasPendingCompaction) {
            DebugUtil.Info(DebugCategory, "[ctx_compress] Compaction already pending; overwriting with new hints.");
            // 允许覆盖：LLM 的 keep/forget 提示比上一次的更有信息量。
        }

        var keep = string.IsNullOrWhiteSpace(keep_hints)
            ? ContextCompressionPrompts.DefaultKeepHint
            : keep_hints!;

        var forget = string.IsNullOrWhiteSpace(forget_hints)
            ? ContextCompressionPrompts.DefaultForgetHint
            : forget_hints!;

        var systemPrompt = _buildSystemPrompt(keep, forget);

        DebugUtil.Info(DebugCategory, $"[ctx_compress] Requesting compaction. keep='{keep}' forget='{forget}'");

        var ok = _engine.RequestCompaction(systemPrompt, _summarizePrompt);

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

    // ─────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────

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

        // Fail fast：构造阶段校验模板包含两个必需标记。
        if (!template.Contains(KeepToken) || !template.Contains(ForgetToken)) {
            throw new ArgumentException(
                $"自定义 SystemPrompt 模板必须包含 {KeepToken} 和 {ForgetToken} 两个标记。" +
                "请使用 ContextCompressionPrompts.DefaultSystemPromptTemplate 作为参考。",
                nameof(template)
            );
        }

        var captured = template;
        return (keep, forget) => {
            return captured
                .Replace(KeepToken, keep)
                .Replace(ForgetToken, forget);
        };
    }
}
