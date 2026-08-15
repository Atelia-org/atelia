using Atelia.SessionJournal.RecapGrid.Manager;

namespace Atelia.SessionJournal.RecapGrid.Runtime;

public sealed partial class RecapCompletionRuntime
    : IRecapCellBatchExecutor, IDisposable, IAsyncDisposable {
    private readonly IRecapCompletionRouteResolver _resolver;
    private readonly RecapCompletionRuntimeOptions _options;
    private readonly IRecapCompletionTelemetry? _telemetry;
    private readonly RuntimeLifetime _lifetime;
    private readonly object _routeGate = new();
    private readonly Dictionary<RecapCompletionRouteKey,
        RecapCompletionRouteResolution> _routeCache = [];
    private readonly Dictionary<IRecapCompletionInvoker,
        RecapCompletionResourceOwnership> _invokerOwnership = new(
            ReferenceEqualityComparer.Instance
        );

    public RecapCompletionRuntime(
        IRecapCompletionRouteResolver resolver,
        RecapCompletionRuntimeOptions? options = null,
        IRecapCompletionTelemetry? telemetry = null
    ) {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _options = options ?? new RecapCompletionRuntimeOptions();
        _telemetry = telemetry;
        _lifetime = new RuntimeLifetime(DisposeOwnedInvokersAsync);
    }

    public ValueTask<RecapCellBatchExecutionResult> ExecuteAsync(
        FrozenRowBatch batch,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(batch);
        if (!_lifetime.TryEnter(out RuntimeLifetime.OperationLease? lease)) {
            return ValueTask.FromResult<RecapCellBatchExecutionResult>(
                new RecapCellBatchExecutionResult.RejectedBeforeDispatch(
                    "RuntimeDisposed",
                    "The recap completion runtime is closing or disposed."
                )
            );
        }
        return ExecuteEnteredAsync(batch, lease!, cancellationToken);
    }

    private async ValueTask<RecapCellBatchExecutionResult> ExecuteEnteredAsync(
        FrozenRowBatch batch,
        RuntimeLifetime.OperationLease lease,
        CancellationToken cancellationToken
    ) {
        using (lease)
        using (_lifetime.EnterOperationScope()) {
            if (cancellationToken.IsCancellationRequested) {
                return new RecapCellBatchExecutionResult.Completed(
                    batch.OrderedMissingWork.Select(static work =>
                        (RecapCellExecutionOutcome)new RecapCellExecutionOutcome
                            .NotStartedDueToCallerCancellation(
                                work.EvaluationKey.Digest
                            )).ToArray()
                );
            }
            RuntimePreflightResult preflight = Preflight(batch);
            return preflight switch {
                RuntimePreflightResult.Rejected value
                    => new RecapCellBatchExecutionResult.RejectedBeforeDispatch(
                        value.Code,
                        value.Detail
                    ),
                RuntimePreflightResult.Ready value
                    => await RunPreparedAsync(
                        value.Work,
                        cancellationToken
                    ).ConfigureAwait(false),
                _ => new RecapCellBatchExecutionResult.RejectedBeforeDispatch(
                    "RuntimePreflightContractInvalid",
                    "The runtime preflight returned an unsupported result."
                )
            };
        }
    }

    public void Dispose() => _lifetime.DisposeAndDrain();

    public ValueTask DisposeAsync() => _lifetime.DisposeAndDrainAsync();

    private async ValueTask DisposeOwnedInvokersAsync() {
        IRecapCompletionInvoker[] invokers;
        lock (_routeGate) {
            invokers = [.. _routeCache.Values
                .OfType<RecapCompletionRouteResolution.Bound>()
                .Where(static value => value.Route.InvokerOwnership
                    is RecapCompletionResourceOwnership.Owned)
                .Select(static value => value.Route.Invoker)
                .Distinct<IRecapCompletionInvoker>(
                    ReferenceEqualityComparer.Instance
                )];
        }
        foreach (IRecapCompletionInvoker invoker in invokers) {
            try {
                switch (invoker) {
                    case IAsyncDisposable asyncDisposable:
                        await asyncDisposable.DisposeAsync()
                            .ConfigureAwait(false);
                        break;
                    case IDisposable disposable:
                        disposable.Dispose();
                        break;
                }
            }
            catch (Exception exception) when (!IsFatal(exception)) {
                // Disposal is operational cleanup and cannot rewrite settled work.
            }
        }
    }

    private RecapCompletionRouteResolution ResolveRoute(
        RecapCompletionRouteKey key
    ) {
        lock (_routeGate) {
            if (_routeCache.TryGetValue(key, out var existing)) {
                return existing;
            }
            RecapCompletionRouteResolution resolved = _resolver.Resolve(key)
                ?? new RecapCompletionRouteResolution.Invalid(
                    "RouteResolverReturnedNull",
                    "The route resolver returned null."
                );
            resolved = NormalizeExternalResolution(resolved);
            if (resolved is RecapCompletionRouteResolution.Bound bound) {
                if (_invokerOwnership.TryGetValue(
                        bound.Route.Invoker,
                        out RecapCompletionResourceOwnership ownership)
                    && ownership != bound.Route.InvokerOwnership) {
                    resolved = new RecapCompletionRouteResolution.Invalid(
                        "InvokerOwnershipConflict",
                        "One invoker reference cannot be both owned and borrowed."
                    );
                }
                else {
                    _invokerOwnership[bound.Route.Invoker] =
                        bound.Route.InvokerOwnership;
                }
            }
            _routeCache.Add(key, resolved);
            return resolved;
        }
    }

    private static RecapCompletionRouteResolution NormalizeExternalResolution(
        RecapCompletionRouteResolution resolution
    ) => resolution switch {
        RecapCompletionRouteResolution.Unavailable value
            => CreateBoundedExternalResolution(
                value.Code,
                value.Detail,
                invalid: false
            ),
        RecapCompletionRouteResolution.Invalid value
            => CreateBoundedExternalResolution(
                value.Code,
                value.Detail,
                invalid: true
            ),
        _ => resolution
    };

    private static RecapCompletionRouteResolution
        CreateBoundedExternalResolution(
            string code,
            string detail,
            bool invalid
        ) {
        if (!RuntimeDiagnostics.TryValidateExternalCode(
                code,
                out string validatedCode)) {
            return new RecapCompletionRouteResolution.Invalid(
                "RouteResolutionInvalid",
                "The route resolver returned an invalid diagnostic code."
            );
        }
        string boundedDetail = RuntimeDiagnostics.BoundDetail(detail);
        return invalid
            ? new RecapCompletionRouteResolution.Invalid(
                validatedCode,
                boundedDetail
            )
            : new RecapCompletionRouteResolution.Unavailable(
                validatedCode,
                boundedDetail
            );
    }
}
