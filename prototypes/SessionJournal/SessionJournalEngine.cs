using System.Collections.Immutable;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.EventJournal;

namespace Atelia.SessionJournal;

public sealed class SessionJournalEngine : IDisposable {
    private static readonly EventJournalOptions DefaultJournalOptions = new() {
        PayloadCodecPolicy = EventPayloadCodecPolicy.Zlib
    };

    private readonly EventJournal.EventJournal _journal;
    private readonly RefId _mainRef;
    private readonly SessionJournalTestHooks _testHooks;
    private SessionRuntime? _runtime;
    private SessionGoverningSetup? _governingSetupCursor;
    private GoverningSetupResolutionDiagnostics _lastGoverningSetupResolutionDiagnostics;
    private bool _disposed;

    private SessionJournalEngine(
        EventJournal.EventJournal journal,
        RefId mainRef,
        SessionRuntime? runtime,
        SessionJournalTestHooks? testHooks
    ) {
        _journal = journal;
        _mainRef = mainRef;
        _runtime = runtime;
        _testHooks = testHooks ?? new SessionJournalTestHooks();
    }

    public string Path => _journal.JournalPath;

    internal GoverningSetupResolutionDiagnostics LastGoverningSetupResolutionDiagnostics
        => _lastGoverningSetupResolutionDiagnostics;

    internal EventAddress? GoverningSetupCursorHeadForTest => _governingSetupCursor?.Head;

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
        var projection = Project(cancellationToken);
        if (projection.ExecutionState.Phase is not (SessionExecutionPhase.Idle or SessionExecutionPhase.TurnFailed)) {
            throw new InvalidOperationException(
                $"SendAsync requires an idle or explicitly failed turn boundary. Current phase is '{projection.ExecutionState.Phase}'; call ResumeAsync first."
            );
        }

        AppendObservation(observation);
        TriggerFailpoint(SessionJournalFailpoint.AfterObservationCommitted);
        return await CompletePendingObservationAsync(observer, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ResumeOutcome> ResumeAsync(CancellationToken cancellationToken = default)
        => await ResumeAsync(observer: null, cancellationToken).ConfigureAwait(false);

    public async Task<ResumeOutcome> ResumeAsync(
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        SessionProjection projection = Project(cancellationToken);
        return projection.ExecutionState.Phase switch {
            SessionExecutionPhase.Empty or SessionExecutionPhase.Idle or SessionExecutionPhase.TurnFailed =>
                new ResumeOutcome(Advanced: false),
            SessionExecutionPhase.AwaitingAgentAction => ToResumeOutcome(
                await CompletePendingObservationAsync(observer, cancellationToken).ConfigureAwait(false)
            ),
            SessionExecutionPhase.AwaitingCompletion => throw new InvalidOperationException(
                "The current completion request is already durably prepared. CS-3A does not resend or replan it; canonical request recovery is implemented by CS-3C."
            ),
            SessionExecutionPhase.AwaitingToolExecution => ToResumeOutcome(
                await ContinueToolLoopAsync(projection, observer, cancellationToken).ConfigureAwait(false)
            ),
            _ => throw new InvalidOperationException($"Unknown SessionJournal execution phase '{projection.ExecutionState.Phase}'.")
        };
    }

    public EventAddress AppendObservation(string content) {
        ValidateRequired(content, nameof(content));
        return Append(SessionEventKind.ObservationAccepted, new ObservationAcceptedBody(content));
    }

    public EventAddress AppendRuntimeConfigSetup(SessionRuntimeConfiguration configuration) {
        ArgumentNullException.ThrowIfNull(configuration);
        ValidateRuntimeConfiguration(configuration);
        SessionProjection projection = Project();
        if (projection.ExecutionState.Phase is not (SessionExecutionPhase.Idle or SessionExecutionPhase.TurnFailed)) {
            throw new InvalidOperationException(
                $"AppendRuntimeConfigSetup requires an idle or explicitly failed turn boundary. Current phase is '{projection.ExecutionState.Phase}'."
            );
        }

        return Append(SessionEventKind.RuntimeConfigSetup, configuration);
    }

    public EventAddress AppendSystemPromptSetup(string systemPrompt) {
        if (systemPrompt is null) { throw new ArgumentNullException(nameof(systemPrompt)); }
        SessionProjection projection = Project();
        if (projection.ExecutionState.Phase is not (SessionExecutionPhase.Idle or SessionExecutionPhase.TurnFailed)) {
            throw new InvalidOperationException(
                $"AppendSystemPromptSetup requires an idle or explicitly failed turn boundary. Current phase is '{projection.ExecutionState.Phase}'."
            );
        }

        return Append(SessionEventKind.SystemPromptSetup, new SystemPromptSetupBody(systemPrompt));
    }

    public EventAddress AppendImportedAgentAction(ActionMessage action, CompletionDescriptor invocation) {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(invocation);
        SessionProjection projection = Project();
        if (projection.ExecutionState.Phase != SessionExecutionPhase.AwaitingAgentAction
            || projection.ExecutionState.HeadKind is not (SessionEventKind.ObservationAccepted or SessionEventKind.ToolResultObserved)) {
            throw new InvalidOperationException(
                "AppendImportedAgentAction requires an unprepared observation or settled tool-result completion boundary."
            );
        }
        return Append(SessionEventKind.ImportedAgentAction, new AgentActionProducedBody(action, invocation));
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

            EventFrameHeader header = _journal.ReadEventHeaderPreview(address).Unwrap();
            headerVisitCount++;
            ValidateSessionHeaderPreview(address, header);

            var kind = (SessionEventKind)header.OpaqueEventKind;
            if (kind == SessionEventKind.RuntimeConfigSetup && runtimeConfigSetupAddress is null) {
                runtimeConfigSetupAddress = address;
            }
            else if (kind == SessionEventKind.SystemPromptSetup && systemPromptSetupAddress is null) {
                systemPromptSetupAddress = address;
            }
            else if (kind == SessionEventKind.CompletionRequestPrepared
                && (runtimeConfigSetupAddress is null || systemPromptSetupAddress is null)) {
                using EventFrame manifestFrame = _journal.ReadEvent(address).Unwrap();
                payloadReadCount++;
                manifestPayloadReadCount++;
                object decoded = SessionEventCodec.Decode(kind, manifestFrame.Payload, out _);
                var manifest = decoded as CompletionRequestPreparedBody
                    ?? throw new InvalidDataException($"completion-request-prepared at {address} decoded to '{decoded.GetType().Name}'.");

                if (runtimeConfigSetupAddress is null) {
                    runtimeConfig = ReadAndValidateSetupReference<SessionRuntimeConfiguration>(
                        manifest.Setups.RuntimeConfig,
                        SessionEventKind.RuntimeConfigSetup,
                        ref payloadReadCount
                    );
                    runtimeConfigSetupAddress = manifest.Setups.RuntimeConfig.Address;
                }
                if (systemPromptSetupAddress is null) {
                    SystemPromptSetupBody prompt = ReadAndValidateSetupReference<SystemPromptSetupBody>(
                        manifest.Setups.SystemPrompt,
                        SessionEventKind.SystemPromptSetup,
                        ref payloadReadCount
                    );
                    systemPrompt = prompt.Content;
                    systemPromptSetupAddress = manifest.Setups.SystemPrompt.Address;
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
        using EventFrame frame = _journal.ReadEvent(address).Unwrap();
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
        IReadOnlyList<EventAddress> chain = _journal.ReadChronologicalChain(head, checkedRead: true, cancellationToken: cancellationToken).Unwrap();
        var events = new List<DecodedSessionEvent>(chain.Count);
        foreach (EventAddress address in chain) {
            cancellationToken.ThrowIfCancellationRequested();
            using EventFrame frame = _journal.ReadEvent(address).Unwrap();
            ValidateSessionHeaderPreview(address, frame.Header);

            var kind = (SessionEventKind)frame.Header.OpaqueEventKind;
            object body = SessionEventCodec.Decode(kind, frame.Payload, out int version);
            events.Add(new DecodedSessionEvent(kind, version, body, address, frame.Header.Parent));
        }

        return events;
    }

    private async Task<TurnResult> CompletePendingObservationAsync(
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken
    ) {
        SessionRuntime runtime = RequireRuntime();
        SessionProjection projection = Project(cancellationToken);
        if (projection.ExecutionState.Phase != SessionExecutionPhase.AwaitingAgentAction) {
            throw new InvalidOperationException(
                $"Completion can resume only from '{SessionExecutionPhase.AwaitingAgentAction}', got '{projection.ExecutionState.Phase}'."
            );
        }

        SessionRuntimeConfiguration config = projection.Config
            ?? throw new InvalidDataException("SessionJournal projection is missing session configuration.");
        string systemPrompt = projection.SystemPrompt
            ?? throw new InvalidDataException("SessionJournal projection is missing system prompt.");
        SessionCompletionTargetIdentity completionTarget = runtime.CompletionTarget
            ?? throw new InvalidOperationException(
                "SessionJournal runtime requires non-secret CompletionTarget identity before a durable completion request can be prepared."
            );
        ImmutableArray<ToolDefinition> tools = runtime.ToolSession?.VisibleDefinitions ?? ImmutableArray<ToolDefinition>.Empty;
        var request = new CompletionRequest(
            ModelId: config.ModelId,
            SystemPrompt: systemPrompt,
            Context: projection.Context,
            Tools: tools,
            MaxTokens: runtime.MaxTokens
        );

        EventAddress expectedParent = projection.Head
            ?? throw new InvalidDataException("AwaitingAgentAction projection requires a raw head.");
        SessionGoverningSetup governingSetup = EnsureGoverningSetupCursor(expectedParent, cancellationToken);
        if (governingSetup.RuntimeConfig != config
            || !string.Equals(governingSetup.SystemPrompt, systemPrompt, StringComparison.Ordinal)) {
            throw new InvalidDataException("Governing setup cursor does not match the exact current projection.");
        }

        CompletionRequestPreparedBody manifest = BuildRequestManifest(
            request,
            projection,
            governingSetup,
            completionTarget,
            runtime,
            tools,
            expectedParent,
            cancellationToken
        );
        EventAddress preparedAddress = AppendExpected(
            SessionEventKind.CompletionRequestPrepared,
            manifest,
            expectedParent,
            requireBoundSetupCursor: true
        );
        TriggerFailpoint(SessionJournalFailpoint.AfterRequestPreparedCommitted);

        CompletionResult result = await runtime.CompletionClient
            .StreamCompletionAsync(request, observer, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Termination.IsSuccess) {
            IReadOnlyList<string> frozenErrors = FreezeErrors(result.Errors)
                ?? Array.AsReadOnly(Array.Empty<string>());
            AppendExpected(
                SessionEventKind.CompletionAttemptFailed,
                new CompletionAttemptFailedBody(
                    manifest.Attempt.AttemptId,
                    result.Termination.Kind,
                    result.Termination.ProviderReason,
                    result.Termination.Detail,
                    frozenErrors
                ),
                preparedAddress,
                requireBoundSetupCursor: false
            );
            throw new SessionJournalTurnAbortedException(
                BuildTurnAbortMessage(result.Termination),
                result.Termination,
                frozenErrors
            );
        }

        ValidateCompletionInvocation(result.Invocation, runtime.CompletionClient, request);
        TriggerFailpoint(SessionJournalFailpoint.AfterCompletionBeforeActionCommitted);
        AppendExpected(
            SessionEventKind.AgentActionProduced,
            new AgentActionProducedBody(result.Message, result.Invocation),
            preparedAddress,
            requireBoundSetupCursor: false
        );

        projection = Project(cancellationToken);
        if (projection.ExecutionState.Phase == SessionExecutionPhase.AwaitingToolExecution) { return await ContinueToolLoopAsync(projection, observer, cancellationToken).ConfigureAwait(false); }

        return new TurnResult(result.Message, result.Invocation, FreezeErrors(result.Errors));
    }

    private async Task<TurnResult> ContinueToolLoopAsync(
        SessionProjection projection,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken
    ) {
        SessionRuntime runtime = RequireRuntime();
        ToolSession toolSession = RequireToolSession(runtime);
        if (projection.ExecutionState.PendingToolCall is null) { throw new InvalidDataException("AwaitingToolExecution requires a pending tool call."); }

        RawToolCall toolCall = projection.ExecutionState.PendingToolCall;
        if (!projection.ExecutionState.PendingToolExecutionStarted) {
            string operationId = projection.ExecutionState.PendingOperationId ?? BuildOperationId(projection.Head, toolCall);
            AppendToolExecutionStarted(toolCall, operationId);
            TriggerFailpoint(SessionJournalFailpoint.AfterToolStartedCommitted);
        }

        toolSession.RestoreExecutionSequence(projection.ExecutionState.ToolExecutionSequenceCheckpoint);
        ToolCallExecutionResult executionResult = await toolSession.ExecuteAsync(toolCall, cancellationToken).ConfigureAwait(false);
        AppendToolResultObserved(executionResult);
        TriggerFailpoint(SessionJournalFailpoint.AfterToolResultCommitted);

        SessionProjection refreshed = Project(cancellationToken);
        return refreshed.ExecutionState.Phase switch {
            SessionExecutionPhase.AwaitingToolExecution => await ContinueToolLoopAsync(refreshed, observer, cancellationToken).ConfigureAwait(false),
            SessionExecutionPhase.AwaitingAgentAction => await CompletePendingObservationAsync(observer, cancellationToken).ConfigureAwait(false),
            SessionExecutionPhase.Idle => new TurnResult(
                new ActionMessage(Array.Empty<ActionBlock>()),
                new CompletionDescriptor(runtime.CompletionClient.Name, runtime.CompletionClient.ApiSpecId, refreshed.Config?.ModelId ?? string.Empty),
                null
            ),
            _ => throw new InvalidOperationException($"Tool loop cannot continue from phase '{refreshed.ExecutionState.Phase}'.")
        };
    }

    private EventAddress AppendToolExecutionStarted(RawToolCall call, string operationId) {
        ArgumentNullException.ThrowIfNull(call);
        ValidateRequired(call.ToolCallId, nameof(call.ToolCallId));
        ValidateRequired(call.ToolName, nameof(call.ToolName));
        ValidateRequired(call.RawArgumentsJson, nameof(call.RawArgumentsJson));
        ValidateRequired(operationId, nameof(operationId));
        return Append(
            SessionEventKind.ToolExecutionStarted,
            new ToolExecutionStartedBody(call.ToolCallId, call.ToolName, call.RawArgumentsJson, operationId)
        );
    }

    private EventAddress AppendToolResultObserved(ToolCallExecutionResult result) {
        ArgumentNullException.ThrowIfNull(result);
        return Append(
            SessionEventKind.ToolResultObserved,
            new ToolResultObservedBody(result.ToolCallId, result.ToolName, result.ExecuteResult.Status, result.ExecuteResult.Blocks)
        );
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
        SessionProjection projection,
        SessionGoverningSetup governingSetup,
        SessionCompletionTargetIdentity completionTarget,
        SessionRuntime runtime,
        ImmutableArray<ToolDefinition> tools,
        EventAddress expectedParent,
        CancellationToken cancellationToken
    ) {
        string correlationId = projection.ExecutionState.ActiveCorrelationId
            ?? throw new InvalidDataException("AwaitingAgentAction requires an active correlation id.");
        string reason = projection.ExecutionState.HeadKind == SessionEventKind.ToolResultObserved
            ? "tool-continuation"
            : "observation";
        SessionRequestCommitment commitment = SessionRequestCanonicalizer.CreateCommitment(request);
        var manifest = new CompletionRequestPreparedBody(
            new SessionRequestAttempt(
                $"attempt-{Guid.NewGuid():N}",
                correlationId,
                reason,
                ReplacesAttemptId: null
            ),
            new SessionContextPlan(
                SelectionPolicyId: SessionRequestManifestDefaults.SelectionPolicyId,
                PlannerFingerprint: SessionRequestManifestDefaults.PlannerFingerprint,
                RawStartExclusive: null,
                RawRangeSha256: ComputeRawRangeSha256(expectedParent, cancellationToken),
                ArtifactInputs: ImmutableArray<SessionRequestArtifactInput>.Empty,
                RecalledInputs: ImmutableArray<SessionRequestRecalledInput>.Empty,
                RenderingProfileId: SessionRequestManifestDefaults.RenderingProfileId,
                ModelProfileId: request.ModelId,
                EstimatedInputTokens: checked((commitment.ByteLength + 3) / 4),
                Reason: reason
            ),
            new SessionGoverningSetupReferences(
                CreateSetupReference(governingSetup.RuntimeConfigSetupAddress, SessionEventKind.RuntimeConfigSetup),
                CreateSetupReference(governingSetup.SystemPromptSetupAddress, SessionEventKind.SystemPromptSetup)
            ),
            new SessionRequestParameters(request.ModelId, request.MaxTokens),
            new SessionRequestToolSet(
                SessionRequestManifestDefaults.ToolCodecId,
                SessionRequestCanonicalizer.ComputeToolSetSha256(tools),
                tools
            ),
            new SessionRequestRendering(
                ContextRendererId: SessionRequestManifestDefaults.ContextRendererId,
                ContextRendererFingerprint: SessionRequestManifestDefaults.ContextRendererFingerprint,
                CanonicalRequestCodecId: SessionRequestManifestDefaults.CanonicalRequestCodecId,
                ToolCodecId: SessionRequestManifestDefaults.ToolCodecId,
                ReasoningCodecSetFingerprint: SessionRequestManifestDefaults.ReasoningCodecSetFingerprint
            ),
            new SessionRequestTarget(
                completionTarget,
                governingSetup.RuntimeConfig.CompletionSurfaceId,
                runtime.CompletionClient.Name,
                runtime.CompletionClient.ApiSpecId
            ),
            commitment
        );

        ValidateManifestMatchesRequest(manifest, request, tools, governingSetup, completionTarget, runtime);
        return manifest;
    }

    private void ValidateManifestMatchesRequest(
        CompletionRequestPreparedBody manifest,
        CompletionRequest request,
        ImmutableArray<ToolDefinition> tools,
        SessionGoverningSetup governingSetup,
        SessionCompletionTargetIdentity completionTarget,
        SessionRuntime runtime
    ) {
        SessionRequestManifestCodec.Validate(manifest);
        SessionRequestCommitment expectedCommitment = SessionRequestCanonicalizer.CreateCommitment(request);
        SessionSetupReference expectedRuntimeSetup = CreateSetupReference(
            governingSetup.RuntimeConfigSetupAddress,
            SessionEventKind.RuntimeConfigSetup
        );
        SessionSetupReference expectedPromptSetup = CreateSetupReference(
            governingSetup.SystemPromptSetupAddress,
            SessionEventKind.SystemPromptSetup
        );
        if (manifest.Commitment != expectedCommitment
            || !string.Equals(manifest.Parameters.ModelId, request.ModelId, StringComparison.Ordinal)
            || manifest.Parameters.MaxTokens != request.MaxTokens
            || !string.Equals(request.ModelId, governingSetup.RuntimeConfig.ModelId, StringComparison.Ordinal)
            || !string.Equals(request.SystemPrompt, governingSetup.SystemPrompt, StringComparison.Ordinal)
            || !string.Equals(manifest.ToolSet.Sha256, SessionRequestCanonicalizer.ComputeToolSetSha256(tools), StringComparison.Ordinal)
            || !manifest.ToolSet.Definitions.SequenceEqual(tools)
            || manifest.Setups.RuntimeConfig != expectedRuntimeSetup
            || manifest.Setups.SystemPrompt != expectedPromptSetup
            || manifest.Target.Connection != completionTarget
            || !string.Equals(manifest.Target.CompletionSurfaceId, governingSetup.RuntimeConfig.CompletionSurfaceId, StringComparison.Ordinal)
            || !string.Equals(manifest.Target.ClientName, runtime.CompletionClient.Name, StringComparison.Ordinal)
            || !string.Equals(manifest.Target.ApiSpecId, runtime.CompletionClient.ApiSpecId, StringComparison.Ordinal)) {
            throw new InvalidDataException("completion-request-prepared manifest does not match the exact provider-neutral CompletionRequest.");
        }
    }

    private SessionSetupReference CreateSetupReference(EventAddress address, SessionEventKind expectedKind) {
        using EventFrame frame = _journal.ReadEvent(address).Unwrap();
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

    internal string ComputeRawRangeSha256ForTest(EventAddress rawEndInclusive)
        => ComputeRawRangeSha256(rawEndInclusive, CancellationToken.None);

    private string ComputeRawRangeSha256(EventAddress rawEndInclusive, CancellationToken cancellationToken) {
        IReadOnlyList<EventAddress> chain = _journal.ReadChronologicalChain(
            rawEndInclusive,
            checkedRead: true,
            cancellationToken: cancellationToken
        ).Unwrap();
        var entries = new List<SessionRawRangeHashEntry>(chain.Count);
        foreach (EventAddress address in chain) {
            cancellationToken.ThrowIfCancellationRequested();
            using EventFrame frame = _journal.ReadEvent(address).Unwrap();
            ValidateSessionHeaderPreview(address, frame.Header);
            var kind = (SessionEventKind)frame.Header.OpaqueEventKind;
            _ = SessionEventCodec.Decode(kind, frame.Payload, out int bodySchemaVersion);
            entries.Add(new SessionRawRangeHashEntry(
                address,
                frame.Header.Parent,
                frame.Header.OpaqueEventKind,
                bodySchemaVersion,
                SessionRequestCanonicalizer.Sha256Hex(frame.Payload)
            ));
        }

        return SessionRawRangeHasher.Compute(
            rawStartExclusive: null,
            rawEndInclusive,
            entries
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
        using EventFrame frame = _journal.ReadEvent(address).Unwrap();
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
        using EventFrame frame = _journal.ReadEvent(reference.Address).Unwrap();
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
        using EventFrame frame = _journal.ReadEvent(address).Unwrap();
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

    private static ToolSession RequireToolSession(SessionRuntime runtime)
        => runtime.ToolSession ?? throw new InvalidOperationException("SessionJournal runtime requires a ToolSession for tool execution.");

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

    private static void ValidateCompletionInvocation(
        CompletionDescriptor invocation,
        ICompletionClient client,
        CompletionRequest request
    ) {
        if (!string.Equals(invocation.ProviderId, client.Name, StringComparison.Ordinal)
            || !string.Equals(invocation.ApiSpecId, client.ApiSpecId, StringComparison.Ordinal)
            || !string.Equals(invocation.Model, request.ModelId, StringComparison.Ordinal)) {
            throw new InvalidDataException(
                "Completion result invocation does not match the actual client identity and prepared request model."
            );
        }
    }

    private static IReadOnlyList<string>? FreezeErrors(IReadOnlyList<string>? errors)
        => errors is null ? null : Array.AsReadOnly(errors.ToArray());

    private static string BuildOperationId(EventAddress? head, RawToolCall call) {
        ArgumentNullException.ThrowIfNull(call);
        string turnKey = head?.ToString() ?? "no-head";
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
