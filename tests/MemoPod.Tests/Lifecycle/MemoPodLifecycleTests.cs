using System.Collections.Immutable;
using Atelia.MemoPod;

namespace Atelia.MemoPod.Tests.Lifecycle;

public sealed class MemoPodLifecycleTests : IDisposable {
    private static readonly MemoPodId PodId = MemoPodId.Parse(
        "88888888888888888888888888888888"
    );

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "atelia-memo-pod-lifecycle-tests",
        Guid.NewGuid().ToString("N")
    );

    public MemoPodLifecycleTests() {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void CreateIsEditableAndDoesNotMutateFilesystem() {
        MemoPod pod = MemoPod.Create(_root, PodId, "customer details");

        Assert.Equal(PodId, pod.PodId);
        Assert.Equal("customer details", pod.Topic);
        Assert.Equal(MemoPodPhase.Editable, pod.Phase);
        Assert.Empty(pod.List());
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public void ArgumentPhaseAndMissingMemoFailuresUseStandardExceptions() {
        Assert.Throws<ArgumentException>(() =>
            MemoPod.Create(_root, default, "topic"));
        Assert.Throws<ArgumentException>(() =>
            MemoPod.Open(_root, default));
        Assert.Throws<ArgumentException>(() =>
            MemoPod.Create(_root, PodId, string.Empty));

        MemoPod pod = MemoPod.Create(_root, PodId, "topic");
        Assert.Throws<ArgumentException>(() => pod.Append(string.Empty));
        Assert.Throws<ArgumentException>(() => pod.Get(default));
        Assert.Throws<ArgumentException>(() => pod.TryGet(default, out _));
        Assert.Throws<KeyNotFoundException>(() =>
            pod.Remove(MemoId.Parse("m1:00000001")));
        Assert.Throws<InvalidOperationException>(() => pod.ResumeEditing());
    }

    [Fact]
    public async Task FreezeAndOpenRoundTripCompleteFrozenState() {
        MemoPod pod = MemoPod.Create(_root, PodId, "customer details");
        MemoId first = pod.Append(
            "order 17 ships Friday",
            title: "Order 17",
            gist: "Ships Friday",
            summary: "The customer expects order 17 to ship on Friday."
        );
        MemoId second = pod.Append("prefers email");

        await pod.FreezeAsync();

        Assert.Equal(MemoPodPhase.Frozen, pod.Phase);
        Assert.Equal(
            [first, second],
            pod.List().Select(static memo => memo.Id).ToArray()
        );
        Assert.Contains(first.Value, pod.FrozenPrompt.ExactText);
        Assert.Throws<InvalidOperationException>(
            () => pod.Append("rejected")
        );
        Assert.Throws<InvalidOperationException>(() => pod.Remove(first));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => pod.FreezeAsync()
        );

        MemoPod reopened = MemoPod.Open(_root, PodId);
        Assert.Equal(MemoPodPhase.Frozen, reopened.Phase);
        Assert.Equal(pod.Topic, reopened.Topic);
        Assert.Equal(pod.List().ToArray(), reopened.List().ToArray());
        Memo reopenedFirst = reopened.Get(first);
        Assert.Equal("Order 17", reopenedFirst.Title);
        Assert.Equal("Ships Friday", reopenedFirst.Gist);
        Assert.Equal(
            "The customer expects order 17 to ship on Friday.",
            reopenedFirst.Summary
        );
        Assert.Equal(
            pod.FrozenPrompt.Sha256,
            reopened.FrozenPrompt.Sha256
        );
    }

    [Fact]
    public async Task ResumeEditingIsExplicitAndReadApisWorkInBothPhases() {
        MemoPod pod = MemoPod.Create(_root, PodId, "topic");
        MemoId id = pod.Append("memo");
        ImmutableArray<Memo> beforeMutation = pod.List();
        await pod.FreezeAsync();

        Assert.Equal("memo", pod.Get(id).ExactText);
        Assert.True(pod.TryGet(id, out Memo? frozenMemo));
        Assert.NotNull(frozenMemo);
        Assert.Equal("memo", frozenMemo.ExactText);

        pod.ResumeEditing();
        Assert.Equal(MemoPodPhase.Editable, pod.Phase);
        Assert.Throws<InvalidOperationException>(() => pod.ResumeEditing());
        MemoId added = pod.Append("later");

        Assert.Single(beforeMutation);
        Assert.Equal(id, beforeMutation[0].Id);
        Assert.Equal([id, added], pod.List().Select(static memo => memo.Id));
    }

    [Fact]
    public async Task FirstFreezeIsNoClobberAndFailureKeepsEditableDirty() {
        MemoPod owner = MemoPod.Create(_root, PodId, "owner");
        owner.Append("old");
        await owner.FreezeAsync();

        MemoPod challenger = MemoPod.Create(_root, PodId, "challenger");
        challenger.Append("new");
        MemoPodPersistenceException collision =
            await Assert.ThrowsAsync<MemoPodPersistenceException>(
                () => challenger.FreezeAsync()
            );

        Assert.Equal(
            MemoPodPersistenceFailureKind.AlreadyExists,
            collision.FailureKind
        );
        Assert.Equal(MemoPodPhase.Editable, challenger.Phase);
        Assert.Equal(
            "old",
            Assert.Single(MemoPod.Open(_root, PodId).List()).ExactText
        );

        File.Delete(
            MemoPodStoreLayout.Resolve(_root, PodId).DocumentPath
        );
        await challenger.FreezeAsync();
        Assert.Equal(MemoPodPhase.Frozen, challenger.Phase);
    }

    [Fact]
    public async Task DirtyFalseRefreezeRendersWithoutPublishingOrRewriting() {
        MemoPod created = MemoPod.Create(_root, PodId, "topic");
        created.Append("memo");
        await created.FreezeAsync();

        int renderCount = 0;
        int publishCount = 0;
        var hooks = new MemoPodLifecycleTestHooks(
            BeforeRender: _ => renderCount++,
            PublisherHooks: new MemoPodPublisherTestHooks(
                BeforePublish: _ => publishCount++
            )
        );
        MemoPod pod = MemoPod.OpenForTesting(_root, PodId, hooks);
        renderCount = 0;
        MemoPodFrozenPrompt firstPrompt = pod.FrozenPrompt;
        MemoPodStorePaths paths = MemoPodStoreLayout.Resolve(_root, PodId);
        byte[] before = File.ReadAllBytes(paths.DocumentPath);

        pod.ResumeEditing();
        await pod.FreezeAsync();

        Assert.Equal(1, renderCount);
        Assert.Equal(0, publishCount);
        Assert.Equal(MemoPodPhase.Frozen, pod.Phase);
        Assert.Equal(firstPrompt.Sha256, pod.FrozenPrompt.Sha256);
        Assert.NotSame(firstPrompt, pod.FrozenPrompt);
        Assert.Equal(before, File.ReadAllBytes(paths.DocumentPath));
    }

    [Fact]
    public async Task RejectedMutationsDoNotMakeCleanEditablePodDirty() {
        MemoPod created = MemoPod.Create(_root, PodId, "topic");
        created.Append("memo");
        await created.FreezeAsync();

        int publishCount = 0;
        MemoPod pod = MemoPod.OpenForTesting(
            _root,
            PodId,
            new MemoPodLifecycleTestHooks(
                PublisherHooks: new MemoPodPublisherTestHooks(
                    BeforePublish: _ => publishCount++
                )
            )
        );
        pod.ResumeEditing();

        Assert.Throws<ArgumentException>(() => pod.Append(string.Empty));
        Assert.Throws<KeyNotFoundException>(
            () => pod.Remove(MemoId.Parse("m1:000000ff"))
        );
        await pod.FreezeAsync();

        Assert.Equal(0, publishCount);
        Assert.Equal(MemoPodPhase.Frozen, pod.Phase);
    }

    [Fact]
    public async Task PreparationFaultAndCancellationPreserveEditableWork() {
        bool failRender = true;
        bool cancelAfterRender = true;
        using var cancellation = new CancellationTokenSource();
        MemoPod pod = MemoPod.CreateForTesting(
            _root,
            PodId,
            "topic",
            new MemoPodLifecycleTestHooks(
                BeforeRender: _ => {
                    if (failRender) {
                        failRender = false;
                        throw new IOException("render fixture");
                    }
                },
                AfterRenderBeforePublish: _ => {
                    if (cancelAfterRender) {
                        cancelAfterRender = false;
                        cancellation.Cancel();
                    }
                }
            )
        );
        MemoId id = pod.Append("survives preparation failures");

        await Assert.ThrowsAsync<IOException>(() => pod.FreezeAsync());
        Assert.Equal(MemoPodPhase.Editable, pod.Phase);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pod.FreezeAsync(cancellation.Token)
        );
        Assert.Equal(MemoPodPhase.Editable, pod.Phase);

        await pod.FreezeAsync();
        Assert.Equal(id, Assert.Single(pod.List()).Id);
        Assert.Equal(MemoPodPhase.Frozen, pod.Phase);
    }

    [Fact]
    public async Task InitiallyCanceledFreezeStopsBeforeCandidateRendering() {
        int renderCount = 0;
        MemoPod pod = MemoPod.CreateForTesting(
            _root,
            PodId,
            "topic",
            new MemoPodLifecycleTestHooks(
                BeforeRender: _ => renderCount++
            )
        );
        pod.Append("memo");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pod.FreezeAsync(cancellation.Token)
        );

        Assert.Equal(0, renderCount);
        Assert.Equal(MemoPodPhase.Editable, pod.Phase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public async Task PublisherPreparationFailureIsTypedAndRetryable() {
        bool fail = true;
        MemoPod pod = MemoPod.CreateForTesting(
            _root,
            PodId,
            "topic",
            new MemoPodLifecycleTestHooks(
                PublisherHooks: new MemoPodPublisherTestHooks(
                    BeforePublish: _ => {
                        if (fail) {
                            fail = false;
                            throw new IOException("publisher fixture");
                        }
                    }
                )
            )
        );
        pod.Append("memo");

        MemoPodPersistenceException failure =
            await Assert.ThrowsAsync<MemoPodPersistenceException>(
                () => pod.FreezeAsync()
            );
        Assert.Equal(
            MemoPodPersistenceFailureKind.IoFailure,
            failure.FailureKind
        );
        Assert.Equal(MemoPodPhase.Editable, pod.Phase);

        await pod.FreezeAsync();
        Assert.Equal(MemoPodPhase.Frozen, pod.Phase);
    }

    [Fact]
    public async Task CancellationAfterInstallDoesNotReversePublished() {
        using var cancellation = new CancellationTokenSource();
        MemoPod pod = MemoPod.CreateForTesting(
            _root,
            PodId,
            "topic",
            new MemoPodLifecycleTestHooks(
                PublisherHooks: new MemoPodPublisherTestHooks(
                    AfterInstallBeforeDirectoryFsync: _ =>
                        cancellation.Cancel()
                )
            )
        );
        pod.Append("memo");

        await pod.FreezeAsync(cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(MemoPodPhase.Frozen, pod.Phase);
        Assert.Single(MemoPod.Open(_root, PodId).List());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AfterFsyncCancellationAndDiagnosticRemainPublished(
        bool throwDiagnostic
    ) {
        using var cancellation = new CancellationTokenSource();
        MemoPod pod = MemoPod.CreateForTesting(
            _root,
            PodId,
            "topic",
            new MemoPodLifecycleTestHooks(
                PublisherHooks: new MemoPodPublisherTestHooks(
                    AfterDirectoryFsync: _ => {
                        cancellation.Cancel();
                        if (throwDiagnostic) {
                            throw new IOException("post-sync diagnostic");
                        }
                    }
                )
            )
        );
        pod.Append("memo");

        await pod.FreezeAsync(cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(MemoPodPhase.Frozen, pod.Phase);
        Assert.Single(MemoPod.Open(_root, PodId).List());
    }

    [Fact]
    public async Task IndeterminateCommitInvalidatesEveryInstanceApi() {
        MemoPod pod = MemoPod.CreateForTesting(
            _root,
            PodId,
            "topic",
            new MemoPodLifecycleTestHooks(
                PublisherHooks: new MemoPodPublisherTestHooks(
                    AfterInstallBeforeDirectoryFsync: _ =>
                        throw new IOException("indeterminate fixture")
                )
            )
        );
        MemoId id = pod.Append("memo");

        MemoPodCommitIndeterminateException failure =
            await Assert.ThrowsAsync<MemoPodCommitIndeterminateException>(
                () => pod.FreezeAsync()
            );
        Assert.Equal(
            MemoPodPersistenceFailureKind.CommitIndeterminate,
            failure.FailureKind
        );

        AssertInvalidated(() => _ = pod.PodId);
        AssertInvalidated(() => _ = pod.Topic);
        AssertInvalidated(() => _ = pod.Phase);
        AssertInvalidated(() => pod.Append("later"));
        AssertInvalidated(() => pod.Remove(id));
        AssertInvalidated(() => pod.Get(id));
        AssertInvalidated(() => pod.TryGet(id, out _));
        AssertInvalidated(() => pod.List());
        AssertInvalidated(() => _ = pod.FrozenPrompt);
        AssertInvalidated(() => pod.ResumeEditing());
        await Assert.ThrowsAsync<MemoPodInvalidatedException>(
            () => pod.FreezeAsync()
        );

        Assert.Equal(
            "memo",
            Assert.Single(MemoPod.Open(_root, PodId).List()).ExactText
        );
    }

    [Fact]
    public async Task ReplaceMissingIsNotFoundAndKeepsEditableWork() {
        MemoPod created = MemoPod.Create(_root, PodId, "topic");
        created.Append("old");
        await created.FreezeAsync();

        MemoPod pod = MemoPod.Open(_root, PodId);
        pod.ResumeEditing();
        pod.Append("new");
        File.Delete(
            MemoPodStoreLayout.Resolve(_root, PodId).DocumentPath
        );

        MemoPodPersistenceException failure =
            await Assert.ThrowsAsync<MemoPodPersistenceException>(
                () => pod.FreezeAsync()
            );
        Assert.Equal(
            MemoPodPersistenceFailureKind.NotFound,
            failure.FailureKind
        );
        Assert.Equal(MemoPodPhase.Editable, pod.Phase);
        Assert.Equal(2, pod.List().Length);
    }

    public void Dispose() {
        if (Directory.Exists(_root)) {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static void AssertInvalidated(Action operation)
        => Assert.Throws<MemoPodInvalidatedException>(operation);
}
