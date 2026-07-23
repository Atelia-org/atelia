using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.Data;

namespace Atelia.SessionJournal;

public sealed class SessionJournalEngine : IDisposable {
    private readonly EventJournal.EventJournal _journal;
    private readonly RefId _mainRef;
    private bool _disposed;

    private SessionJournalEngine(EventJournal.EventJournal journal, RefId mainRef) {
        _journal = journal;
        _mainRef = mainRef;
    }

    public string Path => _journal.JournalPath;

    public static SessionJournalEngine Create(string path, SessionCreateOptions options) {
        ArgumentNullException.ThrowIfNull(options);
        ValidateCreateOptions(options);

        var journal = EventJournal.EventJournal.CreateNew(path);
        try {
            journal.CreateBranch(SessionJournalDefaults.MainBranchName, startPoint: null).Unwrap();
            RefId mainRef = journal.OpenBranch(SessionJournalDefaults.MainBranchName).Unwrap();
            var engine = new SessionJournalEngine(journal, mainRef);
            engine.Append(SessionEventKind.SessionCreated, new SessionCreatedBody(
                options.ModelId,
                options.SystemPrompt,
                options.CompletionSurfaceId,
                options.Schema
            ));
            return engine;
        }
        catch {
            journal.Dispose();
            throw;
        }
    }

    public static SessionJournalEngine Open(string path) {
        var journal = EventJournal.EventJournal.OpenExisting(path);
        try {
            RefId mainRef = journal.OpenBranch(SessionJournalDefaults.MainBranchName).Unwrap();
            return new SessionJournalEngine(journal, mainRef);
        }
        catch {
            journal.Dispose();
            throw;
        }
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

    private void ThrowIfDisposed() {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
