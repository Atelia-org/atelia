using FluentAssertions;
using Xunit;

namespace Atelia.DesignDsl.Tests;

/// <summary>
/// Task-S-006: EndToEnd 测试。
/// 使用 dsl-sample.md 测试 fixture 验证完整的 DSL 解析流程。
/// </summary>
public class EndToEndTests {
    private static readonly string TestDataPath = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "test-data", "DesignDsl", "dsl-sample.md"
    );

    /// <summary>
    /// 验证测试 fixture 文件存在。
    /// </summary>
    [Fact]
    public void TestFixture_FileExists() {
        // Assert
        File.Exists(TestDataPath).Should().BeTrue($"测试 fixture 文件应存在于 {TestDataPath}");
    }

    /// <summary>
    /// 验证完整解析流程：Term 节点存在。
    /// </summary>
    [Fact]
    public void Parse_DslSample_ContainsTermNodes() {
        // Arrange
        var markdown = File.ReadAllText(TestDataPath);
        var doc = TestHelpers.ParseMarkdown(markdown);
        var sections = AtxSectionSplitter.Split(doc.ToList(), markdown);

        var pipeline = new NodeBuilderPipeline();
        pipeline.InsertBefore(new TermNodeBuilder());
        pipeline.InsertBefore(new ClauseNodeBuilder());

        // Act
        var tree = AxtTreeBuilder.Build(sections, pipeline);

        // Assert - 关键节点存在
        var termNodes = tree.AllNodes.OfType<TermNode>().ToList();
        termNodes.Should().NotBeEmpty("应包含至少一个 TermNode");

        // 验证特定 Term 存在
        termNodes.Should().Contain(t => t.TermId == "Document-Root", "应包含 Document-Root term");
        termNodes.Should().Contain(t => t.TermId == "Child-Concept", "应包含 Child-Concept term");
        termNodes.Should().Contain(t => t.TermId == "Another-Root", "应包含 Another-Root term");
    }

    /// <summary>
    /// 验证完整解析流程：三种 ClauseModifier 各有一个。
    /// </summary>
    [Fact]
    public void Parse_DslSample_ContainsAllClauseModifiers() {
        // Arrange
        var markdown = File.ReadAllText(TestDataPath);
        var doc = TestHelpers.ParseMarkdown(markdown);
        var sections = AtxSectionSplitter.Split(doc.ToList(), markdown);

        var pipeline = new NodeBuilderPipeline();
        pipeline.InsertBefore(new TermNodeBuilder());
        pipeline.InsertBefore(new ClauseNodeBuilder());

        // Act
        var tree = AxtTreeBuilder.Build(sections, pipeline);

        // Assert - 三种 Modifier 各至少一个
        var clauseNodes = tree.AllNodes.OfType<ClauseNode>().ToList();

        clauseNodes.Should().Contain(c => c.Modifier == ClauseModifier.Decision,
            "应包含至少一个 decision clause"
        );
        clauseNodes.Should().Contain(c => c.Modifier == ClauseModifier.Spec,
            "应包含至少一个 spec clause"
        );
        clauseNodes.Should().Contain(c => c.Modifier == ClauseModifier.Derived,
            "应包含至少一个 derived clause"
        );
    }

    /// <summary>
    /// 验证树结构：父子关系正确。
    /// </summary>
    [Fact]
    public void Parse_DslSample_TreeStructureCorrect() {
        // Arrange
        var markdown = File.ReadAllText(TestDataPath);
        var doc = TestHelpers.ParseMarkdown(markdown);
        var sections = AtxSectionSplitter.Split(doc.ToList(), markdown);

        var pipeline = new NodeBuilderPipeline();
        pipeline.InsertBefore(new TermNodeBuilder());
        pipeline.InsertBefore(new ClauseNodeBuilder());

        // Act
        var tree = AxtTreeBuilder.Build(sections, pipeline);

        // Assert - 父子关系
        // Document-Root (Level 1) 应该是 RootNode 的子节点
        var documentRoot = tree.AllNodes.OfType<TermNode>().First(t => t.TermId == "Document-Root");
        documentRoot.Parent.Should().Be(tree.Root, "Document-Root 应该是 RootNode 的直接子节点");

        // ROOT-REQUIREMENT (Level 2 decision) 应该是 Document-Root 的子节点
        var rootRequirement = tree.AllNodes.OfType<ClauseNode>().First(c => c.ClauseId == "ROOT-REQUIREMENT");
        rootRequirement.Parent.Should().Be(documentRoot, "ROOT-REQUIREMENT 应该是 Document-Root 的子节点");

        // ROOT-FORMAT (Level 3 spec) 应该是 ROOT-REQUIREMENT 的子节点
        var rootFormat = tree.AllNodes.OfType<ClauseNode>().First(c => c.ClauseId == "ROOT-FORMAT");
        rootFormat.Parent.Should().Be(rootRequirement, "ROOT-FORMAT 应该是 ROOT-REQUIREMENT 的子节点");

        // ROOT-EXAMPLE (Level 4 derived) 应该是 ROOT-FORMAT 的子节点
        var rootExample = tree.AllNodes.OfType<ClauseNode>().First(c => c.ClauseId == "ROOT-EXAMPLE");
        rootExample.Parent.Should().Be(rootFormat, "ROOT-EXAMPLE 应该是 ROOT-FORMAT 的子节点");
    }

    /// <summary>
    /// 验证不同 Heading Level 的 Term/Clause 被正确识别。
    /// </summary>
    [Fact]
    public void Parse_DslSample_DifferentLevelsRecognized() {
        // Arrange
        var markdown = File.ReadAllText(TestDataPath);
        var doc = TestHelpers.ParseMarkdown(markdown);
        var sections = AtxSectionSplitter.Split(doc.ToList(), markdown);

        var pipeline = new NodeBuilderPipeline();
        pipeline.InsertBefore(new TermNodeBuilder());
        pipeline.InsertBefore(new ClauseNodeBuilder());

        // Act
        var tree = AxtTreeBuilder.Build(sections, pipeline);

        // Assert - 不同 Level 的节点
        var termNodes = tree.AllNodes.OfType<TermNode>().ToList();
        var clauseNodes = tree.AllNodes.OfType<ClauseNode>().ToList();

        // Level 1 Term
        termNodes.Should().Contain(t => t.Depth == 1, "应包含 Level 1 的 Term");
        // Level 2 Term 和 Clause
        termNodes.Should().Contain(t => t.Depth == 2, "应包含 Level 2 的 Term");
        clauseNodes.Should().Contain(c => c.Depth == 2, "应包含 Level 2 的 Clause");
        // Level 3 Clause
        clauseNodes.Should().Contain(c => c.Depth == 3, "应包含 Level 3 的 Clause");
        // Level 4 Clause
        clauseNodes.Should().Contain(c => c.Depth == 4, "应包含 Level 4 的 Clause");
    }

    /// <summary>
    /// 验证 RootNode 包含 Preface 内容。
    /// </summary>
    [Fact]
    public void Parse_DslSample_RootContainsPreface() {
        // Arrange
        var markdown = File.ReadAllText(TestDataPath);
        var doc = TestHelpers.ParseMarkdown(markdown);
        var sections = AtxSectionSplitter.Split(doc.ToList(), markdown);

        var pipeline = new NodeBuilderPipeline();
        pipeline.InsertBefore(new TermNodeBuilder());
        pipeline.InsertBefore(new ClauseNodeBuilder());

        // Act
        var tree = AxtTreeBuilder.Build(sections, pipeline);

        // Assert
        tree.Root.Should().NotBeNull();
        tree.Root.Content.Should().NotBeEmpty("RootNode 应包含 Preface 内容");
    }

    /// <summary>
    /// 验证无标题的 Term 和 Clause 节点。
    /// </summary>
    [Fact]
    public void Parse_DslSample_NodesWithoutTitle() {
        // Arrange
        var markdown = File.ReadAllText(TestDataPath);
        var doc = TestHelpers.ParseMarkdown(markdown);
        var sections = AtxSectionSplitter.Split(doc.ToList(), markdown);

        var pipeline = new NodeBuilderPipeline();
        pipeline.InsertBefore(new TermNodeBuilder());
        pipeline.InsertBefore(new ClauseNodeBuilder());

        // Act
        var tree = AxtTreeBuilder.Build(sections, pipeline);

        // Assert - 无标题的节点
        var anotherRoot = tree.AllNodes.OfType<TermNode>().First(t => t.TermId == "Another-Root");
        anotherRoot.Title.Should().BeNull("Another-Root 没有标题");

        var simpleClause = tree.AllNodes.OfType<ClauseNode>().First(c => c.ClauseId == "SIMPLE");
        simpleClause.Title.Should().BeNull("SIMPLE clause 没有标题");
    }
}
