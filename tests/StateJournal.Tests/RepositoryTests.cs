using Xunit;

namespace Atelia.StateJournal.Tests;

public class RepositoryTests : IDisposable {
    private readonly List<string> _tempDirs = new();

    private string GetTempDir() {
        var path = Path.Combine(Path.GetTempPath(), $"repo-test-{Guid.NewGuid()}");
        _tempDirs.Add(path);
        return path;
    }

    public void Dispose() {
        foreach (var dir in _tempDirs) {
            try { if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); } } catch { }
        }
    }

    [Fact]
    public void Create_NewDirectory_Succeeds() {
        var dir = GetTempDir();
        using var repo = AssertSuccess(Repository.Create(dir));

        Assert.True(Directory.Exists(dir));
        Assert.True(Directory.Exists(Path.Combine(dir, "refs")));
        Assert.True(Directory.Exists(Path.Combine(dir, "refs", "branches")));
        Assert.True(Directory.Exists(Path.Combine(dir, "recent")));
        Assert.True(Directory.Exists(Path.Combine(dir, "archive")));
        Assert.True(File.Exists(Path.Combine(dir, "state-journal.lock")));
        // 初始 active segment 已创建
        var segFiles = Directory.GetFiles(Path.Combine(dir, "recent"), "*.sj.rbf");
        Assert.Single(segFiles);
        Assert.Contains("00000001.sj.rbf", segFiles[0]);
        Assert.Empty(Directory.GetFiles(Path.Combine(dir, "refs", "branches"), "*.json", SearchOption.AllDirectories));
    }

    [Fact]
    public void Create_ExistingRepo_Fails() {
        var dir = GetTempDir();
        using var repo = AssertSuccess(Repository.Create(dir));

        var result = Repository.Create(dir);
        Assert.True(result.IsFailure);
    }

    [Fact]
    public void Commit_ThenOpen_RoundTrips() {
        var dir = GetTempDir();

        // Create + Commit
        {
            using var repo = CreateRepositoryWithBranch(dir, "main", out var main);
            var root = main.CreateDict<int, int>();
            root.Upsert(1, 10);
            root.Upsert(2, 20);
            AssertSuccess(repo.Commit(root));
        }

        // Open + verify
        {
            using var repo = AssertSuccess(Repository.Open(dir));
            var rev = AssertSuccess(repo.CheckoutBranch("main"));
            Assert.NotNull(rev.GraphRoot);
            var root = Assert.IsAssignableFrom<DurableDict<int, int>>(rev.GraphRoot);
            Assert.Equal(2, root.Count);
            Assert.Equal(GetIssue.None, root.Get(1, out int v1));
            Assert.Equal(10, v1);
            Assert.Equal(GetIssue.None, root.Get(2, out int v2));
            Assert.Equal(20, v2);
        }
    }

    [Fact]
    public void CheckoutBranch_MainBranch_ReturnsLoadedRevision() {
        var dir = GetTempDir();
        using var repo = CreateRepositoryWithBranch(dir, "main", out var main);

        var root = main.CreateDict<int, int>();
        root.Upsert(7, 70);
        AssertSuccess(repo.Commit(root));

        var opened = AssertSuccess(repo.CheckoutBranch("main"));
        Assert.Same(main, opened);
        Assert.NotNull(opened.GraphRoot);
    }

    [Fact]
    public void CheckoutBranch_UnknownBranch_ReturnsError() {
        var dir = GetTempDir();
        using var repo = AssertSuccess(Repository.Create(dir));

        var result = repo.CheckoutBranch("does-not-exist");
        Assert.True(result.IsFailure);
    }

    [Fact]
    public void Open_EmptyRepository_DoesNotInjectMainBranch() {
        var dir = GetTempDir();
        using (var repo = AssertSuccess(Repository.Create(dir))) {
            Assert.True(repo.CheckoutBranch("main").IsFailure);
        }

        using var reopened = AssertSuccess(Repository.Open(dir));
        Assert.True(reopened.CheckoutBranch("main").IsFailure);
    }

    [Fact]
    public void CreateBranch_Empty_SucceedsAndPersists() {
        var dir = GetTempDir();
        using (var repo = AssertSuccess(Repository.Create(dir))) {
            var feature = AssertSuccess(repo.CreateBranch("feature"));
            Assert.NotNull(feature);
            Assert.Equal(default, feature.HeadId);
            Assert.True(File.Exists(Path.Combine(dir, "refs", "branches", "feature.json")));
        }

        using var reopened = AssertSuccess(Repository.Open(dir));
        var reopenedFeature = AssertSuccess(reopened.CheckoutBranch("feature"));
        Assert.Equal(default, reopenedFeature.HeadId);
        Assert.Null(reopenedFeature.GraphRoot);
    }

    [Fact]
    public void CreateBranch_FromExistingBranch_UsesCommittedHeadOnly() {
        var dir = GetTempDir();
        using var repo = CreateRepositoryWithBranch(dir, "main", out var main);

        var mainRoot = main.CreateDict<int, int>();
        mainRoot.Upsert(1, 10);
        AssertSuccess(repo.Commit(mainRoot));

        mainRoot.Upsert(2, 20); // 未提交改动，不应被派生 branch 看到

        var feature = AssertSuccess(repo.CreateBranch("feature", "main"));
        var featureRoot = Assert.IsAssignableFrom<DurableDict<int, int>>(feature.GraphRoot);
        Assert.Equal(1, featureRoot.Count);
        Assert.Equal(GetIssue.None, featureRoot.Get(1, out int v));
        Assert.Equal(10, v);
        Assert.Equal(GetIssue.NotFound, featureRoot.Get(2, out _));
    }

    [Fact]
    public void CreateBranch_FromExistingBranch_CommitDoesNotMoveSourceBranch() {
        var dir = GetTempDir();
        using var repo = CreateRepositoryWithBranch(dir, "main", out var main);

        var mainRoot = main.CreateDict<int, int>();
        mainRoot.Upsert(1, 10);
        AssertSuccess(repo.Commit(mainRoot));

        var feature = AssertSuccess(repo.CreateBranch("feature", "main"));
        var featureRoot = Assert.IsAssignableFrom<DurableDict<int, int>>(feature.GraphRoot);
        featureRoot.Upsert(2, 20);
        AssertSuccess(repo.Commit(featureRoot));

        var mainAgain = AssertSuccess(repo.CheckoutBranch("main"));
        var mainLoadedRoot = Assert.IsAssignableFrom<DurableDict<int, int>>(mainAgain.GraphRoot);
        Assert.Equal(1, mainLoadedRoot.Count);
        Assert.Equal(GetIssue.None, mainLoadedRoot.Get(1, out int v1));
        Assert.Equal(10, v1);
        Assert.Equal(GetIssue.NotFound, mainLoadedRoot.Get(2, out _));
    }

    [Fact]
    public void CreateBranch_DuplicateName_ReturnsError() {
        var dir = GetTempDir();
        using var repo = AssertSuccess(Repository.Create(dir));

        _ = AssertSuccess(repo.CreateBranch("feature"));
        var duplicate = repo.CreateBranch("feature");
        Assert.True(duplicate.IsFailure);
    }

    [Fact]
    public void CreateBranch_UnknownSource_ReturnsError() {
        var dir = GetTempDir();
        using var repo = AssertSuccess(Repository.Create(dir));

        var result = repo.CreateBranch("feature", "missing");
        Assert.True(result.IsFailure);
    }

    [Fact]
    public void Commit_WritesBranchAndSequenceFiles() {
        var dir = GetTempDir();
        using var repo = CreateRepositoryWithBranch(dir, "main", out var main);
        var root = main.CreateDict<int, int>();
        root.Upsert(1, 1);
        AssertSuccess(repo.Commit(root));

        var branchPath = Path.Combine(dir, "refs", "branches", "main.json");
        Assert.True(File.Exists(branchPath));
        var branchContent = File.ReadAllText(branchPath);
        Assert.Contains("segmentNumber", branchContent);
        Assert.Contains("ticket", branchContent);
    }



    [Fact]
    public void Open_NonExistentDirectory_ReturnsError() {
        var dir = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid()}");
        var result = Repository.Open(dir);
        Assert.True(result.IsFailure);
    }



    [Fact]
    public void Open_TruncatedLatestSegment_DetectsCorruptionOnCheckout() {
        var dir = GetTempDir();
        string segmentPath;
        {
            using var repo = CreateRepositoryWithBranch(dir, "main", out var main);
            var root = main.CreateDict<int, int>();
            root.Upsert(1, 10);
            AssertSuccess(repo.Commit(root));
            segmentPath = Directory.GetFiles(Path.Combine(dir, "recent"), "*.sj.rbf").Single();
        }

        // Severely truncate the segment to guarantee corruption (keep only RBF header fence)
        using (var stream = new FileStream(segmentPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            stream.SetLength(4); // Only keep the 4-byte header fence
        }

        // Open succeeds (only validates RBF header + alignment)
        using var reopened = AssertSuccess(Repository.Open(dir));
        // Checkout fails because committed data is missing from the truncated segment
        var checkout = reopened.CheckoutBranch("main");
        Assert.True(checkout.IsFailure);
    }

    [Fact]
    public void FileRotation_TriggeredWhenExceedingThreshold() {
        var dir = GetTempDir();
        using var repo = CreateRepositoryWithBranch(dir, "main", out var main);
        // 设置极小阈值以触发轮换
        repo.SetRotationThreshold(1);

        var root = main.CreateDict<int, int>();
        root.Upsert(1, 100);
        AssertSuccess(repo.Commit(root)); // 写入数据使文件 > 1 byte

        root.Upsert(2, 200);
        AssertSuccess(repo.Commit(root)); // 应触发轮换（SaveAs）

        var segFiles = Directory.GetFiles(Path.Combine(dir, "recent"), "*.sj.rbf");
        Assert.True(segFiles.Length >= 2, $"Expected at least 2 segment files, got {segFiles.Length}");
    }

    [Fact]
    public void FileRotation_DataPreservedAfterRotation() {
        var dir = GetTempDir();
        using var repo = CreateRepositoryWithBranch(dir, "main", out var main);
        repo.SetRotationThreshold(1);

        var root = main.CreateDict<int, int>();
        root.Upsert(1, 100);
        AssertSuccess(repo.Commit(root));

        root.Upsert(2, 200);
        AssertSuccess(repo.Commit(root));

        root.Upsert(3, 300);
        AssertSuccess(repo.Commit(root));
        repo.Dispose();

        // Reopen and verify all data present
        using var repo2 = AssertSuccess(Repository.Open(dir));
        var reopenedMain = AssertSuccess(repo2.CheckoutBranch("main"));
        var loadedRoot = Assert.IsAssignableFrom<DurableDict<int, int>>(reopenedMain.GraphRoot);
        Assert.Equal(3, loadedRoot.Count);
        Assert.Equal(GetIssue.None, loadedRoot.Get(1, out int v1));
        Assert.Equal(100, v1);
        Assert.Equal(GetIssue.None, loadedRoot.Get(3, out int v3));
        Assert.Equal(300, v3);
    }

    [Fact]
    public void MaintainSegmentLayout_ArchivesExcessRecentSegmentsIntoBuckets() {
        const int OverCreate = 4;
        var dir = GetTempDir();
        using var repo = CreateRepositoryWithBranch(dir, "main", out var main);
        repo.SetRotationThreshold(1);

        var root = main.CreateDict<int, int>();
        for (int i = 1; i <= Repository.RecentSegmentWindowTargetCount + OverCreate; i++) {
            root.Upsert(i, i);
            AssertSuccess(repo.Commit(root));
        }

        repo.MaintainSegmentLayout();

        var recentDir = Path.Combine(dir, "recent");
        var recentFiles = Directory.GetFiles(recentDir, "*.sj.rbf");
        Assert.Equal(Repository.RecentSegmentWindowTargetCount, recentFiles.Length);
        Assert.DoesNotContain(SegmentPathTestHelper.RecentSegmentPath(dir, 1), recentFiles);

        var archiveFile = SegmentPathTestHelper.ArchiveSegmentPath(dir, 1);
        Assert.True(File.Exists(archiveFile));
        Assert.Equal(OverCreate, Directory.GetFiles(Path.Combine(dir, "archive"), "*.sj.rbf", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void Open_BestEffortMaintenance_ArchivesExcessRecentSegments() {
        const int OverCreate = 4;
        var dir = GetTempDir();
        using (var repo = CreateRepositoryWithBranch(dir, "main", out var main)) {
            repo.SetRotationThreshold(1);

            var root = main.CreateDict<int, int>();
            for (int i = 1; i <= Repository.RecentSegmentWindowTargetCount + OverCreate; i++) {
                root.Upsert(i, i);
                AssertSuccess(repo.Commit(root));
            }
        }

        var recentDirBefore = Path.Combine(dir, "recent");
        Assert.Equal(Repository.RecentSegmentWindowTargetCount + OverCreate, Directory.GetFiles(recentDirBefore, "*.sj.rbf").Length);

        using var reopened = AssertSuccess(Repository.Open(dir));

        var recentDirAfter = Path.Combine(dir, "recent");
        Assert.Equal(Repository.RecentSegmentWindowTargetCount, Directory.GetFiles(recentDirAfter, "*.sj.rbf").Length);
        Assert.True(File.Exists(SegmentPathTestHelper.ArchiveSegmentPath(dir, 1)));
    }

    [Fact]
    public void SetRotationThreshold_NegativeValue_Throws() {
        var dir = GetTempDir();
        using var repo = CreateRepositoryWithBranch(dir, "main", out _);

        Assert.Throws<ArgumentOutOfRangeException>(() => repo.SetRotationThreshold(-1));
    }

    [Fact]
    public void MultipleCommits_ThenOpen_RestoresLatestState() {
        var dir = GetTempDir();
        {
            using var repo = CreateRepositoryWithBranch(dir, "main", out var main);
            var root = main.CreateDict<int, int>();
            for (int i = 0; i < 10; i++) {
                root.Upsert(i, i * i);
                AssertSuccess(repo.Commit(root));
            }
        }

        {
            using var repo = AssertSuccess(Repository.Open(dir));
            var main = AssertSuccess(repo.CheckoutBranch("main"));
            var root = Assert.IsAssignableFrom<DurableDict<int, int>>(main.GraphRoot);
            Assert.Equal(10, root.Count);
            for (int i = 0; i < 10; i++) {
                Assert.Equal(GetIssue.None, root.Get(i, out int v));
                Assert.Equal(i * i, v);
            }
        }
    }

    [Fact]
    public void Commit_WhenBranchFileDiverged_FailsCasAndPoisonsRepository() {
        var dir = GetTempDir();
        using var repo = CreateRepositoryWithBranch(dir, "main", out var main);

        var root = main.CreateDict<int, int>();
        root.Upsert(1, 10);
        AssertSuccess(repo.Commit(root));

        var mainBranchPath = Path.Combine(dir, "refs", "branches", "main.json");
        var branchAfterCommit1 = File.ReadAllText(mainBranchPath);

        root.Upsert(2, 20);
        AssertSuccess(repo.Commit(root));

        // 模拟外部把 branch 回退到旧值，触发 CAS 失败。
        File.WriteAllText(mainBranchPath, branchAfterCommit1);

        root.Upsert(3, 30);
        var failed = repo.Commit(root);
        Assert.True(failed.IsFailure);
        Assert.Contains("CAS mismatch", failed.Error!.Message);

        var openFailed = repo.CheckoutBranch("main");
        Assert.True(openFailed.IsFailure);
        Assert.Contains("poisoned state", openFailed.Error!.Message);
    }

    [Fact]
    public void Commit_WhenRotatedCommitCasFails_DoesNotAdvanceRevisionBoundSegment() {
        var dir = GetTempDir();
        using var repo = CreateRepositoryWithBranch(dir, "main", out var main);
        repo.SetRotationThreshold(1);

        var root = main.CreateDict<int, int>();
        root.Upsert(1, 10);
        AssertSuccess(repo.Commit(root));
        Assert.Equal(1u, main.HeadSegmentNumber);

        var mainBranchPath = Path.Combine(dir, "refs", "branches", "main.json");
        var branchAfterCommit1 = File.ReadAllText(mainBranchPath);

        root.Upsert(2, 20);
        AssertSuccess(repo.Commit(root));
        Assert.Equal(2u, main.HeadSegmentNumber);

        File.WriteAllText(mainBranchPath, branchAfterCommit1);

        root.Upsert(3, 30);
        var failed = repo.Commit(root);
        Assert.True(failed.IsFailure);
        Assert.Contains("CAS mismatch", failed.Error!.Message);
        Assert.Equal(2u, main.HeadSegmentNumber);
    }



    #region OpenOrCreate / HasBranch / GetOrCreateBranch

    [Fact]
    public void OpenOrCreate_NonExistentDirectory_CreatesNewRepository() {
        var dir = GetTempDir();
        Assert.False(Directory.Exists(dir));

        using var repo = AssertSuccess(Repository.OpenOrCreate(dir));

        Assert.True(File.Exists(Path.Combine(dir, "state-journal.lock")));
        Assert.True(Directory.Exists(Path.Combine(dir, "refs", "branches")));
    }

    [Fact]
    public void OpenOrCreate_EmptyDirectory_CreatesNewRepository() {
        var dir = GetTempDir();
        Directory.CreateDirectory(dir);

        using var repo = AssertSuccess(Repository.OpenOrCreate(dir));
        Assert.True(File.Exists(Path.Combine(dir, "state-journal.lock")));
    }

    [Fact]
    public void OpenOrCreate_ExistingValidRepository_OpensIt() {
        var dir = GetTempDir();

        // Build a repo with a committed branch.
        {
            using var repo = CreateRepositoryWithBranch(dir, "main", out var main);
            var root = main.CreateDict<int, int>();
            root.Upsert(1, 10);
            AssertSuccess(repo.Commit(root));
        }

        using var reopened = AssertSuccess(Repository.OpenOrCreate(dir));
        Assert.True(reopened.HasBranch("main"));
        var rev = AssertSuccess(reopened.CheckoutBranch("main"));
        var root2 = Assert.IsAssignableFrom<DurableDict<int, int>>(rev.GraphRoot);
        Assert.Equal(1, root2.Count);
    }

    [Fact]
    public void OpenOrCreate_NonEmptyNonRepoDirectory_Fails() {
        var dir = GetTempDir();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "stranger.txt"), "hello");

        var result = Repository.OpenOrCreate(dir);
        Assert.True(result.IsFailure);
        Assert.Contains("not a StateJournal repository", result.Error!.Message);
        // 没被破坏：文件还在。
        Assert.True(File.Exists(Path.Combine(dir, "stranger.txt")));
        Assert.False(File.Exists(Path.Combine(dir, "state-journal.lock")));
    }

    [Fact]
    public void HasBranch_HitMissAndInvalidName() {
        var dir = GetTempDir();
        using var repo = CreateRepositoryWithBranch(dir, "main", out _);

        Assert.True(repo.HasBranch("main"));
        Assert.False(repo.HasBranch("does-not-exist"));
        // 非法名称返回 false，而非抛异常。
        Assert.False(repo.HasBranch(""));
        Assert.False(repo.HasBranch("/leading-slash"));
        Assert.False(repo.HasBranch("trailing-slash/"));
        Assert.False(repo.HasBranch(".."));
    }

    [Fact]
    public void GetOrCreateBranch_Existing_ReturnsSameRevision() {
        var dir = GetTempDir();
        using var repo = CreateRepositoryWithBranch(dir, "main", out var main);

        var again = AssertSuccess(repo.GetOrCreateBranch("main"));
        Assert.Same(main, again);
    }

    [Fact]
    public void GetOrCreateBranch_Missing_CreatesAndCanCommit() {
        var dir = GetTempDir();
        using var repo = AssertSuccess(Repository.Create(dir));

        Assert.False(repo.HasBranch("feature"));
        var feature = AssertSuccess(repo.GetOrCreateBranch("feature"));
        Assert.True(repo.HasBranch("feature"));

        var root = feature.CreateDict<int, int>();
        root.Upsert(42, 420);
        AssertSuccess(repo.Commit(root));

        // 再次调用应返回同一会话。
        var again = AssertSuccess(repo.GetOrCreateBranch("feature"));
        Assert.Same(feature, again);
    }

    #endregion

    private static T AssertSuccess<T>(AteliaResult<T> result) where T : notnull {
        Assert.True(result.IsSuccess, $"Expected success but got error: {result.Error}");
        return result.Value!;
    }

    private static Repository CreateRepositoryWithBranch(string dir, string branchName, out Revision revision) {
        var repo = AssertSuccess(Repository.Create(dir));
        revision = AssertSuccess(repo.CreateBranch(branchName));
        return repo;
    }

    [Fact]
    public void GetGraphRoot_UnbornBranch_Fails() {
        var dir = GetTempDir();
        using var repo = CreateRepositoryWithBranch(dir, "main", out var main);

        var result = main.GetGraphRoot<DurableDict<string>>();
        Assert.True(result.IsFailure);
        Assert.NotNull(result.Error);
        Assert.Contains("unborn", result.Error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetGraphRoot_TypeMatches_Succeeds() {
        var dir = GetTempDir();
        using var repo = CreateRepositoryWithBranch(dir, "main", out var main);
        var root = main.CreateDict<string>();
        root.Upsert("k", "v");
        AssertSuccess(repo.Commit(root));

        var result = main.GetGraphRoot<DurableDict<string>>();
        Assert.True(result.IsSuccess);
        Assert.Same(root, result.Value);
    }

    [Fact]
    public void GetGraphRoot_TypeMismatch_Fails() {
        var dir = GetTempDir();
        using var repo = CreateRepositoryWithBranch(dir, "main", out var main);
        var root = main.CreateDict<string>();
        root.Upsert("k", "v");
        AssertSuccess(repo.Commit(root));

        var result = main.GetGraphRoot<DurableDeque<string>>();
        Assert.True(result.IsFailure);
        Assert.NotNull(result.Error);
        Assert.Contains("Kind=MixedDict", result.Error!.Message);
        Assert.Contains("DurableDeque", result.Error!.Message);
    }

    #region ValidateBranchName

    [Theory]
    [InlineData("main")]
    [InlineData("feature")]
    [InlineData("release/v1.0")]
    [InlineData("user/alice/experiment")]
    [InlineData("a")]
    [InlineData("A")]
    [InlineData("0")]
    [InlineData("my-branch")]
    [InlineData("my_branch")]
    [InlineData("my.branch")]
    [InlineData("feature/JIRA-1234")]
    [InlineData("x/y/z/w")]
    [InlineData("CamelCase123")]
    public void ValidateBranchName_ValidNames_ReturnsNull(string name) {
        Assert.Null(Repository.ValidateBranchName(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void ValidateBranchName_NullOrWhitespace_ReturnsError(string? name) {
        Assert.NotNull(Repository.ValidateBranchName(name));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../evil")]
    [InlineData("../../etc/passwd")]
    [InlineData("ok/../evil")]
    [InlineData("a/../../b")]
    [InlineData("./hidden")]
    [InlineData("a/./b")]
    [InlineData("a/.")]
    [InlineData("a/..")]
    public void ValidateBranchName_PathTraversal_ReturnsError(string name) {
        var error = Repository.ValidateBranchName(name);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("/leading-slash")]
    [InlineData("trailing-slash/")]
    [InlineData("double//slash")]
    [InlineData("a///b")]
    public void ValidateBranchName_SlashViolations_ReturnsError(string name) {
        Assert.NotNull(Repository.ValidateBranchName(name));
    }

    [Theory]
    [InlineData("ends-with.")]
    [InlineData("ends-with-")]
    public void ValidateBranchName_BadTrailingChar_ReturnsError(string name) {
        Assert.NotNull(Repository.ValidateBranchName(name));
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("has\ttab")]
    [InlineData("has\nnewline")]
    [InlineData("back\\slash")]
    [InlineData("col:on")]
    [InlineData("aster*isk")]
    [InlineData("quest?ion")]
    [InlineData("bra[cket")]
    [InlineData("til~de")]
    [InlineData("car^et")]
    [InlineData("at@sign")]
    [InlineData("cur{ly")]
    [InlineData("pi|pe")]
    [InlineData("semi;colon")]
    [InlineData("dol$lar")]
    [InlineData("perce%nt")]
    [InlineData("amp&ersand")]
    [InlineData("equ=als")]
    [InlineData("exclam!ation")]
    [InlineData("paren(hesis")]
    [InlineData("angle<bracket")]
    [InlineData("angle>bracket")]
    [InlineData("quote\"mark")]
    [InlineData("single'quote")]
    [InlineData("grave`accent")]
    [InlineData("hash#tag")]
    [InlineData("plus+sign")]
    [InlineData("comma,sep")]
    public void ValidateBranchName_IllegalCharacters_ReturnsError(string name) {
        Assert.NotNull(Repository.ValidateBranchName(name));
    }

    [Theory]
    [InlineData("HEAD.lock")]
    [InlineData("feature.lock")]
    [InlineData("a/b.lock")]
    [InlineData("my.LOCK")]
    public void ValidateBranchName_DotLockSuffix_ReturnsError(string name) {
        Assert.NotNull(Repository.ValidateBranchName(name));
    }

    [Fact]
    public void ValidateBranchName_ExceedsMaxLength_ReturnsError() {
        var name = new string('a', Repository.MaxBranchNameLength + 1);
        Assert.NotNull(Repository.ValidateBranchName(name));
    }

    [Fact]
    public void ValidateBranchName_ExactMaxLength_IsValid() {
        var name = new string('a', Repository.MaxBranchNameLength);
        Assert.Null(Repository.ValidateBranchName(name));
    }

    [Theory]
    [InlineData("\0nul")]
    [InlineData("bel\x07l")]
    [InlineData("esc\x1B")]
    [InlineData("del\x7F")]
    public void ValidateBranchName_ControlCharacters_ReturnsError(string name) {
        Assert.NotNull(Repository.ValidateBranchName(name));
    }

    [Theory]
    [InlineData("日本語")]
    [InlineData("brüche")]
    [InlineData("分支名")]
    public void ValidateBranchName_NonAscii_ReturnsError(string name) {
        Assert.NotNull(Repository.ValidateBranchName(name));
    }

    [Fact]
    public void CreateBranch_InvalidName_ReturnsError() {
        var dir = GetTempDir();
        using var repo = AssertSuccess(Repository.Create(dir));

        var result = repo.CreateBranch("../../evil");
        Assert.True(result.IsFailure);
        Assert.Contains("Invalid branch name", result.Error!.Message);
    }

    [Fact]
    public void CreateBranch_InvalidName_WithSource_ReturnsError() {
        var dir = GetTempDir();
        using var repo = CreateRepositoryWithBranch(dir, "main", out _);

        var result = repo.CreateBranch("../escape", "main");
        Assert.True(result.IsFailure);
        Assert.Contains("Invalid branch name", result.Error!.Message);
    }

    [Fact]
    public void CreateBranch_NestedSlash_Succeeds() {
        var dir = GetTempDir();
        using var repo = AssertSuccess(Repository.Create(dir));

        var rev = AssertSuccess(repo.CreateBranch("feature/my-work"));
        Assert.NotNull(rev);
        Assert.True(File.Exists(Path.Combine(dir, "refs", "branches", "feature", "my-work.json")));
    }

    #endregion
}
