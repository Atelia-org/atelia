using System.Text.RegularExpressions;

namespace Atelia.DesignDsl;

/// <summary>
/// Term 节点构建器。
/// 识别 `term `Term-ID` 可选标题` 模式，返回 <see cref="TermNode"/>。
/// </summary>
public sealed partial class TermNodeBuilder : INodeBuilder {
    // Term 模式：term `Term-ID` 可选标题
    // - 关键字 term 大小写不敏感
    // - 关键字与反引号之间必须有空白
    // - 允许尾随空白
    [GeneratedRegex(@"^\s*term\s+`([^`]+)`(?:\s+(.+?))?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TermPattern();

    // Identifier 格式：字母数字，可用 - 连接多个段
    [GeneratedRegex(@"^[a-zA-Z0-9]+(-[a-zA-Z0-9]+)*$")]
    private static partial Regex IdentifierPattern();

    /// <summary>
    /// 尝试构建 Term 节点。
    /// </summary>
    /// <param name="section">ATX Section，包含 Heading、Content 和 HeadingText。</param>
    /// <returns>匹配 Term 模式返回 <see cref="TermNode"/>，否则返回 null。</returns>
    public AxtNode? TryBuild(AtxSection section) {
        var match = TermPattern().Match(section.HeadingText);
        if (!match.Success) { return null; }

        var termId = match.Groups[1].Value;

        // Term-ID 必须符合 Identifier 格式
        if (!IdentifierPattern().IsMatch(termId)) { return null; }

        // Title 提取后 Trim，空字符串转为 null
        string? title = null;
        if (match.Groups[2].Success) {
            var trimmed = match.Groups[2].Value.Trim();
            if (trimmed.Length > 0) { title = trimmed; }
        }

        return new TermNode(section.Heading, section.Content, termId, title);
    }
}
