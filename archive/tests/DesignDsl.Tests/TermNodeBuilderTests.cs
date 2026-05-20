using FluentAssertions;
using Xunit;

namespace Atelia.DesignDsl.Tests;

/// <summary>
/// Task-S-004: TermNodeBuilder 测试。
/// 验证 Term 节点识别的正例和反例。
/// </summary>
public class TermNodeBuilderTests {
    private readonly TermNodeBuilder _builder = new();

    #region 正例：标准格式

    /// <summary>
    /// 正例 1: 标准格式 `## term `User-Account` 用户账号`。
    /// 验证完整的 Term 模式匹配。
    /// </summary>
    [Fact]
    public void TryBuild_StandardFormat_ReturnsTermNode() {
        // Arrange
        var markdown = "## term `User-Account` 用户账号";
        var doc = TestHelpers.ParseMarkdown(markdown);
        var result = AtxSectionSplitter.Split(doc.ToList(), markdown);
        var section = result.Sections[0];

        // Act
        var node = _builder.TryBuild(section);

        // Assert
        node.Should().NotBeNull();
        node.Should().BeOfType<TermNode>();

        var termNode = (TermNode)node!;
        termNode.TermId.Should().Be("User-Account");
        termNode.Title.Should().Be("用户账号");
        termNode.Depth.Should().Be(2);
    }

    #endregion

    #region 正例：仅 ID 无标题

    /// <summary>
    /// 正例 2: 仅 ID 无标题 `## term `Simple``。
    /// 验证 Title 为 null。
    /// </summary>
    [Fact]
    public void TryBuild_IdOnly_ReturnsTitleNull() {
        // Arrange
        var markdown = "## term `Simple`";
        var doc = TestHelpers.ParseMarkdown(markdown);
        var result = AtxSectionSplitter.Split(doc.ToList(), markdown);
        var section = result.Sections[0];

        // Act
        var node = _builder.TryBuild(section);

        // Assert
        node.Should().NotBeNull();
        var termNode = (TermNode)node!;
        termNode.TermId.Should().Be("Simple");
        termNode.Title.Should().BeNull();
    }

    #endregion

    #region 正例：大小写变体

    /// <summary>
    /// 正例 3a: 小写 `term`。
    /// </summary>
    [Fact]
    public void TryBuild_LowercaseTerm_Matches() {
        // Arrange
        var markdown = "## term `ID` title";
        var doc = TestHelpers.ParseMarkdown(markdown);
        var result = AtxSectionSplitter.Split(doc.ToList(), markdown);
        var section = result.Sections[0];

        // Act
        var node = _builder.TryBuild(section);

        // Assert
        node.Should().NotBeNull();
    }

    /// <summary>
    /// 正例 3b: 首字母大写 `Term`。
    /// </summary>
    [Fact]
    public void TryBuild_CapitalizedTerm_Matches() {
        // Arrange
        var markdown = "## Term `ID` title";
        var doc = TestHelpers.ParseMarkdown(markdown);
        var result = AtxSectionSplitter.Split(doc.ToList(), markdown);
        var section = result.Sections[0];

        // Act
        var node = _builder.TryBuild(section);

        // Assert
        node.Should().NotBeNull();
    }

    /// <summary>
    /// 正例 3c: 全大写 `TERM`。
    /// </summary>
    [Fact]
    public void TryBuild_UppercaseTerm_Matches() {
        // Arrange
        var markdown = "## TERM `ID` title";
        var doc = TestHelpers.ParseMarkdown(markdown);
        var result = AtxSectionSplitter.Split(doc.ToList(), markdown);
        var section = result.Sections[0];

        // Act
        var node = _builder.TryBuild(section);

        // Assert
        node.Should().NotBeNull();
    }

    #endregion

    #region 正例：不同 Level

    /// <summary>
    /// 正例 4: 不同 Heading Level 都能识别。
    /// </summary>
    [Theory]
    [InlineData("# term `L1` Title", 1)]
    [InlineData("## term `L2` Title", 2)]
    [InlineData("### term `L3` Title", 3)]
    [InlineData("#### term `L4` Title", 4)]
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

    #region 反例：缺少关键字

    /// <summary>
    /// 反例 1: 缺少关键字 `## `NotATerm``。
    /// </summary>
    [Fact]
    public void TryBuild_MissingKeyword_ReturnsNull() {
        // Arrange
        var markdown = "## `NotATerm`";
        var doc = TestHelpers.ParseMarkdown(markdown);
        var result = AtxSectionSplitter.Split(doc.ToList(), markdown);
        var section = result.Sections[0];

        // Act
        var node = _builder.TryBuild(section);

        // Assert
        node.Should().BeNull();
    }

    #endregion

    #region 反例：ID 无反引号

    /// <summary>
    /// 反例 2: ID 无反引号 `## term NotATerm`。
    /// </summary>
    [Fact]
    public void TryBuild_IdWithoutBackticks_ReturnsNull() {
        // Arrange
        var markdown = "## term NotATerm";
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
    /// 反例 3: ID 格式非法（含空格）`## term `Invalid ID``。
    /// </summary>
    [Fact]
    public void TryBuild_IdWithSpace_ReturnsNull() {
        // Arrange
        var markdown = "## term `Invalid ID`";
        var doc = TestHelpers.ParseMarkdown(markdown);
        var result = AtxSectionSplitter.Split(doc.ToList(), markdown);
        var section = result.Sections[0];

        // Act
        var node = _builder.TryBuild(section);

        // Assert
        node.Should().BeNull();
    }

    #endregion

    #region 反例：关键字与 ID 无空白

    /// <summary>
    /// 反例 4: 关键字与 ID 无空白 `## term`NoSpace``。
    /// </summary>
    [Fact]
    public void TryBuild_NoSpaceBetweenKeywordAndId_ReturnsNull() {
        // Arrange
        var markdown = "## term`NoSpace`";
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
