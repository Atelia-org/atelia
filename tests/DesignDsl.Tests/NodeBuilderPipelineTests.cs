using FluentAssertions;
using Markdig.Syntax;
using Xunit;

namespace Atelia.DesignDsl.Tests;

/// <summary>
/// Task-S-002: NodeBuilderPipeline 测试。
/// 验证职责链调度器的行为：默认兜底、顺序优先、InsertBefore 语义。
/// </summary>
public class NodeBuilderPipelineTests {
    #region Case 1: DefaultNodeBuilder 兜底

    /// <summary>
    /// Case 1: 默认构造的 Pipeline 使用 DefaultNodeBuilder 兜底。
    /// 验证默认 pipeline 必须始终能构建出节点。
    /// </summary>
    [Fact]
    public void DefaultPipeline_ContainsOnlyDefaultBuilder() {
        // Arrange & Act
        var pipeline = new NodeBuilderPipeline();

        // Assert
        // [ ] Builders.Count == 1
        pipeline.Builders.Should().HaveCount(1);

        // [ ] Builders[0] 类型为 DefaultNodeBuilder
        pipeline.Builders[0].Should().BeOfType<DefaultNodeBuilder>();
    }

    /// <summary>
    /// Case 1: 默认 Pipeline 能构建出 AxtNode。
    /// 验证未注册任何其它 builder 时，必然走 DefaultNodeBuilder。
    /// </summary>
    [Fact]
    public void DefaultPipeline_BuildReturnsNode() {
        // Arrange
        var markdown = """
            # A

            content
            """;
        var doc = TestHelpers.ParseMarkdown(markdown);
        var heading = (HeadingBlock)doc[0];
        var content = doc.Skip(1).ToList();

        var pipeline = new NodeBuilderPipeline();

        // Act
        var node = pipeline.Build(heading, content, markdown);

        // Assert
        // [ ] 返回类型：AxtNode（不为 null）
        node.Should().NotBeNull();
        node.Should().BeOfType<AxtNode>();

        // [ ] node.Depth == heading.Level（此处为 1）
        node.Depth.Should().Be(1);

        // [ ] node.SourceHeadingBlock 引用等于输入 heading
        node.SourceHeadingBlock.Should().BeSameAs(heading);

        // [ ] node.Content 引用等于输入 content 列表
        node.Content.Should().BeEquivalentTo(content);
    }

    #endregion

    #region Case 5: Pipeline 顺序优先

    /// <summary>
    /// Case 5: Pipeline 按顺序调用 Builder，首个非 null 结果获胜。
    /// 验证按注册顺序调用 builder，第一个返回非 null 的结果立即返回。
    /// </summary>
    [Fact]
    public void Pipeline_FirstNonNullWins() {
        // Arrange
        var markdown = "# A";
        var doc = TestHelpers.ParseMarkdown(markdown);
        var heading = (HeadingBlock)doc[0];
        var content = new List<Block>();

        var expectedNode = new AxtNode(heading, content);

        // Builder1 返回 null
        var builder1 = new TestNodeBuilder(returnNull: true);
        // Builder2 返回特定节点
        var builder2 = new TestNodeBuilder(returnNull: false, nodeToReturn: expectedNode);

        var pipeline = new NodeBuilderPipeline(new INodeBuilder[] { builder1, builder2 });

        // Act
        var result = pipeline.Build(heading, content, markdown);

        // Assert
        // [ ] 按注册顺序调用 builder
        builder1.WasCalled.Should().BeTrue();
        builder2.WasCalled.Should().BeTrue();

        // [ ] 第一个返回非 null 的结果立即返回
        result.Should().BeSameAs(expectedNode);
    }

    /// <summary>
    /// Case 5: 当所有自定义 Builder 返回 null 时，DefaultNodeBuilder 兜底。
    /// </summary>
    [Fact]
    public void Pipeline_FallsBackToDefault() {
        // Arrange
        var markdown = "# A";
        var doc = TestHelpers.ParseMarkdown(markdown);
        var heading = (HeadingBlock)doc[0];
        var content = new List<Block>();

        // 两个都返回 null 的 Builder
        var builder1 = new TestNodeBuilder(returnNull: true);
        var builder2 = new TestNodeBuilder(returnNull: true);

        var pipeline = new NodeBuilderPipeline(new INodeBuilder[] { builder1, builder2 });

        // Act
        var result = pipeline.Build(heading, content, markdown);

        // Assert
        // 应该由 DefaultNodeBuilder 兜底返回
        result.Should().NotBeNull();
        builder1.WasCalled.Should().BeTrue();
        builder2.WasCalled.Should().BeTrue();
    }

    #endregion

    #region Case 6: InsertBefore 语义

    /// <summary>
    /// Case 6: InsertBefore 在 DefaultNodeBuilder 之前插入。
    /// 验证 DefaultNodeBuilder 始终保持在最后。
    /// </summary>
    [Fact]
    public void InsertBefore_InsertsBeforeDefault() {
        // Arrange
        var customBuilder = new TestNodeBuilder(returnNull: true);
        var pipeline = new NodeBuilderPipeline();

        // Act
        pipeline.InsertBefore(customBuilder);

        // Assert
        // [ ] Builders.Count == 2
        pipeline.Builders.Should().HaveCount(2);

        // [ ] Builders[0] == customBuilder
        pipeline.Builders[0].Should().BeSameAs(customBuilder);

        // [ ] Builders[1] 类型为 DefaultNodeBuilder
        pipeline.Builders[1].Should().BeOfType<DefaultNodeBuilder>();
    }

    /// <summary>
    /// Case 6: InsertBefore 不改变 DefaultNodeBuilder 的兜底语义。
    /// 验证若 customBuilder 返回 null，则仍应由 DefaultNodeBuilder 兜底返回 AxtNode。
    /// </summary>
    [Fact]
    public void InsertBefore_DefaultStillFallsBack() {
        // Arrange
        var markdown = "# A";
        var doc = TestHelpers.ParseMarkdown(markdown);
        var heading = (HeadingBlock)doc[0];
        var content = new List<Block>();

        var customBuilder = new TestNodeBuilder(returnNull: true);
        var pipeline = new NodeBuilderPipeline();
        pipeline.InsertBefore(customBuilder);

        // Act
        var result = pipeline.Build(heading, content, markdown);

        // Assert
        // [ ] 若 customBuilder 返回 null，则仍应由 DefaultNodeBuilder 兜底返回 AxtNode
        result.Should().NotBeNull();
        customBuilder.WasCalled.Should().BeTrue();
    }

    #endregion

    /// <summary>
    /// 测试用的 INodeBuilder 实现。
    /// </summary>
    private class TestNodeBuilder : INodeBuilder {
        private readonly bool _returnNull;
        private readonly AxtNode? _nodeToReturn;

        public bool WasCalled { get; private set; }

        public TestNodeBuilder(bool returnNull, AxtNode? nodeToReturn = null) {
            _returnNull = returnNull;
            _nodeToReturn = nodeToReturn;
        }

        public AxtNode? TryBuild(HeadingBlock heading, IReadOnlyList<Block> content, string originalMarkdown) {
            WasCalled = true;
            return _returnNull ? null : (_nodeToReturn ?? new AxtNode(heading, content));
        }
    }
}
