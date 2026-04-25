using Xunit;

namespace Atelia.StateJournal.Tests;

public class InlineStringApiTests {
    [Fact]
    public void InlineString_NormalizesNullToEmptyString() {
        var value = new InlineString(null);

        Assert.Equal(string.Empty, value.Value);
        Assert.Equal(string.Empty, value.ToString());
    }

    [Fact]
    public void InlineString_CanBeUsed_AsTypedDictKeyAndValue_ThroughPublicRepositoryApi() {
        string repoDir = Path.Combine(Path.GetTempPath(), $"state-journal-inline-string-{Guid.NewGuid():N}");

        try {
            var createRepo = Repository.Create(repoDir);
            Assert.True(createRepo.IsSuccess, $"Repository.Create failed: {createRepo.Error}");

            using (var repo = createRepo.Value!) {
                var createBranch = repo.CreateBranch("main");
                Assert.True(createBranch.IsSuccess, $"CreateBranch failed: {createBranch.Error}");
                var rev = createBranch.Value!;
                var root = rev.CreateDict<InlineString, InlineString>();

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
            var reopenedRoot = Assert.IsAssignableFrom<DurableDict<InlineString, InlineString>>(reopenedRev.GraphRoot);

            Assert.Equal("hello", reopenedRoot.GetOrThrow("title").Value);
            Assert.Equal("世界", reopenedRoot.GetOrThrow("你好").Value);
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
