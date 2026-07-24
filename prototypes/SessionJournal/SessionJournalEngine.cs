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

        IReadOnlyList<EventAddress> chain = _journal.ReadChronologicalChain(head.Value, checkedRead: true, cancellationToken: cancellationToken).Unwrap();
        var events = new List<DecodedSessionEvent>(chain.Count);
        foreach (EventAddress address in chain) {
            cancellationToken.ThrowIfCancellationRequested();
            using EventFrame frame = _journal.ReadEvent(address).Unwrap();
            ValidateSessionHeaderPreview(address, frame.Header);

            var kind = (SessionEventKind)frame.Header.OpaqueEventKind;
            object body = SessionEventCodec.Decode(kind, frame.Payload, out int version);
            events.Add(new DecodedSessionEvent(kind, version, body, address));
        }

        return SessionReducer.Reduce(events);
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
        if (projection.ExecutionState.Phase != SessionExecutionPhase.Idle) {
            throw new InvalidOperationException(
                $"SendAsync requires an idle session. Current phase is '{projection.ExecutionState.Phase}'; call ResumeAsync first."
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
            SessionExecutionPhase.Empty or SessionExecutionPhase.Idle => new ResumeOutcome(Advanced: false),
            SessionExecutionPhase.AwaitingAgentAction => ToResumeOutcome(
                await CompletePendingObservationAsync(observer, cancellationToken).ConfigureAwait(false)
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
        if (projection.ExecutionState.Phase != SessionExecutionPhase.Idle) {
            throw new InvalidOperationException(
                $"AppendRuntimeConfigSetup requires an idle session. Current phase is '{projection.ExecutionState.Phase}'."
            );
        }

        return Append(SessionEventKind.RuntimeConfigSetup, configuration);
    }

    public EventAddress AppendSystemPromptSetup(string systemPrompt) {
        if (systemPrompt is null) { throw new ArgumentNullException(nameof(systemPrompt)); }
        SessionProjection projection = Project();
        if (projection.ExecutionState.Phase != SessionExecutionPhase.Idle) {
            throw new InvalidOperationException(
                $"AppendSystemPromptSetup requires an idle session. Current phase is '{projection.ExecutionState.Phase}'."
            );
        }

        return Append(SessionEventKind.SystemPromptSetup, new SystemPromptSetupBody(systemPrompt));
    }

    public EventAddress AppendAgentAction(ActionMessage action, CompletionDescriptor invocation) {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(invocation);
        return Append(SessionEventKind.AgentActionProduced, new AgentActionProducedBody(action, invocation));
    }

    public SessionGoverningSetup ResolveGoverningSetup(EventAddress head, CancellationToken cancellationToken = default) {
        ThrowIfDisposed();

        EventAddress? cursor = head;
        EventAddress? runtimeConfigSetupAddress = null;
        EventAddress? systemPromptSetupAddress = null;

        while (cursor is { } address && (runtimeConfigSetupAddress is null || systemPromptSetupAddress is null)) {
            cancellationToken.ThrowIfCancellationRequested();

            EventFrameHeader header = _journal.ReadEventHeaderPreview(address).Unwrap();
            ValidateSessionHeaderPreview(address, header);

            var kind = (SessionEventKind)header.OpaqueEventKind;
            if (kind == SessionEventKind.RuntimeConfigSetup && runtimeConfigSetupAddress is null) {
                runtimeConfigSetupAddress = address;
            }
            else if (kind == SessionEventKind.SystemPromptSetup && systemPromptSetupAddress is null) {
                systemPromptSetupAddress = address;
            }

            cursor = header.Parent;
        }

        if (runtimeConfigSetupAddress is null) {
            throw new InvalidDataException($"SessionJournal governing setup for head {head} is missing runtime-config-setup on its parent chain.");
        }

        if (systemPromptSetupAddress is null) {
            throw new InvalidDataException($"SessionJournal governing setup for head {head} is missing system-prompt-setup on its parent chain.");
        }

        SessionRuntimeConfiguration runtimeConfig = ReadRuntimeConfigSetup(runtimeConfigSetupAddress.Value);
        string systemPrompt = ReadSystemPromptSetup(systemPromptSetupAddress.Value);

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
            engine.Append(SessionEventKind.RuntimeConfigSetup, options.ToRuntimeConfiguration());
            engine.Append(SessionEventKind.SystemPromptSetup, new SystemPromptSetupBody(options.SystemPrompt));
            engine.Append(SessionEventKind.SessionCreated, new SessionCreatedBody());
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
        var request = new CompletionRequest(
            ModelId: config.ModelId,
            SystemPrompt: systemPrompt,
            Context: projection.Context,
            Tools: runtime.ToolSession?.VisibleDefinitions ?? ImmutableArray<ToolDefinition>.Empty
        );

        CompletionResult result = await runtime.CompletionClient
            .StreamCompletionAsync(request, observer, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Termination.IsSuccess) {
            throw new SessionJournalTurnAbortedException(
                BuildTurnAbortMessage(result.Termination),
                result.Termination,
                FreezeErrors(result.Errors)
            );
        }

        TriggerFailpoint(SessionJournalFailpoint.AfterCompletionBeforeActionCommitted);
        AppendAgentAction(result.Message, result.Invocation);

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

    private EventAddress Append(SessionEventKind kind, object body) {
        ThrowIfDisposed();
        EventAddress? expectedHead = _journal.GetHead(_mainRef);
        byte[] payload = SessionEventCodec.Encode(kind, body);
        return _journal.CommitToRef(
            SessionJournalDefaults.MainBranchName,
            expectedHead,
            payload,
            opaqueEventKind: (uint)kind,
            hint: default
        ).Unwrap().EventAddress;
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
                $"Completion ended incompletely and was not persisted. reason={termination.ProviderReason ?? "<none>"}, detail={termination.Detail ?? "<none>"}",
            CompletionTerminationKind.Failed =>
                $"Completion failed and was not persisted. reason={termination.ProviderReason ?? "<none>"}, detail={termination.Detail ?? "<none>"}",
            _ =>
                $"Completion was aborted and was not persisted. reason={termination.ProviderReason ?? "<none>"}, detail={termination.Detail ?? "<none>"}"
        };
    }

    private void ThrowIfDisposed() {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
