using Atelia.Completion.Abstractions;

namespace Atelia.MemoPod.Tests.Recall;

public sealed class MemoPodRecallResultTests {
    public static TheoryData<string, string[]> OrderedSelections { get; }
        = new() {
            { "{\"memoIds\":[]}", [] },
            { "{\"memoIds\":[\"m1:00000002\"]}", ["m1:00000002"] },
            {
                "{\"memoIds\":[\"m1:00000003\",\"m1:00000001\",\"m1:00000002\"]}",
                ["m1:00000003", "m1:00000001", "m1:00000002"]
            },
        };

    [Theory]
    [MemberData(nameof(OrderedSelections))]
    public async Task NoneOneAndManyPreserveModelOrder(
        string arguments,
        string[] expectedIds
    ) {
        using MemoPodRecallFixture fixture =
            await MemoPodRecallFixture.CreateAsync(
                exactTexts: ["first", "second", "third"]
            );
        var client = new FakeMemoRecallCompletionClient {
            Handler = (self, request, _) => Task.FromResult(
                self.Result(request, [self.ToolCall(arguments)])
            )
        };

        MemoRecallResult result = await fixture.Pod.RecallAsync(
            client,
            "model",
            "query",
            MemoPodRecallFixture.Options()
        );

        Assert.Equal(
            expectedIds,
            result.Memos.Select(static memo => memo.Id.Value).ToArray()
        );
        Assert.Equal(fixture.Pod.FrozenPrompt.Sha256, result.FrozenPromptSha256);
    }

    [Fact]
    public async Task UsageCopiesNormalizedFieldsAndDropsProviderDiagnostics() {
        using MemoPodRecallFixture fixture =
            await MemoPodRecallFixture.CreateAsync(
                exactTexts: ["memo"]
            );
        var providerUsage = new CompletionUsage(
            uncachedInputTokens: 11,
            cacheCreationInputTokens: 12,
            cacheReadInputTokens: 13,
            outputTokens: 14,
            promptCache: new PromptCacheTelemetry(
                PromptCacheRequestStatus.Requested,
                PromptCacheSupportStatus.Supported,
                PromptCacheObservationStatus.Complete,
                new Dictionary<string, string> {
                    ["provider-sensitive"] = "discard me"
                }
            )
        );
        var client = new FakeMemoRecallCompletionClient {
            Handler = (self, request, _) => Task.FromResult(
                self.Result(
                    request,
                    [self.ToolCall("{\"memoIds\":[]}")],
                    errors: Array.Empty<string>(),
                    usage: providerUsage
                )
            )
        };

        MemoRecallResult result = await fixture.Pod.RecallAsync(
            client,
            "model",
            "query",
            MemoPodRecallFixture.Options()
        );

        Assert.NotSame(providerUsage, result.Usage);
        Assert.Equal(11, result.Usage.UncachedInputTokens);
        Assert.Equal(12, result.Usage.CacheCreationInputTokens);
        Assert.Equal(13, result.Usage.CacheReadInputTokens);
        Assert.Equal(14, result.Usage.OutputTokens);
        Assert.Equal(
            PromptCacheRequestStatus.Requested,
            result.Usage.PromptCache.RequestStatus
        );
        Assert.Equal(
            PromptCacheSupportStatus.Supported,
            result.Usage.PromptCache.SupportStatus
        );
        Assert.Equal(
            PromptCacheObservationStatus.Complete,
            result.Usage.PromptCache.ObservationStatus
        );
        Assert.Null(result.Usage.PromptCache.ProviderDiagnostics);
    }

    [Fact]
    public async Task ResultRemainsSelfContainedAfterResumeAndRemoval() {
        string root = Path.Combine(
            Path.GetTempPath(),
            "atelia-memo-pod-recall-result-tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(root);
        MemoPod pod = MemoPod.Create(root, MemoPodRecallFixture.PodId, "topic");
        MemoId id = pod.Append(
            "remember me",
            title: "Initial title",
            gist: "Initial gist",
            summary: "Initial summary"
        );
        await pod.FreezeAsync();
        pod.ResumeEditing();
        pod.UpdateDerivedInfo(
            id,
            title: "Durable detail",
            gist: "Remember this",
            summary: "The result must keep the latest derived info after removal."
        );
        await pod.FreezeAsync();
        using MemoPodRecallFixture fixture =
            new(root, pod, [id]);
        string arguments =
            $"{{\"memoIds\":[\"{fixture.Ids[0].Value}\"]}}";
        var client = new FakeMemoRecallCompletionClient {
            Handler = (self, request, _) => Task.FromResult(
                self.Result(request, [self.ToolCall(arguments)])
            )
        };

        MemoRecallResult result = await fixture.Pod.RecallAsync(
            client,
            "model",
            "query",
            MemoPodRecallFixture.Options()
        );
        fixture.Pod.ResumeEditing();
        fixture.Pod.Remove(fixture.Ids[0]);

        Memo recalled = Assert.Single(result.Memos);
        Assert.Equal(fixture.Ids[0], recalled.Id);
        Assert.Equal("Durable detail", recalled.Title);
        Assert.Equal("Remember this", recalled.Gist);
        Assert.Equal(
            "The result must keep the latest derived info after removal.",
            recalled.Summary
        );
        Assert.Equal("remember me", recalled.ExactText);
        Assert.Empty(fixture.Pod.List());
    }

    [Fact]
    public async Task UnknownAndRemovedIdsAreInvalidModelOutput() {
        using MemoPodRecallFixture fixture =
            await MemoPodRecallFixture.CreateAsync(
                exactTexts: ["active", "to remove"]
            );
        MemoId removed = fixture.Ids[1];
        fixture.Pod.ResumeEditing();
        fixture.Pod.Remove(removed);
        await fixture.Pod.FreezeAsync();
        string[] selected = [removed.Value, "m1:000000ff"];

        foreach (string id in selected) {
            var client = new FakeMemoRecallCompletionClient {
                Handler = (self, request, _) => Task.FromResult(
                    self.Result(
                        request,
                        [self.ToolCall(
                            $"{{\"memoIds\":[\"{id}\"]}}"
                        )]
                    )
                )
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
                MemoRecallFailureKind.InvalidModelOutput,
                failure.FailureKind
            );
            Assert.Equal(MemoPodPhase.Frozen, fixture.Pod.Phase);
        }
    }

    [Fact]
    public async Task MaximumCanonicalMemoIdCanReopenAndHydrate() {
        string root = Path.Combine(
            Path.GetTempPath(),
            "atelia-memo-pod-recall-max-id-tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(root);
        try {
            MemoId maximumId = MemoId.FromOrdinal(uint.MaxValue);
            var document = new MemoPodDocument(
                MemoPodRecallFixture.PodId,
                "maximum ID",
                MemoPodDocument.ExhaustedNextMemoOrdinal,
                [new Memo(maximumId, "last legal memo")]
            );
            MemoPodPublishResult publish = MemoPodDocumentPublisher.Publish(
                root,
                document,
                MemoPodPublishMode.CreateNew
            );
            Assert.Equal(
                MemoPodPublishSettlement.Published,
                publish.Settlement
            );

            MemoPod pod = MemoPod.Open(root, MemoPodRecallFixture.PodId);
            var client = new FakeMemoRecallCompletionClient {
                Handler = (self, request, _) => Task.FromResult(
                    self.Result(
                        request,
                        [self.ToolCall(
                            "{\"memoIds\":[\"m1:ffffffff\"]}"
                        )]
                    )
                )
            };

            MemoRecallResult result = await pod.RecallAsync(
                client,
                "model",
                "query",
                MemoPodRecallFixture.Options()
            );

            Memo recalled = Assert.Single(result.Memos);
            Assert.Equal(maximumId, recalled.Id);
            Assert.Equal("last legal memo", recalled.ExactText);
        }
        finally {
            Directory.Delete(root, recursive: true);
        }
    }
}
