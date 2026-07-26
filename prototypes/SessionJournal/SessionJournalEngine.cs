using System.Collections.Immutable;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.EventJournal;
using Atelia.SessionJournal.Derived;

namespace Atelia.SessionJournal;

public sealed class SessionJournalEngine : IDisposable {
    private const string UnsupportedTailToolCallReason = "atelia.host.unsupported-tool-call";
    private const string InvalidCompletionInvocationReason =
        "atelia.host.invalid-completion-invocation";

    private static readonly EventJournalOptions DefaultJournalOptions = new() {
        PayloadCodecPolicy = EventPayloadCodecPolicy.Zlib
    };

    private readonly EventJournal.EventJournal _journal;
    private readonly SessionJournalEventReader _reader;
    private readonly RefId _mainRef;
    private readonly SessionJournalTestHooks _testHooks;
    private SessionRuntime? _runtime;
    private SessionGoverningSetup? _governingSetupCursor;
    private GoverningSetupResolutionDiagnostics _lastGoverningSetupResolutionDiagnostics;
    private SessionTailProjectionDiagnostics _lastTailProjectionDiagnostics;
    private int _fullProjectionInvocationCount;
    private bool _disposed;

    private SessionJournalEngine(
        EventJournal.EventJournal journal,
        RefId mainRef,
        SessionRuntime? runtime,
        SessionJournalTestHooks? testHooks
    ) {
        _journal = journal;
        _reader = new SessionJournalEventReader(journal);
        _mainRef = mainRef;
        _runtime = runtime;
        _testHooks = testHooks ?? new SessionJournalTestHooks();
    }

    public string Path => _journal.JournalPath;

    internal GoverningSetupResolutionDiagnostics LastGoverningSetupResolutionDiagnostics
        => _lastGoverningSetupResolutionDiagnostics;

    internal EventAddress? GoverningSetupCursorHeadForTest => _governingSetupCursor?.Head;

    internal SessionTailProjectionDiagnostics LastTailProjectionDiagnostics
        => _lastTailProjectionDiagnostics;

    internal int FullProjectionInvocationCount => _fullProjectionInvocationCount;

    internal SessionJournalReadDiagnostics CaptureReadDiagnostics() {
        SessionJournalReadDiagnostics reads = _reader.CaptureDiagnostics();
        return reads with {
            FullProjectionInvocationCount = _fullProjectionInvocationCount
        };
    }

    internal SessionJournalPayloadLifetimeDiagnostics
        CapturePayloadLifetimeDiagnostics()
        => _reader.CapturePayloadLifetimeDiagnostics();

    internal SessionExecutionRecovery ResolveExecutionTail(
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        return SessionExecutionTailResolver.Resolve(
            _reader,
            _journal.GetHead(_mainRef),
            cancellationToken
        );
    }

    internal SessionExecutionRecovery ResolveExecutionTail(
        EventAddress exactHead,
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        return SessionExecutionTailResolver.Resolve(
            _reader,
            exactHead,
            cancellationToken
        );
    }

    public static SessionJournalEngine Create(string path, SessionCreateOptions options)
        => CreateCore(path, options, runtime: null, testHooks: null);

    public static SessionJournalEngine Create(string path, SessionCreateOptions options, SessionRuntime runtime)
        => CreateCore(path, options, runtime, testHooks: null);

    internal static SessionJournalEngine CreateForTest(
        string path,
        SessionCreateOptions options,
        SessionRuntime runtime,
        SessionJournalTestHooks testHooks
    ) => CreateCore(path, options, runtime, testHooks);

    internal static SessionJournalEngine CreateForTest(
        string path,
        SessionCreateOptions options,
        SessionRuntime? runtime,
        SessionJournalTestHooks testHooks,
        EventJournalOptions journalOptions
    ) => CreateCore(path, options, runtime, testHooks, journalOptions);

    public static SessionJournalEngine Open(string path)
        => OpenCore(path, runtime: null, testHooks: null);

    public static SessionJournalEngine Open(string path, SessionRuntime runtime)
        => OpenCore(path, runtime, testHooks: null);

    internal static SessionJournalEngine OpenForTest(
        string path,
        SessionRuntime runtime,
        SessionJournalTestHooks testHooks
    ) => OpenCore(path, runtime, testHooks);

    internal static SessionJournalEngine OpenForTest(
        string path,
        SessionRuntime? runtime,
        SessionJournalTestHooks testHooks,
        EventJournalOptions journalOptions
    ) => OpenCore(path, runtime, testHooks, journalOptions);

    public void UseRuntime(SessionRuntime runtime) {
        ThrowIfDisposed();
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public SessionProjection Project(CancellationToken cancellationToken = default) {
        ThrowIfDisposed();
        _fullProjectionInvocationCount++;
        EventAddress? head = _journal.GetHead(_mainRef);
        if (head is null) { return SessionReducer.Empty; }

        return SessionReducer.Reduce(ReadDecodedChronologicalEvents(head.Value, cancellationToken));
    }

    public SessionHistoryReplay ReplayHistory(CancellationToken cancellationToken = default) {
        ThrowIfDisposed();
        EventAddress? head = _journal.GetHead(_mainRef);
        if (head is null) { return SessionHistoryReplay.Empty; }

        var messages = new List<AddressedSessionHistoryMessage>();
        SessionProjection projection = SessionReducer.Reduce(
            ReadDecodedChronologicalEvents(head.Value, cancellationToken),
            messages
        );
        return new SessionHistoryReplay(
            head,
            messages.Count == 0 ? Array.AsReadOnly(Array.Empty<AddressedSessionHistoryMessage>()) : Array.AsReadOnly(messages.ToArray()),
            projection.ExecutionState
        );
    }

    public async Task<TurnResult> SendAsync(string observation, CancellationToken cancellationToken = default)
        => await SendAsync(observation, observer: null, cancellationToken).ConfigureAwait(false);

    public async Task<TurnResult> SendAsync(
        string observation,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        ValidateRequired(observation, nameof(observation));
        SessionRuntime runtime = RequireRuntime();
        ImmutableArray<ToolDefinition> visibleTools =
            runtime.ToolSession?.VisibleDefinitions ?? ImmutableArray<ToolDefinition>.Empty;
        if (!visibleTools.IsEmpty) {
            _ = RequireToolRuntimeIdentity(runtime, visibleTools);
        }
        _ = ValidateRuntimePlanningPrerequisites(runtime);

        SessionExecutionRecovery recovery = ResolveExecutionTail(
            cancellationToken
        );
        if (recovery.State.Phase is not (
            SessionExecutionPhase.Idle
            or SessionExecutionPhase.TurnFailed
        )) {
            throw new InvalidOperationException(
                $"SendAsync requires an idle or explicitly failed turn boundary. Current phase is '{recovery.State.Phase}'; call ResumeAsync first."
            );
        }
        _ = await EnsureActiveArtifactSetReadyAsync(
            recovery.Head
                ?? throw new InvalidDataException(
                    "Active artifact-set policy requires a raw session head."
                ),
            cancellationToken
        ).ConfigureAwait(false);
        EventAddress observationAddress = AppendExpected(
            SessionEventKind.ObservationAccepted,
            new ObservationAcceptedBody(observation),
            recovery.Head,
            requireBoundSetupCursor: false
        );
        TriggerFailpoint(SessionJournalFailpoint.AfterObservationCommitted);
        SessionExecutionRecovery observationRecovery = ResolveExecutionTail(
            observationAddress,
            cancellationToken
        );
        return await CompleteAwaitingAgentActionAsync(
            observationRecovery,
            observer,
            cancellationToken
        ).ConfigureAwait(false);
    }

    public async Task<ResumeOutcome> ResumeAsync(CancellationToken cancellationToken = default)
        => await ResumeAsync(observer: null, cancellationToken).ConfigureAwait(false);

    public async Task<ResumeOutcome> ResumeAsync(
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        SessionExecutionRecovery recovery = ResolveExecutionTail(
            cancellationToken
        );
        return recovery.State.Phase switch {
            SessionExecutionPhase.Empty or SessionExecutionPhase.Idle or SessionExecutionPhase.TurnFailed =>
                new ResumeOutcome(Advanced: false),
            SessionExecutionPhase.AwaitingAgentAction => ToResumeOutcome(
                await CompleteAwaitingAgentActionAsync(
                    recovery,
                    observer,
                    cancellationToken
                ).ConfigureAwait(false)
            ),
            SessionExecutionPhase.AwaitingCompletion =>
                await ResumePreparedCompletionAsync(
                    recovery,
                    observer,
                    cancellationToken
                ).ConfigureAwait(false),
            SessionExecutionPhase.AwaitingToolExecution => ToResumeOutcome(
                await ContinueToolLoopAsync(
                    recovery,
                    observer,
                    cancellationToken
                ).ConfigureAwait(false)
            ),
            _ => throw new InvalidOperationException($"Unknown SessionJournal execution phase '{recovery.State.Phase}'.")
        };
    }

    public EventAddress AppendObservation(string content) {
        ValidateRequired(content, nameof(content));
        return Append(SessionEventKind.ObservationAccepted, new ObservationAcceptedBody(content));
    }

    public async ValueTask<EventAddress> CommitArtifactSetAsync(
        IReadOnlyList<SessionArtifactSetMemberSelection> members,
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(members);
        if (members.Count < 2) {
            throw new ArgumentException(
                "An active artifact set requires at least two exact members.",
                nameof(members)
            );
        }
        SessionExecutionRecovery recovery = ResolveExecutionTail(cancellationToken);
        if (recovery.Head is not { } expectedHead
            || recovery.State.Phase != SessionExecutionPhase.Idle) {
            throw new InvalidOperationException(
                "ArtifactSetCommitted requires an exact idle SessionJournal head."
            );
        }
        var roles = new HashSet<string>(StringComparer.Ordinal);
        var artifactIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (SessionArtifactSetMemberSelection member in members) {
            ValidateRequired(member.RoleId, nameof(member.RoleId));
            ValidateRequired(member.ArtifactId, nameof(member.ArtifactId));
            if (!roles.Add(member.RoleId) || !artifactIds.Add(member.ArtifactId)) {
                throw new ArgumentException(
                    "Artifact set roles and exact artifact ids must be unique.",
                    nameof(members)
                );
            }
        }

        DerivedRecapStore store = DerivedRecapStore.Open(Path);
        var artifacts = new List<(string RoleId, DerivedRecapArtifact Artifact)>(
            members.Count
        );
        foreach (SessionArtifactSetMemberSelection member in members) {
            DerivedRecapArtifact artifact = await store
                .TryReadArtifactAsync(member.ArtifactId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    $"Exact artifact '{member.ArtifactId}' was not found or is unusable."
                );
            artifacts.Add((member.RoleId, artifact));
        }
        EventAddress commonAnchor = artifacts[0].Artifact.AnchorRawEvent;
        if (artifacts.Any(item =>
                item.Artifact.AnchorRawEvent != commonAnchor
                || item.Artifact.SourceEndInclusive != commonAnchor)) {
            throw new InvalidDataException(
                "Active artifact-set members require one common exact coverage anchor."
            );
        }
        var targets = new HashSet<(MemoryPackCarrier Carrier, string BlockKey)>();
        foreach ((_, DerivedRecapArtifact artifact) in artifacts) {
            if (!targets.Add((
                    artifact.Target.Carrier,
                    artifact.Target.BlockKey
                ))) {
                throw new InvalidDataException(
                    "Active artifact-set members require unique target blocks."
                );
            }
        }
        ValidateArtifactSetLineage(
            expectedHead,
            commonAnchor,
            artifacts.Select(static item => item.Artifact.SourceRawHead),
            cancellationToken
        );
        SessionTailContextProjection.ValidateReplaySafeBoundary(
            _reader,
            commonAnchor
        );
        SessionGoverningSetup coverageSetup =
            ResolveGoverningSetup(commonAnchor, cancellationToken);
        foreach ((_, DerivedRecapArtifact artifact) in artifacts) {
            if (artifact.GoverningRuntimeConfigSetup
                    != coverageSetup.RuntimeConfigSetupAddress
                || artifact.GoverningSystemPromptSetup
                    != coverageSetup.SystemPromptSetupAddress) {
                throw new InvalidDataException(
                    $"Artifact '{artifact.ArtifactId}' governing setup does not match the common anchor."
                );
            }
        }
        SessionGoverningSetup currentSetup = EnsureGoverningSetupCursor(
            expectedHead,
            cancellationToken
        );
        ImmutableArray<SessionArtifactSetMember> committedMembers = [
            .. artifacts
                .OrderBy(static item => item.RoleId, StringComparer.Ordinal)
                .Select(static item => {
                    SessionRequestArtifactInput input =
                        SessionTailContextProjection.CreateArtifactInput(
                            item.Artifact
                        );
                    return new SessionArtifactSetMember(
                        item.RoleId,
                        item.Artifact.ArtifactId,
                        item.Artifact.ArtifactKind,
                        item.Artifact.Target,
                        input.ContentSha256
                    );
                })
        ];
        var body = new ArtifactSetCommittedBody(
            SessionRequestManifestDefaults.ActiveArtifactSetPolicyId,
            SessionRequestManifestDefaults.ActiveArtifactSetPolicyFingerprint,
            commonAnchor,
            CreateSetupReferences(coverageSetup),
            CreateSetupReferences(currentSetup),
            committedMembers
        );
        return AppendExpected(
            SessionEventKind.ArtifactSetCommitted,
            body,
            expectedHead,
            requireBoundSetupCursor: true
        );
    }

    public EventAddress AppendRuntimeConfigSetup(SessionRuntimeConfiguration configuration) {
        ArgumentNullException.ThrowIfNull(configuration);
        ValidateRuntimeConfiguration(configuration);
        SessionExecutionRecovery recovery = ResolveExecutionTail();
        if (recovery.State.Phase is not (
            SessionExecutionPhase.Idle
            or SessionExecutionPhase.TurnFailed
        )) {
            throw new InvalidOperationException(
                $"AppendRuntimeConfigSetup requires an idle or explicitly failed turn boundary. Current phase is '{recovery.State.Phase}'."
            );
        }

        return AppendExpected(
            SessionEventKind.RuntimeConfigSetup,
            configuration,
            recovery.Head,
            requireBoundSetupCursor: false
        );
    }

    public EventAddress AppendSystemPromptSetup(string systemPrompt) {
        if (systemPrompt is null) { throw new ArgumentNullException(nameof(systemPrompt)); }
        SessionExecutionRecovery recovery = ResolveExecutionTail();
        if (recovery.State.Phase is not (
            SessionExecutionPhase.Idle
            or SessionExecutionPhase.TurnFailed
        )) {
            throw new InvalidOperationException(
                $"AppendSystemPromptSetup requires an idle or explicitly failed turn boundary. Current phase is '{recovery.State.Phase}'."
            );
        }

        return AppendExpected(
            SessionEventKind.SystemPromptSetup,
            new SystemPromptSetupBody(systemPrompt),
            recovery.Head,
            requireBoundSetupCursor: false
        );
    }

    public EventAddress AppendImportedAgentAction(ActionMessage action, CompletionDescriptor invocation) {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(invocation);
        SessionExecutionRecovery recovery = ResolveExecutionTail();
        if (recovery.State.Phase != SessionExecutionPhase.AwaitingAgentAction
            || recovery.State.HeadKind is not (SessionEventKind.ObservationAccepted or SessionEventKind.ToolResultObserved)) {
            throw new InvalidOperationException(
                "AppendImportedAgentAction requires an unprepared observation or settled tool-result completion boundary."
            );
        }
        SessionToolRuntimeIdentity? toolRuntimeIdentity = null;
        if (action.ToolCalls.Count > 0) {
            SessionRuntime runtime = RequireRuntime();
            toolRuntimeIdentity = RequireToolRuntimeIdentity(
                runtime,
                runtime.ToolSession?.VisibleDefinitions ?? ImmutableArray<ToolDefinition>.Empty
            );
        }
        return AppendExpected(
            SessionEventKind.ImportedAgentAction,
            new AgentActionProducedBody(
                action,
                invocation,
                recovery.State.ActiveCorrelationId
                    ?? throw new InvalidDataException(
                        "An imported agent action requires an active completion-boundary correlation id."
                    ),
                new SessionExecutionCheckpoint(
                    recovery.State.ToolExecutionSequenceCheckpoint
                ),
                toolRuntimeIdentity
            ),
            recovery.Head,
            requireBoundSetupCursor: false
        );
    }

    public SessionGoverningSetup ResolveGoverningSetup(EventAddress head, CancellationToken cancellationToken = default) {
        ThrowIfDisposed();
        _lastGoverningSetupResolutionDiagnostics = default;

        EventAddress? cursor = head;
        EventAddress? runtimeConfigSetupAddress = null;
        EventAddress? systemPromptSetupAddress = null;
        SessionRuntimeConfiguration? runtimeConfig = null;
        string? systemPrompt = null;
        int headerVisitCount = 0;
        int payloadReadCount = 0;
        int manifestPayloadReadCount = 0;

        while (cursor is { } address && (runtimeConfigSetupAddress is null || systemPromptSetupAddress is null)) {
            cancellationToken.ThrowIfCancellationRequested();

            EventFrameHeader header = _reader.ReadEventHeaderPreview(address).Unwrap();
            headerVisitCount++;
            ValidateSessionHeaderPreview(address, header);

            var kind = (SessionEventKind)header.OpaqueEventKind;
            if (kind == SessionEventKind.RuntimeConfigSetup && runtimeConfigSetupAddress is null) {
                runtimeConfigSetupAddress = address;
            }
            else if (kind == SessionEventKind.SystemPromptSetup && systemPromptSetupAddress is null) {
                systemPromptSetupAddress = address;
            }
            else if (kind is (
                    SessionEventKind.CompletionRequestPrepared
                    or SessionEventKind.ArtifactSetCommitted
                )
                && (runtimeConfigSetupAddress is null || systemPromptSetupAddress is null)) {
                using SessionJournalEventFrame manifestFrame = _reader.ReadEvent(address).Unwrap();
                payloadReadCount++;
                manifestPayloadReadCount++;
                object decoded = SessionEventCodec.Decode(kind, manifestFrame.Payload, out _);
                SessionGoverningSetupReferences setupReferences = decoded switch {
                    CompletionRequestPreparedBody manifest => manifest.Setups,
                    ArtifactSetCommittedBody activation => activation.CurrentSetups,
                    _ => throw new InvalidDataException(
                        $"setup checkpoint at {address} decoded to '{decoded.GetType().Name}'."
                    )
                };

                if (runtimeConfigSetupAddress is null) {
                    runtimeConfig = ReadAndValidateSetupReference<SessionRuntimeConfiguration>(
                        setupReferences.RuntimeConfig,
                        SessionEventKind.RuntimeConfigSetup,
                        ref payloadReadCount
                    );
                    runtimeConfigSetupAddress = setupReferences.RuntimeConfig.Address;
                }
                if (systemPromptSetupAddress is null) {
                    SystemPromptSetupBody prompt = ReadAndValidateSetupReference<SystemPromptSetupBody>(
                        setupReferences.SystemPrompt,
                        SessionEventKind.SystemPromptSetup,
                        ref payloadReadCount
                    );
                    systemPrompt = prompt.Content;
                    systemPromptSetupAddress = setupReferences.SystemPrompt.Address;
                }
            }

            cursor = header.Parent;
        }

        if (runtimeConfigSetupAddress is null) {
            throw new InvalidDataException($"SessionJournal governing setup for head {head} is missing runtime-config-setup on its parent chain.");
        }

        if (systemPromptSetupAddress is null) {
            throw new InvalidDataException($"SessionJournal governing setup for head {head} is missing system-prompt-setup on its parent chain.");
        }

        if (runtimeConfig is null) {
            runtimeConfig = ReadRuntimeConfigSetup(runtimeConfigSetupAddress.Value);
            payloadReadCount++;
        }
        if (systemPrompt is null) {
            systemPrompt = ReadSystemPromptSetup(systemPromptSetupAddress.Value);
            payloadReadCount++;
        }

        _lastGoverningSetupResolutionDiagnostics = new(
            headerVisitCount,
            payloadReadCount,
            manifestPayloadReadCount
        );

        return new SessionGoverningSetup(
            head,
            runtimeConfigSetupAddress.Value,
            runtimeConfig,
            systemPromptSetupAddress.Value,
            systemPrompt
        );
    }

    public byte[] ReadPayloadBytes(EventAddress address) {
        ThrowIfDisposed();
        using SessionJournalEventFrame frame = _reader.ReadEvent(address).Unwrap();
        return frame.Payload.ToArray();
    }

    public void Dispose() {
        if (_disposed) { return; }
        _journal.Dispose();
        _disposed = true;
    }

    private static SessionJournalEngine CreateCore(
        string path,
        SessionCreateOptions options,
        SessionRuntime? runtime,
        SessionJournalTestHooks? testHooks,
        EventJournalOptions? journalOptions = null
    ) {
        ArgumentNullException.ThrowIfNull(options);
        ValidateCreateOptions(options);

        var journal = EventJournal.EventJournal.CreateNew(path, journalOptions ?? DefaultJournalOptions);
        try {
            journal.CreateBranch(SessionJournalDefaults.MainBranchName, startPoint: null).Unwrap();
            RefId mainRef = journal.OpenBranch(SessionJournalDefaults.MainBranchName).Unwrap();
            var engine = new SessionJournalEngine(journal, mainRef, runtime, testHooks);
            SessionRuntimeConfiguration runtimeConfig = options.ToRuntimeConfiguration();
            EventAddress runtimeAddress = engine.Append(SessionEventKind.RuntimeConfigSetup, runtimeConfig);
            EventAddress promptAddress = engine.Append(SessionEventKind.SystemPromptSetup, new SystemPromptSetupBody(options.SystemPrompt));
            EventAddress createdAddress = engine.Append(SessionEventKind.SessionCreated, new SessionCreatedBody());
            engine._governingSetupCursor = new SessionGoverningSetup(
                createdAddress,
                runtimeAddress,
                runtimeConfig,
                promptAddress,
                options.SystemPrompt
            );
            return engine;
        }
        catch {
            journal.Dispose();
            throw;
        }
    }

    private static SessionJournalEngine OpenCore(
        string path,
        SessionRuntime? runtime,
        SessionJournalTestHooks? testHooks,
        EventJournalOptions? journalOptions = null
    ) {
        var journal = EventJournal.EventJournal.OpenExisting(path, journalOptions ?? DefaultJournalOptions);
        try {
            RefId mainRef = journal.OpenBranch(SessionJournalDefaults.MainBranchName).Unwrap();
            return new SessionJournalEngine(journal, mainRef, runtime, testHooks);
        }
        catch {
            journal.Dispose();
            throw;
        }
    }

    private IReadOnlyList<DecodedSessionEvent> ReadDecodedChronologicalEvents(
        EventAddress head,
        CancellationToken cancellationToken
    ) {
        IReadOnlyList<EventAddress> chain = _reader.ReadChronologicalChain(head, checkedRead: true, cancellationToken: cancellationToken).Unwrap();
        var events = new List<DecodedSessionEvent>(chain.Count);
        foreach (EventAddress address in chain) {
            cancellationToken.ThrowIfCancellationRequested();
            using SessionJournalEventFrame frame = _reader.ReadEvent(address).Unwrap();
            ValidateSessionHeaderPreview(address, frame.Header);

            var kind = (SessionEventKind)frame.Header.OpaqueEventKind;
            object body = SessionEventCodec.Decode(kind, frame.Payload, out int version);
            events.Add(new DecodedSessionEvent(kind, version, body, address, frame.Header.Parent));
        }

        return events;
    }

    private async Task<TurnResult> CompleteAwaitingAgentActionAsync(
        SessionExecutionRecovery recovery,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken
    ) {
        if (recovery.Head is null
            || recovery.State.Phase !=
                SessionExecutionPhase.AwaitingAgentAction) {
            throw new InvalidOperationException(
                $"Completion requires an exact '{SessionExecutionPhase.AwaitingAgentAction}' recovery boundary."
            );
        }
        SessionRuntime runtime = RequireRuntime();
        return await CompleteArtifactTailAsync(
            runtime,
            recovery,
            observer,
            cancellationToken
        ).ConfigureAwait(false);
    }

    private async Task<TurnResult> CompleteArtifactTailAsync(
        SessionRuntime runtime,
        SessionExecutionRecovery recovery,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken
    ) {
        EventAddress completionBoundary = recovery.Head
            ?? throw new InvalidDataException(
                "Artifact-tail completion requires an exact completion boundary."
            );
        if (recovery.State.Phase !=
                SessionExecutionPhase.AwaitingAgentAction
            || recovery.State.HeadKind is not (
                SessionEventKind.ObservationAccepted
                or SessionEventKind.ToolResultObserved
            )) {
            throw new InvalidOperationException(
                "Artifact-tail completion requires ObservationAccepted or a dependency-closed ToolResultObserved boundary."
            );
        }
        ImmutableArray<ToolDefinition> tools =
            runtime.ToolSession?.VisibleDefinitions ?? ImmutableArray<ToolDefinition>.Empty;
        if (!tools.IsEmpty) {
            _ = RequireToolRuntimeIdentity(runtime, tools);
        }
        _lastTailProjectionDiagnostics = default;
        SessionCompletionTargetIdentity completionTarget =
            ValidateRuntimePlanningPrerequisites(runtime);
        SessionGoverningSetup governingSetup = EnsureGoverningSetupCursor(
            completionBoundary,
            cancellationToken
        );
        ReadyActiveArtifactSet readyArtifactSet =
            await EnsureActiveArtifactSetReadyAsync(
            completionBoundary,
            cancellationToken
        ).ConfigureAwait(false);
        SessionActiveArtifactSet activeArtifactSet = readyArtifactSet.Active;
        SessionTailContextProjectionResult tail = SessionTailContextProjection.Materialize(
            _reader,
            completionBoundary,
            governingSetup,
            ReadSetupFromReferences(
                activeArtifactSet.Body.CommonAnchor,
                activeArtifactSet.Body.CoverageSetups
            ),
            readyArtifactSet.Artifacts,
            cancellationToken
        );
        _lastTailProjectionDiagnostics = tail.Diagnostics;
        var materialization = new RequestContextMaterialization(
            tail.SystemPrompt,
            tail.Context,
            tail.RawStartExclusive,
            tail.RawRangeSha256,
            tail.ArtifactInputs,
            activeArtifactSet.Reference
        );
        var request = new CompletionRequest(
            ModelId: governingSetup.RuntimeConfig.ModelId,
            SystemPrompt: materialization.SystemPrompt,
            Context: materialization.Context,
            Tools: tools,
            MaxTokens: runtime.MaxTokens
        );

        CommittedCompletionResult committed =
            await ExecutePreparedCompletionAsync(
            request,
            completionBoundary,
            governingSetup,
            completionTarget,
            runtime,
            tools,
            materialization,
            recovery.State.ActiveCorrelationId
                ?? throw new InvalidDataException(
                    "Artifact-tail completion requires an active correlation id."
                ),
            reason: recovery.State.HeadKind == SessionEventKind.ToolResultObserved
                ? "tool-continuation"
                : "observation",
            new SessionExecutionCheckpoint(
                recovery.State.ToolExecutionSequenceCheckpoint
            ),
            allowResultToolCalls: !tools.IsEmpty,
            observer,
            cancellationToken
        ).ConfigureAwait(false);
        SessionExecutionRecovery actionRecovery = ResolveExecutionTail(
            committed.ActionAddress,
            cancellationToken
        );
        if (actionRecovery.State.Phase == SessionExecutionPhase.AwaitingToolExecution) {
            return await ContinueToolLoopAsync(
                actionRecovery,
                observer,
                cancellationToken
            ).ConfigureAwait(false);
        }
        if (actionRecovery.State.Phase != SessionExecutionPhase.Idle) {
            throw new InvalidDataException(
                $"Artifact-tail terminal Action resolved to unexpected phase '{actionRecovery.State.Phase}'."
            );
        }
        return new TurnResult(
            committed.Result.Message,
            committed.Result.Invocation,
            FreezeErrors(committed.Result.Errors)
        );
    }

    private async Task<CommittedCompletionResult> ExecutePreparedCompletionAsync(
        CompletionRequest request,
        EventAddress expectedParent,
        SessionGoverningSetup governingSetup,
        SessionCompletionTargetIdentity completionTarget,
        SessionRuntime runtime,
        ImmutableArray<ToolDefinition> tools,
        RequestContextMaterialization materialization,
        string correlationId,
        string reason,
        SessionExecutionCheckpoint executionCheckpoint,
        bool allowResultToolCalls,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken
    ) {
        CompletionRequestPreparedBody manifest = BuildRequestManifest(
            request,
            expectedParent,
            governingSetup,
            completionTarget,
            runtime,
            tools,
            materialization,
            correlationId,
            reason,
            executionCheckpoint,
            cancellationToken
        );
        EventAddress preparedAddress = AppendExpected(
            SessionEventKind.CompletionRequestPrepared,
            manifest,
            expectedParent,
            requireBoundSetupCursor: true
        );
        TriggerFailpoint(SessionJournalFailpoint.AfterRequestPreparedCommitted);

        return await ExecuteCommittedCompletionAttemptAsync(
            request,
            preparedAddress,
            manifest.Attempt.AttemptId,
            manifest,
            runtime,
            allowResultToolCalls,
            observer,
            cancellationToken
        ).ConfigureAwait(false);
    }

    private async Task<CommittedCompletionResult> ExecuteCommittedCompletionAttemptAsync(
        CompletionRequest request,
        EventAddress activeAttemptAddress,
        string activeAttemptId,
        CompletionRequestPreparedBody manifest,
        SessionRuntime runtime,
        bool allowResultToolCalls,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken
    ) {
        CompletionResult result = await runtime.CompletionClient
            .StreamCompletionAsync(request, observer, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Termination.IsSuccess) {
            IReadOnlyList<string> frozenErrors = FreezeErrors(result.Errors)
                ?? Array.AsReadOnly(Array.Empty<string>());
            AppendExpected(
                SessionEventKind.CompletionAttemptFailed,
                new CompletionAttemptFailedBody(
                    activeAttemptId,
                    result.Termination.Kind,
                    result.Termination.ProviderReason,
                    result.Termination.Detail,
                    frozenErrors
                ),
                activeAttemptAddress,
                requireBoundSetupCursor: false
            );
            throw new SessionJournalTurnAbortedException(
                BuildTurnAbortMessage(result.Termination),
                result.Termination,
                frozenErrors
            );
        }

        string? invocationMismatch = GetCompletionInvocationMismatch(
            result.Invocation,
            runtime.CompletionClient,
            request
        );
        if (invocationMismatch is not null) {
            ThrowKnownHostFailure(
                activeAttemptAddress,
                activeAttemptId,
                InvalidCompletionInvocationReason,
                invocationMismatch
            );
        }
        if (!allowResultToolCalls && result.Message.ToolCalls.Count > 0) {
            const string detail =
                "Provider returned tool calls for a request whose durable policy supports no tools.";
            ThrowKnownHostFailure(
                activeAttemptAddress,
                activeAttemptId,
                UnsupportedTailToolCallReason,
                detail
            );
        }
        TriggerFailpoint(SessionJournalFailpoint.AfterCompletionBeforeActionCommitted);
        EventAddress actionAddress = AppendExpected(
            SessionEventKind.AgentActionProduced,
            new AgentActionProducedBody(
                result.Message,
                result.Invocation,
                manifest.Attempt.CorrelationId,
                manifest.Execution,
                result.Message.ToolCalls.Count == 0
                    ? null
                    : manifest.ToolSet.RuntimeIdentity
                        ?? throw new InvalidDataException(
                            "A prepared result containing tool calls requires a durable tool runtime identity."
                        )
            ),
            activeAttemptAddress,
            requireBoundSetupCursor: false
        );
        TriggerFailpoint(SessionJournalFailpoint.AfterActionCommitted);
        return new CommittedCompletionResult(result, actionAddress);
    }

    private void ThrowKnownHostFailure(
        EventAddress activeAttemptAddress,
        string activeAttemptId,
        string reason,
        string detail
    ) {
        CompletionTermination hostFailure = CompletionTermination.Failed(reason, detail);
        IReadOnlyList<string> errors = Array.AsReadOnly([detail]);
        AppendExpected(
            SessionEventKind.CompletionAttemptFailed,
            new CompletionAttemptFailedBody(
                activeAttemptId,
                hostFailure.Kind,
                hostFailure.ProviderReason,
                hostFailure.Detail,
                errors
            ),
            activeAttemptAddress,
            requireBoundSetupCursor: false
        );
        throw new SessionJournalTurnAbortedException(
            BuildTurnAbortMessage(hostFailure),
            hostFailure,
            errors
        );
    }

    private async Task<ResumeOutcome> ResumePreparedCompletionAsync(
        SessionExecutionRecovery recovery,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken
    ) {
        if (recovery.State.Phase !=
                SessionExecutionPhase.AwaitingCompletion
            || recovery.Boundary.SourcePrepared is not {
            } sourcePreparedAddress
            || recovery.State.ActiveCompletionAttemptAddress is not {
            } activeAttemptAddress
            || recovery.State.PendingCompletionAttemptId is not {
            } activeAttemptId
            || recovery.State.PendingRequestPreparedAddress !=
                sourcePreparedAddress
            || recovery.Head != activeAttemptAddress) {
            throw new InvalidDataException(
                "Prepared recovery is missing its exact durable attempt boundary."
            );
        }
        SessionPreparedCompletionRecoveryPolicy policy =
            _runtime?.PreparedCompletionRecoveryPolicy
            ?? SessionPreparedCompletionRecoveryPolicy.RefuseUncertain;
        if (policy == SessionPreparedCompletionRecoveryPolicy.RefuseUncertain) {
            throw new InvalidOperationException(
                "The current completion attempt has an uncertain outcome. "
                + "Recovery policy RefuseUncertain does not call the provider or mutate the journal."
            );
        }
        if (policy != SessionPreparedCompletionRecoveryPolicy.RestartWithNewAttempt) {
            throw new NotSupportedException(
                $"Unsupported prepared completion recovery policy '{policy}'."
            );
        }

        SessionRuntime runtime = RequireRuntime();
        SessionPreparedRequestReconstruction reconstruction =
            SessionPreparedRequestReconstructor.Reconstruct(
                _reader,
                sourcePreparedAddress,
                cancellationToken
            );
        CompletionRequestPreparedBody manifest =
            reconstruction.Manifest;
        if (!string.Equals(
                manifest.Attempt.CorrelationId,
                recovery.State.ActiveCorrelationId,
                StringComparison.Ordinal
            )
            || manifest.Execution.LastIssuedToolExecutionSequence !=
                recovery.State.ToolExecutionSequenceCheckpoint) {
            throw new InvalidDataException(
                "Prepared reconstruction does not match the resolved execution checkpoint."
            );
        }
        ValidateRecoveryRuntimeCompatibility(runtime, manifest);
        string restartedAttemptId = $"attempt-{Guid.NewGuid():N}";
        EventAddress restartedAddress = AppendExpected(
            SessionEventKind.CompletionAttemptRestarted,
            new CompletionAttemptRestartedBody(
                restartedAttemptId,
                activeAttemptId,
                sourcePreparedAddress
            ),
            activeAttemptAddress,
            requireBoundSetupCursor: false
        );
        TriggerFailpoint(SessionJournalFailpoint.AfterCompletionAttemptRestartedCommitted);

        bool sourceAllowsToolCalls =
            !manifest.ToolSet.Definitions.IsEmpty;
        CommittedCompletionResult committed =
            await ExecuteCommittedCompletionAttemptAsync(
            reconstruction.Request,
            restartedAddress,
            restartedAttemptId,
            manifest,
            runtime,
            sourceAllowsToolCalls,
            observer,
            cancellationToken
        ).ConfigureAwait(false);

        SessionExecutionRecovery actionRecovery = ResolveExecutionTail(
            committed.ActionAddress,
            cancellationToken
        );
        if (actionRecovery.State.Phase ==
            SessionExecutionPhase.AwaitingToolExecution) {
            return ToResumeOutcome(
                await ContinueToolLoopAsync(
                    actionRecovery,
                    observer,
                    cancellationToken
                ).ConfigureAwait(false)
            );
        }
        if (actionRecovery.State.Phase != SessionExecutionPhase.Idle) {
            throw new InvalidDataException(
                $"Recovered terminal Action resolved to unexpected phase '{actionRecovery.State.Phase}'."
            );
        }
        return ToResumeOutcome(new TurnResult(
            committed.Result.Message,
            committed.Result.Invocation,
            FreezeErrors(committed.Result.Errors)
        ));
    }

    private static void ValidateRecoveryRuntimeCompatibility(
        SessionRuntime runtime,
        CompletionRequestPreparedBody manifest
    ) {
        SessionCompletionTargetIdentity completionTarget = runtime.CompletionTarget
            ?? throw new InvalidOperationException(
                "Prepared completion recovery requires the exact durable CompletionTarget identity."
            );
        ImmutableArray<ToolDefinition> visibleTools =
            runtime.ToolSession?.VisibleDefinitions ?? ImmutableArray<ToolDefinition>.Empty;
        SessionToolRuntimeIdentity? currentToolRuntimeIdentity = visibleTools.IsEmpty
            ? null
            : runtime.ToolRuntimeIdentity;
        if (completionTarget != manifest.Target.Connection
            || !string.Equals(
                runtime.CompletionClient.Name,
                manifest.Target.ClientName,
                StringComparison.Ordinal
            )
            || !string.Equals(
                runtime.CompletionClient.ApiSpecId,
                manifest.Target.ApiSpecId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                SessionRequestCanonicalizer.ComputeToolSetSha256(visibleTools),
                manifest.ToolSet.Sha256,
                StringComparison.Ordinal
            )
            || currentToolRuntimeIdentity != manifest.ToolSet.RuntimeIdentity) {
            throw new InvalidOperationException(
                "Current runtime dispatch identity or visible tool definitions do not exactly match the prepared manifest."
            );
        }
    }

    private async Task<TurnResult> ContinueToolLoopAsync(
        SessionExecutionRecovery recovery,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken
    ) {
        if (recovery.Head is null
            || recovery.State.Phase !=
                SessionExecutionPhase.AwaitingToolExecution) {
            throw new InvalidDataException(
                "Tool continuation requires an exact AwaitingToolExecution recovery boundary."
            );
        }
        SessionRuntime runtime = RequireRuntime();
        ToolSession toolSession = RequireToolSession(runtime);
        if (recovery.State.PendingToolCall is null) { throw new InvalidDataException("AwaitingToolExecution requires a pending tool call."); }
        SessionToolRuntimeIdentity expectedToolRuntimeIdentity =
            recovery.State.PendingToolRuntimeIdentity
                ?? throw new InvalidDataException(
                    "AwaitingToolExecution requires a durable pending tool runtime identity."
                );
        if (runtime.ToolRuntimeIdentity != expectedToolRuntimeIdentity) {
            throw new InvalidOperationException(
                "Current tool runtime implementation/capability identity does not match the durable pending Action."
            );
        }

        RawToolCall toolCall = recovery.State.PendingToolCall;
        long reservedExecutionSequence =
            recovery.State.ToolExecutionSequenceCheckpoint;
        if (!recovery.State.PendingToolExecutionStarted) {
            string operationId = recovery.State.PendingOperationId
                ?? BuildOperationId(recovery.Head, toolCall);
            reservedExecutionSequence = checked(reservedExecutionSequence + 1);
            EventAddress startedAddress = AppendToolExecutionStarted(
                toolCall,
                operationId,
                reservedExecutionSequence,
                expectedToolRuntimeIdentity,
                recovery.Head
            );
            TriggerFailpoint(SessionJournalFailpoint.AfterToolStartedCommitted);
            recovery = ResolveExecutionTail(
                startedAddress,
                cancellationToken
            );
        }
        if (!recovery.State.PendingToolExecutionStarted
            || recovery.State.PendingOperationId is null
            || recovery.State.ToolExecutionSequenceCheckpoint !=
                reservedExecutionSequence) {
            throw new InvalidDataException(
                "Resolved tool execution does not preserve its durable Started reservation."
            );
        }

        EnsureCurrentHead(recovery.Head);
        ToolCallExecutionResult executionResult = await toolSession.ExecuteReservedAsync(
            toolCall,
            reservedExecutionSequence,
            recovery.State.PendingOperationId,
            cancellationToken
        ).ConfigureAwait(false);
        TriggerFailpoint(SessionJournalFailpoint.AfterToolExecutionBeforeResultCommitted);
        EventAddress resultAddress = AppendToolResultObserved(
            executionResult,
            reservedExecutionSequence,
            recovery.Head
        );
        TriggerFailpoint(SessionJournalFailpoint.AfterToolResultCommitted);

        SessionExecutionRecovery refreshed = ResolveExecutionTail(
            resultAddress,
            cancellationToken
        );
        return refreshed.State.Phase switch {
            SessionExecutionPhase.AwaitingToolExecution =>
                await ContinueToolLoopAsync(
                    refreshed,
                    observer,
                    cancellationToken
                ).ConfigureAwait(false),
            SessionExecutionPhase.AwaitingAgentAction =>
                await CompleteAwaitingAgentActionAsync(
                    refreshed,
                    observer,
                    cancellationToken
                ).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Tool loop cannot continue from phase '{refreshed.State.Phase}'.")
        };
    }

    private EventAddress AppendToolExecutionStarted(
        RawToolCall call,
        string operationId,
        long executionSequence,
        SessionToolRuntimeIdentity toolRuntimeIdentity,
        EventAddress? expectedHead
    ) {
        ArgumentNullException.ThrowIfNull(call);
        ValidateRequired(call.ToolCallId, nameof(call.ToolCallId));
        ValidateRequired(call.ToolName, nameof(call.ToolName));
        ValidateRequired(call.RawArgumentsJson, nameof(call.RawArgumentsJson));
        ValidateRequired(operationId, nameof(operationId));
        ArgumentNullException.ThrowIfNull(toolRuntimeIdentity);
        return AppendExpected(
            SessionEventKind.ToolExecutionStarted,
            new ToolExecutionStartedBody(
                call.ToolCallId,
                call.ToolName,
                call.RawArgumentsJson,
                operationId,
                executionSequence,
                toolRuntimeIdentity
            ),
            expectedHead,
            requireBoundSetupCursor: false
        );
    }

    private EventAddress AppendToolResultObserved(
        ToolCallExecutionResult result,
        long executionSequence,
        EventAddress? expectedHead
    ) {
        ArgumentNullException.ThrowIfNull(result);
        return AppendExpected(
            SessionEventKind.ToolResultObserved,
            new ToolResultObservedBody(
                result.ToolCallId,
                result.ToolName,
                executionSequence,
                result.ExecuteResult.Status,
                result.ExecuteResult.Blocks
            ),
            expectedHead,
            requireBoundSetupCursor: false
        );
    }

    private void EnsureCurrentHead(EventAddress? expectedHead) {
        EventAddress? observedHead = _journal.GetHead(_mainRef);
        if (observedHead != expectedHead) {
            throw new InvalidOperationException(
                $"Tool execution recovery is stale. Expected current head '{expectedHead}', observed '{observedHead}'."
            );
        }
    }

    private SessionGoverningSetup EnsureGoverningSetupCursor(
        EventAddress expectedHead,
        CancellationToken cancellationToken
    ) {
        if (_governingSetupCursor is { } cursor && cursor.Head == expectedHead) {
            return cursor;
        }

        EventAddress? observedHead = _journal.GetHead(_mainRef);
        if (observedHead != expectedHead) {
            _governingSetupCursor = null;
            throw new InvalidOperationException(
                $"Cannot bind governing setup cursor: expected head '{expectedHead}', observed '{observedHead}'."
            );
        }

        cursor = ResolveGoverningSetup(expectedHead, cancellationToken);
        _governingSetupCursor = cursor;
        return cursor;
    }

    private CompletionRequestPreparedBody BuildRequestManifest(
        CompletionRequest request,
        EventAddress authoritativeRawEndInclusive,
        SessionGoverningSetup governingSetup,
        SessionCompletionTargetIdentity completionTarget,
        SessionRuntime runtime,
        ImmutableArray<ToolDefinition> tools,
        RequestContextMaterialization materialization,
        string correlationId,
        string reason,
        SessionExecutionCheckpoint executionCheckpoint,
        CancellationToken cancellationToken
    ) {
        ValidateRequired(correlationId, nameof(correlationId));
        ValidateRequired(reason, nameof(reason));
        ArgumentNullException.ThrowIfNull(executionCheckpoint);
        SessionRequestCommitment commitment = SessionRequestCanonicalizer.CreateCommitment(request);
        var manifest = new CompletionRequestPreparedBody(
            new SessionRequestAttempt(
                $"attempt-{Guid.NewGuid():N}",
                correlationId,
                reason
            ),
            executionCheckpoint,
            new SessionContextPlan(
                RawStartExclusive: materialization.RawStartExclusive,
                RawRangeSha256: materialization.RawRangeSha256,
                ArtifactInputs: materialization.ArtifactInputs,
                ActiveArtifactSet: materialization.ActiveArtifactSet
            ),
            new SessionGoverningSetupReferences(
                CreateSetupReference(governingSetup.RuntimeConfigSetupAddress, SessionEventKind.RuntimeConfigSetup),
                CreateSetupReference(governingSetup.SystemPromptSetupAddress, SessionEventKind.SystemPromptSetup)
            ),
            new SessionRequestParameters(request.ModelId, request.MaxTokens),
            new SessionRequestToolSet(
                SessionRequestManifestDefaults.ToolCodecId,
                SessionRequestCanonicalizer.ComputeToolSetSha256(tools),
                tools,
                tools.IsEmpty ? null : RequireToolRuntimeIdentity(runtime, tools)
            ),
            new SessionRequestRecipe(
                RecipeId: SessionRequestManifestDefaults.RecipeId,
                CanonicalRequestCodecId: SessionRequestManifestDefaults.CanonicalRequestCodecId
            ),
            new SessionRequestTarget(
                completionTarget,
                runtime.CompletionClient.Name,
                runtime.CompletionClient.ApiSpecId
            ),
            commitment
        );

        SessionPreparedRequestReconstruction reconstructed =
            SessionPreparedRequestReconstructor.Reconstruct(
                _reader,
                manifest,
                authoritativeRawEndInclusive,
                cancellationToken
            );
        byte[] originalCanonicalBytes = SessionRequestCanonicalizer.Canonicalize(request);
        if (!originalCanonicalBytes.AsSpan().SequenceEqual(reconstructed.CanonicalBytes)) {
            throw new InvalidDataException(
                "Prepared manifest reconstruction does not exactly match the original canonical request bytes."
            );
        }
        return manifest;
    }

    private SessionSetupReference CreateSetupReference(EventAddress address, SessionEventKind expectedKind) {
        using SessionJournalEventFrame frame = _reader.ReadEvent(address).Unwrap();
        ValidateSessionHeaderPreview(address, frame.Header);
        var actualKind = (SessionEventKind)frame.Header.OpaqueEventKind;
        if (actualKind != expectedKind) {
            throw new InvalidDataException($"Expected '{expectedKind}' setup at {address}, got '{actualKind}'.");
        }
        _ = SessionEventCodec.Decode(actualKind, frame.Payload, out int bodySchemaVersion);
        return new SessionSetupReference(
            address,
            bodySchemaVersion,
            SessionRequestCanonicalizer.Sha256Hex(frame.Payload)
        );
    }

    private SessionGoverningSetupReferences CreateSetupReferences(
        SessionGoverningSetup setup
    ) => new(
        CreateSetupReference(
            setup.RuntimeConfigSetupAddress,
            SessionEventKind.RuntimeConfigSetup
        ),
        CreateSetupReference(
            setup.SystemPromptSetupAddress,
            SessionEventKind.SystemPromptSetup
        )
    );

    private SessionGoverningSetup ReadSetupFromReferences(
        EventAddress head,
        SessionGoverningSetupReferences references
    ) {
        int payloadReads = 0;
        SessionRuntimeConfiguration runtime =
            ReadAndValidateSetupReference<SessionRuntimeConfiguration>(
                references.RuntimeConfig,
                SessionEventKind.RuntimeConfigSetup,
                ref payloadReads
            );
        SystemPromptSetupBody prompt =
            ReadAndValidateSetupReference<SystemPromptSetupBody>(
                references.SystemPrompt,
                SessionEventKind.SystemPromptSetup,
                ref payloadReads
            );
        return new SessionGoverningSetup(
            head,
            references.RuntimeConfig.Address,
            runtime,
            references.SystemPrompt.Address,
            prompt.Content
        );
    }

    private void ValidateArtifactSetLineage(
        EventAddress currentHead,
        EventAddress commonAnchor,
        IEnumerable<EventAddress> sourceHeads,
        CancellationToken cancellationToken
    ) {
        var unseen = new HashSet<EventAddress>(sourceHeads);
        EventAddress? cursor = currentHead;
        while (cursor is { } address) {
            cancellationToken.ThrowIfCancellationRequested();
            unseen.Remove(address);
            EventFrameHeader header = _reader.ReadEventHeaderPreview(address).Unwrap();
            ValidateSessionHeaderPreview(address, header);
            if (address == commonAnchor) {
                if (unseen.Count != 0) {
                    throw new InvalidDataException(
                        "At least one artifact sourceRawHead is off the current lineage or before the common anchor."
                    );
                }
                return;
            }
            cursor = header.Parent;
        }
        throw new InvalidDataException(
            "Artifact-set common anchor is not on the current lineage."
        );
    }

    private SessionActiveArtifactSet ResolveActiveArtifactSet(
        EventAddress completionBoundary,
        CancellationToken cancellationToken
    ) {
        EventAddress? cursor = completionBoundary;
        while (cursor is { } address) {
            cancellationToken.ThrowIfCancellationRequested();
            EventFrameHeader header =
                _reader.ReadEventHeaderPreview(address).Unwrap();
            ValidateSessionHeaderPreview(address, header);
            var kind = (SessionEventKind)header.OpaqueEventKind;
            if (kind == SessionEventKind.ArtifactSetCommitted) {
                return ReadActiveArtifactSet(address, expectedReference: null);
            }
            if (kind == SessionEventKind.CompletionRequestPrepared) {
                using SessionJournalEventFrame frame = _reader.ReadEvent(address).Unwrap();
                var manifest = (CompletionRequestPreparedBody)SessionEventCodec.Decode(
                    kind,
                    frame.Payload,
                    out _
                );
                SessionArtifactSetReference reference =
                    manifest.Plan.ActiveArtifactSet;
                SessionActiveArtifactSet resolved =
                    ReadActiveArtifactSet(reference.Address, reference);
                ValidateManifestArtifactSetAssertion(manifest, resolved.Body);
                return resolved;
            }
            if (kind == SessionEventKind.SessionCreated) {
                break;
            }
            cursor = header.Parent;
        }
        throw new SessionJournalNotReadyException(
            SessionJournalNotReadyReason.ActiveArtifactSetRequired,
            "Online completion requires a durable ArtifactSetCommitted ancestor on the current lineage."
        );
    }

    private async ValueTask<ReadyActiveArtifactSet> EnsureActiveArtifactSetReadyAsync(
        EventAddress completionBoundary,
        CancellationToken cancellationToken
    ) {
        SessionActiveArtifactSet active =
            ResolveActiveArtifactSet(completionBoundary, cancellationToken);
        ValidateActiveArtifactSetRawLineage(
            completionBoundary,
            active,
            cancellationToken
        );
        _ = ReadSetupFromReferences(
            active.Body.CommonAnchor,
            active.Body.CoverageSetups
        );
        HashSet<EventAddress> allowedSourceHeads =
            CollectArtifactCoverageIntervalAndValidateCurrentSetups(
                active,
                cancellationToken
            );
        DerivedRecapStore store = DerivedRecapStore.Open(Path);
        var artifacts =
            ImmutableArray.CreateBuilder<DerivedRecapArtifact>(
                active.Body.Members.Length
            );
        foreach (SessionArtifactSetMember member in active.Body.Members) {
            cancellationToken.ThrowIfCancellationRequested();
            DerivedRecapArtifact artifact = await store
                .TryReadArtifactAsync(member.ArtifactId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new SessionJournalNotReadyException(
                    SessionJournalNotReadyReason.ArtifactSetMemberUnavailable,
                    $"Active artifact-set member '{member.ArtifactId}' is missing or unusable.",
                    member.ArtifactId
                );

            SessionRequestArtifactInput contribution;
            try {
                contribution =
                    SessionTailContextProjection.CreateArtifactInput(artifact);
            }
            catch (InvalidDataException ex) {
                throw new SessionJournalNotReadyException(
                    SessionJournalNotReadyReason.ArtifactSetMemberMismatch,
                    $"Active artifact-set member '{member.ArtifactId}' cannot produce its committed context contribution: {ex.Message}",
                    member.ArtifactId
                );
            }

            bool exactIdentityMatches =
                string.Equals(
                    artifact.ArtifactId,
                    member.ArtifactId,
                    StringComparison.Ordinal
                )
                && string.Equals(
                    artifact.ArtifactKind,
                    member.ArtifactKind,
                    StringComparison.Ordinal
                )
                && artifact.Target == member.Target
                && artifact.AnchorRawEvent == active.Body.CommonAnchor
                && artifact.SourceEndInclusive == active.Body.CommonAnchor
                && artifact.GoverningRuntimeConfigSetup
                    == active.Body.CoverageSetups.RuntimeConfig.Address
                && artifact.GoverningSystemPromptSetup
                    == active.Body.CoverageSetups.SystemPrompt.Address
                && string.Equals(
                    contribution.ContentSha256,
                    member.ContentSha256,
                    StringComparison.Ordinal
                );
            if (!exactIdentityMatches) {
                throw new SessionJournalNotReadyException(
                    SessionJournalNotReadyReason.ArtifactSetMemberMismatch,
                    $"Active artifact-set member '{member.ArtifactId}' does not match its committed identity, coverage, or context contribution.",
                    member.ArtifactId
                );
            }
            ValidateArtifactSourceHead(allowedSourceHeads, artifact);
            artifacts.Add(artifact);
        }
        ImmutableArray<DerivedRecapArtifact> readyArtifacts =
            artifacts.MoveToImmutable();
        SessionTailContextProjection.ValidateReplaySafeBoundary(
            _reader,
            active.Body.CommonAnchor
        );
        return new ReadyActiveArtifactSet(active, readyArtifacts);
    }

    private void ValidateActiveArtifactSetRawLineage(
        EventAddress completionBoundary,
        SessionActiveArtifactSet active,
        CancellationToken cancellationToken
    ) {
        EventAddress? cursor = completionBoundary;
        while (cursor is { } address && address != active.Address) {
            cancellationToken.ThrowIfCancellationRequested();
            EventFrameHeader header =
                _reader.ReadEventHeaderPreview(address).Unwrap();
            ValidateSessionHeaderPreview(address, header);
            cursor = header.Parent;
        }
        if (cursor != active.Address) {
            throw new InvalidDataException(
                "Referenced active ArtifactSetCommitted is not on the current completion-boundary lineage."
            );
        }
    }

    private HashSet<EventAddress>
        CollectArtifactCoverageIntervalAndValidateCurrentSetups(
        SessionActiveArtifactSet active,
        CancellationToken cancellationToken
    ) {
        var allowedSourceHeads = new HashSet<EventAddress>();
        SessionSetupReference? runtimeOverride = null;
        SessionSetupReference? promptOverride = null;
        EventAddress? cursor = active.Parent;
        while (cursor is { } address) {
            cancellationToken.ThrowIfCancellationRequested();
            EventFrameHeader header =
                _reader.ReadEventHeaderPreview(address).Unwrap();
            ValidateSessionHeaderPreview(address, header);
            var kind = (SessionEventKind)header.OpaqueEventKind;
            if (kind == SessionEventKind.RuntimeConfigSetup
                && runtimeOverride is null) {
                runtimeOverride = CreateSetupReference(
                    address,
                    SessionEventKind.RuntimeConfigSetup
                );
            }
            else if (kind == SessionEventKind.SystemPromptSetup
                && promptOverride is null) {
                promptOverride = CreateSetupReference(
                    address,
                    SessionEventKind.SystemPromptSetup
                );
            }
            allowedSourceHeads.Add(address);
            if (address == active.Body.CommonAnchor) {
                var expectedCurrent = new SessionGoverningSetupReferences(
                    runtimeOverride
                        ?? active.Body.CoverageSetups.RuntimeConfig,
                    promptOverride
                        ?? active.Body.CoverageSetups.SystemPrompt
                );
                if (active.Body.CurrentSetups != expectedCurrent) {
                    throw new InvalidDataException(
                        "ArtifactSetCommitted currentSetups do not match the authoritative governing setup folded from commonAnchor through its Parent."
                    );
                }
                return allowedSourceHeads;
            }
            cursor = header.Parent;
        }
        throw new InvalidDataException(
            "ArtifactSetCommitted commonAnchor is not on its Parent lineage."
        );
    }

    internal static void ValidateArtifactSourceHead(
        IReadOnlySet<EventAddress> allowedSourceHeads,
        DerivedRecapArtifact artifact
    ) {
        ArgumentNullException.ThrowIfNull(allowedSourceHeads);
        ArgumentNullException.ThrowIfNull(artifact);
        if (!allowedSourceHeads.Contains(artifact.SourceRawHead)) {
            throw new SessionJournalNotReadyException(
                SessionJournalNotReadyReason.ArtifactSetMemberMismatch,
                $"Active artifact-set member '{artifact.ArtifactId}' sourceRawHead is outside the committed coverage interval.",
                artifact.ArtifactId
            );
        }
    }

    private SessionActiveArtifactSet ReadActiveArtifactSet(
        EventAddress address,
        SessionArtifactSetReference? expectedReference
    ) {
        using SessionJournalEventFrame frame = _reader.ReadEvent(address).Unwrap();
        ValidateSessionHeaderPreview(address, frame.Header);
        if ((SessionEventKind)frame.Header.OpaqueEventKind
            != SessionEventKind.ArtifactSetCommitted) {
            throw new InvalidDataException(
                $"Active artifact-set reference at {address} is not ArtifactSetCommitted."
            );
        }
        object decoded = SessionEventCodec.Decode(
            SessionEventKind.ArtifactSetCommitted,
            frame.Payload,
            out int version
        );
        var reference = new SessionArtifactSetReference(
            address,
            version,
            SessionRequestCanonicalizer.Sha256Hex(frame.Payload)
        );
        if (expectedReference is not null && expectedReference != reference) {
            throw new InvalidDataException(
                "Prepared active artifact-set reference does not match exact raw bytes."
            );
        }
        return new SessionActiveArtifactSet(
            address,
            frame.Header.Parent
                ?? throw new InvalidDataException(
                    "ArtifactSetCommitted cannot be a root event."
                ),
            (ArtifactSetCommittedBody)decoded,
            reference
        );
    }

    private static void ValidateManifestArtifactSetAssertion(
        CompletionRequestPreparedBody manifest,
        ArtifactSetCommittedBody activation
    ) {
        if (manifest.Plan.RawStartExclusive != activation.CommonAnchor) {
            throw new InvalidDataException(
                "Prepared plan.rawStartExclusive does not match its asserted ArtifactSet commonAnchor."
            );
        }
        _ = SessionCoherentRequestRecipe.ValidateAndAggregate(
            manifest.Plan.ArtifactInputs,
            activation
        );
    }

    private EventAddress Append(SessionEventKind kind, object body) {
        ThrowIfDisposed();
        EventAddress? expectedHead = _journal.GetHead(_mainRef);
        return AppendExpected(kind, body, expectedHead, requireBoundSetupCursor: false);
    }

    private EventAddress AppendExpected(
        SessionEventKind kind,
        object body,
        EventAddress? expectedHead,
        bool requireBoundSetupCursor
    ) {
        ThrowIfDisposed();
        EventAddress? observedHead = _journal.GetHead(_mainRef);
        if (observedHead != expectedHead) {
            _governingSetupCursor = null;
            throw new InvalidOperationException(
                $"SessionJournal branch head changed before append. Expected '{expectedHead}', observed '{observedHead}'."
            );
        }
        if (requireBoundSetupCursor
            && (_governingSetupCursor is null || _governingSetupCursor.Head != expectedHead)) {
            _governingSetupCursor = null;
            throw new InvalidOperationException(
                $"A completion request can be prepared only with a governing setup cursor bound to expected parent '{expectedHead}'."
            );
        }

        byte[] payload = SessionEventCodec.Encode(kind, body);
        try {
            _testHooks.BeforeCommit?.Invoke(kind);
            EventAddress committed = _journal.CommitToRef(
                SessionJournalDefaults.MainBranchName,
                expectedHead,
                payload,
                opaqueEventKind: (uint)kind,
                hint: default
            ).Unwrap().EventAddress;
            AdvanceGoverningSetupCursor(kind, body, expectedHead, committed);
            return committed;
        }
        catch {
            _governingSetupCursor = null;
            throw;
        }
    }

    private void AdvanceGoverningSetupCursor(
        SessionEventKind kind,
        object body,
        EventAddress? expectedHead,
        EventAddress committed
    ) {
        SessionGoverningSetup? cursor = _governingSetupCursor;
        if (cursor is null) { return; }
        if (cursor.Head != expectedHead) {
            _governingSetupCursor = null;
            return;
        }

        _governingSetupCursor = kind switch {
            SessionEventKind.RuntimeConfigSetup when body is SessionRuntimeConfiguration config =>
                cursor with {
                    Head = committed,
                    RuntimeConfigSetupAddress = committed,
                    RuntimeConfig = config
                },
            SessionEventKind.SystemPromptSetup when body is SystemPromptSetupBody prompt =>
                cursor with {
                    Head = committed,
                    SystemPromptSetupAddress = committed,
                    SystemPrompt = prompt.Content
                },
            _ => cursor with { Head = committed }
        };
    }

    private static void ValidateSessionHeaderPreview(EventAddress address, EventFrameHeader header) {
        if (!Enum.IsDefined(typeof(SessionEventKind), header.OpaqueEventKind)) {
            throw new InvalidDataException($"Unknown SessionJournal event kind '{header.OpaqueEventKind}' at {address}.");
        }

        if (header.Hint != default(AddressHint)) {
            throw new InvalidDataException($"SessionJournal trunk requires EventAddress hint 0, got '{header.Hint}' at {address}.");
        }
    }

    private SessionRuntimeConfiguration ReadRuntimeConfigSetup(EventAddress address) {
        using SessionJournalEventFrame frame = _reader.ReadEvent(address).Unwrap();
        ValidateSessionHeaderPreview(address, frame.Header);
        var kind = (SessionEventKind)frame.Header.OpaqueEventKind;
        if (kind != SessionEventKind.RuntimeConfigSetup) {
            throw new InvalidDataException($"Expected runtime-config-setup at {address}, got '{kind}'.");
        }

        object body = SessionEventCodec.Decode(kind, frame.Payload, out _);
        return body as SessionRuntimeConfiguration
            ?? throw new InvalidDataException($"runtime-config-setup at {address} decoded to unexpected body type '{body.GetType().Name}'.");
    }

    private T ReadAndValidateSetupReference<T>(
        SessionSetupReference reference,
        SessionEventKind expectedKind,
        ref int payloadReadCount
    ) where T : class {
        using SessionJournalEventFrame frame = _reader.ReadEvent(reference.Address).Unwrap();
        payloadReadCount++;
        ValidateSessionHeaderPreview(reference.Address, frame.Header);
        var actualKind = (SessionEventKind)frame.Header.OpaqueEventKind;
        if (actualKind != expectedKind) {
            throw new InvalidDataException(
                $"Setup checkpoint expected '{expectedKind}' at {reference.Address}, got '{actualKind}'."
            );
        }

        object body = SessionEventCodec.Decode(actualKind, frame.Payload, out int bodySchemaVersion);
        if (bodySchemaVersion != reference.BodySchemaVersion) {
            throw new InvalidDataException(
                $"Setup checkpoint schema version mismatch at {reference.Address}: expected {reference.BodySchemaVersion}, got {bodySchemaVersion}."
            );
        }

        string payloadSha256 = SessionRequestCanonicalizer.Sha256Hex(frame.Payload);
        if (!string.Equals(payloadSha256, reference.PayloadSha256, StringComparison.Ordinal)) {
            throw new InvalidDataException($"Setup checkpoint payload hash mismatch at {reference.Address}.");
        }

        return body as T
            ?? throw new InvalidDataException(
                $"Setup checkpoint at {reference.Address} decoded to '{body.GetType().Name}', expected '{typeof(T).Name}'."
            );
    }

    private string ReadSystemPromptSetup(EventAddress address) {
        using SessionJournalEventFrame frame = _reader.ReadEvent(address).Unwrap();
        ValidateSessionHeaderPreview(address, frame.Header);
        var kind = (SessionEventKind)frame.Header.OpaqueEventKind;
        if (kind != SessionEventKind.SystemPromptSetup) {
            throw new InvalidDataException($"Expected system-prompt-setup at {address}, got '{kind}'.");
        }

        object body = SessionEventCodec.Decode(kind, frame.Payload, out _);
        return body is SystemPromptSetupBody prompt
            ? prompt.Content
            : throw new InvalidDataException($"system-prompt-setup at {address} decoded to unexpected body type '{body.GetType().Name}'.");
    }

    private SessionRuntime RequireRuntime()
        => _runtime ?? throw new InvalidOperationException("SessionJournal runtime is required for SendAsync/ResumeAsync.");

    private static SessionCompletionTargetIdentity
        ValidateRuntimePlanningPrerequisites(SessionRuntime runtime) {
        ArgumentNullException.ThrowIfNull(runtime);
        SessionCompletionTargetIdentity target = runtime.CompletionTarget
            ?? throw new InvalidOperationException(
                "SessionJournal runtime requires non-secret CompletionTarget identity before request planning."
            );
        ValidateRequired(
            target.ConnectionId,
            "CompletionTarget.ConnectionId"
        );
        ValidateRequired(target.Kind, "CompletionTarget.Kind");
        ValidateRequired(
            target.ConnectionFingerprint,
            "CompletionTarget.ConnectionFingerprint"
        );
        ValidateRequired(
            target.RequestAdapterFingerprint,
            "CompletionTarget.RequestAdapterFingerprint"
        );
        ArgumentNullException.ThrowIfNull(runtime.CompletionClient);
        ValidateRequired(
            runtime.CompletionClient.Name,
            "CompletionClient.Name"
        );
        ValidateRequired(
            runtime.CompletionClient.ApiSpecId,
            "CompletionClient.ApiSpecId"
        );
        if (runtime.MaxTokens is <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(runtime),
                "SessionJournal runtime MaxTokens must be positive when specified."
            );
        }
        return target;
    }

    private static ToolSession RequireToolSession(SessionRuntime runtime)
        => runtime.ToolSession ?? throw new InvalidOperationException("SessionJournal runtime requires a ToolSession for tool execution.");

    private static SessionToolRuntimeIdentity RequireToolRuntimeIdentity(
        SessionRuntime runtime,
        ImmutableArray<ToolDefinition> visibleTools
    ) {
        if (visibleTools.IsEmpty) {
            throw new InvalidOperationException(
                "A tool-bearing request requires at least one visible tool definition."
            );
        }
        SessionToolRuntimeIdentity identity = runtime.ToolRuntimeIdentity
            ?? throw new InvalidOperationException(
                "A tool-bearing SessionJournal runtime requires a non-secret ToolRuntimeIdentity."
            );
        ValidateRequired(
            identity.HostId,
            "ToolRuntimeIdentity.HostId"
        );
        ValidateRequired(
            identity.ImplementationSetFingerprint,
            "ToolRuntimeIdentity.ImplementationSetFingerprint"
        );
        ValidateRequired(
            identity.CapabilitySetFingerprint,
            "ToolRuntimeIdentity.CapabilitySetFingerprint"
        );
        return identity;
    }

    private void TriggerFailpoint(SessionJournalFailpoint failpoint) {
        if (_testHooks.Failpoint == failpoint) { throw new SessionJournalFailpointException(failpoint); }
    }

    private static ResumeOutcome ToResumeOutcome(TurnResult result)
        => new(
            Advanced: true,
            Message: result.Message,
            Invocation: result.Invocation,
            Errors: result.Errors
        );

    private static string? GetCompletionInvocationMismatch(
        CompletionDescriptor invocation,
        ICompletionClient client,
        CompletionRequest request
    ) {
        if (string.Equals(invocation.ProviderId, client.Name, StringComparison.Ordinal)
            && string.Equals(invocation.ApiSpecId, client.ApiSpecId, StringComparison.Ordinal)
            && string.Equals(invocation.Model, request.ModelId, StringComparison.Ordinal)) {
            return null;
        }
        return
            "Completion result invocation does not match the active client and reconstructed request: "
            + $"expected provider='{client.Name}', apiSpec='{client.ApiSpecId}', model='{request.ModelId}'; "
            + $"actual provider='{invocation.ProviderId}', apiSpec='{invocation.ApiSpecId}', model='{invocation.Model}'.";
    }

    private static IReadOnlyList<string>? FreezeErrors(IReadOnlyList<string>? errors)
        => errors is null ? null : Array.AsReadOnly(errors.ToArray());

    private static string BuildCorrelationId(EventAddress observationAddress)
        => $"atelia.session-journal.turn.v1:{EventAddressTextCodec.Format(observationAddress)}";

    private static string BuildOperationId(EventAddress? head, RawToolCall call) {
        ArgumentNullException.ThrowIfNull(call);
        string turnKey = head is { } address
            ? EventAddressTextCodec.Format(address)
            : "no-head";
        return $"atelia.session-journal.tool.v1:{turnKey}:{call.ToolCallId}";
    }

    private static void ValidateCreateOptions(SessionCreateOptions options) {
        ValidateRequired(options.ModelId, nameof(options.ModelId));
        ValidateRequired(options.CompletionSurfaceId, nameof(options.CompletionSurfaceId));
        ValidateRequired(options.Schema, nameof(options.Schema));
        if (options.SystemPrompt is null) { throw new ArgumentNullException(nameof(options.SystemPrompt)); }
    }

    private static void ValidateRuntimeConfiguration(SessionRuntimeConfiguration configuration) {
        ValidateRequired(configuration.ModelId, nameof(configuration.ModelId));
        ValidateRequired(configuration.CompletionSurfaceId, nameof(configuration.CompletionSurfaceId));
        ValidateRequired(configuration.Schema, nameof(configuration.Schema));
    }

    private static void ValidateRequired(string value, string name) {
        if (string.IsNullOrWhiteSpace(value)) { throw new ArgumentException("Value must not be null, empty, or whitespace.", name); }
    }

    private sealed record RequestContextMaterialization(
        string SystemPrompt,
        IReadOnlyList<IHistoryMessage> Context,
        EventAddress RawStartExclusive,
        string RawRangeSha256,
        ImmutableArray<SessionRequestArtifactInput> ArtifactInputs,
        SessionArtifactSetReference ActiveArtifactSet
    );

    private sealed record ReadyActiveArtifactSet(
        SessionActiveArtifactSet Active,
        ImmutableArray<DerivedRecapArtifact> Artifacts
    );

    private sealed record CommittedCompletionResult(
        CompletionResult Result,
        EventAddress ActionAddress
    );

    private static string BuildTurnAbortMessage(CompletionTermination termination) {
        ArgumentNullException.ThrowIfNull(termination);
        return termination.Kind switch {
            CompletionTerminationKind.Incomplete =>
                $"Completion ended incompletely; the prepared request and known failure outcome were persisted, while no success action was persisted. reason={termination.ProviderReason ?? "<none>"}, detail={termination.Detail ?? "<none>"}",
            CompletionTerminationKind.Failed =>
                $"Completion failed; the prepared request and known failure outcome were persisted, while no success action was persisted. reason={termination.ProviderReason ?? "<none>"}, detail={termination.Detail ?? "<none>"}",
            _ =>
                $"Completion was aborted; no action was persisted and the prepared request remains durable. reason={termination.ProviderReason ?? "<none>"}, detail={termination.Detail ?? "<none>"}"
        };
    }

    private void ThrowIfDisposed() {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
