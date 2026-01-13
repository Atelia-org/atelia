using FluentAssertions;
using Xunit;

namespace Atelia.DesignDsl.Tests;

/// <summary>
/// Task-S-005: ClauseNodeBuilder 测试。
/// 验证 Clause 节点识别的正例和反例。
/// </summary>
public class ClauseNodeBuilderTests {
    private readonly ClauseNodeBuilder _builder = new();

    #region 正例：三种 Modifier

    /// <summary>
    /// 正例 1a: decision 修饰符。
    /// </summary>
    [Fact]
    public void TryBuild_DecisionModifier_ReturnsClauseNode() {
        // Arrange
        var markdown = "### decision [ACCOUNT-SECURITY] 账号安全";
        var doc = TestHelpers.ParseMarkdown(markdown);
        var result = AtxSectionSplitter.Split(doc.ToList(), markdown);
        var section = result.Sections[0];

        // Act
        var node = _builder.TryBuild(section);

        // Assert
        node.Should().NotBeNull();
        node.Should().BeOfType<ClauseNode>();

        var clauseNode = (ClauseNode)node!;
        clauseNode.Modifier.Should().Be(ClauseModifier.Decision);
        clauseNode.ClauseId.Should().Be("ACCOUNT-SECURITY");
        clauseNode.Title.Should().Be("账号安全");
    }

    /// <summary>
    /// 正例 1b: spec 修饰符。
    /// </summary>
    [Fact]
    public void TryBuild_SpecModifier_ReturnsClauseNode() {
        // Arrange
        var markdown = "### spec [PWD-COMPLEXITY] 密码复杂度";
        var doc = TestHelpers.ParseMarkdown(markdown);
        var result = AtxSectionSplitter.Split(doc.ToList(), markdown);
        var section = result.Sections[0];

        // Act
        var node = _builder.TryBuild(section);

        // Assert
        node.Should().NotBeNull();
        var clauseNode = (ClauseNode)node!;
        clauseNode.Modifier.Should().Be(ClauseModifier.Spec);
        clauseNode.ClauseId.Should().Be("PWD-COMPLEXITY");
    }

    /// <summary>
    /// 正例 1c: derived 修饰符。
    /// </summary>
    [Fact]
    public void TryBuild_DerivedModifier_ReturnsClauseNode() {
        // Arrange
        var markdown = "### derived [EXAMPLE-USAGE] 使用示例";
        var doc = TestHelpers.ParseMarkdown(markdown);
        var result = AtxSectionSplitter.Split(doc.ToList(), markdown);
        var section = result.Sections[0];

        // Act
        var node = _builder.TryBuild(section);

        // Assert
        node.Should().NotBeNull();
        var clauseNode = (ClauseNode)node!;
        clauseNode.Modifier.Should().Be(ClauseModifier.Derived);
        clauseNode.ClauseId.Should().Be("EXAMPLE-USAGE");
    }

    #endregion

    #region 正例：仅 ID 无标题

    /// <summary>
    /// 正例 2: 仅 ID 无标题 `### spec [SIMPLE]`。
    /// 验证 Title 为 null。
    /// </summary>
    [Fact]
    public void TryBuild_IdOnly_ReturnsTitleNull() {
        // Arrange
        var markdown = "### spec [SIMPLE]";
        var doc = TestHelpers.ParseMarkdown(markdown);
        var result = AtxSectionSplitter.Split(doc.ToList(), markdown);
        var section = result.Sections[0];

        // Act
        var node = _builder.TryBuild(section);

        // Assert
        node.Should().NotBeNull();
        var clauseNode = (ClauseNode)node!;
        clauseNode.ClauseId.Should().Be("SIMPLE");
        clauseNode.Title.Should().BeNull();
    }

    #endregion

    #region 正例：大小写变体

    /// <summary>
    /// 正例 3a: 全大写 DECISION。
    /// </summary>
    [Fact]
    public void TryBuild_UppercaseDecision_Matches() {
        // Arrange
        var markdown = "### DECISION [ID] title";
        var doc = TestHelpers.ParseMarkdown(markdown);
        var result = AtxSectionSplitter.Split(doc.ToList(), markdown);
        var section = result.Sections[0];

        // Act
        var node = _builder.TryBuild(section);

        // Assert
        node.Should().NotBeNull();
        ((ClauseNode)node!).Modifier.Should().Be(ClauseModifier.Decision);
    }

    /// <summary>
    /// 正例 3b: 首字母大写 Spec。
    /// </summary>
    [Fact]
    public void TryBuild_CapitalizedSpec_Matches() {
        // Arrange
        var markdown = "### Spec [ID] title";
        var doc = TestHelpers.ParseMarkdown(markdown);
        var result = AtxSectionSplitter.Split(doc.ToList(), markdown);
        var section = result.Sections[0];

        // Act
        var node = _builder.TryBuild(section);

        // Assert
        node.Should().NotBeNull();
        ((ClauseNode)node!).Modifier.Should().Be(ClauseModifier.Spec);
    }

    /// <summary>
    /// 正例 3c: 小写 derived。
    /// </summary>
    [Fact]
    public void TryBuild_LowercaseDerived_Matches() {
        // Arrange
        var markdown = "### derived [ID] title";
        var doc = TestHelpers.ParseMarkdown(markdown);
        var result = AtxSectionSplitter.Split(doc.ToList(), markdown);
        var section = result.Sections[0];

        // Act
        var node = _builder.TryBuild(section);

        // Assert
        node.Should().NotBeNull();
        ((ClauseNode)node!).Modifier.Should().Be(ClauseModifier.Derived);
    }

    #endregion

    #region 正例：不同 Level

    /// <summary>
    /// 正例 4: 不同 Heading Level 都能识别。
    /// </summary>
    [Theory]
    [InlineData("## decision [L2] Title", 2)]
    [InlineData("### spec [L3] Title", 3)]
    [InlineData("#### derived [L4] Title", 4)]
    public void TryBuild_DifferentLevels_AllRecognized(string markdown, int expectedLevel) {
        // Arrange
        var doc = TestHelpers.ParseMarkdown(markdown);
        var result = AtxSectionSplitter.Split(doc.ToList(), markdown);
        var section = result.Sections[0];

        // Act
        var node = _builder.TryBuild(section);

        // Assert
        node.Should().NotBeNull();
        node!.Depth.Should().Be(expectedLevel);
    }

    #endregion

    #region 反例：未知 Modifier

    /// <summary>
    /// 反例 1: 未知 Modifier `### unknown [ID]`。
    /// </summary>
    [Fact]
    public void TryBuild_UnknownModifier_ReturnsNull() {
        // Arrange
        var markdown = "### unknown [ID]";
        var doc = TestHelpers.ParseMarkdown(markdown);
        var result = AtxSectionSplitter.Split(doc.ToList(), markdown);
        var section = result.Sections[0];

        // Act
        var node = _builder.TryBuild(section);

        // Assert
        node.Should().BeNull();
    }

    #endregion

    #region 反例：ID 无方括号

    /// <summary>
    /// 反例 2: ID 无方括号 `### spec ID-WITHOUT-BRACKETS`。
    /// </summary>
    [Fact]
    public void TryBuild_IdWithoutBrackets_ReturnsNull() {
        // Arrange
        var markdown = "### spec ID-WITHOUT-BRACKETS";
        var doc = TestHelpers.ParseMarkdown(markdown);
        var result = AtxSectionSplitter.Split(doc.ToList(), markdown);
        var section = result.Sections[0];

        // Act
        var node = _builder.TryBuild(section);

        // Assert
        node.Should().BeNull();
    }

    #endregion

    #region 反例：ID 格式非法

    /// <summary>
    /// 反例 3: ID 格式非法（含空格）`### spec [Invalid ID]`。
    /// </summary>
    [Fact]
    public void TryBuild_IdWithSpace_ReturnsNull() {
        // Arrange
        var markdown = "### spec [Invalid ID]";
        var doc = TestHelpers.ParseMarkdown(markdown);
        var result = AtxSectionSplitter.Split(doc.ToList(), markdown);
        var section = result.Sections[0];

        // Act
        var node = _builder.TryBuild(section);

        // Assert
        node.Should().BeNull();
    }

    #endregion

    #region 反例：Modifier 与方括号无空白

    /// <summary>
    /// 反例 4: Modifier 与方括号无空白 `### spec[NoSpace]`。
    /// </summary>
    [Fact]
    public void TryBuild_NoSpaceBetweenModifierAndBracket_ReturnsNull() {
        // Arrange
        var markdown = "### spec[NoSpace]";
        var doc = TestHelpers.ParseMarkdown(markdown);
        var result = AtxSectionSplitter.Split(doc.ToList(), markdown);
        var section = result.Sections[0];

        // Act
        var node = _builder.TryBuild(section);

        // Assert
        node.Should().BeNull();
    }

    #endregion
}
