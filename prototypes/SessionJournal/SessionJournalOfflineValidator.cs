using System.Collections.Immutable;
using Atelia.EventJournal;
using Atelia.SessionJournal.Derived;

namespace Atelia.SessionJournal;

public static class SessionJournalOfflineReadiness {
    public const string ActiveCoherent = "active-coherent";
    public const string NeedsArtifactSetCheckpoint = "needs-artifact-set-checkpoint";
}

public sealed record SessionJournalOfflineArtifactMemberReport(
    string RoleId,
    string ArtifactId,
    bool Available,
    string? ArtifactKind,
    string? Target,
    string? Anchor,
    string? Issue
);

public sealed record SessionJournalOfflineArtifactSetReport(
    string Address,
    string PolicyId,
    string PolicyFingerprint,
    string CommonAnchor,
    IReadOnlyList<SessionJournalOfflineArtifactMemberReport> Members,
    bool IsUsable
);

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
    int PreparedRequestCount,
    SessionJournalOfflineArtifactSetReport? ActiveArtifactSet,
    string Readiness
);

/// <summary>
/// Full, read-only validation intended for administration and offline migration tooling.
/// It deliberately pays the cost of checked root-to-head replay and compares that oracle
/// with the online tail resolver at the exact same head.
/// </summary>
public static class SessionJournalOfflineValidator {
    public static async ValueTask<SessionJournalOfflineValidationReport> ValidateAsync(
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
        RawArtifactSetActivation? latestArtifactSet = null;
        var artifactSets = new Dictionary<EventAddress, RawArtifactSetActivation>();
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
                    else if (body is ArtifactSetCommittedBody artifactSet) {
                        ValidateSetupReference(
                            journal,
                            artifactSet.CoverageSetups.RuntimeConfig,
                            SessionEventKind.RuntimeConfigSetup
                        );
                        ValidateSetupReference(
                            journal,
                            artifactSet.CoverageSetups.SystemPrompt,
                            SessionEventKind.SystemPromptSetup
                        );
                        ValidateSetupReference(
                            journal,
                            artifactSet.CurrentSetups.RuntimeConfig,
                            SessionEventKind.RuntimeConfigSetup
                        );
                        ValidateSetupReference(
                            journal,
                            artifactSet.CurrentSetups.SystemPrompt,
                            SessionEventKind.SystemPromptSetup
                        );
                        var activation = new RawArtifactSetActivation(
                            address,
                            frame.Header.Parent,
                            artifactSet,
                            new SessionArtifactSetReference(
                                address,
                                bodySchemaVersion,
                                SessionRequestCanonicalizer.Sha256Hex(
                                    frame.Payload
                                )
                            )
                        );
                        artifactSets.Add(address, activation);
                        latestArtifactSet ??= activation;
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

                ValidateArtifactSetAndPreparedProvenance(
                    reverseEvents,
                    artifactSets
                );
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
            if (latestArtifactSet is { } setupActivation) {
                SessionGoverningSetup coverage =
                    ResolveGoverningSetup(
                        chronologicalEvents,
                        setupActivation.Body.CommonAnchor,
                        cancellationToken
                    );
                EventAddress activationParent = setupActivation.Parent
                    ?? throw new InvalidDataException(
                        "ArtifactSetCommitted requires an exact raw parent."
                    );
                SessionGoverningSetup current =
                    ResolveGoverningSetup(
                        chronologicalEvents,
                        activationParent,
                        cancellationToken
                    );
                if (coverage.RuntimeConfigSetupAddress
                        != setupActivation.Body.CoverageSetups.RuntimeConfig.Address
                    || coverage.SystemPromptSetupAddress
                        != setupActivation.Body.CoverageSetups.SystemPrompt.Address
                    || current.RuntimeConfigSetupAddress
                        != setupActivation.Body.CurrentSetups.RuntimeConfig.Address
                    || current.SystemPromptSetupAddress
                        != setupActivation.Body.CurrentSetups.SystemPrompt.Address) {
                    throw new InvalidDataException(
                        "ArtifactSetCommitted setup references do not match their authoritative raw boundaries."
                    );
                }
            }
        }

        SessionJournalOfflineArtifactSetReport? artifactSetReport = null;
        if (latestArtifactSet is { } active) {
            var chainPositions = chain
                .Select(static (address, index) => (address, index))
                .ToDictionary(static item => item.address, static item => item.index);
            var memberReports =
                new List<SessionJournalOfflineArtifactMemberReport>(
                    active.Body.Members.Length
                );
            var store = DerivedRecapStore.Open(fullPath);
            foreach (SessionArtifactSetMember member in active.Body.Members) {
                cancellationToken.ThrowIfCancellationRequested();
                DerivedRecapArtifact? artifact = await store
                    .TryReadArtifactAsync(member.ArtifactId, cancellationToken)
                    .ConfigureAwait(false);
                string? issue = ValidateArtifactMember(
                    artifact,
                    member,
                    active.Body,
                    chainPositions,
                    active.Parent
                );
                memberReports.Add(new SessionJournalOfflineArtifactMemberReport(
                    member.RoleId,
                    member.ArtifactId,
                    Available: issue is null,
                    artifact?.ArtifactKind,
                    artifact is null
                        ? null
                        : $"{artifact.Target.Carrier}/{artifact.Target.BlockKey}",
                    artifact is null
                        ? null
                        : EventAddressTextCodec.Format(artifact.AnchorRawEvent),
                    issue
                ));
            }

            bool isUsable = memberReports.All(static member => member.Available);
            artifactSetReport = new SessionJournalOfflineArtifactSetReport(
                EventAddressTextCodec.Format(active.Address),
                active.Body.PolicyId,
                active.Body.PolicyFingerprint,
                EventAddressTextCodec.Format(active.Body.CommonAnchor),
                memberReports.AsReadOnly(),
                isUsable
            );
        }

        string readiness =
            artifactSetReport?.IsUsable == true
                ? SessionJournalOfflineReadiness.ActiveCoherent
                : SessionJournalOfflineReadiness.NeedsArtifactSetCheckpoint;
        return new SessionJournalOfflineValidationReport(
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
            preparedRequestCount,
            artifactSetReport,
            readiness
        );
    }

    private static string? ValidateArtifactMember(
        DerivedRecapArtifact? artifact,
        SessionArtifactSetMember member,
        ArtifactSetCommittedBody activeSet,
        IReadOnlyDictionary<EventAddress, int> chainPositions,
        EventAddress? activationParent
    ) {
        if (artifact is null) {
            return "Exact artifact is missing or unusable.";
        }
        if (!string.Equals(
                artifact.Status,
                DerivedRecapArtifactStatus.Produced,
                StringComparison.Ordinal
            )) {
            return "Exact artifact is not produced.";
        }
        if (!string.Equals(
                artifact.ArtifactId,
                member.ArtifactId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                artifact.ArtifactKind,
                member.ArtifactKind,
                StringComparison.Ordinal
            )
            || artifact.Target != member.Target) {
            return "Exact artifact identity does not match the committed member.";
        }
        if (artifact.AnchorRawEvent != activeSet.CommonAnchor
            || artifact.SourceEndInclusive != activeSet.CommonAnchor) {
            return "Exact artifact does not share the committed common anchor.";
        }
        if (artifact.GoverningRuntimeConfigSetup
                != activeSet.CoverageSetups.RuntimeConfig.Address
            || artifact.GoverningSystemPromptSetup
                != activeSet.CoverageSetups.SystemPrompt.Address) {
            return "Exact artifact governing setup does not match the committed coverage setup.";
        }
        if (!chainPositions.TryGetValue(
                artifact.SourceRawHead,
                out int sourcePosition
            )
            || !chainPositions.TryGetValue(
                artifact.AnchorRawEvent,
                out int anchorPosition
            )
            || activationParent is not { } parent
            || !chainPositions.TryGetValue(parent, out int activationParentPosition)
            || sourcePosition < anchorPosition
            || sourcePosition > activationParentPosition) {
            return "Exact artifact source provenance is not on the current raw lineage.";
        }
        SessionRequestArtifactInput input =
            LegacyArtifactContextSnapshotFactory.CreateLegacyArtifactInput(artifact);
        if (!string.Equals(
                input.ContentSha256,
                member.ContentSha256,
                StringComparison.Ordinal
            )) {
            return "Exact artifact target contribution hash does not match the committed member.";
        }
        return null;
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

    private static void ValidateArtifactSetAndPreparedProvenance(
        IReadOnlyList<DecodedSessionEvent> chronologicalEvents,
        IReadOnlyDictionary<EventAddress, RawArtifactSetActivation> artifactSets
    ) {
        var governingSetups = new Dictionary<
            EventAddress,
            (EventAddress? RuntimeConfig, EventAddress? SystemPrompt)
        >();
        EventAddress? runtimeConfig = null;
        EventAddress? systemPrompt = null;
        RawArtifactSetActivation? activeArtifactSet = null;

        foreach (DecodedSessionEvent ev in chronologicalEvents) {
            if (ev.Kind == SessionEventKind.RuntimeConfigSetup) {
                runtimeConfig = ev.Address;
            }
            else if (ev.Kind == SessionEventKind.SystemPromptSetup) {
                systemPrompt = ev.Address;
            }
            governingSetups.Add(ev.Address, (runtimeConfig, systemPrompt));

            if (ev.Kind == SessionEventKind.ArtifactSetCommitted) {
                RawArtifactSetActivation activation = artifactSets[ev.Address];
                EventAddress parent = activation.Parent
                    ?? throw new InvalidDataException(
                        $"ArtifactSetCommitted at {activation.Address} requires an exact raw parent."
                    );
                if (!governingSetups.TryGetValue(
                        activation.Body.CommonAnchor,
                        out var coverage
                    )
                    || !governingSetups.TryGetValue(parent, out var current)
                    || coverage.RuntimeConfig is null
                    || coverage.SystemPrompt is null
                    || current.RuntimeConfig is null
                    || current.SystemPrompt is null) {
                    throw new InvalidDataException(
                        $"ArtifactSetCommitted at {activation.Address} has a common anchor or setup boundary outside the current raw lineage."
                    );
                }
                if (activation.Body.CoverageSetups.RuntimeConfig.Address
                        != coverage.RuntimeConfig
                    || activation.Body.CoverageSetups.SystemPrompt.Address
                        != coverage.SystemPrompt
                    || activation.Body.CurrentSetups.RuntimeConfig.Address
                        != current.RuntimeConfig
                    || activation.Body.CurrentSetups.SystemPrompt.Address
                        != current.SystemPrompt) {
                    throw new InvalidDataException(
                        $"ArtifactSetCommitted at {activation.Address} setup references do not match its authoritative raw boundaries."
                    );
                }
                activeArtifactSet = activation;
                continue;
            }

            // Prepared v4 is self-contained.  Its exact raw-range/setup/context assertions are
            // validated by SessionPreparedRequestReconstructor below, independently of raw kind 12.
        }
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

    private static void ValidateSetupReference(
        EventJournal.EventJournal journal,
        SessionSetupReference reference,
        SessionEventKind expectedKind
    ) {
        using EventFrame frame = journal.ReadEvent(reference.Address).Unwrap();
        ValidateHeader(reference.Address, frame.Header);
        var actualKind = (SessionEventKind)frame.Header.OpaqueEventKind;
        if (actualKind != expectedKind) {
            throw new InvalidDataException(
                $"ArtifactSetCommitted setup reference expected '{expectedKind}' at {reference.Address}, got '{actualKind}'."
            );
        }
        _ = SessionEventCodec.Decode(
            actualKind,
            frame.Payload,
            out int bodySchemaVersion
        );
        if (bodySchemaVersion != reference.BodySchemaVersion
            || !string.Equals(
                SessionRequestCanonicalizer.Sha256Hex(frame.Payload),
                reference.PayloadSha256,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                $"ArtifactSetCommitted setup reference is stale or corrupt at {reference.Address}."
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

    private sealed record RawArtifactSetActivation(
        EventAddress Address,
        EventAddress? Parent,
        ArtifactSetCommittedBody Body,
        SessionArtifactSetReference Reference
    );
}
