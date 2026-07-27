using System.Collections.Immutable;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.EventJournal;

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
        // Do this before the durable observation append. Candidate availability is intentionally
        // checked only after that append: a provider selects for the exact new completion boundary.
        ValidateContextPlanningPreflight(runtime);
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
                await ResumeCompletionAsync(
                    recovery,
                    observer,
                    cancellationToken
                ).ConfigureAwait(false),
            SessionExecutionPhase.AwaitingCompletionDispatch =>
                await ResumeCompletionAsync(
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
        SessionAuthoritativeGoverningSetupResolver.Result result =
            SessionAuthoritativeGoverningSetupResolver.Resolve(
                _reader, head, allowLegacyArtifactSetCheckpoint: true, cancellationToken
            );
        _lastGoverningSetupResolutionDiagnostics = result.Diagnostics;
        return result.Setup;
    }

    /// <summary>
    /// Resolves the exact raw setup facts governing <paramref name="head"/>. Legacy kind-12
    /// checkpoints are deliberately disabled: callers receive addresses proven by the raw setup
    /// streams plus the exact schema/hash identity of each referenced payload.
    /// </summary>
    public SessionContextAnchorSetupReferences
        ResolveContextAnchorSetupReferences(
        EventAddress head,
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        SessionAuthoritativeGoverningSetupResolver.Result result =
            SessionAuthoritativeGoverningSetupResolver.Resolve(
                _reader,
                head,
                allowLegacyArtifactSetCheckpoint: false,
                cancellationToken
            );
        SessionSetupReference runtime = CreateSetupReference(
            result.Setup.RuntimeConfigSetupAddress,
            SessionEventKind.RuntimeConfigSetup
        );
        SessionSetupReference prompt = CreateSetupReference(
            result.Setup.SystemPromptSetupAddress,
            SessionEventKind.SystemPromptSetup
        );
        return new SessionContextAnchorSetupReferences(
            new SessionContextSetupReference(
                runtime.Address,
                runtime.BodySchemaVersion,
                runtime.PayloadSha256
            ),
            new SessionContextSetupReference(
                prompt.Address,
                prompt.BodySchemaVersion,
                prompt.PayloadSha256
            )
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
        SessionGoverningSetup governingSetup = EnsurePlanningGoverningSetupCursor(
            completionBoundary,
            cancellationToken
        );
        SessionContextCandidate selectedCandidate = await SelectContextCandidateAsync(
            runtime,
            completionBoundary,
            cancellationToken
        ).ConfigureAwait(false);
        SessionGoverningSetup anchorSetup =
            SessionAuthoritativeGoverningSetupResolver.Resolve(
                _reader,
                selectedCandidate.RawStartExclusive,
                allowLegacyArtifactSetCheckpoint: false,
                cancellationToken
            ).Setup;
        ValidatedSessionContextCandidate candidate =
            SessionContextCandidateValidator.Validate(
                _reader,
                completionBoundary,
                anchorSetup,
                selectedCandidate,
                cancellationToken
            );
        SessionTailContextProjectionResult tail = SessionTailContextProjection.Materialize(
            _reader,
            governingSetup,
            candidate,
            cancellationToken
        );
        _lastTailProjectionDiagnostics = tail.Diagnostics;
        var materialization = new RequestContextMaterialization(
            tail.SystemPrompt,
            tail.Context,
            tail.RawStartExclusive,
            tail.RawRangeSha256,
            CreateSetupReferences(candidate.AnchorGoverningSetup),
            tail.ContextSnapshots.Select(static snapshot => new SessionRequestContextInput(
                SessionArtifactContextSnapshotHasher.ComputeSha256(snapshot), snapshot
            )).ToImmutableArray()
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
        return await StartAndExecuteCompletionAttemptAsync(
            request,
            preparedAddress,
            manifest,
            runtime,
            allowResultToolCalls,
            observer,
            cancellationToken
        ).ConfigureAwait(false);
    }

    private async Task<CommittedCompletionResult> StartAndExecuteCompletionAttemptAsync(
        CompletionRequest request,
        EventAddress expectedParent,
        CompletionRequestPreparedBody manifest,
        SessionRuntime runtime,
        bool allowResultToolCalls,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        EventAddress startedAddress = AppendExpected(
            SessionEventKind.CompletionAttemptStarted,
            new CompletionAttemptStartedBody(),
            expectedParent,
            requireBoundSetupCursor: false
        );
        TriggerFailpoint(SessionJournalFailpoint.AfterCompletionAttemptStartedCommitted);
        return await ExecuteCommittedCompletionAttemptAsync(
            request,
            startedAddress,
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
                InvalidCompletionInvocationReason,
                invocationMismatch
            );
        }
        if (!allowResultToolCalls && result.Message.ToolCalls.Count > 0) {
            const string detail =
                "Provider returned tool calls for a request whose durable policy supports no tools.";
            ThrowKnownHostFailure(
                activeAttemptAddress,
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
                manifest.Origin.CorrelationId,
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
        string reason,
        string detail
    ) {
        CompletionTermination hostFailure = CompletionTermination.Failed(reason, detail);
        IReadOnlyList<string> errors = Array.AsReadOnly([detail]);
        AppendExpected(
            SessionEventKind.CompletionAttemptFailed,
            new CompletionAttemptFailedBody(
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

    private async Task<ResumeOutcome> ResumeCompletionAsync(
        SessionExecutionRecovery recovery,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken
    ) {
        if (recovery.State.Phase is not (
                SessionExecutionPhase.AwaitingCompletionDispatch
                or SessionExecutionPhase.AwaitingCompletion)
            || recovery.Boundary.SourcePrepared is not {
            } sourcePreparedAddress
            || recovery.State.PendingRequestPreparedAddress !=
                sourcePreparedAddress
            || recovery.Head is not { } activeHead) {
            throw new InvalidDataException(
                "Prepared recovery is missing its exact durable attempt boundary."
            );
        }
        bool uncertain =
            recovery.State.Phase == SessionExecutionPhase.AwaitingCompletion;
        if (uncertain
            && recovery.State.ActiveCompletionAttemptAddress != activeHead) {
            throw new InvalidDataException(
                "Uncertain completion recovery is missing its exact active Started boundary."
            );
        }
        if (!uncertain
            && recovery.State.ActiveCompletionAttemptAddress is not null) {
            throw new InvalidDataException(
                "Prepared-only recovery must not expose an active Started boundary."
            );
        }
        SessionUncertainCompletionRecoveryPolicy policy =
            _runtime?.UncertainCompletionRecoveryPolicy
            ?? SessionUncertainCompletionRecoveryPolicy.Refuse;
        if (uncertain && policy == SessionUncertainCompletionRecoveryPolicy.Refuse) {
            throw new InvalidOperationException(
                "The current completion attempt has an uncertain outcome. "
                + "Recovery policy Refuse does not call the provider or mutate the journal."
            );
        }
        if (uncertain
            && policy != SessionUncertainCompletionRecoveryPolicy.RestartWithNewAttempt) {
            throw new NotSupportedException(
                $"Unsupported uncertain completion recovery policy '{policy}'."
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
                manifest.Origin.CorrelationId,
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

        bool sourceAllowsToolCalls =
            !manifest.ToolSet.Definitions.IsEmpty;
        CommittedCompletionResult committed =
            await StartAndExecuteCompletionAttemptAsync(
                reconstruction.Request,
                activeHead,
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

    private SessionGoverningSetup EnsurePlanningGoverningSetupCursor(
        EventAddress expectedHead,
        CancellationToken cancellationToken
    ) {
        EventAddress? observedHead = _journal.GetHead(_mainRef);
        if (observedHead != expectedHead) {
            _governingSetupCursor = null;
            throw new InvalidOperationException(
                $"Cannot bind planning governing setup cursor: expected head '{expectedHead}', observed '{observedHead}'."
            );
        }

        _lastGoverningSetupResolutionDiagnostics = default;
        SessionAuthoritativeGoverningSetupResolver.Result result =
            SessionAuthoritativeGoverningSetupResolver.Resolve(
                _reader,
                expectedHead,
                allowLegacyArtifactSetCheckpoint: false,
                cancellationToken
            );
        _lastGoverningSetupResolutionDiagnostics = result.Diagnostics;
        _governingSetupCursor = result.Setup;
        return result.Setup;
    }

    private static void ValidateContextPlanningPreflight(
        SessionRuntime runtime
    ) {
        _ = RequireContextCandidateSource(runtime);
        (runtime.ContextSelection ?? SessionContextSelectionOptions.Default)
            .ValidateShape();
    }

    private static ICoherentContextCandidateSource RequireContextCandidateSource(
        SessionRuntime runtime
    ) => runtime.ContextCandidateSource
        ?? throw new SessionJournalNotReadyException(
            SessionJournalNotReadyReason.ContextCandidateSourceRequired,
            "Online completion requires an ICoherentContextCandidateSource configured on SessionRuntime."
        );

    private static async ValueTask<SessionContextCandidate> SelectContextCandidateAsync(
        SessionRuntime runtime,
        EventAddress completionBoundary,
        CancellationToken cancellationToken
    ) {
        ICoherentContextCandidateSource source = RequireContextCandidateSource(runtime);
        SessionContextSelectionRequest request =
            (runtime.ContextSelection ?? SessionContextSelectionOptions.Default)
                .CreateRequest(completionBoundary);
        SessionContextCandidate? candidate = await source
            .SelectAsync(request, cancellationToken)
            .ConfigureAwait(false);
        return candidate ?? throw new SessionJournalNotReadyException(
            SessionJournalNotReadyReason.ContextCandidateUnavailable,
            $"No coherent context candidate is currently available for completion boundary '{completionBoundary}'."
        );
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
            new SessionRequestOrigin(
                correlationId,
                reason
            ),
            executionCheckpoint,
            new SessionContextPlan(
                RawStartExclusive: materialization.RawStartExclusive,
                RawRangeSha256: materialization.RawRangeSha256,
                RawStartSetups: materialization.RawStartSetups,
                ExactContextInputs: materialization.ExactContextInputs
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
        SessionGoverningSetupReferences RawStartSetups,
        ImmutableArray<SessionRequestContextInput> ExactContextInputs
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
