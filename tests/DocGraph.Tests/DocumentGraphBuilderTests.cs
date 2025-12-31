// DocGraph v0.1 - DocumentGraphBuilder 集成测试
// 参考：spec.md §4 文档图构建约束

using System.IO;
using Xunit;
using Atelia.DocGraph.Core;

namespace Atelia.DocGraph.Tests;

/// <summary>
/// DocumentGraphBuilder 集成测试。
/// 覆盖 Day 2 任务：目录扫描、关系提取、闭包构建、验证逻辑。
/// </summary>
public class DocumentGraphBuilderTests : IDisposable
{
    private readonly string _tempDir;

    public DocumentGraphBuilderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "docgraph-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    #region 任务 2.2：目录扫描和文件过滤

    [Fact]
    public void Build_ShouldScanWishDirectoriesRecursively()
    {
        // Arrange
        CreateDirectory("wishes/active");
        CreateDirectory("wishes/active/subdir");
        CreateFile("wishes/active/wish-0001.md", """
            ---
            title: Wish 1
            produce:
              - docs/api.md
            ---
            """);
        CreateFile("wishes/active/subdir/wish-0002.md", """
            ---
            title: Wish 2
            produce:
              - docs/spec.md
            ---
            """);
        CreateFile("docs/api.md", """
            ---
            title: API Doc
            ---
            """);
        CreateFile("docs/spec.md", """
            ---
            title: Spec Doc
            ---
            """);

        var builder = new DocumentGraphBuilder(_tempDir);

        // Act
        var graph = builder.Build();

        // Assert - 应该扫描到子目录中的文件
        Assert.Equal(4, graph.AllNodes.Count);
        Assert.Equal(2, graph.RootNodes.Count);
        Assert.Contains(graph.AllNodes, n => n.DocId == "W-0001");
        Assert.Contains(graph.AllNodes, n => n.DocId == "W-0002");
    }

    [Fact]
    public void Build_ShouldIgnoreHiddenFiles()
    {
        // Arrange
        CreateDirectory("wishes/active");
        CreateFile("wishes/active/wish-0001.md", """
            ---
            title: Normal Wish
            produce:
              - docs/api.md
            ---
            """);
        CreateFile("wishes/active/.hidden-wish.md", """
            ---
            title: Hidden Wish
            produce:
              - docs/hidden.md
            ---
            """);
        CreateFile("docs/api.md", """
            ---
            title: API Doc
            ---
            """);

        var builder = new DocumentGraphBuilder(_tempDir);

        // Act
        var graph = builder.Build();

        // Assert - 隐藏文件应该被忽略
        Assert.Equal(2, graph.AllNodes.Count);
        Assert.Single(graph.RootNodes);
        Assert.DoesNotContain(graph.AllNodes, n => n.Title == "Hidden Wish");
    }

    [Fact]
    public void Build_ShouldIgnoreTempFiles()
    {
        // Arrange
        CreateDirectory("wishes/active");
        CreateFile("wishes/active/wish-0001.md", """
            ---
            title: Normal Wish
            produce:
              - docs/api.md
            ---
            """);
        CreateFile("wishes/active/wish-0002.md~", """
            ---
            title: Temp Wish Backup
            produce:
              - docs/temp.md
            ---
            """);
        CreateFile("wishes/active/#wish-0003.md#", """
            ---
            title: Emacs Autosave
            produce:
              - docs/autosave.md
            ---
            """);
        CreateFile("docs/api.md", """
            ---
            title: API Doc
            ---
            """);

        var builder = new DocumentGraphBuilder(_tempDir);

        // Act
        var graph = builder.Build();

        // Assert - 临时文件应该被忽略
        Assert.Equal(2, graph.AllNodes.Count);
        Assert.Single(graph.RootNodes);
    }

    [Fact]
    public void Build_ShouldOnlyProcessMdFiles()
    {
        // Arrange
        CreateDirectory("wishes/active");
        CreateFile("wishes/active/wish-0001.md", """
            ---
            title: Markdown Wish
            produce:
              - docs/api.md
            ---
            """);
        CreateFile("wishes/active/readme.txt", "This is a text file");
        CreateFile("wishes/active/notes.yaml", "key: value");
        CreateFile("docs/api.md", """
            ---
            title: API Doc
            ---
            """);

        var builder = new DocumentGraphBuilder(_tempDir);

        // Act
        var graph = builder.Build();

        // Assert - 只处理 .md 文件
        Assert.Equal(2, graph.AllNodes.Count);
        Assert.DoesNotContain(graph.AllNodes, n => n.FilePath.EndsWith(".txt"));
        Assert.DoesNotContain(graph.AllNodes, n => n.FilePath.EndsWith(".yaml"));
    }

    [Fact]
    public void Build_ShouldDeriveStatusFromDirectory()
    {
        // Arrange
        CreateDirectory("wishes/active");
        CreateDirectory("wishes/completed");
        CreateFile("wishes/active/wish-0001.md", """
            ---
            title: Active Wish
            produce:
              - docs/api.md
            ---
            """);
        CreateFile("wishes/completed/wish-0002.md", """
            ---
            title: Completed Wish
            produce:
              - docs/spec.md
            ---
            """);
        CreateFile("docs/api.md", """
            ---
            title: API Doc
            ---
            """);
        CreateFile("docs/spec.md", """
            ---
            title: Spec Doc
            ---
            """);

        var builder = new DocumentGraphBuilder(_tempDir);

        // Act
        var graph = builder.Build();

        // Assert - 状态应该从目录推导
        var activeWish = graph.AllNodes.First(n => n.DocId == "W-0001");
        var completedWish = graph.AllNodes.First(n => n.DocId == "W-0002");
        Assert.Equal("active", activeWish.Status);
        Assert.Equal("completed", completedWish.Status);
    }

    [Fact]
    public void Build_ShouldHandleEmptyWishDirectory()
    {
        // Arrange
        CreateDirectory("wishes/active");
        // 目录存在但没有文件

        var builder = new DocumentGraphBuilder(_tempDir);

        // Act
        var graph = builder.Build();

        // Assert
        Assert.Empty(graph.AllNodes);
        Assert.Empty(graph.RootNodes);
    }

    [Fact]
    public void Build_ShouldHandleNonExistentWishDirectory()
    {
        // Arrange - 不创建任何目录

        var builder = new DocumentGraphBuilder(_tempDir);

        // Act
        var graph = builder.Build();

        // Assert - 应该优雅处理不存在的目录
        Assert.Empty(graph.AllNodes);
        Assert.Empty(graph.RootNodes);
    }

    #endregion

    #region 任务 2.3：produce 关系提取和闭包构建

    [Fact]
    public void Build_ShouldExtractProduceRelations()
    {
        // Arrange
        CreateDirectory("wishes/active");
        CreateFile("wishes/active/wish-0001.md", """
            ---
            title: Main Wish
            produce:
              - docs/api.md
              - docs/spec.md
            ---
            """);
        CreateFile("docs/api.md", """
            ---
            title: API Doc
            ---
            """);
        CreateFile("docs/spec.md", """
            ---
            title: Spec Doc
            ---
            """);

        var builder = new DocumentGraphBuilder(_tempDir);

        // Act
        var graph = builder.Build();

        // Assert
        var wish = graph.RootNodes.First();
        Assert.Equal(2, wish.Produces.Count);
        Assert.Contains(wish.Produces, p => p.FilePath == "docs/api.md");
        Assert.Contains(wish.Produces, p => p.FilePath == "docs/spec.md");
    }

    [Fact]
    public void Build_ShouldEstablishBidirectionalRelations()
    {
        // Arrange
        CreateDirectory("wishes/active");
        CreateFile("wishes/active/wish-0001.md", """
            ---
            title: Main Wish
            produce:
              - docs/api.md
            ---
            """);
        CreateFile("docs/api.md", """
            ---
            title: API Doc
            ---
            """);

        var builder = new DocumentGraphBuilder(_tempDir);

        // Act
        var graph = builder.Build();

        // Assert - 检查双向关系
        var wish = graph.RootNodes.First();
        var apiDoc = graph.ByPath["docs/api.md"];

        Assert.Contains(wish.Produces, p => p.FilePath == "docs/api.md");
        Assert.Contains(apiDoc.ProducedBy, p => p.FilePath == wish.FilePath);
    }

    [Fact]
    public void Build_ShouldBuildTransitiveClosure()
    {
        // Arrange - 多层 produce 关系：Wish → Doc1 → Doc2 → Doc3
        CreateDirectory("wishes/active");
        CreateFile("wishes/active/wish-0001.md", """
            ---
            title: Root Wish
            produce:
              - docs/level1.md
            ---
            """);
        CreateFile("docs/level1.md", """
            ---
            title: Level 1
            produce:
              - docs/level2.md
            ---
            """);
        CreateFile("docs/level2.md", """
            ---
            title: Level 2
            produce:
              - docs/level3.md
            ---
            """);
        CreateFile("docs/level3.md", """
            ---
            title: Level 3
            ---
            """);

        var builder = new DocumentGraphBuilder(_tempDir);

        // Act
        var graph = builder.Build();

        // Assert - 所有层级的文档都应该被追踪
        Assert.Equal(4, graph.AllNodes.Count);
        Assert.Contains(graph.AllNodes, n => n.FilePath == "docs/level1.md");
        Assert.Contains(graph.AllNodes, n => n.FilePath == "docs/level2.md");
        Assert.Contains(graph.AllNodes, n => n.FilePath == "docs/level3.md");
    }

    [Fact]
    public void Build_ShouldHandleMissingProduceTarget()
    {
        // Arrange
        CreateDirectory("wishes/active");
        CreateFile("wishes/active/wish-0001.md", """
            ---
            title: Wish with missing target
            produce:
              - docs/missing.md
            ---
            """);
        // 不创建 docs/missing.md

        var builder = new DocumentGraphBuilder(_tempDir);

        // Act
        var graph = builder.Build();

        // Assert - 应该创建占位节点
        Assert.Equal(2, graph.AllNodes.Count);
        var missingNode = graph.ByPath["docs/missing.md"];
        Assert.NotNull(missingNode);
        Assert.Contains("[缺失]", missingNode.Title);
    }

    [Fact]
    public void Build_ShouldHandleCircularReference()
    {
        // Arrange - A → B → A 循环引用
        CreateDirectory("wishes/active");
        CreateFile("wishes/active/wish-0001.md", """
            ---
            title: Wish A
            produce:
              - docs/docB.md
            ---
            """);
        CreateFile("docs/docB.md", """
            ---
            title: Doc B
            produce:
              - wishes/active/wish-0001.md
            ---
            """);

        var builder = new DocumentGraphBuilder(_tempDir);

        // Act - 不应该死循环
        var graph = builder.Build();

        // Assert
        Assert.Equal(2, graph.AllNodes.Count);
    }

    [Fact]
    public void Build_ShouldHandleSingleStringProduce()
    {
        // Arrange - produce 是单个字符串而非数组
        CreateDirectory("wishes/active");
        CreateFile("wishes/active/wish-0001.md", """
            ---
            title: Single Produce Wish
            produce: docs/api.md
            ---
            """);
        CreateFile("docs/api.md", """
            ---
            title: API Doc
            ---
            """);

        var builder = new DocumentGraphBuilder(_tempDir);

        // Act
        var graph = builder.Build();

        // Assert
        var wish = graph.RootNodes.First();
        Assert.Single(wish.Produces);
        Assert.Equal("docs/api.md", wish.Produces[0].FilePath);
    }

    #endregion

    #region 任务 2.4：基础验证逻辑

    [Fact]
    public void Validate_ShouldRequireTitleField()
    {
        // Arrange
        CreateDirectory("wishes/active");
        CreateFile("wishes/active/wish-0001.md", """
            ---
            produce:
              - docs/api.md
            ---
            """);
        CreateFile("docs/api.md", """
            ---
            title: API Doc
            ---
            """);

        var builder = new DocumentGraphBuilder(_tempDir);
        var graph = builder.Build();

        // Act
        var result = builder.Validate(graph);

        // Assert
        Assert.True(result.HasWarnings);
        Assert.Contains(result.Issues, i =>
            i.ErrorCode == "DOCGRAPH_FRONTMATTER_REQUIRED_FIELD_MISSING" &&
            i.Message.Contains("title"));
    }

    [Fact]
    public void Validate_ShouldRequireProduceForWish()
    {
        // Arrange
        CreateDirectory("wishes/active");
        CreateFile("wishes/active/wish-0001.md", """
            ---
            title: Wish without produce
            ---
            """);

        var builder = new DocumentGraphBuilder(_tempDir);
        var graph = builder.Build();

        // Act
        var result = builder.Validate(graph);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i =>
            i.ErrorCode == "DOCGRAPH_FRONTMATTER_REQUIRED_FIELD_MISSING" &&
            i.Severity == IssueSeverity.Error &&
            i.Message.Contains("produce"));
    }

    [Fact]
    public void Validate_ShouldDetectDanglingLinks()
    {
        // Arrange
        CreateDirectory("wishes/active");
        CreateFile("wishes/active/wish-0001.md", """
            ---
            title: Wish with dangling link
            produce:
              - docs/nonexistent.md
            ---
            """);

        var builder = new DocumentGraphBuilder(_tempDir);
        var graph = builder.Build();

        // Act
        var result = builder.Validate(graph);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i =>
            i.ErrorCode == "DOCGRAPH_RELATION_DANGLING_LINK" &&
            i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void Validate_ShouldDetectInvalidPath()
    {
        // Arrange
        CreateDirectory("wishes/active");
        CreateFile("wishes/active/wish-0001.md", """
            ---
            title: Wish with invalid path
            produce:
              - ../../../outside/file.md
            ---
            """);

        var builder = new DocumentGraphBuilder(_tempDir);
        var graph = builder.Build();

        // Act
        var result = builder.Validate(graph);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i =>
            i.ErrorCode == "DOCGRAPH_PATH_OUT_OF_WORKSPACE");
    }

    [Fact]
    public void Validate_ShouldPassForValidGraph()
    {
        // Arrange - 完整的产物文档必须包含 docId, title, produce_by
        CreateDirectory("wishes/active");
        CreateFile("wishes/active/wish-0001.md", """
            ---
            title: Valid Wish
            produce:
              - docs/api.md
            ---
            """);
        CreateFile("docs/api.md", """
            ---
            docId: api-doc
            title: API Doc
            produce_by:
              - wishes/active/wish-0001.md
            ---
            """);

        var builder = new DocumentGraphBuilder(_tempDir);
        var graph = builder.Build();

        // Act
        var result = builder.Validate(graph);

        // Assert
        Assert.True(result.IsValid);
        Assert.DoesNotContain(result.Issues, i => i.Severity >= IssueSeverity.Error);
    }

    #endregion

    #region 任务 2.5：ValidationResult 和错误报告

    [Fact]
    public void Validate_ShouldIncludeScanStatistics()
    {
        // Arrange
        CreateDirectory("wishes/active");
        CreateFile("wishes/active/wish-0001.md", """
            ---
            title: Wish 1
            produce:
              - docs/api.md
              - docs/spec.md
            ---
            """);
        CreateFile("docs/api.md", """
            ---
            title: API Doc
            ---
            """);
        CreateFile("docs/spec.md", """
            ---
            title: Spec Doc
            ---
            """);

        var builder = new DocumentGraphBuilder(_tempDir);
        var graph = builder.Build();

        // Act
        var result = builder.Validate(graph);

        // Assert
        Assert.Equal(3, result.Statistics.TotalFiles);
        Assert.Equal(1, result.Statistics.WishDocuments);
        Assert.Equal(2, result.Statistics.ProductDocuments);
        Assert.Equal(2, result.Statistics.TotalRelations);
        Assert.True(result.Statistics.ElapsedTime.TotalMilliseconds > 0);
    }

    [Fact]
    public void Validate_IssuesShouldHaveThreeTierSuggestions()
    {
        // Arrange
        CreateDirectory("wishes/active");
        CreateFile("wishes/active/wish-0001.md", """
            ---
            title: Wish with dangling link
            produce:
              - docs/missing.md
            ---
            """);

        var builder = new DocumentGraphBuilder(_tempDir);
        var graph = builder.Build();

        // Act
        var result = builder.Validate(graph);

        // Assert - 检查三层建议结构
        var issue = result.Issues.First(i => i.ErrorCode == "DOCGRAPH_RELATION_DANGLING_LINK");
        Assert.NotNull(issue.QuickSuggestion);
        Assert.NotEmpty(issue.QuickSuggestion);
        Assert.NotNull(issue.DetailedSuggestion);
        Assert.NotEmpty(issue.DetailedSuggestion);
    }

    [Fact]
    public void Validate_IssuesShouldBeSortedBySeverity()
    {
        // Arrange - 创建多个不同严重度的问题
        CreateDirectory("wishes/active");
        CreateFile("wishes/active/wish-0001.md", """
            ---
            produce:
              - docs/missing.md
            ---
            """);
        // 这会产生：1) title 缺失 (Warning), 2) produce 目标不存在 (Error)

        var builder = new DocumentGraphBuilder(_tempDir);
        var graph = builder.Build();

        // Act
        var result = builder.Validate(graph);

        // Assert - 问题应该按严重度降序排序
        var severities = result.Issues.Select(i => i.Severity).ToList();
        Assert.True(severities.SequenceEqual(severities.OrderByDescending(s => s)));
    }

    [Fact]
    public void Validate_ShouldIncludeFilePathInIssues()
    {
        // Arrange
        CreateDirectory("wishes/active");
        CreateFile("wishes/active/wish-0001.md", """
            ---
            title: Wish with issue
            produce:
              - docs/missing.md
            ---
            """);

        var builder = new DocumentGraphBuilder(_tempDir);
        var graph = builder.Build();

        // Act
        var result = builder.Validate(graph);

        // Assert
        var issue = result.Issues.First();
        Assert.NotNull(issue.FilePath);
        Assert.NotEmpty(issue.FilePath);
    }

    #endregion

    #region P1-1修复验证测试：路径归一化越界折叠

    [Fact]
    public void Build_ShouldNotCollapseOutOfWorkspacePath()
    {
        // Arrange - P1-1: 越界路径 ../outside.md 不应被折叠为 outside.md
        CreateDirectory("wishes/active");
        CreateFile("wishes/active/wish-0001.md", """
            ---
            title: Wish with out-of-workspace path
            produce:
              - ../outside.md
            ---
            """);
        // 关键：同时创建一个 outside.md 在 workspace 内
        CreateFile("outside.md", """
            ---
            title: Inside Outside
            ---
            """);

        var builder = new DocumentGraphBuilder(_tempDir);

        // Act
        var graph = builder.Build();

        // Assert - 不应该将 ../outside.md 误解为 outside.md
        // 越界路径不应该入队，所以 outside.md 不应该在图中
        Assert.Equal(1, graph.AllNodes.Count);
        Assert.DoesNotContain(graph.AllNodes, n => n.FilePath == "outside.md");
    }

    [Fact]
    public void Build_ShouldNotCollapseDeepOutOfWorkspacePath()
    {
        // Arrange - P1-1: 多层越界路径 ../../../deep/outside.md
        CreateDirectory("wishes/active");
        CreateFile("wishes/active/wish-0001.md", """
            ---
            title: Wish with deep out-of-workspace path
            produce:
              - ../../../deep/outside.md
            ---
            """);
        // 关键：创建一个 deep/outside.md 在 workspace 内
        CreateFile("deep/outside.md", """
            ---
            title: Deep Inside
            ---
            """);

        var builder = new DocumentGraphBuilder(_tempDir);

        // Act
        var graph = builder.Build();

        // Assert - 越界路径不应该入队
        Assert.Equal(1, graph.AllNodes.Count);
        Assert.DoesNotContain(graph.AllNodes, n => n.FilePath == "deep/outside.md");
    }

    [Fact]
    public void Build_ShouldAllowValidParentReference()
    {
        // Arrange - 有效的 .. 引用：subdir/../docs/api.md -> docs/api.md
        CreateDirectory("wishes/active/subdir");
        CreateFile("wishes/active/subdir/wish-0001.md", """
            ---
            title: Wish with valid parent reference
            produce:
              - ../docs/api.md
            ---
            """);
        CreateFile("wishes/active/docs/api.md", """
            ---
            title: API Doc
            ---
            """);

        var builder = new DocumentGraphBuilder(_tempDir);

        // Act
        var graph = builder.Build();

        // Assert - 有效的相对路径应该正常处理
        Assert.Equal(2, graph.AllNodes.Count);
    }

    [Fact]
    public void Validate_ShouldReportOutOfWorkspacePathError()
    {
        // Arrange - P1-1: 验证阶段应该报告越界路径错误
        CreateDirectory("wishes/active");
        CreateFile("wishes/active/wish-0001.md", """
            ---
            title: Wish with out-of-workspace path
            produce:
              - ../outside.md
            ---
            """);

        var builder = new DocumentGraphBuilder(_tempDir);
        var graph = builder.Build();

        // Act
        var result = builder.Validate(graph);

        // Assert - 应该报告PATH_OUT_OF_WORKSPACE错误
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i =>
            i.ErrorCode == "DOCGRAPH_PATH_OUT_OF_WORKSPACE" &&
            i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void Build_ProductDocShouldNotCollapseOutOfWorkspacePath()
    {
        // Arrange - P1-1: 产物文档的 produce 路径也不应越界折叠
        CreateDirectory("wishes/active");
        CreateFile("wishes/active/wish-0001.md", """
            ---
            title: Root Wish
            produce:
              - docs/level1.md
            ---
            """);
        CreateFile("docs/level1.md", """
            ---
            title: Level 1
            produce:
              - ../../../system/secret.md
            ---
            """);
        // 创建一个 system/secret.md 在 workspace 内
        CreateFile("system/secret.md", """
            ---
            title: Inside Secret
            ---
            """);

        var builder = new DocumentGraphBuilder(_tempDir);

        // Act
        var graph = builder.Build();

        // Assert - 越界路径不应该入队
        Assert.Equal(2, graph.AllNodes.Count);
        Assert.DoesNotContain(graph.AllNodes, n => n.FilePath == "system/secret.md");
    }

    #endregion

    #region P2-3修复验证测试：排序规则字段补齐

    [Fact]
    public void Validate_ShouldIncludeTargetFilePathInIssues()
    {
        // Arrange - P2-3: 关系类问题应该包含目标文件路径
        CreateDirectory("wishes/active");
        CreateFile("wishes/active/wish-0001.md", """
            ---
            title: Wish with dangling link
            produce:
              - docs/missing.md
            ---
            """);

        var builder = new DocumentGraphBuilder(_tempDir);
        var graph = builder.Build();

        // Act
        var result = builder.Validate(graph);

        // Assert - DANGLING_LINK 问题应该包含 TargetFilePath
        var danglingIssue = result.Issues.FirstOrDefault(i =>
            i.ErrorCode == "DOCGRAPH_RELATION_DANGLING_LINK");

        Assert.NotNull(danglingIssue);
        Assert.Equal("docs/missing.md", danglingIssue.TargetFilePath);
    }

    [Fact]
    public void Validate_ShouldSortIssuesByTargetFilePath()
    {
        // Arrange - P2-3: 问题应该按源文件路径+目标文件路径排序
        CreateDirectory("wishes/active");
        CreateFile("wishes/active/wish-0001.md", """
            ---
            title: Wish with multiple dangling links
            produce:
              - docs/z-last.md
              - docs/a-first.md
              - docs/m-middle.md
            ---
            """);

        var builder = new DocumentGraphBuilder(_tempDir);
        var graph = builder.Build();

        // Act
        var result = builder.Validate(graph);

        // Assert - DANGLING_LINK 问题应该按 TargetFilePath 排序
        var danglingIssues = result.Issues
            .Where(i => i.ErrorCode == "DOCGRAPH_RELATION_DANGLING_LINK")
            .ToList();

        Assert.Equal(3, danglingIssues.Count);
        Assert.Equal("docs/a-first.md", danglingIssues[0].TargetFilePath);
        Assert.Equal("docs/m-middle.md", danglingIssues[1].TargetFilePath);
        Assert.Equal("docs/z-last.md", danglingIssues[2].TargetFilePath);
    }

    #endregion

    #region P1-2修复验证测试：产物文档核心字段验证

    [Fact]
    public void Validate_ShouldErrorMissingDocIdInProduct()
    {
        // Arrange - P1-2: 产物文档缺少 docId 字段应报Error
        // 遵循 [S-FRONTMATTER-006]：必填字段缺失 → Error
        CreateDirectory("wishes/active");
        CreateFile("wishes/active/wish-0001.md", """
            ---
            title: Valid Wish
            produce:
              - docs/missing-docid.md
            ---
            """);
        CreateFile("docs/missing-docid.md", """
            ---
            title: Product without docId
            produce_by:
              - wishes/active/wish-0001.md
            ---
            """);

        var builder = new DocumentGraphBuilder(_tempDir);
        var graph = builder.Build();

        // Act
        var result = builder.Validate(graph);

        // Assert - 应该有 Error 级别的 docId 缺失问题（不是 Warning）
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i =>
            i.ErrorCode == "DOCGRAPH_FRONTMATTER_REQUIRED_FIELD_MISSING" &&
            i.Severity == IssueSeverity.Error &&
            i.Message.Contains("docId"));
    }

    [Fact]
    public void Validate_ShouldErrorMissingProduceByInProduct()
    {
        // Arrange - P1-2: 产物文档缺少 produce_by 字段应报Error
        // 遵循 [S-FRONTMATTER-006]：必填字段缺失 → Error
        CreateDirectory("wishes/active");
        CreateFile("wishes/active/wish-0001.md", """
            ---
            title: Valid Wish
            produce:
              - docs/missing-produceby.md
            ---
            """);
        CreateFile("docs/missing-produceby.md", """
            ---
            docId: test-product
            title: Product without produce_by
            ---
            """);

        var builder = new DocumentGraphBuilder(_tempDir);
        var graph = builder.Build();

        // Act
        var result = builder.Validate(graph);

        // Assert - 应该有 Error 级别的 produce_by 缺失问题（不是 Warning）
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i =>
            i.ErrorCode == "DOCGRAPH_FRONTMATTER_REQUIRED_FIELD_MISSING" &&
            i.Severity == IssueSeverity.Error &&
            i.Message.Contains("produce_by"));
    }

    [Fact]
    public void Validate_ShouldPassWithCompleteProductFrontmatter()
    {
        // Arrange - P1-2: 完整的产物文档 frontmatter
        CreateDirectory("wishes/active");
        CreateFile("wishes/active/wish-0001.md", """
            ---
            title: Valid Wish
            produce:
              - docs/complete-product.md
            ---
            """);
        CreateFile("docs/complete-product.md", """
            ---
            docId: complete-product-id
            title: Complete Product
            produce_by:
              - wishes/active/wish-0001.md
            ---
            """);

        var builder = new DocumentGraphBuilder(_tempDir);
        var graph = builder.Build();

        // Act
        var result = builder.Validate(graph);

        // Assert - 不应该有产物文档核心字段问题
        Assert.DoesNotContain(result.Issues, i =>
            i.FilePath == "docs/complete-product.md" &&
            i.ErrorCode == "DOCGRAPH_FRONTMATTER_REQUIRED_FIELD_MISSING");
    }

    [Fact]
    public void Validate_ShouldNotWarnPlaceholderNodeForMissingCoreFields()
    {
        // Arrange - P1-2: 占位节点（文件不存在）不应该验证 docId/produce_by 核心字段
        // 因为占位节点本身就表示"文件不存在"，强制验证核心字段没有意义
        CreateDirectory("wishes/active");
        CreateFile("wishes/active/wish-0001.md", """
            ---
            title: Wish with dangling link
            produce:
              - docs/nonexistent.md
            ---
            """);
        // 不创建 docs/nonexistent.md

        var builder = new DocumentGraphBuilder(_tempDir);
        var graph = builder.Build();

        // Act
        var result = builder.Validate(graph);

        // Assert
        // 1. 应该有 DANGLING_LINK 错误（在 wish 文档上报告）
        Assert.Contains(result.Issues, i =>
            i.ErrorCode == "DOCGRAPH_RELATION_DANGLING_LINK" &&
            i.FilePath == "wishes/active/wish-0001.md");

        // 2. 占位节点的问题只应该是 title 缺失（因为 Title 是 "[缺失] xxx"）
        var placeholderIssues = result.Issues.Where(i => i.FilePath == "docs/nonexistent.md").ToList();

        // 不应该有 docId 或 produce_by 缺失的警告（因为这些字段检查只针对有效文件）
        Assert.DoesNotContain(placeholderIssues, i =>
            i.ErrorCode == "DOCGRAPH_FRONTMATTER_REQUIRED_FIELD_MISSING" &&
            (i.Message.Contains("docId") || i.Message.Contains("produce_by")));
    }

    #endregion

    #region Craftsman审计关键测试

    [Fact]
    public void Build_ShouldNotBindEdgeToCollapsedOutOfWorkspacePath()
    {
        // Craftsman审计关键测试1：建边阶段不应误绑定
        // 越界路径（如 ../docs/api.md）在 workspace 内存在同名文件时，Produces 不应建立该边
        CreateDirectory("wishes/active");
        CreateFile("wishes/active/wish-0001.md", """
            ---
            title: Wish with out-of-workspace produce
            produce:
              - ../docs/api.md
              - docs/valid.md
            ---
            """);
        // 在 workspace 内创建 docs/api.md，如果越界路径被折叠，可能会误连到这里
        // 注意：docs/api.md 不应该出现在图中，因为没有有效的 produce 指向它
        CreateFile("docs/api.md", """
            ---
            docId: inside-api
            title: Inside API Doc
            produce_by:
              - wishes/active/wish-0001.md
            ---
            """);
        CreateFile("docs/valid.md", """
            ---
            docId: valid-doc
            title: Valid Doc
            produce_by:
              - wishes/active/wish-0001.md
            ---
            """);

        var builder = new DocumentGraphBuilder(_tempDir);

        // Act
        var graph = builder.Build();

        // Assert - 越界路径 ../docs/api.md 不应该被建边到 docs/api.md
        var wish = graph.RootNodes.First();
        
        // Produces 应该只有 docs/valid.md，不应该有 docs/api.md
        Assert.Single(wish.Produces);
        Assert.Equal("docs/valid.md", wish.Produces[0].FilePath);
        
        // docs/api.md 不应该在图中，因为越界路径被正确跳过
        Assert.False(graph.ByPath.ContainsKey("docs/api.md"), 
            "docs/api.md 不应该在图中，因为没有有效的 produce 路径指向它");
    }

    [Fact]
    public void Validate_ProduceByOutOfWorkspaceShouldReturnCorrectErrorCode()
    {
        // Craftsman审计关键测试2：produce_by越界错误码/严重度
        // `produce_by: ["../outside.md"]` 应触发 `DOCGRAPH_PATH_OUT_OF_WORKSPACE`（而非 DANGLING_BACKLINK）
        CreateDirectory("wishes/active");
        CreateFile("wishes/active/wish-0001.md", """
            ---
            title: Valid Wish
            produce:
              - docs/product.md
            ---
            """);
        CreateFile("docs/product.md", """
            ---
            docId: product-id
            title: Product Doc
            produce_by:
              - ../outside.md
            ---
            """);

        var builder = new DocumentGraphBuilder(_tempDir);
        var graph = builder.Build();

        // Act
        var result = builder.Validate(graph);

        // Assert - 应该报告 PATH_OUT_OF_WORKSPACE 错误（而非 DANGLING_BACKLINK）
        Assert.Contains(result.Issues, i =>
            i.ErrorCode == "DOCGRAPH_PATH_OUT_OF_WORKSPACE" &&
            i.Severity == IssueSeverity.Error &&
            i.FilePath == "docs/product.md");

        // 不应该将越界路径误报为 DANGLING_BACKLINK
        Assert.DoesNotContain(result.Issues, i =>
            i.ErrorCode == "DOCGRAPH_RELATION_DANGLING_BACKLINK" &&
            i.FilePath == "docs/product.md" &&
            i.Message.Contains("../outside.md"));
    }

    [Fact]
    public void Validate_ProductCoreFieldsMissingShouldBeError()
    {
        // Craftsman审计关键测试3：产物核心字段必填为Error
        // 缺失 docId/title/produce_by 应按 [S-FRONTMATTER-006] 返回 Error
        CreateDirectory("wishes/active");
        CreateFile("wishes/active/wish-0001.md", """
            ---
            title: Valid Wish
            produce:
              - docs/minimal.md
            ---
            """);
        // 产物文档只有 title，缺少 docId 和 produce_by
        CreateFile("docs/minimal.md", """
            ---
            title: Minimal Product
            ---
            """);

        var builder = new DocumentGraphBuilder(_tempDir);
        var graph = builder.Build();

        // Act
        var result = builder.Validate(graph);

        // Assert - docId 和 produce_by 缺失都应该是 Error 级别
        Assert.False(result.IsValid, "缺少核心字段应导致验证不通过");

        // docId 缺失应为 Error
        var docIdIssue = result.Issues.FirstOrDefault(i =>
            i.ErrorCode == "DOCGRAPH_FRONTMATTER_REQUIRED_FIELD_MISSING" &&
            i.FilePath == "docs/minimal.md" &&
            i.Message.Contains("docId"));
        Assert.NotNull(docIdIssue);
        Assert.Equal(IssueSeverity.Error, docIdIssue.Severity);

        // produce_by 缺失应为 Error
        var produceByIssue = result.Issues.FirstOrDefault(i =>
            i.ErrorCode == "DOCGRAPH_FRONTMATTER_REQUIRED_FIELD_MISSING" &&
            i.FilePath == "docs/minimal.md" &&
            i.Message.Contains("produce_by"));
        Assert.NotNull(produceByIssue);
        Assert.Equal(IssueSeverity.Error, produceByIssue.Severity);
    }

    #endregion

    #region P0修复验证测试

    [Fact]
    public void Validate_ShouldDetectMissingFrontmatter()
    {
        // Arrange - [A-DOCGRAPH-005] produce目标存在但无frontmatter
        CreateDirectory("wishes/active");
        CreateFile("wishes/active/wish-0001.md", """
            ---
            title: Valid Wish
            produce:
              - docs/no-frontmatter.md
            ---
            """);
        CreateFile("docs/no-frontmatter.md", "This file has no frontmatter");

        var builder = new DocumentGraphBuilder(_tempDir);
        var graph = builder.Build();

        // Act
        var result = builder.Validate(graph);

        // Assert - 应该报告DANGLING_LINK错误
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i =>
            i.ErrorCode == "DOCGRAPH_RELATION_DANGLING_LINK" &&
            i.Message.Contains("frontmatter"));
    }

    [Fact]
    public void Validate_ShouldDetectMissingBacklink()
    {
        // Arrange - [A-DOCGRAPH-005] 目标文档没有produce_by声明源
        CreateDirectory("wishes/active");
        CreateFile("wishes/active/wish-0001.md", """
            ---
            title: Source Wish
            produce:
              - docs/target.md
            ---
            """);
        CreateFile("docs/target.md", """
            ---
            title: Target Doc
            ---
            """);
        // 注意：target.md 没有 produce_by 字段

        var builder = new DocumentGraphBuilder(_tempDir);
        var graph = builder.Build();

        // Act
        var result = builder.Validate(graph);

        // Assert - 应该报告MISSING_BACKLINK警告
        Assert.True(result.HasWarnings);
        Assert.Contains(result.Issues, i =>
            i.ErrorCode == "DOCGRAPH_RELATION_MISSING_BACKLINK");
    }

    [Fact]
    public void Validate_ShouldPassWithCorrectBacklink()
    {
        // Arrange - 正确的双向链接
        CreateDirectory("wishes/active");
        CreateFile("wishes/active/wish-0001.md", """
            ---
            title: Source Wish
            produce:
              - docs/target.md
            ---
            """);
        CreateFile("docs/target.md", """
            ---
            title: Target Doc
            produce_by:
              - wishes/active/wish-0001.md
            ---
            """);

        var builder = new DocumentGraphBuilder(_tempDir);
        var graph = builder.Build();

        // Act
        var result = builder.Validate(graph);

        // Assert - 不应该有MISSING_BACKLINK
        Assert.DoesNotContain(result.Issues, i =>
            i.ErrorCode == "DOCGRAPH_RELATION_MISSING_BACKLINK");
    }

    [Fact]
    public void Validate_ShouldDetectCircularReference()
    {
        // Arrange - [A-DOCGRAPH-003] 循环引用检测
        CreateDirectory("wishes/active");
        CreateFile("wishes/active/wish-0001.md", """
            ---
            title: Wish A
            produce:
              - docs/docB.md
            ---
            """);
        CreateFile("docs/docB.md", """
            ---
            title: Doc B
            produce_by:
              - wishes/active/wish-0001.md
            produce:
              - docs/docC.md
            ---
            """);
        CreateFile("docs/docC.md", """
            ---
            title: Doc C
            produce_by:
              - docs/docB.md
            produce:
              - wishes/active/wish-0001.md
            ---
            """);
        // 循环：wish-0001 -> docB -> docC -> wish-0001

        var builder = new DocumentGraphBuilder(_tempDir);
        var graph = builder.Build();

        // Act
        var result = builder.Validate(graph);

        // Assert - 应该有Info级别的循环引用记录
        Assert.Contains(result.Issues, i =>
            i.ErrorCode == "DOCGRAPH_RELATION_CIRCULAR_REFERENCE" &&
            i.Severity == IssueSeverity.Info);
    }

    [Fact]
    public void Build_ShouldHaveDeterministicEdgeOrder()
    {
        // Arrange - [A-DOCGRAPH-004] 边排序确定性
        CreateDirectory("wishes/active");
        CreateFile("wishes/active/wish-0001.md", """
            ---
            title: Wish with multiple produces
            produce:
              - docs/z-last.md
              - docs/a-first.md
              - docs/m-middle.md
            ---
            """);
        CreateFile("docs/z-last.md", """
            ---
            title: Z Doc
            produce_by:
              - wishes/active/wish-0001.md
            ---
            """);
        CreateFile("docs/a-first.md", """
            ---
            title: A Doc
            produce_by:
              - wishes/active/wish-0001.md
            ---
            """);
        CreateFile("docs/m-middle.md", """
            ---
            title: M Doc
            produce_by:
              - wishes/active/wish-0001.md
            ---
            """);

        var builder = new DocumentGraphBuilder(_tempDir);

        // Act
        var graph = builder.Build();

        // Assert - Produces应该按FilePath字典序排序
        var wish = graph.RootNodes.First();
        Assert.Equal(3, wish.Produces.Count);
        Assert.Equal("docs/a-first.md", wish.Produces[0].FilePath);
        Assert.Equal("docs/m-middle.md", wish.Produces[1].FilePath);
        Assert.Equal("docs/z-last.md", wish.Produces[2].FilePath);
    }

    [Fact]
    public void Build_ShouldHaveDeterministicProducedByOrder()
    {
        // Arrange - [A-DOCGRAPH-004] ProducedBy边排序确定性
        CreateDirectory("wishes/active");
        CreateFile("wishes/active/wish-0003.md", """
            ---
            title: Wish 3
            produce:
              - docs/shared.md
            ---
            """);
        CreateFile("wishes/active/wish-0001.md", """
            ---
            title: Wish 1
            produce:
              - docs/shared.md
            ---
            """);
        CreateFile("wishes/active/wish-0002.md", """
            ---
            title: Wish 2
            produce:
              - docs/shared.md
            ---
            """);
        CreateFile("docs/shared.md", """
            ---
            title: Shared Doc
            produce_by:
              - wishes/active/wish-0003.md
              - wishes/active/wish-0001.md
              - wishes/active/wish-0002.md
            ---
            """);

        var builder = new DocumentGraphBuilder(_tempDir);

        // Act
        var graph = builder.Build();

        // Assert - ProducedBy应该按FilePath字典序排序
        var shared = graph.ByPath["docs/shared.md"];
        Assert.Equal(3, shared.ProducedBy.Count);
        Assert.Equal("wishes/active/wish-0001.md", shared.ProducedBy[0].FilePath);
        Assert.Equal("wishes/active/wish-0002.md", shared.ProducedBy[1].FilePath);
        Assert.Equal("wishes/active/wish-0003.md", shared.ProducedBy[2].FilePath);
    }

    #endregion

    #region 辅助方法

    private void CreateDirectory(string relativePath)
    {
        var fullPath = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(fullPath);
    }

    private void CreateFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_tempDir, relativePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (dir != null && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(fullPath, content);
    }

    #endregion
}
