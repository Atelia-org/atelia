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

internal sealed record GalateaInspectDelegateDispatchRequest {
    private GalateaInspectDelegateDispatchRequest(
        string dispatchId,
        string threadId,
        string task,
        string? expectedTurnId
    ) {
        DispatchId = dispatchId;
        ThreadId = threadId;
        Task = task;
        ExpectedTurnId = expectedTurnId;
    }

    internal string DispatchId { get; }
    internal string ThreadId { get; }
    internal string Task { get; }
    internal string? ExpectedTurnId { get; }

    internal static GalateaInspectDelegateDispatchRequest ForOutcomeUnknown(
        string dispatchId,
        string threadId,
        string task
    ) => new(dispatchId, threadId, task, expectedTurnId: null);

    internal static GalateaInspectDelegateDispatchRequest ForAccepted(
        string dispatchId,
        string threadId,
        string task,
        string expectedTurnId
    ) {
        ArgumentNullException.ThrowIfNull(expectedTurnId);
        return new(dispatchId, threadId, task, expectedTurnId);
    }
}

internal enum GalateaDelegateInspectionSource {
    Live,
    Persistent
}

internal abstract record GalateaDelegateDispatchInspection(
    string DispatchId,
    string ThreadId
) {
    internal sealed record NotFound(
        string DispatchId,
        string ThreadId,
        GalateaDelegateInspectionSource Source =
            GalateaDelegateInspectionSource.Persistent
    ) : GalateaDelegateDispatchInspection(DispatchId, ThreadId);

    internal sealed record Running(
        string DispatchId,
        string ThreadId,
        string TurnId,
        GalateaDelegateInspectionSource Source =
            GalateaDelegateInspectionSource.Persistent
    ) : GalateaDelegateDispatchInspection(DispatchId, ThreadId);

    internal sealed record Completed(
        string DispatchId,
        string ThreadId,
        string TurnId,
        string Final,
        GalateaDelegateInspectionSource Source =
            GalateaDelegateInspectionSource.Persistent
    ) : GalateaDelegateDispatchInspection(DispatchId, ThreadId);

    internal sealed record Failed(
        string DispatchId,
        string ThreadId,
        string TurnId,
        string Code,
        GalateaDelegateInspectionSource Source =
            GalateaDelegateInspectionSource.Persistent
    ) : GalateaDelegateDispatchInspection(DispatchId, ThreadId);

    internal sealed record Ambiguous(
        string DispatchId,
        string ThreadId,
        string Code,
        GalateaDelegateInspectionSource Source =
            GalateaDelegateInspectionSource.Persistent
    ) : GalateaDelegateDispatchInspection(DispatchId, ThreadId);

    internal sealed record AcceptedTurnNotVisible(
        string DispatchId,
        string ThreadId,
        string TurnId,
        GalateaDelegateInspectionSource Source
    ) : GalateaDelegateDispatchInspection(DispatchId, ThreadId) {
        internal const string FailureCode = "ACCEPTED_TURN_NOT_VISIBLE";
        internal string Code => FailureCode;
    }
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
        // A conforming V3 peer returns this as a semantic unavailable result.
        // Keep the generic failure form retryable as a defensive boundary.
        ("inspect-dispatch", "ACCEPTED_TURN_NOT_VISIBLE") =>
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
