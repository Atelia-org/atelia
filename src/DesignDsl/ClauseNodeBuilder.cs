using System.Text.RegularExpressions;

namespace Atelia.DesignDsl;

/// <summary>
/// Clause 节点构建器。
/// 识别 `<modifier> [Clause-ID] 可选标题` 模式，返回 <see cref="ClauseNode"/>。
/// </summary>
public sealed partial class ClauseNodeBuilder : INodeBuilder {
    // Clause 模式：<modifier> [Clause-ID] 可选标题
    // - Modifier: decision | spec | derived（大小写不敏感）
    // - Modifier 与方括号之间必须有空白
    // - 允许尾随空白
    [GeneratedRegex(@"^\s*(decision|spec|derived)\s+\[([^\]]+)\](?:\s+(.+?))?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ClausePattern();

    // Identifier 格式：字母数字，可用 - 连接多个段
    [GeneratedRegex(@"^[a-zA-Z0-9]+(-[a-zA-Z0-9]+)*$")]
    private static partial Regex IdentifierPattern();

    /// <summary>
    /// 尝试构建 Clause 节点。
    /// </summary>
    /// <param name="section">ATX Section，包含 Heading、Content 和 HeadingText。</param>
    /// <returns>匹配 Clause 模式返回 <see cref="ClauseNode"/>，否则返回 null。</returns>
    public AxtNode? TryBuild(AtxSection section) {
        var match = ClausePattern().Match(section.HeadingText);
        if (!match.Success) { return null; }

        // 解析 Modifier（忽略大小写）
        var modifierStr = match.Groups[1].Value;
        if (!Enum.TryParse<ClauseModifier>(modifierStr, ignoreCase: true, out var modifier)) {
            return null;
        }

        var clauseId = match.Groups[2].Value;

        // Clause-ID 必须符合 Identifier 格式
        if (!IdentifierPattern().IsMatch(clauseId)) { return null; }

        // Title 提取后 Trim，空字符串转为 null
        string? title = null;
        if (match.Groups[3].Success) {
            var trimmed = match.Groups[3].Value.Trim();
            if (trimmed.Length > 0) { title = trimmed; }
        }

        return new ClauseNode(section.Heading, section.Content, modifier, clauseId, title);
    }
}
