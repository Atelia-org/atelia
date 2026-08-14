using System.Collections.Concurrent;
using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.RecapGrid.Manager;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Runtime.Tests;

public sealed class RuntimeRenderingAndSchedulingTests {
    [Fact]
    public async Task Renderer_ExposesOnlyPriorWhitelistAndVisibleHistory() {
        var descriptor = new CompletionDescriptor(
            "history-provider",
            "history-api",
            "history-model"
        );
        FrozenRowBatch batch = RuntimeTestFixture.Batch(
            history: [
                new SessionContextHeader(
                    "system-visible",
                    "observation-visible",
                    new ActionMessage([
                        new ActionBlock.Text("header-before<think>hidden-header</think>header-after"),
                        new ActionBlock.TextReasoningBlock("hidden-reasoning", descriptor)
                    ])
                ),
                new ActionMessage([
                    new ActionBlock.Text("before<think>hidden-inline</think>after"),
                    new ActionBlock.TextReasoningBlock("hidden-native", descriptor),
                    new ActionBlock.ToolCall(new RawToolCall(
                        "visible_tool",
                        "tool-1",
                        "{}"
                    ))
                ]),
                new ToolResultsMessage(
                    "tool-visible",
                    [ToolResult.FromText(
                        "visible_tool",
                        "tool-1",
                        ToolExecutionStatus.Success,
                        "result-visible"
                    )]
                )
            ]
        );
        CompletionRequest? captured = null;
        ScriptedInvoker? invoker = null;
        invoker = new ScriptedInvoker((request, _) => {
            captured = request;
            return ValueTask.FromResult(
                RuntimeTestFixture.Updated(request, invoker!)
            );
        });
        using var runtime = Runtime(RuntimeTestFixture.Route(batch, invoker));

        _ = await runtime.ExecuteAsync(batch, default);

        Assert.NotNull(captured);
        var prior = Assert.IsType<ObservationMessage>(
            captured.PromptPrefix.SharedContextMessages[0]
        );
        Assert.Equal(
            "{\"schema\":\"atelia.recap.prior.v1\",\"columns\":[]}",
            prior.Content
        );
        string rendered = RenderForAssertion(
            captured.PromptPrefix.SharedContextMessages
        );
        Assert.Contains("system-visible", rendered, StringComparison.Ordinal);
        Assert.Contains("observation-visible", rendered, StringComparison.Ordinal);
        Assert.Contains("header-beforeheader-after", rendered, StringComparison.Ordinal);
        Assert.Contains("beforeafter", rendered, StringComparison.Ordinal);
        Assert.Contains("visible_tool", rendered, StringComparison.Ordinal);
        Assert.Contains("tool-visible", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden-", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("digest", rendered, StringComparison.OrdinalIgnoreCase);

        var tail = Assert.IsType<ObservationMessage>(
            Assert.Single(captured.TailMessages)
        );
        Assert.Contains("\"topic\":\"Question 0\"", tail.Content, StringComparison.Ordinal);
        Assert.Contains("\"carrier\":\"system\"", tail.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Digest", tail.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PriorProjection_RecomputedFromOneCellLoop_AndMetadataDoesNotLeak() {
        FrozenRowBatch updatedPrior = RuntimeTestFixture.BatchWithPrior(
            RecapCellOutcome.Updated,
            "same content"
        );
        FrozenRowBatch keptPrior = RuntimeTestFixture.BatchWithPrior(
            RecapCellOutcome.KeepUnchanged,
            "same content"
        );
        var captured = new ConcurrentQueue<CompletionRequest>();
        ScriptedInvoker? invoker = null;
        invoker = new ScriptedInvoker((request, _) => {
            captured.Enqueue(request);
            return ValueTask.FromResult(
                RuntimeTestFixture.Updated(request, invoker!)
            );
        });
        RecapCompletionRoute route = RuntimeTestFixture.Route(
            updatedPrior,
            invoker
        );
        using var runtime = Runtime(route);

        _ = await runtime.ExecuteAsync(updatedPrior, default);
        _ = await runtime.ExecuteAsync(keptPrior, default);

        CompletionRequest[] requests = captured.ToArray();
        Assert.Equal(2, requests.Length);
        var first = Assert.IsType<ObservationMessage>(
            requests[0].PromptPrefix.SharedContextMessages[0]
        );
        var second = Assert.IsType<ObservationMessage>(
            requests[1].PromptPrefix.SharedContextMessages[0]
        );
        Assert.Equal(first.Content, second.Content);
        Assert.Equal(
            "{\"schema\":\"atelia.recap.prior.v1\",\"columns\":[{\"logicalColumnId\":\"case.column-0\",\"content\":\"same content\"}]}",
            first.Content
        );
        Assert.DoesNotContain("cell", first.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outcome", first.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task KeepUnchanged_RequiresExactSameColumnPrior() {
        FrozenRowBatch withPrior = RuntimeTestFixture.BatchWithPrior();
        FrozenRowBatch firstRow = RuntimeTestFixture.Batch();
        ScriptedInvoker? invoker = null;
        invoker = new ScriptedInvoker((request, _) => ValueTask.FromResult(
            RuntimeTestFixture.Result(
                request,
                invoker!,
                "{\"outcome\":\"keep-unchanged\",\"content\":null}"
            )
        ));
        RecapCompletionRoute route = RuntimeTestFixture.Route(withPrior, invoker);
        using var runtime = Runtime(route);

        var accepted = Assert.IsType<RecapCellBatchExecutionResult.Completed>(
            await runtime.ExecuteAsync(withPrior, default)
        );
        Assert.IsType<RecapCellExecutionOutcome.KeepUnchanged>(
            Assert.Single(accepted.OrderedOutcomes)
        );

        var missing = Assert.IsType<RecapCellBatchExecutionResult.Completed>(
            await runtime.ExecuteAsync(firstRow, default)
        );
        var failed = Assert.IsType<RecapCellExecutionOutcome.Failed>(
            Assert.Single(missing.OrderedOutcomes)
        );
        Assert.Equal("KeepUnchangedInvalid", failed.Code);
    }

    [Fact]
    public async Task KeepUnchanged_NewOverlayColumnCannotReuseCrossColumnPrior() {
        FrozenRowBatch batch = RuntimeTestFixture.OverlayBatchWithNewColumn();
        ScriptedInvoker? invoker = null;
        invoker = new ScriptedInvoker((request, _) => ValueTask.FromResult(
            RuntimeTestFixture.Result(
                request,
                invoker!,
                "{\"outcome\":\"keep-unchanged\",\"content\":null}"
            )
        ));
        using var runtime = Runtime(RuntimeTestFixture.Route(batch, invoker));

        var completed = Assert.IsType<RecapCellBatchExecutionResult.Completed>(
            await runtime.ExecuteAsync(batch, default)
        );
        var failed = Assert.IsType<RecapCellExecutionOutcome.Failed>(
            Assert.Single(completed.OrderedOutcomes)
        );

        Assert.Equal("KeepUnchangedInvalid", failed.Code);
        Assert.Equal(1, invoker.CallCount);
    }

    [Fact]
    public async Task GroupLeaderSettlesBeforeFollowersBegin() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch(columnCount: 3);
        var leaderStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseLeader = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        ScriptedInvoker? invoker = null;
        invoker = new ScriptedInvoker(async (request, _) => {
            int call = invoker!.CallCount;
            if (call == 1) {
                leaderStarted.TrySetResult();
                await releaseLeader.Task;
            }
            return RuntimeTestFixture.Updated(request, invoker);
        });
        using var runtime = Runtime(RuntimeTestFixture.Route(
            batch,
            invoker,
            maximumConcurrency: 3
        ));

        Task<RecapCellBatchExecutionResult> execution = runtime
            .ExecuteAsync(batch, default).AsTask();
        await leaderStarted.Task;
        await Task.Delay(30);
        Assert.Equal(1, invoker.CallCount);
        releaseLeader.TrySetResult();
        var completed = Assert.IsType<RecapCellBatchExecutionResult.Completed>(
            await execution
        );

        Assert.Equal(3, invoker.CallCount);
        Assert.Equal(3, completed.OrderedOutcomes.Count);
    }

    [Fact]
    public async Task CatchableLeaderFailure_ReleasesFollowers() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch(columnCount: 3);
        ScriptedInvoker? invoker = null;
        invoker = new ScriptedInvoker((request, _) => {
            if (invoker!.CallCount == 1) {
                throw new IOException("leader failed");
            }
            return ValueTask.FromResult(
                RuntimeTestFixture.Updated(request, invoker)
            );
        });
        using var runtime = Runtime(RuntimeTestFixture.Route(batch, invoker));

        var completed = Assert.IsType<RecapCellBatchExecutionResult.Completed>(
            await runtime.ExecuteAsync(batch, default)
        );

        Assert.Equal(3, invoker.CallCount);
        Assert.IsType<RecapCellExecutionOutcome.Failed>(completed.OrderedOutcomes[0]);
        Assert.IsType<RecapCellExecutionOutcome.Updated>(completed.OrderedOutcomes[1]);
        Assert.IsType<RecapCellExecutionOutcome.Updated>(completed.OrderedOutcomes[2]);
        AssertCallCountEquivalent(invoker, completed);
    }

    [Fact]
    public async Task CallerCancellationAfterLeaderStart_DoesNotDispatchFollowers() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch(columnCount: 3);
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var invoker = new ScriptedInvoker(async (_, token) => {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        });
        using var runtime = Runtime(RuntimeTestFixture.Route(batch, invoker));
        using var cancellation = new CancellationTokenSource();

        Task<RecapCellBatchExecutionResult> execution = runtime
            .ExecuteAsync(batch, cancellation.Token).AsTask();
        await started.Task;
        cancellation.Cancel();
        var completed = Assert.IsType<RecapCellBatchExecutionResult.Completed>(
            await execution
        );

        Assert.Equal(1, invoker.CallCount);
        Assert.IsType<RecapCellExecutionOutcome.Failed>(completed.OrderedOutcomes[0]);
        Assert.All(completed.OrderedOutcomes.Skip(1), static outcome =>
            Assert.IsType<RecapCellExecutionOutcome.NotStartedDueToCallerCancellation>(outcome));
        AssertCallCountEquivalent(invoker, completed);
    }

    [Fact]
    public async Task FatalLeader_DrainsStartedLeadersAndStopsAllFollowers() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch(
            columnCount: 4,
            semanticModelGroupSize: 2
        );
        var siblingStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseSibling = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var fatal = new ScriptedInvoker(async (_, _) => {
            await siblingStarted.Task;
            throw new OutOfMemoryException("fatal-test");
        });
        ScriptedInvoker? sibling = null;
        sibling = new ScriptedInvoker(async (request, _) => {
            siblingStarted.TrySetResult();
            await releaseSibling.Task;
            return RuntimeTestFixture.Updated(request, sibling!);
        });
        RecapCompletionRoute first = RuntimeTestFixture.Route(
            batch,
            fatal,
            workIndex: 0
        );
        RecapCompletionRoute second = RuntimeTestFixture.Route(
            batch,
            sibling,
            workIndex: 2
        );
        using var runtime = Runtime(first, second);

        Task<RecapCellBatchExecutionResult> execution = runtime
            .ExecuteAsync(batch, default).AsTask();
        await siblingStarted.Task;
        await Task.Delay(30);
        Assert.False(execution.IsCompleted);
        releaseSibling.TrySetResult();

        OutOfMemoryException exception = await Assert.ThrowsAsync<
            OutOfMemoryException>(() => execution);
        Assert.Equal("fatal-test", exception.Message);
        Assert.Equal(1, fatal.CallCount);
        Assert.Equal(1, sibling.CallCount);
    }

    [Fact]
    public async Task DisposeFromInvoker_IsReentrantAndOwnedInvokerDisposesOnce() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch();
        RecapCompletionRuntime? runtime = null;
        DisposableInvoker? invoker = null;
        invoker = new DisposableInvoker(async (request, _) => {
            await runtime!.DisposeAsync();
            return RuntimeTestFixture.Updated(request, invoker!);
        });
        RecapCompletionRoute route = RuntimeTestFixture.Route(batch, invoker);
        runtime = Runtime(route);

        var completed = Assert.IsType<RecapCellBatchExecutionResult.Completed>(
            await runtime.ExecuteAsync(batch, default)
        );
        Assert.IsType<RecapCellExecutionOutcome.Updated>(
            Assert.Single(completed.OrderedOutcomes)
        );
        Assert.Equal(1, invoker.DisposeCount);
        Assert.IsType<RecapCellBatchExecutionResult.RejectedBeforeDispatch>(
            await runtime.ExecuteAsync(batch, default)
        );
        runtime.Dispose();
        Assert.Equal(1, invoker.DisposeCount);
    }

    [Fact]
    public async Task NormalAwaitThenDispose_DoesNotInheritOperationScope() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch();
        DisposableInvoker? invoker = null;
        invoker = new DisposableInvoker((request, _) => ValueTask.FromResult(
            RuntimeTestFixture.Updated(request, invoker!)
        ));
        RecapCompletionRuntime runtime = Runtime(
            RuntimeTestFixture.Route(batch, invoker)
        );

        _ = await runtime.ExecuteAsync(batch, default);
        runtime.Dispose();

        Assert.Equal(1, invoker.DisposeCount);
        Assert.IsType<RecapCellBatchExecutionResult.RejectedBeforeDispatch>(
            await runtime.ExecuteAsync(batch, default)
        );
    }

    [Fact]
    public async Task SyncOwnedCleanup_CanReenterParentDisposeExactlyOnce() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch();
        RecapCompletionRuntime? runtime = null;
        DisposableInvoker? invoker = null;
        invoker = new DisposableInvoker(
            (request, _) => ValueTask.FromResult(
                RuntimeTestFixture.Updated(request, invoker!)
            ),
            () => runtime!.Dispose()
        );
        runtime = Runtime(RuntimeTestFixture.Route(batch, invoker));

        _ = await runtime.ExecuteAsync(batch, default);
        runtime.Dispose();
        runtime.Dispose();

        Assert.Equal(1, invoker.DisposeCount);
    }

    [Fact]
    public async Task AsyncOwnedCleanup_CanReenterParentDisposeAsyncExactlyOnce() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch();
        RecapCompletionRuntime? runtime = null;
        AsyncDisposableInvoker? invoker = null;
        invoker = new AsyncDisposableInvoker(
            (request, _) => ValueTask.FromResult(
                RuntimeTestFixture.Updated(request, invoker!)
            ),
            async () => await runtime!.DisposeAsync()
        );
        runtime = Runtime(RuntimeTestFixture.Route(batch, invoker));

        _ = await runtime.ExecuteAsync(batch, default);
        await runtime.DisposeAsync();
        await runtime.DisposeAsync();

        Assert.Equal(1, invoker.DisposeCount);
    }

    [Fact]
    public async Task OwnedAndBorrowedCompletionClients_HaveExplicitDisposal() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch();
        var ownedClient = new DisposableCompletionClient();
        var borrowedClient = new DisposableCompletionClient();
        var ownedInvoker = new CompletionClientRecapInvoker(
            ownedClient,
            RecapCompletionResourceOwnership.Owned
        );
        var borrowingInvoker = new CompletionClientRecapInvoker(
            borrowedClient,
            RecapCompletionResourceOwnership.Borrowed
        );
        RecapCompletionRoute ownedRoute = RecapCompletionRoute.Create(
            RuntimeTestFixture.Route(batch, ownedInvoker).Key,
            "owned-connection",
            "test-model",
            ownedInvoker,
            RecapCompletionResourceOwnership.Owned,
            1,
            TimeSpan.FromSeconds(5)
        );
        RecapCompletionRoute borrowedRoute = RecapCompletionRoute.Create(
            RuntimeTestFixture.Route(batch, borrowingInvoker).Key,
            "borrowed-connection",
            "test-model",
            borrowingInvoker,
            RecapCompletionResourceOwnership.Owned,
            1,
            TimeSpan.FromSeconds(5)
        );

        await ExecuteAndDisposeAsync(batch, ownedRoute);
        await ExecuteAndDisposeAsync(batch, borrowedRoute);

        Assert.Equal(1, ownedClient.DisposeCount);
        Assert.Equal(0, borrowedClient.DisposeCount);
        borrowedClient.Dispose();
        Assert.Equal(1, borrowedClient.DisposeCount);
    }

    [Fact]
    public async Task BorrowedInvokerRoute_IsNeverDisposedByRuntime() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch();
        DisposableInvoker? invoker = null;
        invoker = new DisposableInvoker((request, _) => ValueTask.FromResult(
            RuntimeTestFixture.Updated(request, invoker!)
        ));
        RecapCompletionRoute template = RuntimeTestFixture.Route(
            batch,
            invoker
        );
        RecapCompletionRoute borrowed = RecapCompletionRoute.Create(
            template.Key,
            template.ConnectionId,
            template.ModelId,
            invoker,
            RecapCompletionResourceOwnership.Borrowed,
            template.MaximumConcurrency,
            template.DispatchTimeout
        );
        RecapCompletionRuntime runtime = Runtime(borrowed);

        _ = await runtime.ExecuteAsync(batch, default);
        await runtime.DisposeAsync();

        Assert.Equal(0, invoker.DisposeCount);
        invoker.Dispose();
        Assert.Equal(1, invoker.DisposeCount);
    }

    [Fact]
    public async Task SameInvokerCannotMixOwnedAndBorrowedRoutes() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch(
            columnCount: 2,
            distinctSemanticModels: true
        );
        var invoker = new DisposableInvoker((_, _) =>
            throw new InvalidOperationException("must not dispatch"));
        RecapCompletionRoute owned = RuntimeTestFixture.Route(
            batch,
            invoker,
            workIndex: 0
        );
        RecapCompletionRoute second = RuntimeTestFixture.Route(
            batch,
            invoker,
            workIndex: 1
        );
        RecapCompletionRoute borrowed = RecapCompletionRoute.Create(
            second.Key,
            second.ConnectionId,
            second.ModelId,
            invoker,
            RecapCompletionResourceOwnership.Borrowed,
            second.MaximumConcurrency,
            second.DispatchTimeout
        );
        using var runtime = Runtime(owned, borrowed);

        var rejected = Assert.IsType<
            RecapCellBatchExecutionResult.RejectedBeforeDispatch
        >(await runtime.ExecuteAsync(batch, default));

        Assert.Equal("InvokerOwnershipConflict", rejected.Code);
        Assert.Equal(0, invoker.DisposeCount);
    }

    [Fact]
    public async Task OperationalTelemetry_CarriesBoundedWholeWorkEvidence() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch(columnCount: 2);
        var usage = new CompletionUsage(
            uncachedInputTokens: 7,
            cacheCreationInputTokens: 3,
            cacheReadInputTokens: 5,
            outputTokens: 11,
            promptCache: new PromptCacheTelemetry(
                PromptCacheRequestStatus.Requested,
                PromptCacheSupportStatus.Supported,
                PromptCacheObservationStatus.Complete
            )
        );
        ScriptedInvoker? invoker = null;
        invoker = new ScriptedInvoker((request, _) => ValueTask.FromResult(
            RuntimeTestFixture.Updated(request, invoker!) with { Usage = usage }
        ));
        var telemetry = new CapturingTelemetry();
        using var runtime = new RecapCompletionRuntime(
            new ScriptedResolver(_ => new RecapCompletionRouteResolution.Bound(
                RuntimeTestFixture.Route(batch, invoker)
            )),
            new RecapCompletionRuntimeOptions(
                new CompletionInvocationOptions {
                    PromptCacheReuseHint =
                        PromptCacheReuseHint.ReuseExpectedSoon
                }
            ),
            telemetry
        );

        _ = await runtime.ExecuteAsync(batch, default);

        RecapCompletionTelemetryEvent[] events = telemetry.Events.ToArray();
        Assert.Equal(2, events.Length);
        Assert.Equal(
            [RecapCompletionWorkRole.Leader, RecapCompletionWorkRole.Follower],
            events.Select(static value => value.Role)
        );
        foreach (RecapCompletionTelemetryEvent value in events) {
            FrozenRecapCellWork work = batch.OrderedMissingWork.Single(
                item => item.EvaluationKey.Digest == value.EvaluationKey
            );
            Assert.Equal(work.Family.Digest, value.FamilyDigest);
            Assert.Equal(work.Definition.Digest, value.DefinitionDigest);
            Assert.Equal("test-connection", value.ConnectionId);
            Assert.Equal("test-model", value.ModelId);
            Assert.Equal("test-provider", value.ProviderId);
            Assert.Equal("test-api-v1", value.ApiSpecId);
            Assert.Equal(
                work.EvaluationKey.HistorySegmentDigest.Value,
                value.HistorySegmentDigest
            );
            Assert.True(value.IsFirstRowPrior);
            Assert.Null(value.PriorProjectionDigest);
            Assert.True(value.ResultReceived);
            Assert.Equal(CompletionTerminationKind.Completed, value.Termination);
            Assert.Same(usage, value.Usage);
            Assert.Equal("updated", value.ProviderOutcome);
            Assert.Equal(
                PromptCacheReuseHint.ReuseExpectedSoon,
                value.CacheReuseHint
            );
            Assert.True(value.AdmissionWait >= TimeSpan.Zero);
            Assert.True(value.LaneWait >= TimeSpan.Zero);
        }
    }

    [Fact]
    public async Task ProviderExceptionDetail_IsStrictUtf8AndCodeOwnedBound() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch();
        var messages = new Queue<string>([
            new string('x', 4 * 1024 * 1024),
            "invalid-\uD800-detail"
        ]);
        var invoker = new ScriptedInvoker((_, _) =>
            throw new IOException(messages.Dequeue()));
        using var runtime = Runtime(RuntimeTestFixture.Route(batch, invoker));

        var bounded = Assert.IsType<RecapCellBatchExecutionResult.Completed>(
            await runtime.ExecuteAsync(batch, default)
        );
        string boundedDetail = Assert.IsType<RecapCellExecutionOutcome.Failed>(
            Assert.Single(bounded.OrderedOutcomes)
        ).Detail;
        Assert.Equal(4 * 1024, Encoding.UTF8.GetByteCount(boundedDetail));
        var invalid = Assert.IsType<RecapCellBatchExecutionResult.Completed>(
            await runtime.ExecuteAsync(batch, default)
        );
        Assert.Equal(
            "Provider returned an invalid diagnostic string.",
            Assert.IsType<RecapCellExecutionOutcome.Failed>(
                Assert.Single(invalid.OrderedOutcomes)
            ).Detail
        );
    }

    [Fact]
    public async Task DisposeAsync_ReallyAwaitsOperationAndAsyncOwnedDisposal() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch();
        var callStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseCall = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var disposalStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseDisposal = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        AsyncDisposableInvoker? invoker = null;
        invoker = new AsyncDisposableInvoker(
            async (request, _) => {
                callStarted.TrySetResult();
                await releaseCall.Task;
                return RuntimeTestFixture.Updated(request, invoker!);
            },
            async () => {
                disposalStarted.TrySetResult();
                await releaseDisposal.Task;
            }
        );
        RecapCompletionRuntime runtime = Runtime(
            RuntimeTestFixture.Route(batch, invoker)
        );
        Task<RecapCellBatchExecutionResult> execution = runtime
            .ExecuteAsync(batch, default).AsTask();
        await callStarted.Task;

        Task disposing = runtime.DisposeAsync().AsTask();
        Assert.False(disposing.IsCompleted);
        releaseCall.TrySetResult();
        await execution;
        await disposalStarted.Task;
        Assert.False(disposing.IsCompleted);
        releaseDisposal.TrySetResult();
        await disposing;

        Assert.Equal(1, invoker.DisposeCount);
    }

    [Fact]
    public async Task ResolverAndTelemetryDispose_AreReentrantWithoutSelfLock() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch();
        DisposableInvoker? invoker = null;
        invoker = new DisposableInvoker((request, _) => ValueTask.FromResult(
            RuntimeTestFixture.Updated(request, invoker!)
        ));
        RecapCompletionRoute route = RuntimeTestFixture.Route(batch, invoker);
        RecapCompletionRuntime? runtime = null;
        var telemetry = new CallbackTelemetry(() => runtime!.Dispose());
        runtime = new RecapCompletionRuntime(
            new ScriptedResolver(key => {
                runtime!.Dispose();
                return key == route.Key
                    ? new RecapCompletionRouteResolution.Bound(route)
                    : throw new InvalidOperationException();
            }),
            telemetry: telemetry
        );

        var completed = Assert.IsType<RecapCellBatchExecutionResult.Completed>(
            await runtime.ExecuteAsync(batch, default)
        );

        Assert.IsType<RecapCellExecutionOutcome.Updated>(
            Assert.Single(completed.OrderedOutcomes)
        );
        Assert.Equal(1, telemetry.Count);
        Assert.Equal(1, invoker.DisposeCount);
    }

    [Fact]
    public async Task SameFamilyDifferentExactRoutes_SharesPrefixButNotRouteAffinity() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch(
            columnCount: 2,
            distinctSemanticModels: true
        );
        CompletionPromptPrefix? firstPrefix = null;
        CompletionPromptPrefix? secondPrefix = null;
        ScriptedInvoker? firstInvoker = null;
        firstInvoker = new ScriptedInvoker((request, _) => {
            firstPrefix = request.PromptPrefix;
            return ValueTask.FromResult(
                RuntimeTestFixture.Updated(request, firstInvoker!)
            );
        });
        ScriptedInvoker? secondInvoker = null;
        secondInvoker = new ScriptedInvoker((request, _) => {
            secondPrefix = request.PromptPrefix;
            return ValueTask.FromResult(
                RuntimeTestFixture.Updated(request, secondInvoker!)
            );
        });
        RecapCompletionRoute first = RuntimeTestFixture.Route(
            batch,
            firstInvoker,
            workIndex: 0
        );
        RecapCompletionRoute second = RuntimeTestFixture.Route(
            batch,
            secondInvoker,
            workIndex: 1
        );
        using var runtime = Runtime(first, second);

        _ = await runtime.ExecuteAsync(batch, default);

        Assert.Same(firstPrefix, secondPrefix);
        Assert.Equal(1, firstInvoker.CallCount);
        Assert.Equal(1, secondInvoker.CallCount);
    }

    [Fact]
    public async Task ExternalDispose_DrainsEnteredCallThenDisposesOwnedInvoker() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        DisposableInvoker? invoker = null;
        invoker = new DisposableInvoker(async (request, _) => {
            started.TrySetResult();
            await release.Task;
            return RuntimeTestFixture.Updated(request, invoker!);
        });
        RecapCompletionRuntime runtime = Runtime(
            RuntimeTestFixture.Route(batch, invoker)
        );
        Task<RecapCellBatchExecutionResult> execution = runtime
            .ExecuteAsync(batch, default).AsTask();
        await started.Task;

        Task disposing;
        using (ExecutionContext.SuppressFlow()) {
            disposing = Task.Run(runtime.Dispose);
        }
        await Task.Delay(30);
        Assert.False(disposing.IsCompleted);
        Assert.Equal(0, invoker.DisposeCount);
        release.TrySetResult();
        await execution;
        await disposing;

        Assert.Equal(1, invoker.DisposeCount);
        Assert.IsType<RecapCellBatchExecutionResult.RejectedBeforeDispatch>(
            await runtime.ExecuteAsync(batch, default)
        );
    }

    [Fact]
    public async Task AllExactRoutesResolveBeforeAnyGroupLeaderDispatches() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch(
            columnCount: 2,
            distinctSemanticModels: true
        );
        var invoker = new ScriptedInvoker((_, _) => throw new InvalidOperationException());
        RecapCompletionRoute first = RuntimeTestFixture.Route(
            batch,
            invoker,
            workIndex: 0
        );
        using var runtime = new RecapCompletionRuntime(
            new ScriptedResolver(key => key == first.Key
                ? new RecapCompletionRouteResolution.Bound(first)
                : new RecapCompletionRouteResolution.Unavailable(
                    "SecondRouteMissing",
                    "The second exact route is absent."
                ))
        );

        var rejected = Assert.IsType<
            RecapCellBatchExecutionResult.RejectedBeforeDispatch
        >(await runtime.ExecuteAsync(batch, default));

        Assert.Equal("SecondRouteMissing", rejected.Code);
        Assert.Equal(0, invoker.CallCount);
    }

    [Fact]
    public async Task EveryGroupLeaderStartsBeforeEitherGroupsFollowers() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch(
            columnCount: 4,
            semanticModelGroupSize: 2
        );
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        ScriptedInvoker? firstInvoker = null;
        firstInvoker = new ScriptedInvoker(async (request, _) => {
            if (firstInvoker!.CallCount == 1) { await release.Task; }
            return RuntimeTestFixture.Updated(request, firstInvoker);
        });
        ScriptedInvoker? secondInvoker = null;
        secondInvoker = new ScriptedInvoker(async (request, _) => {
            if (secondInvoker!.CallCount == 1) { await release.Task; }
            return RuntimeTestFixture.Updated(request, secondInvoker);
        });
        using var runtime = Runtime(
            RuntimeTestFixture.Route(batch, firstInvoker, workIndex: 0),
            RuntimeTestFixture.Route(batch, secondInvoker, workIndex: 2)
        );

        Task<RecapCellBatchExecutionResult> execution = runtime
            .ExecuteAsync(batch, default).AsTask();
        await WaitUntilAsync(() => firstInvoker.CallCount == 1
            && secondInvoker.CallCount == 1);
        Assert.Equal(2, firstInvoker.CallCount + secondInvoker.CallCount);
        release.TrySetResult();
        _ = await execution;

        Assert.Equal(2, firstInvoker.CallCount);
        Assert.Equal(2, secondInvoker.CallCount);
    }

    [Fact]
    public async Task FastLeaderFollowerWaitsForOccupiedSiblingLeaderStart() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch(
            columnCount: 4,
            semanticModelGroupSize: 2
        );
        FrozenRowBatch holderBatch = RuntimeTestFixture.Batch(
            semanticModelId: "semantic-1"
        );
        var holderStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseHolder = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        ScriptedInvoker? firstInvoker = null;
        firstInvoker = new ScriptedInvoker((request, _) =>
            ValueTask.FromResult(RuntimeTestFixture.Updated(
                request,
                firstInvoker!
            )));
        ScriptedInvoker? delayedInvoker = null;
        delayedInvoker = new ScriptedInvoker(async (request, _) => {
            if (delayedInvoker!.CallCount == 1) {
                holderStarted.TrySetResult();
                await releaseHolder.Task;
            }
            return RuntimeTestFixture.Updated(request, delayedInvoker);
        });
        RecapCompletionRoute firstRoute = RuntimeTestFixture.Route(
            batch,
            firstInvoker,
            maximumConcurrency: 1,
            workIndex: 0
        );
        RecapCompletionRoute delayedRoute = RuntimeTestFixture.Route(
            batch,
            delayedInvoker,
            maximumConcurrency: 1,
            workIndex: 2
        );
        using var runtime = Runtime(firstRoute, delayedRoute);

        Task<RecapCellBatchExecutionResult> holder = runtime
            .ExecuteAsync(holderBatch, default).AsTask();
        await holderStarted.Task;
        Task<RecapCellBatchExecutionResult> execution = runtime
            .ExecuteAsync(batch, default).AsTask();
        await Task.Delay(30);

        Assert.Equal(1, firstInvoker.CallCount);
        releaseHolder.TrySetResult();
        await Task.WhenAll(holder, execution);
        Assert.Equal(2, firstInvoker.CallCount);
        Assert.Equal(3, delayedInvoker.CallCount);
    }

    private static RecapCompletionRuntime Runtime(
        params RecapCompletionRoute[] routes
    ) => new(new ScriptedResolver(key => {
        RecapCompletionRoute? route = routes.SingleOrDefault(
            value => value.Key == key
        );
        return route is null
            ? new RecapCompletionRouteResolution.Unavailable(
                "RouteMissing",
                "No exact route."
            )
            : new RecapCompletionRouteResolution.Bound(route);
    }));

    private static string RenderForAssertion(
        IReadOnlyList<IHistoryMessage> messages
    ) => string.Join("\n", messages.Select(static message => message switch {
        ObservationMessage value => value.Content,
        ActionMessage value => string.Join("|", value.Blocks.Select(
            static block => block switch {
                ActionBlock.Text text => text.Content,
                ActionBlock.ToolCall call => call.Call.ToolName,
                _ => "unexpected"
            })),
        _ => message.ToString()
    }));

    private static void AssertCallCountEquivalent(
        ScriptedInvoker invoker,
        RecapCellBatchExecutionResult.Completed completed
    ) => Assert.Equal(
        invoker.CallCount,
        completed.OrderedOutcomes.Count(static outcome => outcome
            is not RecapCellExecutionOutcome
                .NotStartedDueToCallerCancellation)
    );

    private static async Task WaitUntilAsync(Func<bool> condition) {
        for (int index = 0; index < 100; index++) {
            if (condition()) { return; }
            await Task.Delay(10);
        }
        Assert.Fail("Condition did not become true.");
    }

    private static async Task ExecuteAndDisposeAsync(
        FrozenRowBatch batch,
        RecapCompletionRoute route
    ) {
        RecapCompletionRuntime runtime = Runtime(route);
        _ = await runtime.ExecuteAsync(batch, default);
        await runtime.DisposeAsync();
    }

    private sealed class DisposableInvoker : IRecapCompletionInvoker,
        IDisposable {
        private readonly Func<CompletionRequest, CancellationToken,
            ValueTask<CompletionResult>> _handler;
        private readonly Action? _dispose;
        private int _disposeCount;

        internal DisposableInvoker(Func<CompletionRequest,
            CancellationToken, ValueTask<CompletionResult>> handler,
            Action? dispose = null
        ) {
            _handler = handler;
            _dispose = dispose;
        }

        public string ProviderId => "test-provider";
        public string ApiSpecId => "test-api-v1";
        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public ValueTask<CompletionResult> InvokeAsync(
            CompletionRequest request,
            CompletionInvocationOptions invocationOptions,
            CancellationToken cancellationToken
        ) {
            _ = invocationOptions;
            return _handler(request, cancellationToken);
        }

        public void Dispose() {
            Interlocked.Increment(ref _disposeCount);
            _dispose?.Invoke();
        }
    }

    private sealed class AsyncDisposableInvoker : IRecapCompletionInvoker,
        IAsyncDisposable {
        private readonly Func<CompletionRequest, CancellationToken,
            ValueTask<CompletionResult>> _handler;
        private readonly Func<ValueTask> _dispose;
        private int _disposeCount;

        internal AsyncDisposableInvoker(
            Func<CompletionRequest, CancellationToken,
                ValueTask<CompletionResult>> handler,
            Func<ValueTask> dispose
        ) {
            _handler = handler;
            _dispose = dispose;
        }

        public string ProviderId => "test-provider";
        public string ApiSpecId => "test-api-v1";
        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public ValueTask<CompletionResult> InvokeAsync(
            CompletionRequest request,
            CompletionInvocationOptions invocationOptions,
            CancellationToken cancellationToken
        ) {
            _ = invocationOptions;
            return _handler(request, cancellationToken);
        }

        public async ValueTask DisposeAsync() {
            Interlocked.Increment(ref _disposeCount);
            await _dispose().ConfigureAwait(false);
        }
    }

    private sealed class CallbackTelemetry : IRecapCompletionTelemetry {
        private readonly Action _callback;
        private int _count;

        internal CallbackTelemetry(Action callback) => _callback = callback;
        internal int Count => Volatile.Read(ref _count);

        public void Record(RecapCompletionTelemetryEvent value) {
            _ = value;
            Interlocked.Increment(ref _count);
            _callback();
        }
    }

    private sealed class CapturingTelemetry : IRecapCompletionTelemetry {
        internal ConcurrentQueue<RecapCompletionTelemetryEvent> Events {
            get;
        } = [];

        public void Record(RecapCompletionTelemetryEvent value)
            => Events.Enqueue(value);
    }

    private sealed class DisposableCompletionClient : ICompletionClient,
        IDisposable {
        private int _disposeCount;

        public string Name => "test-provider";
        public string ApiSpecId => "test-api-v1";
        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = observer;
            _ = cancellationToken;
            return Task.FromResult(RuntimeTestFixture.Result(
                request,
                new CompletionClientRecapInvoker(
                    this,
                    RecapCompletionResourceOwnership.Borrowed
                ),
                "{\"outcome\":\"updated\",\"content\":\"owned\"}"
            ));
        }

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}
