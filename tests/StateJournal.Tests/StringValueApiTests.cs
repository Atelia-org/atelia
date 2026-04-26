using Xunit;

namespace Atelia.StateJournal.Tests;

public class StringValueApiTests {
    [Fact]
    public void TypedString_NormalizesNullToEmptyString_OnRoundTrip() {
        string repoDir = Path.Combine(Path.GetTempPath(), $"state-journal-string-null-{Guid.NewGuid():N}");

        try {
            var createRepo = Repository.Create(repoDir);
            Assert.True(createRepo.IsSuccess, $"Repository.Create failed: {createRepo.Error}");

            using (var repo = createRepo.Value!) {
                var createBranch = repo.CreateBranch("main");
                Assert.True(createBranch.IsSuccess, $"CreateBranch failed: {createBranch.Error}");
                var rev = createBranch.Value!;
                var root = rev.CreateDict<string, string>();
                root.Upsert("title", null);
                var commit = repo.Commit(root);
                Assert.True(commit.IsSuccess, $"Commit failed: {commit.Error}");
            }

            var openRepo = Repository.Open(repoDir);
            Assert.True(openRepo.IsSuccess, $"Repository.Open failed: {openRepo.Error}");
            using var reopenedRepo = openRepo.Value!;

            var checkout = reopenedRepo.CheckoutBranch("main");
            Assert.True(checkout.IsSuccess, $"CheckoutBranch failed: {checkout.Error}");
            var reopenedRev = checkout.Value!;
            var reopenedRoot = Assert.IsAssignableFrom<DurableDict<string, string>>(reopenedRev.GraphRoot);
            Assert.Equal(string.Empty, reopenedRoot.GetOrThrow("title"));
        }
        finally {
            try {
                if (Directory.Exists(repoDir)) { Directory.Delete(repoDir, recursive: true); }
            }
            catch {
            }
        }
    }

    [Fact]
    public void String_CanBeUsed_AsTypedDictKeyAndValue_ThroughPublicRepositoryApi() {
        string repoDir = Path.Combine(Path.GetTempPath(), $"state-journal-typed-string-{Guid.NewGuid():N}");

        try {
            var createRepo = Repository.Create(repoDir);
            Assert.True(createRepo.IsSuccess, $"Repository.Create failed: {createRepo.Error}");

            using (var repo = createRepo.Value!) {
                var createBranch = repo.CreateBranch("main");
                Assert.True(createBranch.IsSuccess, $"CreateBranch failed: {createBranch.Error}");
                var rev = createBranch.Value!;
                var root = rev.CreateDict<string, string>();

                root.Upsert("title", "hello");
                root.Upsert("你好", "世界");

                var commit = repo.Commit(root);
                Assert.True(commit.IsSuccess, $"Commit failed: {commit.Error}");
            }

            var openRepo = Repository.Open(repoDir);
            Assert.True(openRepo.IsSuccess, $"Repository.Open failed: {openRepo.Error}");
            using var reopenedRepo = openRepo.Value!;

            var checkout = reopenedRepo.CheckoutBranch("main");
            Assert.True(checkout.IsSuccess, $"CheckoutBranch failed: {checkout.Error}");
            var reopenedRev = checkout.Value!;
            var reopenedRoot = Assert.IsAssignableFrom<DurableDict<string, string>>(reopenedRev.GraphRoot);

            Assert.Equal("hello", reopenedRoot.GetOrThrow("title"));
            Assert.Equal("世界", reopenedRoot.GetOrThrow("你好"));
            Assert.True(reopenedRoot.ContainsKey("title"));
        }
        finally {
            try {
                if (Directory.Exists(repoDir)) { Directory.Delete(repoDir, recursive: true); }
            }
            catch {
            }
        }
    }
}
