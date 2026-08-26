using System.Runtime.ExceptionServices;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.RecapGrid.Hosting;

namespace Atelia.Galatea.Server;

/// <summary>
/// Owns Galatea's one host-wide Completion registry and the RecapGrid runtime
/// which borrows it. Feature clients, including input normalization, borrow
/// lazy clients from the same registry and never own their lifetime.
/// </summary>
internal sealed class GalateaCompletionOwner : IAsyncDisposable {
    internal const string InputNormalizerBindingKey =
        "galatea.input-normalizer";

    private readonly CompletionConnectionRegistry _registry;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;

    internal GalateaCompletionOwner(
        GalateaConfig config,
        ICompletionClientFactory completionClientFactory
    ) {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(completionClientFactory);
        GalateaRecapGridRuntimeConfig recapGrid = config.RecapGrid
            ?? throw new InvalidOperationException(
                "Galatea requires strict RecapGrid runtime configuration."
            );

        CompletionConnectionsFileConfig normalized =
            CompletionConnectionConfigLoader.NormalizeAndValidate(new(
                config.Connections,
                config.DefaultConnectionId,
                config.SelectableConnectionIds,
                new Dictionary<string, string?>(StringComparer.Ordinal) {
                    [InputNormalizerBindingKey] =
                        config.InputNormalizerConnectionId,
                }
            ));
        ValidateGalateaRouting(normalized);

        ICompletionClientFactory ownedFactory =
            GalateaCompletionLogging.CreateOwnedFactory(
                completionClientFactory,
                config.CallLogDir
            );
        _registry = new CompletionConnectionRegistry(
            normalized,
            ownedFactory
        );
        RecapGridCompletionHost? recapGridHost = null;
        try {
            recapGridHost = RecapGridCompletionHost
                .CreateBorrowingRegistry(
                    () => GalateaConfigLoader.LoadRouteManifest(
                        recapGrid.RouteManifestPath
                    ),
                    _registry,
                    recapGrid.AgentControlProfiles
                );
            RecapGrid = new GalateaRecapGridComposition(
                recapGridHost,
                recapGrid.CurrentAgentControlProfileId,
                estimators: [new O200kBaseHistoryUnitLoadEstimator()]
            );
        }
        catch (Exception exception) {
            DisposeAfterConstructionFailure(
                recapGridHost,
                _registry,
                exception
            );
            throw;
        }

        Connections = normalized.Connections;
        DefaultConnectionId = normalized.DefaultConnectionId!;
        SelectableConnectionIds = normalized.SelectableConnectionIds!;
        InputNormalizerConnectionId = normalized.Bindings![
            InputNormalizerBindingKey
        ];
    }

    internal GalateaRecapGridComposition RecapGrid { get; }

    internal IReadOnlyList<CompletionConnectionConfig> Connections {
        get;
    }

    internal string DefaultConnectionId { get; }

    internal IReadOnlyList<string> SelectableConnectionIds { get; }

    internal string? InputNormalizerConnectionId { get; }

    internal CompletionConnectionConfig? InputNormalizerConnection =>
        InputNormalizerConnectionId is null
            ? null
            : TryGetConnectionExact(InputNormalizerConnectionId);

    internal ICompletionClient GetInputNormalizerClient() =>
        InputNormalizerConnectionId is null
            ? throw new InvalidOperationException(
                "Galatea input normalization is disabled."
            )
            : _registry.GetClient(InputNormalizerConnectionId);

    private CompletionConnectionConfig TryGetConnectionExact(string id) =>
        _registry.TryGet(id, out CompletionConnectionConfig connection)
            ? connection
            : throw new InvalidOperationException(
                $"Galatea completion connection '{id}' is absent."
            );

    internal static void ValidateGalateaRouting(
        CompletionConnectionsFileConfig config
    ) {
        ArgumentNullException.ThrowIfNull(config);
        if (config.SelectableConnectionIds is null) {
            throw new InvalidDataException(
                "Galatea connections require selectableConnectionIds."
            );
        }
        if (config.Bindings is null
            || config.Bindings.Count != 1
            || !config.Bindings.ContainsKey(InputNormalizerBindingKey)) {
            throw new InvalidDataException(
                "Galatea connections require exactly the "
                + $"'{InputNormalizerBindingKey}' binding."
            );
        }
    }

    private static void DisposeAfterConstructionFailure(
        RecapGridCompletionHost? recapGridHost,
        CompletionConnectionRegistry registry,
        Exception original
    ) {
        Exception? recapCleanup = null;
        Exception? registryCleanup = null;
        try {
            recapGridHost?.Dispose();
        }
        catch (Exception exception) {
            recapCleanup = exception;
        }
        try {
            registry.Dispose();
        }
        catch (Exception exception) {
            registryCleanup = exception;
        }

        if (recapCleanup is not null
            && !GalateaExceptionClassifier.IsNonFatal(recapCleanup)) {
            ExceptionDispatchInfo.Capture(recapCleanup).Throw();
        }
        if (registryCleanup is not null
            && !GalateaExceptionClassifier.IsNonFatal(registryCleanup)) {
            ExceptionDispatchInfo.Capture(registryCleanup).Throw();
        }
        if (!GalateaExceptionClassifier.IsNonFatal(original)) {
            ExceptionDispatchInfo.Capture(original).Throw();
        }

        var failures = new List<Exception> { original };
        if (recapCleanup is not null) { failures.Add(recapCleanup); }
        if (registryCleanup is not null) { failures.Add(registryCleanup); }
        if (failures.Count > 1) {
            throw new AggregateException(
                "Galatea Completion owner construction and cleanup failed.",
                failures
            );
        }
        ExceptionDispatchInfo.Capture(original).Throw();
    }

    public ValueTask DisposeAsync() => new(BeginDispose());

    private Task BeginDispose() {
        lock (_disposeGate) {
            return _disposeTask ??= DisposeCoreAsync();
        }
    }

    private async Task DisposeCoreAsync() {
        Exception? recapFailure = null;
        Exception? registryFailure = null;
        try {
            await RecapGrid.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) {
            recapFailure = exception;
        }

        try {
            await _registry.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) {
            registryFailure = exception;
        }

        if (recapFailure is not null
            && !GalateaExceptionClassifier.IsNonFatal(recapFailure)) {
            ExceptionDispatchInfo.Capture(recapFailure).Throw();
        }
        if (registryFailure is not null
            && !GalateaExceptionClassifier.IsNonFatal(registryFailure)) {
            ExceptionDispatchInfo.Capture(registryFailure).Throw();
        }
        if (recapFailure is not null && registryFailure is not null) {
            throw new AggregateException(recapFailure, registryFailure);
        }
        if (recapFailure is not null) {
            ExceptionDispatchInfo.Capture(recapFailure).Throw();
        }
        if (registryFailure is not null) {
            ExceptionDispatchInfo.Capture(registryFailure).Throw();
        }
    }
}
