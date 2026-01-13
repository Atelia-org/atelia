using Markdig.Extensions.Yaml;
using Markdig.Syntax;

namespace Atelia.DesignDsl;

/// <summary>
/// Block 序列分段器。
/// 将 Markdig 解析出的 Block 序列分段为"YAML FrontMatter + 前导内容 + ATX Section 列表"。
/// </summary>
/// <remarks>
/// 这是纯结构分段，不做任何语义判断（不识别 Term/Clause）。
/// 只按 ATX Heading 分段，Setext Heading 作为普通 Block 处理（归入 Content）。
/// 无论输入什么，都能成功输出，把所有 Block 划分到某个输出部分。
/// </remarks>
public static class AtxSectionSplitter {
    /// <summary>
    /// 将 Block 序列分段。
    /// </summary>
    /// <param name="blocks">Markdig 解析出的 Block 序列。</param>
    /// <param name="originalMarkdown">原始 Markdown 字符串（用于提取标题文本）。</param>
    /// <returns>分段结果，包含 FrontMatter、Preface 和 Sections。</returns>
    public static AtxSectionResult Split(IReadOnlyList<Block> blocks, string originalMarkdown) {
        if (blocks.Count == 0) {
            return new AtxSectionResult(
                frontMatter: null,
                preface: [],
                sections: []
            );
        }

        var index = 0;

        // 1. 检查首个 Block 是否是 YamlFrontMatterBlock
        YamlFrontMatterBlock? frontMatter = null;
        if (blocks[0] is YamlFrontMatterBlock yamlBlock) {
            frontMatter = yamlBlock;
            index = 1;
        }

        // 2. 收集 Preface（首个 ATX HeadingBlock 之前的 Blocks）
        var preface = new List<Block>();
        while (index < blocks.Count && !IsAtxHeading(blocks[index])) {
            preface.Add(blocks[index]);
            index++;
        }

        // 3. 收集 ATX Sections
        var sections = new List<AtxSection>();
        while (index < blocks.Count) {
            // 当前 Block 应该是 ATX HeadingBlock（循环条件保证）
            var heading = (HeadingBlock)blocks[index];
            index++;

            // 收集该 Heading 下辖的 Blocks（直到下一个 ATX HeadingBlock 或 EOF）
            var content = new List<Block>();
            while (index < blocks.Count && !IsAtxHeading(blocks[index])) {
                content.Add(blocks[index]);
                index++;
            }

            // 预先提取标题文本（只计算一次，供所有 INodeBuilder 使用）
            var headingText = HeadingTextExtractor.ExtractText(heading, originalMarkdown);
            sections.Add(new AtxSection(heading, content, headingText));
        }

        return new AtxSectionResult(frontMatter, preface, sections);
    }

    /// <summary>
    /// 判断 Block 是否是 ATX Heading（排除 Setext Heading）。
    /// </summary>
    private static bool IsAtxHeading(Block block) =>
        block is HeadingBlock heading && !heading.IsSetext;
}
