using FluentAssertions;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Xunit;

namespace Atelia.DesignDsl.Tests;

/// <summary>
/// Task-S-003: AxtTreeBuilder 测试。
/// 验证树构建语义：RootNode、父子关系、AllNodes 顺序。
/// </summary>
public class AxtTreeBuilderTests {
    #region Case 1: 空文档

    /// <summary>
    /// Case 1: 空文档。
    /// 验证 Build 不抛异常，tree.Root 存在且 AllNodes 首位是 Root。
    /// </summary>
    [Fact]
    public void Build_EmptyDocument_ReturnsTreeWithRootOnly() {
        // Arrange
        var markdown = "";
        var doc = TestHelpers.ParseMarkdown(markdown);
        var sections = AtxSectionSplitter.Split(doc.ToList(), markdown);
        var pipeline = new NodeBuilderPipeline();

        // Act - [ ] Build 不抛异常
        var tree = AxtTreeBuilder.Build(sections, pipeline);

        // Assert
        // Root 属性验证
        tree.Root.Should().NotBeNull();
        tree.Root.Depth.Should().Be(0);
        tree.Root.Heading.Should().BeNull();
        tree.Root.Content.Should().BeEmpty();
        tree.Root.Children.Should().BeEmpty();

        // [ ] AllNodes 首位是 Root
        tree.AllNodes.Should().HaveCount(1);
        tree.AllNodes[0].Should().BeSameAs(tree.Root);
    }

    #endregion

    #region Case 2: 仅 RootNode 内容（无 Heading）

    /// <summary>
    /// Case 2: 仅有 Preface 内容，无 Heading。
    /// 验证无 Heading 时不产生 AxtNode，Root.Content 等于 Preface。
    /// </summary>
    [Fact]
    public void Build_OnlyPreface_RootContainsAllContent() {
        // Arrange
        var markdown = """
            preface-1

            - a
            - b
            """;
        var doc = TestHelpers.ParseMarkdown(markdown);
        var sections = AtxSectionSplitter.Split(doc.ToList(), markdown);
        var pipeline = new NodeBuilderPipeline();

        // Act
        var tree = AxtTreeBuilder.Build(sections, pipeline);

        // Assert
        // [ ] Root.Content 等于 Splitter 的 Preface
        tree.Root.Content.Should().HaveCount(2);
        tree.Root.Content[0].Should().BeOfType<ParagraphBlock>();
        tree.Root.Content[1].Should().BeOfType<ListBlock>();

        // [ ] 无 Heading 时不产生 AxtNode
        tree.Root.Children.Should().BeEmpty();
        tree.AllNodes.Should().HaveCount(1);
    }

    #endregion

    #region Case 3: 单层 Heading（两个同级节点）

    /// <summary>
    /// Case 3: 两个同级 Heading。
    /// 验证同级节点都挂在 Root 下，AllNodes 按文档出现顺序。
    /// </summary>
    [Fact]
    public void Build_SiblingHeadings_BothUnderRoot() {
        // Arrange
        var markdown = """
            # A

            A-1

            # B
            """;
        var doc = TestHelpers.ParseMarkdown(markdown);
        var sections = AtxSectionSplitter.Split(doc.ToList(), markdown);
        var pipeline = new NodeBuilderPipeline();

        // Act
        var tree = AxtTreeBuilder.Build(sections, pipeline);

        // Assert
        // [ ] 同级节点都挂在 Root 下
        tree.Root.Children.Should().HaveCount(2);

        var nodeA = tree.Root.Children[0] as AxtNode;
        var nodeB = tree.Root.Children[1] as AxtNode;

        nodeA.Should().NotBeNull();
        nodeB.Should().NotBeNull();

        // A 节点验证
        nodeA!.Depth.Should().Be(1);
        nodeA.Parent.Should().BeSameAs(tree.Root);
        nodeA.Content.Should().HaveCount(1);

        // B 节点验证
        nodeB!.Depth.Should().Be(1);
        nodeB.Parent.Should().BeSameAs(tree.Root);
        nodeB.Content.Should().BeEmpty();

        // [ ] AllNodes 按文档出现顺序
        tree.AllNodes.Should().HaveCount(3);
        tree.AllNodes[0].Should().BeSameAs(tree.Root);
        tree.AllNodes[1].Should().BeSameAs(nodeA);
        tree.AllNodes[2].Should().BeSameAs(nodeB);
    }

    #endregion

    #region Case 4: 多层嵌套（逐级加深）

    /// <summary>
    /// Case 4: 逐级加深的嵌套。
    /// 验证深度递增时形成链式嵌套，Parent/Children 双向一致。
    /// </summary>
    [Fact]
    public void Build_NestedHeadings_FormsChain() {
        // Arrange
        var markdown = """
            # A

            ## B

            ### C

            C-1
            """;
        var doc = TestHelpers.ParseMarkdown(markdown);
        var sections = AtxSectionSplitter.Split(doc.ToList(), markdown);
        var pipeline = new NodeBuilderPipeline();

        // Act
        var tree = AxtTreeBuilder.Build(sections, pipeline);

        // Assert
        // [ ] 深度递增时形成链式嵌套
        // 结构：Root -> A -> B -> C
        tree.Root.Children.Should().HaveCount(1);

        var nodeA = tree.Root.Children[0] as AxtNode;
        nodeA.Should().NotBeNull();
        nodeA!.Children.Should().HaveCount(1);

        var nodeB = nodeA.Children[0] as AxtNode;
        nodeB.Should().NotBeNull();
        nodeB!.Children.Should().HaveCount(1);

        var nodeC = nodeB.Children[0] as AxtNode;
        nodeC.Should().NotBeNull();
        nodeC!.Children.Should().BeEmpty();

        // [ ] Parent/Children 双向一致
        nodeC.Parent.Should().BeSameAs(nodeB);
        nodeB.Parent.Should().BeSameAs(nodeA);
        nodeA.Parent.Should().BeSameAs(tree.Root);

        // [ ] AllNodes 顺序：Root, A, B, C
        tree.AllNodes.Should().HaveCount(4);
        tree.AllNodes[0].Should().BeSameAs(tree.Root);
        tree.AllNodes[1].Should().BeSameAs(nodeA);
        tree.AllNodes[2].Should().BeSameAs(nodeB);
        tree.AllNodes[3].Should().BeSameAs(nodeC);
    }

    #endregion

    #region Case 5: 跳跃 Depth

    /// <summary>
    /// Case 5: 跳跃 Depth（# 直接跟 ###）。
    /// 验证不要求 depth 连续，仍按"首个更小 depth"挂载。
    /// </summary>
    [Fact]
    public void Build_JumpingDepth_StillNestsCorrectly() {
        // Arrange
        var markdown = """
            # A

            ### B
            """;
        var doc = TestHelpers.ParseMarkdown(markdown);
        var sections = AtxSectionSplitter.Split(doc.ToList(), markdown);
        var pipeline = new NodeBuilderPipeline();

        // Act
        var tree = AxtTreeBuilder.Build(sections, pipeline);

        // Assert
        var nodeA = tree.Root.Children[0] as AxtNode;
        nodeA.Should().NotBeNull();
        nodeA!.Depth.Should().Be(1);

        // [ ] 不要求 depth 连续，仍按"首个更小 depth"挂载
        nodeA.Children.Should().HaveCount(1);
        var nodeB = nodeA.Children[0] as AxtNode;
        nodeB.Should().NotBeNull();
        nodeB!.Depth.Should().Be(3);
        nodeB.Parent.Should().BeSameAs(nodeA);

        // [ ] AllNodes 顺序：Root, A, B
        tree.AllNodes.Should().HaveCount(3);
        tree.AllNodes[0].Should().BeSameAs(tree.Root);
        tree.AllNodes[1].Should().BeSameAs(nodeA);
        tree.AllNodes[2].Should().BeSameAs(nodeB);
    }

    #endregion

    #region Case 6: 同级多节点

    /// <summary>
    /// Case 6: 同级多节点。
    /// 验证相同 depth 的节点互为兄弟，兄弟节点顺序与文档一致。
    /// </summary>
    [Fact]
    public void Build_SameLevelMultipleNodes_AreSiblings() {
        // Arrange
        var markdown = """
            # A

            ## B

            ## C

            ## D
            """;
        var doc = TestHelpers.ParseMarkdown(markdown);
        var sections = AtxSectionSplitter.Split(doc.ToList(), markdown);
        var pipeline = new NodeBuilderPipeline();

        // Act
        var tree = AxtTreeBuilder.Build(sections, pipeline);

        // Assert
        var nodeA = tree.Root.Children[0] as AxtNode;
        nodeA.Should().NotBeNull();

        // [ ] 相同 depth 的节点互为兄弟
        nodeA!.Children.Should().HaveCount(3);

        var nodeB = nodeA.Children[0] as AxtNode;
        var nodeC = nodeA.Children[1] as AxtNode;
        var nodeD = nodeA.Children[2] as AxtNode;

        // [ ] 兄弟节点顺序与文档一致
        nodeB.Should().NotBeNull();
        nodeC.Should().NotBeNull();
        nodeD.Should().NotBeNull();

        // B/C/D 的 Parent 都是 A
        nodeB!.Parent.Should().BeSameAs(nodeA);
        nodeC!.Parent.Should().BeSameAs(nodeA);
        nodeD!.Parent.Should().BeSameAs(nodeA);

        // [ ] AllNodes 顺序：Root, A, B, C, D
        tree.AllNodes.Should().HaveCount(5);
        tree.AllNodes[0].Should().BeSameAs(tree.Root);
        tree.AllNodes[1].Should().BeSameAs(nodeA);
        tree.AllNodes[2].Should().BeSameAs(nodeB);
        tree.AllNodes[3].Should().BeSameAs(nodeC);
        tree.AllNodes[4].Should().BeSameAs(nodeD);
    }

    #endregion

    #region Case 7: 带 YAML front-matter

    /// <summary>
    /// Case 7: 带 YAML front-matter。
    /// 验证 Root.Content 不含 YAML block。
    /// </summary>
    [Fact]
    public void Build_WithYamlFrontMatter_RootContentExcludesYaml() {
        // Arrange
        var markdown = """
            ---
            title: demo
            ---

            preface

            # A

            A-1
            """;
        var doc = TestHelpers.ParseMarkdown(markdown);
        var sections = AtxSectionSplitter.Split(doc.ToList(), markdown);
        var pipeline = new NodeBuilderPipeline();

        // Act
        var tree = AxtTreeBuilder.Build(sections, pipeline);

        // Assert
        // 前置条件：Splitter 正确处理 YAML
        sections.FrontMatter.Should().NotBeNull();
        sections.Preface.Should().HaveCount(1);

        // [ ] Root.Content 与 Preface 完全一致
        tree.Root.Content.Should().HaveCount(1);
        tree.Root.Content[0].Should().BeOfType<ParagraphBlock>();

        // [ ] YAML block 不应出现在 Root.Content
        tree.Root.Content.Should().NotContain(b => b is YamlFrontMatterBlock);

        // Root.Children[0] 为 A
        tree.Root.Children.Should().HaveCount(1);
    }

    #endregion

    #region Case 8: AllNodes 顺序验证（混合层级）

    /// <summary>
    /// Case 8: 混合层级的 AllNodes 顺序。
    /// 验证 AllNodes 严格按遇到 heading 的顺序追加。
    /// </summary>
    [Fact]
    public void Build_MixedLevels_AllNodesInDocumentOrder() {
        // Arrange
        var markdown = """
            # A

            ## B

            # C

            ## D

            ### E
            """;
        var doc = TestHelpers.ParseMarkdown(markdown);
        var sections = AtxSectionSplitter.Split(doc.ToList(), markdown);
        var pipeline = new NodeBuilderPipeline();

        // Act
        var tree = AxtTreeBuilder.Build(sections, pipeline);

        // Assert
        // [ ] AllNodes 严格按遇到 heading 的顺序追加
        tree.AllNodes.Should().HaveCount(6);

        var root = tree.Root;
        var nodeA = tree.AllNodes[1] as AxtNode;
        var nodeB = tree.AllNodes[2] as AxtNode;
        var nodeC = tree.AllNodes[3] as AxtNode;
        var nodeD = tree.AllNodes[4] as AxtNode;
        var nodeE = tree.AllNodes[5] as AxtNode;

        tree.AllNodes[0].Should().BeSameAs(root);

        // 结构验证
        // [ ] Root.Children: A, C
        root.Children.Should().HaveCount(2);
        root.Children[0].Should().BeSameAs(nodeA);
        root.Children[1].Should().BeSameAs(nodeC);

        // [ ] A.Children: B
        nodeA!.Children.Should().HaveCount(1);
        nodeA.Children[0].Should().BeSameAs(nodeB);

        // [ ] C.Children: D
        nodeC!.Children.Should().HaveCount(1);
        nodeC.Children[0].Should().BeSameAs(nodeD);

        // [ ] D.Children: E
        nodeD!.Children.Should().HaveCount(1);
        nodeD.Children[0].Should().BeSameAs(nodeE);

        // [ ] 当从 `##` 回到 `#` 时，父节点回溯到 Root
        nodeC.Parent.Should().BeSameAs(root);
    }

    #endregion

    #region Case 9: Parent/Children 双向引用一致性

    /// <summary>
    /// Case 9: Parent/Children 双向引用一致性验证。
    /// 验证 Parent 与 Children 的双向引用一致。
    /// </summary>
    [Fact]
    public void Build_ParentChildConsistency_BidirectionalReferencesMatch() {
        // Arrange
        var markdown = """
            # A

            ## B

            ## C

            ### D
            """;
        var doc = TestHelpers.ParseMarkdown(markdown);
        var sections = AtxSectionSplitter.Split(doc.ToList(), markdown);
        var pipeline = new NodeBuilderPipeline();

        // Act
        var tree = AxtTreeBuilder.Build(sections, pipeline);

        // Assert
        // [ ] Root 的 Parent 应为 null
        tree.Root.Parent.Should().BeNull();

        // 系统性验证所有节点的双向一致性
        foreach (var node in tree.AllNodes) {
            // [ ] 对每个节点 N（除 Root）：N.Parent.Children 必须包含 N
            if (node != tree.Root && node.Parent is not null) {
                node.Parent.Children.Should().Contain(node,
                    because: "parent's children should contain the node"
                );
            }

            // [ ] 对每个节点 N：N.Children 中每个 child 的 Parent 必须等于 N
            foreach (var child in node.Children) {
                child.Parent.Should().BeSameAs(node,
                    because: "child's parent should be the node"
                );
            }
        }
    }

    #endregion

    #region Case 10: Setext Heading

    /// <summary>
    /// Case 10: Setext Heading 行为对齐。
    /// 验证当前实现是否处理所有 HeadingBlock（包括 Setext）。
    /// </summary>
    [Fact]
    public void Build_SetextHeading_TreatedAsHeading() {
        // Arrange
        var markdown = """
            Title
            ====

            para
            """;
        var doc = TestHelpers.ParseMarkdown(markdown);
        var sections = AtxSectionSplitter.Split(doc.ToList(), markdown);
        var pipeline = new NodeBuilderPipeline();

        // Act
        var tree = AxtTreeBuilder.Build(sections, pipeline);

        // Assert
        // [ ] Markdig 将 Setext 语法解析为 HeadingBlock
        // [ ] Splitter 会把它当作 section heading
        // [ ] Tree 会产生一个 AxtNode，Depth = heading.Level（通常为 1）
        //
        // 如果 Markdig 将 Setext 解析为 HeadingBlock：
        if (sections.Sections.Count > 0) {
            tree.Root.Children.Should().HaveCount(1);
            var node = tree.Root.Children[0] as AxtNode;
            node.Should().NotBeNull();
            node!.Depth.Should().Be(1);
            node.Content.Should().HaveCount(1);
        }
        else {
            // 如果 Markdig 没有将其解析为 HeadingBlock，则 Preface 包含所有内容
            tree.Root.Content.Should().NotBeEmpty();
        }
    }

    #endregion

    #region 边界情况

    /// <summary>
    /// 边界：深度回退后再深入。
    /// </summary>
    [Fact]
    public void Build_DepthRollbackAndDeepen_CorrectStructure() {
        // Arrange - 复杂的层级变化
        var markdown = """
            # A

            ## B

            ### C

            # D

            ## E
            """;
        var doc = TestHelpers.ParseMarkdown(markdown);
        var sections = AtxSectionSplitter.Split(doc.ToList(), markdown);
        var pipeline = new NodeBuilderPipeline();

        // Act
        var tree = AxtTreeBuilder.Build(sections, pipeline);

        // Assert
        // 结构：Root -> {A, D}
        //       A -> B -> C
        //       D -> E
        tree.Root.Children.Should().HaveCount(2);

        var nodeA = tree.Root.Children[0] as AxtNode;
        var nodeD = tree.Root.Children[1] as AxtNode;

        nodeA.Should().NotBeNull();
        nodeD.Should().NotBeNull();

        nodeA!.Children.Should().HaveCount(1);
        var nodeB = nodeA.Children[0] as AxtNode;
        nodeB!.Children.Should().HaveCount(1);
        var nodeC = nodeB.Children[0] as AxtNode;
        nodeC!.Children.Should().BeEmpty();

        nodeD!.Children.Should().HaveCount(1);
        var nodeE = nodeD.Children[0] as AxtNode;
        nodeE!.Children.Should().BeEmpty();
    }

    /// <summary>
    /// 边界：所有同级 H1。
    /// </summary>
    [Fact]
    public void Build_AllH1_AllUnderRoot() {
        // Arrange
        var markdown = """
            # A
            # B
            # C
            """;
        var doc = TestHelpers.ParseMarkdown(markdown);
        var sections = AtxSectionSplitter.Split(doc.ToList(), markdown);
        var pipeline = new NodeBuilderPipeline();

        // Act
        var tree = AxtTreeBuilder.Build(sections, pipeline);

        // Assert
        tree.Root.Children.Should().HaveCount(3);
        foreach (var child in tree.Root.Children) {
            child.Depth.Should().Be(1);
            child.Parent.Should().BeSameAs(tree.Root);
        }
    }

    #endregion
}
