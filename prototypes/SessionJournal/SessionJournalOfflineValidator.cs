using Atelia.EventJournal;

namespace Atelia.SessionJournal;

public sealed record SessionJournalOfflineValidationReport(
    string RepositoryPath,
    string? Head,
    int EventCount,
    long LogicalPayloadBytes,
    SessionExecutionPhase ExecutionPhase,
    SessionEventKind? HeadKind,
    long ToolExecutionSequenceCheckpoint,
    string? RuntimeConfigSetup,
    string? SystemPromptSetup,
    string? ModelId,
    string? CompletionSurfaceId,
    int PreparedRequestCount
);

/// <summary>
/// Full, read-only validation intended for administration and offline migration tooling.
/// It deliberately pays the cost of checked root-to-head replay and compares that oracle
/// with the online tail resolver at the exact same head.
/// </summary>
public static class SessionJournalOfflineValidator {
    public static ValueTask<SessionJournalOfflineValidationReport> ValidateAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        string fullPath = Path.GetFullPath(repositoryPath);

        EventAddress? head;
        IReadOnlyList<EventAddress> chain;
        IReadOnlyList<DecodedSessionEvent> chronologicalEvents;
        SessionProjection projection;
        SessionExecutionRecovery recovery;
        int preparedRequestCount = 0;
        long logicalPayloadBytes = 0;

        using (var journal =
            EventJournal.EventJournal.OpenReadOnlyExisting(fullPath)) {
            RefId main = journal.OpenBranch(SessionJournalDefaults.MainBranchName).Unwrap();
            head = journal.GetHead(main);
            if (head is null) {
                chain = Array.AsReadOnly(Array.Empty<EventAddress>());
                chronologicalEvents =
                    Array.AsReadOnly(Array.Empty<DecodedSessionEvent>());
                projection = SessionReducer.Empty;
                recovery = SessionExecutionTailResolver.Resolve(
                    new SessionJournalEventReader(journal),
                    head,
                    cancellationToken
                );
            }
            else {
                var reverseAddresses = new List<EventAddress>();
                var reverseEvents = new List<DecodedSessionEvent>();
                var visited = new HashSet<EventAddress>();
                EventAddress? cursor = head;
                while (cursor is { } address) {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!visited.Add(address)) {
                        throw new InvalidDataException(
                            $"SessionJournal raw Parent chain contains a cycle at {address}."
                        );
                    }
                    using EventFrame frame = journal.ReadEvent(address).Unwrap();
                    ValidateHeader(address, frame.Header);
                    logicalPayloadBytes = checked(
                        logicalPayloadBytes + frame.Header.PayloadLength
                    );
                    var kind = (SessionEventKind)frame.Header.OpaqueEventKind;
                    object body = SessionEventCodec.Decode(
                        kind,
                        frame.Payload,
                        out int bodySchemaVersion
                    );
                    reverseAddresses.Add(address);
                    reverseEvents.Add(new DecodedSessionEvent(
                        kind,
                        bodySchemaVersion,
                        body,
                        address,
                        frame.Header.Parent
                    ));
                    if (body is CompletionRequestPreparedBody) {
                        preparedRequestCount = checked(preparedRequestCount + 1);
                    }
                    cursor = frame.Header.Parent;
                }

                reverseAddresses.Reverse();
                reverseEvents.Reverse();
                chain = reverseAddresses.AsReadOnly();
                chronologicalEvents = reverseEvents.AsReadOnly();
                EventAddress? expectedParent = null;
                foreach (DecodedSessionEvent decodedEvent in reverseEvents) {
                    if (decodedEvent.Parent != expectedParent) {
                        throw new InvalidDataException(
                            $"SessionJournal raw chain is not parent-contiguous at {decodedEvent.Address}."
                        );
                    }
                    expectedParent = decodedEvent.Address;
                }
                if (expectedParent != head) {
                    throw new InvalidDataException(
                        "SessionJournal raw chain did not terminate at the exact main head."
                    );
                }

                ValidatePreparedRequestReconstructions(
                    journal,
                    reverseEvents,
                    cancellationToken
                );
                projection = SessionReducer.Reduce(reverseEvents);
                recovery = SessionExecutionTailResolver.Resolve(
                    new SessionJournalEventReader(journal),
                    head,
                    cancellationToken
                );
            }
        }

        if (projection.Head != head || projection.ExecutionState != recovery.State) {
            throw new InvalidDataException(
                "Full SessionJournal reducer and tail execution resolver disagree at the exact head."
            );
        }

        SessionGoverningSetup? governingSetup = null;
        if (head is { } exactHead) {
            governingSetup = ResolveGoverningSetup(
                chronologicalEvents,
                exactHead,
                cancellationToken
            );
            if (projection.Config != governingSetup.RuntimeConfig
                || !string.Equals(
                    projection.SystemPrompt,
                    governingSetup.SystemPrompt,
                    StringComparison.Ordinal
                )) {
                throw new InvalidDataException(
                    "Full SessionJournal projection and governing setup resolver disagree."
                );
            }
        }

        return ValueTask.FromResult(new SessionJournalOfflineValidationReport(
            fullPath,
            EventAddressTextCodec.FormatNullable(head),
            chain.Count,
            logicalPayloadBytes,
            recovery.State.Phase,
            recovery.State.HeadKind,
            recovery.State.ToolExecutionSequenceCheckpoint,
            governingSetup is null
                ? null
                : EventAddressTextCodec.Format(
                    governingSetup.RuntimeConfigSetupAddress
                ),
            governingSetup is null
                ? null
                : EventAddressTextCodec.Format(
                    governingSetup.SystemPromptSetupAddress
                ),
            governingSetup?.RuntimeConfig.ModelId,
            governingSetup?.RuntimeConfig.CompletionSurfaceId,
            preparedRequestCount
        ));
    }

    private static SessionGoverningSetup ResolveGoverningSetup(
        IReadOnlyList<DecodedSessionEvent> chronologicalEvents,
        EventAddress targetHead,
        CancellationToken cancellationToken
    ) {
        EventAddress? runtimeConfigSetupAddress = null;
        SessionRuntimeConfiguration? runtimeConfig = null;
        EventAddress? systemPromptSetupAddress = null;
        string? systemPrompt = null;

        foreach (DecodedSessionEvent decodedEvent in chronologicalEvents) {
            cancellationToken.ThrowIfCancellationRequested();
            switch (decodedEvent.Kind) {
                case SessionEventKind.RuntimeConfigSetup:
                    runtimeConfigSetupAddress = decodedEvent.Address;
                    runtimeConfig =
                        decodedEvent.Body as SessionRuntimeConfiguration
                        ?? throw new InvalidDataException(
                            $"runtime-config-setup at {decodedEvent.Address} decoded to an unexpected body."
                        );
                    break;
                case SessionEventKind.SystemPromptSetup:
                    systemPromptSetupAddress = decodedEvent.Address;
                    systemPrompt =
                        (decodedEvent.Body as SystemPromptSetupBody)?.Content
                        ?? throw new InvalidDataException(
                            $"system-prompt-setup at {decodedEvent.Address} decoded to an unexpected body."
                        );
                    break;
            }

            if (decodedEvent.Address != targetHead) {
                continue;
            }
            if (runtimeConfigSetupAddress is null || runtimeConfig is null) {
                throw new InvalidDataException(
                    $"SessionJournal governing setup for head {targetHead} is missing runtime-config-setup on its parent chain."
                );
            }
            if (systemPromptSetupAddress is null || systemPrompt is null) {
                throw new InvalidDataException(
                    $"SessionJournal governing setup for head {targetHead} is missing system-prompt-setup on its parent chain."
                );
            }
            return new SessionGoverningSetup(
                targetHead,
                runtimeConfigSetupAddress.Value,
                runtimeConfig,
                systemPromptSetupAddress.Value,
                systemPrompt
            );
        }

        throw new InvalidDataException(
            $"SessionJournal governing setup target {targetHead} is not on the current main Parent chain."
        );
    }

    private static void ValidatePreparedRequestReconstructions(
        EventJournal.EventJournal journal,
        IReadOnlyList<DecodedSessionEvent> chronologicalEvents,
        CancellationToken cancellationToken
    ) {
        foreach (DecodedSessionEvent ev in chronologicalEvents) {
            cancellationToken.ThrowIfCancellationRequested();
            if (ev.Kind != SessionEventKind.CompletionRequestPrepared) {
                continue;
            }
            _ = SessionPreparedRequestReconstructor.Reconstruct(
                journal,
                ev.Address,
                cancellationToken
            );
        }
    }

    private static void ValidateHeader(
        EventAddress address,
        EventFrameHeader header
    ) {
        if (!Enum.IsDefined(typeof(SessionEventKind), header.OpaqueEventKind)
            || header.Hint != default(AddressHint)) {
            throw new InvalidDataException(
                $"Invalid SessionJournal event header at {address}."
            );
        }
    }
}
