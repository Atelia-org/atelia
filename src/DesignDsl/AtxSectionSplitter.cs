using Markdig.Extensions.Yaml;
using Markdig.Syntax;

namespace Atelia.DesignDsl;

/// <summary>
/// Block 序列分段器。
/// 将 Markdig 解析出的 Block 序列分段为"YAML FrontMatter + 前导内容 + ATX Section 列表"。
/// </summary>
/// <remarks>
/// 这是纯结构分段，不做任何语义判断（不识别 Term/Clause）。
/// 无论输入什么，都能成功输出，把所有 Block 划分到某个输出部分。
/// </remarks>
public static class AtxSectionSplitter {
    /// <summary>
    /// 将 Block 序列分段。
    /// </summary>
    /// <param name="blocks">Markdig 解析出的 Block 序列。</param>
    /// <returns>分段结果，包含 FrontMatter、Preface 和 Sections。</returns>
    public static AtxSectionResult Split(IReadOnlyList<Block> blocks) {
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

        // 2. 收集 Preface（首个 HeadingBlock 之前的 Blocks）
        var preface = new List<Block>();
        while (index < blocks.Count && blocks[index] is not HeadingBlock) {
            preface.Add(blocks[index]);
            index++;
        }

        // 3. 收集 ATX Sections
        var sections = new List<AtxSection>();
        while (index < blocks.Count) {
            // 当前 Block 应该是 HeadingBlock（循环条件保证）
            var heading = (HeadingBlock)blocks[index];
            index++;

            // 收集该 Heading 下辖的 Blocks（直到下一个 HeadingBlock 或 EOF）
            var content = new List<Block>();
            while (index < blocks.Count && blocks[index] is not HeadingBlock) {
                content.Add(blocks[index]);
                index++;
            }

            sections.Add(new AtxSection(heading, content));
        }

        return new AtxSectionResult(frontMatter, preface, sections);
    }
}
