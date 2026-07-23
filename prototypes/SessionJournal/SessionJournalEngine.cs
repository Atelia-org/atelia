using System.Collections.Immutable;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;

namespace Atelia.SessionJournal;

public sealed class SessionJournalEngine : IDisposable {
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

    public static SessionJournalEngine Open(string path)
        => OpenCore(path, runtime: null, testHooks: null);

    public static SessionJournalEngine Open(string path, SessionRuntime runtime)
        => OpenCore(path, runtime, testHooks: null);

    internal static SessionJournalEngine OpenForTest(
        string path,
        SessionRuntime runtime,
        SessionJournalTestHooks testHooks
    ) => OpenCore(path, runtime, testHooks);

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
            if (!Enum.IsDefined(typeof(SessionEventKind), frame.Header.OpaqueEventKind)) {
                throw new InvalidDataException($"Unknown SessionJournal event kind '{frame.Header.OpaqueEventKind}' at {address}.");
            }

            if (frame.Header.Hint != default(AddressHint)) {
                throw new InvalidDataException($"SessionJournal trunk requires EventAddress hint 0, got '{frame.Header.Hint}' at {address}.");
            }

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
        var projection = Project(cancellationToken);
        return projection.ExecutionState.Phase switch {
            SessionExecutionPhase.Empty or SessionExecutionPhase.Idle => new ResumeOutcome(Advanced: false),
            SessionExecutionPhase.AwaitingAssistantAction => ToResumeOutcome(
                await CompletePendingObservationAsync(observer, cancellationToken).ConfigureAwait(false)
            ),
            SessionExecutionPhase.AwaitingToolExecution => throw new NotSupportedException(
                "SessionJournal slice B does not execute tool calls; tool-loop recovery starts in slice C."
            ),
            _ => throw new InvalidOperationException($"Unknown SessionJournal execution phase '{projection.ExecutionState.Phase}'.")
        };
    }

    public EventAddress AppendObservation(string content) {
        ValidateRequired(content, nameof(content));
        return Append(SessionEventKind.ObservationAccepted, new ObservationAcceptedBody(content));
    }

    public EventAddress AppendAssistantAction(ActionMessage action, CompletionDescriptor invocation) {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(invocation);
        return Append(SessionEventKind.AssistantActionProduced, new AssistantActionProducedBody(action, invocation));
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
        SessionJournalTestHooks? testHooks
    ) {
        ArgumentNullException.ThrowIfNull(options);
        ValidateCreateOptions(options);

        var journal = EventJournal.EventJournal.CreateNew(path);
        try {
            journal.CreateBranch(SessionJournalDefaults.MainBranchName, startPoint: null).Unwrap();
            RefId mainRef = journal.OpenBranch(SessionJournalDefaults.MainBranchName).Unwrap();
            var engine = new SessionJournalEngine(journal, mainRef, runtime, testHooks);
            engine.Append(SessionEventKind.SessionCreated, options.ToConfiguration());
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
        SessionJournalTestHooks? testHooks
    ) {
        var journal = EventJournal.EventJournal.OpenExisting(path);
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
        if (projection.ExecutionState.Phase != SessionExecutionPhase.AwaitingAssistantAction) {
            throw new InvalidOperationException(
                $"Completion can resume only from '{SessionExecutionPhase.AwaitingAssistantAction}', got '{projection.ExecutionState.Phase}'."
            );
        }

        SessionConfiguration config = projection.Config
            ?? throw new InvalidDataException("SessionJournal projection is missing session configuration.");
        var request = new CompletionRequest(
            ModelId: config.ModelId,
            SystemPrompt: config.SystemPrompt,
            Context: projection.Context,
            Tools: ImmutableArray<ToolDefinition>.Empty
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
        AppendAssistantAction(result.Message, result.Invocation);
        return new TurnResult(result.Message, result.Invocation, FreezeErrors(result.Errors));
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

    private SessionRuntime RequireRuntime()
        => _runtime ?? throw new InvalidOperationException("SessionJournal runtime is required for SendAsync/ResumeAsync.");

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

    private static void ValidateCreateOptions(SessionCreateOptions options) {
        ValidateRequired(options.ModelId, nameof(options.ModelId));
        ValidateRequired(options.CompletionSurfaceId, nameof(options.CompletionSurfaceId));
        ValidateRequired(options.Schema, nameof(options.Schema));
        if (options.SystemPrompt is null) {
            throw new ArgumentNullException(nameof(options.SystemPrompt));
        }
    }

    private static void ValidateRequired(string value, string name) {
        if (string.IsNullOrWhiteSpace(value)) {
            throw new ArgumentException("Value must not be null, empty, or whitespace.", name);
        }
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
