using System.Security.Cryptography;
using System.Text;
using Atelia.Completion.Abstractions;

namespace Atelia.MemoPod.Tests.Recall;

public sealed class MemoPodTransientProjectionTests : IDisposable {
    private readonly string _root = Directory.CreateTempSubdirectory(
        "atelia-memo-pod-transient-projection-").FullName;

    [Fact]
    public async Task BrokenRendererDoesNotBlockPublishOpenOrMachineReads() {
        int renders = 0;
        bool broken = true;
        var hooks = new MemoPodLifecycleTestHooks(BeforeRender: _ => {
            renders++;
            if (broken) { throw new IOException("Local projection failed."); }
        });
        MemoPod pod = MemoPod.CreateForTesting(_root, MemoPodRecallFixture.PodId, "topic", hooks);
        MemoId id = pod.Append("original Note\r\nwith its exact ending\n");
        await pod.FreezeAsync();
        MemoPodStorePaths paths = MemoPodStoreLayout.Resolve(_root, pod.PodId);
        byte[] committed = File.ReadAllBytes(paths.DocumentPath);
        string identity = pod.ComputeStateIdentity();

        MemoPod reopened = MemoPod.OpenForTesting(_root, pod.PodId, hooks);
        Assert.Equal(identity, reopened.ComputeStateIdentity());
        Assert.Equal(id, Assert.Single(reopened.List()).Id);
        reopened.ConfirmCurrentDocumentDurability();
        Assert.Equal(0, renders);

        var client = new FakeMemoRecallCompletionClient();
        await Assert.ThrowsAsync<IOException>(() => reopened.RecallAsync(
            client, "model", "query", MemoPodRecallFixture.Options()));

        Assert.Equal(0, client.InvocationCount);
        Assert.Equal(MemoPodPhase.Frozen, reopened.Phase);
        Assert.Equal(identity, reopened.ComputeStateIdentity());
        Assert.Equal(committed, File.ReadAllBytes(paths.DocumentPath));
        Assert.Equal(pod.Get(id), reopened.Get(id));

        broken = false;
        _ = await reopened.RecallAsync(client, "model", "query", MemoPodRecallFixture.Options());
        Assert.Equal(1, client.InvocationCount);
        Assert.Equal(2, renders);
    }

    [Fact]
    public async Task ReplacingCachedPresentationDuringRecallPreservesEpochAndCallDigest() {
        int style = 0;
        int renders = 0;
        var hooks = new MemoPodLifecycleTestHooks(RenderPrompt: document => {
            renders++;
            string text = MemoPodPromptRenderer.Render(document).ExactText;
            return MemoPodFrozenPrompt.FromOwnedUtf8(Encoding.UTF8.GetBytes(text + new string('\n', style)));
        });
        MemoPod pod = MemoPod.CreateForTesting(_root, MemoPodRecallFixture.PodId, "topic", hooks);
        MemoId id = pod.Append("unchanged content");
        await pod.FreezeAsync();
        string identity = pod.ComputeStateIdentity();
        MemoPodStorePaths paths = MemoPodStoreLayout.Resolve(_root, pod.PodId);
        byte[] committed = File.ReadAllBytes(paths.DocumentPath);
        Assert.Equal(0, renders);

        var completion = new TaskCompletionSource<CompletionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeMemoRecallCompletionClient { Handler = (_, _, _) => completion.Task };
        Task<MemoRecallResult> pending = pod.RecallAsync(client, "model", "query", MemoPodRecallFixture.Options());
        CompletionRequest request = Assert.Single(client.Requests);
        string firstText = Assert.IsType<string>(Assert.IsType<ObservationMessage>(
            Assert.Single(request.PromptPrefix.SharedContextMessages)).Content);

        style = 1;
        pod.ClearPromptCache();
        MemoPodFrozenPrompt replacement = pod.FrozenPrompt;
        Assert.NotEqual(firstText, replacement.ExactText);
        Assert.Equal(2, renders);
        completion.SetResult(client.Result(request, [client.ToolCall("{\"memoIds\":[\"" + id.Value + "\"]}")]));
        MemoRecallResult result = await pending;

        Assert.Equal(id, Assert.Single(result.Memos).Id);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(firstText))), result.FrozenPromptSha256);
        Assert.NotEqual(result.FrozenPromptSha256, replacement.Sha256);
        Assert.Equal(identity, pod.ComputeStateIdentity());
        Assert.Equal(committed, File.ReadAllBytes(paths.DocumentPath));

        var nextClient = new FakeMemoRecallCompletionClient();
        MemoRecallResult next = await pod.RecallAsync(nextClient, "model", "query", MemoPodRecallFixture.Options());
        Assert.Equal(replacement.Sha256, next.FrozenPromptSha256);
        Assert.Equal(2, renders);
    }

    [Fact]
    public async Task CancellationDuringLazyProjectionDoesNotInvokeProviderOrRevokeContent() {
        using var cancellation = new CancellationTokenSource();
        MemoPod pod = MemoPod.CreateForTesting(_root, MemoPodRecallFixture.PodId, "topic",
            new MemoPodLifecycleTestHooks(BeforeRender: _ => cancellation.Cancel()));
        MemoId id = pod.Append("saved before rendering");
        await pod.FreezeAsync();
        string identity = pod.ComputeStateIdentity();
        var client = new FakeMemoRecallCompletionClient();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pod.RecallAsync(
            client, "model", "query", MemoPodRecallFixture.Options(), cancellation.Token));

        Assert.Equal(0, client.InvocationCount);
        Assert.Equal(MemoPodPhase.Frozen, pod.Phase);
        Assert.Equal(identity, pod.ComputeStateIdentity());
        Assert.Equal(id, Assert.Single(pod.List()).Id);
    }

    public void Dispose() {
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }
}
