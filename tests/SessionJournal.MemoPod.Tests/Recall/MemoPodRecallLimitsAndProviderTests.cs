using System.Net.Http;
using System.Text.Json;
using Atelia.Completion.Abstractions;

namespace Atelia.SessionJournal.MemoPod.Tests.Recall;

public sealed class MemoPodRecallLimitsAndProviderTests {
    [Fact]
    public void OptionsValidateEveryClosedRange() {
        var minimum = new MemoRecallOptions(1, 1, 1, 1);
        var maximum = new MemoRecallOptions(
            MemoPodLimits.MaximumRecallResultCount,
            MemoPodLimits.MaximumRecallMaxTokens,
            MemoPodLimits.MaximumRenderedPromptUtf8Bytes,
            MemoPodLimits.MaximumActiveExactTextUtf8Bytes
        );

        Assert.Equal(1, minimum.MaxResults);
        Assert.Equal(1, minimum.MaxTokens);
        Assert.Equal(1, minimum.MaximumFrozenPromptUtf8Bytes);
        Assert.Equal(1, minimum.MaximumHydratedExactTextUtf8Bytes);
        Assert.Equal(
            MemoPodLimits.MaximumRecallResultCount,
            maximum.MaxResults
        );
        Assert.Equal(
            MemoPodLimits.MaximumRecallMaxTokens,
            maximum.MaxTokens
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MemoRecallOptions(0, 1, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MemoRecallOptions(
                MemoPodLimits.MaximumRecallResultCount + 1,
                1,
                1,
                1
            ));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MemoRecallOptions(1, 0, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MemoRecallOptions(
                1,
                MemoPodLimits.MaximumRecallMaxTokens + 1,
                1,
                1
            ));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MemoRecallOptions(1, 1, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MemoRecallOptions(
                1,
                1,
                MemoPodLimits.MaximumRenderedPromptUtf8Bytes + 1,
                1
            ));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MemoRecallOptions(1, 1, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MemoRecallOptions(
                1,
                1,
                1,
                MemoPodLimits.MaximumActiveExactTextUtf8Bytes + 1
            ));
    }

    [Fact]
    public async Task QueryValidationIsStrictUtf8BoundedAndPreProvider() {
        using MemoPodRecallFixture fixture =
            await MemoPodRecallFixture.CreateAsync(
                exactTexts: ["memo"]
            );
        var client = new FakeMemoRecallCompletionClient();
        string?[] invalidQueries = [
            null,
            string.Empty,
            " \t\r\n",
            "\ud800",
            new string('é',
                (MemoPodLimits.MaximumRecallQueryUtf8Bytes / 2) + 1),
        ];

        foreach (string? query in invalidQueries) {
            await Assert.ThrowsAnyAsync<ArgumentException>(() =>
                fixture.Pod.RecallAsync(
                    client,
                    "model",
                    query!,
                    MemoPodRecallFixture.Options()
                ));
        }
        Assert.Equal(0, client.InvocationCount);

        string maximumQuery = new(
            'x',
            MemoPodLimits.MaximumRecallQueryUtf8Bytes
        );
        _ = await fixture.Pod.RecallAsync(
            client,
            "model",
            maximumQuery,
            MemoPodRecallFixture.Options()
        );
        Assert.Equal(1, client.InvocationCount);
    }

    [Fact]
    public async Task PromptAndHydrationByteCapsAreLocalLimitFailures() {
        using MemoPodRecallFixture fixture =
            await MemoPodRecallFixture.CreateAsync(
                exactTexts: ["éé"]
            );
        var client = new FakeMemoRecallCompletionClient();
        MemoPodFrozenPrompt prompt = fixture.Pod.FrozenPrompt;

        MemoRecallException promptFailure =
            await Assert.ThrowsAsync<MemoRecallException>(() =>
                fixture.Pod.RecallAsync(
                    client,
                    "model",
                    "query",
                    MemoPodRecallFixture.Options(
                        maximumFrozenPromptUtf8Bytes:
                            prompt.Utf8Length - 1
                    )
                ));
        Assert.Equal(
            MemoRecallFailureKind.LocalLimitExceeded,
            promptFailure.FailureKind
        );
        Assert.Equal(0, client.InvocationCount);

        string arguments =
            $"{{\"memoIds\":[\"{fixture.Ids[0].Value}\"]}}";
        client.Handler = (self, request, _) => Task.FromResult(
            self.Result(request, [self.ToolCall(arguments)])
        );
        MemoRecallException hydrationFailure =
            await Assert.ThrowsAsync<MemoRecallException>(() =>
                fixture.Pod.RecallAsync(
                    client,
                    "model",
                    "query",
                    MemoPodRecallFixture.Options(
                        maximumHydratedExactTextUtf8Bytes: 3
                    )
                ));
        Assert.Equal(
            MemoRecallFailureKind.LocalLimitExceeded,
            hydrationFailure.FailureKind
        );
        Assert.Equal(1, client.InvocationCount);
        Assert.Equal(MemoPodPhase.Frozen, fixture.Pod.Phase);
        Assert.Same(prompt, fixture.Pod.FrozenPrompt);

        using var ignoredByProvider = new CancellationTokenSource();
        client.Handler = (self, request, _) => {
            ignoredByProvider.Cancel();
            return Task.FromResult(self.Result(
                request,
                [self.ToolCall("{\"memoIds\":[]}")]
            ));
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Pod.RecallAsync(
                client,
                "model",
                "query",
                MemoPodRecallFixture.Options(),
                ignoredByProvider.Token
            ));
        Assert.Equal(2, client.InvocationCount);
        Assert.Same(prompt, fixture.Pod.FrozenPrompt);
    }

    [Fact]
    public async Task CallerCancellationPropagatesWithoutRetryOrStateChange() {
        using MemoPodRecallFixture fixture =
            await MemoPodRecallFixture.CreateAsync(
                exactTexts: ["memo"]
            );
        var client = new FakeMemoRecallCompletionClient();
        MemoPodFrozenPrompt prompt = fixture.Pod.FrozenPrompt;
        using var beforeCall = new CancellationTokenSource();
        beforeCall.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Pod.RecallAsync(
                client,
                "model",
                "query",
                MemoPodRecallFixture.Options(),
                beforeCall.Token
            ));
        Assert.Equal(0, client.InvocationCount);

        using var duringCall = new CancellationTokenSource();
        client.Handler = (_, _, _) => {
            duringCall.Cancel();
            return Task.FromCanceled<CompletionResult>(duringCall.Token);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Pod.RecallAsync(
                client,
                "model",
                "query",
                MemoPodRecallFixture.Options(),
                duringCall.Token
            ));
        Assert.Equal(1, client.InvocationCount);
        Assert.Equal(MemoPodPhase.Frozen, fixture.Pod.Phase);
        Assert.Same(prompt, fixture.Pod.FrozenPrompt);
    }

    [Fact]
    public async Task ProviderWhitelistIsTypedAndNeverRetried() {
        using MemoPodRecallFixture fixture =
            await MemoPodRecallFixture.CreateAsync(
                exactTexts: ["memo"]
            );
        MemoPodFrozenPrompt prompt = fixture.Pod.FrozenPrompt;
        Exception[] failures = [
            new HttpRequestException("http"),
            new IOException("io"),
            new JsonException("json"),
            new TimeoutException("timeout"),
            new NotSupportedException("unsupported"),
            new OperationCanceledException("provider cancellation"),
        ];

        foreach (Exception providerException in failures) {
            var client = new FakeMemoRecallCompletionClient {
                Handler = (_, _, _) =>
                    Task.FromException<CompletionResult>(providerException)
            };

            MemoRecallException failure =
                await Assert.ThrowsAsync<MemoRecallException>(() =>
                    fixture.Pod.RecallAsync(
                        client,
                        "model",
                        "query",
                        MemoPodRecallFixture.Options()
                    ));
            Assert.Equal(
                MemoRecallFailureKind.ProviderFailure,
                failure.FailureKind
            );
            Assert.Same(providerException, failure.InnerException);
            Assert.Equal(1, client.InvocationCount);
            Assert.Equal(MemoPodPhase.Frozen, fixture.Pod.Phase);
            Assert.Same(prompt, fixture.Pod.FrozenPrompt);
        }
    }

    [Fact]
    public async Task UnlistedProviderExceptionIsNotBroadlyWrapped() {
        using MemoPodRecallFixture fixture =
            await MemoPodRecallFixture.CreateAsync(
                exactTexts: ["memo"]
            );
        var programmingFailure = new InvalidOperationException("bug");
        var client = new FakeMemoRecallCompletionClient {
            Handler = (_, _, _) =>
                Task.FromException<CompletionResult>(programmingFailure)
        };

        InvalidOperationException actual =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Pod.RecallAsync(
                    client,
                    "model",
                    "query",
                    MemoPodRecallFixture.Options()
                ));

        Assert.Same(programmingFailure, actual);
        Assert.Equal(1, client.InvocationCount);
    }

    [Fact]
    public async Task SameFrozenPromptReferenceDefinesRecallEpoch() {
        using MemoPodRecallFixture fixture =
            await MemoPodRecallFixture.CreateAsync(
                exactTexts: ["memo"]
            );
        MemoPodFrozenPrompt firstEpoch = fixture.Pod.FrozenPrompt;
        var client = new FakeMemoRecallCompletionClient {
            Handler = (self, request, _) => {
                fixture.Pod.ResumeEditing();
                fixture.Pod.FreezeAsync().GetAwaiter().GetResult();
                return Task.FromResult(self.Result(
                    request,
                    [self.ToolCall("{\"memoIds\":[]}")]
                ));
            }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Pod.RecallAsync(
                client,
                "model",
                "query",
                MemoPodRecallFixture.Options()
            ));

        Assert.Equal(MemoPodPhase.Frozen, fixture.Pod.Phase);
        Assert.NotSame(firstEpoch, fixture.Pod.FrozenPrompt);
        Assert.Equal(1, client.InvocationCount);
    }

    [Fact]
    public async Task LifecycleAndArgumentGatesPrecedeProviderCall() {
        string root = Path.Combine(
            Path.GetTempPath(),
            "atelia-memo-pod-recall-gate-tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(root);
        try {
            MemoPod editable = MemoPod.Create(
                root,
                MemoPodRecallFixture.PodId,
                "topic"
            );
            var client = new FakeMemoRecallCompletionClient();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                editable.RecallAsync(
                    null!,
                    string.Empty,
                    string.Empty,
                    null!
                ));
            Assert.Equal(0, client.InvocationCount);

            editable.Append("memo");
            await editable.FreezeAsync();
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                editable.RecallAsync(
                    null!,
                    "model",
                    "query",
                    MemoPodRecallFixture.Options()
                ));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                editable.RecallAsync(
                    client,
                    string.Empty,
                    "query",
                    MemoPodRecallFixture.Options()
                ));
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                editable.RecallAsync(
                    client,
                    "model",
                    "query",
                    null!
                ));
            Assert.Equal(0, client.InvocationCount);

            MemoPod invalidated = MemoPod.CreateForTesting(
                root,
                MemoPodId.Parse("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
                "topic",
                new MemoPodLifecycleTestHooks(
                    PublisherHooks: new MemoPodPublisherTestHooks(
                        AfterInstallBeforeDirectoryFsync: _ =>
                            throw new IOException("indeterminate fixture")
                    )
                )
            );
            invalidated.Append("memo");
            await Assert.ThrowsAsync<
                MemoPodCommitIndeterminateException
            >(() => invalidated.FreezeAsync());
            await Assert.ThrowsAsync<MemoPodInvalidatedException>(() =>
                invalidated.RecallAsync(
                    null!,
                    string.Empty,
                    string.Empty,
                    null!
                ));
        }
        finally {
            Directory.Delete(root, recursive: true);
        }
    }
}
