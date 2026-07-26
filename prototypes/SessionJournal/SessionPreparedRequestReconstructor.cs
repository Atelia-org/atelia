using System.Collections.Immutable;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;

namespace Atelia.SessionJournal;

/// <summary>
/// Exact provider-neutral request reconstructed exclusively from a durable prepared manifest
/// and the raw boundary committed by its event header.
/// </summary>
internal sealed record SessionPreparedRequestReconstruction(
    CompletionRequest Request,
    byte[] CanonicalBytes,
    CompletionRequestPreparedBody Manifest,
    EventAddress RawEndInclusive,
    EventAddress? SourcePreparedAddress
);

/// <summary>
/// The single authoritative CS-3C reconstruction path. This component is intentionally
/// read-only: it never plans, opens a derived artifact store, or substitutes current runtime
/// configuration for the setup references pinned by the manifest.
/// </summary>
internal static class SessionPreparedRequestReconstructor {
    public static SessionPreparedRequestReconstruction Reconstruct(
        EventJournal.EventJournal journal,
        EventAddress sourcePreparedAddress,
        CancellationToken cancellationToken = default
    ) => Reconstruct(
        new SessionJournalEventReader(
            journal ?? throw new ArgumentNullException(nameof(journal))
        ),
        sourcePreparedAddress,
        cancellationToken
    );

    public static SessionPreparedRequestReconstruction Reconstruct(
        SessionJournalEventReader reader,
        EventAddress sourcePreparedAddress,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(reader);
        cancellationToken.ThrowIfCancellationRequested();

        using EventFrame frame = reader.ReadEvent(sourcePreparedAddress).Unwrap();
        ValidateSessionHeader(sourcePreparedAddress, frame.Header);
        var kind = (SessionEventKind)frame.Header.OpaqueEventKind;
        if (kind != SessionEventKind.CompletionRequestPrepared) {
            throw new InvalidDataException(
                $"Expected a completion-request-prepared event at {sourcePreparedAddress}, got '{kind}'."
            );
        }
        EventAddress rawEndInclusive = frame.Header.Parent
            ?? throw new InvalidDataException(
                $"CompletionRequestPrepared at {sourcePreparedAddress} must have a raw boundary parent."
            );
        object decoded = SessionEventCodec.Decode(kind, frame.Payload, out _);
        var manifest = decoded as CompletionRequestPreparedBody
            ?? throw new InvalidDataException(
                $"CompletionRequestPrepared at {sourcePreparedAddress} decoded to an unexpected body."
            );

        return Reconstruct(reader, manifest, rawEndInclusive, cancellationToken) with {
            SourcePreparedAddress = sourcePreparedAddress
        };
    }

    public static SessionPreparedRequestReconstruction Reconstruct(
        EventJournal.EventJournal journal,
        CompletionRequestPreparedBody manifest,
        EventAddress authoritativeRawEndInclusive,
        CancellationToken cancellationToken = default
    ) => Reconstruct(
        new SessionJournalEventReader(
            journal ?? throw new ArgumentNullException(nameof(journal))
        ),
        manifest,
        authoritativeRawEndInclusive,
        cancellationToken
    );

    public static SessionPreparedRequestReconstruction Reconstruct(
        SessionJournalEventReader reader,
        CompletionRequestPreparedBody manifest,
        EventAddress authoritativeRawEndInclusive,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(manifest);
        cancellationToken.ThrowIfCancellationRequested();
        SessionRequestManifestCodec.Validate(manifest);

        SessionRuntimeConfiguration runtimeConfig = ReadAndValidateSetupReference<SessionRuntimeConfiguration>(
            reader,
            manifest.Setups.RuntimeConfig,
            SessionEventKind.RuntimeConfigSetup,
            cancellationToken
        );
        SystemPromptSetupBody systemPrompt = ReadAndValidateSetupReference<SystemPromptSetupBody>(
            reader,
            manifest.Setups.SystemPrompt,
            SessionEventKind.SystemPromptSetup,
            cancellationToken
        );

        ValidateRuntimeAndTarget(manifest, runtimeConfig);
        IReadOnlyList<DecodedSessionEvent> rawEvents = ReadAndValidateRawRange(
            reader,
            manifest.Plan.RawStartExclusive,
            authoritativeRawEndInclusive,
            manifest.Plan.RawRangeSha256,
            cancellationToken
        );

        CompletionRequest request = manifest.Plan.SelectionPolicyId switch {
            SessionRequestManifestDefaults.FullRawSelectionPolicyId => ReconstructFullRaw(
                manifest,
                authoritativeRawEndInclusive,
                runtimeConfig,
                systemPrompt.Content,
                rawEvents
            ),
            SessionRequestManifestDefaults.ExplicitArtifactTailSelectionPolicyId => ReconstructExplicitArtifactTail(
                manifest,
                authoritativeRawEndInclusive,
                runtimeConfig,
                systemPrompt.Content,
                rawEvents
            ),
            SessionRequestManifestDefaults.CoherentArtifactTailSelectionPolicyId => ReconstructCoherentArtifactTail(
                reader,
                manifest,
                authoritativeRawEndInclusive,
                runtimeConfig,
                systemPrompt.Content,
                rawEvents,
                cancellationToken
            ),
            string unsupported => throw new NotSupportedException(
                $"Unsupported selection policy '{unsupported}'."
            )
        };

        byte[] canonicalBytes = SessionRequestCanonicalizer.Canonicalize(request);
        var actualCommitment = new SessionRequestCommitment(
            SessionRequestManifestDefaults.CommitmentAlgorithm,
            canonicalBytes.Length,
            SessionRequestCanonicalizer.Sha256Hex(canonicalBytes)
        );
        if (manifest.Commitment != actualCommitment) {
            throw new InvalidDataException(
                "completion-request-prepared commitment does not match the reconstructed canonical request."
            );
        }

        return new SessionPreparedRequestReconstruction(
            request,
            canonicalBytes,
            manifest,
            authoritativeRawEndInclusive,
            SourcePreparedAddress: null
        );
    }

    private static CompletionRequest ReconstructFullRaw(
        CompletionRequestPreparedBody manifest,
        EventAddress rawEndInclusive,
        SessionRuntimeConfiguration referencedRuntime,
        string referencedSystemPrompt,
        IReadOnlyList<DecodedSessionEvent> rawEvents
    ) {
        SessionProjection projection = SessionReducer.Reduce(rawEvents);
        if (projection.Head != rawEndInclusive
            || projection.ExecutionState.Phase != SessionExecutionPhase.AwaitingAgentAction) {
            throw new InvalidDataException(
                "Full-raw request boundary must reduce to an exact awaiting-agent-action head."
            );
        }
        if (projection.Config != referencedRuntime
            || !string.Equals(projection.SystemPrompt, referencedSystemPrompt, StringComparison.Ordinal)) {
            throw new InvalidDataException(
                "Full-raw governing setup does not match the setup payloads pinned by the manifest."
            );
        }

        EventAddress? finalRuntimeSetup = null;
        EventAddress? finalSystemPromptSetup = null;
        foreach (DecodedSessionEvent ev in rawEvents) {
            if (ev.Kind == SessionEventKind.RuntimeConfigSetup) {
                finalRuntimeSetup = ev.Address;
            }
            else if (ev.Kind == SessionEventKind.SystemPromptSetup) {
                finalSystemPromptSetup = ev.Address;
            }
        }
        if (finalRuntimeSetup != manifest.Setups.RuntimeConfig.Address
            || finalSystemPromptSetup != manifest.Setups.SystemPrompt.Address) {
            throw new InvalidDataException(
                "Full-raw setup references do not identify the final governing setup events in the raw range."
            );
        }

        ValidateAttemptBoundary(manifest, rawEvents[^1], projection.ExecutionState.ActiveCorrelationId);
        return new CompletionRequest(
            manifest.Parameters.ModelId,
            referencedSystemPrompt,
            projection.Context,
            manifest.ToolSet.Definitions,
            manifest.Parameters.MaxTokens
        );
    }

    private static CompletionRequest ReconstructExplicitArtifactTail(
        CompletionRequestPreparedBody manifest,
        EventAddress rawEndInclusive,
        SessionRuntimeConfiguration referencedRuntime,
        string referencedSystemPrompt,
        IReadOnlyList<DecodedSessionEvent> rawEvents
    ) {
        EventAddress rawStartExclusive = manifest.Plan.RawStartExclusive
            ?? throw new InvalidDataException("Explicit artifact tail reconstruction requires rawStartExclusive.");
        SessionRequestArtifactInput artifactInput = manifest.Plan.ArtifactInputs.Single();
        DecodedSessionEvent finalEvent = rawEvents[^1];
        ValidateAttemptBoundary(manifest, finalEvent, expectedCorrelationId: null);
        if (finalEvent.Kind != SessionEventKind.ObservationAccepted) {
            throw new InvalidDataException(
                "Explicit artifact tail request boundary must end at ObservationAccepted."
            );
        }

        var seed = new SessionGoverningSetup(
            rawStartExclusive,
            manifest.Setups.RuntimeConfig.Address,
            referencedRuntime,
            manifest.Setups.SystemPrompt.Address,
            referencedSystemPrompt
        );
        SessionTailContextProjection.TailFoldResult folded =
            SessionTailContextProjection.FoldSuffix(seed, rawEvents);
        if (folded.GoverningSetup.Head != rawEndInclusive
            || folded.GoverningSetup.RuntimeConfigSetupAddress != manifest.Setups.RuntimeConfig.Address
            || folded.GoverningSetup.SystemPromptSetupAddress != manifest.Setups.SystemPrompt.Address
            || folded.GoverningSetup.RuntimeConfig != referencedRuntime
            || !string.Equals(
                folded.GoverningSetup.SystemPrompt,
                referencedSystemPrompt,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "Explicit artifact tail governing setup does not match the setup payloads pinned by the manifest."
            );
        }

        (string expandedSystemPrompt, ImmutableArray<IHistoryMessage> snapshotContext) =
            SessionTailContextProjection.ExpandContextSnapshot(
                referencedSystemPrompt,
                artifactInput.ContextSnapshot
            );
        var context = ImmutableArray.CreateBuilder<IHistoryMessage>(
            snapshotContext.Length + folded.Context.Count
        );
        context.AddRange(snapshotContext);
        context.AddRange(folded.Context);

        return new CompletionRequest(
            manifest.Parameters.ModelId,
            expandedSystemPrompt,
            context.MoveToImmutable(),
            manifest.ToolSet.Definitions,
            manifest.Parameters.MaxTokens
        );
    }

    private static CompletionRequest ReconstructCoherentArtifactTail(
        SessionJournalEventReader reader,
        CompletionRequestPreparedBody manifest,
        EventAddress rawEndInclusive,
        SessionRuntimeConfiguration referencedRuntime,
        string referencedSystemPrompt,
        IReadOnlyList<DecodedSessionEvent> rawEvents,
        CancellationToken cancellationToken
    ) {
        EventAddress rawStartExclusive = manifest.Plan.RawStartExclusive
            ?? throw new InvalidDataException("Coherent artifact-set tail reconstruction requires rawStartExclusive.");
        SessionExecutionRecovery seedRecovery =
            SessionExecutionTailResolver.Resolve(reader, rawStartExclusive, cancellationToken);
        SessionExecutionRecovery finalRecovery =
            SessionExecutionTailResolver.Resolve(reader, rawEndInclusive, cancellationToken);
        if (finalRecovery.State.Phase != SessionExecutionPhase.AwaitingAgentAction
            || finalRecovery.State.HeadKind is not (
                SessionEventKind.ObservationAccepted
                or SessionEventKind.ToolResultObserved
            )) {
            throw new InvalidDataException(
                "Coherent artifact-set tail boundary must be ObservationAccepted or a dependency-closed ToolResultObserved."
            );
        }
        ValidateAttemptBoundary(
            manifest,
            rawEvents[^1],
            finalRecovery.State.ActiveCorrelationId
        );

        var seed = new SessionGoverningSetup(
            rawStartExclusive,
            manifest.Setups.RuntimeConfig.Address,
            referencedRuntime,
            manifest.Setups.SystemPrompt.Address,
            referencedSystemPrompt
        );
        SessionTailContextProjection.TailFoldResult folded =
            SessionTailContextProjection.FoldSuffix(seed, rawEvents, seedRecovery);
        if (folded.GoverningSetup.Head != rawEndInclusive
            || folded.GoverningSetup.RuntimeConfigSetupAddress != manifest.Setups.RuntimeConfig.Address
            || folded.GoverningSetup.SystemPromptSetupAddress != manifest.Setups.SystemPrompt.Address
            || folded.GoverningSetup.RuntimeConfig != referencedRuntime
            || !string.Equals(folded.GoverningSetup.SystemPrompt, referencedSystemPrompt, StringComparison.Ordinal)
            || folded.ToolExecutionSequenceCheckpoint != manifest.Execution.LastIssuedToolExecutionSequence
            || folded.ToolExecutionSequenceCheckpoint != finalRecovery.State.ToolExecutionSequenceCheckpoint
            || !string.Equals(folded.ActiveCorrelationId, finalRecovery.State.ActiveCorrelationId, StringComparison.Ordinal)) {
            throw new InvalidDataException(
                "Coherent artifact-set tail fold does not match its pinned setup or exact final recovery."
            );
        }

        SessionRequestArtifactContextSnapshot aggregate =
            SessionTailContextProjection.AggregateContextSnapshots(
                manifest.Plan.ArtifactInputs
            );
        (string expandedSystemPrompt, ImmutableArray<IHistoryMessage> snapshotContext) =
            SessionTailContextProjection.ExpandContextSnapshot(
                referencedSystemPrompt,
                aggregate
            );
        var context = ImmutableArray.CreateBuilder<IHistoryMessage>(
            snapshotContext.Length + folded.Context.Count
        );
        context.AddRange(snapshotContext);
        context.AddRange(folded.Context);
        return new CompletionRequest(
            manifest.Parameters.ModelId,
            expandedSystemPrompt,
            context.MoveToImmutable(),
            manifest.ToolSet.Definitions,
            manifest.Parameters.MaxTokens
        );
    }

    private static void ValidateAttemptBoundary(
        CompletionRequestPreparedBody manifest,
        DecodedSessionEvent finalEvent,
        string? expectedCorrelationId
    ) {
        string expectedReason;
        string correlationId;
        switch (finalEvent.Kind) {
            case SessionEventKind.ObservationAccepted:
                expectedReason = "observation";
                correlationId =
                    $"atelia.session-journal.turn.v1:{EventAddressTextCodec.Format(finalEvent.Address)}";
                break;
            case SessionEventKind.ToolResultObserved:
                expectedReason = "tool-continuation";
                correlationId = expectedCorrelationId
                    ?? throw new InvalidDataException(
                        "A tool-continuation boundary requires the reducer's active correlation id."
                    );
                break;
            default:
                throw new InvalidDataException(
                    $"Completion request raw boundary kind '{finalEvent.Kind}' is unsupported."
                );
        }

        if (!string.Equals(manifest.Attempt.Reason, expectedReason, StringComparison.Ordinal)
            || !string.Equals(manifest.Plan.Reason, expectedReason, StringComparison.Ordinal)
            || !string.Equals(manifest.Attempt.CorrelationId, correlationId, StringComparison.Ordinal)) {
            throw new InvalidDataException(
                "Completion request reason or correlation id does not match its authoritative raw boundary."
            );
        }
    }

    private static void ValidateRuntimeAndTarget(
        CompletionRequestPreparedBody manifest,
        SessionRuntimeConfiguration runtimeConfig
    ) {
        if (!string.Equals(manifest.Parameters.ModelId, runtimeConfig.ModelId, StringComparison.Ordinal)
            || !string.Equals(
                manifest.Target.CompletionSurfaceId,
                runtimeConfig.CompletionSurfaceId,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "Manifest request parameters or target surface do not match the referenced runtime configuration."
            );
        }
    }

    private static IReadOnlyList<DecodedSessionEvent> ReadAndValidateRawRange(
        SessionJournalEventReader reader,
        EventAddress? rawStartExclusive,
        EventAddress rawEndInclusive,
        string expectedRawRangeSha256,
        CancellationToken cancellationToken
    ) {
        var reverseEvents = new List<DecodedSessionEvent>();
        var reverseEntries = new List<SessionRawRangeHashEntry>();
        EventAddress? cursor = rawEndInclusive;
        while (cursor != rawStartExclusive) {
            cancellationToken.ThrowIfCancellationRequested();
            EventAddress address = cursor
                ?? throw new InvalidDataException(
                    $"Raw start '{rawStartExclusive}' is not an ancestor of raw end '{rawEndInclusive}'."
                );
            using EventFrame frame = reader.ReadEvent(address).Unwrap();
            ValidateSessionHeader(address, frame.Header);
            var kind = (SessionEventKind)frame.Header.OpaqueEventKind;
            object body = SessionEventCodec.Decode(kind, frame.Payload, out int bodySchemaVersion);
            reverseEvents.Add(new DecodedSessionEvent(
                kind,
                bodySchemaVersion,
                body,
                address,
                frame.Header.Parent
            ));
            reverseEntries.Add(new SessionRawRangeHashEntry(
                address,
                frame.Header.Parent,
                frame.Header.OpaqueEventKind,
                bodySchemaVersion,
                SessionRequestCanonicalizer.Sha256Hex(frame.Payload)
            ));
            cursor = frame.Header.Parent;
        }

        reverseEvents.Reverse();
        reverseEntries.Reverse();
        if (reverseEvents.Count == 0) {
            throw new InvalidDataException("Prepared request raw range must not be empty.");
        }
        string actualRawRangeSha256;
        try {
            actualRawRangeSha256 = SessionRawRangeHasher.Compute(
                rawStartExclusive,
                rawEndInclusive,
                reverseEntries
            );
        }
        catch (ArgumentException ex) {
            throw new InvalidDataException("Prepared request raw range is not parent-contiguous.", ex);
        }
        if (!string.Equals(actualRawRangeSha256, expectedRawRangeSha256, StringComparison.Ordinal)) {
            throw new InvalidDataException(
                "completion-request-prepared raw range hash does not match the authoritative raw range."
            );
        }
        return reverseEvents;
    }

    private static T ReadAndValidateSetupReference<T>(
        SessionJournalEventReader reader,
        SessionSetupReference reference,
        SessionEventKind expectedKind,
        CancellationToken cancellationToken
    ) where T : class {
        cancellationToken.ThrowIfCancellationRequested();
        using EventFrame frame = reader.ReadEvent(reference.Address).Unwrap();
        ValidateSessionHeader(reference.Address, frame.Header);
        var actualKind = (SessionEventKind)frame.Header.OpaqueEventKind;
        if (actualKind != expectedKind) {
            throw new InvalidDataException(
                $"Setup reference expected '{expectedKind}' at {reference.Address}, got '{actualKind}'."
            );
        }

        object body = SessionEventCodec.Decode(actualKind, frame.Payload, out int bodySchemaVersion);
        if (bodySchemaVersion != reference.BodySchemaVersion) {
            throw new InvalidDataException(
                $"Setup reference schema version mismatch at {reference.Address}: "
                + $"expected {reference.BodySchemaVersion}, got {bodySchemaVersion}."
            );
        }
        string payloadSha256 = SessionRequestCanonicalizer.Sha256Hex(frame.Payload);
        if (!string.Equals(payloadSha256, reference.PayloadSha256, StringComparison.Ordinal)) {
            throw new InvalidDataException($"Setup reference payload hash mismatch at {reference.Address}.");
        }

        return body as T
            ?? throw new InvalidDataException(
                $"Setup reference at {reference.Address} decoded to '{body.GetType().Name}', "
                + $"expected '{typeof(T).Name}'."
            );
    }

    private static void ValidateSessionHeader(EventAddress address, EventFrameHeader header) {
        if (!Enum.IsDefined(typeof(SessionEventKind), header.OpaqueEventKind)) {
            throw new InvalidDataException(
                $"Unknown SessionJournal event kind '{header.OpaqueEventKind}' at {address}."
            );
        }
        if (header.Hint != default(AddressHint)) {
            throw new InvalidDataException(
                $"SessionJournal trunk requires EventAddress hint 0, got '{header.Hint}' at {address}."
            );
        }
    }
}
