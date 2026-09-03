using Atelia.Completion.Abstractions;
using Atelia.EventJournal;

namespace Atelia.SessionJournal;

internal static class SessionJournalAuditScanner {
    public static SessionJournalAuditScanResult Scan(
        EventJournal.EventJournal journal,
        string branchName,
        RefId branchRefId,
        Action<SessionJournalAuditEvent> visitor,
        Action<EventJournal.EventJournal>?
            afterSnapshotValidatedForTest,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        ArgumentNullException.ThrowIfNull(visitor);

        EventAddress? capturedHead = journal.GetHead(branchRefId);
        if (capturedHead is null) {
            return new SessionJournalAuditScanResult(
                branchName,
                branchRefId,
                CapturedHead: null,
                ExecutionStateAtCapturedHead:
                    new SessionExecutionState(
                        SessionExecutionPhase.Empty,
                        HeadKind: null
                    ),
                EventCount: 0,
                LogicalPayloadBytes: 0,
                new SessionJournalAuditScanDiagnostics(
                    CapturedEventCount: 0,
                    RepositoryEventReadCount: 0,
                    IndexedHeaderLookupCount: 0,
                    IndexedEventLookupCount: 0,
                    DecodedPayloadBytes: 0,
                    PreparedReconstructionCount: 0
                )
            );
        }

        var repositoryReader =
            new SessionJournalEventReader(journal);
        var reverseEvents = new List<MaterializedAuditEvent>();
        var visited = new HashSet<EventAddress>();
        EventAddress? cursor = capturedHead;
        long logicalPayloadBytes = 0;
        while (cursor is { } address) {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(address)) {
                throw new InvalidDataException(
                    $"SessionJournal audit Parent chain contains a "
                    + $"cycle at {address}."
                );
            }

            using SessionJournalEventFrame frame =
                repositoryReader.ReadEvent(address).Unwrap();
            ValidateHeader(address, frame.Header);
            byte[] payload = frame.Payload.ToArray();
            var kind =
                (SessionEventKind)frame.Header.OpaqueEventKind;
            object body = SessionEventCodec.Decode(
                kind,
                payload,
                out int bodySchemaVersion
            );
            if (payload.Length != frame.Header.PayloadLength) {
                throw new InvalidDataException(
                    $"SessionJournal event {address} decoded payload "
                    + $"length {payload.Length} does not match header "
                    + $"length {frame.Header.PayloadLength}."
                );
            }
            string payloadSha256 =
                SessionRequestCanonicalizer.Sha256Hex(payload);
            logicalPayloadBytes = checked(
                logicalPayloadBytes + frame.Header.PayloadLength
            );
            var cached = new SessionJournalCachedEvent(
                address,
                frame.Header,
                payload
            );
            reverseEvents.Add(new MaterializedAuditEvent(
                cached,
                IsPrepared: kind == SessionEventKind.CompletionRequestPrepared,
                CreateAuditEvent(
                    cached,
                    kind,
                    bodySchemaVersion,
                    body,
                    payloadSha256
                )
            ));
            cursor = frame.Header.Parent;
        }

        reverseEvents.Reverse();
        EventAddress? expectedParent = null;
        foreach (MaterializedAuditEvent materialized in reverseEvents) {
            if (materialized.Cached.Header.Parent != expectedParent) {
                throw new InvalidDataException(
                    "SessionJournal audit chain is not "
                    + "root-to-head parent-contiguous at "
                    + $"{materialized.Cached.Address}."
                );
            }
            expectedParent = materialized.Cached.Address;
        }
        if (expectedParent != capturedHead) {
            throw new InvalidDataException(
                "SessionJournal audit chain did not terminate at the "
                + "captured exact ref head."
            );
        }

        var index = reverseEvents.ToDictionary(
            static materialized => materialized.Cached.Address,
            static materialized => materialized.Cached
        );
        var indexedReader = new SessionJournalEventReader(
            journal,
            index,
            cacheOnly: true
        );
        int preparedReconstructionCount = 0;
        foreach (MaterializedAuditEvent materialized in reverseEvents) {
            cancellationToken.ThrowIfCancellationRequested();
            if (!materialized.IsPrepared) {
                continue;
            }
            SessionPreparedRequestAuditVerifier.Verify(
                indexedReader,
                materialized.Cached.Address,
                materialized.PublicEvent.BodySchemaVersion,
                cancellationToken
            );
            preparedReconstructionCount = checked(
                preparedReconstructionCount + 1
            );
        }
        SessionExecutionState executionStateAtCapturedHead =
            SessionExecutionTailResolver.Resolve(
                indexedReader,
                capturedHead.Value,
                cancellationToken
            ).State;

        SessionJournalReaderStorageDiagnostics repositoryReads =
            repositoryReader.CaptureStorageDiagnostics();
        SessionJournalReaderStorageDiagnostics indexedReads =
            indexedReader.CaptureStorageDiagnostics();
        if (indexedReads.StorageHeaderPreviewReadCount != 0
            || indexedReads.StoragePayloadReadCount != 0) {
            throw new InvalidOperationException(
                "SessionJournal indexed audit reconstruction performed "
                + "unexpected repository I/O."
            );
        }

        afterSnapshotValidatedForTest?.Invoke(journal);
        foreach (MaterializedAuditEvent materialized in reverseEvents) {
            cancellationToken.ThrowIfCancellationRequested();
            visitor(materialized.PublicEvent);
        }

        return new SessionJournalAuditScanResult(
            branchName,
            branchRefId,
            capturedHead,
            executionStateAtCapturedHead,
            reverseEvents.Count,
            logicalPayloadBytes,
            new SessionJournalAuditScanDiagnostics(
                reverseEvents.Count,
                checked(
                    repositoryReads.StorageHeaderPreviewReadCount
                    + repositoryReads.StoragePayloadReadCount
                ),
                indexedReads.CachedHeaderReadCount,
                indexedReads.CachedPayloadReadCount,
                logicalPayloadBytes,
                preparedReconstructionCount
            )
        );
    }

    private static SessionJournalAuditEvent CreateAuditEvent(
        SessionJournalCachedEvent cached,
        SessionEventKind kind,
        int bodySchemaVersion,
        object body,
        string payloadSha256
    ) => new(
        cached.Address,
        cached.Header.Parent,
        kind,
        bodySchemaVersion,
        cached.Header.PayloadLength,
        payloadSha256,
        CreateAuditFact(kind, bodySchemaVersion, body)
    );

    private static SessionJournalAuditFact CreateAuditFact(
        SessionEventKind kind,
        int bodySchemaVersion,
        object body
    ) => kind switch {
        SessionEventKind.RuntimeConfigSetup =>
            new SessionJournalAuditRuntimeConfigFact(
                RequireBody<SessionRuntimeConfiguration>(kind, body)
            ),
        SessionEventKind.SystemPromptSetup =>
            new SessionJournalAuditSystemPromptFact(
                RequireBody<SystemPromptSetupBody>(kind, body).Content
            ),
        SessionEventKind.SessionCreated =>
            new SessionJournalAuditSessionCreatedFact(
                RequireBody<SessionCreatedBody>(kind, body).Origin
            ),
        SessionEventKind.ObservationAccepted =>
            CreateObservationFact(
                RequireBody<ObservationAcceptedBody>(kind, body)
            ),
        SessionEventKind.CompletionRequestPrepared =>
            CreatePreparedFact(
                SessionPreparedManifestView.FromDecoded(
                    bodySchemaVersion,
                    body
                )
            ),
        SessionEventKind.AgentActionProduced
            or SessionEventKind.ImportedAgentAction =>
            CreateActionFact(
                RequireBody<AgentActionProducedBody>(kind, body)
            ),
        SessionEventKind.ToolExecutionStarted =>
            CreateToolExecutionStartedFact(
                RequireBody<ToolExecutionStartedBody>(kind, body)
            ),
        SessionEventKind.ToolResultObserved =>
            CreateToolResultObservedFact(
                RequireBody<ToolResultObservedBody>(kind, body)
            ),
        SessionEventKind.CompletionAttemptStarted =>
            new SessionJournalAuditCompletionAttemptStartedFact(),
        SessionEventKind.CompletionAttemptFailed =>
            new SessionJournalAuditCompletionAttemptFailedFact(
                RequireBody<CompletionAttemptFailedBody>(
                    kind,
                    body
                ).TerminationKind
            ),
        _ => throw new NotSupportedException(
            $"Session event kind '{kind}' has no audit fact."
        )
    };

    private static SessionJournalAuditObservationFact
        CreateObservationFact(ObservationAcceptedBody body) =>
        new(
            SessionHistorySemanticCommitment
                .ComputeObservationContributionSha256(
                    new ObservationMessage(body.Content)
                )
        );

    private static SessionJournalAuditPreparedFact CreatePreparedFact(
        SessionPreparedManifestView body
    ) => new(
        body.Origin.CorrelationId,
        body.Origin.Reason,
        body.Execution.LastIssuedToolExecutionSequence,
        body.ToolSet.RuntimeIdentity
    );

    private static SessionJournalAuditActionFact CreateActionFact(
        AgentActionProducedBody body
    ) => new(
        Array.AsReadOnly(body.Action.ToolCalls.ToArray()),
        body.CorrelationId,
        body.Execution.LastIssuedToolExecutionSequence,
        body.ToolRuntimeIdentity,
        SessionHistorySemanticCommitment
            .ComputeActionContributionSha256(body.Action)
    );

    private static SessionJournalAuditToolExecutionStartedFact
        CreateToolExecutionStartedFact(
        ToolExecutionStartedBody body
    ) => new(
        body.ToolCallId,
        body.ToolName,
        body.RawArgumentsJson,
        body.OperationId,
        body.ExecutionSequence,
        body.ToolRuntimeIdentity
    );

    private static SessionJournalAuditToolResultObservedFact
        CreateToolResultObservedFact(
        ToolResultObservedBody body
    ) => new(
        body.ToolCallId,
        body.ToolName,
        body.ExecutionSequence,
        body.Status,
        SessionHistorySemanticCommitment.ComputeToolResultSha256(
            new ToolResult(
                body.ToolName,
                body.ToolCallId,
                body.Status,
                body.Blocks
            )
        )
    );

    private static T RequireBody<T>(
        SessionEventKind kind,
        object body
    ) where T : class
        => body as T
            ?? throw new InvalidDataException(
                $"Session event kind '{kind}' decoded to "
                + $"'{body.GetType().Name}', expected '{typeof(T).Name}'."
            );

    private static void ValidateHeader(
        EventAddress address,
        EventFrameHeader header
    ) {
        if (!Enum.IsDefined(
            typeof(SessionEventKind),
            header.OpaqueEventKind
        )) {
            throw new InvalidDataException(
                $"Unknown SessionJournal event kind "
                + $"'{header.OpaqueEventKind}' at {address}."
            );
        }
        if (header.Hint != default(AddressHint)) {
            throw new InvalidDataException(
                "SessionJournal trunk requires EventAddress hint 0, "
                + $"got '{header.Hint}' at {address}."
            );
        }
    }

    private sealed record MaterializedAuditEvent(
        SessionJournalCachedEvent Cached,
        bool IsPrepared,
        SessionJournalAuditEvent PublicEvent
    );
}
