using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.RecapGrid.Manager;
using Atelia.SessionJournal.RecapGrid.Runtime;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Runtime.PublicSurface.Tests;

public sealed class PublicSurfaceTests {
    [Fact]
    public void ExternalTelemetryCanImplementNamedEventSink() {
        var implementation = new PublicTelemetry();
        IRecapCompletionTelemetry telemetry = implementation;
        var routeKey = new RecapCompletionRouteKey(
            new FamilyDefinitionDigest(new string('a', 64)),
            RecapRewriterProtocolV3.RuntimeProtocolId,
            semanticModelId: null
        );
        var telemetryEvent = new RecapCompletionTelemetryEvent(
            kind: "Completed",
            routeKey: routeKey,
            connectionId: "connection-v1",
            modelId: "model-v1",
            providerId: "public-provider",
            apiSpecId: "public-api-v1",
            evaluationKey: new EvaluationKeyDigest(new string('b', 64)),
            familyDigest: routeKey.FamilyDigest,
            definitionDigest: new MaintainerDefinitionDigest(
                new string('c', 64)
            ),
            historySegmentDigest: new string('d', 64),
            isFirstRowPrior: true,
            priorProjectionDigest: null,
            role: RecapCompletionWorkRole.Leader,
            admissionWait: TimeSpan.Zero,
            laneWait: TimeSpan.Zero,
            elapsed: TimeSpan.Zero,
            cacheReuseHint: PromptCacheReuseHint.ConnectionDefault,
            resultReceived: true,
            termination: null,
            providerErrorCount: 0,
            usage: null,
            providerOutcome: "completed",
            code: null,
            detail: null
        );

        telemetry.Record(telemetryEvent);

        Assert.Same(implementation, telemetry);
        Assert.Equal(1, implementation.RecordCount);
        Assert.Same(telemetryEvent, implementation.LastEvent);
    }

    [Fact]
    public void ExternalComposition_CanConstructExactLazyRouteRuntime() {
        var key = new RecapCompletionRouteKey(
            new FamilyDefinitionDigest(new string('a', 64)),
            RecapRewriterProtocolV3.RuntimeProtocolId,
            semanticModelId: null
        );
        var invoker = new PublicInvoker();
        RecapCompletionRoute route = RecapCompletionRoute.Create(
            key,
            "connection-v1",
            "model-v1",
            invoker,
            RecapCompletionResourceOwnership.Borrowed,
            maximumConcurrency: 2,
            TimeSpan.FromMinutes(1)
        );
        var resolver = new PublicResolver(route);

        using var runtime = new RecapCompletionRuntime(resolver);
        IRecapCellBatchExecutor executor = runtime;
        IAsyncDisposable asyncLifetime = runtime;

        Assert.Same(runtime, executor);
        Assert.Same(runtime, asyncLifetime);
        Assert.Equal(key, route.Key);
        Assert.Equal("connection-v1", route.ConnectionId);
        Assert.Equal(
            RecapCompletionResourceOwnership.Borrowed,
            route.InvokerOwnership
        );
        Assert.Equal(0, resolver.ResolveCount);
        Assert.Null(route.Key.SemanticModelId);
    }

    [Fact]
    public void PublicRouteUnion_IsClosedAndRejectsInvalidPayloads() {
        var key = new RecapCompletionRouteKey(
            new FamilyDefinitionDigest(new string('a', 64)),
            RecapRewriterProtocolV3.RuntimeProtocolId,
            semanticModelId: null
        );
        Assert.ThrowsAny<ArgumentException>(() => RecapCompletionRoute.Create(
            key,
            "",
            "model-v1",
            new PublicInvoker(),
            RecapCompletionResourceOwnership.Borrowed,
            1,
            TimeSpan.FromMinutes(1)
        ));
        Assert.Throws<ArgumentNullException>(() =>
            new RecapCompletionRouteResolution.Bound(null!));
        Assert.ThrowsAny<ArgumentException>(() =>
            new RecapCompletionRouteResolution.Unavailable("", "detail"));
        Assert.ThrowsAny<ArgumentException>(() =>
            new RecapCompletionRouteResolution.Invalid("code", ""));
        Assert.True(typeof(RecapCompletionRouteResolution).IsAbstract);
    }

    private sealed class PublicResolver : IRecapCompletionRouteResolver {
        private readonly RecapCompletionRoute _route;

        internal PublicResolver(RecapCompletionRoute route) => _route = route;
        internal int ResolveCount { get; private set; }

        public RecapCompletionRouteResolution Resolve(
            RecapCompletionRouteKey key
        ) {
            ResolveCount++;
            return key == _route.Key
                ? new RecapCompletionRouteResolution.Bound(_route)
                : new RecapCompletionRouteResolution.Unavailable(
                    "RouteAbsent",
                    "No exact route exists."
                );
        }
    }

    private sealed class PublicInvoker : IRecapCompletionInvoker {
        public string ProviderId => "public-provider";
        public string ApiSpecId => "public-api-v1";

        public ValueTask<CompletionResult> InvokeAsync(
            CompletionRequest request,
            CompletionInvocationOptions invocationOptions,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();
    }

    private sealed class PublicTelemetry : IRecapCompletionTelemetry {
        internal int RecordCount { get; private set; }
        internal RecapCompletionTelemetryEvent? LastEvent { get; private set; }

        public void Record(RecapCompletionTelemetryEvent value) {
            ArgumentNullException.ThrowIfNull(value);
            LastEvent = value;
            RecordCount++;
        }
    }
}
