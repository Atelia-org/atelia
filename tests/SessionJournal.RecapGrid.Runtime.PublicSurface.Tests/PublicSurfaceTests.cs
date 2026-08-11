using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.RecapGrid.Manager;
using Atelia.SessionJournal.RecapGrid.Runtime;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Runtime.PublicSurface.Tests;

public sealed class PublicSurfaceTests {
    [Fact]
    public void ExternalComposition_CanConstructExactLazyRouteRuntime() {
        var key = new RecapCompletionRouteKey(
            new FamilyDefinitionDigest(new string('a', 64)),
            RecapCompletionProtocolV1.RuntimeProtocolId,
            semanticModelId: null
        );
        var invoker = new PublicInvoker();
        RecapCompletionRoute route = RecapCompletionRoute.Create(
            key,
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
        Assert.Equal(
            RecapCompletionResourceOwnership.Borrowed,
            route.InvokerOwnership
        );
        Assert.Equal(0, resolver.ResolveCount);
        Assert.Null(route.Key.SemanticModelId);
    }

    [Fact]
    public void PublicRouteUnion_IsClosedAndRejectsInvalidPayloads() {
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
}
