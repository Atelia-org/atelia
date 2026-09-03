using System.Collections.Immutable;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.EventJournal;

namespace Atelia.SessionJournal;

public sealed partial class SessionJournalEngine : IDisposable {
    public const string CanonicalRequestBytesMetricId =
        "atelia.session-journal.canonical-request-json-utf8-bytes.v1";

    private const string UnsupportedTailToolCallReason = "atelia.host.unsupported-tool-call";
    private const string InvalidCompletionInvocationReason =
        "atelia.host.invalid-completion-invocation";

    private static readonly EventJournalOptions DefaultJournalOptions = new() {
        PayloadCodecPolicy = EventPayloadCodecPolicy.Zlib
    };

    private readonly EventJournal.EventJournal _journal;
    private readonly SessionJournalEventReader _reader;
    private readonly string _branchName;
    private readonly RefId _branchRefId;
    private readonly bool _isReadOnly;
    private readonly SessionJournalTestHooks _testHooks;
    private readonly SessionJournalReadView _readView;
    private SessionRuntime? _runtime;
    private SessionGoverningSetup? _governingSetupCursor;
    private GoverningSetupResolutionDiagnostics _lastGoverningSetupResolutionDiagnostics;
    private SessionTailProjectionDiagnostics _lastTailProjectionDiagnostics;
    private bool _disposed;
    private int _reopenRequired;

    private SessionJournalEngine(
        EventJournal.EventJournal journal,
        string branchName,
        RefId branchRefId,
        bool isReadOnly,
        SessionRuntime? runtime,
        SessionJournalTestHooks? testHooks
    ) {
        _journal = journal;
        _reader = new SessionJournalEventReader(journal);
        _branchName = branchName;
        _branchRefId = branchRefId;
        _isReadOnly = isReadOnly;
        _runtime = runtime;
        _testHooks = testHooks ?? new SessionJournalTestHooks();
        _readView = new SessionJournalReadView(this);
    }

    public string Path => _journal.JournalPath;
    public string BranchName => _branchName;
    public RefId BranchRefId => _branchRefId;
    public bool IsReadOnly => _isReadOnly;

    /// <summary>
    /// Returns the engine-lifetime-bound minimum raw-authority view used by
    /// Derived integrations. This is intentionally not a general mirror of
    /// every read API on <see cref="SessionJournalEngine"/>. The same view
    /// instance is retained for the lifetime of this engine.
    /// </summary>
    public SessionJournalReadView ReadView {
        get {
            ThrowIfDisposed();
            return _readView;
        }
    }

    /// <summary>
    /// Reads the exact current head of the selected Ref without projecting
    /// history or decoding an event payload. Derived sidecars use this only
    /// as a stale-boundary guard around callbacks; raw authority remains in
    /// this engine.
    /// </summary>
    public EventAddress? ReadCurrentHead() {
        ThrowIfDisposed();
        return _journal.GetHead(_branchRefId);
    }

    internal bool MoveCurrentHeadForTest(
        EventAddress expectedOldHead,
        EventAddress? newHead
    ) {
        using MutationLease mutation = EnterMutation(
            nameof(MoveCurrentHeadForTest)
        );
        ThrowIfReadOnlyMutation(nameof(MoveCurrentHeadForTest));
        return _journal.MoveRef(
            _branchRefId,
            expectedOldHead,
            newHead
        ).Unwrap();
    }

    internal GoverningSetupResolutionDiagnostics LastGoverningSetupResolutionDiagnostics
        => _lastGoverningSetupResolutionDiagnostics;

    internal EventAddress? GoverningSetupCursorHeadForTest => _governingSetupCursor?.Head;

    internal SessionTailProjectionDiagnostics LastTailProjectionDiagnostics
        => _lastTailProjectionDiagnostics;

    internal SessionJournalReadDiagnostics CaptureReadDiagnostics()
        => _reader.CaptureDiagnostics();

    internal SessionJournalPayloadLifetimeDiagnostics
        CapturePayloadLifetimeDiagnostics()
        => _reader.CapturePayloadLifetimeDiagnostics();

    internal SessionExecutionRecovery ResolveExecutionTail(
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        return SessionExecutionTailResolver.Resolve(
            _reader,
            _journal.GetHead(_branchRefId),
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
        => CreateCore(
            path,
            options,
            SessionCreationOrigin.Native,
            runtime: null,
            testHooks: null
        );

    internal static SessionJournalEngine CreateForTest(
        string path,
        SessionCreateOptions options,
        SessionRuntime runtime,
        SessionJournalTestHooks testHooks
    ) => CreateCore(
        path,
        options,
        SessionCreationOrigin.Native,
        runtime,
        testHooks
    );

    internal static SessionJournalEngine CreateForTest(
        string path,
        SessionCreateOptions options,
        SessionRuntime? runtime,
        SessionJournalTestHooks testHooks,
        EventJournalOptions journalOptions
    ) => CreateCore(
        path,
        options,
        SessionCreationOrigin.Native,
        runtime,
        testHooks,
        journalOptions
    );

    public static SessionJournalEngine Open(string path)
        => OpenCore(
            path,
            SessionJournalDefaults.MainBranchName,
            runtime: null,
            testHooks: null
        );

    public static SessionJournalEngine Open(string path, string branchName)
        => OpenCore(path, branchName, runtime: null, testHooks: null);

    /// <summary>
    /// Opens the active main branch for strict read-only inspection without raw-tail recovery.
    /// </summary>
    public static SessionJournalEngine OpenReadOnly(string path)
        => OpenReadOnly(
            path,
            SessionJournalDefaults.MainBranchName
        );

    /// <summary>
    /// Opens one existing active branch for strict read-only inspection without raw-tail recovery.
    /// Runtime attachment, Send/Resume, and Append entrypoints fail before invoking collaborators
    /// or changing repository files.
    /// </summary>
    public static SessionJournalEngine OpenReadOnly(
        string path,
        string branchName
    ) => OpenReadOnlyCore(
        path,
        branchName,
        runtime: null,
        testHooks: null
    );

    internal static SessionJournalEngine OpenReadOnlyForTest(
        string path,
        SessionRuntime runtime,
        SessionJournalTestHooks? testHooks = null
    ) => OpenReadOnlyCore(
        path,
        SessionJournalDefaults.MainBranchName,
        runtime,
        testHooks
    );

    internal static SessionJournalEngine OpenReadOnlyForTest(
        string path,
        SessionJournalTestHooks testHooks
    ) => OpenReadOnlyCore(
        path,
        SessionJournalDefaults.MainBranchName,
        runtime: null,
        testHooks
    );

    private static SessionJournalEngine OpenReadOnlyCore(
        string path,
        string branchName,
        SessionRuntime? runtime,
        SessionJournalTestHooks? testHooks
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        var journal = EventJournal.EventJournal.OpenReadOnlyExisting(
            path,
            DefaultJournalOptions
        );
        try {
            RefId branchRefId = journal.OpenBranch(branchName).Unwrap();
            return new SessionJournalEngine(
                journal,
                branchName,
                branchRefId,
                isReadOnly: true,
                runtime,
                testHooks
            );
        }
        catch {
            journal.Dispose();
            throw;
        }
    }

    internal static SessionJournalEngine OpenForTest(
        string path,
        SessionRuntime runtime,
        SessionJournalTestHooks testHooks
    ) => OpenCore(
        path,
        SessionJournalDefaults.MainBranchName,
        runtime,
        testHooks
    );

    internal static SessionJournalEngine OpenForTest(
        string path,
        string branchName,
        SessionRuntime runtime,
        SessionJournalTestHooks testHooks
    ) => OpenCore(path, branchName, runtime, testHooks);

    internal static SessionJournalEngine OpenForTest(
        string path,
        SessionRuntime? runtime,
        SessionJournalTestHooks testHooks,
        EventJournalOptions journalOptions
    ) => OpenCore(
        path,
        SessionJournalDefaults.MainBranchName,
        runtime,
        testHooks,
        journalOptions
    );

    internal static SessionJournalEngine OpenForTest(
        string path,
        string branchName,
        SessionRuntime? runtime,
        SessionJournalTestHooks testHooks,
        EventJournalOptions journalOptions
    ) => OpenCore(
        path,
        branchName,
        runtime,
        testHooks,
        journalOptions
    );

    public void UseRuntime(SessionRuntime runtime) {
        using MutationLease mutation = EnterMutation(nameof(UseRuntime));
        ThrowIfReadOnlyMutation(nameof(UseRuntime));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    /// <summary>
    /// Performs an offline-only checked scan of the exact branch ref bound to
    /// this read-only engine. The scan validates the complete raw Parent
    /// lineage and every historical Prepared commitment before delivering
    /// normalized, non-context facts to <paramref name="visitor"/> in
    /// root-to-head order. Repository reads, validation, and exact-head
    /// execution-state recovery are complete before the first callback, so a
    /// later ref mutation cannot change this scan's captured snapshot. The
    /// visitor is not called when validation fails and must not dispose or
    /// re-enter this engine.
    /// </summary>
    public SessionJournalAuditScanResult ScanCheckedAuditEvents(
        Action<SessionJournalAuditEvent> visitor,
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(visitor);
        if (!_isReadOnly) {
            throw new InvalidOperationException(
                "Checked audit scan requires a read-only "
                + "SessionJournalEngine."
            );
        }
        return SessionJournalAuditScanner.Scan(
            _journal,
            _branchName,
            _branchRefId,
            visitor,
            _testHooks.AfterAuditSnapshotValidated,
            cancellationToken
        );
    }

    public SessionExecutionBoundaryInspection InspectExecutionBoundary(
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        SessionExecutionRecovery recovery =
            ResolveExecutionTail(cancellationToken);
        return new(
            recovery.Head,
            recovery.State.Phase,
            recovery.State.HeadKind
        );
    }

    /// <summary>
    /// Explicitly unbounded/offline inspection that captures the complete selected branch Parent
    /// lineage using event headers only. The returned order is head-to-root and is bound to one
    /// captured ref head; no payload is read or decoded. Online callers use
    /// <see cref="ReadCurrentLineagePrefix"/>.
    /// </summary>
    public SessionCurrentLineageSnapshot ReadCurrentLineageHeaders(
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        EventAddress capturedHead = _journal.GetHead(_branchRefId)
            ?? throw new InvalidOperationException(
                "Current-lineage inspection requires a non-empty SessionJournal."
            );
        SessionJournalReadDiagnostics before =
            _reader.CaptureDiagnostics();
        var entries = new List<SessionCurrentLineageHeader>();
        var visited = new HashSet<EventAddress>();
        EventAddress? cursor = capturedHead;
        while (cursor is { } address) {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(address)) {
                throw new InvalidDataException(
                    $"SessionJournal selected branch Parent chain contains a cycle at {address}."
                );
            }
            EventFrameHeader header =
                _reader.ReadEventHeaderPreview(address).Unwrap();
            ValidateSessionHeaderPreview(address, header);
            entries.Add(new SessionCurrentLineageHeader(
                address,
                header.Parent,
                (SessionEventKind)header.OpaqueEventKind
            ));
            cursor = header.Parent;
        }
        SessionJournalReadDiagnostics after =
            _reader.CaptureDiagnostics();
        return new SessionCurrentLineageSnapshot(
            capturedHead,
            entries.AsReadOnly(),
            new SessionCurrentLineageDiagnostics(
                after.HeaderPreviewReadCount
                    - before.HeaderPreviewReadCount,
                after.PayloadReadCount - before.PayloadReadCount,
                after.LogicalPayloadByteCount
                    - before.LogicalPayloadByteCount
            )
        );
    }

    /// <summary>
    /// Captures at most <paramref name="maxHeaderCount"/> selected-branch Parent headers from one
    /// current ref snapshot. A non-null continuation names the exact next Parent; this method does
    /// not auto-page and never reads event payloads.
    /// </summary>
    public SessionCurrentLineagePrefix ReadCurrentLineagePrefix(
        int maxHeaderCount,
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        EventAddress capturedHead = _journal.GetHead(_branchRefId)
            ?? throw new InvalidOperationException(
                "Current-lineage inspection requires a non-empty SessionJournal."
            );
        return ReadLineagePrefixAtCore(
            capturedHead,
            maxHeaderCount,
            cancellationToken
        );
    }

    /// <summary>
    /// Reads at most <paramref name="maxHeaderCount"/> Parent headers from one exact immutable raw
    /// address. The supplied address is the captured head; no current-ref substitution or hidden
    /// continuation read is performed.
    /// </summary>
    public SessionCurrentLineagePrefix ReadLineagePrefixAt(
        EventAddress capturedHead,
        int maxHeaderCount,
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        return ReadLineagePrefixAtCore(
            capturedHead,
            maxHeaderCount,
            cancellationToken
        );
    }

    private SessionCurrentLineagePrefix ReadLineagePrefixAtCore(
        EventAddress capturedHead,
        int maxHeaderCount,
        CancellationToken cancellationToken
    ) {
        if (capturedHead == default) {
            throw new ArgumentException(
                "The captured lineage head cannot be the default EventAddress.",
                nameof(capturedHead)
            );
        }
        if (maxHeaderCount <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(maxHeaderCount),
                "A bounded lineage read must allow at least one header."
            );
        }
        SessionJournalReadDiagnostics before =
            _reader.CaptureDiagnostics();
        var entries = new List<SessionCurrentLineageHeader>(
            Math.Min(maxHeaderCount, 1024)
        );
        var provenHeaders = new List<SessionProvenLineageHeader>(
            Math.Min(maxHeaderCount, 1024)
        );
        var visited = new HashSet<EventAddress>();
        EventAddress? cursor = capturedHead;
        while (cursor is { } address
               && entries.Count < maxHeaderCount) {
            cancellationToken.ThrowIfCancellationRequested();
            if (address == default) {
                throw new InvalidDataException(
                    "SessionJournal Parent chain contains a default EventAddress."
                );
            }
            if (!visited.Add(address)) {
                throw new InvalidDataException(
                    $"SessionJournal Parent chain contains a cycle at {address}."
                );
            }
            EventFrameHeader header =
                _reader.ReadEventHeaderPreview(address).Unwrap();
            ValidateSessionHeaderPreview(address, header);
            entries.Add(new SessionCurrentLineageHeader(
                address,
                header.Parent,
                (SessionEventKind)header.OpaqueEventKind
            ));
            provenHeaders.Add(new SessionProvenLineageHeader(
                address,
                header
            ));
            cursor = header.Parent;
        }
        if (cursor is { } next && visited.Contains(next)) {
            throw new InvalidDataException(
                $"SessionJournal Parent chain contains a cycle at {next}."
            );
        }
        SessionJournalReadDiagnostics after =
            _reader.CaptureDiagnostics();
        var diagnostics = new SessionCurrentLineageDiagnostics(
            after.HeaderPreviewReadCount
                - before.HeaderPreviewReadCount,
            after.PayloadReadCount - before.PayloadReadCount,
            after.LogicalPayloadByteCount
                - before.LogicalPayloadByteCount
        );
        return new SessionCurrentLineagePrefix(
            Path,
            capturedHead,
            maxHeaderCount,
            entries,
            cursor is { } nextAddress
                ? new SessionCurrentLineageContinuation(nextAddress)
                : null,
            diagnostics,
            new SessionCurrentLineagePrefixState(
                provenHeaders.AsReadOnly()
            )
        );
    }

    private SessionTargetLineageProof ReadLineageTargetProofAtCore(
        EventAddress capturedHead,
        EventAddress requiredAnchor,
        int maxHeaderCount,
        CancellationToken cancellationToken
    ) {
        if (capturedHead == default) {
            throw new ArgumentException(
                "The captured lineage head cannot be the default EventAddress.",
                nameof(capturedHead)
            );
        }
        if (requiredAnchor == default) {
            throw new ArgumentException(
                "The required lineage anchor cannot be the default EventAddress.",
                nameof(requiredAnchor)
            );
        }
        if (maxHeaderCount <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(maxHeaderCount),
                "A target lineage proof must allow at least one header."
            );
        }

        SessionJournalReadDiagnostics before =
            _reader.CaptureDiagnostics();
        var headers = new List<SessionProvenLineageHeader>(
            Math.Min(maxHeaderCount, 1024)
        );
        var visited = new HashSet<EventAddress>();
        EventAddress cursor = capturedHead;
        while (true) {
            cancellationToken.ThrowIfCancellationRequested();
            if (cursor == default) {
                throw new InvalidDataException(
                    "SessionJournal Parent chain contains a default EventAddress."
                );
            }
            if (!visited.Add(cursor)) {
                throw new InvalidDataException(
                    $"SessionJournal Parent chain contains a cycle at {cursor}."
                );
            }
            EventFrameHeader header =
                _reader.ReadEventHeaderPreview(cursor).Unwrap();
            ValidateSessionHeaderPreview(cursor, header);
            headers.Add(new SessionProvenLineageHeader(
                cursor,
                header
            ));

            if (cursor == requiredAnchor) {
                return CreateTargetLineageProof(
                    capturedHead,
                    requiredAnchor,
                    maxHeaderCount,
                    headers,
                    new SessionCurrentLineageAnchorLookup.Found(
                        headers.Count - 1
                    ),
                    before
                );
            }
            if (header.Parent is not EventAddress parent) {
                return CreateTargetLineageProof(
                    capturedHead,
                    requiredAnchor,
                    maxHeaderCount,
                    headers,
                    new SessionCurrentLineageAnchorLookup.OffLineage(
                        requiredAnchor,
                        capturedHead
                    ),
                    before
                );
            }
            if (visited.Contains(parent)) {
                throw new InvalidDataException(
                    $"SessionJournal Parent chain contains a cycle at {parent}."
                );
            }
            if (headers.Count == maxHeaderCount) {
                var evidence = new SessionCurrentLineageBeyondPrefix(
                    requiredAnchor,
                    capturedHead,
                    headers.Count,
                    parent
                );
                return CreateTargetLineageProof(
                    capturedHead,
                    requiredAnchor,
                    maxHeaderCount,
                    headers,
                    new SessionCurrentLineageAnchorLookup
                        .BeyondPrefix(evidence),
                    before
                );
            }
            cursor = parent;
        }
    }

    private SessionTargetLineageProof CreateTargetLineageProof(
        EventAddress capturedHead,
        EventAddress requiredAnchor,
        int maxHeaderCount,
        List<SessionProvenLineageHeader> headers,
        SessionCurrentLineageAnchorLookup lookup,
        SessionJournalReadDiagnostics before
    ) {
        SessionJournalReadDiagnostics after =
            _reader.CaptureDiagnostics();
        var diagnostics = new SessionCurrentLineageDiagnostics(
            after.HeaderPreviewReadCount
                - before.HeaderPreviewReadCount,
            after.PayloadReadCount - before.PayloadReadCount,
            after.LogicalPayloadByteCount
                - before.LogicalPayloadByteCount
        );
        if (diagnostics.HeaderVisits != headers.Count
            || diagnostics.PayloadReads != 0
            || diagnostics.DecodedPayloadBytes != 0) {
            throw new InvalidDataException(
                "A target lineage proof must remain header-only and account for every visited header."
            );
        }
        return new SessionTargetLineageProof(
            capturedHead,
            requiredAnchor,
            maxHeaderCount,
            headers.AsReadOnly(),
            lookup,
            diagnostics
        );
    }

    /// <summary>
    /// Explicitly unbounded/offline resolution of setup seeds for multiple selected-branch
    /// planning starts with one complete header walk. Only setup-event payloads are decoded;
    /// ordinary history payloads remain unread.
    /// </summary>
    public SessionHistoryPlanningSeedBatch ReadHistoryPlanningSeeds(
        IEnumerable<EventAddress> starts,
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(starts);
        EventAddress[] requested = [.. starts.Distinct()];
        if (requested.Any(static address => address == default)) {
            throw new ArgumentException(
                "Planning seed addresses cannot be default.",
                nameof(starts)
            );
        }
        EventAddress capturedHead = _journal.GetHead(_branchRefId)
            ?? throw new InvalidOperationException(
                "Planning seed resolution requires a non-empty SessionJournal."
            );
        SessionJournalReadDiagnostics before =
            _reader.CaptureDiagnostics();
        var headers = new List<SessionCurrentLineageHeader>();
        var visited = new HashSet<EventAddress>();
        EventAddress? cursor = capturedHead;
        while (cursor is { } address) {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(address)) {
                throw new InvalidDataException(
                    $"SessionJournal selected branch Parent chain contains a cycle at {address}."
                );
            }
            EventFrameHeader header =
                _reader.ReadEventHeaderPreview(address).Unwrap();
            ValidateSessionHeaderPreview(address, header);
            headers.Add(new(
                address,
                header.Parent,
                (SessionEventKind)header.OpaqueEventKind
            ));
            cursor = header.Parent;
        }
        var requestedSet = requested.ToHashSet();
        var seeds = new List<SessionHistoryPlanningSeed>(
            requested.Length
        );
        EventAddress? runtimeAddress = null;
        SessionRuntimeConfiguration? runtimeConfig = null;
        SessionContextSetupReference? runtimeReference = null;
        EventAddress? promptAddress = null;
        string? systemPrompt = null;
        SessionContextSetupReference? promptReference = null;
        for (int index = headers.Count - 1; index >= 0; index--) {
            cancellationToken.ThrowIfCancellationRequested();
            SessionCurrentLineageHeader header = headers[index];
            if (SessionOperationalSemantics.IsSetupKind(
                    header.Kind
                )) {
                using SessionJournalEventFrame frame =
                    _reader.ReadEvent(header.Address).Unwrap();
                ValidateSessionHeaderPreview(
                    header.Address,
                    frame.Header
                );
                object body = SessionEventCodec.Decode(
                    header.Kind,
                    frame.Payload,
                    out int schemaVersion
                );
                var reference =
                    new SessionContextSetupReference(
                        header.Address,
                        schemaVersion,
                        SessionRequestCanonicalizer.Sha256Hex(
                            frame.Payload
                        )
                    );
                if (header.Kind
                    == SessionEventKind.RuntimeConfigSetup) {
                    runtimeAddress = header.Address;
                    runtimeConfig = body
                        as SessionRuntimeConfiguration
                        ?? throw new InvalidDataException(
                            $"runtime-config-setup at {header.Address} decoded to an unexpected body."
                        );
                    runtimeReference = reference;
                }
                else {
                    promptAddress = header.Address;
                    systemPrompt = body is SystemPromptSetupBody prompt
                        ? prompt.Content
                        : throw new InvalidDataException(
                            $"system-prompt-setup at {header.Address} decoded to an unexpected body."
                        );
                    promptReference = reference;
                }
            }
            if (!requestedSet.Contains(header.Address)) {
                continue;
            }
            if (runtimeAddress is null
                || runtimeConfig is null
                || runtimeReference is null
                || promptAddress is null
                || systemPrompt is null
                || promptReference is null) {
                throw new InvalidDataException(
                    $"Planning start '{header.Address}' has no complete governing setup."
                );
            }
            seeds.Add(new SessionHistoryPlanningSeed(
                Path,
                header.Address,
                new(
                    runtimeReference,
                    promptReference
                ),
                new SessionGoverningSetup(
                    header.Address,
                    runtimeAddress.Value,
                    runtimeConfig,
                    promptAddress.Value,
                    systemPrompt
                )
            ));
        }
        if (seeds.Count != requested.Length) {
            throw new InvalidDataException(
                "One or more planning seed addresses are outside the selected branch lineage."
            );
        }
        SessionJournalReadDiagnostics after =
            _reader.CaptureDiagnostics();
        var diagnostics = new SessionCurrentLineageDiagnostics(
            after.HeaderPreviewReadCount
                - before.HeaderPreviewReadCount,
            after.PayloadReadCount - before.PayloadReadCount,
            after.LogicalPayloadByteCount
                - before.LogicalPayloadByteCount
        );
        return new SessionHistoryPlanningSeedBatch(
            new SessionCurrentLineageSnapshot(
                capturedHead,
                headers.AsReadOnly(),
                new(
                    headers.Count,
                    PayloadReads: 0,
                    DecodedPayloadBytes: 0
                )
            ),
            seeds.AsReadOnly(),
            diagnostics
        );
    }

    /// <summary>
    /// Explicitly unbounded/offline replay that reads the raw interval after a replay-safe start
    /// boundary and materializes
    /// dependency-closed history units for derived planning. A null start selects the unique
    /// SessionCreated event on the captured selected branch lineage. This API never constructs full history
    /// before the returned start boundary.
    /// </summary>
    public SessionHistoryPlanningWindow ReadHistoryPlanningWindow(
        EventAddress? startExclusive = null,
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        EventAddress observedHead = _journal.GetHead(_branchRefId)
            ?? throw new InvalidOperationException(
                "History planning requires a non-empty SessionJournal."
            );
        return ReadHistoryPlanningWindowAt(
            observedHead,
            startExclusive,
            cancellationToken
        );
    }

    /// <summary>
    /// Explicitly unbounded/offline replay of one dependency-closed planning interval at an exact
    /// historical head. The caller
    /// supplies the captured head rather than reading the current ref, allowing offline validators
    /// to reproduce an immutable epoch without constructing conversation history before the
    /// requested start. A null start resolves the SessionCreated boundary on that exact lineage.
    /// </summary>
    public SessionHistoryPlanningWindow ReadHistoryPlanningWindowAt(
        EventAddress capturedHead,
        EventAddress? startExclusive = null,
        CancellationToken cancellationToken = default
    ) => ReadHistoryPlanningWindowAtCore(
        capturedHead,
        startExclusive,
        planningSeed: null,
        cancellationToken
    );

    /// <summary>
    /// Explicitly unbounded/offline seeded replay at one exact historical head. The seed avoids
    /// setup and execution-state discovery before its boundary, but this overload does not impose
    /// a raw suffix limit; online migration uses
    /// <see cref="ReadHistoryPlanningWindowAtBounded(EventAddress,SessionHistoryPlanningSeed,int,CancellationToken)"/>.
    /// </summary>
    public SessionHistoryPlanningWindow ReadHistoryPlanningWindowAt(
        EventAddress capturedHead,
        SessionHistoryPlanningSeed planningSeed,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(planningSeed);
        return ReadHistoryPlanningWindowAtCore(
            capturedHead,
            planningSeed.Address,
            planningSeed,
            cancellationToken
        );
    }

    /// <summary>
    /// Proves an exact planning start within at most <paramref name="maxRawEventCount"/> raw events
    /// of <paramref name="capturedHead"/> before reading any payload. A farther start returns typed
    /// BeyondPrefix evidence with zero payload reads; this method never falls back to an unbounded
    /// root walk.
    /// </summary>
    public SessionHistoryPlanningWindowReadResult
        ReadHistoryPlanningWindowAtBounded(
        EventAddress capturedHead,
        EventAddress startExclusive,
        int maxRawEventCount,
        CancellationToken cancellationToken = default
    ) => ReadHistoryPlanningWindowAtBoundedCore(
        capturedHead,
        startExclusive,
        maxRawEventCount,
        planningSeed: null,
        cancellationToken
    );

    public SessionHistoryPlanningWindowReadResult
        ReadHistoryPlanningWindowAtBounded(
        EventAddress capturedHead,
        SessionHistoryPlanningSeed planningSeed,
        int maxRawEventCount,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(planningSeed);
        return ReadHistoryPlanningWindowAtBoundedCore(
            capturedHead,
            planningSeed.Address,
            maxRawEventCount,
            planningSeed,
            cancellationToken
        );
    }

    /// <summary>
    /// Proves an exact immutable planning interval using headers only. The returned token is
    /// repository-bound and can be materialized only with a matching authenticated start seed.
    /// </summary>
    public SessionHistoryPlanningWindowProofResult
        ProveHistoryPlanningWindowAtBounded(
        EventAddress capturedHead,
        EventAddress startExclusive,
        int maxRawEventCount,
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        if (startExclusive == default) {
            throw new ArgumentException(
                "History planning start cannot be the default EventAddress.",
                nameof(startExclusive)
            );
        }
        if (maxRawEventCount < 0 || maxRawEventCount == int.MaxValue) {
            throw new ArgumentOutOfRangeException(
                nameof(maxRawEventCount),
                "The bounded raw-event count must be non-negative and leave room for its start header."
            );
        }

        SessionJournalReadDiagnostics before =
            _reader.CaptureDiagnostics();
        SessionTargetLineageProof proof =
            ReadLineageTargetProofAtCore(
                capturedHead,
                startExclusive,
                maxRawEventCount + 1,
                cancellationToken
            );
        switch (proof.Lookup) {
            case SessionCurrentLineageAnchorLookup.BeyondPrefix beyond:
                return new SessionHistoryPlanningWindowProofResult
                    .BeyondPrefix(
                        beyond.Evidence,
                        proof.Diagnostics,
                        new SessionCurrentLineageLogicalCoverage(
                            beyond.Evidence.HeaderCount
                        )
                    );
            case SessionCurrentLineageAnchorLookup.OffLineage:
                throw new InvalidDataException(
                    "History planning start is not an ancestor of the captured raw head."
                );
            case SessionCurrentLineageAnchorLookup.Found found:
                _testHooks.AfterBoundedHistoryProof?.Invoke();
                var interval = new SessionProvenLineageHeader[
                    found.Index
                ];
                for (int index = 0; index < found.Index; index++) {
                    interval[index] =
                        proof.HeadThroughTargetOrLimit[index];
                }
                return new SessionHistoryPlanningWindowProofResult.Available(
                    new SessionHistoryPlanningWindowProof(
                        Path,
                        capturedHead,
                        startExclusive,
                        interval.Length,
                        proof.Diagnostics,
                        new SessionCurrentLineageLogicalCoverage(
                            interval.Length + 1
                        ),
                        new SessionHistoryPlanningWindowProofState(
                            interval,
                            before
                        )
                    )
                );
            default:
                throw new InvalidDataException(
                    "Unknown bounded lineage lookup result."
                );
        }
    }

    /// <summary>
    /// Produces one exact route proof from a previously captured repository-bound lineage
    /// prefix. No additional header or payload read is performed; the local raw-event cap is
    /// still enforced independently for this route step.
    /// </summary>
    public SessionHistoryPlanningWindowProofResult
        ProveHistoryPlanningWindowInPrefix(
        SessionCurrentLineagePrefix prefix,
        EventAddress capturedHead,
        EventAddress startExclusive,
        int maxRawEventCount
    ) {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(prefix);
        if (!PathsEqual(prefix.OwnerPath, Path)
            || prefix.State
                is not SessionCurrentLineagePrefixState state) {
            throw new ArgumentException(
                "Lineage prefix does not belong to this SessionJournal.",
                nameof(prefix)
            );
        }
        if (maxRawEventCount < 0
            || maxRawEventCount == int.MaxValue) {
            throw new ArgumentOutOfRangeException(
                nameof(maxRawEventCount)
            );
        }
        SessionCurrentLineageAnchorLookup endLookup =
            prefix.Lookup(capturedHead);
        if (endLookup
            is SessionCurrentLineageAnchorLookup.BeyondPrefix
                endBeyond) {
            return PrefixPlanningAnchorBeyond(endBeyond.Evidence);
        }
        if (endLookup
            is SessionCurrentLineageAnchorLookup.OffLineage) {
            throw new InvalidDataException(
                "Route endpoint is off the captured lineage."
            );
        }
        var end = (SessionCurrentLineageAnchorLookup.Found)endLookup;

        SessionCurrentLineageAnchorLookup startLookup =
            prefix.Lookup(startExclusive);
        if (startLookup
            is SessionCurrentLineageAnchorLookup.BeyondPrefix
                startBeyond) {
            return PrefixPlanningAnchorBeyond(startBeyond.Evidence);
        }
        if (startLookup
            is SessionCurrentLineageAnchorLookup.OffLineage) {
            throw new InvalidDataException(
                "Route start is off the captured lineage."
            );
        }
        var start = (SessionCurrentLineageAnchorLookup.Found)startLookup;
        if (end.Index >= start.Index) {
            throw new InvalidDataException(
                "Route start must be an ancestor of its endpoint."
            );
        }
        int rawEventCount = start.Index - end.Index;
        if (rawEventCount > maxRawEventCount) {
            int headerCount = maxRawEventCount + 1;
            SessionCurrentLineageHeader tail =
                prefix.HeadToOldest[end.Index + maxRawEventCount];
            EventAddress nextAddress = tail.Parent
                ?? throw new InvalidDataException(
                    "A capped route proof reached root before its known start."
                );
            return new SessionHistoryPlanningWindowProofResult
                .BeyondPrefix(
                    new SessionCurrentLineageBeyondPrefix(
                        startExclusive,
                        capturedHead,
                        headerCount,
                        nextAddress
                    ),
                    new SessionCurrentLineageDiagnostics(
                        HeaderVisits: 0,
                        PayloadReads: 0,
                        DecodedPayloadBytes: 0
                    ),
                    new SessionCurrentLineageLogicalCoverage(
                        headerCount
                    )
                );
        }
        SessionProvenLineageHeader[] interval = state.HeadToOldest
            .Skip(end.Index)
            .Take(rawEventCount)
            .ToArray();
        return new SessionHistoryPlanningWindowProofResult.Available(
            new SessionHistoryPlanningWindowProof(
                Path,
                capturedHead,
                startExclusive,
                rawEventCount,
                new SessionCurrentLineageDiagnostics(
                    HeaderVisits: 0,
                    PayloadReads: 0,
                    DecodedPayloadBytes: 0
                ),
                new SessionCurrentLineageLogicalCoverage(
                    rawEventCount + 1
                ),
                new SessionHistoryPlanningWindowProofState(
                    interval,
                    _reader.CaptureDiagnostics()
                )
            )
        );

        SessionHistoryPlanningWindowProofResult PrefixPlanningAnchorBeyond(
            SessionCurrentLineageBeyondPrefix evidence
        ) => new SessionHistoryPlanningWindowProofResult.BeyondPrefix(
            evidence,
            new SessionCurrentLineageDiagnostics(
                HeaderVisits: 0,
                PayloadReads: 0,
                DecodedPayloadBytes: 0
            ),
            new SessionCurrentLineageLogicalCoverage(
                evidence.HeaderCount
            )
        );
    }

    /// <summary>
    /// Materializes a previously header-proved interval with one authenticated matching seed.
    /// No lineage search or hidden continuation read is performed.
    /// </summary>
    public SessionHistoryPlanningWindow MaterializeHistoryPlanningWindow(
        SessionHistoryPlanningWindowProof proof,
        SessionHistoryPlanningSeed planningSeed,
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(planningSeed);
        if (!PathsEqual(proof.OwnerPath, Path)
            || proof.State
                is not SessionHistoryPlanningWindowProofState state) {
            throw new ArgumentException(
                "History planning proof does not belong to this SessionJournal.",
                nameof(proof)
            );
        }
        if (planningSeed.Address != proof.StartExclusive) {
            throw new ArgumentException(
                "Planning seed does not match the proven history start.",
                nameof(planningSeed)
            );
        }
        SessionHistoryPlanningWindow window =
            MaterializeHistoryPlanningWindow(
                proof.CapturedHead,
                proof.StartExclusive,
                planningSeed,
                state.HeadToStartExclusive,
                state.DiagnosticsBeforeProof,
                verifyBoundedProof: true,
                cancellationToken
            );
        if (window.RawAddresses.Count != proof.RawEventCount) {
            throw new InvalidDataException(
                "The materialized history planning interval did not match its header proof."
            );
        }
        return window;
    }

    /// <summary>
    /// Uses only the headers retained by an exact planning-window proof to verify the governing
    /// setup-address transition from its authenticated start boundary to its exact end. Payload
    /// schema/hash validation remains deferred to materialization.
    /// </summary>
    public void ValidateGoverningSetupTransition(
        SessionHistoryPlanningWindowProof proof,
        SessionContextAnchorSetupReferences startSetups,
        SessionContextAnchorSetupReferences expectedEndSetups
    ) {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(startSetups);
        ArgumentNullException.ThrowIfNull(expectedEndSetups);
        if (!PathsEqual(proof.OwnerPath, Path)
            || proof.State
                is not SessionHistoryPlanningWindowProofState state) {
            throw new ArgumentException(
                "History planning proof does not belong to this SessionJournal.",
                nameof(proof)
            );
        }
        EventAddress runtime = startSetups.RuntimeConfig.Address;
        EventAddress prompt = startSetups.SystemPrompt.Address;
        for (int index = state.HeadToStartExclusive.Count - 1;
             index >= 0;
             index--) {
            SessionProvenLineageHeader header =
                state.HeadToStartExclusive[index];
            switch ((SessionEventKind)header.Header.OpaqueEventKind) {
                case SessionEventKind.RuntimeConfigSetup:
                    runtime = header.Address;
                    break;
                case SessionEventKind.SystemPromptSetup:
                    prompt = header.Address;
                    break;
            }
        }
        if (runtime != expectedEndSetups.RuntimeConfig.Address
            || prompt != expectedEndSetups.SystemPrompt.Address) {
            throw new InvalidDataException(
                $"The header-proved governing setup at '{proof.CapturedHead}' "
                + "does not match the frozen endpoint setup addresses."
            );
        }
    }

    /// <summary>
    /// Converts one repository-bound route proof into an opaque governing-setup proof for its
    /// endpoint. This validates setup-address transitions from retained headers only.
    /// </summary>
    public SessionGoverningSetupProof
        ProveGoverningSetupTransition(
        SessionHistoryPlanningWindowProof proof,
        SessionGoverningSetupProof startProof,
        SessionContextAnchorSetupReferences expectedEndSetups
    ) {
        ArgumentNullException.ThrowIfNull(startProof);
        if (!PathsEqual(startProof.OwnerPath, Path)
            || startProof.State
                is not SessionGoverningSetupProofState state
            || state.Boundary != startProof.Boundary
            || state.ExpectedSetups != startProof.ExpectedSetups
            || startProof.Boundary != proof.StartExclusive) {
            throw new ArgumentException(
                "Route start setup proof does not match the proven interval.",
                nameof(startProof)
            );
        }
        ValidateGoverningSetupTransition(
            proof,
            startProof.ExpectedSetups,
            expectedEndSetups
        );
        return new SessionGoverningSetupProof(
            Path,
            proof.CapturedHead,
            expectedEndSetups,
            new SessionCurrentLineageDiagnostics(
                HeaderVisits: 0,
                PayloadReads: 0,
                DecodedPayloadBytes: 0
            ),
            proof.LogicalCoverage,
            new SessionGoverningSetupProofState(
                proof.CapturedHead,
                expectedEndSetups
            )
        );
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            System.IO.Path.GetFullPath(left),
            System.IO.Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal
        );

    /// <summary>
    /// Proves with headers only that the first runtime-config and system-prompt setup events on
    /// the Parent lineage of one exact immutable boundary have the expected addresses. A bounded
    /// miss returns an explicit continuation and never auto-pages or falls back to a root walk.
    /// </summary>
    public SessionGoverningSetupProofResult
        ProveGoverningSetupAtBounded(
        EventAddress boundary,
        SessionContextAnchorSetupReferences expectedSetups,
        int maxHeaderCount,
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        if (boundary == default) {
            throw new ArgumentException(
                "The governing setup boundary cannot be the default EventAddress.",
                nameof(boundary)
            );
        }
        ArgumentNullException.ThrowIfNull(expectedSetups);
        ArgumentNullException.ThrowIfNull(expectedSetups.RuntimeConfig);
        ArgumentNullException.ThrowIfNull(expectedSetups.SystemPrompt);
        if (expectedSetups.RuntimeConfig.Address == default
            || expectedSetups.SystemPrompt.Address == default) {
            throw new ArgumentException(
                "Expected governing setup addresses cannot be default.",
                nameof(expectedSetups)
            );
        }
        if (expectedSetups.RuntimeConfig.Address
            == expectedSetups.SystemPrompt.Address) {
            throw new ArgumentException(
                "Runtime-config and system-prompt setup addresses must be distinct.",
                nameof(expectedSetups)
            );
        }
        if (maxHeaderCount <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(maxHeaderCount),
                "A bounded governing setup proof must allow at least one header."
            );
        }

        SessionJournalReadDiagnostics before =
            _reader.CaptureDiagnostics();
        var visited = new HashSet<EventAddress>();
        EventAddress cursor = boundary;
        int headerCount = 0;
        bool foundRuntime = false;
        bool foundPrompt = false;
        while (true) {
            cancellationToken.ThrowIfCancellationRequested();
            if (cursor == default) {
                throw new InvalidDataException(
                    "SessionJournal Parent chain contains a default EventAddress."
                );
            }
            if (!visited.Add(cursor)) {
                throw new InvalidDataException(
                    $"SessionJournal Parent chain contains a cycle at {cursor}."
                );
            }
            EventFrameHeader header =
                _reader.ReadEventHeaderPreview(cursor).Unwrap();
            ValidateSessionHeaderPreview(cursor, header);
            headerCount++;
            var kind = (SessionEventKind)header.OpaqueEventKind;
            if (kind == SessionEventKind.RuntimeConfigSetup
                && !foundRuntime) {
                if (cursor != expectedSetups.RuntimeConfig.Address) {
                    throw new InvalidDataException(
                        $"The first runtime-config-setup governing exact boundary {boundary} "
                        + $"is {cursor}, not expected address {expectedSetups.RuntimeConfig.Address}."
                    );
                }
                foundRuntime = true;
            }
            else if (kind == SessionEventKind.SystemPromptSetup
                     && !foundPrompt) {
                if (cursor != expectedSetups.SystemPrompt.Address) {
                    throw new InvalidDataException(
                        $"The first system-prompt-setup governing exact boundary {boundary} "
                        + $"is {cursor}, not expected address {expectedSetups.SystemPrompt.Address}."
                    );
                }
                foundPrompt = true;
            }

            if (foundRuntime && foundPrompt) {
                SessionCurrentLineageDiagnostics diagnostics =
                    CaptureHeaderOnlyProofDiagnostics(
                        before,
                        headerCount,
                        "A governing setup proof"
                    );
                return new SessionGoverningSetupProofResult.Available(
                    new SessionGoverningSetupProof(
                        Path,
                        boundary,
                        expectedSetups,
                        diagnostics,
                        new SessionCurrentLineageLogicalCoverage(
                            headerCount
                        ),
                        new SessionGoverningSetupProofState(
                            boundary,
                            expectedSetups
                        )
                    )
                );
            }

            if (header.Parent is not EventAddress parent) {
                throw new InvalidDataException(
                    $"SessionJournal governing setup for exact boundary {boundary} "
                    + "is missing a runtime-config-setup or system-prompt-setup on its Parent chain."
                );
            }
            if (visited.Contains(parent)) {
                throw new InvalidDataException(
                    $"SessionJournal Parent chain contains a cycle at {parent}."
                );
            }
            if (headerCount == maxHeaderCount) {
                SessionCurrentLineageDiagnostics diagnostics =
                    CaptureHeaderOnlyProofDiagnostics(
                        before,
                        headerCount,
                        "A governing setup proof"
                    );
                return new SessionGoverningSetupProofResult.BeyondPrefix(
                    new SessionGoverningSetupBeyondPrefix(
                        boundary,
                        expectedSetups,
                        boundary,
                        headerCount,
                        parent,
                        foundRuntime
                            ? expectedSetups.SystemPrompt.Address
                            : expectedSetups.RuntimeConfig.Address
                    ),
                    diagnostics,
                    new SessionCurrentLineageLogicalCoverage(
                        headerCount
                    )
                );
            }
            cursor = parent;
        }
    }

    /// <summary>
    /// Produces a direct governing-setup proof from one previously captured repository-bound
    /// lineage prefix. The first setup headers governing the boundary must be present in that
    /// exact prefix; no continuation is followed and no payload is read.
    /// </summary>
    public SessionGoverningSetupProofResult
        ProveGoverningSetupInPrefix(
        SessionCurrentLineagePrefix prefix,
        EventAddress boundary,
        SessionContextAnchorSetupReferences expectedSetups
    ) {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(expectedSetups);
        ValidateExpectedSetupReferences(expectedSetups);
        if (!PathsEqual(prefix.OwnerPath, Path)
            || prefix.State
                is not SessionCurrentLineagePrefixState) {
            throw new ArgumentException(
                "Lineage prefix does not belong to this SessionJournal.",
                nameof(prefix)
            );
        }
        SessionCurrentLineageAnchorLookup boundaryLookup =
            prefix.Lookup(boundary);
        if (boundaryLookup
            is SessionCurrentLineageAnchorLookup.BeyondPrefix beyond) {
            SessionCurrentLineageBeyondPrefix evidence = beyond.Evidence;
            return new SessionGoverningSetupProofResult.BeyondPrefix(
                new SessionGoverningSetupBeyondPrefix(
                    boundary,
                    expectedSetups,
                    evidence.CapturedHead,
                    evidence.HeaderCount,
                    evidence.NextAddress,
                    boundary
                ),
                new SessionCurrentLineageDiagnostics(
                    HeaderVisits: 0,
                    PayloadReads: 0,
                    DecodedPayloadBytes: 0
                ),
                new SessionCurrentLineageLogicalCoverage(
                    evidence.HeaderCount
                )
            );
        }
        if (boundaryLookup
            is SessionCurrentLineageAnchorLookup.OffLineage) {
            throw new InvalidDataException(
                "Governing setup boundary is off the captured lineage."
            );
        }
        var found =
            (SessionCurrentLineageAnchorLookup.Found)boundaryLookup;
        bool foundRuntime = false;
        bool foundPrompt = false;
        int headerCount = 0;
        for (int index = found.Index;
             index < prefix.HeadToOldest.Count;
             index++) {
            SessionCurrentLineageHeader header =
                prefix.HeadToOldest[index];
            headerCount++;
            if (header.Kind == SessionEventKind.RuntimeConfigSetup
                && !foundRuntime) {
                if (header.Address
                    != expectedSetups.RuntimeConfig.Address) {
                    throw new InvalidDataException(
                        $"The first runtime-config-setup governing exact boundary {boundary} "
                        + $"is {header.Address}, not expected address "
                        + $"{expectedSetups.RuntimeConfig.Address}."
                    );
                }
                foundRuntime = true;
            }
            else if (header.Kind
                     == SessionEventKind.SystemPromptSetup
                     && !foundPrompt) {
                if (header.Address
                    != expectedSetups.SystemPrompt.Address) {
                    throw new InvalidDataException(
                        $"The first system-prompt-setup governing exact boundary {boundary} "
                        + $"is {header.Address}, not expected address "
                        + $"{expectedSetups.SystemPrompt.Address}."
                    );
                }
                foundPrompt = true;
            }
            if (foundRuntime && foundPrompt) {
                var diagnostics =
                    new SessionCurrentLineageDiagnostics(
                        HeaderVisits: 0,
                        PayloadReads: 0,
                        DecodedPayloadBytes: 0
                    );
                return new SessionGoverningSetupProofResult.Available(
                    new SessionGoverningSetupProof(
                        Path,
                        boundary,
                        expectedSetups,
                        diagnostics,
                        new SessionCurrentLineageLogicalCoverage(
                            headerCount
                        ),
                        new SessionGoverningSetupProofState(
                            boundary,
                            expectedSetups
                        )
                    )
                );
            }
        }
        if (prefix.Continuation is { } continuation) {
            EventAddress requiredAnchor = !foundRuntime
                ? expectedSetups.RuntimeConfig.Address
                : expectedSetups.SystemPrompt.Address;
            return new SessionGoverningSetupProofResult.BeyondPrefix(
                new SessionGoverningSetupBeyondPrefix(
                    boundary,
                    expectedSetups,
                    prefix.CapturedHead,
                    prefix.HeadToOldest.Count,
                    continuation.NextAddress,
                    requiredAnchor
                ),
                new SessionCurrentLineageDiagnostics(
                    HeaderVisits: 0,
                    PayloadReads: 0,
                    DecodedPayloadBytes: 0
                ),
                new SessionCurrentLineageLogicalCoverage(
                    headerCount
                )
            );
        }
        throw new InvalidDataException(
            $"SessionJournal governing setup for exact boundary {boundary} "
            + "is missing a runtime-config-setup or system-prompt-setup on its Parent chain."
        );
    }

    /// <summary>
    /// Authenticates the setup payload identities named by opaque governing proofs. All proof
    /// ownership and conflicting-reference checks complete before the first payload read; each
    /// distinct setup address is then read exactly once and validated by kind, schema, and hash.
    /// Boundary and history payloads are never read by this method.
    /// </summary>
    public void ValidateGoverningSetupPayloads(
        IEnumerable<SessionGoverningSetupProof> proofs,
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(proofs);
        SessionGoverningSetupProof[] materialized = [.. proofs];
        foreach (SessionGoverningSetupProof proof in materialized) {
            ArgumentNullException.ThrowIfNull(proof);
            if (!PathsEqual(proof.OwnerPath, Path)
                || proof.State
                    is not SessionGoverningSetupProofState state
                || state.Boundary != proof.Boundary
                || state.ExpectedSetups != proof.ExpectedSetups) {
                throw new ArgumentException(
                    "Governing setup proof does not belong to this SessionJournal.",
                    nameof(proofs)
                );
            }
            ValidateExpectedSetupReferences(proof.ExpectedSetups);
        }
        var references = new Dictionary<
            EventAddress,
            (SessionContextSetupReference Reference,
                SessionEventKind Kind)
        >();
        foreach (SessionGoverningSetupProof proof in materialized) {
            AddSetupReference(
                references,
                proof.ExpectedSetups.RuntimeConfig,
                SessionEventKind.RuntimeConfigSetup
            );
            AddSetupReference(
                references,
                proof.ExpectedSetups.SystemPrompt,
                SessionEventKind.SystemPromptSetup
            );
        }
        foreach ((SessionContextSetupReference reference,
                     SessionEventKind kind) in references
                     .OrderBy(static item => item.Key.SegmentNumber)
                     .ThenBy(static item => item.Key.Ticket.Packed)
                     .ThenBy(static item => item.Key.Hint.Packed)
                     .Select(static item => item.Value)) {
            if (kind == SessionEventKind.RuntimeConfigSetup) {
                _ = ReadAndValidatePlanningSetupReference<
                    SessionRuntimeConfiguration
                >(reference, kind, cancellationToken);
            }
            else {
                _ = ReadAndValidatePlanningSetupReference<
                    SystemPromptSetupBody
                >(reference, kind, cancellationToken);
            }
        }
    }

    private static void AddSetupReference(
        IDictionary<
            EventAddress,
            (SessionContextSetupReference Reference,
                SessionEventKind Kind)
        > references,
        SessionContextSetupReference reference,
        SessionEventKind kind
    ) {
        if (references.TryGetValue(
                reference.Address,
                out var existing
            )) {
            if (existing.Reference != reference
                || existing.Kind != kind) {
                throw new InvalidDataException(
                    $"Setup address '{reference.Address}' has conflicting frozen identity."
                );
            }
            return;
        }
        references.Add(reference.Address, (reference, kind));
    }

    private static void ValidateExpectedSetupReferences(
        SessionContextAnchorSetupReferences setups
    ) {
        ArgumentNullException.ThrowIfNull(setups);
        if (setups.RuntimeConfig is null
            || setups.SystemPrompt is null) {
            throw new InvalidDataException(
                "Governing setup proof contains a null setup reference."
            );
        }
        ValidateExpectedSetupReference(
            setups.RuntimeConfig,
            "runtime-config"
        );
        ValidateExpectedSetupReference(
            setups.SystemPrompt,
            "system-prompt"
        );
        if (setups.RuntimeConfig.Address
            == setups.SystemPrompt.Address) {
            throw new InvalidDataException(
                "Runtime-config and system-prompt setup references must use distinct addresses."
            );
        }
    }

    private static void ValidateExpectedSetupReference(
        SessionContextSetupReference reference,
        string label
    ) {
        if (reference.Address == default) {
            throw new InvalidDataException(
                $"Frozen {label} setup address cannot be default."
            );
        }
        if (reference.BodySchemaVersion <= 0) {
            throw new InvalidDataException(
                $"Frozen {label} setup schema version must be positive."
            );
        }
        if (reference.PayloadSha256 is null
            || reference.PayloadSha256.Length != 64
            || reference.PayloadSha256.Any(static ch =>
                !((ch >= '0' && ch <= '9')
                  || (ch >= 'a' && ch <= 'f')))) {
            throw new InvalidDataException(
                $"Frozen {label} setup hash must be lowercase SHA-256 hex."
            );
        }
    }

    /// <summary>
    /// Materializes an opaque governing setup proof into a verified replay-safe planning seed.
    /// This is the first phase that reads setup or boundary payloads and validates schema/hash.
    /// </summary>
    public SessionHistoryPlanningSeed MaterializeHistoryPlanningSeed(
        SessionGoverningSetupProof proof,
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(proof);
        if (!PathsEqual(proof.OwnerPath, Path)
            || proof.State
                is not SessionGoverningSetupProofState state
            || state.Boundary != proof.Boundary
            || state.ExpectedSetups != proof.ExpectedSetups) {
            throw new ArgumentException(
                "Governing setup proof does not belong to this SessionJournal.",
                nameof(proof)
            );
        }
        return CreateHistoryPlanningSeed(
            proof.Boundary,
            proof.ExpectedSetups,
            cancellationToken
        );
    }

    private SessionCurrentLineageDiagnostics
        CaptureHeaderOnlyProofDiagnostics(
        SessionJournalReadDiagnostics before,
        int expectedHeaderCount,
        string operation
    ) {
        SessionJournalReadDiagnostics after =
            _reader.CaptureDiagnostics();
        var diagnostics = new SessionCurrentLineageDiagnostics(
            after.HeaderPreviewReadCount
                - before.HeaderPreviewReadCount,
            after.PayloadReadCount - before.PayloadReadCount,
            after.LogicalPayloadByteCount
                - before.LogicalPayloadByteCount
        );
        if (diagnostics.HeaderVisits != expectedHeaderCount
            || diagnostics.PayloadReads != 0
            || diagnostics.DecodedPayloadBytes != 0) {
            throw new InvalidDataException(
                $"{operation} must remain header-only and account for every visited header."
            );
        }
        return diagnostics;
    }

    /// <summary>
    /// Locates the canonical SessionCreated planning boundary within one bounded raw suffix. The
    /// complete prefix is proved with headers only; setup and SessionCreated payloads are read only
    /// after the boundary is found. This method never auto-pages or falls back to a root walk.
    /// </summary>
    public SessionCreatedPlanningSeedReadResult
        ReadSessionCreatedPlanningSeedAtBounded(
        EventAddress capturedHead,
        int maxRawEventCount,
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        if (capturedHead == default) {
            throw new ArgumentException(
                "The captured lineage head cannot be the default EventAddress.",
                nameof(capturedHead)
            );
        }
        if (maxRawEventCount < 0 || maxRawEventCount == int.MaxValue) {
            throw new ArgumentOutOfRangeException(
                nameof(maxRawEventCount),
                "The bounded raw-event count must be non-negative and leave room for the SessionCreated header."
            );
        }

        SessionCurrentLineagePrefix prefix = ReadLineagePrefixAtCore(
            capturedHead,
            maxRawEventCount + 1,
            cancellationToken
        );
        for (int index = 0; index < prefix.HeadToOldest.Count; index++) {
            SessionCurrentLineageHeader header =
                prefix.HeadToOldest[index];
            if (header.Kind != SessionEventKind.SessionCreated) {
                continue;
            }

            _testHooks.AfterBoundedHistoryProof?.Invoke();
            SessionContextAnchorSetupReferences setups =
                ResolveContextAnchorSetupReferences(
                    header.Address,
                    cancellationToken
                );
            SessionHistoryPlanningSeed seed =
                CreateHistoryPlanningSeed(
                    header.Address,
                    setups,
                    cancellationToken
                );
            return new SessionCreatedPlanningSeedReadResult.Available(
                seed,
                index,
                prefix.Diagnostics
            );
        }

        if (prefix.Continuation is { } continuation) {
            return new SessionCreatedPlanningSeedReadResult.BeyondPrefix(
                prefix.CapturedHead,
                prefix.HeadToOldest.Count,
                continuation.NextAddress,
                prefix.Diagnostics
            );
        }
        throw new InvalidDataException(
            "SessionJournal lineage has no SessionCreated planning boundary."
        );
    }

    private SessionHistoryPlanningWindowReadResult
        ReadHistoryPlanningWindowAtBoundedCore(
        EventAddress capturedHead,
        EventAddress startExclusive,
        int maxRawEventCount,
        SessionHistoryPlanningSeed? planningSeed,
        CancellationToken cancellationToken
    ) {
        ThrowIfDisposed();
        if (startExclusive == default) {
            throw new ArgumentException(
                "History planning start cannot be the default EventAddress.",
                nameof(startExclusive)
            );
        }
        if (maxRawEventCount < 0 || maxRawEventCount == int.MaxValue) {
            throw new ArgumentOutOfRangeException(
                nameof(maxRawEventCount),
                "The bounded raw-event count must be non-negative and leave room for its start header."
            );
        }

        SessionJournalReadDiagnostics before =
            _reader.CaptureDiagnostics();
        SessionTargetLineageProof proof =
            ReadLineageTargetProofAtCore(
                capturedHead,
                startExclusive,
                maxRawEventCount + 1,
                cancellationToken
            );
        switch (proof.Lookup) {
            case SessionCurrentLineageAnchorLookup.BeyondPrefix beyond:
                return new SessionHistoryPlanningWindowReadResult
                    .BeyondPrefix(beyond.Evidence, proof.Diagnostics);
            case SessionCurrentLineageAnchorLookup.OffLineage:
                throw new InvalidDataException(
                    "History planning start is not an ancestor of the captured raw head."
                );
            case SessionCurrentLineageAnchorLookup.Found found:
                _testHooks.AfterBoundedHistoryProof?.Invoke();
                var interval = new SessionProvenLineageHeader[
                    found.Index
                ];
                for (int index = 0; index < found.Index; index++) {
                    interval[index] =
                        proof.HeadThroughTargetOrLimit[index];
                }
                SessionHistoryPlanningWindow window =
                    MaterializeHistoryPlanningWindow(
                        capturedHead,
                        startExclusive,
                        planningSeed,
                        interval,
                        before,
                        verifyBoundedProof: true,
                        cancellationToken
                    );
                if (window.RawAddresses.Count
                    > maxRawEventCount) {
                    throw new InvalidDataException(
                        "The materialized history planning interval exceeded its proven raw-event bound."
                    );
                }
                return new SessionHistoryPlanningWindowReadResult
                    .Available(window, proof.Diagnostics);
            default:
                throw new InvalidDataException(
                    "Unknown bounded lineage lookup result."
                );
        }
    }

    /// <summary>
    /// Rehydrates one durable exact setup checkpoint into a repository-bound planning seed.
    /// This reads only the two referenced setup payloads and the bounded execution dependency
    /// closure at <paramref name="startExclusive"/>; it never searches toward the raw root for
    /// governing configuration.
    /// </summary>
    public SessionHistoryPlanningSeed CreateHistoryPlanningSeed(
        EventAddress startExclusive,
        SessionContextAnchorSetupReferences setups,
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        if (startExclusive == default) {
            throw new ArgumentException(
                "Planning seed address cannot be default.",
                nameof(startExclusive)
            );
        }
        ArgumentNullException.ThrowIfNull(setups);
        SessionRuntimeConfiguration runtime =
            ReadAndValidatePlanningSetupReference<
                SessionRuntimeConfiguration
            >(
                setups.RuntimeConfig,
                SessionEventKind.RuntimeConfigSetup,
                cancellationToken
            );
        SystemPromptSetupBody prompt =
            ReadAndValidatePlanningSetupReference<SystemPromptSetupBody>(
                setups.SystemPrompt,
                SessionEventKind.SystemPromptSetup,
                cancellationToken
            );
        SessionExecutionRecovery executionRecovery =
            SessionTailContextProjection.ValidateReplaySafeBoundary(
                _reader,
                startExclusive,
                cancellationToken
            );
        return new SessionHistoryPlanningSeed(
            Path,
            startExclusive,
            setups,
            new SessionGoverningSetup(
                startExclusive,
                setups.RuntimeConfig.Address,
                runtime,
                setups.SystemPrompt.Address,
                prompt.Content
            ),
            executionRecovery
        );
    }

    private SessionHistoryPlanningWindow
        ReadHistoryPlanningWindowAtCore(
        EventAddress capturedHead,
        EventAddress? startExclusive,
        SessionHistoryPlanningSeed? planningSeed,
        CancellationToken cancellationToken
    ) {
        ThrowIfDisposed();
        if (capturedHead == default) {
            throw new ArgumentException(
                "Historical planning head cannot be the default EventAddress.",
                nameof(capturedHead)
            );
        }
        if (startExclusive == default(EventAddress)) {
            throw new ArgumentException(
                "History planning start cannot be the default EventAddress.",
                nameof(startExclusive)
            );
        }
        SessionJournalReadDiagnostics before =
            _reader.CaptureDiagnostics();
        var reverseHeaders = new List<SessionProvenLineageHeader>();
        var visited = new HashSet<EventAddress>();
        EventAddress? cursor = capturedHead;
        EventAddress? resolvedStart = null;
        while (cursor is { } address) {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(address)) {
                throw new InvalidDataException(
                    $"SessionJournal Parent chain contains a cycle at {address}."
                );
            }
            EventFrameHeader header =
                _reader.ReadEventHeaderPreview(address).Unwrap();
            ValidateSessionHeaderPreview(address, header);
            var kind = (SessionEventKind)header.OpaqueEventKind;
            if (startExclusive is { } requestedStart) {
                if (address == requestedStart) {
                    resolvedStart = address;
                    break;
                }
            }
            else if (kind == SessionEventKind.SessionCreated) {
                resolvedStart = address;
                break;
            }
            reverseHeaders.Add(new SessionProvenLineageHeader(
                address,
                header
            ));
            cursor = header.Parent;
        }
        if (resolvedStart is null) {
            throw new InvalidDataException(
                startExclusive is null
                    ? "SessionJournal lineage has no SessionCreated planning boundary."
                    : "History planning start is not an ancestor of the captured raw head."
            );
        }

        return MaterializeHistoryPlanningWindow(
            capturedHead,
            resolvedStart.Value,
            planningSeed,
            reverseHeaders,
            before,
            verifyBoundedProof: false,
            cancellationToken
        );
    }

    private SessionHistoryPlanningWindow
        MaterializeHistoryPlanningWindow(
        EventAddress capturedHead,
        EventAddress resolvedStart,
        SessionHistoryPlanningSeed? planningSeed,
        IReadOnlyList<SessionProvenLineageHeader>
            headToStartExclusive,
        SessionJournalReadDiagnostics before,
        bool verifyBoundedProof,
        CancellationToken cancellationToken
    ) {
        if (planningSeed is not null
            && (planningSeed.Address != resolvedStart
                || !string.Equals(
                    System.IO.Path.GetFullPath(
                        planningSeed.OwnerPath
                    ),
                    System.IO.Path.GetFullPath(Path),
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal
                ))) {
            throw new ArgumentException(
                "Planning seed does not belong to this SessionJournal boundary.",
                nameof(planningSeed)
            );
        }
        SessionExecutionRecovery executionSeed =
            planningSeed?.ExecutionRecovery
            ?? SessionTailContextProjection.ValidateReplaySafeBoundary(
                _reader,
                resolvedStart,
                cancellationToken
            );
        SessionProvenLineageHeader[] chronologicalHeaders =
            headToStartExclusive.Reverse().ToArray();
        var rawAddresses = new List<EventAddress>(
            chronologicalHeaders.Length
        );
        var events = new List<DecodedSessionEvent>(
            chronologicalHeaders.Length
        );
        var rawHashEntries =
            new List<SessionRawRangeHashEntry>(
                chronologicalHeaders.Length
            );
        var suffixSetupReferences =
            new Dictionary<EventAddress, SessionContextSetupReference>();
        foreach (SessionProvenLineageHeader proven
                 in chronologicalHeaders) {
            cancellationToken.ThrowIfCancellationRequested();
            using SessionJournalEventFrame frame =
                _reader.ReadEvent(proven.Address).Unwrap();
            EventFrameHeader expectedHeader =
                verifyBoundedProof
                    && _testHooks
                        .RewriteBoundedHistoryProofHeader
                        is { } rewrite
                    ? rewrite(proven.Header)
                    : proven.Header;
            if (frame.Address != proven.Address
                || frame.Header != expectedHeader) {
                throw new InvalidDataException(
                    $"History planning payload read at '{proven.Address}' does not match its proven lineage header."
                );
            }
            ValidateSessionHeaderPreview(
                proven.Address,
                frame.Header
            );
            var kind = (SessionEventKind)frame.Header.OpaqueEventKind;
            object body = SessionEventCodec.Decode(
                kind,
                frame.Payload,
                out int bodySchemaVersion
            );
            if (SessionOperationalSemantics.IsSetupKind(kind)) {
                suffixSetupReferences.Add(
                    proven.Address,
                    new SessionContextSetupReference(
                        proven.Address,
                        bodySchemaVersion,
                        SessionRequestCanonicalizer.Sha256Hex(
                            frame.Payload
                        )
                    )
                );
            }
            events.Add(new DecodedSessionEvent(
                kind,
                bodySchemaVersion,
                body,
                proven.Address,
                frame.Header.Parent
            ));
            rawHashEntries.Add(
                new SessionRawRangeHashEntry(
                    proven.Address,
                    frame.Header.Parent,
                    frame.Header.OpaqueEventKind,
                    bodySchemaVersion,
                    SessionRequestCanonicalizer.Sha256Hex(
                        frame.Payload
                    )
                )
            );
            rawAddresses.Add(proven.Address);
        }

        SessionGoverningSetup governingSetup;
        SessionContextAnchorSetupReferences startSetups;
        if (planningSeed is null) {
            governingSetup = ResolveGoverningSetup(
                resolvedStart,
                cancellationToken
            );
            SessionSetupReference runtime = CreateSetupReference(
                governingSetup.RuntimeConfigSetupAddress,
                SessionEventKind.RuntimeConfigSetup
            );
            SessionSetupReference prompt = CreateSetupReference(
                governingSetup.SystemPromptSetupAddress,
                SessionEventKind.SystemPromptSetup
            );
            startSetups = new(
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
        else {
            governingSetup = planningSeed.GoverningSetup;
            startSetups = planningSeed.Setups;
        }
        SessionDependencyClosedFoldSeed foldSeed =
            SessionDependencyClosedFoldSeed.Create(
                governingSetup,
                executionSeed
            );
        var units = new List<SessionHistoryPlanningUnit>();
        var boundaries = new List<SessionHistoryPlanningBoundary>();
        SessionTailContextProjection.TailFoldResult folded =
            SessionTailContextProjection.FoldSuffix(
                foldSeed,
                events,
                units,
                boundaries
            );
        var endSetups = new SessionContextAnchorSetupReferences(
            ResolveFoldedSetupReference(
                folded.GoverningSetup.RuntimeConfigSetupAddress,
                startSetups.RuntimeConfig,
                suffixSetupReferences
            ),
            ResolveFoldedSetupReference(
                folded.GoverningSetup.SystemPromptSetupAddress,
                startSetups.SystemPrompt,
                suffixSetupReferences
            )
        );
        var boundaryAddresses = boundaries
            .Select(static boundary => boundary.Address)
            .ToHashSet();
        var boundarySetups =
            new Dictionary<
                EventAddress,
                SessionContextAnchorSetupReferences
            >();
        SessionContextSetupReference runtimeAtBoundary =
            startSetups.RuntimeConfig;
        SessionContextSetupReference promptAtBoundary =
            startSetups.SystemPrompt;
        foreach (DecodedSessionEvent ev in events) {
            if (ev.Kind == SessionEventKind.RuntimeConfigSetup) {
                runtimeAtBoundary = suffixSetupReferences[ev.Address];
            }
            else if (ev.Kind
                     == SessionEventKind.SystemPromptSetup) {
                promptAtBoundary = suffixSetupReferences[ev.Address];
            }
            if (boundaryAddresses.Contains(ev.Address)) {
                boundarySetups.Add(
                    ev.Address,
                    new SessionContextAnchorSetupReferences(
                        runtimeAtBoundary,
                        promptAtBoundary
                    )
                );
            }
        }
        if (boundarySetups.Count != boundaries.Count) {
            throw new InvalidDataException(
                "Planning fold did not produce exact setup references for every replay-safe boundary."
            );
        }
        SessionJournalReadDiagnostics after =
            _reader.CaptureDiagnostics();
        return new SessionHistoryPlanningWindow(
            capturedHead,
            resolvedStart,
            startSetups,
            endSetups,
            rawAddresses.AsReadOnly(),
            units.AsReadOnly(),
            boundaries.AsReadOnly(),
            new System.Collections.ObjectModel.ReadOnlyDictionary<
                EventAddress,
                SessionContextAnchorSetupReferences
            >(boundarySetups),
            new SessionHistoryPlanningDiagnostics(
                after.HeaderPreviewReadCount
                    - before.HeaderPreviewReadCount,
                after.PayloadReadCount - before.PayloadReadCount,
                after.LogicalPayloadByteCount
                    - before.LogicalPayloadByteCount,
                events.Count
            )
        ) {
            RawRangeSha256 = SessionRawRangeHasher.Compute(
                resolvedStart,
                capturedHead,
                rawHashEntries
            ),
            RawHashEntries = rawHashEntries.AsReadOnly(),
            Folded = folded
        };
    }

    private static SessionContextSetupReference
        ResolveFoldedSetupReference(
        EventAddress address,
        SessionContextSetupReference startReference,
        IReadOnlyDictionary<EventAddress, SessionContextSetupReference>
            suffixReferences
    ) {
        if (address == startReference.Address) {
            return startReference;
        }
        return suffixReferences.TryGetValue(
            address,
            out SessionContextSetupReference? reference
        )
            ? reference
            : throw new InvalidDataException(
                $"Folded governing setup '{address}' has no exact payload reference."
            );
    }

    internal async Task<TurnResult> SendAsync(
        string observation,
        CancellationToken cancellationToken = default
    ) {
        return await SendAsync(
                observation,
                observer: null,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async Task<TurnResult> SendAsync(
        EventAddress expectedHead,
        string observation,
        CancellationToken cancellationToken = default
    ) => await SendAsync(
            expectedHead,
            observation,
            observer: null,
            cancellationToken
        )
        .ConfigureAwait(false);

    internal async Task<TurnResult> SendAsync(
        string observation,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) => await SendCoreAsync(
            null,
            observation,
            observer,
            cancellationToken
        )
        .ConfigureAwait(false);

    public async Task<TurnResult> SendAsync(
        EventAddress expectedHead,
        string observation,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) => await SendCoreAsync(
            expectedHead,
            observation,
            observer,
            cancellationToken
        )
        .ConfigureAwait(false);

    private async Task<TurnResult> SendCoreAsync(
        EventAddress? expectedHead,
        string observation,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken
    ) {
        using MutationLease mutation = EnterMutation(nameof(SendAsync));
        ThrowIfReadOnlyMutation(nameof(SendAsync));
        ValidateRequired(observation, nameof(observation));
        SessionExecutionRecovery recovery = ResolveExecutionTail(
            cancellationToken
        );
        if (expectedHead is { } boundHead
            && recovery.Head != boundHead) {
            throw new SessionJournalExpectedHeadMismatchException(
                boundHead,
                recovery.Head
            );
        }
        if (recovery.State.Phase == SessionExecutionPhase.TurnFailed) {
            if (recovery.State.HeadKind
                    != SessionEventKind.CompletionAttemptFailed
                || recovery.Head is null) {
                throw new InvalidDataException(
                    "TurnFailed SendAsync requires the exact "
                    + "CompletionAttemptFailed head; legacy failed-turn "
                    + "setup suffixes are unsupported."
                );
            }
            throw new InvalidOperationException(
                "SendAsync cannot continue from TurnFailed. Call "
                + $"AbandonFailedTurn('{recovery.Head.Value}') with the "
                + "exact failed head before sending a new observation."
            );
        }
        if (recovery.State.Phase != SessionExecutionPhase.Idle) {
            throw new InvalidOperationException(
                $"SendAsync requires an idle boundary. Current phase is '{recovery.State.Phase}'; call ResumeAsync first."
            );
        }
        SessionRuntime runtime = RequireRuntime();
        ImmutableArray<ToolDefinition> visibleTools =
            runtime.ToolSession?.VisibleDefinitions ?? ImmutableArray<ToolDefinition>.Empty;
        if (!visibleTools.IsEmpty) {
            _ = RequireToolRuntimeIdentity(runtime, visibleTools);
        }
        _ = ValidateRuntimePlanningPrerequisites(runtime);
        // Empty-lineage bootstrap is proven before lifecycle work so invalid genesis topology
        // cannot trigger maintainer completion or durable raw effects. Exact selection is repeated
        // after lifecycle because maintenance may publish a new set at the current boundary.
        ValidateContextPlanningPreflight(runtime);
        await PreflightFreshBootstrapBeforeContextLifecycleAsync(
                runtime,
                recovery,
                observation,
                visibleTools,
                cancellationToken
            )
            .ConfigureAwait(false);
        SessionContextLifecycleResult lifecycle = await
            PrepareContextLifecycleAsync(
                runtime,
                recovery,
                observation,
                cancellationToken
            )
            .ConfigureAwait(false);
        await ValidatePendingObservationContextReadinessAsync(
                runtime,
                recovery,
                recovery.Head!.Value,
                observation,
                visibleTools,
                lifecycle.Status
                    == SessionContextLifecycleStatus.RawHistoryAuthorized,
                cancellationToken
            )
            .ConfigureAwait(false);
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

    internal async Task<ResumeOutcome> ResumeAsync(
        CancellationToken cancellationToken = default
    ) {
        return await ResumeAsync(
                observer: null,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async Task<ResumeOutcome> ResumeAsync(
        EventAddress expectedHead,
        CancellationToken cancellationToken = default
    ) => await ResumeAsync(
            expectedHead,
            observer: null,
            cancellationToken
        )
        .ConfigureAwait(false);

    /// <summary>
    /// Executes and durably settles exactly one pending tool operation, then
    /// stops before planning or dispatching the completion after the tool
    /// result. This lets a Host run derived maintenance at the exact
    /// ToolResultObserved boundary before it selects the current completion
    /// route.
    /// </summary>
    public async Task<SessionPendingToolBoundaryResult>
        ExecutePendingToolToBoundaryAsync(
        EventAddress expectedHead,
        ToolSession toolSession,
        SessionToolRuntimeIdentity toolRuntimeIdentity,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(toolSession);
        ArgumentNullException.ThrowIfNull(toolRuntimeIdentity);
        using MutationLease mutation = EnterMutation(
            nameof(ExecutePendingToolToBoundaryAsync)
        );
        ThrowIfReadOnlyMutation(nameof(ExecutePendingToolToBoundaryAsync));
        SessionExecutionRecovery recovery = ResolveExecutionTail(
            cancellationToken
        );
        if (recovery.Head != expectedHead) {
            throw new SessionJournalExpectedHeadMismatchException(
                expectedHead,
                recovery.Head
            );
        }
        SessionExecutionRecovery refreshed = await
            ExecutePendingToolOnceAsync(
                recovery,
                toolSession,
                toolRuntimeIdentity,
                cancellationToken
            ).ConfigureAwait(false);
        EventAddress head = refreshed.Head
            ?? throw new InvalidDataException(
                "A settled tool operation must leave a raw head."
            );
        return refreshed.State.Phase switch {
            SessionExecutionPhase.AwaitingAgentAction
                => new SessionPendingToolBoundaryResult.Settled(head),
            SessionExecutionPhase.AwaitingToolExecution
                => new SessionPendingToolBoundaryResult.MorePending(head),
            _ => throw new InvalidDataException(
                $"A settled tool operation reached unexpected phase '{refreshed.State.Phase}'."
            )
        };
    }

    internal async Task<ResumeOutcome> ResumeAsync(
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) => await ResumeCoreAsync(
            null,
            observer,
            cancellationToken
        )
        .ConfigureAwait(false);

    public async Task<ResumeOutcome> ResumeAsync(
        EventAddress expectedHead,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) => await ResumeCoreAsync(
            expectedHead,
            observer,
            cancellationToken
        )
        .ConfigureAwait(false);

    private async Task<ResumeOutcome> ResumeCoreAsync(
        EventAddress? expectedHead,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken
    ) {
        using MutationLease mutation = EnterMutation(nameof(ResumeAsync));
        ThrowIfReadOnlyMutation(nameof(ResumeAsync));
        SessionExecutionRecovery recovery = ResolveExecutionTail(
            cancellationToken
        );
        if (expectedHead is { } boundHead
            && recovery.Head != boundHead) {
            throw new SessionJournalExpectedHeadMismatchException(
                boundHead,
                recovery.Head
            );
        }
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

    internal EventAddress AppendObservation(string content) {
        using MutationLease mutation = EnterMutation(
            nameof(AppendObservation)
        );
        ThrowIfReadOnlyMutation(nameof(AppendObservation));
        ValidateRequired(content, nameof(content));
        SessionExecutionRecovery recovery = ResolveExecutionTail();
        if (recovery.State.Phase != SessionExecutionPhase.Idle) {
            throw new InvalidOperationException(
                $"AppendObservation requires an idle boundary. Current phase is '{recovery.State.Phase}'; abandon an exact failed turn first."
            );
        }

        return AppendExpected(
            SessionEventKind.ObservationAccepted,
            new ObservationAcceptedBody(content),
            recovery.Head,
            requireBoundSetupCursor: false
        );
    }

    internal EventAddress AppendRuntimeConfigSetup(SessionRuntimeConfiguration configuration) {
        using MutationLease mutation = EnterMutation(
            nameof(AppendRuntimeConfigSetup)
        );
        ThrowIfReadOnlyMutation(nameof(AppendRuntimeConfigSetup));
        ArgumentNullException.ThrowIfNull(configuration);
        ValidateRuntimeConfiguration(configuration);
        SessionExecutionRecovery recovery = ResolveExecutionTail();
        if (recovery.State.Phase != SessionExecutionPhase.Idle) {
            throw new InvalidOperationException(
                $"AppendRuntimeConfigSetup requires an idle boundary. Current phase is '{recovery.State.Phase}'; abandon an exact failed turn first."
            );
        }

        return AppendExpected(
            SessionEventKind.RuntimeConfigSetup,
            configuration,
            recovery.Head,
            requireBoundSetupCursor: false
        );
    }

    internal EventAddress AppendSystemPromptSetup(string systemPrompt) {
        using MutationLease mutation = EnterMutation(
            nameof(AppendSystemPromptSetup)
        );
        ThrowIfReadOnlyMutation(nameof(AppendSystemPromptSetup));
        if (systemPrompt is null) { throw new ArgumentNullException(nameof(systemPrompt)); }
        SessionExecutionRecovery recovery = ResolveExecutionTail();
        if (recovery.State.Phase != SessionExecutionPhase.Idle) {
            throw new InvalidOperationException(
                $"AppendSystemPromptSetup requires an idle boundary. Current phase is '{recovery.State.Phase}'; abandon an exact failed turn first."
            );
        }

        return AppendExpected(
            SessionEventKind.SystemPromptSetup,
            new SystemPromptSetupBody(systemPrompt),
            recovery.Head,
            requireBoundSetupCursor: false
        );
    }

    internal EventAddress AppendImportedAgentAction(ActionMessage action, CompletionDescriptor invocation) {
        using MutationLease mutation = EnterMutation(
            nameof(AppendImportedAgentAction)
        );
        ThrowIfReadOnlyMutation(nameof(AppendImportedAgentAction));
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

    /// <summary>
    /// Resolves governing setup through the actual Parent lineage, using a controlled-writer
    /// Prepared event as a bounded checkpoint when encountered. Checkpoint setup payloads are
    /// revalidated by kind, schema, and hash; this online path trusts append-time
    /// reconstruction/canonical validation, bound-cursor validation, and head CAS instead of
    /// repeating an O(N) latest-ancestor proof. Untrusted imported journals require full offline
    /// validation before online use.
    /// </summary>
    public SessionGoverningSetup ResolveGoverningSetup(EventAddress head, CancellationToken cancellationToken = default) {
        ThrowIfDisposed();
        _lastGoverningSetupResolutionDiagnostics = default;
        SessionAuthoritativeGoverningSetupResolver.Result result =
            SessionAuthoritativeGoverningSetupResolver.Resolve(
                _reader,
                head,
                cancellationToken
            );
        _lastGoverningSetupResolutionDiagnostics = result.Diagnostics;
        return result.Setup;
    }

    /// <summary>
    /// Resolves the raw setup facts governing <paramref name="head"/> through direct Parent
    /// lineage events or a controlled-writer Prepared checkpoint. Returned references have exact
    /// kind/schema/hash identity. A checkpoint hit does not repeat an O(N) proof that its
    /// references are the latest setup ancestors; it relies on append-time exact reconstruction,
    /// bound-cursor validation, and head CAS. Untrusted imported journals must first pass full
    /// offline validation.
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

    internal byte[] ReadPayloadBytes(EventAddress address) {
        ThrowIfDisposed();
        using SessionJournalEventFrame frame = _reader.ReadEvent(address).Unwrap();
        return frame.Payload.ToArray();
    }

    public void Dispose() {
        BeginDerivedSidecarDispose();
        try {
            using MutationLease mutation = EnterMutation(nameof(Dispose));
            if (_disposed) { return; }
            _journal.Dispose();
            _disposed = true;
        }
        catch {
            CancelDerivedSidecarDispose();
            throw;
        }
    }

    internal static SessionJournalEngine CreateCore(
        string path,
        SessionCreateOptions options,
        SessionCreationOrigin origin,
        SessionRuntime? runtime,
        SessionJournalTestHooks? testHooks,
        EventJournalOptions? journalOptions = null
    ) {
        ArgumentNullException.ThrowIfNull(options);
        ValidateCreateOptions(options, origin);

        var journal = EventJournal.EventJournal.CreateNew(path, journalOptions ?? DefaultJournalOptions);
        try {
            RefId mainRef = journal.CreateBranch(
                SessionJournalDefaults.MainBranchName,
                startPoint: null
            ).Unwrap();
            var engine = new SessionJournalEngine(
                journal,
                SessionJournalDefaults.MainBranchName,
                mainRef,
                isReadOnly: false,
                runtime,
                testHooks
            );
            SessionRuntimeConfiguration runtimeConfig = options.ToRuntimeConfiguration();
            EventAddress runtimeAddress = engine.Append(SessionEventKind.RuntimeConfigSetup, runtimeConfig);
            EventAddress promptAddress = engine.Append(SessionEventKind.SystemPromptSetup, new SystemPromptSetupBody(options.SystemPrompt));
            EventAddress createdAddress = engine.Append(
                SessionEventKind.SessionCreated,
                new SessionCreatedBody(origin)
            );
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
        string branchName,
        SessionRuntime? runtime,
        SessionJournalTestHooks? testHooks,
        EventJournalOptions? journalOptions = null
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        var journal = EventJournal.EventJournal.OpenExisting(path, journalOptions ?? DefaultJournalOptions);
        try {
            RefId branchRefId = journal.OpenBranch(branchName).Unwrap();
            return new SessionJournalEngine(
                journal,
                branchName,
                branchRefId,
                isReadOnly: false,
                runtime,
                testHooks
            );
        }
        catch {
            journal.Dispose();
            throw;
        }
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
        ValidateContextPlanningPreflight(runtime);
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
        await PreflightFreshBootstrapBeforeContextLifecycleAsync(
                runtime,
                recovery,
                pendingObservation: null,
                tools,
                cancellationToken
            )
            .ConfigureAwait(false);
        SessionContextLifecycleResult lifecycle = await
            PrepareContextLifecycleAsync(
                runtime,
                recovery,
                pendingObservation: null,
                cancellationToken
            )
            .ConfigureAwait(false);
        SelectedContextCandidate selection = await SelectContextCandidateAsync(
            runtime,
            recovery,
            completionBoundary,
            governingSetup,
            lifecycle.Status
                == SessionContextLifecycleStatus.RawHistoryAuthorized,
            cancellationToken
        ).ConfigureAwait(false);
        SessionContextCandidate selectedCandidate = selection.Candidate;
        SessionTailContextProjectionResult tail =
            MaterializeSelectedContext(
                selection,
                recovery,
                governingSetup
            );
        _lastTailProjectionDiagnostics = tail.Diagnostics;
        var materialization = new RequestContextMaterialization(
            tail.SystemPrompt,
            tail.Context,
            tail.RawStartExclusive,
            tail.RawRangeSha256,
            ToManifestSetupReferences(
                selectedCandidate.AnchorSetups
            ),
            tail.ContextSnapshots.Select(static snapshot => new SessionRequestContextInput(
                SessionArtifactContextSnapshotHasher.ComputeSha256(snapshot), snapshot
            )).ToImmutableArray()
        );
        var request = new CompletionRequest(
            governingSetup.RuntimeConfig.ModelId,
            new CompletionPromptPrefix(
                materialization.SystemPrompt,
                CompletionOutputContract.ProviderDefault(tools),
                materialization.Context
            ),
            tailMessages: []
        );
        if (runtime.MaximumCanonicalRequestBytes
                is long maximumCanonicalRequestBytes
            && SessionRequestCanonicalizer.Canonicalize(request)
                is { Length: var actualCanonicalRequestBytes }
            && actualCanonicalRequestBytes
                > maximumCanonicalRequestBytes) {
            throw new InvalidDataException(
                "Canonical request byte guard rejected the exact final request "
                + $"before Prepared: metric={CanonicalRequestBytesMetricId}, "
                + $"actualBytes={actualCanonicalRequestBytes}, "
                + $"maximumBytes={maximumCanonicalRequestBytes}."
            );
        }

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

    private static SessionTailContextProjectionResult
        MaterializeSelectedContext(
        SelectedContextCandidate selection,
        SessionExecutionRecovery recovery,
        SessionGoverningSetup currentGoverningSetup
    ) {
        SessionHistoryPlanningWindow window = selection.Window;
        SessionTailContextProjection.TailFoldResult folded =
            window.Folded
            ?? throw new InvalidDataException(
                "Selected context planning window is missing its exact fold."
            );
        if (folded.GoverningSetup != currentGoverningSetup
            || folded.Phase != recovery.State.Phase
            || folded.ToolExecutionSequenceCheckpoint
                != recovery.State.ToolExecutionSequenceCheckpoint
            || !string.Equals(
                folded.ActiveCorrelationId,
                recovery.State.ActiveCorrelationId,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "Selected context planning fold does not match the exact completion boundary."
            );
        }
        ImmutableArray<SessionRequestArtifactContextSnapshot>
            snapshots = [
                .. selection.Candidate.Contributions.Select(
                    static contribution =>
                        SessionContextContributionRenderer
                            .RenderOneHot(
                                contribution.Target,
                                contribution.ExactText
                            )
                )
            ];
        (
            string systemPrompt,
            ImmutableArray<IHistoryMessage> header
        ) = SessionCoherentRequestRecipe.Expand(
            folded.GoverningSetup.SystemPrompt,
            SessionCoherentRequestRecipe.Aggregate(snapshots)
        );
        var context =
            ImmutableArray.CreateBuilder<IHistoryMessage>(
                header.Length
                + window.Units.Count
            );
        context.AddRange(header);
        for (int index = 0;
             index < window.Units.Count;
             index++) {
            context.Add(window.Units[index].Message);
        }
        int rawStart =
            selection.Candidate.SetAdmissionAnchor
                == window.StartExclusive
            ? 0
            : IndexAfter(
                window.RawAddresses,
                selection.Candidate.SetAdmissionAnchor
            );
        SessionRawRangeHashEntry[] rawEntries = [
            .. window.RawHashEntries.Skip(rawStart)
        ];
        string rawRangeSha256 = SessionRawRangeHasher.Compute(
            selection.Candidate.SetAdmissionAnchor,
            window.ObservedRawHead,
            rawEntries
        );
        return new SessionTailContextProjectionResult(
            systemPrompt,
            context.MoveToImmutable(),
            selection.Candidate.SetAdmissionAnchor,
            rawRangeSha256,
            snapshots,
            new SessionTailProjectionDiagnostics(
                checked((int)window.Diagnostics.HeaderVisits),
                checked((int)window.Diagnostics.PayloadReads),
                window.Diagnostics.DecodedEventCount
            )
        );
    }

    private static SessionGoverningSetupReferences
        ToManifestSetupReferences(
        SessionContextAnchorSetupReferences references
    ) => new(
        new SessionSetupReference(
            references.RuntimeConfig.Address,
            references.RuntimeConfig.BodySchemaVersion,
            references.RuntimeConfig.PayloadSha256
        ),
        new SessionSetupReference(
            references.SystemPrompt.Address,
            references.SystemPrompt.BodySchemaVersion,
            references.SystemPrompt.PayloadSha256
        )
    );

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
        CompletionResult result;
        try {
            result = await runtime.CompletionClient
                .StreamCompletionAsync(request, observer, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CompletionRequestRejectedException rejection) {
            throw PersistKnownCompletionFailure(
                activeAttemptAddress,
                rejection.Termination,
                rejection.Errors
            );
        }

        if (!result.Termination.IsSuccess) {
            throw PersistKnownCompletionFailure(
                activeAttemptAddress,
                result.Termination,
                result.Errors
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
        throw PersistKnownCompletionFailure(
            activeAttemptAddress,
            hostFailure,
            errors
        );
    }

    private SessionJournalTurnAbortedException
        PersistKnownCompletionFailure(
            EventAddress activeAttemptAddress,
            CompletionTermination failure,
            IReadOnlyList<string>? errors
        ) {
        IReadOnlyList<string> frozenErrors = FreezeErrors(errors)
            ?? Array.AsReadOnly(Array.Empty<string>());
        try {
            AppendExpected(
                SessionEventKind.CompletionAttemptFailed,
                new CompletionAttemptFailedBody(
                    failure.Kind,
                    failure.ProviderReason,
                    failure.Detail,
                    frozenErrors
                ),
                activeAttemptAddress,
                requireBoundSetupCursor: false
            );
        }
        catch {
            // CommitToRef appends a Ref move before DurableFlush. If that
            // flush (or a later return path) throws, this EventJournal
            // instance can still report Started while reopening may recover
            // either Started or the exact Failed move. Never continue from
            // the stale in-memory Ref cache; repository reopen is the only
            // authority that may classify the physical head.
            Interlocked.Exchange(ref _reopenRequired, 1);
            throw;
        }
        return new SessionJournalTurnAbortedException(
            BuildTurnAbortMessage(failure),
            failure,
            frozenErrors
        );
    }

    private async Task<ResumeOutcome> ResumeCompletionAsync(
        SessionExecutionRecovery recovery,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken
    ) {
        if (!SessionOperationalSemantics.IsPreparedOrAttemptPhase(
                recovery.State.Phase
            )
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
        SessionPreparedRequestReconstruction reconstruction =
            ReconstructPreparedRecovery(recovery, cancellationToken);
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
        CompletionRequestPreparedBody manifest =
            reconstruction.Manifest;
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
        SessionRuntime runtime = RequireRuntime();
        ToolSession toolSession = RequireToolSession(runtime);
        SessionToolRuntimeIdentity runtimeIdentity = runtime.ToolRuntimeIdentity
            ?? throw new InvalidOperationException(
                "Tool continuation requires an exact current tool runtime identity."
            );
        SessionExecutionRecovery refreshed = await ExecutePendingToolOnceAsync(
            recovery,
            toolSession,
            runtimeIdentity,
            cancellationToken
        ).ConfigureAwait(false);
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

    private async Task<SessionExecutionRecovery> ExecutePendingToolOnceAsync(
        SessionExecutionRecovery recovery,
        ToolSession toolSession,
        SessionToolRuntimeIdentity runtimeIdentity,
        CancellationToken cancellationToken
    ) {
        if (recovery.Head is null
            || recovery.State.Phase !=
                SessionExecutionPhase.AwaitingToolExecution) {
            throw new InvalidOperationException(
                "Tool-boundary execution requires an exact AwaitingToolExecution recovery boundary."
            );
        }
        if (recovery.State.PendingToolCall is null) { throw new InvalidDataException("AwaitingToolExecution requires a pending tool call."); }
        SessionToolRuntimeIdentity expectedToolRuntimeIdentity =
            recovery.State.PendingToolRuntimeIdentity
                ?? throw new InvalidDataException(
                    "AwaitingToolExecution requires a durable pending tool runtime identity."
                );
        if (runtimeIdentity != expectedToolRuntimeIdentity) {
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

        return ResolveExecutionTail(
            resultAddress,
            cancellationToken
        );
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
        EventAddress? observedHead = _journal.GetHead(_branchRefId);
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

        EventAddress? observedHead = _journal.GetHead(_branchRefId);
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
        EventAddress? observedHead = _journal.GetHead(_branchRefId);
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
        if (runtime.MaximumCanonicalRequestBytes is <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(runtime.MaximumCanonicalRequestBytes),
                "Maximum canonical request bytes must be positive when specified."
            );
        }
    }

    private async ValueTask
        PreflightFreshBootstrapBeforeContextLifecycleAsync(
        SessionRuntime runtime,
        SessionExecutionRecovery recovery,
        string? pendingObservation,
        ImmutableArray<ToolDefinition> tools,
        CancellationToken cancellationToken
    ) {
        if (runtime.ContextLifecycle is null) {
            return;
        }
        EventAddress boundary = recovery.Head
            ?? throw new InvalidDataException(
                "Fresh bootstrap preflight requires an exact raw boundary."
            );
        SessionGoverningSetup governingSetup =
            EnsurePlanningGoverningSetupCursor(
                boundary,
                cancellationToken
            );
        ICoherentContextCandidateSource source =
            RequireContextCandidateSource(runtime);
        SessionContextCandidateSelection selection = await source
            .SelectAsync(
                CreateContextSelectionRequest(
                    boundary,
                    governingSetup
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(selection);
        selection.ValidateShape();
        EnsureCurrentHead(boundary);
        switch (selection.Status) {
            case SessionContextCandidateSelectionStatus.Selected:
                _ = RequireSelectedDescriptor(selection);
                return;
            case SessionContextCandidateSelectionStatus.OrdinalUnavailable:
                RequireNoSelectedDescriptor(selection);
                return;
            case SessionContextCandidateSelectionStatus.EmptyLineage:
                RequireNoSelectedDescriptor(selection);
                break;
            case SessionContextCandidateSelectionStatus.RawHistoryAuthorized:
                RequireNoSelectedDescriptor(selection);
                return;
            case SessionContextCandidateSelectionStatus
                    .ExactPublishedSetInvalid:
                RequireNoSelectedDescriptor(selection);
                return;
            case SessionContextCandidateSelectionStatus.StoreUnavailable:
                RequireNoSelectedDescriptor(selection);
                return;
            case SessionContextCandidateSelectionStatus.BeyondPrefix:
                RequireNoSelectedDescriptor(selection);
                return;
            default:
                throw new InvalidDataException(
                    $"Unknown context candidate selection status '{selection.Status}'."
                );
        }

        FreshBootstrapBoundary expectedBoundary =
            pendingObservation is null
                ? FreshBootstrapBoundary.ActiveCompletionBoundary
                : FreshBootstrapBoundary.PreAppend;
        EmptyLineageTopology topology =
            ClassifyEmptyLineageTopology(
                boundary,
                recovery,
                expectedBoundary,
                cancellationToken
            );
        if (topology.Kind == EmptyLineageTopologyKind.Mature) {
            // ResolveExecutionTail has already checked the complete operational
            // ancestry. The checked walk above additionally proves a valid
            // SessionCreated root without treating malformed ancestry as mature;
            // only a genuinely fresh topology requires Native origin.
            // Let the single engine-owned lifecycle pass publish a candidate;
            // the exact post-lifecycle selection remains authoritative.
            return;
        }
        SessionHistoryPlanningWindow window =
            ReadHistoryPlanningWindowAt(
                boundary,
                startExclusive: null,
                cancellationToken
            );
        CompletionRequest projectedRequest =
            BuildProjectedCompletionRequest(
                runtime,
                governingSetup,
                tools,
                window,
                ImmutableArray<SessionContextContribution>.Empty,
                pendingObservation is null
                    ? null
                    : new ObservationMessage(pendingObservation)
            );
        EnforceProjectedCanonicalRequestByteGuard(
            runtime,
            projectedRequest
        );
    }

    private EventAddress ValidateEmptyLineageRawHistoryTopology(
        EventAddress selectedHead,
        SessionExecutionRecovery recovery,
        FreshBootstrapBoundary expectedBoundary,
        bool allowMatureRawHistory,
        CancellationToken cancellationToken
    ) {
        if (expectedBoundary
                == FreshBootstrapBoundary.ActiveCompletionBoundary
            && !IsExactActiveCompletionBoundary(
                selectedHead,
                recovery
            )) {
            throw FreshBootstrapUnavailable(
                "Recovery bootstrap requires the exact active "
                + "ObservationAccepted or ToolResultObserved boundary."
            );
        }
        EmptyLineageTopology topology =
            ClassifyEmptyLineageTopology(
                selectedHead,
                recovery,
                expectedBoundary,
                cancellationToken
            );
        if (topology.Kind == EmptyLineageTopologyKind.Mature
            && !allowMatureRawHistory) {
            throw FreshBootstrapUnavailable(
                "Fresh bootstrap requires no prior operational history; "
                + $"encountered '{topology.FirstOperationalKind}' at "
                + $"{topology.FirstOperationalAddress}."
            );
        }
        return topology.SessionCreatedAddress;
    }

    private EmptyLineageTopology ClassifyEmptyLineageTopology(
        EventAddress selectedHead,
        SessionExecutionRecovery recovery,
        FreshBootstrapBoundary expectedBoundary,
        CancellationToken cancellationToken
    ) {
        if (recovery.Head != selectedHead) {
            throw new InvalidDataException(
                "Fresh bootstrap recovery is not bound to the selected raw head."
            );
        }

        EventAddress? cursor;
        switch (expectedBoundary) {
            case FreshBootstrapBoundary.PreAppend:
                if (recovery.State.Phase != SessionExecutionPhase.Idle) {
                    throw FreshBootstrapUnavailable(
                        "Pre-append bootstrap requires a fresh idle boundary."
                    );
                }
                cursor = selectedHead;
                break;
            case FreshBootstrapBoundary.ActiveCompletionBoundary:
                if (recovery.State.Phase
                        != SessionExecutionPhase.AwaitingAgentAction) {
                    throw FreshBootstrapUnavailable(
                        "Recovery bootstrap requires an active "
                        + "AwaitingAgentAction boundary."
                    );
                }
                if (recovery.State.HeadKind
                        == SessionEventKind.ToolResultObserved) {
                    // ToolResultObserved is necessarily mature raw history.
                    // Starting at its head lets the checked walk establish that
                    // fact while preserving the result in the raw tail.
                    cursor = selectedHead;
                    break;
                }
                EventFrameHeader observationHeader =
                    _reader.ReadEventHeaderPreview(selectedHead).Unwrap();
                ValidateSessionHeaderPreview(
                    selectedHead,
                    observationHeader
                );
                if ((SessionEventKind)observationHeader.OpaqueEventKind
                        != SessionEventKind.ObservationAccepted
                    || observationHeader.Parent is null) {
                    throw FreshBootstrapUnavailable(
                        "Recovery bootstrap head is not a valid ObservationAccepted event."
                    );
                }
                cursor = observationHeader.Parent;
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(expectedBoundary),
                    expectedBoundary,
                    "Unknown fresh bootstrap boundary."
                );
        }

        var visited = new HashSet<EventAddress>();
        EventAddress? firstOperationalAddress = null;
        SessionEventKind? firstOperationalKind = null;
        while (cursor is { } address) {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(address)) {
                throw new InvalidDataException(
                    $"Fresh bootstrap Parent chain contains a cycle at {address}."
                );
            }
            EventFrameHeader header =
                _reader.ReadEventHeaderPreview(address).Unwrap();
            ValidateSessionHeaderPreview(address, header);
            var kind = (SessionEventKind)header.OpaqueEventKind;
            if (SessionOperationalSemantics.IsSetupKind(kind)) {
                cursor = header.Parent;
                continue;
            }
            if (kind != SessionEventKind.SessionCreated) {
                firstOperationalAddress ??= address;
                firstOperationalKind ??= kind;
                cursor = header.Parent;
                continue;
            }
            using SessionJournalEventFrame frame =
                _reader.ReadEvent(address).Unwrap();
            ValidateSessionHeaderPreview(address, frame.Header);
            SessionCreatedBody created = SessionEventCodec.Decode(
                    SessionEventKind.SessionCreated,
                    frame.Payload,
                    out _
                )
                as SessionCreatedBody
                ?? throw new InvalidDataException(
                    $"SessionCreated at {address} decoded to an unexpected body."
                );
            if (firstOperationalAddress is null
                && created.Origin != SessionCreationOrigin.Native) {
                throw FreshBootstrapUnavailable(
                    "Empty-lineage online preparation requires a native "
                    + "SessionCreated origin; "
                    + $"actual origin is '{created.Origin}'."
                );
            }
            EnsureCurrentHead(selectedHead);
            return firstOperationalAddress is null
                ? new EmptyLineageTopology(
                    EmptyLineageTopologyKind.Fresh,
                    address
                )
                : new EmptyLineageTopology(
                    EmptyLineageTopologyKind.Mature,
                    address,
                    firstOperationalAddress,
                    firstOperationalKind
                );
        }
        throw FreshBootstrapUnavailable(
            "Empty-lineage ancestry has no SessionCreated boundary."
        );
    }

    private static bool IsExactActiveCompletionBoundary(
        EventAddress selectedHead,
        SessionExecutionRecovery recovery
    ) => recovery.State.Phase
            == SessionExecutionPhase.AwaitingAgentAction
        && recovery.State.HeadKind switch {
            SessionEventKind.ObservationAccepted
                => recovery.Boundary.SourcePrepared is null
                    && recovery.Boundary.SourceObservation == selectedHead,
            SessionEventKind.ToolResultObserved
                => recovery.Head == selectedHead,
            _ => false,
        };

    private static SessionJournalNotReadyException
        FreshBootstrapUnavailable(string detail) => new(
        SessionJournalNotReadyReason.ContextCandidateUnavailable,
        detail
    );

    private async ValueTask<SessionContextLifecycleResult>
        PrepareContextLifecycleAsync(
        SessionRuntime runtime,
        SessionExecutionRecovery recovery,
        string? pendingObservation,
        CancellationToken cancellationToken
    ) {
        if (runtime.ContextLifecycle is not { } lifecycle) {
            return SessionContextLifecycleResult.Ready;
        }
        return await PrepareContextLifecycleAsync(
            lifecycle,
            recovery,
            pendingObservation,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<SessionContextLifecycleResult>
        PrepareContextLifecycleAsync(
        ISessionContextLifecycleCoordinator lifecycle,
        SessionExecutionRecovery recovery,
        string? pendingObservation,
        CancellationToken cancellationToken
    ) {
        EventAddress boundary = recovery.Head
            ?? throw new InvalidDataException(
                "Online context lifecycle requires an exact non-empty raw boundary."
            );
        EventAddress? expectedHead = _journal.GetHead(_branchRefId);
        if (expectedHead != boundary) {
            throw new InvalidOperationException(
                "Online context lifecycle boundary is stale before preparation."
            );
        }
        SessionContextLifecycleResult result = await
            InvokeLifecycleWithAuditScopeAsync(
                lifecycle,
                new SessionContextLifecycleRequest(
                    CreateContextSelectionRequest(
                        boundary,
                        EnsurePlanningGoverningSetupCursor(
                            boundary,
                            cancellationToken
                        )
                    ),
                    recovery.State.Phase,
                    DeriveContextLifecycleTrigger(
                        recovery,
                        pendingObservation
                    ),
                    pendingObservation
                ),
                cancellationToken
            ).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(result);
        _testHooks.AfterContextLifecyclePrepared?.Invoke(_journal);
        EnsureCurrentHead(expectedHead);
        switch (result.Status) {
            case SessionContextLifecycleStatus.Ready:
            case SessionContextLifecycleStatus.RawHistoryAuthorized:
                return result;
            case SessionContextLifecycleStatus.Backpressure:
                throw new SessionJournalNotReadyException(
                    SessionJournalNotReadyReason.RecapMaintenanceBackpressure,
                    result.Detail
                    ?? "Derived recap maintenance reached explicit backpressure."
                );
            case SessionContextLifecycleStatus.Unavailable:
                throw new SessionJournalNotReadyException(
                    SessionJournalNotReadyReason.RecapMaintenanceUnavailable,
                    result.Detail
                    ?? "Derived recap maintenance is unavailable."
                );
            default:
                throw new InvalidDataException(
                    $"Unknown context lifecycle status '{result.Status}'."
                );
        }
    }

    /// <summary>
    /// Executes one derived lifecycle pass under the mutable owner's normal
    /// mutation/audit authority without appending raw events or dispatching a
    /// completion. The exact expected head prevents a Host from running a
    /// maintenance pass against a boundary it did not inspect.
    /// </summary>
    public async ValueTask<SessionContextLifecycleResult>
        PrepareContextLifecycleMaintenanceAsync(
        EventAddress expectedHead,
        ISessionContextLifecycleCoordinator lifecycle,
        string? pendingObservation,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(lifecycle);
        using MutationLease mutation = EnterMutation(
            nameof(PrepareContextLifecycleMaintenanceAsync));
        ThrowIfReadOnlyMutation(
            nameof(PrepareContextLifecycleMaintenanceAsync));
        SessionExecutionRecovery recovery = ResolveExecutionTail(
            cancellationToken);
        if (recovery.Head != expectedHead) {
            throw new SessionJournalExpectedHeadMismatchException(
                expectedHead,
                recovery.Head
            );
        }
        bool valid = recovery.State.Phase switch {
            SessionExecutionPhase.Idle => pendingObservation is not null,
            SessionExecutionPhase.AwaitingAgentAction
                => pendingObservation is null
                    && recovery.State.HeadKind is
                        SessionEventKind.ObservationAccepted
                            or SessionEventKind.ToolResultObserved,
            _ => false
        };
        if (!valid) {
            throw new InvalidOperationException(
                "Derived lifecycle maintenance is unavailable at the current phase."
            );
        }
        return await PrepareContextLifecycleAsync(
            lifecycle,
            recovery,
            pendingObservation,
            cancellationToken).ConfigureAwait(false);
    }

    private static SessionContextLifecycleTrigger
        DeriveContextLifecycleTrigger(
        SessionExecutionRecovery recovery,
        string? pendingObservation
    ) {
        if (pendingObservation is not null) {
            return SessionContextLifecycleTrigger.PreObservation;
        }
        return recovery.State.HeadKind switch {
            SessionEventKind.ObservationAccepted
                => SessionContextLifecycleTrigger.ObservationAccepted,
            SessionEventKind.ToolResultObserved
                => SessionContextLifecycleTrigger.ToolResultObserved,
            _ => throw new InvalidDataException(
                $"Online context lifecycle cannot derive a trigger from '{recovery.State.HeadKind}'."
            ),
        };
    }

    private async ValueTask
        ValidatePendingObservationContextReadinessAsync(
        SessionRuntime runtime,
        SessionExecutionRecovery recovery,
        EventAddress currentBoundary,
        string pendingObservation,
        ImmutableArray<ToolDefinition> tools,
        bool allowMatureRawHistory,
        CancellationToken cancellationToken
    ) {
        SessionGoverningSetup governingSetup =
            EnsurePlanningGoverningSetupCursor(
                currentBoundary,
                cancellationToken
            );
        SessionContextSelectionRequest request =
            CreateContextSelectionRequest(
                currentBoundary,
                governingSetup
            );
        ICoherentContextCandidateSource source =
            RequireContextCandidateSource(runtime);
        SessionContextCandidateSelection selection = await source
            .SelectAsync(request, cancellationToken)
            .ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(selection);
        selection.ValidateShape();
        var projectedObservation =
            new ObservationMessage(pendingObservation);
        if (selection.Status
            == SessionContextCandidateSelectionStatus.EmptyLineage) {
            RequireNoSelectedDescriptor(selection);
            _ = ValidateEmptyLineageRawHistoryTopology(
                currentBoundary,
                recovery,
                FreshBootstrapBoundary.PreAppend,
                allowMatureRawHistory,
                cancellationToken
            );
            SessionHistoryPlanningWindow rawHistory =
                ReadHistoryPlanningWindowAt(
                    currentBoundary,
                    startExclusive: null,
                    cancellationToken
                );
            CompletionRequest rawHistoryProjectedRequest =
                BuildProjectedCompletionRequest(
                    runtime,
                    governingSetup,
                    tools,
                    rawHistory,
                    ImmutableArray<
                        SessionContextContribution
                    >.Empty,
                    projectedObservation
                );
            EnforceProjectedCanonicalRequestByteGuard(
                runtime,
                rawHistoryProjectedRequest
            );
            return;
        }
        if (selection.Status
            == SessionContextCandidateSelectionStatus.RawHistoryAuthorized) {
            RequireNoSelectedDescriptor(selection);
            if (!allowMatureRawHistory) {
                throw new SessionJournalNotReadyException(
                    SessionJournalNotReadyReason.ContextCandidateUnavailable,
                    "Mature raw history requires authorization from the same lifecycle pass before the observation append."
                );
            }
            SessionHistoryPlanningWindow rawHistory =
                ReadHistoryPlanningWindowAt(
                    currentBoundary,
                    startExclusive: null,
                    cancellationToken
                );
            CompletionRequest rawHistoryProjectedRequest =
                BuildProjectedCompletionRequest(
                    runtime,
                    governingSetup,
                    tools,
                    rawHistory,
                    ImmutableArray<SessionContextContribution>.Empty,
                    projectedObservation
                );
            EnforceProjectedCanonicalRequestByteGuard(
                runtime,
                rawHistoryProjectedRequest
            );
            return;
        }
        if (selection.Status
            == SessionContextCandidateSelectionStatus.OrdinalUnavailable) {
            RequireNoSelectedDescriptor(selection);
            throw new SessionJournalNotReadyException(
                SessionJournalNotReadyReason.ContextCandidateUnavailable,
                "The requested coherent context candidate ordinal is unavailable before the observation append."
            );
        }
        ThrowIfContextSelectionUnavailable(
            selection,
            "before the observation append"
        );
        SessionContextCandidateDescriptor descriptor =
            RequireSelectedDescriptor(selection);
        SessionHistoryPlanningSeed seed =
            CreateHistoryPlanningSeed(
                descriptor.SetAdmissionAnchor,
                descriptor.AnchorSetups,
                cancellationToken
            );
        SessionHistoryPlanningWindow window =
            ReadHistoryPlanningWindowAt(
                currentBoundary,
                seed,
                cancellationToken
            );
        SessionContextCandidateMaterializationResult materialization =
            await source
            .MaterializeAsync(descriptor, cancellationToken)
            .ConfigureAwait(false);
        SessionContextCandidate candidate =
            RequireMaterializedCandidate(materialization);
        ImmutableArray<SessionContextContribution> contributions =
            SessionContextCandidateValidator.ValidateMaterializedCandidate(
                descriptor,
                candidate,
                CreateAllowedSourceHeads(window, descriptor.SetAdmissionAnchor),
                allowEmpty: false
            );
        CompletionRequest projectedRequest =
            BuildProjectedCompletionRequest(
            runtime,
            governingSetup,
            tools,
            window,
            contributions,
            projectedObservation
        );
        EnforceProjectedCanonicalRequestByteGuard(
            runtime,
            projectedRequest
        );
    }

    private static SessionContextSelectionRequest
        CreateContextSelectionRequest(
        EventAddress completionBoundary,
        SessionGoverningSetup governingSetup
    ) {
        ArgumentNullException.ThrowIfNull(governingSetup);
        if (governingSetup.Head != completionBoundary) {
            throw new InvalidDataException(
                "Context selection governing setup does not belong "
                + "to the exact completion boundary."
            );
        }
        var request = new SessionContextSelectionRequest(
            completionBoundary,
            governingSetup.RuntimeConfig
                .DerivedContext.NthPrevious
        );
        request.ValidateShape();
        return request;
    }

    private static ICoherentContextCandidateSource RequireContextCandidateSource(
        SessionRuntime runtime
    ) => runtime.ContextCandidateSource
        ?? throw new SessionJournalNotReadyException(
            SessionJournalNotReadyReason.ContextCandidateSourceRequired,
            "Online completion requires an ICoherentContextCandidateSource configured on SessionRuntime."
        );

    private async ValueTask<SelectedContextCandidate> SelectContextCandidateAsync(
        SessionRuntime runtime,
        SessionExecutionRecovery recovery,
        EventAddress completionBoundary,
        SessionGoverningSetup governingSetup,
        bool allowMatureRawHistory,
        CancellationToken cancellationToken
    ) {
        ICoherentContextCandidateSource source = RequireContextCandidateSource(runtime);
        SessionContextSelectionRequest request =
            CreateContextSelectionRequest(
                completionBoundary,
                governingSetup
            );
        SessionContextCandidateSelection selection = await source
            .SelectAsync(request, cancellationToken)
            .ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(selection);
        selection.ValidateShape();
        if (selection.Status
            == SessionContextCandidateSelectionStatus.EmptyLineage) {
            RequireNoSelectedDescriptor(selection);
            _ = ValidateEmptyLineageRawHistoryTopology(
                completionBoundary,
                recovery,
                FreshBootstrapBoundary.ActiveCompletionBoundary,
                allowMatureRawHistory,
                cancellationToken
            );
            return SelectRawHistoryCandidate(
                completionBoundary,
                cancellationToken
            );
        }
        if (selection.Status
            == SessionContextCandidateSelectionStatus.RawHistoryAuthorized) {
            RequireNoSelectedDescriptor(selection);
            if (!allowMatureRawHistory) {
                throw new SessionJournalNotReadyException(
                    SessionJournalNotReadyReason.ContextCandidateUnavailable,
                    "Mature raw history requires authorization from the same lifecycle pass."
                );
            }
            return SelectRawHistoryCandidate(
                completionBoundary,
                cancellationToken
            );
        }
        if (selection.Status
            == SessionContextCandidateSelectionStatus.OrdinalUnavailable) {
            RequireNoSelectedDescriptor(selection);
            throw new SessionJournalNotReadyException(
                SessionJournalNotReadyReason.ContextCandidateUnavailable,
                $"The requested coherent context candidate ordinal is unavailable for completion boundary '{completionBoundary}'."
            );
        }
        ThrowIfContextSelectionUnavailable(
            selection,
            $"for completion boundary '{completionBoundary}'"
        );

        SessionContextCandidateDescriptor descriptor =
            RequireSelectedDescriptor(selection);
        SessionHistoryPlanningSeed seed =
            CreateHistoryPlanningSeed(
                descriptor.SetAdmissionAnchor,
                descriptor.AnchorSetups,
                cancellationToken
            );
        SessionHistoryPlanningWindow window =
            ReadHistoryPlanningWindowAt(
                completionBoundary,
                seed,
                cancellationToken
            );
        SessionContextCandidateMaterializationResult materialization =
            await source
            .MaterializeAsync(descriptor, cancellationToken)
            .ConfigureAwait(false);
        SessionContextCandidate candidate =
            RequireMaterializedCandidate(materialization);
        ImmutableArray<SessionContextContribution> contributions =
            SessionContextCandidateValidator.ValidateMaterializedCandidate(
                descriptor,
                candidate,
                CreateAllowedSourceHeads(window, descriptor.SetAdmissionAnchor),
                allowEmpty: false
            );
        return new SelectedContextCandidate(
            candidate with { Contributions = contributions },
            window
        );
    }

    private SelectedContextCandidate SelectRawHistoryCandidate(
        EventAddress completionBoundary,
        CancellationToken cancellationToken
    ) {
        SessionHistoryPlanningWindow window =
            ReadHistoryPlanningWindowAt(
                completionBoundary,
                startExclusive: null,
                cancellationToken
            );
        return new SelectedContextCandidate(
            new SessionContextCandidate(
                window.StartExclusive,
                window.StartSetups,
                Array.Empty<SessionContextContribution>()
            ),
            window
        );
    }

    private static SessionContextCandidateDescriptor RequireSelectedDescriptor(
        SessionContextCandidateSelection selection
    ) {
        if (selection.Status
                != SessionContextCandidateSelectionStatus.Selected
            || selection.Candidate is not { } descriptor
            || string.IsNullOrWhiteSpace(descriptor.Handle)
            || descriptor.Handle.Length > 512
            || descriptor.Handle.Contains('\0', StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(descriptor.SnapshotToken)
            || descriptor.SnapshotToken.Length > 512
            || descriptor.SnapshotToken.Contains(
                '\0',
                StringComparison.Ordinal
            )
            || descriptor.SetAdmissionAnchor == default
            || descriptor.AnchorSetups is null
            || descriptor.AnchorSetups.RuntimeConfig is null
            || descriptor.AnchorSetups.SystemPrompt is null) {
            throw new InvalidDataException(
                "A selected context candidate must include bounded handle/snapshot tokens and complete raw anchor facts."
            );
        }
        return descriptor;
    }

    private static SessionContextCandidate RequireMaterializedCandidate(
        SessionContextCandidateMaterializationResult result
    ) {
        ArgumentNullException.ThrowIfNull(result);
        return result switch {
            SessionContextCandidateMaterializationResult.Materialized available
                => available.Candidate
                    ?? throw new InvalidDataException(
                        "A materialized context result requires a candidate."
                    ),
            SessionContextCandidateMaterializationResult.Stale stale
                => throw new SessionJournalNotReadyException(
                    SessionJournalNotReadyReason.ContextCandidateUnavailable,
                    RequireMaterializationDetail(stale.Detail)
                ),
            SessionContextCandidateMaterializationResult.Busy busy
                => throw new SessionJournalNotReadyException(
                    SessionJournalNotReadyReason.ContextStoreUnavailable,
                    RequireMaterializationDetail(busy.Detail)
                ),
            SessionContextCandidateMaterializationResult.Disposed disposed
                => throw new SessionJournalNotReadyException(
                    SessionJournalNotReadyReason.ContextStoreUnavailable,
                    RequireMaterializationDetail(disposed.Detail)
                ),
            SessionContextCandidateMaterializationResult.Invalid invalid
                => throw new SessionJournalNotReadyException(
                    SessionJournalNotReadyReason.ContextCandidateInvalid,
                    RequireMaterializationDetail(invalid.Detail)
                ),
            _ => throw new InvalidDataException(
                "The context source returned an unknown materialization outcome."
            )
        };
    }

    private static string RequireMaterializationDetail(string detail) {
        if (string.IsNullOrWhiteSpace(detail)) {
            throw new InvalidDataException(
                "A non-materialized context result requires detail."
            );
        }
        return detail;
    }

    private static void RequireNoSelectedDescriptor(
        SessionContextCandidateSelection selection
    ) {
        if (selection.Candidate is not null
            || selection.Status is not (
                SessionContextCandidateSelectionStatus.EmptyLineage
                or SessionContextCandidateSelectionStatus.RawHistoryAuthorized
                or SessionContextCandidateSelectionStatus.OrdinalUnavailable
                or SessionContextCandidateSelectionStatus
                    .ExactPublishedSetInvalid
                or SessionContextCandidateSelectionStatus.StoreUnavailable
                or SessionContextCandidateSelectionStatus.BeyondPrefix
            )) {
            throw new InvalidDataException(
                "A non-selected context candidate result cannot include a descriptor."
            );
        }
    }

    private static void ThrowIfContextSelectionUnavailable(
        SessionContextCandidateSelection selection,
        string phase
    ) {
        switch (selection.Status) {
            case SessionContextCandidateSelectionStatus
                    .ExactPublishedSetInvalid:
                RequireNoSelectedDescriptor(selection);
                throw new SessionJournalNotReadyException(
                    SessionJournalNotReadyReason.ContextCandidateInvalid,
                    selection.Detail
                    ?? $"The exact published recap set is structurally invalid {phase}."
                );
            case SessionContextCandidateSelectionStatus.StoreUnavailable:
                RequireNoSelectedDescriptor(selection);
                throw new SessionJournalNotReadyException(
                    SessionJournalNotReadyReason.ContextStoreUnavailable,
                    selection.Detail
                    ?? $"The recap store is unavailable {phase}."
                );
            case SessionContextCandidateSelectionStatus.BeyondPrefix:
                RequireNoSelectedDescriptor(selection);
                throw new SessionJournalNotReadyException(
                    SessionJournalNotReadyReason.ContextCandidateUnavailable,
                    selection.Detail
                    ?? $"The required context anchor is beyond the configured lineage prefix {phase}."
                );
            case SessionContextCandidateSelectionStatus.RawHistoryAuthorized:
                RequireNoSelectedDescriptor(selection);
                throw new SessionJournalNotReadyException(
                    SessionJournalNotReadyReason.ContextCandidateUnavailable,
                    $"Mature raw history is not authorized by the same lifecycle pass {phase}."
                );
        }
    }

    private static HashSet<EventAddress> CreateAllowedSourceHeads(
        SessionHistoryPlanningWindow window,
        EventAddress anchor
    ) {
        var allowed = new HashSet<EventAddress> { anchor };
        int rawStart = anchor == window.StartExclusive
            ? 0
            : IndexAfter(window.RawAddresses, anchor);
        for (int index = rawStart;
             index < window.RawAddresses.Count;
             index++) {
            allowed.Add(window.RawAddresses[index]);
        }
        return allowed;
    }

    private static int IndexAfter(
        IReadOnlyList<EventAddress> addresses,
        EventAddress address
    ) {
        for (int index = 0; index < addresses.Count; index++) {
            if (addresses[index] == address) {
                return checked(index + 1);
            }
        }
        throw new InvalidDataException(
            "Candidate anchor is absent from the bounded raw interval."
        );
    }

    private static CompletionRequest BuildProjectedCompletionRequest(
        SessionRuntime runtime,
        SessionGoverningSetup governingSetup,
        ImmutableArray<ToolDefinition> tools,
        SessionHistoryPlanningWindow window,
        ImmutableArray<SessionContextContribution> contributions,
        IHistoryMessage? projectedMessage = null
    ) {
        SessionRequestArtifactContextSnapshot[] snapshots = [
            .. contributions.Select(static contribution =>
                SessionContextContributionRenderer.RenderOneHot(
                    contribution.Target,
                    contribution.ExactText
                )
            )
        ];
        (
            string systemPrompt,
            ImmutableArray<IHistoryMessage> header
        ) = SessionCoherentRequestRecipe.Expand(
            governingSetup.SystemPrompt,
            SessionCoherentRequestRecipe.Aggregate(snapshots)
        );
        var context =
            ImmutableArray.CreateBuilder<IHistoryMessage>(
                header.Length
                + window.Units.Count
                + (projectedMessage is null ? 0 : 1)
            );
        context.AddRange(header);
        for (int index = 0;
             index < window.Units.Count;
             index++) {
            context.Add(window.Units[index].Message);
        }
        if (projectedMessage is not null) {
            context.Add(projectedMessage);
        }
        return new CompletionRequest(
            governingSetup.RuntimeConfig.ModelId,
            new CompletionPromptPrefix(
                systemPrompt,
                CompletionOutputContract.ProviderDefault(tools),
                context.MoveToImmutable()
            ),
            tailMessages: []
        );
    }

    private static void EnforceProjectedCanonicalRequestByteGuard(
        SessionRuntime runtime,
        CompletionRequest request
    ) {
        if (runtime.MaximumCanonicalRequestBytes
                is not long maximumCanonicalRequestBytes) {
            return;
        }
        int actualCanonicalRequestBytes =
            SessionRequestCanonicalizer.Canonicalize(request).Length;
        if (actualCanonicalRequestBytes <= maximumCanonicalRequestBytes) {
            return;
        }
        throw new SessionJournalNotReadyException(
            SessionJournalNotReadyReason.ContextCandidateUnavailable,
            "Canonical request byte guard rejected the projected request "
            + $"before online request side effects: metric={CanonicalRequestBytesMetricId}, "
            + $"actualBytes={actualCanonicalRequestBytes}, "
            + $"maximumBytes={maximumCanonicalRequestBytes}."
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
            new SessionRequestParameters(request.ModelId),
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
        EventAddress? expectedHead = _journal.GetHead(_branchRefId);
        return AppendExpected(kind, body, expectedHead, requireBoundSetupCursor: false);
    }

    private EventAddress AppendExpected(
        SessionEventKind kind,
        object body,
        EventAddress? expectedHead,
        bool requireBoundSetupCursor
    ) {
        ThrowIfDisposed();
        EventAddress? observedHead = _journal.GetHead(_branchRefId);
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
            _testHooks.BeforeCommit?.Invoke(kind, _journal);
            EventAddress committed = _journal.CommitToRef(
                _branchRefId,
                expectedHead,
                payload,
                opaqueEventKind: (uint)kind,
                hint: default
            ).Unwrap().EventAddress;
            _testHooks.AfterCommitBeforeReturn?.Invoke(kind, _journal);
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

    private T ReadAndValidatePlanningSetupReference<T>(
        SessionContextSetupReference reference,
        SessionEventKind expectedKind,
        CancellationToken cancellationToken
    ) where T : class {
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();
        using SessionJournalEventFrame frame =
            _reader.ReadEvent(reference.Address).Unwrap();
        ValidateSessionHeaderPreview(reference.Address, frame.Header);
        var actualKind =
            (SessionEventKind)frame.Header.OpaqueEventKind;
        if (actualKind != expectedKind) {
            throw new InvalidDataException(
                $"Planning setup seed expected '{expectedKind}' at {reference.Address}, got '{actualKind}'."
            );
        }
        object body = SessionEventCodec.Decode(
            actualKind,
            frame.Payload,
            out int bodySchemaVersion
        );
        if (bodySchemaVersion != reference.BodySchemaVersion) {
            throw new InvalidDataException(
                $"Planning setup seed schema version mismatch at {reference.Address}: expected {reference.BodySchemaVersion}, got {bodySchemaVersion}."
            );
        }
        string payloadSha256 =
            SessionRequestCanonicalizer.Sha256Hex(frame.Payload);
        if (!string.Equals(
                payloadSha256,
                reference.PayloadSha256,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                $"Planning setup seed payload hash mismatch at {reference.Address}."
            );
        }
        return body as T
            ?? throw new InvalidDataException(
                $"Planning setup seed at {reference.Address} decoded to '{body.GetType().Name}', expected '{typeof(T).Name}'."
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

    private static string BuildOperationId(EventAddress? head, RawToolCall call) {
        ArgumentNullException.ThrowIfNull(call);
        string turnKey = head is { } address
            ? EventAddressTextCodec.Format(address)
            : "no-head";
        return $"atelia.session-journal.tool.v1:{turnKey}:{call.ToolCallId}";
    }

    private static void ValidateCreateOptions(
        SessionCreateOptions options,
        SessionCreationOrigin origin
    ) {
        ValidateRequired(options.ModelId, nameof(options.ModelId));
        ValidateRequired(options.CompletionSurfaceId, nameof(options.CompletionSurfaceId));
        ValidateRequired(options.Schema, nameof(options.Schema));
        if (options.DerivedContextNthPrevious < 0) {
            throw new ArgumentOutOfRangeException(
                nameof(options.DerivedContextNthPrevious),
                "Derived context nth-previous ordinal cannot be negative."
            );
        }
        if (!Enum.IsDefined(origin)) {
            throw new ArgumentOutOfRangeException(
                nameof(origin),
                origin,
                "Unknown session creation origin."
            );
        }
        if (options.SystemPrompt is null) { throw new ArgumentNullException(nameof(options.SystemPrompt)); }
    }

    private static void ValidateRuntimeConfiguration(SessionRuntimeConfiguration configuration) {
        ValidateRequired(configuration.ModelId, nameof(configuration.ModelId));
        ValidateRequired(configuration.CompletionSurfaceId, nameof(configuration.CompletionSurfaceId));
        ValidateRequired(configuration.Schema, nameof(configuration.Schema));
        ArgumentNullException.ThrowIfNull(configuration.DerivedContext);
        if (configuration.DerivedContext.NthPrevious < 0) {
            throw new ArgumentOutOfRangeException(
                nameof(configuration.DerivedContext.NthPrevious),
                "Derived context nth-previous ordinal cannot be negative."
            );
        }
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

    private sealed record SelectedContextCandidate(
        SessionContextCandidate Candidate,
        SessionHistoryPlanningWindow Window
    );

    private sealed record SessionProvenLineageHeader(
        EventAddress Address,
        EventFrameHeader Header
    );

    private sealed record SessionTargetLineageProof(
        EventAddress CapturedHead,
        EventAddress RequiredAnchor,
        int MaxHeaderCount,
        IReadOnlyList<SessionProvenLineageHeader>
            HeadThroughTargetOrLimit,
        SessionCurrentLineageAnchorLookup Lookup,
        SessionCurrentLineageDiagnostics Diagnostics
    );

    private sealed record SessionCurrentLineagePrefixState(
        IReadOnlyList<SessionProvenLineageHeader> HeadToOldest
    );

    private sealed record SessionHistoryPlanningWindowProofState(
        IReadOnlyList<SessionProvenLineageHeader>
            HeadToStartExclusive,
        SessionJournalReadDiagnostics DiagnosticsBeforeProof
    );

    private sealed record SessionGoverningSetupProofState(
        EventAddress Boundary,
        SessionContextAnchorSetupReferences ExpectedSetups
    );

    private enum FreshBootstrapBoundary {
        PreAppend,
        ActiveCompletionBoundary,
    }

    private enum EmptyLineageTopologyKind {
        Fresh,
        Mature,
    }

    private sealed record EmptyLineageTopology(
        EmptyLineageTopologyKind Kind,
        EventAddress SessionCreatedAddress,
        EventAddress? FirstOperationalAddress = null,
        SessionEventKind? FirstOperationalKind = null
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
        if (Volatile.Read(ref _reopenRequired) != 0) {
            throw new SessionJournalReopenRequiredException();
        }
    }

    internal void EnsureNotDisposedForReadView()
        => ThrowIfDisposed();

    private void ThrowIfReadOnlyMutation(string operation) {
        ThrowIfDisposed();
        if (_isReadOnly) {
            throw new InvalidOperationException(
                $"SessionJournalEngine is read-only; mutation operation '{operation}' is not allowed."
            );
        }
    }
}
