// DocGraph v0.1 - PathNormalizer 测试
// 参考：spec.md §3.1-3.3 路径处理约束

using Atelia.DocGraph.Utils;
using Xunit;

namespace Atelia.DocGraph.Tests;

public class PathNormalizerTests
{
    #region Normalize Tests

    [Theory]
    [InlineData("path/to/file.md", "path/to/file.md")]
    [InlineData("path\\to\\file.md", "path/to/file.md")]
    [InlineData("./path/to/file.md", "path/to/file.md")]
    [InlineData("path/./to/./file.md", "path/to/file.md")]
    [InlineData("path/to/../file.md", "path/file.md")]
    [InlineData("a/b/c/../d/e/../f.md", "a/b/d/f.md")]
    public void Normalize_ShouldHandleValidPaths(string input, string expected)
    {
        var result = PathNormalizer.Normalize(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_ShouldReturnNullForEmptyOrWhitespace(string? input)
    {
        var result = PathNormalizer.Normalize(input);
        Assert.Null(result);
    }

    [Fact]
    public void Normalize_ShouldRemoveTrailingSlash()
    {
        var result = PathNormalizer.Normalize("path/to/dir/");
        Assert.Equal("path/to/dir", result);
    }

    #endregion

    #region IsWithinWorkspace Tests

    [Theory]
    [InlineData("path/to/file.md")]
    [InlineData("./path/to/file.md")]
    [InlineData("a/b/../c/file.md")]
    public void IsWithinWorkspace_ShouldReturnTrueForValidPaths(string path)
    {
        var result = PathNormalizer.IsWithinWorkspace(path, "/workspace");
        Assert.True(result);
    }

    [Theory]
    [InlineData("../outside.md")]
    [InlineData("a/../../outside.md")]
    [InlineData("a/b/../../../outside.md")]
    public void IsWithinWorkspace_ShouldReturnFalseForOutOfBoundsPaths(string path)
    {
        var result = PathNormalizer.IsWithinWorkspace(path, "/workspace");
        Assert.False(result);
    }

    [Theory]
    [InlineData("/absolute/path.md")]
    [InlineData("C:\\Windows\\path.md")]
    [InlineData("file:///network/share.md")]
    public void IsWithinWorkspace_ShouldReturnFalseForAbsolutePaths(string path)
    {
        var result = PathNormalizer.IsWithinWorkspace(path, "/workspace");
        Assert.False(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsWithinWorkspace_ShouldReturnFalseForEmptyPaths(string? path)
    {
        var result = PathNormalizer.IsWithinWorkspace(path, "/workspace");
        Assert.False(result);
    }

    #endregion

    #region ToWorkspaceRelative Tests

    [Fact]
    public void ToWorkspaceRelative_ShouldConvertAbsoluteToRelative()
    {
        var workspaceRoot = "/repos/focus";
        var absolutePath = "/repos/focus/docs/api.md";

        var result = PathNormalizer.ToWorkspaceRelative(absolutePath, workspaceRoot);

        Assert.Equal("docs/api.md", result);
    }

    [Fact]
    public void ToWorkspaceRelative_ShouldReturnNullForPathOutsideWorkspace()
    {
        var workspaceRoot = "/repos/focus";
        var absolutePath = "/repos/other/docs/api.md";

        var result = PathNormalizer.ToWorkspaceRelative(absolutePath, workspaceRoot);

        Assert.Null(result);
    }

    #endregion

    #region ToAbsolute Tests

    [Fact]
    public void ToAbsolute_ShouldConvertRelativeToAbsolute()
    {
        var workspaceRoot = "/repos/focus";
        var relativePath = "docs/api.md";

        var result = PathNormalizer.ToAbsolute(relativePath, workspaceRoot);

        Assert.NotNull(result);
        Assert.EndsWith("docs/api.md", result.Replace('\\', '/'));
    }

    [Fact]
    public void ToAbsolute_ShouldReturnNullForOutOfBoundsPath()
    {
        var workspaceRoot = "/repos/focus";
        var relativePath = "../outside.md";

        var result = PathNormalizer.ToAbsolute(relativePath, workspaceRoot);

        Assert.Null(result);
    }

    #endregion
}
