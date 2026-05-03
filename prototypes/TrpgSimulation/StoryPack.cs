namespace Atelia.DebugApps.TrpgSimulation;

// ═════════════════════════════════════════
// StoryPack：可切换的成套提示词配置
// ═════════════════════════════════════════
internal sealed record StoryPack(
    string Name,
    string Description,
    string GmSystemPrompt,
    string PlayerSystemPrompt,
    string InitialObservation
) {
    /// <summary>
    /// GM 输出传递给 Player 时的包装模板。
    /// 占位符：{time}（时间标签）、{content}（GM 原文）。
    /// 用 .Replace() 展开，避免 string.Format 与花括号冲突。
    /// </summary>
    public string GmToPlayerEnvelope { get; init; } =
        "[系统 · {time}]\n{content}";

    /// <summary>
    /// Player 输出传递给 GM 时的包装模板。
    /// 占位符：{time}（时间标签）、{content}（Player 原文）。
    /// </summary>
    public string PlayerToGmEnvelope { get; init; } =
        "[系统 · {time}]\n{content}";
}

static partial class StoryPacks {

}
