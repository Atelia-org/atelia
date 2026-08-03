using Atelia.Testing;
using Xunit;

namespace Atelia.SessionJournal.Cli.Tests;

public sealed class CreateOnlyDirectorySnapshotTests {
    [Fact]
    public async Task SnapshotRemainsHiddenUntilCreateOnlyPublish() {
        using var fixture = new SnapshotFixture();
        string source = fixture.CreateSource();
        string destination = fixture.Path("output", "snapshot");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        DirectoryTreeFingerprint before =
            CreateOnlyDirectorySnapshot.Fingerprint(source);

        using CreateOnlyDirectorySnapshot prepared =
            await CreateOnlyDirectorySnapshot.PrepareAsync(
                source,
                destination,
                [],
                temporary => {
                    Assert.False(Path.Exists(destination));
                    Assert.Equal(
                        before,
                        CreateOnlyDirectorySnapshot.Fingerprint(temporary)
                    );
                    return ValueTask.CompletedTask;
                }
            );

        Assert.False(Path.Exists(destination));
        Assert.True(Directory.Exists(prepared.TemporaryPath));
        prepared.Publish();

        Assert.True(Directory.Exists(destination));
        Assert.Equal(
            before,
            CreateOnlyDirectorySnapshot.Fingerprint(destination)
        );
        Assert.Equal("payload", File.ReadAllText(Path.Combine(
            destination,
            "nested",
            "value.txt"
        )));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingDestinationIsRejectedWithoutOverwrite(
        bool existingDirectory
    ) {
        using var fixture = new SnapshotFixture();
        string source = fixture.CreateSource();
        string destination = fixture.Path("output", "snapshot");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (existingDirectory) {
            Directory.CreateDirectory(destination);
            File.WriteAllText(
                Path.Combine(destination, "sentinel.txt"),
                "existing"
            );
        }
        else {
            File.WriteAllText(destination, "existing");
        }

        await Assert.ThrowsAsync<IOException>(async () =>
            await CreateOnlyDirectorySnapshot.PrepareAsync(
                source,
                destination,
                [],
                _ => ValueTask.CompletedTask
            )
        );

        Assert.Equal(
            "existing",
            existingDirectory
                ? File.ReadAllText(Path.Combine(
                    destination,
                    "sentinel.txt"
                ))
                : File.ReadAllText(destination)
        );
    }

    [Theory]
    [InlineData("file")]
    [InlineData("empty-directory")]
    [InlineData("nonempty-directory")]
    public async Task PublishLosesLastMomentCreateOnlyRaceWithoutOverwritingWinner(
        string winnerKind
    ) {
        using var fixture = new SnapshotFixture();
        string source = fixture.CreateSource();
        string outputParent = fixture.Path("output");
        Directory.CreateDirectory(outputParent);
        string destination = Path.Combine(outputParent, "snapshot");
        using CreateOnlyDirectorySnapshot prepared =
            await CreateOnlyDirectorySnapshot.PrepareAsync(
                source,
                destination,
                [],
                _ => ValueTask.CompletedTask,
                new CreateOnlyDirectorySnapshotTestHooks(
                    BeforePublishRename: (_, target) => {
                        if (winnerKind == "file") {
                            File.WriteAllText(target, "winner");
                            return;
                        }
                        Directory.CreateDirectory(target);
                        if (winnerKind == "nonempty-directory") {
                            File.WriteAllText(
                                Path.Combine(target, "sentinel.txt"),
                                "winner"
                            );
                        }
                    }
                )
            );
        string temporary = prepared.TemporaryPath;

        Assert.Throws<IOException>(prepared.Publish);

        if (winnerKind == "file") {
            Assert.Equal("winner", File.ReadAllText(destination));
        }
        else {
            Assert.True(Directory.Exists(destination));
            Assert.Equal(
                winnerKind == "nonempty-directory",
                File.Exists(Path.Combine(destination, "sentinel.txt"))
            );
        }
        Assert.True(Directory.Exists(temporary));
        prepared.Dispose();
        Assert.False(Path.Exists(temporary));
        Assert.True(Path.Exists(destination));
    }

    [Fact]
    public async Task TemporaryDirectoryCompetitorIsNotClaimedOrDeleted() {
        using var fixture = new SnapshotFixture();
        string source = fixture.CreateSource();
        string outputParent = fixture.Path("output");
        Directory.CreateDirectory(outputParent);
        string destination = Path.Combine(outputParent, "snapshot");
        string temporaryName = ".owned-race.staging";
        string? competitor = null;

        await Assert.ThrowsAsync<IOException>(async () =>
            await CreateOnlyDirectorySnapshot.PrepareAsync(
                source,
                destination,
                [],
                _ => ValueTask.CompletedTask,
                new CreateOnlyDirectorySnapshotTestHooks(
                    TemporaryNameFactory: () => temporaryName,
                    BeforeTemporaryDirectoryCreate: path => {
                        competitor = path;
                        Directory.CreateDirectory(path);
                        File.WriteAllText(
                            Path.Combine(path, "sentinel.txt"),
                            "competitor"
                        );
                    }
                )
            )
        );

        Assert.NotNull(competitor);
        Assert.Equal(
            "competitor",
            File.ReadAllText(Path.Combine(competitor!, "sentinel.txt"))
        );
        Assert.False(Path.Exists(destination));
    }

    [Fact]
    public async Task TemporarySymlinkCompetitorIsNotDeletedOrTraversed() {
        using var fixture = new SnapshotFixture();
        string source = fixture.CreateSource();
        string outputParent = fixture.Path("output");
        Directory.CreateDirectory(outputParent);
        string destination = Path.Combine(outputParent, "snapshot");
        string external = fixture.Path("external");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "sentinel.txt"), "safe");
        string? competitor = null;

        await Assert.ThrowsAsync<IOException>(async () =>
            await CreateOnlyDirectorySnapshot.PrepareAsync(
                source,
                destination,
                [],
                _ => ValueTask.CompletedTask,
                new CreateOnlyDirectorySnapshotTestHooks(
                    TemporaryNameFactory: () => ".symlink-race.staging",
                    BeforeTemporaryDirectoryCreate: path => {
                        competitor = path;
                        Directory.CreateSymbolicLink(path, external);
                    }
                )
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
        Assert.False(Path.Exists(destination));
    }

    [Fact]
    public async Task MissingOutputParentIsRejectedWithoutCreatingIt() {
        using var fixture = new SnapshotFixture();
        string source = fixture.CreateSource();
        string outputParent = fixture.Path("missing-parent");
        string destination = Path.Combine(outputParent, "snapshot");

        await Assert.ThrowsAsync<DirectoryNotFoundException>(async () =>
            await CreateOnlyDirectorySnapshot.PrepareAsync(
                source,
                destination,
                [],
                _ => ValueTask.CompletedTask
            )
        );

        Assert.False(Path.Exists(outputParent));
    }

    [Fact]
    public async Task OverlappingOutputIsRejectedBeforeCopy() {
        using var fixture = new SnapshotFixture();
        string source = fixture.CreateSource();
        string destination = Path.Combine(source, "nested-output");

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await CreateOnlyDirectorySnapshot.PrepareAsync(
                source,
                destination,
                [],
                _ => ValueTask.CompletedTask
            )
        );

        Assert.False(Path.Exists(destination));
        Assert.Equal("payload", File.ReadAllText(Path.Combine(
            source,
            "nested",
            "value.txt"
        )));
    }

    [Fact]
    public async Task OutputNestedUnderProtectedOldBaseIsRejected() {
        using var fixture = new SnapshotFixture();
        string source = fixture.CreateSource();
        string oldBase = fixture.Path("old-base");
        Directory.CreateDirectory(oldBase);
        string destination = Path.Combine(oldBase, "snapshot");

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await CreateOnlyDirectorySnapshot.PrepareAsync(
                source,
                destination,
                [oldBase],
                _ => ValueTask.CompletedTask
            )
        );

        Assert.Empty(Directory.EnumerateFileSystemEntries(oldBase));
    }

    [Fact]
    public async Task ReparsePointInSourceTreeIsRejectedWithoutTraversal() {
        using var fixture = new SnapshotFixture();
        string source = fixture.CreateSource();
        string external = fixture.Path("external");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "sentinel.txt"), "safe");
        Directory.CreateSymbolicLink(
            Path.Combine(source, "linked"),
            external
        );
        string destination = fixture.Path("output", "snapshot");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await CreateOnlyDirectorySnapshot.PrepareAsync(
                source,
                destination,
                [],
                _ => ValueTask.CompletedTask
            )
        );

        Assert.False(Path.Exists(destination));
        Assert.Equal(
            "safe",
            File.ReadAllText(Path.Combine(external, "sentinel.txt"))
        );
    }

    [Fact]
    public async Task ReparsePointInOutputPathChainIsRejectedBeforeCopy() {
        using var fixture = new SnapshotFixture();
        string source = fixture.CreateSource();
        string realOutputParent = fixture.Path("real-output");
        Directory.CreateDirectory(realOutputParent);
        string linkedOutputParent = fixture.Path("linked-output");
        Directory.CreateSymbolicLink(
            linkedOutputParent,
            realOutputParent
        );
        string destination = Path.Combine(
            linkedOutputParent,
            "snapshot"
        );

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await CreateOnlyDirectorySnapshot.PrepareAsync(
                source,
                destination,
                [],
                _ => ValueTask.CompletedTask
            )
        );

        Assert.Empty(Directory.EnumerateFileSystemEntries(realOutputParent));
    }

    [Fact]
    public async Task ValidationFailureCleansHiddenCopyAndDoesNotPublish() {
        using var fixture = new SnapshotFixture();
        string source = fixture.CreateSource();
        string outputParent = fixture.Path("output");
        Directory.CreateDirectory(outputParent);
        string destination = Path.Combine(outputParent, "snapshot");

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await CreateOnlyDirectorySnapshot.PrepareAsync(
                source,
                destination,
                [],
                _ => throw new InvalidDataException("validation failed")
            )
        );

        Assert.False(Path.Exists(destination));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outputParent));
    }

    [Fact]
    public async Task ValidationInsertedSymlinkIsUnlinkedWithoutTouchingExternalTree() {
        using var fixture = new SnapshotFixture();
        string source = fixture.CreateSource();
        string outputParent = fixture.Path("output");
        Directory.CreateDirectory(outputParent);
        string destination = Path.Combine(outputParent, "snapshot");
        string external = fixture.Path("external");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "sentinel.txt"), "safe");

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await CreateOnlyDirectorySnapshot.PrepareAsync(
                source,
                destination,
                [],
                temporary => {
                    Directory.CreateSymbolicLink(
                        Path.Combine(temporary, "validation-link"),
                        external
                    );
                    return ValueTask.CompletedTask;
                }
            )
        );

        Assert.False(Path.Exists(destination));
        Assert.Equal(
            "safe",
            File.ReadAllText(Path.Combine(external, "sentinel.txt"))
        );
        Assert.Empty(Directory.EnumerateFileSystemEntries(outputParent));
    }

    [Fact]
    public async Task DisposingPreparedSnapshotAfterLaterFailureDoesNotPublish() {
        using var fixture = new SnapshotFixture();
        string source = fixture.CreateSource();
        string outputParent = fixture.Path("output");
        Directory.CreateDirectory(outputParent);
        string destination = Path.Combine(outputParent, "snapshot");
        CreateOnlyDirectorySnapshot prepared =
            await CreateOnlyDirectorySnapshot.PrepareAsync(
                source,
                destination,
                [],
                _ => ValueTask.CompletedTask
            );
        string temporary = prepared.TemporaryPath;

        prepared.Dispose();

        Assert.False(Path.Exists(destination));
        Assert.False(Path.Exists(temporary));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outputParent));
    }

    private sealed class SnapshotFixture : IDisposable {
        private readonly string _root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "atelia-create-only-directory-snapshot-tests",
            Guid.NewGuid().ToString("N")
        );

        internal string Path(params string[] parts) =>
            System.IO.Path.Combine([_root, .. parts]);

        internal string CreateSource() {
            string source = Path("source");
            Directory.CreateDirectory(System.IO.Path.Combine(
                source,
                "nested"
            ));
            Directory.CreateDirectory(System.IO.Path.Combine(
                source,
                "empty"
            ));
            File.WriteAllText(
                System.IO.Path.Combine(
                    source,
                    "nested",
                    "value.txt"
                ),
                "payload"
            );
            return source;
        }

        public void Dispose() {
            TestDirectorySafety.DeleteOwnedTreeNoFollow(_root);
        }
    }
}
