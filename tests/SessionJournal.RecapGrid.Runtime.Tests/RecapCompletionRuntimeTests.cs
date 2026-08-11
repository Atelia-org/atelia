using System.Collections.Concurrent;
using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.RecapGrid.Manager;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Runtime.Tests;

public sealed class RecapCompletionRuntimeTests {
    [Fact]
    public async Task Execute_ValidUpdatedOutput_CompletesInTargetOrder() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch(columnCount: 2);
        ScriptedInvoker? invoker = null;
        invoker = new ScriptedInvoker((request, _) => ValueTask.FromResult(
            RuntimeTestFixture.Updated(
                request,
                invoker!,
                request.TailMessages.Single() is ObservationMessage tail
                    ? tail.Content!
                    : "missing"
            )
        ));
        RecapCompletionRoute route = RuntimeTestFixture.Route(batch, invoker);
        using var runtime = Runtime(route);

        var completed = Assert.IsType<
            RecapCellBatchExecutionResult.Completed
        >(await runtime.ExecuteAsync(batch, default));

        Assert.Equal(2, invoker.CallCount);
        Assert.Equal(
            batch.OrderedMissingWork.Select(static work =>
                work.EvaluationKey.Digest),
            completed.OrderedOutcomes.Select(static outcome =>
                outcome.EvaluationKey)
        );
        Assert.All(completed.OrderedOutcomes, static outcome =>
            Assert.IsType<RecapCellExecutionOutcome.Updated>(outcome));
    }

    [Fact]
    public async Task PreflightProtocolMismatch_RejectsWholeBatchBeforeRemoteStart() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch(
            columnCount: 2,
            inputProtocolId: "unsupported-input-v1"
        );
        var invoker = new ScriptedInvoker((_, _) => throw new InvalidOperationException());
        RecapCompletionRoute route = RuntimeTestFixture.Route(batch, invoker);
        using var runtime = Runtime(route);

        var rejected = Assert.IsType<
            RecapCellBatchExecutionResult.RejectedBeforeDispatch
        >(await runtime.ExecuteAsync(batch, default));

        Assert.Equal("ProtocolUnavailable", rejected.Code);
        Assert.Equal(0, invoker.CallCount);
    }

    [Fact]
    public async Task SameFamily_PerWorkRuntimeProtocolMismatch_RejectsWholeBatch() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch(
            columnCount: 2,
            runtimeProtocolIds: [
                RecapCompletionProtocolV1.RuntimeProtocolId,
                "unsupported-runtime-v2"
            ]
        );
        var invoker = new ScriptedInvoker((_, _) =>
            throw new InvalidOperationException("must not dispatch"));
        RecapCompletionRoute[] routes = [
            RuntimeTestFixture.Route(batch, invoker, workIndex: 0),
            RuntimeTestFixture.Route(batch, invoker, workIndex: 1)
        ];
        var resolver = new ScriptedResolver(key =>
            new RecapCompletionRouteResolution.Bound(
                routes.Single(route => route.Key == key)
            ));
        using var runtime = new RecapCompletionRuntime(resolver);

        var rejected = Assert.IsType<
            RecapCellBatchExecutionResult.RejectedBeforeDispatch
        >(await runtime.ExecuteAsync(batch, default));

        Assert.Equal("ProtocolUnavailable", rejected.Code);
        Assert.Equal(1, resolver.CallCount);
        Assert.Equal(0, invoker.CallCount);
    }

    [Fact]
    public async Task NullSemanticRoute_IsExactAndNeverFallsBack() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch(
            semanticModelId: null
        );
        RecapCompletionRouteKey? observed = null;
        var resolver = new ScriptedResolver(key => {
            observed = key;
            return new RecapCompletionRouteResolution.Unavailable(
                "ExactRouteAbsent",
                "No exact null-semantic route."
            );
        });
        using var runtime = new RecapCompletionRuntime(resolver);

        var rejected = Assert.IsType<
            RecapCellBatchExecutionResult.RejectedBeforeDispatch
        >(await runtime.ExecuteAsync(batch, default));

        Assert.Equal("ExactRouteAbsent", rejected.Code);
        Assert.NotNull(observed);
        Assert.Null(observed.Value.SemanticModelId);
        Assert.Equal(1, resolver.CallCount);
    }

    [Fact]
    public async Task ResolverFallbackKey_IsRejectedBeforeRemoteStart() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch(
            semanticModelId: null
        );
        var invoker = new ScriptedInvoker((_, _) => throw new InvalidOperationException());
        RecapCompletionRoute exact = RuntimeTestFixture.Route(batch, invoker);
        var different = RecapCompletionRoute.Create(
            new RecapCompletionRouteKey(
                exact.Key.FamilyDigest,
                exact.Key.RuntimeProtocolId,
                "fallback-model"
            ),
            exact.ModelId,
            invoker,
            RecapCompletionResourceOwnership.Owned,
            exact.MaximumConcurrency,
            exact.DispatchTimeout
        );
        using var runtime = new RecapCompletionRuntime(
            new ScriptedResolver(_ =>
                new RecapCompletionRouteResolution.Bound(different))
        );

        var rejected = Assert.IsType<
            RecapCellBatchExecutionResult.RejectedBeforeDispatch
        >(await runtime.ExecuteAsync(batch, default));

        Assert.Equal("RouteKeyMismatch", rejected.Code);
        Assert.Equal(0, invoker.CallCount);
    }

    [Fact]
    public async Task ResolverDiagnostics_AreStrictBoundedAndCodeOwned() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch();
        using var boundedRuntime = new RecapCompletionRuntime(
            new ScriptedResolver(_ =>
                new RecapCompletionRouteResolution.Unavailable(
                    "ExternalUnavailable",
                    new string('x', 4 * 1024 * 1024)
                ))
        );

        var bounded = Assert.IsType<
            RecapCellBatchExecutionResult.RejectedBeforeDispatch
        >(await boundedRuntime.ExecuteAsync(batch, default));
        Assert.Equal("ExternalUnavailable", bounded.Code);
        Assert.Equal(4 * 1024, Encoding.UTF8.GetByteCount(bounded.Detail));

        using var invalidRuntime = new RecapCompletionRuntime(
            new ScriptedResolver(_ =>
                new RecapCompletionRouteResolution.Invalid(
                    "invalid-\uD800-code",
                    "must not escape"
                ))
        );
        var invalid = Assert.IsType<
            RecapCellBatchExecutionResult.RejectedBeforeDispatch
        >(await invalidRuntime.ExecuteAsync(batch, default));
        Assert.Equal("RouteResolutionInvalid", invalid.Code);
        Assert.Equal(
            "The route resolver returned an invalid diagnostic code.",
            invalid.Detail
        );
    }

    [Theory]
    [InlineData("{\"outcome\":\"updated\",\"content\":null}", "TerminalOutcomeInvalid")]
    [InlineData("{\"outcome\":\"unknown\",\"content\":\"x\"}", "TerminalOutcomeInvalid")]
    [InlineData("{\"outcome\":\"updated\",\"content\":\"x\",\"extra\":1}", "TerminalArgumentsShapeInvalid")]
    [InlineData("{\"outcome\":\"updated\",\"outcome\":\"updated\",\"content\":\"x\"}", "TerminalArgumentsShapeInvalid")]
    [InlineData("{\"outcome\":\"updated\",\"content\":\"\\uD800\"}", "UpdatedContentInvalidUtf16")]
    [InlineData("{\"outcome\":\"updated\",\"content\":\"\\uDC00\"}", "UpdatedContentInvalidUtf16")]
    [InlineData("\uFEFF{\"outcome\":\"updated\",\"content\":\"x\"}", "TerminalArgumentsInvalid")]
    [InlineData("{\"outcome\":\"updated\",\"content\":\"x\"} trailing", "TerminalArgumentsInvalidJson")]
    public async Task Parser_InvalidShape_IsStableFailure(
        string arguments,
        string expectedCode
    ) {
        FrozenRowBatch batch = RuntimeTestFixture.Batch();
        ScriptedInvoker? invoker = null;
        invoker = new ScriptedInvoker((request, _) => ValueTask.FromResult(
            RuntimeTestFixture.Result(request, invoker!, arguments)
        ));
        RecapCompletionRoute route = RuntimeTestFixture.Route(batch, invoker);
        using var runtime = Runtime(route);

        var completed = Assert.IsType<
            RecapCellBatchExecutionResult.Completed
        >(await runtime.ExecuteAsync(batch, default));
        var failed = Assert.IsType<RecapCellExecutionOutcome.Failed>(
            Assert.Single(completed.OrderedOutcomes)
        );

        Assert.Equal(expectedCode, failed.Code);
        Assert.Equal(1, invoker.CallCount);
    }

    [Fact]
    public async Task PreCancelledBatch_ReturnsAllNotStartedAndDoesNotResolveRoute() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch(columnCount: 2);
        var resolver = new ScriptedResolver(_ => throw new InvalidOperationException());
        using var runtime = new RecapCompletionRuntime(resolver);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var completed = Assert.IsType<
            RecapCellBatchExecutionResult.Completed
        >(await runtime.ExecuteAsync(batch, cancellation.Token));

        Assert.All(completed.OrderedOutcomes, static outcome => Assert.IsType<
            RecapCellExecutionOutcome.NotStartedDueToCallerCancellation
        >(outcome));
        Assert.Equal(0, resolver.CallCount);
    }

    [Fact]
    public async Task TimeoutStartsAfterDispatch_MapsStartedCallToFailed() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch();
        var invoker = new ScriptedInvoker(async (_, token) => {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        });
        RecapCompletionRoute route = RuntimeTestFixture.Route(
            batch,
            invoker,
            dispatchTimeout: TimeSpan.FromMilliseconds(30)
        );
        using var runtime = Runtime(route);

        var completed = Assert.IsType<
            RecapCellBatchExecutionResult.Completed
        >(await runtime.ExecuteAsync(batch, default));
        var failed = Assert.IsType<RecapCellExecutionOutcome.Failed>(
            Assert.Single(completed.OrderedOutcomes)
        );

        Assert.Equal("CompletionTimeout", failed.Code);
        Assert.Equal(1, invoker.CallCount);
    }

    [Fact]
    public async Task SharedRouteAcrossBatches_EnforcesHostWideConcurrencyCap() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch();
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        ScriptedInvoker? invoker = null;
        invoker = new ScriptedInvoker(async (request, _) => {
            await release.Task;
            return RuntimeTestFixture.Updated(request, invoker!);
        });
        RecapCompletionRoute route = RuntimeTestFixture.Route(
            batch,
            invoker,
            maximumConcurrency: 1
        );
        using var runtime = Runtime(route);

        Task<RecapCellBatchExecutionResult> first = runtime
            .ExecuteAsync(batch, default).AsTask();
        Task<RecapCellBatchExecutionResult> second = runtime
            .ExecuteAsync(batch, default).AsTask();
        await WaitUntilAsync(() => invoker.CallCount == 1);
        Assert.Equal(1, invoker.MaximumActive);
        release.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(2, invoker.CallCount);
        Assert.Equal(1, invoker.MaximumActive);
    }

    [Fact]
    public async Task ThrowingTelemetry_DoesNotChangeSuccessfulOutcome() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch();
        ScriptedInvoker? invoker = null;
        invoker = new ScriptedInvoker((request, _) => ValueTask.FromResult(
            RuntimeTestFixture.Updated(request, invoker!)
        ));
        RecapCompletionRoute route = RuntimeTestFixture.Route(batch, invoker);
        using var runtime = Runtime(route, new ThrowingTelemetry());

        var completed = Assert.IsType<
            RecapCellBatchExecutionResult.Completed
        >(await runtime.ExecuteAsync(batch, default));

        Assert.IsType<RecapCellExecutionOutcome.Updated>(
            Assert.Single(completed.OrderedOutcomes)
        );
    }

    [Fact]
    public async Task DisposedRuntime_RejectsWithoutResolvingOrDispatching() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch();
        var resolver = new ScriptedResolver(_ => throw new InvalidOperationException());
        var runtime = new RecapCompletionRuntime(resolver);
        runtime.Dispose();

        var rejected = Assert.IsType<
            RecapCellBatchExecutionResult.RejectedBeforeDispatch
        >(await runtime.ExecuteAsync(batch, default));

        Assert.Equal("RuntimeDisposed", rejected.Code);
        Assert.Equal(0, resolver.CallCount);
    }

    [Theory]
    [InlineData("éa", true, null)]
    [InlineData("éaa", false, "UpdatedContentTooLarge")]
    [InlineData("   ", false, "UpdatedContentBlank")]
    public async Task UpdatedContent_UsesStrictExactUtf8Cap(
        string content,
        bool succeeds,
        string? expectedCode
    ) {
        FrozenRowBatch batch = RuntimeTestFixture.Batch(
            maxContentUtf8Bytes: 3
        );
        ScriptedInvoker? invoker = null;
        invoker = new ScriptedInvoker((request, _) => ValueTask.FromResult(
            RuntimeTestFixture.Updated(request, invoker!, content)
        ));
        using var runtime = Runtime(RuntimeTestFixture.Route(batch, invoker));

        var completed = Assert.IsType<
            RecapCellBatchExecutionResult.Completed
        >(await runtime.ExecuteAsync(batch, default));

        if (succeeds) {
            Assert.Equal(
                content,
                Assert.IsType<RecapCellExecutionOutcome.Updated>(
                    Assert.Single(completed.OrderedOutcomes)
                ).Content
            );
        }
        else {
            Assert.Equal(
                expectedCode,
                Assert.IsType<RecapCellExecutionOutcome.Failed>(
                    Assert.Single(completed.OrderedOutcomes)
                ).Code
            );
        }
    }

    [Fact]
    public async Task NeutralContentCap_ExactAndCapPlusOne() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch(
            maxContentUtf8Bytes: 1024 * 1024
        );
        var content = new Queue<string>([
            new string('x', 256 * 1024),
            new string('x', 256 * 1024 + 1)
        ]);
        ScriptedInvoker? invoker = null;
        invoker = new ScriptedInvoker((request, _) => ValueTask.FromResult(
            RuntimeTestFixture.Updated(
                request,
                invoker!,
                content.Dequeue()
            )
        ));
        using var runtime = Runtime(RuntimeTestFixture.Route(batch, invoker));

        var exact = Assert.IsType<RecapCellBatchExecutionResult.Completed>(
            await runtime.ExecuteAsync(batch, default)
        );
        Assert.IsType<RecapCellExecutionOutcome.Updated>(
            Assert.Single(exact.OrderedOutcomes)
        );
        var over = Assert.IsType<RecapCellBatchExecutionResult.Completed>(
            await runtime.ExecuteAsync(batch, default)
        );
        Assert.Equal(
            "UpdatedContentTooLarge",
            Assert.IsType<RecapCellExecutionOutcome.Failed>(
                Assert.Single(over.OrderedOutcomes)
            ).Code
        );
    }

    [Fact]
    public async Task WrongToolTextOrMultipleCalls_AreRejectedAfterOneProviderStart() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch();
        var outputs = new Queue<IReadOnlyList<ActionBlock>>([
            [new ActionBlock.Text("plain text")],
            [new ActionBlock.ToolCall(new RawToolCall(
                "wrong",
                "call-1",
                "{\"outcome\":\"updated\",\"content\":\"x\"}"
            ))],
            [
                new ActionBlock.ToolCall(new RawToolCall(
                    "submit",
                    "call-1",
                    "{\"outcome\":\"updated\",\"content\":\"x\"}"
                )),
                new ActionBlock.ToolCall(new RawToolCall(
                    "submit",
                    "call-2",
                    "{\"outcome\":\"updated\",\"content\":\"y\"}"
                ))
            ]
        ]);
        ScriptedInvoker? invoker = null;
        invoker = new ScriptedInvoker((request, _) => ValueTask.FromResult(
            RuntimeTestFixture.Result(
                request,
                invoker!,
                "{}",
                outputs.Dequeue()
            )
        ));
        using var runtime = Runtime(RuntimeTestFixture.Route(batch, invoker));

        for (int index = 0; index < 3; index++) {
            var completed = Assert.IsType<
                RecapCellBatchExecutionResult.Completed
            >(await runtime.ExecuteAsync(batch, default));
            Assert.Equal(
                "TerminalToolCallInvalid",
                Assert.IsType<RecapCellExecutionOutcome.Failed>(
                    Assert.Single(completed.OrderedOutcomes)
                ).Code
            );
        }
        Assert.Equal(3, invoker.CallCount);
    }

    [Fact]
    public async Task TerminationErrorsAndNullResult_AreStableStartedFailures() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch();
        var next = new Queue<int>([0, 1, 2]);
        ScriptedInvoker? invoker = null;
        invoker = new ScriptedInvoker((request, _) => {
            CompletionResult normal = RuntimeTestFixture.Updated(
                request,
                invoker!
            );
            return next.Dequeue() switch {
                0 => ValueTask.FromResult(normal with {
                    Termination = CompletionTermination.Incomplete()
                }),
                1 => ValueTask.FromResult(normal with {
                    Errors = ["provider error"]
                }),
                _ => ValueTask.FromResult<CompletionResult>(null!)
            };
        });
        using var runtime = Runtime(RuntimeTestFixture.Route(batch, invoker));
        string[] expected = [
            "CompletionIncomplete",
            "CompletionReportedErrors",
            "CompletionResultNull"
        ];

        foreach (string code in expected) {
            var completed = Assert.IsType<
                RecapCellBatchExecutionResult.Completed
            >(await runtime.ExecuteAsync(batch, default));
            Assert.Equal(
                code,
                Assert.IsType<RecapCellExecutionOutcome.Failed>(
                    Assert.Single(completed.OrderedOutcomes)
                ).Code
            );
        }
        Assert.Equal(3, invoker.CallCount);
    }

    [Theory]
    [InlineData("{\"Outcome\":\"updated\",\"content\":\"x\"}", "TerminalArgumentsShapeInvalid")]
    [InlineData("{/*comment*/\"outcome\":\"updated\",\"content\":\"x\"}", "TerminalArgumentsInvalidJson")]
    public async Task Parser_RejectsCaseVariantsAndComments(
        string arguments,
        string expectedCode
    ) {
        FrozenRowBatch batch = RuntimeTestFixture.Batch();
        ScriptedInvoker? invoker = null;
        invoker = new ScriptedInvoker((request, _) => ValueTask.FromResult(
            RuntimeTestFixture.Result(request, invoker!, arguments)
        ));
        using var runtime = Runtime(RuntimeTestFixture.Route(batch, invoker));

        var completed = Assert.IsType<RecapCellBatchExecutionResult.Completed>(
            await runtime.ExecuteAsync(batch, default)
        );
        Assert.Equal(
            expectedCode,
            Assert.IsType<RecapCellExecutionOutcome.Failed>(
                Assert.Single(completed.OrderedOutcomes)
            ).Code
        );
    }

    [Fact]
    public async Task Parser_RejectsArgumentsBeyondCodeOwnedBound() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch();
        string huge = "{\"outcome\":\"updated\",\"content\":\""
            + new string('x', 2 * 1024 * 1024)
            + "\"}";
        ScriptedInvoker? invoker = null;
        invoker = new ScriptedInvoker((request, _) => ValueTask.FromResult(
            RuntimeTestFixture.Result(request, invoker!, huge)
        ));
        using var runtime = Runtime(RuntimeTestFixture.Route(batch, invoker));

        var completed = Assert.IsType<RecapCellBatchExecutionResult.Completed>(
            await runtime.ExecuteAsync(batch, default)
        );
        Assert.Equal(
            "TerminalArgumentsTooLarge",
            Assert.IsType<RecapCellExecutionOutcome.Failed>(
                Assert.Single(completed.OrderedOutcomes)
            ).Code
        );
    }

    [Fact]
    public async Task EscapedContent_UsesDecodedExactCapAndRawEnvelopeBound() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch(
            maxContentUtf8Bytes: 1024 * 1024
        );
        static string Arguments(int decodedCharacters) =>
            "{\"outcome\":\"updated\",\"content\":\""
            + string.Concat(Enumerable.Repeat("\\u0078", decodedCharacters))
            + "\"}";
        var arguments = new Queue<string>([
            Arguments(256 * 1024),
            Arguments(256 * 1024 + 1)
        ]);
        ScriptedInvoker? invoker = null;
        invoker = new ScriptedInvoker((request, _) => ValueTask.FromResult(
            RuntimeTestFixture.Result(
                request,
                invoker!,
                arguments.Dequeue()
            )
        ));
        using var runtime = Runtime(RuntimeTestFixture.Route(batch, invoker));

        var exact = Assert.IsType<RecapCellBatchExecutionResult.Completed>(
            await runtime.ExecuteAsync(batch, default)
        );
        Assert.Equal(
            256 * 1024,
            Assert.IsType<RecapCellExecutionOutcome.Updated>(
                Assert.Single(exact.OrderedOutcomes)
            ).Content.Length
        );
        var over = Assert.IsType<RecapCellBatchExecutionResult.Completed>(
            await runtime.ExecuteAsync(batch, default)
        );
        Assert.Equal(
            "UpdatedContentTooLarge",
            Assert.IsType<RecapCellExecutionOutcome.Failed>(
                Assert.Single(over.OrderedOutcomes)
            ).Code
        );
    }

    [Fact]
    public async Task Parser_RejectsInvalidUtf16BeforeAllocatingArgumentBytes() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch();
        string arguments =
            "{\"outcome\":\"updated\",\"content\":\"\uD800\"}";
        ScriptedInvoker? invoker = null;
        invoker = new ScriptedInvoker((request, _) => ValueTask.FromResult(
            RuntimeTestFixture.Result(request, invoker!, arguments)
        ));
        using var runtime = Runtime(RuntimeTestFixture.Route(batch, invoker));

        var completed = Assert.IsType<RecapCellBatchExecutionResult.Completed>(
            await runtime.ExecuteAsync(batch, default)
        );
        Assert.Equal(
            "TerminalArgumentsInvalidUtf16",
            Assert.IsType<RecapCellExecutionOutcome.Failed>(
                Assert.Single(completed.OrderedOutcomes)
            ).Code
        );
    }

    [Fact]
    public async Task ResolverAndRouteConstruction_AreDeferredUntilExecuteAndCached() {
        FrozenRowBatch batch = RuntimeTestFixture.Batch();
        int routeConstructionCount = 0;
        ScriptedInvoker? invoker = null;
        invoker = new ScriptedInvoker((request, _) => ValueTask.FromResult(
            RuntimeTestFixture.Updated(request, invoker!)
        ));
        var resolver = new ScriptedResolver(key => {
            Interlocked.Increment(ref routeConstructionCount);
            return new RecapCompletionRouteResolution.Bound(
                RecapCompletionRoute.Create(
                    key,
                    "test-model",
                    invoker,
                    RecapCompletionResourceOwnership.Owned,
                    2,
                    TimeSpan.FromSeconds(5)
                )
            );
        });
        using var runtime = new RecapCompletionRuntime(resolver);
        Assert.Equal(0, routeConstructionCount);
        Assert.Equal(0, resolver.CallCount);

        _ = await runtime.ExecuteAsync(batch, default);
        _ = await runtime.ExecuteAsync(batch, default);

        Assert.Equal(1, routeConstructionCount);
        Assert.Equal(1, resolver.CallCount);
        Assert.Equal(2, invoker.CallCount);
    }

    private static RecapCompletionRuntime Runtime(
        RecapCompletionRoute route,
        IRecapCompletionTelemetry? telemetry = null
    ) => new(
        new ScriptedResolver(key => key == route.Key
            ? new RecapCompletionRouteResolution.Bound(route)
            : new RecapCompletionRouteResolution.Unavailable(
                "RouteMissing",
                "No route."
            )),
        telemetry: telemetry
    );

    private static async Task WaitUntilAsync(Func<bool> condition) {
        for (int index = 0; index < 100; index++) {
            if (condition()) { return; }
            await Task.Delay(10);
        }
        Assert.Fail("Condition did not become true.");
    }

    private sealed class ThrowingTelemetry : IRecapCompletionTelemetry {
        public void Record(RecapCompletionTelemetryEvent value)
            => throw new IOException(value.Kind);
    }
}
