using Atelia.Testing;
using Xunit;

namespace Atelia.SessionJournal.Cli.Tests;

public sealed class TestDirectorySafetyTests {
    [Fact]
    public void FilesystemRootContainsDescendantInOnlyOneDirection() {
        string root = Path.GetPathRoot(Path.GetTempPath())
            ?? throw new InvalidOperationException(
                "The temp path has no filesystem root."
            );
        string descendant = Path.Combine(
            root,
            "atelia-test-directory-safety-descendant"
        );

        Assert.True(TestDirectorySafety.IsAncestor(root, descendant));
        Assert.False(TestDirectorySafety.IsAncestor(descendant, root));
        Assert.Throws<ArgumentException>(() =>
            TestDirectorySafety.EnsureDisjoint(root, descendant)
        );
        Assert.Throws<ArgumentException>(() =>
            TestDirectorySafety.EnsureDisjoint(descendant, root)
        );
    }

    [Fact]
    public void FilesystemRootChildrenAreSiblingsNotAncestors() {
        string root = Path.GetPathRoot(Path.GetTempPath())
            ?? throw new InvalidOperationException(
                "The temp path has no filesystem root."
            );
        string first = Path.Combine(root, "atelia-path-sibling-a");
        string second = Path.Combine(root, "atelia-path-sibling-b");

        Assert.False(TestDirectorySafety.IsAncestor(first, second));
        Assert.False(TestDirectorySafety.IsAncestor(second, first));
        TestDirectorySafety.EnsureDisjoint(first, second);
    }
}
