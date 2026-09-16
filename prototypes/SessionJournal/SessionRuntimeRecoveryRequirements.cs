using System.Collections.Immutable;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;

namespace Atelia.SessionJournal;

/// <summary>
/// Durable tool dispatch state at the captured raw head. An already started tool
/// must follow its side-effect recovery contract. Pure generation has no dispatch gate.
/// </summary>
public enum SessionDurableDispatchState {
    NotStarted,
    StartedOutcomeUncertain,
}

/// <summary>
/// Minimal non-secret runtime identity required to continue from one exact
/// SessionJournal head. Request content, tool definitions, raw tool arguments,
/// operation ids, and credentials are deliberately absent.
/// </summary>
public abstract class SessionRuntimeRecoveryRequirements {
    private protected SessionRuntimeRecoveryRequirements(
        EventAddress? capturedHead,
        SessionExecutionPhase phase,
        SessionEventKind? headKind
    ) {
        CapturedHead = capturedHead;
        Phase = phase;
        HeadKind = headKind;
    }

    public EventAddress? CapturedHead { get; }
    public SessionExecutionPhase Phase { get; }
    public SessionEventKind? HeadKind { get; }

    /// <summary>
    /// Empty and Idle tails have no pending runtime dispatch. A later
    /// user-initiated Send is a separate Host operation.
    /// </summary>
    public sealed class NoRuntimeRequired
        : SessionRuntimeRecoveryRequirements {
        internal NoRuntimeRequired(
            EventAddress? capturedHead,
            SessionExecutionPhase phase,
            SessionEventKind? headKind
        ) : base(capturedHead, phase, headKind) {
        }
    }

    /// <summary>
    /// Historical failure remains blocked until explicitly ended. The legacy type name
    /// does not authorize rewinding input or already committed tool facts.
    /// </summary>
    public sealed class LegacyFailedTurnBlocked
        : SessionRuntimeRecoveryRequirements {
        internal LegacyFailedTurnBlocked(EventAddress failedHead)
            : base(
                failedHead,
                SessionExecutionPhase.TurnFailed,
                SessionEventKind.CompletionAttemptFailed
            ) {
            FailedHead = failedHead;
        }

        public EventAddress FailedHead { get; }
    }

    /// <summary>
    /// An accepted Observation or settled tool-result tail needs a newly
    /// selected completion runtime. No durable completion target is frozen yet.
    /// </summary>
    public sealed class NewRequestRequired
        : SessionRuntimeRecoveryRequirements {
        internal NewRequestRequired(
            EventAddress capturedHead,
            SessionExecutionPhase phase,
            SessionEventKind? headKind
        ) : base(capturedHead, phase, headKind) {
        }
    }

    /// <summary>
    /// Prepared completion recovery must bind every listed identity exactly.
    /// VisibleToolSetSha256 fingerprints definitions without exposing them.
    /// </summary>
    public sealed class FrozenCompletionRequired
        : SessionRuntimeRecoveryRequirements {
        internal FrozenCompletionRequired(
            EventAddress capturedHead,
            SessionExecutionPhase phase,
            SessionEventKind? headKind,
            SessionCompletionTargetIdentity completionTarget,
            string clientName,
            string apiSpecId,
            string visibleToolSetSha256,
            SessionToolRuntimeIdentity? toolRuntimeIdentity,
            EventAddress sourcePreparedAddress
        ) : base(capturedHead, phase, headKind) {
            CompletionTarget = completionTarget;
            ClientName = clientName;
            ApiSpecId = apiSpecId;
            VisibleToolSetSha256 = visibleToolSetSha256;
            ToolRuntimeIdentity = toolRuntimeIdentity;
            SourcePreparedAddress = sourcePreparedAddress;
        }

        public SessionCompletionTargetIdentity CompletionTarget { get; }
        public string ClientName { get; }
        public string ApiSpecId { get; }
        public string VisibleToolSetSha256 { get; }
        public SessionToolRuntimeIdentity? ToolRuntimeIdentity { get; }
        public EventAddress SourcePreparedAddress { get; }
    }

    /// <summary>
    /// A pending tool call binds only the durable tool runtime. The Host also
    /// supplies a newly selected completion runtime for the request that may
    /// follow the settled tool result; that completion target is not frozen by
    /// the pending Action.
    /// </summary>
    public sealed class ToolContinuationRequired
        : SessionRuntimeRecoveryRequirements {
        internal ToolContinuationRequired(
            EventAddress capturedHead,
            SessionExecutionPhase phase,
            SessionEventKind? headKind,
            SessionToolRuntimeIdentity toolRuntimeIdentity,
            SessionDurableDispatchState dispatchState
        ) : base(capturedHead, phase, headKind) {
            ToolRuntimeIdentity = toolRuntimeIdentity;
            DispatchState = dispatchState;
        }

        public SessionToolRuntimeIdentity ToolRuntimeIdentity { get; }
        public SessionDurableDispatchState DispatchState { get; }
    }
}

/// <summary>
/// Computes the exact SessionJournal wire fingerprint for the visible tool
/// definitions of a candidate runtime without exposing the broader prepared
/// request canonicalizer.
/// </summary>
public static class SessionVisibleToolSetFingerprint {
    public static string ComputeSha256(
        ImmutableArray<ToolDefinition> visibleDefinitions
    ) {
        if (visibleDefinitions.IsDefault) {
            throw new ArgumentException(
                "Visible tool definitions cannot be default.",
                nameof(visibleDefinitions)
            );
        }
        return SessionRequestCanonicalizer.ComputeToolSetSha256(
            visibleDefinitions
        );
    }
}
