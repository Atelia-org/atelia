using System.CommandLine;
using Atelia.StateJournal;
using Xunit;

namespace Atelia.TextAdv.Tests;

public sealed class GameEntryVersionTests : IDisposable {
    private readonly string _repoDir;

    public GameEntryVersionTests() {
        _repoDir = Path.Combine(Path.GetTempPath(), $"textadv-version-test-{Guid.NewGuid():N}");
    }

    public void Dispose() {
        TextAdvRuntimeEnvironment.SetRepoDirOverride(null);
        if (Directory.Exists(_repoDir)) {
            Directory.Delete(_repoDir, recursive: true);
        }
    }

    [Fact]
    public void BuildGame_ShouldExposeLoadVersionCommand() {
        var root = GameEntry.BuildGame();

        Assert.Contains(root.Subcommands, static cmd => cmd.Name == "load-version");
    }

    [Fact]
    public void LoadVersionAsNewBranch_ShouldCreateNewBranchFromHistoricalVersion() {
        TextAdvRuntimeEnvironment.SetRepoDirOverride(_repoDir);

        var repo = AssertSuccess(Repository.Create(_repoDir));
        var root = GameSimulation.CreateNewWorld(repo);
        Assert.True(repo.TryGetBranchHeadAddress("main", out var firstVersion));

        var moveResult = GameSimulation.MovePlayer(root, "north");
        Assert.True(moveResult.IsSuccess, $"Move failed: {moveResult.Error}");
        var latestMainHead = AssertSuccess(repo.Commit(root));
        repo.Dispose();

        var session = AssertSuccess(TextAdvSession.Load(_repoDir));
        var loadResult = session.LoadVersionAsNewBranch(firstVersion);
        Assert.True(loadResult.IsSuccess, $"LoadVersionAsNewBranch failed: {loadResult.Error}");

        var loaded = loadResult.Value;
        Assert.Equal("beach", GameSimulation.DescribeCurrentPerception(loaded.Root).Location.LocationId);
        Assert.Equal(firstVersion, loaded.Root.Revision.HeadAddress);
        Assert.NotEqual("main", loaded.BranchName);

        // main 保持在最新位置，证明 load-version 走的是“从旧版本派生新 branch”而不是回退 main。
        var reopenedMain = AssertSuccess(session.Repo.CheckoutBranch("main"));
        var reopenedMainRoot = Assert.IsAssignableFrom<DurableDict<string>>(reopenedMain.GraphRoot);
        Assert.Equal("forest", GameSimulation.DescribeCurrentPerception(reopenedMainRoot).Location.LocationId);
        Assert.True(session.Repo.TryGetBranchHeadAddress("main", out var mainHeadAfterLoad));
        Assert.Equal(latestMainHead, mainHeadAfterLoad);
        Assert.True(session.Repo.TryGetBranchHeadAddress(loaded.BranchName, out var loadedBranchHead));
        Assert.Equal(firstVersion, loadedBranchHead);
        session.Repo.Dispose();
    }

    [Fact]
    public void LoadVersionAsNewBranch_ShouldAllowContinuingOnLoadedBranchWithoutTouchingMain() {
        TextAdvRuntimeEnvironment.SetRepoDirOverride(_repoDir);

        var repo = AssertSuccess(Repository.Create(_repoDir));
        var root = GameSimulation.CreateNewWorld(repo);
        Assert.True(repo.TryGetBranchHeadAddress("main", out var firstVersion));

        var moveResult = GameSimulation.MovePlayer(root, "north");
        Assert.True(moveResult.IsSuccess, $"Move failed: {moveResult.Error}");
        var latestMainHead = AssertSuccess(repo.Commit(root));
        repo.Dispose();

        var session = AssertSuccess(TextAdvSession.Load(_repoDir));
        var loadResult = session.LoadVersionAsNewBranch(firstVersion);
        Assert.True(loadResult.IsSuccess, $"LoadVersionAsNewBranch failed: {loadResult.Error}");

        var loaded = loadResult.Value;
        Assert.Equal("beach", GameSimulation.DescribeCurrentPerception(loaded.Root).Location.LocationId);
        Assert.True(session.Repo.TryGetBranchHeadAddress("main", out var mainHeadAfterLoad));
        Assert.Equal(latestMainHead, mainHeadAfterLoad);

        var secondMoveResult = GameSimulation.MovePlayer(loaded.Root, "north");
        Assert.True(secondMoveResult.IsSuccess, $"Move after rewind failed: {secondMoveResult.Error}");
        var loadedHead = AssertSuccess(session.Repo.Commit(loaded.Root));

        Assert.Equal("forest", GameSimulation.DescribeCurrentPerception(loaded.Root).Location.LocationId);
        Assert.True(session.Repo.TryGetBranchHeadAddress(loaded.BranchName, out var loadedBranchHead));
        Assert.Equal(loadedHead, loadedBranchHead);
        Assert.True(session.Repo.TryGetBranchHeadAddress("main", out var mainHeadAfterCommit));
        Assert.Equal(latestMainHead, mainHeadAfterCommit);
        session.Repo.Dispose();
    }

    [Fact]
    public void SessionStore_ShouldPersistCurrentBranchAndHelpModeOutsideWorldState() {
        var state = new TextAdvSessionState {
            CurrentBranchName = "loads/test-branch",
            HelpMode = TerminalHelpMode.On.ToString()
        };

        TextAdvSessionStore.Save(_repoDir, state);
        var reloaded = TextAdvSessionStore.Load(_repoDir);

        Assert.Equal("loads/test-branch", reloaded.CurrentBranchName);
        Assert.Equal(TerminalHelpMode.On, reloaded.GetNormalizedHelpMode());
    }

    [Fact]
    public void TextAdvSession_CreateNewAndReload_ShouldRoundTripSessionMetadata() {
        var created = AssertSuccess(TextAdvSession.CreateNew(_repoDir));
        Assert.Equal(TextAdvSessionState.DefaultBranchName, created.BranchName);
        Assert.Equal(TerminalHelpMode.Off, created.HelpMode);

        var persisted = AssertSuccess(created.WithHelpMode(TerminalHelpMode.On).Persist(_repoDir));
        persisted.Repo.Dispose();
        var reloaded = AssertSuccess(TextAdvSession.Load(_repoDir));

        Assert.Equal(persisted.BranchName, reloaded.BranchName);
        Assert.Equal(TerminalHelpMode.On, reloaded.HelpMode);
        Assert.Equal("beach", GameSimulation.DescribeCurrentPerception(reloaded.Root).Location.LocationId);

        reloaded.Repo.Dispose();
    }

    [Fact]
    public void TextAdvSession_Load_WhenSessionBranchMissing_ShouldFailFastWithoutFallingBackToMain() {
        var created = AssertSuccess(TextAdvSession.CreateNew(_repoDir));
        AssertSuccess(created.Repo.CreateBranch("feature/replay"));
        AssertSuccess(created.WithHelpMode(TerminalHelpMode.On).Persist(_repoDir));
        created.Repo.Dispose();

        TextAdvSessionStore.Save(_repoDir, new TextAdvSessionState {
            CurrentBranchName = "feature/missing",
            HelpMode = TerminalHelpMode.On.ToString()
        });

        var loadResult = TextAdvSession.Load(_repoDir);

        Assert.True(loadResult.IsFailure);
        Assert.Equal("TextAdv.SessionBranchMissing", loadResult.Error!.ErrorCode);
        Assert.Contains("feature/missing", loadResult.Error.Message);

        var reloadedState = TextAdvSessionStore.Load(_repoDir);
        Assert.Equal("feature/missing", reloadedState.CurrentBranchName);

        using var reopenedRepo = AssertSuccess(Repository.Open(_repoDir));
        Assert.True(reopenedRepo.HasBranch("main"));
    }

    private static T AssertSuccess<T>(AteliaResult<T> result) where T : notnull {
        Assert.True(result.IsSuccess, $"Expected success but got error: {result.Error}");
        return result.Value!;
    }
}
