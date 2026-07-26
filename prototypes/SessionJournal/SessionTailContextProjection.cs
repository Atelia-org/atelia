using System.Collections.Immutable;
using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal.Derived;

namespace Atelia.SessionJournal;

internal sealed record SessionTailContextProjectionResult(
    string SystemPrompt,
    ImmutableArray<IHistoryMessage> Context,
    EventAddress RawStartExclusive,
    string RawRangeSha256,
    ImmutableArray<SessionRequestArtifactInput> ArtifactInputs,
    SessionGoverningSetup FinalGoverningSetup,
    SessionTailProjectionDiagnostics Diagnostics
);

internal static class SessionTailContextProjection {
    public static async ValueTask<SessionTailContextProjectionResult> MaterializeAsync(
        SessionJournalEventReader reader,
        string sessionJournalPath,
        EventAddress expectedParent,
        SessionGoverningSetup currentGoverningSetup,
        SessionGoverningSetup anchorSetup,
        ImmutableArray<string> artifactIds,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(currentGoverningSetup);
        ArgumentNullException.ThrowIfNull(anchorSetup);
        if (artifactIds.Length < 2
            || artifactIds.Any(string.IsNullOrWhiteSpace)
            || artifactIds.Distinct(StringComparer.Ordinal).Count() != artifactIds.Length) {
            throw new ArgumentException(
                "Artifact-tail materialization requires at least two distinct exact artifact ids.",
                nameof(artifactIds)
            );
        }

        var store = DerivedRecapStore.Open(sessionJournalPath);
        var artifacts = new List<DerivedRecapArtifact>(artifactIds.Length);
        foreach (string artifactId in artifactIds) {
            DerivedRecapArtifact artifact = await store
                .TryReadArtifactAsync(artifactId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException($"Exact recap artifact '{artifactId}' was not found or is unusable.");
            if (!string.Equals(artifact.ArtifactId, artifactId, StringComparison.Ordinal)
                || !string.Equals(artifact.Status, DerivedRecapArtifactStatus.Produced, StringComparison.Ordinal)) {
                throw new InvalidDataException($"Recap artifact '{artifactId}' is not a produced exact artifact.");
            }
            if (artifact.AnchorRawEvent != artifact.SourceEndInclusive) {
                throw new InvalidDataException($"Recap artifact '{artifactId}' anchor must equal sourceEndInclusive.");
            }
            artifacts.Add(artifact);
        }
        EventAddress commonAnchor = artifacts[0].AnchorRawEvent;
        if (artifacts.Any(artifact => artifact.AnchorRawEvent != commonAnchor)) {
            throw new InvalidDataException("A coherent artifact set requires one common anchor.");
        }
        if (commonAnchor == expectedParent) {
            throw new InvalidDataException(
                "Coherent artifact-set anchor must be a strict ancestor of the current completion boundary."
            );
        }

        IReadOnlyList<EventAddress> suffixAddresses = CollectAndValidateSuffix(
            reader,
            expectedParent,
            commonAnchor,
            artifacts.Select(static artifact => artifact.SourceRawHead).ToHashSet(),
            cancellationToken,
            out int headerVisitCount
        );
        ValidateReplaySafeBoundary(reader, commonAnchor);

        if (anchorSetup.Head != commonAnchor) {
            throw new InvalidDataException(
                "Artifact-tail anchor setup must be pinned to the common coverage anchor."
            );
        }
        foreach (DerivedRecapArtifact artifact in artifacts) {
            if (artifact.GoverningRuntimeConfigSetup != anchorSetup.RuntimeConfigSetupAddress
                || artifact.GoverningSystemPromptSetup != anchorSetup.SystemPromptSetupAddress) {
                throw new InvalidDataException(
                    $"Recap artifact '{artifact.ArtifactId}' governing setup does not match its common anchor."
                );
            }
        }
        var suffixEntries = new List<SessionRawRangeHashEntry>(suffixAddresses.Count);
        var suffixEvents = new List<DecodedSessionEvent>(suffixAddresses.Count);
        foreach (EventAddress address in suffixAddresses) {
            cancellationToken.ThrowIfCancellationRequested();
            using EventFrame frame = reader.ReadEvent(address).Unwrap();
            ValidateSessionHeader(address, frame.Header);
            var kind = (SessionEventKind)frame.Header.OpaqueEventKind;
            object body = SessionEventCodec.Decode(kind, frame.Payload, out int bodySchemaVersion);
            suffixEvents.Add(new DecodedSessionEvent(kind, bodySchemaVersion, body, address, frame.Header.Parent));
            suffixEntries.Add(new SessionRawRangeHashEntry(
                address,
                frame.Header.Parent,
                frame.Header.OpaqueEventKind,
                bodySchemaVersion,
                SessionRequestCanonicalizer.Sha256Hex(frame.Payload)
            ));
        }

        string rawRangeSha256 = SessionRawRangeHasher.Compute(
            commonAnchor,
            expectedParent,
            suffixEntries
        );
        SessionExecutionRecovery anchorRecovery =
            SessionExecutionTailResolver.Resolve(reader, commonAnchor, cancellationToken);
        SessionExecutionRecovery currentRecovery =
            SessionExecutionTailResolver.Resolve(reader, expectedParent, cancellationToken);
        TailFoldResult folded = FoldSuffix(anchorSetup, suffixEvents, anchorRecovery);
        if (currentGoverningSetup.Head != expectedParent
            || folded.GoverningSetup.Head != expectedParent
            || folded.GoverningSetup.RuntimeConfigSetupAddress != currentGoverningSetup.RuntimeConfigSetupAddress
            || folded.GoverningSetup.SystemPromptSetupAddress != currentGoverningSetup.SystemPromptSetupAddress
            || folded.GoverningSetup.RuntimeConfig != currentGoverningSetup.RuntimeConfig
            || !string.Equals(folded.GoverningSetup.SystemPrompt, currentGoverningSetup.SystemPrompt, StringComparison.Ordinal)) {
            throw new InvalidDataException("Tail projection governing setup does not match the exact current-head governing setup.");
        }
        if (folded.Phase != currentRecovery.State.Phase
            || folded.ToolExecutionSequenceCheckpoint != currentRecovery.State.ToolExecutionSequenceCheckpoint
            || !string.Equals(
                folded.ActiveCorrelationId,
                currentRecovery.State.ActiveCorrelationId,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "Tail projection execution checkpoint or correlation does not match exact current recovery."
            );
        }

        var contributions = artifacts
            .Select(CreateTargetContribution)
            .OrderBy(static item => item.Carrier)
            .ThenBy(static item => item.BlockKey, StringComparer.Ordinal)
            .ToArray();
        var targets = new HashSet<(MemoryPackCarrier Carrier, string BlockKey)>();
        foreach (TargetContribution contribution in contributions) {
            if (!targets.Add((contribution.Carrier, contribution.BlockKey))) {
                throw new InvalidDataException(
                    $"Coherent artifact set contains duplicate target '{contribution.Carrier}/{contribution.BlockKey}'."
                );
            }
        }
        ImmutableArray<SessionRequestArtifactInput> artifactInputs = [
            .. contributions.Select(static item => item.ArtifactInput)
        ];
        SessionRequestArtifactContextSnapshot contextSnapshot =
            AggregateContextSnapshots(artifactInputs);
        (string systemPrompt, ImmutableArray<IHistoryMessage> headerContext) = ExpandContextSnapshot(
            folded.GoverningSetup.SystemPrompt,
            contextSnapshot
        );
        var context = ImmutableArray.CreateBuilder<IHistoryMessage>(headerContext.Length + folded.Context.Count);
        context.AddRange(headerContext);
        context.AddRange(folded.Context);

        return new SessionTailContextProjectionResult(
            systemPrompt,
            context.MoveToImmutable(),
            commonAnchor,
            rawRangeSha256,
            artifactInputs,
            folded.GoverningSetup,
            new SessionTailProjectionDiagnostics(
                headerVisitCount,
                suffixEvents.Count,
                suffixEvents.Count
            )
        );
    }

    private static IReadOnlyList<EventAddress> CollectAndValidateSuffix(
        SessionJournalEventReader reader,
        EventAddress expectedParent,
        EventAddress anchor,
        IReadOnlySet<EventAddress> sourceRawHeads,
        CancellationToken cancellationToken,
        out int headerVisitCount
    ) {
        var reverseSuffix = new List<EventAddress>();
        EventAddress? cursor = expectedParent;
        var unseenSourceHeads = new HashSet<EventAddress>(sourceRawHeads);
        headerVisitCount = 0;
        while (cursor is { } address) {
            cancellationToken.ThrowIfCancellationRequested();
            EventFrameHeader header = reader.ReadEventHeaderPreview(address).Unwrap();
            headerVisitCount++;
            ValidateSessionHeader(address, header);
            unseenSourceHeads.Remove(address);
            if (address == anchor) {
                if (unseenSourceHeads.Count > 0) {
                    throw new InvalidDataException(
                        "At least one recap artifact sourceRawHead is not on the current lineage at or after its anchor."
                    );
                }
                reverseSuffix.Reverse();
                return reverseSuffix;
            }
            reverseSuffix.Add(address);
            cursor = header.Parent;
        }

        throw new InvalidDataException("Recap artifact anchor is not an ancestor of the current completion boundary.");
    }

    internal static void ValidateReplaySafeBoundary(SessionJournalEventReader reader, EventAddress anchor) {
        using EventFrame frame = reader.ReadEvent(anchor).Unwrap();
        ValidateSessionHeader(anchor, frame.Header);
        var kind = (SessionEventKind)frame.Header.OpaqueEventKind;
        object body = SessionEventCodec.Decode(kind, frame.Payload, out _);
        switch (kind) {
            case SessionEventKind.RuntimeConfigSetup:
            case SessionEventKind.SystemPromptSetup:
            case SessionEventKind.SessionCreated:
            case SessionEventKind.ObservationAccepted:
            case SessionEventKind.CompletionAttemptFailed:
                return;
            case SessionEventKind.AgentActionProduced:
            case SessionEventKind.ImportedAgentAction:
                if (((AgentActionProducedBody)body).Action.ToolCalls.Count == 0) { return; }
                throw new InvalidDataException("Recap artifact anchor is an action with outstanding tool dependencies.");
            case SessionEventKind.ToolExecutionStarted:
            case SessionEventKind.ToolResultObserved:
            case SessionEventKind.CompletionRequestPrepared:
            case SessionEventKind.CompletionAttemptRestarted:
                throw new InvalidDataException($"Recap artifact anchor kind '{kind}' is not replay-safe in CS-3B.");
            default:
                throw new InvalidDataException($"Unsupported recap artifact anchor kind '{kind}'.");
        }
    }

    internal static TailFoldResult FoldSuffix(
        SessionGoverningSetup seed,
        IReadOnlyList<DecodedSessionEvent> events,
        SessionExecutionRecovery? executionSeed = null
    ) {
        EventAddress runtimeAddress = seed.RuntimeConfigSetupAddress;
        SessionRuntimeConfiguration runtimeConfig = seed.RuntimeConfig;
        EventAddress promptAddress = seed.SystemPromptSetupAddress;
        string systemPrompt = seed.SystemPrompt;
        var context = new List<IHistoryMessage>();
        ActionMessage? openAction = null;
        var observedResults = new Dictionary<string, ToolResultObservedBody>(StringComparer.Ordinal);
        RawToolCall? pendingCall = null;
        bool pendingStarted = false;
        long? executionSequenceCheckpoint =
            executionSeed?.State.ToolExecutionSequenceCheckpoint;
        string? activeCorrelationId = executionSeed?.State.ActiveCorrelationId;
        SessionExecutionPhase phase = executionSeed?.State.Phase
            ?? InferSeedPhase(executionSeed?.State.HeadKind);
        SessionToolRuntimeIdentity? pendingToolRuntimeIdentity = null;
        CompletionRequestPreparedBody? sourcePrepared = null;
        EventAddress? sourcePreparedAddress = null;
        string? activeAttemptId = null;
        EventAddress? activeAttemptAddress = null;
        HashSet<string>? seenAttemptIds = null;
        SessionEventKind? priorKind = executionSeed?.State.HeadKind;
        EventAddress? priorAddress = executionSeed?.Head ?? seed.Head;

        foreach (DecodedSessionEvent ev in events) {
            if (ev.Parent != priorAddress) {
                throw new InvalidDataException(
                    $"{ev.Kind} at {ev.Address} does not directly descend from the prior suffix event."
                );
            }
            switch (ev.Kind) {
                case SessionEventKind.RuntimeConfigSetup:
                    EnsureSetupPhase(ev, phase, openAction, sourcePrepared);
                    runtimeAddress = ev.Address;
                    runtimeConfig = RequireBody<SessionRuntimeConfiguration>(ev);
                    phase = phase == SessionExecutionPhase.Empty
                        ? SessionExecutionPhase.Empty
                        : SessionExecutionPhase.Idle;
                    activeCorrelationId = null;
                    break;
                case SessionEventKind.SystemPromptSetup:
                    EnsureSetupPhase(ev, phase, openAction, sourcePrepared);
                    promptAddress = ev.Address;
                    systemPrompt = RequireBody<SystemPromptSetupBody>(ev).Content;
                    phase = phase == SessionExecutionPhase.Empty
                        ? SessionExecutionPhase.Empty
                        : SessionExecutionPhase.Idle;
                    activeCorrelationId = null;
                    break;
                case SessionEventKind.SessionCreated:
                    EnsureNoOpenTool(ev, openAction);
                    _ = RequireBody<SessionCreatedBody>(ev);
                    if (phase != SessionExecutionPhase.Empty
                        || priorKind != SessionEventKind.SystemPromptSetup) {
                        throw new InvalidDataException(
                            $"{ev.Kind} at {ev.Address} must complete the bootstrap setup run."
                        );
                    }
                    executionSequenceCheckpoint = 0;
                    activeCorrelationId = null;
                    phase = SessionExecutionPhase.Idle;
                    break;
                case SessionEventKind.ArtifactSetCommitted:
                    EnsureNoOpenTool(ev, openAction);
                    _ = RequireBody<ArtifactSetCommittedBody>(ev);
                    if (phase is not (
                        SessionExecutionPhase.Idle
                        or SessionExecutionPhase.TurnFailed
                    )
                        || sourcePrepared is not null) {
                        throw new InvalidDataException(
                            $"{ev.Kind} at {ev.Address} must appear at an idle or failed suffix boundary."
                        );
                    }
                    activeCorrelationId = null;
                    phase = SessionExecutionPhase.Idle;
                    break;
                case SessionEventKind.CompletionAttemptFailed: {
                    EnsureNoOpenTool(ev, openAction);
                    CompletionAttemptFailedBody failed =
                        RequireBody<CompletionAttemptFailedBody>(ev);
                    if (phase != SessionExecutionPhase.AwaitingCompletion
                        || sourcePrepared is null
                        || ev.Parent != activeAttemptAddress
                        || !string.Equals(failed.AttemptId, activeAttemptId, StringComparison.Ordinal)) {
                        throw new InvalidDataException(
                            $"{ev.Kind} at {ev.Address} does not fail the active suffix completion attempt."
                        );
                    }
                    sourcePrepared = null;
                    sourcePreparedAddress = null;
                    activeAttemptId = null;
                    activeAttemptAddress = null;
                    seenAttemptIds = null;
                    activeCorrelationId = null;
                    phase = SessionExecutionPhase.TurnFailed;
                    break;
                }
                case SessionEventKind.CompletionAttemptRestarted: {
                    EnsureNoOpenTool(ev, openAction);
                    CompletionAttemptRestartedBody restarted =
                        RequireBody<CompletionAttemptRestartedBody>(ev);
                    if (phase != SessionExecutionPhase.AwaitingCompletion
                        || sourcePrepared is null
                        || sourcePreparedAddress is null
                        || seenAttemptIds is null
                        || restarted.SourcePreparedAddress != sourcePreparedAddress
                        || ev.Parent != activeAttemptAddress
                        || !string.Equals(restarted.ReplacesAttemptId, activeAttemptId, StringComparison.Ordinal)
                        || string.IsNullOrWhiteSpace(restarted.AttemptId)
                        || string.Equals(restarted.AttemptId, restarted.ReplacesAttemptId, StringComparison.Ordinal)
                        || !seenAttemptIds.Add(restarted.AttemptId)) {
                        throw new InvalidDataException(
                            $"{ev.Kind} at {ev.Address} does not replace the active suffix Prepared attempt."
                        );
                    }
                    activeAttemptId = restarted.AttemptId;
                    activeAttemptAddress = ev.Address;
                    break;
                }
                case SessionEventKind.CompletionRequestPrepared: {
                    EnsureNoOpenTool(ev, openAction);
                    CompletionRequestPreparedBody prepared =
                        RequireBody<CompletionRequestPreparedBody>(ev);
                    if (phase != SessionExecutionPhase.AwaitingAgentAction
                        || sourcePrepared is not null
                        || prepared.Attempt.ReplacesAttemptId is not null) {
                        throw new InvalidDataException(
                            $"{ev.Kind} at {ev.Address} requires an unprepared suffix completion boundary."
                        );
                    }
                    string expectedReason = priorKind switch {
                        SessionEventKind.ObservationAccepted => "observation",
                        SessionEventKind.ToolResultObserved => "tool-continuation",
                        _ => throw new InvalidDataException(
                            $"{ev.Kind} at {ev.Address} does not follow a completion boundary."
                        )
                    };
                    if (!string.Equals(prepared.Attempt.Reason, expectedReason, StringComparison.Ordinal)
                        || !string.Equals(prepared.Plan.Reason, expectedReason, StringComparison.Ordinal)
                        || !string.Equals(prepared.Attempt.CorrelationId, activeCorrelationId, StringComparison.Ordinal)) {
                        throw new InvalidDataException(
                            $"{ev.Kind} at {ev.Address} reason or correlation does not match its suffix completion boundary."
                        );
                    }
                    if (executionSequenceCheckpoint is long current
                        && prepared.Execution.LastIssuedToolExecutionSequence != current) {
                        throw new InvalidDataException(
                            $"{ev.Kind} at {ev.Address} changes the suffix execution checkpoint."
                        );
                    }
                    executionSequenceCheckpoint =
                        prepared.Execution.LastIssuedToolExecutionSequence;
                    sourcePrepared = prepared;
                    sourcePreparedAddress = ev.Address;
                    activeAttemptId = prepared.Attempt.AttemptId;
                    activeAttemptAddress = ev.Address;
                    seenAttemptIds = new HashSet<string>(StringComparer.Ordinal) {
                        prepared.Attempt.AttemptId
                    };
                    activeCorrelationId = prepared.Attempt.CorrelationId;
                    phase = SessionExecutionPhase.AwaitingCompletion;
                    break;
                }
                case SessionEventKind.ObservationAccepted:
                    EnsureNoOpenTool(ev, openAction);
                    if (phase is not (
                        SessionExecutionPhase.Idle
                        or SessionExecutionPhase.TurnFailed
                    )) {
                        throw new InvalidDataException(
                            $"{ev.Kind} at {ev.Address} must appear at an idle or failed suffix boundary."
                        );
                    }
                    context.Add(new ObservationMessage(RequireBody<ObservationAcceptedBody>(ev).Content));
                    activeCorrelationId =
                        $"atelia.session-journal.turn.v1:{EventAddressTextCodec.Format(ev.Address)}";
                    sourcePrepared = null;
                    sourcePreparedAddress = null;
                    activeAttemptId = null;
                    activeAttemptAddress = null;
                    seenAttemptIds = null;
                    phase = SessionExecutionPhase.AwaitingAgentAction;
                    break;
                case SessionEventKind.AgentActionProduced:
                case SessionEventKind.ImportedAgentAction: {
                    EnsureNoOpenTool(ev, openAction);
                    AgentActionProducedBody actionBody =
                        RequireBody<AgentActionProducedBody>(ev);
                    ActionMessage action = actionBody.Action;
                    ValidateToolCalls(ev, action);
                    bool isPreparedAction =
                        ev.Kind == SessionEventKind.AgentActionProduced
                        && phase == SessionExecutionPhase.AwaitingCompletion;
                    bool isImportedAction =
                        ev.Kind == SessionEventKind.ImportedAgentAction
                        && phase == SessionExecutionPhase.AwaitingAgentAction
                        && sourcePrepared is null
                        && priorKind is (
                            SessionEventKind.ObservationAccepted
                            or SessionEventKind.ToolResultObserved
                        );
                    if (!isPreparedAction && !isImportedAction) {
                        throw new InvalidDataException(
                            $"{ev.Kind} at {ev.Address} does not directly follow its required completion boundary."
                        );
                    }
                    if (!string.Equals(
                            actionBody.CorrelationId,
                            activeCorrelationId,
                            StringComparison.Ordinal
                        )
                        || executionSequenceCheckpoint is long current
                            && actionBody.Execution.LastIssuedToolExecutionSequence != current) {
                        throw new InvalidDataException(
                            $"{ev.Kind} at {ev.Address} changes the suffix correlation or execution checkpoint."
                        );
                    }
                    executionSequenceCheckpoint =
                        actionBody.Execution.LastIssuedToolExecutionSequence;
                    if (ev.Kind == SessionEventKind.AgentActionProduced) {
                        SessionToolRuntimeIdentity? expectedRuntimeIdentity =
                            action.ToolCalls.Count == 0
                                ? null
                                : sourcePrepared?.ToolSet.RuntimeIdentity;
                        if (sourcePrepared is null
                            || ev.Parent != activeAttemptAddress
                            || !string.Equals(actionBody.CorrelationId, sourcePrepared.Attempt.CorrelationId, StringComparison.Ordinal)
                            || actionBody.Execution != sourcePrepared.Execution
                            || actionBody.ToolRuntimeIdentity != expectedRuntimeIdentity) {
                            throw new InvalidDataException(
                                $"{ev.Kind} at {ev.Address} does not match its suffix Prepared snapshot."
                            );
                        }
                    }
                    activeCorrelationId = actionBody.CorrelationId;
                    sourcePrepared = null;
                    sourcePreparedAddress = null;
                    activeAttemptId = null;
                    activeAttemptAddress = null;
                    seenAttemptIds = null;
                    context.Add(action);
                    if (action.ToolCalls.Count > 0) {
                        pendingToolRuntimeIdentity = actionBody.ToolRuntimeIdentity
                            ?? throw new InvalidDataException(
                                $"{ev.Kind} at {ev.Address} has tool calls without runtime identity."
                            );
                        openAction = action;
                        observedResults.Clear();
                        pendingCall = action.ToolCalls[0];
                        pendingStarted = false;
                        phase = SessionExecutionPhase.AwaitingToolExecution;
                    }
                    else {
                        if (actionBody.ToolRuntimeIdentity is not null) {
                            throw new InvalidDataException(
                                $"{ev.Kind} at {ev.Address} has a runtime identity without tool calls."
                            );
                        }
                        activeCorrelationId = null;
                        phase = SessionExecutionPhase.Idle;
                    }
                    break;
                }
                case SessionEventKind.ToolExecutionStarted: {
                    if (phase != SessionExecutionPhase.AwaitingToolExecution
                        || openAction is null
                        || pendingCall is null
                        || pendingStarted) {
                        throw new InvalidDataException($"{ev.Kind} at {ev.Address} has no unstarted suffix-local pending tool call.");
                    }
                    ToolExecutionStartedBody started = RequireBody<ToolExecutionStartedBody>(ev);
                    EnsurePendingMatches(ev, pendingCall, started.ToolCallId, started.ToolName, started.RawArgumentsJson);
                    if (pendingToolRuntimeIdentity != started.ToolRuntimeIdentity
                        || executionSequenceCheckpoint is not long current
                        || started.ExecutionSequence != checked(current + 1)) {
                        throw new InvalidDataException(
                            $"{ev.Kind} at {ev.Address} does not match the pending runtime identity and next reserved sequence."
                        );
                    }
                    executionSequenceCheckpoint = started.ExecutionSequence;
                    pendingStarted = true;
                    break;
                }
                case SessionEventKind.ToolResultObserved: {
                    if (phase != SessionExecutionPhase.AwaitingToolExecution
                        || openAction is null
                        || pendingCall is null
                        || !pendingStarted) {
                        throw new InvalidDataException($"{ev.Kind} at {ev.Address} has no started suffix-local pending tool call.");
                    }
                    ToolResultObservedBody result = RequireBody<ToolResultObservedBody>(ev);
                    EnsurePendingMatches(ev, pendingCall, result.ToolCallId, result.ToolName, rawArgumentsJson: null);
                    if (result.ExecutionSequence != executionSequenceCheckpoint) {
                        throw new InvalidDataException(
                            $"{ev.Kind} at {ev.Address} does not repeat the active reserved sequence."
                        );
                    }
                    if (!observedResults.TryAdd(result.ToolCallId, result)) {
                        throw new InvalidDataException($"{ev.Kind} at {ev.Address} duplicates suffix tool result '{result.ToolCallId}'.");
                    }
                    pendingCall = openAction.ToolCalls.FirstOrDefault(call => !observedResults.ContainsKey(call.ToolCallId));
                    pendingStarted = false;
                    if (pendingCall is null) {
                        context.Add(ProjectToolResults(openAction, observedResults));
                        openAction = null;
                        observedResults.Clear();
                        pendingToolRuntimeIdentity = null;
                        phase = SessionExecutionPhase.AwaitingAgentAction;
                    }
                    break;
                }
                default:
                    throw new InvalidDataException($"Unsupported suffix event kind '{ev.Kind}'.");
            }
            priorKind = ev.Kind;
            priorAddress = ev.Address;
        }
        if (openAction is not null || pendingCall is not null || pendingStarted) {
            throw new InvalidDataException("Recap raw suffix ends with unresolved tool dependencies.");
        }

        EventAddress finalHead = events.Count == 0 ? seed.Head : events[^1].Address;
        return new TailFoldResult(
            new SessionGoverningSetup(
                finalHead,
                runtimeAddress,
                runtimeConfig,
                promptAddress,
                systemPrompt
            ),
            context,
            activeCorrelationId,
            executionSequenceCheckpoint ?? 0,
            phase
        );
    }

    private static SessionExecutionPhase InferSeedPhase(
        SessionEventKind? headKind
    ) => headKind switch {
        null
            or SessionEventKind.RuntimeConfigSetup
            or SessionEventKind.SystemPromptSetup =>
                SessionExecutionPhase.Empty,
        SessionEventKind.SessionCreated
            or SessionEventKind.ArtifactSetCommitted
            or SessionEventKind.AgentActionProduced
            or SessionEventKind.ImportedAgentAction =>
                SessionExecutionPhase.Idle,
        SessionEventKind.ObservationAccepted
            or SessionEventKind.ToolResultObserved =>
                SessionExecutionPhase.AwaitingAgentAction,
        SessionEventKind.CompletionRequestPrepared
            or SessionEventKind.CompletionAttemptRestarted =>
                SessionExecutionPhase.AwaitingCompletion,
        SessionEventKind.CompletionAttemptFailed =>
            SessionExecutionPhase.TurnFailed,
        SessionEventKind.ToolExecutionStarted =>
            SessionExecutionPhase.AwaitingToolExecution,
        _ => throw new InvalidDataException(
            $"Cannot infer suffix seed phase for '{headKind}'."
        )
    };

    private static void EnsureSetupPhase(
        DecodedSessionEvent ev,
        SessionExecutionPhase phase,
        ActionMessage? openAction,
        CompletionRequestPreparedBody? sourcePrepared
    ) {
        EnsureNoOpenTool(ev, openAction);
        if (sourcePrepared is not null
            || phase is not (
                SessionExecutionPhase.Empty
                or SessionExecutionPhase.Idle
                or SessionExecutionPhase.TurnFailed
            )) {
            throw new InvalidDataException(
                $"{ev.Kind} at {ev.Address} must appear only at a setup, idle, or failed suffix boundary."
            );
        }
    }

    internal static SessionRequestArtifactContextSnapshot AggregateContextSnapshots(
        IReadOnlyList<SessionRequestArtifactInput> inputs
    ) => new(
        JoinSnapshotField(inputs, static snapshot => snapshot.SystemPromptFragment),
        JoinSnapshotField(inputs, static snapshot => snapshot.ObservationMessage),
        JoinSnapshotField(inputs, static snapshot => snapshot.ActionMessage)
    );

    private static string JoinSnapshotField(
        IReadOnlyList<SessionRequestArtifactInput> inputs,
        Func<SessionRequestArtifactContextSnapshot, string> selector
    ) => string.Join(
        "\n\n",
        inputs.Select(input => selector(input.ContextSnapshot))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
    );

    private static TargetContribution CreateTargetContribution(DerivedRecapArtifact artifact) {
        SessionRequestArtifactInput input = CreateArtifactInput(artifact);
        return new TargetContribution(
            artifact.Target.Carrier,
            artifact.Target.BlockKey,
            input
        );
    }

    internal static SessionRequestArtifactInput CreateArtifactInput(
        DerivedRecapArtifact artifact
    ) {
        if (!artifact.MemoryPack.TryGetBlock(artifact.Target, out MemoryPackBlock block)) {
            throw new InvalidDataException(
                $"Recap artifact '{artifact.ArtifactId}' is missing its target block."
            );
        }
        var singleton = new MemoryPack();
        singleton.GetCarrier(artifact.Target.Carrier).Add(
            artifact.Target.BlockKey,
            new MemoryPackBlock(block.Text)
        );
        RenderedMemoryPack rendered = singleton.Render();
        var snapshot = artifact.Target.Carrier switch {
            MemoryPackCarrier.System => new SessionRequestArtifactContextSnapshot(
                rendered.SystemPromptFragment,
                "",
                ""
            ),
            MemoryPackCarrier.Observation => new SessionRequestArtifactContextSnapshot(
                "",
                rendered.ObservationMessage,
                ""
            ),
            MemoryPackCarrier.Action => new SessionRequestArtifactContextSnapshot(
                "",
                "",
                rendered.ActionMessage
            ),
            _ => throw new InvalidDataException(
                $"Unsupported recap target carrier '{artifact.Target.Carrier}'."
            )
        };
        return new SessionRequestArtifactInput(
            artifact.ArtifactId,
            artifact.ArtifactKind,
            SessionArtifactContextSnapshotHasher.ComputeSha256(snapshot),
            snapshot
        );
    }

    internal static (string SystemPrompt, ImmutableArray<IHistoryMessage> Context) ExpandContextSnapshot(
        string baseSystemPrompt,
        SessionRequestArtifactContextSnapshot snapshot
    ) {
        ArgumentNullException.ThrowIfNull(baseSystemPrompt);
        ArgumentNullException.ThrowIfNull(snapshot);
        var systemPrompt = new StringBuilder(baseSystemPrompt);
        var context = ImmutableArray.CreateBuilder<IHistoryMessage>(2);
        if (!string.IsNullOrWhiteSpace(snapshot.SystemPromptFragment)) {
            // This separator participates in the canonical request commitment and must not vary by OS.
            if (systemPrompt.Length > 0) { systemPrompt.Append("\n\n"); }
            systemPrompt.Append(snapshot.SystemPromptFragment.Trim());
        }
        if (!string.IsNullOrWhiteSpace(snapshot.ObservationMessage)) {
            context.Add(new ObservationMessage(snapshot.ObservationMessage));
        }
        if (!string.IsNullOrEmpty(snapshot.ActionMessage)) {
            context.Add(new ActionMessage([new ActionBlock.Text(snapshot.ActionMessage)]));
        }
        return (systemPrompt.ToString(), context.ToImmutable());
    }

    private static ToolResultsMessage ProjectToolResults(
        ActionMessage action,
        IReadOnlyDictionary<string, ToolResultObservedBody> observedResults
    ) {
        var results = new ToolResult[action.ToolCalls.Count];
        for (int i = 0; i < action.ToolCalls.Count; i++) {
            RawToolCall call = action.ToolCalls[i];
            ToolResultObservedBody body = observedResults[call.ToolCallId];
            results[i] = new ToolResult(body.ToolName, body.ToolCallId, body.Status, body.Blocks);
        }
        return new ToolResultsMessage(content: null, results);
    }

    private static void ValidateToolCalls(DecodedSessionEvent ev, ActionMessage action) {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (RawToolCall call in action.ToolCalls) {
            if (string.IsNullOrWhiteSpace(call.ToolCallId)
                || string.IsNullOrWhiteSpace(call.ToolName)
                || string.IsNullOrWhiteSpace(call.RawArgumentsJson)
                || !ids.Add(call.ToolCallId)) {
                throw new InvalidDataException($"{ev.Kind} at {ev.Address} contains invalid or duplicate tool calls.");
            }
        }
    }

    private static void EnsurePendingMatches(
        DecodedSessionEvent ev,
        RawToolCall pending,
        string callId,
        string toolName,
        string? rawArgumentsJson
    ) {
        if (!string.Equals(pending.ToolCallId, callId, StringComparison.Ordinal)
            || !string.Equals(pending.ToolName, toolName, StringComparison.Ordinal)
            || rawArgumentsJson is not null
                && !string.Equals(pending.RawArgumentsJson, rawArgumentsJson, StringComparison.Ordinal)) {
            throw new InvalidDataException($"{ev.Kind} at {ev.Address} does not match the current suffix pending tool call.");
        }
    }

    private static void EnsureNoOpenTool(DecodedSessionEvent ev, ActionMessage? openAction) {
        if (openAction is not null) {
            throw new InvalidDataException($"{ev.Kind} at {ev.Address} appears before suffix tool dependencies are closed.");
        }
    }

    private static T RequireBody<T>(DecodedSessionEvent ev) where T : class
        => ev.Body as T
            ?? throw new InvalidDataException($"{ev.Kind} at {ev.Address} decoded to unexpected body type.");

    private static void ValidateSessionHeader(EventAddress address, EventFrameHeader header) {
        if (!Enum.IsDefined(typeof(SessionEventKind), header.OpaqueEventKind)
            || header.Hint != default(AddressHint)) {
            throw new InvalidDataException($"Invalid SessionJournal event header at {address}.");
        }
    }

    internal sealed record TailFoldResult(
        SessionGoverningSetup GoverningSetup,
        IReadOnlyList<IHistoryMessage> Context,
        string? ActiveCorrelationId,
        long ToolExecutionSequenceCheckpoint,
        SessionExecutionPhase Phase
    );

    private sealed record TargetContribution(
        MemoryPackCarrier Carrier,
        string BlockKey,
        SessionRequestArtifactInput ArtifactInput
    );
}
