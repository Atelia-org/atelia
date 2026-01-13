using Markdig.Extensions.Yaml;
using Markdig.Syntax;

namespace Atelia.DesignDsl;

/// <summary>
/// Block 序列分段结果。
/// 将 Markdown 文档的 Block 序列分为三部分：YAML Front Matter、前导内容（Preface）、ATX Section 列表。
/// </summary>
public sealed class AtxSectionResult {
    /// <summary>
    /// YAML Front Matter（如果文档首个 Block 是 YamlFrontMatterBlock）。
    /// </summary>
    public YamlFrontMatterBlock? FrontMatter { get; }

    /// <summary>
    /// FrontMatter 之后、首个 ATX Heading 之前的 Blocks（用于 RootNode.Content）。
    /// </summary>
    public IReadOnlyList<Block> Preface { get; }

    /// <summary>
    /// ATX Heading 及其下辖 Blocks 的列表。
    /// </summary>
    public IReadOnlyList<AtxSection> Sections { get; }

    /// <summary>
    /// 创建分段结果。
    /// </summary>
    /// <param name="frontMatter">YAML Front Matter（可选）。</param>
    /// <param name="preface">前导内容。</param>
    /// <param name="sections">ATX Section 列表。</param>
    public AtxSectionResult(
        YamlFrontMatterBlock? frontMatter,
        IReadOnlyList<Block> preface,
        IReadOnlyList<AtxSection> sections
    ) {
        FrontMatter = frontMatter;
        Preface = preface;
        Sections = sections;
    }
}
