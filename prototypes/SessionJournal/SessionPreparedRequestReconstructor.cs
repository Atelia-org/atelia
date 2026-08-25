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

        using SessionJournalEventFrame frame = reader.ReadEvent(sourcePreparedAddress).Unwrap();
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

        IReadOnlyList<DecodedSessionEvent> rawEvents = ReadAndValidateRawRange(
            reader,
            manifest.Plan.RawStartExclusive,
            authoritativeRawEndInclusive,
            manifest.Plan.RawRangeSha256,
            cancellationToken
        );
        SessionGoverningSetup authoritativeRawStart =
            SessionAuthoritativeGoverningSetupResolver.Resolve(
                reader,
                manifest.Plan.RawStartExclusive,
                cancellationToken
            ).Setup;
        SessionGoverningSetup rawStartSetup = ReadSetupFromReferences(
            reader,
            manifest.Plan.RawStartExclusive,
            manifest.Plan.RawStartSetups,
            cancellationToken
        );
        if (rawStartSetup.RuntimeConfigSetupAddress
                != authoritativeRawStart.RuntimeConfigSetupAddress
            || rawStartSetup.SystemPromptSetupAddress
                != authoritativeRawStart.SystemPromptSetupAddress
            || rawStartSetup.RuntimeConfig != authoritativeRawStart.RuntimeConfig
            || !string.Equals(
                rawStartSetup.SystemPrompt,
                authoritativeRawStart.SystemPrompt,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "Prepared v5 plan.rawStartSetups do not match the authoritative governing setup at rawStartExclusive."
            );
        }
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

        ValidateRuntime(manifest, runtimeConfig);
        CompletionRequest request = ReconstructExactContextTail(
            reader,
            manifest,
            authoritativeRawEndInclusive,
            runtimeConfig,
            systemPrompt.Content,
            rawEvents,
            rawStartSetup,
            cancellationToken
        );

        byte[] canonicalBytes = SessionRequestCanonicalizer.Canonicalize(request);
        var actualCommitment = new SessionRequestCommitment(
            canonicalBytes.Length,
            SessionRequestCanonicalizer.Sha256Hex(canonicalBytes)
        );
        if (manifest.Commitment != actualCommitment) {
            throw new InvalidDataException(
                "completion-request-prepared commitment does not match the reconstructed canonical request: "
                + $"expectedLength={manifest.Commitment.ByteLength}, "
                + $"actualLength={actualCommitment.ByteLength}, "
                + $"expectedSha256={manifest.Commitment.Sha256}, "
                + $"actualSha256={actualCommitment.Sha256}."
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

    private static CompletionRequest ReconstructExactContextTail(
        SessionJournalEventReader reader,
        CompletionRequestPreparedBody manifest,
        EventAddress rawEndInclusive,
        SessionRuntimeConfiguration referencedRuntime,
        string referencedSystemPrompt,
        IReadOnlyList<DecodedSessionEvent> rawEvents,
        SessionGoverningSetup rawStartSetup,
        CancellationToken cancellationToken
    ) {
        EventAddress rawStartExclusive = manifest.Plan.RawStartExclusive;
        SessionExecutionRecovery seedRecovery =
            SessionExecutionTailResolver.Resolve(reader, rawStartExclusive, cancellationToken);
        SessionDependencyClosedFoldSeed foldSeed =
            SessionDependencyClosedFoldSeed.Create(
                rawStartSetup,
                seedRecovery
            );
        SessionExecutionRecovery finalRecovery =
            SessionExecutionTailResolver.Resolve(reader, rawEndInclusive, cancellationToken);
        if (finalRecovery.State.Phase != SessionExecutionPhase.AwaitingAgentAction
            || finalRecovery.State.HeadKind is not (
                SessionEventKind.ObservationAccepted
                or SessionEventKind.ToolResultObserved
            )) {
            throw new InvalidDataException(
                "Prepared v5 tail boundary must be ObservationAccepted or a dependency-closed ToolResultObserved."
            );
        }
        ValidateAttemptBoundary(
            manifest,
            rawEvents[^1],
            finalRecovery.State.ActiveCorrelationId
        );

        SessionTailContextProjection.TailFoldResult folded =
            SessionTailContextProjection.FoldSuffix(
                foldSeed,
                rawEvents
            );
        if (folded.GoverningSetup.Head != rawEndInclusive
            || folded.GoverningSetup.RuntimeConfigSetupAddress != manifest.Setups.RuntimeConfig.Address
            || folded.GoverningSetup.SystemPromptSetupAddress != manifest.Setups.SystemPrompt.Address
            || folded.GoverningSetup.RuntimeConfig != referencedRuntime
            || !string.Equals(folded.GoverningSetup.SystemPrompt, referencedSystemPrompt, StringComparison.Ordinal)
            || folded.Phase != finalRecovery.State.Phase
            || folded.ToolExecutionSequenceCheckpoint != manifest.Execution.LastIssuedToolExecutionSequence
            || folded.ToolExecutionSequenceCheckpoint != finalRecovery.State.ToolExecutionSequenceCheckpoint
            || !string.Equals(folded.ActiveCorrelationId, finalRecovery.State.ActiveCorrelationId, StringComparison.Ordinal)) {
            throw new InvalidDataException(
                "Prepared v5 tail fold does not match its pinned setup or exact final recovery."
            );
        }

        SessionRequestArtifactContextSnapshot aggregate =
            SessionCoherentRequestRecipe.AggregateExactInputs(manifest.Plan.ExactContextInputs);
        (string expandedSystemPrompt, ImmutableArray<IHistoryMessage> snapshotContext) =
            SessionCoherentRequestRecipe.Expand(
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
            new CompletionPromptPrefix(
                expandedSystemPrompt,
                CompletionOutputContract.ProviderDefault(
                    manifest.ToolSet.Definitions
                ),
                context.MoveToImmutable()
            ),
            tailMessages: [],
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
                    SessionOperationalSemantics
                        .BuildObservationCorrelationId(
                            finalEvent.Address
                        );
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

        if (!string.Equals(manifest.Origin.Reason, expectedReason, StringComparison.Ordinal)
            || !string.Equals(manifest.Origin.CorrelationId, correlationId, StringComparison.Ordinal)) {
            throw new InvalidDataException(
                "Completion request reason or correlation id does not match its authoritative raw boundary."
            );
        }
    }

    private static void ValidateRuntime(
        CompletionRequestPreparedBody manifest,
        SessionRuntimeConfiguration runtimeConfig
    ) {
        if (!string.Equals(manifest.Parameters.ModelId, runtimeConfig.ModelId, StringComparison.Ordinal)) {
            throw new InvalidDataException(
                "Manifest request model does not match the referenced runtime configuration."
            );
        }
    }

    private static SessionGoverningSetup ReadSetupFromReferences(
        SessionJournalEventReader reader,
        EventAddress head,
        SessionGoverningSetupReferences references,
        CancellationToken cancellationToken
    ) {
        SessionRuntimeConfiguration runtime =
            ReadAndValidateSetupReference<SessionRuntimeConfiguration>(
                reader,
                references.RuntimeConfig,
                SessionEventKind.RuntimeConfigSetup,
                cancellationToken
            );
        SystemPromptSetupBody prompt =
            ReadAndValidateSetupReference<SystemPromptSetupBody>(
                reader,
                references.SystemPrompt,
                SessionEventKind.SystemPromptSetup,
                cancellationToken
            );
        return new SessionGoverningSetup(
            head,
            references.RuntimeConfig.Address,
            runtime,
            references.SystemPrompt.Address,
            prompt.Content
        );
    }

    private static IReadOnlyList<DecodedSessionEvent> ReadAndValidateRawRange(
        SessionJournalEventReader reader,
        EventAddress rawStartExclusive,
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
            using SessionJournalEventFrame frame = reader.ReadEvent(address).Unwrap();
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
        using SessionJournalEventFrame frame = reader.ReadEvent(reference.Address).Unwrap();
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
