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
    SessionRequestArtifactInput ArtifactInput,
    SessionGoverningSetup FinalGoverningSetup,
    SessionTailProjectionDiagnostics Diagnostics
);

internal static class SessionTailContextProjection {
    public static async ValueTask<SessionTailContextProjectionResult> MaterializeAsync(
        EventJournal.EventJournal journal,
        string sessionJournalPath,
        EventAddress expectedParent,
        SessionGoverningSetup currentGoverningSetup,
        SessionTailProjectionOptions options,
        Func<EventAddress, CancellationToken, SessionGoverningSetup> resolveGoverningSetup,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(currentGoverningSetup);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(resolveGoverningSetup);

        DerivedRecapArtifact artifact = await DerivedRecapStore.Open(sessionJournalPath)
            .TryReadArtifactAsync(options.ArtifactId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException($"Exact recap artifact '{options.ArtifactId}' was not found or is unusable.");
        if (!string.Equals(artifact.ArtifactId, options.ArtifactId, StringComparison.Ordinal)
            || !string.Equals(artifact.Status, DerivedRecapArtifactStatus.Produced, StringComparison.Ordinal)) {
            throw new InvalidDataException($"Recap artifact '{options.ArtifactId}' is not a produced exact artifact.");
        }
        if (artifact.AnchorRawEvent != artifact.SourceEndInclusive) {
            throw new InvalidDataException("Recap artifact anchor must equal sourceEndInclusive.");
        }
        if (artifact.AnchorRawEvent == expectedParent) {
            throw new InvalidDataException(
                "Recap artifact anchor must be a strict ancestor of the current ObservationAccepted boundary."
            );
        }

        IReadOnlyList<EventAddress> suffixAddresses = CollectAndValidateSuffix(
            journal,
            expectedParent,
            artifact.AnchorRawEvent,
            artifact.SourceRawHead,
            cancellationToken,
            out int headerVisitCount
        );
        ValidateReplaySafeBoundary(journal, artifact.AnchorRawEvent);

        SessionGoverningSetup anchorSetup = resolveGoverningSetup(artifact.AnchorRawEvent, cancellationToken);
        var suffixEntries = new List<SessionRawRangeHashEntry>(suffixAddresses.Count);
        var suffixEvents = new List<DecodedSessionEvent>(suffixAddresses.Count);
        foreach (EventAddress address in suffixAddresses) {
            cancellationToken.ThrowIfCancellationRequested();
            using EventFrame frame = journal.ReadEvent(address).Unwrap();
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
            artifact.AnchorRawEvent,
            expectedParent,
            suffixEntries
        );
        TailFoldResult folded = FoldSuffix(anchorSetup, suffixEvents);
        if (currentGoverningSetup.Head != expectedParent
            || folded.GoverningSetup.Head != expectedParent
            || folded.GoverningSetup.RuntimeConfigSetupAddress != currentGoverningSetup.RuntimeConfigSetupAddress
            || folded.GoverningSetup.SystemPromptSetupAddress != currentGoverningSetup.SystemPromptSetupAddress
            || folded.GoverningSetup.RuntimeConfig != currentGoverningSetup.RuntimeConfig
            || !string.Equals(folded.GoverningSetup.SystemPrompt, currentGoverningSetup.SystemPrompt, StringComparison.Ordinal)) {
            throw new InvalidDataException("Tail projection governing setup does not match the exact current-head governing setup.");
        }

        RenderedMemoryPack rendered = artifact.MemoryPack.Render();
        var contextSnapshot = new SessionRequestArtifactContextSnapshot(
            rendered.SystemPromptFragment,
            rendered.ObservationMessage,
            rendered.ActionMessage
        );
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
            artifact.AnchorRawEvent,
            rawRangeSha256,
            new SessionRequestArtifactInput(
                artifact.ArtifactId,
                artifact.ArtifactKind,
                SessionArtifactContextSnapshotHasher.ComputeSha256(contextSnapshot),
                contextSnapshot
            ),
            folded.GoverningSetup,
            new SessionTailProjectionDiagnostics(
                headerVisitCount,
                suffixEvents.Count,
                suffixEvents.Count
            )
        );
    }

    private static IReadOnlyList<EventAddress> CollectAndValidateSuffix(
        EventJournal.EventJournal journal,
        EventAddress expectedParent,
        EventAddress anchor,
        EventAddress sourceRawHead,
        CancellationToken cancellationToken,
        out int headerVisitCount
    ) {
        var reverseSuffix = new List<EventAddress>();
        EventAddress? cursor = expectedParent;
        bool sawSourceHead = false;
        headerVisitCount = 0;
        while (cursor is { } address) {
            cancellationToken.ThrowIfCancellationRequested();
            EventFrameHeader header = journal.ReadEventHeaderPreview(address).Unwrap();
            headerVisitCount++;
            ValidateSessionHeader(address, header);
            if (address == sourceRawHead) { sawSourceHead = true; }
            if (address == anchor) {
                if (!sawSourceHead) {
                    throw new InvalidDataException(
                        "Recap artifact sourceRawHead is not on the current lineage at or after its anchor."
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

    private static void ValidateReplaySafeBoundary(EventJournal.EventJournal journal, EventAddress anchor) {
        using EventFrame frame = journal.ReadEvent(anchor).Unwrap();
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
                throw new InvalidDataException($"Recap artifact anchor kind '{kind}' is not replay-safe in CS-3B.");
            default:
                throw new InvalidDataException($"Unsupported recap artifact anchor kind '{kind}'.");
        }
    }

    private static TailFoldResult FoldSuffix(
        SessionGoverningSetup seed,
        IReadOnlyList<DecodedSessionEvent> events
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

        foreach (DecodedSessionEvent ev in events) {
            switch (ev.Kind) {
                case SessionEventKind.RuntimeConfigSetup:
                    EnsureNoOpenTool(ev, openAction);
                    runtimeAddress = ev.Address;
                    runtimeConfig = RequireBody<SessionRuntimeConfiguration>(ev);
                    break;
                case SessionEventKind.SystemPromptSetup:
                    EnsureNoOpenTool(ev, openAction);
                    promptAddress = ev.Address;
                    systemPrompt = RequireBody<SystemPromptSetupBody>(ev).Content;
                    break;
                case SessionEventKind.SessionCreated:
                case SessionEventKind.CompletionRequestPrepared:
                case SessionEventKind.CompletionAttemptFailed:
                    EnsureNoOpenTool(ev, openAction);
                    break;
                case SessionEventKind.ObservationAccepted:
                    EnsureNoOpenTool(ev, openAction);
                    context.Add(new ObservationMessage(RequireBody<ObservationAcceptedBody>(ev).Content));
                    break;
                case SessionEventKind.AgentActionProduced:
                case SessionEventKind.ImportedAgentAction: {
                    EnsureNoOpenTool(ev, openAction);
                    ActionMessage action = RequireBody<AgentActionProducedBody>(ev).Action;
                    ValidateToolCalls(ev, action);
                    context.Add(action);
                    if (action.ToolCalls.Count > 0) {
                        openAction = action;
                        observedResults.Clear();
                        pendingCall = action.ToolCalls[0];
                        pendingStarted = false;
                    }
                    break;
                }
                case SessionEventKind.ToolExecutionStarted: {
                    if (openAction is null || pendingCall is null || pendingStarted) {
                        throw new InvalidDataException($"{ev.Kind} at {ev.Address} has no unstarted suffix-local pending tool call.");
                    }
                    ToolExecutionStartedBody started = RequireBody<ToolExecutionStartedBody>(ev);
                    EnsurePendingMatches(ev, pendingCall, started.ToolCallId, started.ToolName, started.RawArgumentsJson);
                    pendingStarted = true;
                    break;
                }
                case SessionEventKind.ToolResultObserved: {
                    if (openAction is null || pendingCall is null || !pendingStarted) {
                        throw new InvalidDataException($"{ev.Kind} at {ev.Address} has no started suffix-local pending tool call.");
                    }
                    ToolResultObservedBody result = RequireBody<ToolResultObservedBody>(ev);
                    EnsurePendingMatches(ev, pendingCall, result.ToolCallId, result.ToolName, rawArgumentsJson: null);
                    if (!observedResults.TryAdd(result.ToolCallId, result)) {
                        throw new InvalidDataException($"{ev.Kind} at {ev.Address} duplicates suffix tool result '{result.ToolCallId}'.");
                    }
                    pendingCall = openAction.ToolCalls.FirstOrDefault(call => !observedResults.ContainsKey(call.ToolCallId));
                    pendingStarted = false;
                    if (pendingCall is null) {
                        context.Add(ProjectToolResults(openAction, observedResults));
                        openAction = null;
                        observedResults.Clear();
                    }
                    break;
                }
                default:
                    throw new InvalidDataException($"Unsupported suffix event kind '{ev.Kind}'.");
            }
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
            context
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
        return (systemPrompt.ToString(), context.MoveToImmutable());
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

    private sealed record TailFoldResult(
        SessionGoverningSetup GoverningSetup,
        IReadOnlyList<IHistoryMessage> Context
    );
}
