// DocGraph v0.2 - CLI 命令测试
// 参考：spec.md §11 验收标准
// 注意：v0.2 使用 Wish Instance Directory 布局（wish/W-XXXX-slug/wish.md）

using System.CommandLine;
using Atelia.DocGraph.Commands;
using Atelia.DocGraph.Core;
using FluentAssertions;
using Xunit;

namespace Atelia.DocGraph.Tests;

/// <summary>
/// CLI 命令集成测试。
/// </summary>
public class CommandTests : IDisposable
{
    private readonly string _testDir;

    public CommandTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"docgraph-cmd-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, recursive: true);
        }
    }

    #region ValidateCommand Tests

    [Fact]
    public async Task ValidateCommand_EmptyWorkspace_Returns0()
    {
        // Arrange - v0.2: 使用 wish 目录
        var wishDir = Path.Combine(_testDir, "wish");
        Directory.CreateDirectory(wishDir);

        var command = new ValidateCommand();
        var rootCommand = new RootCommand { command };

        // Act
        var exitCode = await rootCommand.InvokeAsync(new[] { "validate", _testDir });

        // Assert
        exitCode.Should().Be(0, "空工作区应返回成功");
    }

    [Fact]
    public async Task ValidateCommand_NonExistentPath_Returns3()
    {
        // Arrange
        var nonExistent = Path.Combine(_testDir, "nonexistent");
        var command = new ValidateCommand();
        var rootCommand = new RootCommand { command };

        // Act
        var exitCode = await rootCommand.InvokeAsync(new[] { "validate", nonExistent });

        // Assert
        exitCode.Should().Be(3, "不存在的路径应返回Fatal");
    }

    [Fact]
    public async Task ValidateCommand_ValidWorkspace_Returns0()
    {
        // Arrange
        SetupValidWorkspace();

        var command = new ValidateCommand();
        var rootCommand = new RootCommand { command };

        // Act
        var exitCode = await rootCommand.InvokeAsync(new[] { "validate", _testDir });

        // Assert
        exitCode.Should().Be(0, "有效工作区应返回成功");
    }

    [Fact]
    public async Task ValidateCommand_WithWarnings_Returns1()
    {
        // Arrange
        SetupWorkspaceWithWarnings();

        var command = new ValidateCommand();
        var rootCommand = new RootCommand { command };

        // Act
        var exitCode = await rootCommand.InvokeAsync(new[] { "validate", _testDir });

        // Assert
        exitCode.Should().Be(1, "有警告应返回1");
    }

    [Fact]
    public async Task ValidateCommand_VerboseOption_ShowsDetails()
    {
        // Arrange
        SetupValidWorkspace();

        var command = new ValidateCommand();
        var rootCommand = new RootCommand { command };
        var output = new StringWriter();
        Console.SetOut(output);

        // Act
        await rootCommand.InvokeAsync(new[] { "validate", _testDir, "--verbose" });
        var result = output.ToString();

        // Assert
        result.Should().Contain("扫描目录", "详细模式应显示扫描信息");
    }

    #endregion

    #region RunCommand Tests

    [Fact]
    public async Task RunCommand_ValidWorkspace_GeneratesReachableDocumentsPanel()
    {
        // Arrange
        SetupValidWorkspace();

        var command = new RunCommand();
        var rootCommand = new RootCommand { command };

        // Act
        var exitCode = await rootCommand.InvokeAsync(new[] { "run", _testDir, "--yes" });

        // Assert
        exitCode.Should().Be(0, "全流程在有效工作区应成功完成");

        var outputFile = Path.Combine(_testDir, "wish-panels", "reachable-documents.gen.md");
        File.Exists(outputFile).Should().BeTrue("RunCommand 应生成 reachable-documents 面板");

        var content = File.ReadAllText(outputFile);
        content.Should().Contain("<!-- 本文档由 DocGraph 工具自动生成", "生成文件必须标记为工具生成");
        content.Should().Contain("wish/W-0001-test/wish.md", "输出应包含 Wish root 路径");
    }

    #endregion

    #region FixCommand Tests

    [Fact]
    public async Task FixCommand_DryRun_DoesNotCreateFiles()
    {
        // Arrange
        SetupWorkspaceWithDanglingLink();
        var targetFile = Path.Combine(_testDir, "docs", "missing.md");
        File.Exists(targetFile).Should().BeFalse("目标文件应不存在");

        var command = new FixCommand();
        var rootCommand = new RootCommand { command };

        // Act
        await rootCommand.InvokeAsync(new[] { "fix", _testDir, "--dry-run" });

        // Assert
        File.Exists(targetFile).Should().BeFalse("dry-run 模式不应创建文件");
    }

    [Fact]
    public async Task FixCommand_WithYes_CreatesFiles()
    {
        // Arrange
        SetupWorkspaceWithDanglingLink();
        var targetFile = Path.Combine(_testDir, "docs", "missing.md");
        File.Exists(targetFile).Should().BeFalse("目标文件应不存在");

        var command = new FixCommand();
        var rootCommand = new RootCommand { command };

        // Act
        await rootCommand.InvokeAsync(new[] { "fix", _testDir, "--yes" });

        // Assert
        File.Exists(targetFile).Should().BeTrue("--yes 模式应创建缺失文件");
    }

    [Fact]
    public async Task FixCommand_NonExistentPath_Returns3()
    {
        // Arrange
        var nonExistent = Path.Combine(_testDir, "nonexistent");
        var command = new FixCommand();
        var rootCommand = new RootCommand { command };

        // Act
        var exitCode = await rootCommand.InvokeAsync(new[] { "fix", nonExistent });

        // Assert
        exitCode.Should().Be(3, "不存在的路径应返回Fatal");
    }

    #endregion

    #region StatsCommand Tests

    [Fact]
    public async Task StatsCommand_EmptyWorkspace_Returns0()
    {
        // Arrange - v0.2: 使用 wish 目录
        var wishDir = Path.Combine(_testDir, "wish");
        Directory.CreateDirectory(wishDir);

        var command = new StatsCommand();
        var rootCommand = new RootCommand { command };

        // Act
        var exitCode = await rootCommand.InvokeAsync(new[] { "stats", _testDir });

        // Assert
        exitCode.Should().Be(0, "统计命令应始终返回成功");
    }

    [Fact]
    public async Task StatsCommand_ValidWorkspace_ShowsStats()
    {
        // Arrange
        SetupValidWorkspace();

        var command = new StatsCommand();
        var rootCommand = new RootCommand { command };
        var output = new StringWriter();
        Console.SetOut(output);

        // Act
        await rootCommand.InvokeAsync(new[] { "stats", _testDir });
        var result = output.ToString();

        // Assert
        result.Should().Contain("文档统计", "应显示文档统计");
        result.Should().Contain("Wish 文档", "应显示 Wish 文档计数");
    }

    [Fact]
    public async Task StatsCommand_VerboseOption_ShowsDetails()
    {
        // Arrange
        SetupValidWorkspace();

        var command = new StatsCommand();
        var rootCommand = new RootCommand { command };
        var output = new StringWriter();
        Console.SetOut(output);

        // Act
        await rootCommand.InvokeAsync(new[] { "stats", _testDir, "--verbose" });
        var result = output.ToString();

        // Assert
        result.Should().Contain("文档详情", "详细模式应显示文档详情");
    }

    [Fact]
    public async Task StatsCommand_JsonOption_OutputsJson()
    {
        // Arrange
        SetupValidWorkspace();

        var command = new StatsCommand();
        var rootCommand = new RootCommand { command };
        var output = new StringWriter();
        Console.SetOut(output);

        // Act
        await rootCommand.InvokeAsync(new[] { "stats", _testDir, "--json" });
        var result = output.ToString();

        // Assert
        result.Should().Contain("\"totalDocuments\":", "JSON 模式应输出有效 JSON");
        result.Should().Contain("\"wishDocuments\":", "JSON 应包含 Wish 文档计数");
    }

    [Fact]
    public async Task StatsCommand_NonExistentPath_Returns3()
    {
        // Arrange
        var nonExistent = Path.Combine(_testDir, "nonexistent");
        var command = new StatsCommand();
        var rootCommand = new RootCommand { command };

        // Act
        var exitCode = await rootCommand.InvokeAsync(new[] { "stats", nonExistent });

        // Assert
        exitCode.Should().Be(3, "不存在的路径应返回Fatal");
    }

    #endregion

    #region Exit Code Tests

    [Fact]
    public async Task ExitCode_Success_Is0()
    {
        // Arrange
        SetupValidWorkspace();

        var command = new ValidateCommand();
        var rootCommand = new RootCommand { command };

        // Act
        var exitCode = await rootCommand.InvokeAsync(new[] { "validate", _testDir });

        // Assert
        exitCode.Should().Be(0, "成功验证应返回0");
    }

    [Fact]
    public async Task ExitCode_Warning_Is1()
    {
        // Arrange
        SetupWorkspaceWithWarnings();

        var command = new ValidateCommand();
        var rootCommand = new RootCommand { command };

        // Act
        var exitCode = await rootCommand.InvokeAsync(new[] { "validate", _testDir });

        // Assert
        exitCode.Should().Be(1, "有警告应返回1");
    }

    [Fact]
    public async Task ExitCode_Warning_WithMissingFields_Is1()
    {
        // Arrange - 缺少必填字段现在是 Warning 而非 Error
        SetupWorkspaceWithErrors();

        var command = new ValidateCommand();
        var rootCommand = new RootCommand { command };

        // Act
        var exitCode = await rootCommand.InvokeAsync(new[] { "validate", _testDir });

        // Assert
        exitCode.Should().Be(1, "有警告应返回1（必填字段缺失已降级为 Warning）");
    }

    [Fact]
    public async Task ExitCode_Fatal_Is3()
    {
        // Arrange
        var nonExistent = Path.Combine(_testDir, "nonexistent");

        var command = new ValidateCommand();
        var rootCommand = new RootCommand { command };

        // Act
        var exitCode = await rootCommand.InvokeAsync(new[] { "validate", nonExistent });

        // Assert
        exitCode.Should().Be(3, "致命错误应返回3");
    }

    #endregion

    #region JSON Output Tests

    [Fact]
    public async Task ValidateCommand_JsonOutput_ReturnsValidJson()
    {
        // Arrange
        SetupValidWorkspace();

        var command = new ValidateCommand();
        var rootCommand = new RootCommand { command };
        var output = new StringWriter();
        Console.SetOut(output);

        // Act
        await rootCommand.InvokeAsync(new[] { "validate", _testDir, "--output", "json" });
        var result = output.ToString();

        // Assert
        result.Should().Contain("\"isValid\":", "JSON 输出应包含 isValid 字段");
        result.Should().Contain("\"statistics\":", "JSON 输出应包含 statistics 字段");
        result.Should().Contain("\"issues\":", "JSON 输出应包含 issues 字段");
    }

    [Fact]
    public async Task ValidateCommand_JsonOutput_ContainsIssues()
    {
        // Arrange
        SetupWorkspaceWithWarnings();

        var command = new ValidateCommand();
        var rootCommand = new RootCommand { command };
        var output = new StringWriter();
        Console.SetOut(output);

        // Act
        await rootCommand.InvokeAsync(new[] { "validate", _testDir, "-o", "json" });
        var result = output.ToString();

        // Assert
        result.Should().Contain("\"errorCode\":", "JSON 输出应包含错误码");
        result.Should().Contain("\"severity\":", "JSON 输出应包含严重度");
    }

    #endregion

    #region Helper Methods

    private void SetupValidWorkspace()
    {
        // v0.2: 使用 wish 实例目录布局
        var wishDir = Path.Combine(_testDir, "wish", "W-0001-test");
        var docsDir = Path.Combine(_testDir, "docs");
        Directory.CreateDirectory(wishDir);
        Directory.CreateDirectory(docsDir);

        // 创建 Wish 文档
        var wishContent = """
            ---
            wishId: "W-0001"
            title: "测试需求"
            status: Active
            produce:
              - docs/api.md
            ---
            # 测试需求
            """;
        File.WriteAllText(Path.Combine(wishDir, "wish.md"), wishContent);

        // 创建产物文档
        var apiContent = """
            ---
            docId: api
            title: "API 文档"
            produce_by:
              - wish/W-0001-test/wish.md
            ---
            # API 文档
            """;
        File.WriteAllText(Path.Combine(docsDir, "api.md"), apiContent);
    }

    private void SetupWorkspaceWithWarnings()
    {
        // v0.2: 使用 wish 实例目录布局
        // 创建有警告的工作区（缺少 backlink）
        var wishDir = Path.Combine(_testDir, "wish", "W-0001-test");
        var docsDir = Path.Combine(_testDir, "docs");
        Directory.CreateDirectory(wishDir);
        Directory.CreateDirectory(docsDir);

        // Wish 文档引用产物
        var wishContent = """
            ---
            wishId: "W-0001"
            title: "测试需求"
            status: Active
            produce:
              - docs/api.md
            ---
            # 测试需求
            """;
        File.WriteAllText(Path.Combine(wishDir, "wish.md"), wishContent);

        // 产物文档 produce_by 指向错误的文件（Warning - DANGLING_BACKLINK）
        var apiContent = """
            ---
            docId: api
            title: "API 文档"
            produce_by:
              - wish/W-0099-other/wish.md
            ---
            # API 文档
            """;
        File.WriteAllText(Path.Combine(docsDir, "api.md"), apiContent);
    }

    private void SetupWorkspaceWithErrors()
    {
        // v0.2: 使用 wish 实例目录布局
        // 创建有警告的工作区（缺少必填字段）
        // 注：必填字段缺失已降级为 Warning
        var wishDir = Path.Combine(_testDir, "wish", "W-0001-test");
        Directory.CreateDirectory(wishDir);

        // Wish 文档缺少 produce 字段（Warning）
        var wishContent = """
            ---
            wishId: "W-0001"
            title: "测试需求"
            status: Active
            ---
            # 测试需求
            """;
        File.WriteAllText(Path.Combine(wishDir, "wish.md"), wishContent);
    }

    private void SetupWorkspaceWithDanglingLink()
    {
        // v0.2: 使用 wish 实例目录布局
        // 创建有悬空引用的工作区
        var wishDir = Path.Combine(_testDir, "wish", "W-0001-test");
        Directory.CreateDirectory(wishDir);

        // Wish 文档引用不存在的文件
        var wishContent = """
            ---
            wishId: "W-0001"
            title: "测试需求"
            status: Active
            produce:
              - docs/missing.md
            ---
            # 测试需求
            """;
        File.WriteAllText(Path.Combine(wishDir, "wish.md"), wishContent);
    }

    #endregion
}
