namespace Atelia.Galatea.Server;

internal sealed record GalateaEnsureDelegateBindingRequest(
    string BindingOperationId
);

internal sealed record GalateaDelegateBindingEstablished(
    string BindingOperationId,
    string ThreadId
);

internal sealed record GalateaStartDelegateTurnRequest(
    string DispatchId,
    string ThreadId,
    string Task
);

internal sealed record GalateaDelegateTurnAccepted(
    string DispatchId,
    string ThreadId,
    string TurnId
);

internal sealed record GalateaInspectDelegateDispatchRequest(
    string DispatchId,
    string ThreadId,
    string Task
);

internal abstract record GalateaDelegateDispatchInspection(
    string DispatchId,
    string ThreadId
) {
    internal sealed record NotFound(
        string DispatchId,
        string ThreadId
    ) : GalateaDelegateDispatchInspection(DispatchId, ThreadId);

    internal sealed record Running(
        string DispatchId,
        string ThreadId,
        string TurnId
    ) : GalateaDelegateDispatchInspection(DispatchId, ThreadId);

    internal sealed record Completed(
        string DispatchId,
        string ThreadId,
        string TurnId,
        string Final
    ) : GalateaDelegateDispatchInspection(DispatchId, ThreadId);

    internal sealed record Failed(
        string DispatchId,
        string ThreadId,
        string TurnId,
        string Code
    ) : GalateaDelegateDispatchInspection(DispatchId, ThreadId);

    internal sealed record Ambiguous(
        string DispatchId,
        string ThreadId,
        string Code
    ) : GalateaDelegateDispatchInspection(DispatchId, ThreadId);
}

internal sealed class GalateaDurableDelegateTransportException
    : GalateaSidecarOperationException {
    internal GalateaDurableDelegateTransportException(
        string stage,
        string code
    ) : base(
        $"Galatea durable delegate transport failed at {stage}: {code}.",
        stage,
        code
    ) { }
}

internal interface IGalateaDurableDelegateTransport : IAsyncDisposable {
    Task<GalateaDelegateBindingEstablished> EnsureBindingAsync(
        GalateaEnsureDelegateBindingRequest request,
        CancellationToken ct
    );

    Task<GalateaDelegateTurnAccepted> StartTurnAsync(
        GalateaStartDelegateTurnRequest request,
        CancellationToken ct
    );

    Task<GalateaDelegateDispatchInspection> InspectDispatchAsync(
        GalateaInspectDelegateDispatchRequest request,
        CancellationToken ct
    );
}
