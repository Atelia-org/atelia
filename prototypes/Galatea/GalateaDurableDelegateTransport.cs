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
    ) {
        FailurePolicy = GalateaDurableDelegateFailurePolicies.Classify(
            stage,
            code
        );
    }

    internal GalateaDurableDelegateFailurePolicy FailurePolicy { get; }
}

internal enum GalateaDurableDelegateFailurePolicy {
    RetryableBinding,
    StartOutcomeUnknown,
    InspectionUnavailable,
    DeterministicConflict,
    PreWriteRejected,
    Stopped,
    FatalTransport
}

internal static class GalateaDurableDelegateFailurePolicies {
    internal static GalateaDurableDelegateFailurePolicy Classify(
        string stage,
        string code
    ) => (stage, code) switch {
        ("ensure-binding", "BINDING_OUTCOME_UNKNOWN") =>
            GalateaDurableDelegateFailurePolicy.RetryableBinding,
        ("start-turn", "START_OUTCOME_UNKNOWN") =>
            GalateaDurableDelegateFailurePolicy.StartOutcomeUnknown,
        ("inspect-dispatch", "INSPECTION_UNAVAILABLE") =>
            GalateaDurableDelegateFailurePolicy.InspectionUnavailable,
        ("protocol", "DUPLICATE_BINDING_OPERATION_ID")
            or ("protocol", "DUPLICATE_DISPATCH_ID")
            or ("ensure-binding", "THREAD_NOT_FOUND")
            or ("ensure-binding", "INVALID_CWD")
            or ("ensure-binding", "CWD_NOT_ALLOWED")
            or ("start-turn", "DISPATCH_ALREADY_ACTIVE")
            or ("start-turn", "THREAD_NOT_FOUND")
            or ("start-turn", "CWD_MISMATCH")
            or ("start-turn", "BRIDGE_BUSY") =>
                GalateaDurableDelegateFailurePolicy.DeterministicConflict,
        ("protocol", "OPERATION_CAPACITY_EXCEEDED")
            or ("protocol", "SIDECAR_READY_TIMEOUT")
            or ("protocol", "SIDECAR_WRITE_CANCELLED_BEFORE_START")
            or ("protocol", "SIDECAR_WRITE_GATE_TIMEOUT")
            or ("protocol", "SIDECAR_WRITE_FAILED") =>
                GalateaDurableDelegateFailurePolicy.PreWriteRejected,
        ("ensure-binding", "SIDECAR_STOPPING")
            or ("shutdown", _) =>
                GalateaDurableDelegateFailurePolicy.Stopped,
        _ => GalateaDurableDelegateFailurePolicy.FatalTransport
    };
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
