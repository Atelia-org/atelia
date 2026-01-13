using FluentAssertions;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Xunit;

namespace Atelia.DesignDsl.Tests;

/// <summary>
/// Task-S-001: AtxSectionSplitter 测试。
/// 验证 Block 序列分段器的纯结构分段行为。
/// </summary>
public class AtxSectionSplitterTests {
    #region Case 1: 空输入

    /// <summary>
    /// Case 1: 空字符串 Markdown 输入。
    /// 验证不抛异常，返回对象不为 null，所有集合为空。
    /// </summary>
    [Fact]
    public void Split_EmptyInput_ReturnsEmptyResult() {
        // Arrange
        var markdown = "";
        var doc = TestHelpers.ParseMarkdown(markdown);

        // Act
        var result = AtxSectionSplitter.Split(doc.ToList(), markdown);

        // Assert
        // [ ] 不抛异常 - 如果执行到这里说明没抛异常
        // [ ] 返回对象不为 null
        result.Should().NotBeNull();

        // [ ] FrontMatter == null 且 Preface.Count == 0 且 Sections.Count == 0
        result.FrontMatter.Should().BeNull();
        result.Preface.Should().BeEmpty();
        result.Sections.Should().BeEmpty();
    }

    #endregion

    #region Case 2: 无 HeadingBlock

    /// <summary>
    /// Case 2: 仅段落 + 列表，无 HeadingBlock。
    /// 验证所有非 YAML blocks 进入 Preface，Sections 为空。
    /// </summary>
    [Fact]
    public void Split_NoHeading_AllBlocksInPreface() {
        // Arrange
        var markdown = """
            这是一个段落。

            - a
            - b
            """;
        var doc = TestHelpers.ParseMarkdown(markdown);

        // Act
        var result = AtxSectionSplitter.Split(doc.ToList(), markdown);

        // Assert
        result.FrontMatter.Should().BeNull();

        // [ ] 所有非 YAML blocks 都进入 Preface
        result.Preface.Should().HaveCount(2);
        result.Preface[0].Should().BeOfType<ParagraphBlock>();
        result.Preface[1].Should().BeOfType<ListBlock>();

        // 验证 ListBlock 包含 2 个 ListItem
        var listBlock = (ListBlock)result.Preface[1];
        listBlock.Should().HaveCount(2);

        // [ ] Sections 为空
        result.Sections.Should().BeEmpty();
    }

    #endregion

    #region Case 3: 仅有 Preface（带 YAML Front-Matter）

    /// <summary>
    /// Case 3: 有 YAML Front-Matter，无 Heading。
    /// 验证 FrontMatter 单独存储，Preface 不包含 YamlFrontMatterBlock。
    /// </summary>
    [Fact]
    public void Split_WithYamlFrontMatter_FrontMatterSeparated() {
        // Arrange
        var markdown = """
            ---
            title: demo
            ---

            preface text
            """;
        var doc = TestHelpers.ParseMarkdown(markdown);

        // Act
        var result = AtxSectionSplitter.Split(doc.ToList(), markdown);

        // Assert
        // [ ] FrontMatter 单独存储
        result.FrontMatter.Should().NotBeNull();
        result.FrontMatter.Should().BeOfType<YamlFrontMatterBlock>();

        // [ ] Preface 不包含 YamlFrontMatterBlock
        result.Preface.Should().HaveCount(1);
        result.Preface[0].Should().BeOfType<ParagraphBlock>();
        result.Preface.Should().NotContain(b => b is YamlFrontMatterBlock);

        // [ ] 无 Heading 时 Sections 为空
        result.Sections.Should().BeEmpty();
    }

    #endregion

    #region Case 4: Preface + 单个 Section

    /// <summary>
    /// Case 4: Preface + 单个 Section。
    /// 验证 Preface 收集到首个 HeadingBlock 之前，Heading 下的 blocks 进入 Section.Content。
    /// </summary>
    [Fact]
    public void Split_PrefaceAndSingleSection_CorrectlySeparated() {
        // Arrange
        var markdown = """
            preface paragraph

            # A

            A-1

            A-2
            """;
        var doc = TestHelpers.ParseMarkdown(markdown);

        // Act
        var result = AtxSectionSplitter.Split(doc.ToList(), markdown);

        // Assert
        result.FrontMatter.Should().BeNull();

        // [ ] Preface 收集到首个 HeadingBlock 之前
        result.Preface.Should().HaveCount(1);
        result.Preface[0].Should().BeOfType<ParagraphBlock>();

        // [ ] 一个 HeadingBlock 对应一个 Section
        result.Sections.Should().HaveCount(1);

        var sectionA = result.Sections[0];
        // [ ] Heading 类型和 Level
        sectionA.Heading.Should().BeOfType<HeadingBlock>();
        sectionA.Heading.Level.Should().Be(1);

        // [ ] Heading 下的 blocks 全部进入对应 Section.Content
        sectionA.Content.Should().HaveCount(2);
        sectionA.Content[0].Should().BeOfType<ParagraphBlock>();
        sectionA.Content[1].Should().BeOfType<ParagraphBlock>();
    }

    #endregion

    #region Case 5: 多个 Section（含"空内容"的 Section）

    /// <summary>
    /// Case 5: 多个 Section，包含空内容的 Section。
    /// 验证相邻 HeadingBlock 之间没有 blocks 时，Content 为空列表。
    /// </summary>
    [Fact]
    public void Split_MultipleSections_EmptyContentBetweenHeadings() {
        // Arrange
        var markdown = """
            # A

            A-1

            ## B

            ### C

            C-1
            """;
        var doc = TestHelpers.ParseMarkdown(markdown);

        // Act
        var result = AtxSectionSplitter.Split(doc.ToList(), markdown);

        // Assert
        result.FrontMatter.Should().BeNull();
        result.Preface.Should().BeEmpty();
        result.Sections.Should().HaveCount(3);

        // Section A: Level=1, Content.Count=1
        var sectionA = result.Sections[0];
        sectionA.Heading.Level.Should().Be(1);
        sectionA.Content.Should().HaveCount(1);
        sectionA.Content[0].Should().BeOfType<ParagraphBlock>();

        // Section B: Level=2, Content.Count=0 (紧跟下一个 HeadingBlock)
        // [ ] 相邻 HeadingBlock 之间没有 blocks 时，对应 Content 为空列表
        var sectionB = result.Sections[1];
        sectionB.Heading.Level.Should().Be(2);
        sectionB.Content.Should().BeEmpty();

        // Section C: Level=3, Content.Count=1
        var sectionC = result.Sections[2];
        sectionC.Heading.Level.Should().Be(3);
        sectionC.Content.Should().HaveCount(1);
        sectionC.Content[0].Should().BeOfType<ParagraphBlock>();

        // [ ] 分段不依赖 heading level（Level 变化不影响"遇到 Heading 即切分"）
        // 验证点：三个不同 level 的 heading 都被正确切分
    }

    #endregion

    #region Case 6: YAML Front-Matter + Preface + 多个 Section

    /// <summary>
    /// Case 6: 完整文档结构。
    /// 验证 FrontMatter 只在首个 block 被识别，Preface 不包含 YAML，每个 HeadingBlock 产生一个 Section。
    /// </summary>
    [Fact]
    public void Split_FullDocument_AllPartsSeparated() {
        // Arrange
        var markdown = """
            ---
            a: 1
            b: 2
            ---

            preface

            # A

            A-1

            # B

            B-1
            """;
        var doc = TestHelpers.ParseMarkdown(markdown);

        // Act
        var result = AtxSectionSplitter.Split(doc.ToList(), markdown);

        // Assert
        // [ ] FrontMatter 必须只在"首个 block"时被识别
        result.FrontMatter.Should().NotBeNull();

        // [ ] Preface 不包含 YAML block
        result.Preface.Should().HaveCount(1);
        result.Preface[0].Should().BeOfType<ParagraphBlock>();
        result.Preface.Should().NotContain(b => b is YamlFrontMatterBlock);

        // [ ] 每个 HeadingBlock 都产生一个 Section
        result.Sections.Should().HaveCount(2);

        var sectionA = result.Sections[0];
        sectionA.Heading.Level.Should().Be(1);
        sectionA.Content.Should().HaveCount(1);

        var sectionB = result.Sections[1];
        sectionB.Heading.Level.Should().Be(1);
        sectionB.Content.Should().HaveCount(1);
    }

    #endregion

    #region Case 7: 无 YAML Front-Matter（首个 block 不是 YAML）

    /// <summary>
    /// Case 7: 文档以非 YAML block 开头。
    /// 验证仅当 blocks[0] 是 YamlFrontMatterBlock 才设置 FrontMatter。
    /// </summary>
    /// <remarks>
    /// 注意：Markdig 会将 "text\n---" 解析为 Setext Heading，
    /// 所以这个测试用更复杂的内容避免触发 Setext 解析。
    /// </remarks>
    [Fact]
    public void Split_NoYamlAtStart_FrontMatterIsNull() {
        // Arrange - 使用不会触发 Setext Heading 的内容（多行文本 + 空行 + ---）
        var markdown = """
            This is some text.

            Another paragraph.

            # A
            """;
        var doc = TestHelpers.ParseMarkdown(markdown);

        // Act
        var result = AtxSectionSplitter.Split(doc.ToList(), markdown);

        // Assert
        // [ ] 仅当 blocks[0] 是 YamlFrontMatterBlock 才设置 FrontMatter
        result.FrontMatter.Should().BeNull();

        // [ ] Preface 包含 HeadingBlock 之前的所有 blocks
        result.Preface.Should().NotBeEmpty();
        result.Preface.Should().HaveCount(2);
        result.Preface[0].Should().BeOfType<ParagraphBlock>();
        result.Preface[1].Should().BeOfType<ParagraphBlock>();

        // [ ] Sections 包含 A
        result.Sections.Should().HaveCount(1);
    }

    #endregion

    #region Case 8: YAML block 不在首位

    /// <summary>
    /// Case 8: YAML block 不在首位（边界情况）。
    /// 验证 Splitter 只识别文档首块 YAML。
    /// </summary>
    /// <remarks>
    /// 注意：Markdig 会将 "a: 1\n---" 解析为 Setext Heading（Level=2）。
    /// 现在 Splitter 只按 ATX Heading 分段，Setext Heading 作为普通 Block 处理。
    /// </remarks>
    [Fact]
    public void Split_YamlNotFirst_TreatedAsRegularBlock() {
        // Arrange - Markdig 会将 "a: 1\n---" 解析为 Setext Heading
        var markdown = """
            preface first

            ---
            a: 1
            ---

            # A
            """;
        var doc = TestHelpers.ParseMarkdown(markdown);

        // Act
        var result = AtxSectionSplitter.Split(doc.ToList(), markdown);

        // Assert
        // [ ] Splitter 当前实现只识别文档首块 YAML
        result.FrontMatter.Should().BeNull();

        // [ ] Preface 包含 "preface first" 和 Setext Heading（作为普通 Block）
        result.Preface.Should().NotBeEmpty();
        result.Preface[0].Should().BeOfType<ParagraphBlock>();
        // Setext Heading 被当作普通 Block 归入 Preface
        result.Preface.OfType<HeadingBlock>().Should().Contain(hb => hb.IsSetext);

        // [ ] Sections 只包含 ATX Heading "# A"
        result.Sections.Should().HaveCount(1);
        var sectionA = result.Sections[0];
        sectionA.Heading.Level.Should().Be(1);
        sectionA.Heading.IsSetext.Should().BeFalse();

        // [ ] 即使出现类似 YAML 的内容，但不在 blocks[0]，也不会进入 FrontMatter 字段
        // 此验证点已通过 FrontMatter.Should().BeNull() 覆盖
    }

    #endregion

    #region Case 9: Setext Heading 作为普通 Block 处理

    /// <summary>
    /// Case 9: Setext Heading 不触发分段。
    /// 验证只有 ATX Heading 才触发分段，Setext Heading 归入当前 Section 的 Content。
    /// </summary>
    [Fact]
    public void Split_SetextHeading_TreatedAsContent() {
        // Arrange
        var markdown = """
            # ATX Heading

            Setext Title
            ------------

            paragraph after setext
            """;
        var doc = TestHelpers.ParseMarkdown(markdown);

        // Act
        var result = AtxSectionSplitter.Split(doc.ToList(), markdown);

        // Assert
        result.FrontMatter.Should().BeNull();
        result.Preface.Should().BeEmpty();

        // [ ] 只有一个 Section（ATX Heading），Setext Heading 归入 Content
        result.Sections.Should().HaveCount(1);

        var section = result.Sections[0];
        section.Heading.Level.Should().Be(1);
        section.Heading.IsSetext.Should().BeFalse();

        // [ ] Content 包含 Setext Heading 和后续段落
        section.Content.Should().HaveCount(2);
        section.Content[0].Should().BeOfType<HeadingBlock>();
        ((HeadingBlock)section.Content[0]).IsSetext.Should().BeTrue();
        section.Content[1].Should().BeOfType<ParagraphBlock>();
    }

    #endregion
}
