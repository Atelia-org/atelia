using System.Collections.Immutable;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.EventJournal;

namespace Atelia.SessionJournal;

public sealed class SessionJournalEngine : IDisposable {
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
    private SessionRuntime? _runtime;
    private SessionGoverningSetup? _governingSetupCursor;
    private GoverningSetupResolutionDiagnostics _lastGoverningSetupResolutionDiagnostics;
    private SessionTailProjectionDiagnostics _lastTailProjectionDiagnostics;
    private bool _disposed;

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
    }

    public string Path => _journal.JournalPath;
    public string BranchName => _branchName;
    public RefId BranchRefId => _branchRefId;
    public bool IsReadOnly => _isReadOnly;

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
        => OpenCore(
            path,
            SessionJournalDefaults.MainBranchName,
            runtime: null,
            testHooks: null
        );

    public static SessionJournalEngine Open(string path, SessionRuntime runtime)
        => OpenCore(
            path,
            SessionJournalDefaults.MainBranchName,
            runtime,
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

    public static SessionJournalEngine Open(
        string path,
        string branchName,
        SessionRuntime runtime
    ) => OpenCore(path, branchName, runtime, testHooks: null);

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
    /// Captures the selected branch Parent lineage using event headers only. The returned order is
    /// head-to-root and is bound to one captured ref head; no payload is read or decoded.
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
    /// Resolves setup seeds for multiple selected-branch planning starts with one header walk. Only
    /// setup-event payloads are decoded; ordinary history payloads remain unread.
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
    /// Reads only the raw interval after a replay-safe start boundary and materializes
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
    /// Replays one dependency-closed planning interval at an exact historical head. The caller
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
        var reverseAddresses = new List<EventAddress>();
        EventAddress? cursor = capturedHead;
        EventAddress? resolvedStart = null;
        while (cursor is { } address) {
            cancellationToken.ThrowIfCancellationRequested();
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
            reverseAddresses.Add(address);
            cursor = header.Parent;
        }
        if (resolvedStart is null) {
            throw new InvalidDataException(
                startExclusive is null
                    ? "SessionJournal lineage has no SessionCreated planning boundary."
                    : "History planning start is not an ancestor of the captured raw head."
            );
        }

        if (planningSeed is not null
            && (planningSeed.Address != resolvedStart.Value
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
                resolvedStart.Value,
                cancellationToken
            );
        reverseAddresses.Reverse();
        var events = new List<DecodedSessionEvent>(
            reverseAddresses.Count
        );
        var rawHashEntries =
            new List<SessionRawRangeHashEntry>(
                reverseAddresses.Count
            );
        var suffixSetupReferences =
            new Dictionary<EventAddress, SessionContextSetupReference>();
        foreach (EventAddress address in reverseAddresses) {
            cancellationToken.ThrowIfCancellationRequested();
            using SessionJournalEventFrame frame =
                _reader.ReadEvent(address).Unwrap();
            ValidateSessionHeaderPreview(address, frame.Header);
            var kind = (SessionEventKind)frame.Header.OpaqueEventKind;
            object body = SessionEventCodec.Decode(
                kind,
                frame.Payload,
                out int bodySchemaVersion
            );
            if (SessionOperationalSemantics.IsSetupKind(kind)) {
                suffixSetupReferences.Add(
                    address,
                    new SessionContextSetupReference(
                        address,
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
                address,
                frame.Header.Parent
            ));
            rawHashEntries.Add(
                new SessionRawRangeHashEntry(
                    address,
                    frame.Header.Parent,
                    frame.Header.OpaqueEventKind,
                    bodySchemaVersion,
                    SessionRequestCanonicalizer.Sha256Hex(
                        frame.Payload
                    )
                )
            );
        }

        SessionGoverningSetup governingSetup;
        SessionContextAnchorSetupReferences startSetups;
        if (planningSeed is null) {
            governingSetup = ResolveGoverningSetup(
                resolvedStart.Value,
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
            resolvedStart.Value,
            startSetups,
            endSetups,
            reverseAddresses.AsReadOnly(),
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

    public async Task<TurnResult> SendAsync(
        string observation,
        CancellationToken cancellationToken = default
    ) {
        ThrowIfReadOnlyMutation(nameof(SendAsync));
        return await SendAsync(
                observation,
                observer: null,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async Task<TurnResult> SendAsync(
        string observation,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) {
        ThrowIfReadOnlyMutation(nameof(SendAsync));
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
        if (!SessionOperationalSemantics.IsIdleOrFailedPhase(
                recovery.State.Phase
            )) {
            throw new InvalidOperationException(
                $"SendAsync requires an idle or explicitly failed turn boundary. Current phase is '{recovery.State.Phase}'; call ResumeAsync first."
            );
        }
        // Empty-lineage bootstrap is proven before lifecycle work so invalid genesis topology
        // cannot trigger maintainer completion or durable raw effects. Exact selection is repeated
        // after lifecycle because maintenance may publish a new set at the current boundary.
        ValidateContextPlanningPreflight(runtime);
        await PreflightFreshBootstrapBeforeMemoryLifecycleAsync(
                runtime,
                recovery,
                observation,
                visibleTools,
                cancellationToken
            )
            .ConfigureAwait(false);
        await PrepareMemoryLifecycleAsync(
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

    public async Task<ResumeOutcome> ResumeAsync(
        CancellationToken cancellationToken = default
    ) {
        ThrowIfReadOnlyMutation(nameof(ResumeAsync));
        return await ResumeAsync(
                observer: null,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async Task<ResumeOutcome> ResumeAsync(
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) {
        ThrowIfReadOnlyMutation(nameof(ResumeAsync));
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
        ThrowIfReadOnlyMutation(nameof(AppendObservation));
        ValidateRequired(content, nameof(content));
        return Append(SessionEventKind.ObservationAccepted, new ObservationAcceptedBody(content));
    }

    public EventAddress AppendRuntimeConfigSetup(SessionRuntimeConfiguration configuration) {
        ThrowIfReadOnlyMutation(nameof(AppendRuntimeConfigSetup));
        ArgumentNullException.ThrowIfNull(configuration);
        ValidateRuntimeConfiguration(configuration);
        SessionExecutionRecovery recovery = ResolveExecutionTail();
        if (!SessionOperationalSemantics.IsIdleOrFailedPhase(
                recovery.State.Phase
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
        ThrowIfReadOnlyMutation(nameof(AppendSystemPromptSetup));
        if (systemPrompt is null) { throw new ArgumentNullException(nameof(systemPrompt)); }
        SessionExecutionRecovery recovery = ResolveExecutionTail();
        if (!SessionOperationalSemantics.IsIdleOrFailedPhase(
                recovery.State.Phase
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
                new SessionCreatedBody(options.Origin)
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
        await PreflightFreshBootstrapBeforeMemoryLifecycleAsync(
                runtime,
                recovery,
                pendingObservation: null,
                tools,
                cancellationToken
            )
            .ConfigureAwait(false);
        await PrepareMemoryLifecycleAsync(
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
            ModelId: governingSetup.RuntimeConfig.ModelId,
            SystemPrompt: materialization.SystemPrompt,
            Context: materialization.Context,
            Tools: tools,
            MaxTokens: runtime.MaxTokens
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
                        SessionCoherentRequestRecipe
                            .CreateOneHotSnapshot(
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
            selection.Candidate.RawStartExclusive
                == window.StartExclusive
            ? 0
            : IndexAfter(
                window.RawAddresses,
                selection.Candidate.RawStartExclusive
            );
        SessionRawRangeHashEntry[] rawEntries = [
            .. window.RawHashEntries.Skip(rawStart)
        ];
        string rawRangeSha256 = SessionRawRangeHasher.Compute(
            selection.Candidate.RawStartExclusive,
            window.ObservedRawHead,
            rawEntries
        );
        return new SessionTailContextProjectionResult(
            systemPrompt,
            context.MoveToImmutable(),
            selection.Candidate.RawStartExclusive,
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
        PreflightFreshBootstrapBeforeMemoryLifecycleAsync(
        SessionRuntime runtime,
        SessionExecutionRecovery recovery,
        string? pendingObservation,
        ImmutableArray<ToolDefinition> tools,
        CancellationToken cancellationToken
    ) {
        if (runtime.MemoryLifecycle is null) {
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
                new SessionContextSelectionRequest(
                    boundary,
                    governingSetup.RuntimeConfig
                        .DerivedContext.NthPrevious
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(selection);
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
            default:
                throw new InvalidDataException(
                    $"Unknown context candidate selection status '{selection.Status}'."
                );
        }

        FreshBootstrapBoundary expectedBoundary =
            pendingObservation is null
                ? FreshBootstrapBoundary.ActiveFirstObservation
                : FreshBootstrapBoundary.PreAppend;
        _ = ValidateFreshBootstrapTopology(
            boundary,
            recovery,
            expectedBoundary,
            cancellationToken
        );
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

    private EventAddress ValidateFreshBootstrapTopology(
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
            case FreshBootstrapBoundary.ActiveFirstObservation:
                if (recovery.State.Phase
                        != SessionExecutionPhase.AwaitingAgentAction
                    || recovery.State.HeadKind
                        != SessionEventKind.ObservationAccepted
                    || recovery.Boundary.SourcePrepared is not null
                    || recovery.Boundary.SourceObservation
                        != selectedHead) {
                    throw FreshBootstrapUnavailable(
                        "Recovery bootstrap requires the exact active first ObservationAccepted boundary."
                    );
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
                throw FreshBootstrapUnavailable(
                    "Fresh bootstrap requires only setup updates after SessionCreated; "
                    + $"encountered '{kind}' at {address}."
                );
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
            if (created.Origin != SessionCreationOrigin.Native) {
                throw FreshBootstrapUnavailable(
                    "Fresh bootstrap requires a native SessionCreated origin; "
                    + $"actual origin is '{created.Origin}'."
                );
            }
            EnsureCurrentHead(selectedHead);
            return address;
        }
        throw FreshBootstrapUnavailable(
            "Fresh bootstrap ancestry has no SessionCreated boundary."
        );
    }

    private static SessionJournalNotReadyException
        FreshBootstrapUnavailable(string detail) => new(
        SessionJournalNotReadyReason.ContextCandidateUnavailable,
        detail
    );

    private async ValueTask PrepareMemoryLifecycleAsync(
        SessionRuntime runtime,
        SessionExecutionRecovery recovery,
        string? pendingObservation,
        CancellationToken cancellationToken
    ) {
        if (runtime.MemoryLifecycle is not { } lifecycle) {
            return;
        }
        EventAddress boundary = recovery.Head
            ?? throw new InvalidDataException(
                "Online memory lifecycle requires an exact non-empty raw boundary."
            );
        EventAddress? expectedHead = _journal.GetHead(_branchRefId);
        if (expectedHead != boundary) {
            throw new InvalidOperationException(
                "Online memory lifecycle boundary is stale before preparation."
            );
        }
        SessionMemoryLifecycleResult result = await lifecycle
            .PrepareAsync(
                this,
                new SessionMemoryLifecycleRequest(
                    boundary,
                    recovery.State.Phase,
                    pendingObservation
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(result);
        EnsureCurrentHead(expectedHead);
        switch (result.Status) {
            case SessionMemoryLifecycleStatus.Ready:
                return;
            case SessionMemoryLifecycleStatus.Backpressure:
                throw new SessionJournalNotReadyException(
                    SessionJournalNotReadyReason.MemoryMaintenanceBackpressure,
                    result.Detail
                    ?? "Derived memory maintenance reached explicit backpressure."
                );
            case SessionMemoryLifecycleStatus.Unavailable:
                throw new SessionJournalNotReadyException(
                    SessionJournalNotReadyReason.MemoryMaintenanceUnavailable,
                    result.Detail
                    ?? "Derived memory maintenance is unavailable."
                );
            default:
                throw new InvalidDataException(
                    $"Unknown memory lifecycle status '{result.Status}'."
                );
        }
    }

    private async ValueTask
        ValidatePendingObservationContextReadinessAsync(
        SessionRuntime runtime,
        SessionExecutionRecovery recovery,
        EventAddress currentBoundary,
        string pendingObservation,
        ImmutableArray<ToolDefinition> tools,
        CancellationToken cancellationToken
    ) {
        SessionGoverningSetup governingSetup =
            EnsurePlanningGoverningSetupCursor(
                currentBoundary,
                cancellationToken
            );
        SessionContextSelectionRequest request =
            new(
                currentBoundary,
                governingSetup.RuntimeConfig
                    .DerivedContext.NthPrevious
            );
        ICoherentContextCandidateSource source =
            RequireContextCandidateSource(runtime);
        SessionContextCandidateSelection selection = await source
            .SelectAsync(request, cancellationToken)
            .ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(selection);
        var projectedObservation =
            new ObservationMessage(pendingObservation);
        if (selection.Status
            == SessionContextCandidateSelectionStatus.EmptyLineage) {
            RequireNoSelectedDescriptor(selection);
            _ = ValidateFreshBootstrapTopology(
                currentBoundary,
                recovery,
                FreshBootstrapBoundary.PreAppend,
                cancellationToken
            );
            SessionHistoryPlanningWindow bootstrap =
                ReadHistoryPlanningWindowAt(
                    currentBoundary,
                    startExclusive: null,
                    cancellationToken
                );
            CompletionRequest bootstrapProjectedRequest =
                BuildProjectedCompletionRequest(
                    runtime,
                    governingSetup,
                    tools,
                    bootstrap,
                    ImmutableArray<
                        SessionContextContribution
                    >.Empty,
                    projectedObservation
                );
            EnforceProjectedCanonicalRequestByteGuard(
                runtime,
                bootstrapProjectedRequest
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
        SessionContextCandidateDescriptor descriptor =
            RequireSelectedDescriptor(selection);
        SessionHistoryPlanningSeed seed =
            CreateHistoryPlanningSeed(
                descriptor.RawStartExclusive,
                descriptor.AnchorSetups,
                cancellationToken
            );
        SessionHistoryPlanningWindow window =
            ReadHistoryPlanningWindowAt(
                currentBoundary,
                seed,
                cancellationToken
            );
        SessionContextCandidate candidate = await source
            .MaterializeAsync(descriptor, cancellationToken)
            .ConfigureAwait(false);
        ImmutableArray<SessionContextContribution> contributions =
            SessionContextCandidateValidator.ValidateMaterializedCandidate(
                descriptor,
                candidate,
                CreateAllowedSourceHeads(window, descriptor.RawStartExclusive),
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
        CancellationToken cancellationToken
    ) {
        ICoherentContextCandidateSource source = RequireContextCandidateSource(runtime);
        SessionContextSelectionRequest request =
            new(
                completionBoundary,
                governingSetup.RuntimeConfig
                    .DerivedContext.NthPrevious
            );
        SessionContextCandidateSelection selection = await source
            .SelectAsync(request, cancellationToken)
            .ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.Status
            == SessionContextCandidateSelectionStatus.EmptyLineage) {
            RequireNoSelectedDescriptor(selection);
            _ = ValidateFreshBootstrapTopology(
                completionBoundary,
                recovery,
                FreshBootstrapBoundary.ActiveFirstObservation,
                cancellationToken
            );
            return SelectBootstrapCandidate(
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

        SessionContextCandidateDescriptor descriptor =
            RequireSelectedDescriptor(selection);
        SessionHistoryPlanningSeed seed =
            CreateHistoryPlanningSeed(
                descriptor.RawStartExclusive,
                descriptor.AnchorSetups,
                cancellationToken
            );
        SessionHistoryPlanningWindow window =
            ReadHistoryPlanningWindowAt(
                completionBoundary,
                seed,
                cancellationToken
            );
        SessionContextCandidate candidate = await source
            .MaterializeAsync(descriptor, cancellationToken)
            .ConfigureAwait(false);
        ImmutableArray<SessionContextContribution> contributions =
            SessionContextCandidateValidator.ValidateMaterializedCandidate(
                descriptor,
                candidate,
                CreateAllowedSourceHeads(window, descriptor.RawStartExclusive),
                allowEmpty: false
            );
        return new SelectedContextCandidate(
            candidate with { Contributions = contributions },
            window
        );
    }

    private SelectedContextCandidate SelectBootstrapCandidate(
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
            || descriptor.RawStartExclusive == default
            || descriptor.AnchorSetups is null
            || descriptor.AnchorSetups.RuntimeConfig is null
            || descriptor.AnchorSetups.SystemPrompt is null) {
            throw new InvalidDataException(
                "A selected context candidate must include one bounded handle and complete raw anchor facts."
            );
        }
        return descriptor;
    }

    private static void RequireNoSelectedDescriptor(
        SessionContextCandidateSelection selection
    ) {
        if (selection.Candidate is not null
            || selection.Status is not (
                SessionContextCandidateSelectionStatus.EmptyLineage
                or SessionContextCandidateSelectionStatus.OrdinalUnavailable
            )) {
            throw new InvalidDataException(
                "A non-selected context candidate result cannot include a descriptor."
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
                SessionCoherentRequestRecipe.CreateOneHotSnapshot(
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
            systemPrompt,
            context.MoveToImmutable(),
            tools,
            runtime.MaxTokens
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
            _testHooks.BeforeCommit?.Invoke(kind);
            EventAddress committed = _journal.CommitToRef(
                _branchRefId,
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
        if (options.DerivedContextNthPrevious < 0) {
            throw new ArgumentOutOfRangeException(
                nameof(options.DerivedContextNthPrevious),
                "Derived context nth-previous ordinal cannot be negative."
            );
        }
        if (!Enum.IsDefined(options.Origin)) {
            throw new ArgumentOutOfRangeException(
                nameof(options.Origin),
                options.Origin,
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

    private enum FreshBootstrapBoundary {
        PreAppend,
        ActiveFirstObservation,
    }

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

    private void ThrowIfReadOnlyMutation(string operation) {
        ThrowIfDisposed();
        if (_isReadOnly) {
            throw new InvalidOperationException(
                $"SessionJournalEngine is read-only; mutation operation '{operation}' is not allowed."
            );
        }
    }
}
