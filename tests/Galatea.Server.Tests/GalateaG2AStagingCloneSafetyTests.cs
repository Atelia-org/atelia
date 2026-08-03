using Atelia.Testing;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaG2AStagingCloneSafetyTests {
    [Fact]
    public void SourceAncestorOfMissingCloneParentIsRejectedWithoutCreation() {
        using var fixture = new CloneSafetyFixture();
        string source = fixture.CreateSource();
        string cloneParent = Path.Combine(
            source,
            "must-not-be-created"
        );

        Assert.Throws<ArgumentException>(() =>
            GalateaG2AStagingHostAcceptanceTests.StagingClone
                .CreateFrom(
                    source,
                    cloneParentOverride: cloneParent
                )
        );

        Assert.False(Path.Exists(cloneParent));
        Assert.Equal(
            "payload",
            File.ReadAllText(Path.Combine(source, "value.txt"))
        );
        Assert.Equal(
            Path.Combine(source, "value.txt"),
            Assert.Single(
                Directory.EnumerateFileSystemEntries(source)
            )
        );
    }

    [Fact]
    public void CompetitorCloneRootDirectoryIsNotClaimedOrDeleted() {
        using var fixture = new CloneSafetyFixture();
        string source = fixture.CreateSource();
        string rootName = "competitor-" + Guid.NewGuid().ToString("N");
        string? competitor = null;
        try {
            Assert.Throws<IOException>(() =>
                GalateaG2AStagingHostAcceptanceTests.StagingClone
                    .CreateFrom(
                        source,
                        () => rootName,
                        path => {
                            competitor = path;
                            Directory.CreateDirectory(path);
                            File.WriteAllText(
                                Path.Combine(path, "sentinel.txt"),
                                "competitor"
                            );
                        }
                    )
            );

            Assert.NotNull(competitor);
            Assert.Equal(
                "competitor",
                File.ReadAllText(Path.Combine(
                    competitor!,
                    "sentinel.txt"
                ))
            );
            Assert.False(Directory.Exists(Path.Combine(
                competitor!,
                "session-clone"
            )));
        }
        finally {
            if (competitor is not null) {
                TestDirectorySafety.DeleteOwnedTreeNoFollow(competitor);
            }
        }
    }

    [Fact]
    public void CompetitorCloneRootSymlinkIsNotDeletedOrTraversed() {
        using var fixture = new CloneSafetyFixture();
        string source = fixture.CreateSource();
        string external = fixture.Path("external");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "sentinel.txt"), "safe");
        string rootName = "symlink-" + Guid.NewGuid().ToString("N");
        string? competitor = null;
        try {
            Assert.Throws<IOException>(() =>
                GalateaG2AStagingHostAcceptanceTests.StagingClone
                    .CreateFrom(
                        source,
                        () => rootName,
                        path => {
                            competitor = path;
                            Directory.CreateSymbolicLink(path, external);
                        }
                    )
            );

            Assert.NotNull(competitor);
            Assert.True(
                (File.GetAttributes(competitor!)
                    & FileAttributes.ReparsePoint) != 0
            );
            Assert.Equal(
                "safe",
                File.ReadAllText(Path.Combine(external, "sentinel.txt"))
            );
            Assert.False(Directory.Exists(Path.Combine(
                external,
                "session-clone"
            )));
        }
        finally {
            if (competitor is not null) {
                TestDirectorySafety.DeleteOwnedTreeNoFollow(competitor);
            }
        }
    }

    private sealed class CloneSafetyFixture : IDisposable {
        private readonly string _root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "atelia-g2a-clone-safety-tests",
            Guid.NewGuid().ToString("N")
        );

        internal string Path(params string[] parts) =>
            System.IO.Path.Combine([_root, .. parts]);

        internal string CreateSource() {
            string source = Path("source");
            Directory.CreateDirectory(source);
            File.WriteAllText(
                System.IO.Path.Combine(source, "value.txt"),
                "payload"
            );
            return source;
        }

        public void Dispose() {
            TestDirectorySafety.DeleteOwnedTreeNoFollow(_root);
        }
    }
}
