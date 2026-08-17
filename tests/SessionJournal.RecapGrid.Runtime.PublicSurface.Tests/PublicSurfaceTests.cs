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
        var record = typeof(IRecapCompletionTelemetry).GetMethods()
            .SingleOrDefault(static method =>
                method.Name == nameof(IRecapCompletionTelemetry.Record)
                && method.ReturnType == typeof(void)
                && method.GetParameters()
                    .Select(static parameter => parameter.ParameterType)
                    .SequenceEqual([
                        typeof(RecapCompletionTelemetryEvent)
                    ])
            );

        Assert.Same(implementation, telemetry);
        Assert.NotNull(record);
        Assert.Equal(
            [typeof(RecapCompletionTelemetryEvent)],
            record!.GetParameters()
                .Select(static parameter => parameter.ParameterType)
                .ToArray()
        );
        Assert.Throws<ArgumentNullException>(() => telemetry.Record(null!));
        Assert.Equal(0, implementation.RecordCount);
    }

    [Fact]
    public async Task ExternalInvokerCanReturnMinimalLegalResult() {
        IRecapCompletionInvoker invoker = new PublicInvoker();
        var request = new CompletionRequest(
            "model-v1",
            new CompletionPromptPrefix(
                "system",
                CompletionOutputContract.ProviderDefault([]),
                Array.Empty<IHistoryMessage>()
            ),
            [new ObservationMessage("tail")]
        );

        CompletionResult result = await invoker.InvokeAsync(
            request,
            CompletionInvocationOptions.Default,
            CancellationToken.None
        );

        Assert.Equal("public-result", result.Message.GetFlattenedText());
        Assert.Equal(invoker.ProviderId, result.Invocation.ProviderId);
        Assert.Equal(invoker.ApiSpecId, result.Invocation.ApiSpecId);
        Assert.Equal(request.ModelId, result.Invocation.Model);
        Assert.Equal(
            CompletionTerminationKind.Completed,
            result.Termination.Kind
        );
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
        RecapCompletionRoute route = RecapCompletionRoute.Create(
            key,
            "connection-v1",
            "model-v1",
            new PublicInvoker(),
            RecapCompletionResourceOwnership.Borrowed,
            1,
            TimeSpan.FromMinutes(1)
        );
        var resolver = new PublicResolver(route);
        RecapCompletionRouteResolution.Invalid invalid = Assert.IsType<
            RecapCompletionRouteResolution.Invalid
        >(resolver.Resolve(new RecapCompletionRouteKey(
            key.FamilyDigest,
            "unsupported-protocol-v1",
            semanticModelId: null
        )));
        Assert.Equal("RouteInvalid", invalid.Code);
        RecapCompletionRouteResolution.Unavailable unavailable = Assert.IsType<
            RecapCompletionRouteResolution.Unavailable
        >(resolver.Resolve(new RecapCompletionRouteKey(
            new FamilyDefinitionDigest(new string('e', 64)),
            key.RuntimeProtocolId,
            semanticModelId: null
        )));
        Assert.Equal("RouteAbsent", unavailable.Code);
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
            if (key == _route.Key) {
                return new RecapCompletionRouteResolution.Bound(_route);
            }
            if (key.RuntimeProtocolId == "unsupported-protocol-v1") {
                return new RecapCompletionRouteResolution.Invalid(
                    "RouteInvalid",
                    "The route key is not supported."
                );
            }
            return new RecapCompletionRouteResolution.Unavailable(
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
        ) {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(invocationOptions);
            invocationOptions.Validate();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new CompletionResult(
                new ActionMessage([new ActionBlock.Text("public-result")]),
                new CompletionDescriptor(
                    ProviderId,
                    ApiSpecId,
                    request.ModelId
                )
            ));
        }
    }

    private sealed class PublicTelemetry : IRecapCompletionTelemetry {
        internal int RecordCount { get; private set; }
        internal string? LastKind { get; private set; }
        internal RecapCompletionRouteKey LastRouteKey { get; private set; }
        internal string? LastProviderOutcome { get; private set; }

        public void Record(RecapCompletionTelemetryEvent value) {
            ArgumentNullException.ThrowIfNull(value);
            LastKind = value.Kind;
            LastRouteKey = value.RouteKey;
            LastProviderOutcome = value.ProviderOutcome;
            RecordCount++;
        }
    }
}
