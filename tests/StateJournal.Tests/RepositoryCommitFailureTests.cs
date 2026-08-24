using System.Text.Json.Nodes;
using Xunit;

namespace Atelia.StateJournal.Tests;

public sealed class RepositoryCommitFailureTests : IDisposable {
    private readonly List<string> _tempDirs = new();

    [Fact]
    public void DataDurabilityFailure_IsStructuredNotPublished_PoisonsAndRollsBackRotation() {
        var dir = GetTempDir();
        using var repo = CreateRepositoryWithBranch(dir, out var revision);
        var root = revision.CreateDict<string, int>();
        root.Upsert("before", 1);
        var parent = AssertSuccess(repo.Commit(root));

        repo.SetRotationThreshold(1);
        root.Upsert("after", 2);

        AteliaResult<CommitAddress> result;
        using (Repository.InjectCommitFaultScope(
            RepositoryCommitFaultPoint.BeforeDataDurabilityFlush,
            () => new IOException("injected data flush failure")
        )) {
            result = repo.Commit(root);
        }

        var error = AssertCommitError(
            result,
            parent,
            RepositoryCommitFailurePhase.DataDurability,
            RepositoryCommitPublicationState.NotPublished
        );
        Assert.Equal(2U, error.CandidateAddress.SegmentNumber);
        Assert.False(error.MayHavePublished);
        AssertStructuredDetails(error);
        Assert.False(File.Exists(SegmentPathTestHelper.RecentSegmentPath(dir, 2)));
        AssertPoisoned(repo, root);

        repo.Dispose();
        using var reopened = AssertSuccess(Repository.Open(dir));
        Assert.True(reopened.TryGetBranchHeadAddress("main", out var reopenedHead));
        Assert.Equal(parent, reopenedHead);
        var reopenedRoot = Assert.IsAssignableFrom<DurableDict<string, int>>(
            AssertSuccess(reopened.CheckoutBranch("main")).GraphRoot
        );
        Assert.Equal(1, reopenedRoot.Count);
        Assert.Equal(GetIssue.None, reopenedRoot.Get("before", out var before));
        Assert.Equal(1, before);
        Assert.Equal(GetIssue.NotFound, reopenedRoot.Get("after", out _));
    }

    [Fact]
    public void VerifyExpectedHeadFailure_IsStructuredNotPublished() {
        var dir = GetTempDir();
        using var repo = CreateRepositoryWithBranch(dir, out var revision);
        var root = revision.CreateDict<int, int>();
        root.Upsert(1, 1);
        var parent = AssertSuccess(repo.Commit(root));

        var branchPath = GetBranchPath(dir, "main");
        var branchJson = JsonNode.Parse(File.ReadAllText(branchPath))!.AsObject();
        branchJson["generation"] = branchJson["generation"]!.GetValue<ulong>() + 1;
        branchJson["head"] = null;
        File.WriteAllText(branchPath, branchJson.ToJsonString());

        root.Upsert(2, 2);
        var error = AssertCommitError(
            repo.Commit(root),
            parent,
            RepositoryCommitFailurePhase.VerifyExpectedHead,
            RepositoryCommitPublicationState.NotPublished
        );

        Assert.False(error.MayHavePublished);
        AssertPoisoned(repo, root);
    }

    [Fact]
    public void BeforePrimaryPublicationFailure_IsNotPublished_AndReopensAtParent() {
        var dir = GetTempDir();
        using var repo = CreateRepositoryWithBranch(dir, out var revision);
        var root = revision.CreateDict<int, string>();
        root.Upsert(1, "parent");
        var parent = AssertSuccess(repo.Commit(root));
        root.Upsert(2, "candidate");

        RepositoryCommitError error;
        using (Repository.InjectCommitFaultScope(
            RepositoryCommitFaultPoint.BeforePrimaryRefPublication,
            () => new IOException("injected pre-publication failure")
        )) {
            error = AssertCommitError(
                repo.Commit(root),
                parent,
                RepositoryCommitFailurePhase.PublishPrimaryRef,
                RepositoryCommitPublicationState.NotPublished
            );
        }

        AssertPoisoned(repo, root);
        repo.Dispose();

        using var reopened = AssertSuccess(Repository.Open(dir));
        Assert.True(reopened.TryGetBranchHeadAddress("main", out var reopenedHead));
        Assert.Equal(parent, reopenedHead);
        Assert.NotEqual(error.CandidateAddress, reopenedHead);
    }

    [Fact]
    public void DuringPrimaryPublicationFailure_IsMayHavePublished_AndReopensAtParent() {
        var dir = GetTempDir();
        using var repo = CreateRepositoryWithBranch(dir, out var revision);
        var root = revision.CreateDict<int, string>();
        root.Upsert(1, "parent");
        var parent = AssertSuccess(repo.Commit(root));
        repo.SetRotationThreshold(1);
        root.Upsert(2, "candidate");

        RepositoryCommitError error;
        using (Repository.InjectCommitFaultScope(
            RepositoryCommitFaultPoint.DuringPrimaryRefPublication,
            () => new IOException("injected publication-boundary failure")
        )) {
            error = AssertCommitError(
                repo.Commit(root),
                parent,
                RepositoryCommitFailurePhase.PublishPrimaryRef,
                RepositoryCommitPublicationState.MayHavePublished
            );
        }

        Assert.True(error.MayHavePublished);
        Assert.Equal(2U, error.CandidateAddress.SegmentNumber);
        Assert.True(File.Exists(SegmentPathTestHelper.RecentSegmentPath(dir, 2)));
        AssertPoisoned(repo, root);
        repo.Dispose();

        Assert.True(File.Exists(SegmentPathTestHelper.RecentSegmentPath(dir, 2)));
        using var reopened = AssertSuccess(Repository.Open(dir));
        Assert.True(reopened.TryGetBranchHeadAddress("main", out var reopenedHead));
        Assert.Equal(parent, reopenedHead);
        var reopenedRoot = Assert.IsAssignableFrom<DurableDict<int, string>>(
            AssertSuccess(reopened.CheckoutBranch("main")).GraphRoot
        );
        Assert.Equal(1, reopenedRoot.Count);
        Assert.Equal(GetIssue.None, reopenedRoot.Get(1, out var parentValue));
        Assert.Equal("parent", parentValue);
        Assert.Equal(GetIssue.NotFound, reopenedRoot.Get(2, out _));
    }

    [Fact]
    public void ReflogFailureAfterNonRotatedPublication_IsPublished_AndReopensAtExactCandidate() {
        var dir = GetTempDir();
        using var repo = CreateRepositoryWithBranch(dir, out var revision);
        var root = revision.CreateDict<string, int>();
        root.Upsert("before", 1);
        var parent = AssertSuccess(repo.Commit(root));
        root.Upsert("after", 2);

        RepositoryCommitError error;
        using (Repository.InjectCommitFaultScope(
            RepositoryCommitFaultPoint.BeforeReflogAppend,
            () => new IOException("injected reflog failure")
        )) {
            error = AssertCommitError(
                repo.Commit(root),
                parent,
                RepositoryCommitFailurePhase.AppendReflog,
                RepositoryCommitPublicationState.Published
            );
        }

        Assert.Equal(parent.SegmentNumber, error.CandidateAddress.SegmentNumber);
        Assert.True(error.MayHavePublished);
        AssertPoisoned(repo, root);
        repo.Dispose();

        AssertExactCandidateState(dir, parent, error.CandidateAddress);
    }

    [Fact]
    public void ReflogFailureAfterRotatedPublication_PreservesCandidateSegmentAndLineage() {
        var dir = GetTempDir();
        using var repo = CreateRepositoryWithBranch(dir, out var revision);
        var root = revision.CreateDict<string, int>();
        root.Upsert("before", 1);
        var parent = AssertSuccess(repo.Commit(root));
        repo.SetRotationThreshold(1);
        root.Upsert("after", 2);

        RepositoryCommitError error;
        using (Repository.InjectCommitFaultScope(
            RepositoryCommitFaultPoint.BeforeReflogAppend,
            () => new IOException("injected rotated reflog failure")
        )) {
            error = AssertCommitError(
                repo.Commit(root),
                parent,
                RepositoryCommitFailurePhase.AppendReflog,
                RepositoryCommitPublicationState.Published
            );
        }

        Assert.Equal(2U, error.CandidateAddress.SegmentNumber);
        Assert.True(File.Exists(SegmentPathTestHelper.RecentSegmentPath(dir, 2)));
        AssertPoisoned(repo, root);
        repo.Dispose();

        Assert.True(File.Exists(SegmentPathTestHelper.RecentSegmentPath(dir, 2)));
        AssertExactCandidateState(dir, parent, error.CandidateAddress);
    }

    [Fact]
    public void CommitFaultScope_DoesNotAffectCreateBranch_AndInvokesFactoryOnce() {
        var dir = GetTempDir();
        using var repo = AssertSuccess(Repository.Create(dir));
        var factoryCalls = 0;

        using (Repository.InjectCommitFaultScope(
            RepositoryCommitFaultPoint.BeforePrimaryRefPublication,
            () => {
                factoryCalls++;
                return new IOException("commit-only one-shot fault");
            }
        )) {
            var revision = AssertSuccess(repo.CreateBranch("main"));
            var root = revision.CreateDict<int, int>();
            root.Upsert(1, 1);
            var result = repo.Commit(root);
            var error = Assert.IsType<RepositoryCommitError>(result.Error);
            Assert.Null(error.ExpectedHeadAddress);
            Assert.Equal(RepositoryCommitFailurePhase.PublishPrimaryRef, error.FailurePhase);
            Assert.Equal(RepositoryCommitPublicationState.NotPublished, error.PublicationState);
            Assert.Equal("null", error.Details![nameof(error.ExpectedHeadAddress)]);
            Assert.Equal(1, factoryCalls);

            _ = repo.Commit(root);
            Assert.Equal(1, factoryCalls);
        }
    }

    public void Dispose() {
        foreach (var dir in _tempDirs) {
            try {
                if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); }
            }
            catch {
                // best-effort test cleanup
            }
        }
    }

    private static RepositoryCommitError AssertCommitError(
        AteliaResult<CommitAddress> result,
        CommitAddress expectedHead,
        RepositoryCommitFailurePhase expectedPhase,
        RepositoryCommitPublicationState expectedPublicationState
    ) {
        Assert.True(result.IsFailure);
        var error = Assert.IsType<RepositoryCommitError>(result.Error);
        Assert.Equal(RepositoryCommitError.StableErrorCode, error.ErrorCode);
        Assert.Equal("main", error.BranchName);
        Assert.Equal(expectedHead, error.ExpectedHeadAddress);
        Assert.False(error.CandidateAddress.CommitTicket.IsNull);
        Assert.Equal(expectedPhase, error.FailurePhase);
        Assert.Equal(expectedPublicationState, error.PublicationState);
        Assert.True(error.RequiresRepositoryReopen);
        Assert.False(error.CanRetryTransparently);
        return error;
    }

    private static void AssertStructuredDetails(RepositoryCommitError error) {
        var details = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(error.Details);
        Assert.Equal(error.BranchName, details[nameof(error.BranchName)]);
        Assert.Equal(error.ExpectedHeadAddress?.ToString() ?? "null", details[nameof(error.ExpectedHeadAddress)]);
        Assert.Equal(error.CandidateAddress.ToString(), details[nameof(error.CandidateAddress)]);
        Assert.Equal(error.FailurePhase.ToString(), details[nameof(error.FailurePhase)]);
        Assert.Equal(error.PublicationState.ToString(), details[nameof(error.PublicationState)]);
        Assert.Equal(bool.TrueString, details[nameof(error.RequiresRepositoryReopen)]);
        Assert.Equal(bool.FalseString, details[nameof(error.CanRetryTransparently)]);
        Assert.Equal(error.MayHavePublished.ToString(), details[nameof(error.MayHavePublished)]);
    }

    private static void AssertPoisoned(Repository repo, DurableObject root) {
        var next = repo.Commit(root);
        Assert.True(next.IsFailure);
        Assert.Contains("poisoned", next.Error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertExactCandidateState(
        string dir,
        CommitAddress expectedParent,
        CommitAddress expectedCandidate
    ) {
        using var reopened = AssertSuccess(Repository.Open(dir));
        Assert.True(reopened.TryGetBranchHeadAddress("main", out var physicalHead));
        Assert.Equal(expectedCandidate, physicalHead);

        var reopenedRevision = AssertSuccess(reopened.CheckoutBranch("main"));
        Assert.Equal(expectedParent, reopenedRevision.HeadParentAddress);
        var reopenedRoot = Assert.IsAssignableFrom<DurableDict<string, int>>(reopenedRevision.GraphRoot);
        Assert.Equal(2, reopenedRoot.Count);
        Assert.Equal(GetIssue.None, reopenedRoot.Get("before", out var before));
        Assert.Equal(1, before);
        Assert.Equal(GetIssue.None, reopenedRoot.Get("after", out var after));
        Assert.Equal(2, after);

        var historicalRoot = Assert.IsAssignableFrom<DurableDict<string, int>>(
            AssertSuccess(reopened.LoadRootAtCommit(expectedCandidate))
        );
        Assert.Equal(2, historicalRoot.Count);
    }

    private Repository CreateRepositoryWithBranch(string dir, out Revision revision) {
        var repo = AssertSuccess(Repository.Create(dir));
        revision = AssertSuccess(repo.CreateBranch("main"));
        return repo;
    }

    private string GetTempDir() {
        var path = Path.Combine(Path.GetTempPath(), $"repo-commit-failure-{Guid.NewGuid()}");
        _tempDirs.Add(path);
        return path;
    }

    private static string GetBranchPath(string repoDir, string branchName) {
        var relative = branchName.Replace('/', Path.DirectorySeparatorChar) + ".json";
        return Path.Combine(repoDir, "refs", "branches", relative);
    }

    private static T AssertSuccess<T>(AteliaResult<T> result) where T : notnull {
        Assert.True(result.IsSuccess, $"Expected success but got error: {result.Error}");
        return result.Value!;
    }
}
