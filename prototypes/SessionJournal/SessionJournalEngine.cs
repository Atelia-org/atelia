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
    /// Captures the current main Parent lineage using event headers only. The returned order is
    /// head-to-root and is bound to one captured ref head; no payload is read or decoded.
    /// </summary>
    public SessionCurrentLineageSnapshot ReadCurrentLineageHeaders(
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        EventAddress capturedHead = _journal.GetHead(_mainRef)
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
                    $"SessionJournal current main Parent chain contains a cycle at {address}."
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
    /// Resolves setup seeds for multiple current-main planning starts with one header walk. Only
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
        EventAddress capturedHead = _journal.GetHead(_mainRef)
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
                    $"SessionJournal current main Parent chain contains a cycle at {address}."
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
            if (header.Kind is SessionEventKind.RuntimeConfigSetup
                or SessionEventKind.SystemPromptSetup) {
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
                "One or more planning seed addresses are outside the current main lineage."
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
    /// SessionCreated event on the captured main lineage. This API never constructs full history
    /// before the returned start boundary.
    /// </summary>
    public SessionHistoryPlanningWindow ReadHistoryPlanningWindow(
        EventAddress? startExclusive = null,
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        EventAddress observedHead = _journal.GetHead(_mainRef)
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
            if (kind is SessionEventKind.RuntimeConfigSetup
                or SessionEventKind.SystemPromptSetup) {
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

        SessionGoverningSetup seed;
        SessionContextAnchorSetupReferences startSetups;
        if (planningSeed is null) {
            seed = ResolveGoverningSetup(
                resolvedStart.Value,
                cancellationToken
            );
            SessionSetupReference runtime = CreateSetupReference(
                seed.RuntimeConfigSetupAddress,
                SessionEventKind.RuntimeConfigSetup
            );
            SessionSetupReference prompt = CreateSetupReference(
                seed.SystemPromptSetupAddress,
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
            seed = planningSeed.GoverningSetup;
            startSetups = planningSeed.Setups;
        }
        var addressedMessages = new List<AddressedSessionHistoryMessage>();
        var boundaries = new List<SessionHistoryPlanningBoundary>();
        SessionTailContextProjection.TailFoldResult folded =
            SessionTailContextProjection.FoldSuffix(
            seed,
            events,
            executionSeed,
            addressedMessages,
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
        var units = addressedMessages
            .Select(static addressed => new SessionHistoryPlanningUnit(
                addressed.Message,
                addressed.SourceStartInclusive,
                addressed.SourceEndInclusive
            ))
            .ToArray();
        SessionJournalReadDiagnostics after =
            _reader.CaptureDiagnostics();
        return new SessionHistoryPlanningWindow(
            capturedHead,
            resolvedStart.Value,
            startSetups,
            endSetups,
            reverseAddresses.AsReadOnly(),
            Array.AsReadOnly(units),
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
        await PrepareMemoryLifecycleAsync(
                runtime,
                recovery,
                observation,
                cancellationToken
            )
            .ConfigureAwait(false);
        await ValidatePendingObservationContextReadinessAsync(
                runtime,
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
        await PrepareMemoryLifecycleAsync(
                runtime,
                recovery,
                pendingObservation: null,
                cancellationToken
            )
            .ConfigureAwait(false);
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
        SelectedContextCandidate selection = await SelectContextCandidateAsync(
            runtime,
            completionBoundary,
            governingSetup,
            tools,
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
        SessionContextSelectionOptions selectionOptions =
            runtime.ContextSelection
            ?? SessionContextSelectionOptions.Default;
        if (selectionOptions.TotalContextTokenBudget
                is long totalBudget
            && SessionHistoryTokenEstimator
                .EstimateCanonicalRequest(request) > totalBudget) {
            throw new InvalidDataException(
                "Selected context exceeded the total budget after exact final materialization."
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
                - selection.CompletedUnitCount
            );
        context.AddRange(header);
        for (int index = selection.CompletedUnitCount;
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
            folded.GoverningSetup,
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
        EventAddress? expectedHead = _journal.GetHead(_mainRef);
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
        EventAddress currentBoundary,
        string pendingObservation,
        ImmutableArray<ToolDefinition> tools,
        CancellationToken cancellationToken
    ) {
        SessionContextSelectionOptions options =
            runtime.ContextSelection
            ?? SessionContextSelectionOptions.Default;
        SessionContextSelectionRequest request =
            options.CreateRequest(currentBoundary);
        ICoherentContextCandidateSource source =
            RequireContextCandidateSource(runtime);
        SessionContextCandidateDiscovery discovery = await source
            .DiscoverAsync(request, cancellationToken)
            .ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(discovery.Candidates);
        SessionContextCandidateDescriptor[] descriptors =
            SnapshotCandidateDescriptors(
                discovery.Candidates,
                request.MaxCandidateCount
            );
        SessionGoverningSetup governingSetup =
            EnsurePlanningGoverningSetupCursor(
                currentBoundary,
                cancellationToken
            );
        var projectedObservation =
            new ObservationMessage(pendingObservation);
        long projectedObservationTokens =
            SessionHistoryTokenEstimator.Estimate(
                projectedObservation
            );
        if (discovery.Status
            == SessionContextCandidateDiscoveryStatus.EmptyLineage) {
            if (descriptors.Length != 0) {
                throw new InvalidDataException(
                    "An empty candidate lineage cannot include descriptors."
                );
            }
            long bootstrapBudget =
                options.BootstrapRawSuffixTokenBudget
                ?? throw new SessionJournalNotReadyException(
                    SessionJournalNotReadyReason.ContextCandidateUnavailable,
                    "The derived candidate lineage is empty and bounded empty-memory bootstrap is not configured."
                );
            SessionHistoryPlanningWindow bootstrap =
                ReadHistoryPlanningWindowAt(
                    currentBoundary,
                    startExclusive: null,
                    cancellationToken
                );
            long rawTokens = checked(
                SumUnitTokens(bootstrap.Units, 0)
                + projectedObservationTokens
            );
            if (rawTokens > bootstrapBudget
                || options.RawSuffixTokenBudget is long rawBudget
                    && rawTokens > rawBudget) {
                throw new SessionJournalNotReadyException(
                    SessionJournalNotReadyReason.ContextCandidateUnavailable,
                    "The projected empty-memory bootstrap raw suffix exceeds its configured budget."
                );
            }
            long totalTokens =
                EstimateCandidateRequestTokens(
                    runtime,
                    governingSetup,
                    tools,
                    bootstrap,
                    completedUnitCount: 0,
                    ImmutableArray<
                        SessionContextContribution
                    >.Empty,
                    projectedObservation
                );
            if (options.TotalContextTokenBudget
                    is long totalBudget
                && totalTokens > totalBudget) {
                throw new SessionJournalNotReadyException(
                    SessionJournalNotReadyReason.ContextCandidateUnavailable,
                    "The projected empty-memory bootstrap request exceeds its configured total context budget."
                );
            }
            return;
        }
        if (discovery.Status
                != SessionContextCandidateDiscoveryStatus.Candidates
            || descriptors.Length == 0) {
            throw new SessionJournalNotReadyException(
                SessionJournalNotReadyReason.ContextCandidateUnavailable,
                "No coherent context candidate is available before the observation append."
            );
        }
        ValidateCandidateDescriptors(descriptors);
        SessionContextCandidateDescriptor oldest =
            descriptors[^1];
        SessionHistoryPlanningSeed seed =
            CreateHistoryPlanningSeed(
                oldest.RawStartExclusive,
                oldest.AnchorSetups,
                cancellationToken
            );
        SessionHistoryPlanningWindow window =
            ReadHistoryPlanningWindowAt(
                currentBoundary,
                seed,
                cancellationToken
            );
        CandidateCost[] measured =
            MeasureCandidateCosts(window, descriptors)
                .Select(candidate => candidate with {
                    RawSuffixTokens = checked(
                        candidate.RawSuffixTokens
                        + projectedObservationTokens
                    )
                })
                .ToArray();
        foreach (CandidateCost attempt
                 in SelectCandidateAttempts(request, measured)) {
            SessionContextCandidate candidate = await source
                .MaterializeAsync(
                    attempt.Descriptor,
                    cancellationToken
                )
                .ConfigureAwait(false);
            ImmutableArray<SessionContextContribution>
                contributions =
                    SessionContextCandidateValidator
                        .ValidateMaterializedCandidate(
                            attempt.Descriptor,
                            candidate,
                            CreateAllowedSourceHeads(
                                window,
                                attempt.CompletedUnitCount,
                                attempt.Descriptor
                                    .RawStartExclusive
                            ),
                            allowEmpty: false
                        );
            long totalTokens =
                EstimateCandidateRequestTokens(
                    runtime,
                    governingSetup,
                    tools,
                    window,
                    attempt.CompletedUnitCount,
                    contributions,
                    projectedObservation
                );
            if (request.TotalContextTokenBudget
                    is not long totalBudget
                || totalTokens <= totalBudget) {
                return;
            }
        }
        throw new SessionJournalNotReadyException(
            SessionJournalNotReadyReason.ContextCandidateUnavailable,
            "No coherent context candidate fits the projected observation request budget."
        );
    }

    private static SessionContextCandidateDescriptor[]
        SnapshotCandidateDescriptors(
        IReadOnlyList<SessionContextCandidateDescriptor> candidates,
        int maximumCount
    ) {
        ArgumentNullException.ThrowIfNull(candidates);
        if (maximumCount <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCount)
            );
        }
        var snapshot =
            new List<SessionContextCandidateDescriptor>(
                maximumCount
            );
        foreach (SessionContextCandidateDescriptor candidate
                 in candidates) {
            if (snapshot.Count == maximumCount) {
                throw new InvalidDataException(
                    "Context candidate source exceeded the requested discovery bound."
                );
            }
            snapshot.Add(candidate);
        }
        return [.. snapshot];
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
        EventAddress completionBoundary,
        SessionGoverningSetup governingSetup,
        ImmutableArray<ToolDefinition> tools,
        CancellationToken cancellationToken
    ) {
        ICoherentContextCandidateSource source = RequireContextCandidateSource(runtime);
        SessionContextSelectionOptions options =
            runtime.ContextSelection
            ?? SessionContextSelectionOptions.Default;
        SessionContextSelectionRequest request =
            options.CreateRequest(completionBoundary);
        SessionContextCandidateDiscovery discovery = await source
            .DiscoverAsync(request, cancellationToken)
            .ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(discovery.Candidates);
        SessionContextCandidateDescriptor[] descriptors =
            SnapshotCandidateDescriptors(
                discovery.Candidates,
                request.MaxCandidateCount
            );
        if (discovery.Status
            == SessionContextCandidateDiscoveryStatus.EmptyLineage) {
            if (descriptors.Length != 0) {
                throw new InvalidDataException(
                    "An empty candidate lineage cannot include descriptors."
                );
            }
            return SelectBootstrapCandidate(
                runtime,
                completionBoundary,
                governingSetup,
                tools,
                options,
                cancellationToken
            );
        }
        if (discovery.Status
                != SessionContextCandidateDiscoveryStatus.Candidates
            || descriptors.Length == 0) {
            throw new SessionJournalNotReadyException(
                SessionJournalNotReadyReason.ContextCandidateUnavailable,
                $"No coherent context candidate is currently available for completion boundary '{completionBoundary}'."
            );
        }

        ValidateCandidateDescriptors(descriptors);
        SessionContextCandidateDescriptor oldest =
            descriptors[^1];
        SessionHistoryPlanningSeed seed =
            CreateHistoryPlanningSeed(
                oldest.RawStartExclusive,
                oldest.AnchorSetups,
                cancellationToken
            );
        SessionHistoryPlanningWindow window =
            ReadHistoryPlanningWindowAt(
                completionBoundary,
                seed,
                cancellationToken
            );
        CandidateCost[] costs = MeasureCandidateCosts(
            window,
            descriptors
        );
        IEnumerable<CandidateCost> attempts =
            SelectCandidateAttempts(request, costs);
        foreach (CandidateCost attempt in attempts) {
            cancellationToken.ThrowIfCancellationRequested();
            SessionContextCandidate candidate = await source
                .MaterializeAsync(
                    attempt.Descriptor,
                    cancellationToken
                )
                .ConfigureAwait(false);
            HashSet<EventAddress> allowedSources =
                CreateAllowedSourceHeads(
                    window,
                    attempt.CompletedUnitCount,
                    attempt.Descriptor.RawStartExclusive
                );
            ImmutableArray<SessionContextContribution>
                contributions =
                    SessionContextCandidateValidator
                        .ValidateMaterializedCandidate(
                            attempt.Descriptor,
                            candidate,
                            allowedSources,
                            allowEmpty: false
                        );
            long totalTokens = EstimateCandidateRequestTokens(
                runtime,
                governingSetup,
                tools,
                window,
                attempt.CompletedUnitCount,
                contributions
            );
            if (request.TotalContextTokenBudget is long totalBudget
                && totalTokens > totalBudget) {
                continue;
            }
            return new SelectedContextCandidate(
                candidate with {
                    Contributions = contributions
                },
                IsBootstrap: false,
                window,
                attempt.CompletedUnitCount
            );
        }
        throw new SessionJournalNotReadyException(
            SessionJournalNotReadyReason.ContextCandidateUnavailable,
            $"No coherent context candidate fits the configured budget at completion boundary '{completionBoundary}'."
        );
    }

    private SelectedContextCandidate SelectBootstrapCandidate(
        SessionRuntime runtime,
        EventAddress completionBoundary,
        SessionGoverningSetup governingSetup,
        ImmutableArray<ToolDefinition> tools,
        SessionContextSelectionOptions options,
        CancellationToken cancellationToken
    ) {
        long bootstrapBudget =
            options.BootstrapRawSuffixTokenBudget
            ?? throw new SessionJournalNotReadyException(
                SessionJournalNotReadyReason.ContextCandidateUnavailable,
                "The derived candidate lineage is empty and bounded empty-memory bootstrap is not configured."
            );
        SessionHistoryPlanningWindow window =
            ReadHistoryPlanningWindowAt(
                completionBoundary,
                startExclusive: null,
                cancellationToken
            );
        long rawTokens = SumUnitTokens(
            window.Units,
            startIndex: 0
        );
        if (rawTokens > bootstrapBudget
            || options.RawSuffixTokenBudget is long rawBudget
                && rawTokens > rawBudget) {
            throw new SessionJournalNotReadyException(
                SessionJournalNotReadyReason.ContextCandidateUnavailable,
                "The empty-memory bootstrap raw suffix exceeds its configured budget."
            );
        }
        long totalTokens = EstimateCandidateRequestTokens(
            runtime,
            governingSetup,
            tools,
            window,
            completedUnitCount: 0,
            ImmutableArray<SessionContextContribution>.Empty
        );
        if (options.TotalContextTokenBudget is long totalBudget
            && totalTokens > totalBudget) {
            throw new SessionJournalNotReadyException(
                SessionJournalNotReadyReason.ContextCandidateUnavailable,
                "The empty-memory bootstrap request exceeds its configured total context budget."
            );
        }
        return new SelectedContextCandidate(
            new SessionContextCandidate(
                window.StartExclusive,
                window.StartSetups,
                Array.Empty<SessionContextContribution>()
            ),
            IsBootstrap: true,
            window,
            CompletedUnitCount: 0
        );
    }

    private static void ValidateCandidateDescriptors(
        IReadOnlyList<SessionContextCandidateDescriptor> descriptors
    ) {
        var handles = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < descriptors.Count; index++) {
            SessionContextCandidateDescriptor descriptor =
                descriptors[index]
                ?? throw new InvalidDataException(
                    "Context candidate discovery contains a null descriptor."
                );
            if (string.IsNullOrWhiteSpace(descriptor.Handle)
                || descriptor.Handle.Length > 512
                || descriptor.Handle.Contains('\0', StringComparison.Ordinal)
                || descriptor.Ordinal != index
                || descriptor.RawStartExclusive == default
                || descriptor.AnchorSetups is null
                || descriptor.AnchorSetups.RuntimeConfig is null
                || descriptor.AnchorSetups.SystemPrompt is null
                || !handles.Add(descriptor.Handle)) {
                throw new InvalidDataException(
                    "Context candidate descriptors must have unique bounded handles, contiguous ordinals, and complete raw anchor facts."
                );
            }
        }
    }

    private static CandidateCost[] MeasureCandidateCosts(
        SessionHistoryPlanningWindow window,
        IReadOnlyList<SessionContextCandidateDescriptor> descriptors
    ) {
        var boundaries =
            window.ReplaySafeBoundaries.ToDictionary(
                static boundary => boundary.Address
            );
        var costs = new CandidateCost[descriptors.Count];
        int priorCompleted = int.MaxValue;
        for (int index = 0; index < descriptors.Count; index++) {
            SessionContextCandidateDescriptor descriptor =
                descriptors[index];
            int completed;
            SessionContextAnchorSetupReferences authoritativeSetups;
            if (descriptor.RawStartExclusive
                == window.StartExclusive) {
                completed = 0;
                authoritativeSetups = window.StartSetups;
            }
            else if (boundaries.TryGetValue(
                    descriptor.RawStartExclusive,
                    out SessionHistoryPlanningBoundary? boundary)
                && window.ReplaySafeBoundarySetups.TryGetValue(
                    descriptor.RawStartExclusive,
                    out authoritativeSetups!
                )) {
                completed = boundary.CompletedUnitCount;
            }
            else {
                throw new InvalidDataException(
                    "A discovered context candidate anchor is not a replay-safe boundary on the authoritative raw interval."
                );
            }
            if (authoritativeSetups != descriptor.AnchorSetups) {
                throw new InvalidDataException(
                    "A discovered context candidate setup snapshot does not match raw authority."
                );
            }
            if (completed >= priorCompleted) {
                throw new InvalidDataException(
                    "Context candidate ordinals do not follow progressively older raw anchors."
                );
            }
            priorCompleted = completed;
            costs[index] = new CandidateCost(
                descriptor,
                completed,
                SumUnitTokens(window.Units, completed)
            );
        }
        if (costs[^1].Descriptor.RawStartExclusive
            != window.StartExclusive) {
            throw new InvalidDataException(
                "The oldest discovered context candidate does not match the bounded authority start."
            );
        }
        return costs;
    }

    private static IEnumerable<CandidateCost> SelectCandidateAttempts(
        SessionContextSelectionRequest request,
        IReadOnlyList<CandidateCost> costs
    ) {
        bool FitsRaw(CandidateCost candidate)
            => request.RawSuffixTokenBudget is not long budget
                || candidate.RawSuffixTokens <= budget;

        return request.Mode switch {
            SessionContextSelectionMode.Latest =>
                FitsRaw(costs[0])
                    ? new[] { costs[0] }
                    : Array.Empty<CandidateCost>(),
            SessionContextSelectionMode.NthPrevious =>
                request.NthPreviousOrdinal < costs.Count
                && FitsRaw(costs[request.NthPreviousOrdinal])
                    ? new[] { costs[request.NthPreviousOrdinal] }
                    : Array.Empty<CandidateCost>(),
            SessionContextSelectionMode.Budgeted =>
                costs.Where(FitsRaw)
                    .OrderByDescending(
                        static candidate =>
                            candidate.Descriptor.Ordinal
                    )
                    .ToArray(),
            _ => throw new ArgumentOutOfRangeException(
                nameof(request.Mode),
                request.Mode,
                "Unsupported context selection mode."
            )
        };
    }

    private static long SumUnitTokens(
        IReadOnlyList<SessionHistoryPlanningUnit> units,
        int startIndex
    ) {
        long total = 0;
        for (int index = startIndex; index < units.Count; index++) {
            total = checked(
                total
                + SessionHistoryTokenEstimator.Estimate(
                    units[index].Message
                )
            );
        }
        return total;
    }

    private static HashSet<EventAddress> CreateAllowedSourceHeads(
        SessionHistoryPlanningWindow window,
        int completedUnitCount,
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
        // Completed unit count is validated independently against the replay-safe boundary map.
        _ = completedUnitCount;
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

    private static long EstimateCandidateRequestTokens(
        SessionRuntime runtime,
        SessionGoverningSetup governingSetup,
        ImmutableArray<ToolDefinition> tools,
        SessionHistoryPlanningWindow window,
        int completedUnitCount,
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
                - completedUnitCount
                + (projectedMessage is null ? 0 : 1)
            );
        context.AddRange(header);
        for (int index = completedUnitCount;
             index < window.Units.Count;
             index++) {
            context.Add(window.Units[index].Message);
        }
        if (projectedMessage is not null) {
            context.Add(projectedMessage);
        }
        return SessionHistoryTokenEstimator.EstimateCanonicalRequest(
            new CompletionRequest(
                governingSetup.RuntimeConfig.ModelId,
                systemPrompt,
                context.MoveToImmutable(),
                tools,
                runtime.MaxTokens
            )
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

    private sealed record SelectedContextCandidate(
        SessionContextCandidate Candidate,
        bool IsBootstrap,
        SessionHistoryPlanningWindow Window,
        int CompletedUnitCount
    );

    private sealed record CandidateCost(
        SessionContextCandidateDescriptor Descriptor,
        int CompletedUnitCount,
        long RawSuffixTokens
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
