using Atelia.EventJournal;

namespace Atelia.SessionJournal;

public sealed partial class SessionJournalEngine {
    private readonly AsyncLocal<LifecycleAuditToken?>
        _lifecycleAuditToken = new();
    private readonly AsyncLocal<SessionSelectedLineageDerivedAuditToken?>
        _derivedAuditToken = new();

    internal bool IsSelectedLineageForwardCursorBoundTo(
        SessionSelectedLineageForwardCursor cursor,
        string repositoryPath,
        RefId refId,
        EventAddress capturedHead
    ) {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(cursor);
        RequireOfflineAuditCursor(cursor);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        if (!ReferenceEquals(cursor.Owner, this)
            || cursor.IsDisposed
            || cursor.InspectionExhausted) {
            return false;
        }
        return string.Equals(
                System.IO.Path.TrimEndingDirectorySeparator(
                    System.IO.Path.GetFullPath(repositoryPath)
                ),
                System.IO.Path.TrimEndingDirectorySeparator(
                    System.IO.Path.GetFullPath(Path)
                ),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal)
            && cursor.Authority.Capture.BranchRefId == refId
            && cursor.Authority.Capture.CapturedHead == capturedHead;
    }

    internal EventAddress?
        ReadSelectedLineageForwardCursorCurrentHead(
        SessionSelectedLineageForwardCursor cursor
    ) {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(cursor);
        RequireOfflineAuditCursor(cursor);
        if (!ReferenceEquals(cursor.Owner, this)
            || cursor.IsDisposed) {
            throw new ArgumentException(
                "Forward cursor is unavailable for raw-head fencing.",
                nameof(cursor)
            );
        }
        EventAddress? observed = ReadCurrentHead();
        return _testHooks.RewriteForwardCursorObservedHead
            ?.Invoke(observed)
            ?? observed;
    }

    /// <summary>
    /// Begins an explicitly requested offline audit of the complete selected
    /// Parent lineage. Normal online/read-view paths cannot call this API.
    /// </summary>
    public SessionSelectedLineageAuditSession BeginSelectedLineageAudit(
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        RequireOfflineAuditEngine();
        return BeginSelectedLineageAuditCore(
            ownerBoundLifecycleAudit: false,
            cancellationToken);
    }

    private SessionSelectedLineageAuditSession
        BeginSelectedLineageAuditCore(
        bool ownerBoundLifecycleAudit,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        EventAddress capturedHead = _journal.GetHead(_branchRefId)
            ?? throw new InvalidOperationException(
                "Selected-lineage audit requires a non-empty SessionJournal."
            );
        var capture = new SessionSelectedLineageAuditCapture(
            _branchName,
            _branchRefId,
            capturedHead
        );
        RequireSelectedLineageCaptureCurrent(capture);
        return new SessionSelectedLineageAuditSession(
            this,
            capture,
            ownerBoundLifecycleAudit
        );
    }

    /// <summary>
    /// Captures one bounded, complete selected-lineage snapshot only while
    /// this mutable engine is invoking its exact lifecycle coordinator. The
    /// returned snapshot remains owner-bound and can open cursors only inside
    /// that same callback scope.
    /// </summary>
    public SessionSelectedLineageAuditSnapshotCaptureResult
        CaptureSelectedLineageAuditSnapshot(
        int maximumEvents,
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        if (_isReadOnly) {
            throw new InvalidOperationException(
                "Lifecycle audit capture requires the mutable SessionJournal owner."
            );
        }
        if (maximumEvents is < 1 or > 1_048_576) {
            throw new ArgumentOutOfRangeException(nameof(maximumEvents));
        }
        LifecycleAuditToken token = RequireLifecycleAuditToken();
        if (Interlocked.CompareExchange(ref token.ActiveCapture, 1, 0) != 0) {
            return new SessionSelectedLineageAuditSnapshotCaptureResult.Busy();
        }
        try {
            return CaptureSelectedLineageAuditSnapshotCore(
                maximumEvents,
                cancellationToken);
        }
        finally {
            Volatile.Write(ref token.ActiveCapture, 0);
        }
    }

    internal SessionSelectedLineageAuditSnapshotCaptureResult
        CaptureSelectedLineageAuditSnapshotForDerivedSidecar(
        SessionSelectedLineageDerivedAuditToken token,
        int maximumEvents,
        CancellationToken cancellationToken = default
    ) => ExecuteDerivedSelectedLineageAudit(
        token,
        "SessionJournal.CaptureSelectedLineageAuditForDerivedSidecar",
        () => {
            if (maximumEvents is < 1 or > 1_048_576) {
                throw new ArgumentOutOfRangeException(nameof(maximumEvents));
            }
            if (Interlocked.CompareExchange(
                    ref token.ActiveCapture, 1, 0) != 0) {
                return new SessionSelectedLineageAuditSnapshotCaptureResult
                    .Busy();
            }
            try {
                return CaptureSelectedLineageAuditSnapshotCore(
                    maximumEvents,
                    cancellationToken);
            }
            finally {
                Volatile.Write(ref token.ActiveCapture, 0);
            }
        });

    internal T ExecuteDerivedSelectedLineageAudit<T>(
        SessionSelectedLineageDerivedAuditToken token,
        string operation,
        Func<T> callback
    ) {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(callback);
        if (!ReferenceEquals(token.Owner, this) || !token.IsActive) {
            throw new InvalidOperationException(
                "The derived selected-lineage audit token is inactive or belongs to another owner.");
        }
        return ExecuteDerivedSidecarMutation(
            operation,
            _ => {
                if (_derivedAuditToken.Value is not null) {
                    throw new InvalidOperationException(
                        "Derived selected-lineage audit scopes cannot be nested.");
                }
                _derivedAuditToken.Value = token;
                try {
                    return callback();
                }
                finally {
                    _derivedAuditToken.Value = null;
                }
            });
    }

    private SessionSelectedLineageAuditSnapshotCaptureResult
        CaptureSelectedLineageAuditSnapshotCore(
        int maximumEvents,
        CancellationToken cancellationToken
    ) {
        try {
            EventAddress expected = ReadCurrentHead()
                ?? throw new InvalidOperationException(
                    "Owner-bound audit requires a non-empty SessionJournal."
                );
            _testHooks.AfterLifecycleAuditExpectedHeadCaptured
                ?.Invoke(_journal);
            SessionSelectedLineageAuditSession session =
                BeginSelectedLineageAuditCore(
                    ownerBoundLifecycleAudit: true,
                    cancellationToken);
            if (session.Capture.CapturedHead != expected) {
                return new SessionSelectedLineageAuditSnapshotCaptureResult
                    .RawHeadChanged(expected, session.Capture.CapturedHead);
            }
            var pages = new List<SessionSelectedLineageAuditPage>();
            while (!session.IsCaptureComplete) {
                cancellationToken.ThrowIfCancellationRequested();
                long remaining = maximumEvents - session.EventCount;
                int pageSize = (int)Math.Min(
                    SessionSelectedLineageAuditLimits.MaximumPageEventCount,
                    Math.Max(1, remaining + 1));
                SessionSelectedLineageAuditPage page = session.ReadNextPage(
                    pageSize, cancellationToken);
                if (session.EventCount > maximumEvents) {
                    return new SessionSelectedLineageAuditSnapshotCaptureResult
                        .LimitExceeded(maximumEvents, session.EventCount);
                }
                pages.Add(page);
            }
            _ = session.Complete(cancellationToken);
            EventAddress? observed = ReadCurrentHead();
            if (observed != expected) {
                return new SessionSelectedLineageAuditSnapshotCaptureResult
                    .RawHeadChanged(expected, observed);
            }
            return new SessionSelectedLineageAuditSnapshotCaptureResult
                .Available(new SessionSelectedLineageAuditSnapshot(
                    this,
                    session.Capture,
                    pages.AsReadOnly()
                ));
        }
        catch (SessionSelectedLineageAuditChangedException changed) {
            return new SessionSelectedLineageAuditSnapshotCaptureResult
                .RawHeadChanged(changed.ExpectedHead, changed.ObservedHead);
        }
    }

    /// <summary>
    /// Replays already committed audit pages against raw authority before
    /// resuming at their exact continuation. Persisted pages never confer
    /// authority by themselves.
    /// </summary>
    public SessionSelectedLineageAuditSession ResumeSelectedLineageAudit(
        SessionSelectedLineageAuditCapture capture,
        IEnumerable<SessionSelectedLineageAuditPage> committedPages,
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        RequireOfflineAuditEngine();
        return ResumeSelectedLineageAuditCore(
            capture,
            committedPages,
            ownerBoundLifecycleAudit: false,
            cancellationToken);
    }

    private SessionSelectedLineageAuditSession
        ResumeSelectedLineageAuditCore(
        SessionSelectedLineageAuditCapture capture,
        IEnumerable<SessionSelectedLineageAuditPage> committedPages,
        bool ownerBoundLifecycleAudit,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(committedPages);
        RequireSelectedLineageCaptureCurrent(capture);

        var session = new SessionSelectedLineageAuditSession(
            this,
            capture,
            ownerBoundLifecycleAudit
        );
        foreach (SessionSelectedLineageAuditPage committed
                 in committedPages) {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(committed);
            if (committed.Ordinal != session.CommittedPageCount) {
                throw new InvalidDataException(
                    "Selected-lineage audit page ordinals are not contiguous."
                );
            }
            if (committed.HeadToOldest.Count is <= 0
                or > SessionSelectedLineageAuditLimits
                    .MaximumPageEventCount) {
                throw new InvalidDataException(
                    "Selected-lineage audit page entry count is outside the allowed range."
                );
            }
            SessionSelectedLineageAuditPage observed =
                session.ReadNextPage(
                    committed.HeadToOldest.Count,
                    cancellationToken
                );
            if (!SelectedLineagePagesEqual(observed, committed)) {
                throw new InvalidDataException(
                    $"Selected-lineage audit page {committed.Ordinal} does not match raw authority."
                );
            }
        }
        RequireSelectedLineageCaptureCurrent(capture);
        return session;
    }

    internal SessionSelectedLineageAuditPage
        ReadSelectedLineageAuditPage(
        SessionSelectedLineageAuditSession session,
        int maxEventCount,
        CancellationToken cancellationToken
    ) {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(session);
        RequireAuditSession(session);
        if (!ReferenceEquals(session.Owner, this)) {
            throw new ArgumentException(
                "Selected-lineage audit session belongs to another engine.",
                nameof(session)
            );
        }
        if (maxEventCount is <= 0
            or > SessionSelectedLineageAuditLimits
                .MaximumPageEventCount) {
            throw new ArgumentOutOfRangeException(
                nameof(maxEventCount),
                $"Audit page size must be between 1 and {SessionSelectedLineageAuditLimits.MaximumPageEventCount}."
            );
        }
        if (session.AuthorityIssued) {
            throw new InvalidOperationException(
                "Selected-lineage audit authority was already issued."
            );
        }
        EventAddress pageHead = session.NextAddress
            ?? throw new InvalidOperationException(
                "Selected-lineage audit capture is already complete."
            );
        RequireSelectedLineageCaptureCurrent(session.Capture);

        var entries = new List<SessionSelectedLineageAuditEntry>(
            maxEventCount
        );
        EventAddress? cursor = pageHead;
        ulong? childSequenceExclusive =
            session.ChildSequenceExclusive;
        EventAddress? sessionCreatedAddress = null;
        SessionContextAnchorSetupReferences? sessionCreatedSetups = null;
        SessionContextSetupReference? pageRuntimeSetup = null;
        SessionContextSetupReference? pageSystemPromptSetup = null;
        while (cursor is { } address
               && entries.Count < maxEventCount) {
            cancellationToken.ThrowIfCancellationRequested();
            using SessionJournalEventFrame frame =
                _reader.ReadEvent(address).Unwrap();
            if (frame.Address != address) {
                throw new InvalidDataException(
                    $"Selected-lineage audit read returned the wrong address for {address}."
                );
            }
            ValidateSessionHeaderPreview(address, frame.Header);
            if (childSequenceExclusive is { } childSequence
                && frame.Header.SequenceNumber >= childSequence) {
                throw new InvalidDataException(
                    $"Selected Parent lineage sequence is not strictly decreasing at {address}."
                );
            }
            var kind =
                (SessionEventKind)frame.Header.OpaqueEventKind;
            object body = SessionEventCodec.Decode(
                kind,
                frame.Payload,
                out int bodySchemaVersion
            );
            if (SessionOperationalSemantics.IsSetupKind(kind)) {
                var reference = new SessionContextSetupReference(
                    address,
                    bodySchemaVersion,
                    SessionRequestCanonicalizer.Sha256Hex(
                        frame.Payload
                    )
                );
                if (kind == SessionEventKind.RuntimeConfigSetup) {
                    pageRuntimeSetup ??= reference;
                }
                else {
                    pageSystemPromptSetup ??= reference;
                }
            }
            if (kind == SessionEventKind.CompletionRequestPrepared) {
                SessionPreparedRequestAuditVerifier.Verify(
                    _reader,
                    address,
                    bodySchemaVersion,
                    cancellationToken
                );
            }
            if (kind == SessionEventKind.SessionCreated) {
                if (sessionCreatedAddress is not null) {
                    throw new InvalidDataException(
                        "Selected SessionJournal lineage contains multiple SessionCreated events in one audit page."
                    );
                }
                sessionCreatedAddress = address;
                sessionCreatedSetups =
                    ResolveContextAnchorSetupReferences(
                        address,
                        cancellationToken
                    );
            }
            entries.Add(new SessionSelectedLineageAuditEntry(
                address,
                frame.Header.Parent,
                frame.Header.SequenceNumber,
                kind,
                bodySchemaVersion,
                frame.Header.PayloadLength,
                SessionRequestCanonicalizer.Sha256Hex(frame.Payload)
            ));
            childSequenceExclusive = frame.Header.SequenceNumber;
            cursor = frame.Header.Parent;
        }

        var page = new SessionSelectedLineageAuditPage(
            session.CommittedPageCount,
            pageHead,
            entries.AsReadOnly(),
            cursor
        );
        RequireSelectedLineageCaptureCurrent(session.Capture);
        session.AcceptPage(
            page,
            entries[^1].SequenceNumber,
            sessionCreatedAddress,
            sessionCreatedSetups,
            pageRuntimeSetup,
            pageSystemPromptSetup
        );
        return page;
    }

    internal SessionSelectedLineageAuditAuthority
        CompleteSelectedLineageAudit(
        SessionSelectedLineageAuditSession session,
        CancellationToken cancellationToken
    ) {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(session);
        RequireAuditSession(session);
        if (!ReferenceEquals(session.Owner, this)) {
            throw new ArgumentException(
                "Selected-lineage audit session belongs to another engine.",
                nameof(session)
            );
        }
        if (!session.IsCaptureComplete) {
            throw new InvalidOperationException(
                "Selected-lineage audit cannot issue authority before reaching the raw root."
            );
        }
        if (session.AuthorityIssued) {
            throw new InvalidOperationException(
                "Selected-lineage audit authority was already issued."
            );
        }
        EventAddress rootAddress = session.RootAddress
            ?? throw new InvalidDataException(
                "Selected-lineage audit has no raw root."
            );
        EventAddress sessionCreatedAddress =
            session.SessionCreatedAddress
            ?? throw new InvalidDataException(
                "Selected lineage has no SessionCreated event."
            );
        SessionContextAnchorSetupReferences sessionCreatedSetups =
            session.SessionCreatedSetups
            ?? throw new InvalidDataException(
                "SessionCreated event has no setup provenance."
            );
        RequireSelectedLineageCaptureCurrent(session.Capture);
        SessionHistoryPlanningSeed bootstrapSeed =
            CreateHistoryPlanningSeed(
                sessionCreatedAddress,
                sessionCreatedSetups,
                cancellationToken
            );
        SessionContextAnchorSetupReferences headSetups =
            session.HeadSetups
            ?? throw new InvalidDataException(
                "Selected lineage has no complete governing setup provenance."
            );
        SessionExecutionRecovery recovery = ResolveExecutionTail(
            session.Capture.CapturedHead,
            cancellationToken
        );
        if (recovery.Head != session.Capture.CapturedHead) {
            throw new InvalidDataException(
                "Execution-tail recovery did not terminate at the captured raw head."
            );
        }
        RequireSelectedLineageCaptureCurrent(session.Capture);
        session.MarkAuthorityIssued();
        return new SessionSelectedLineageAuditAuthority(
            this,
            session.Capture,
            rootAddress,
            bootstrapSeed,
            headSetups,
            recovery.State,
            session.EventCount,
            session.LogicalPayloadBytes,
            session.MaximumResidentEntryCount
        );
    }

    public SessionSelectedLineageForwardCursor
        OpenSelectedLineageForwardCursor(
        ISessionSelectedLineageAuditPageSnapshot snapshot,
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        RequireOfflineAuditEngine();
        return OpenSelectedLineageForwardCursorCore(
            new SessionSelectedLineageSnapshotLease(
                snapshot,
                disposeSnapshotOnLastRelease: true),
            ownerBoundLifecycleAudit: false,
            cancellationToken);
    }

    internal SessionSelectedLineageForwardCursor
        OpenLifecycleSelectedLineageForwardCursor(
        SessionSelectedLineageAuditSnapshot snapshot,
        CancellationToken cancellationToken
    ) {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(snapshot);
        RequireOwnerBoundAuditToken();
        if (!ReferenceEquals(snapshot.Owner, this)
            || snapshot.IsDisposed) {
            throw new ArgumentException(
                "Lifecycle audit snapshot belongs to another owner or is disposed.",
                nameof(snapshot));
        }
        return OpenSelectedLineageForwardCursorCore(
            new SessionSelectedLineageSnapshotLease(
                snapshot,
                disposeSnapshotOnLastRelease: false),
            ownerBoundLifecycleAudit: true,
            cancellationToken);
    }

    private SessionSelectedLineageForwardCursor
        OpenSelectedLineageForwardCursorCore(
        SessionSelectedLineageSnapshotLease snapshotLease,
        bool ownerBoundLifecycleAudit,
        CancellationToken cancellationToken
    ) {
        ISessionSelectedLineageAuditPageSnapshot snapshot =
            snapshotLease.Snapshot;
        ArgumentNullException.ThrowIfNull(snapshot);
        try {
            if (snapshot.PageCount <= 0) {
                throw new InvalidDataException(
                    "A sealed selected-lineage page snapshot must contain at least one page."
                );
            }
            SessionSelectedLineageAuditSession replay =
                ResumeSelectedLineageAuditCore(
                    snapshot.Capture,
                    snapshot.ReadHeadToOldestPages(),
                    ownerBoundLifecycleAudit,
                    cancellationToken
                );
            if (!replay.IsCaptureComplete
                || replay.CommittedPageCount != snapshot.PageCount) {
                throw new InvalidDataException(
                    "Sealed selected-lineage page snapshot is incomplete."
                );
            }
            SessionSelectedLineageAuditAuthority authority =
                replay.Complete(cancellationToken);
            ValidateSelectedLineageForwardSnapshot(
                snapshot,
                authority,
                cancellationToken
            );

            IEnumerator<SessionSelectedLineageAuditEntry> entries =
                EnumerateSelectedLineageForwardEntries(snapshot)
                    .GetEnumerator();
            bool foundBootstrap = false;
            EventAddress? expectedParent = null;
            while (entries.MoveNext()) {
                SessionSelectedLineageAuditEntry entry =
                    entries.Current;
                if (entry.Parent != expectedParent) {
                    throw new InvalidDataException(
                        $"Forward spool enumeration is not contiguous at {entry.Address}."
                    );
                }
                expectedParent = entry.Address;
                if (entry.Address
                    == authority.BootstrapSeed.Address) {
                    foundBootstrap = true;
                    break;
                }
            }
            if (!foundBootstrap) {
                entries.Dispose();
                throw new InvalidDataException(
                    "Sealed selected-lineage snapshot omits the bootstrap boundary."
                );
            }
            return new SessionSelectedLineageForwardCursor(
                this,
                snapshotLease,
                authority,
                entries,
                authority.BootstrapSeed,
                ownerBoundLifecycleAudit
            );
        }
        catch {
            snapshotLease.Release();
            throw;
        }
    }

    internal SessionSelectedLineageForwardCursor
        ForkSelectedLineageForwardCursorAtBoundary(
        SessionSelectedLineageForwardCursor source,
        EventAddress boundary,
        SessionContextAnchorSetupReferences setups,
        CancellationToken cancellationToken
    ) {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        RequireOfflineAuditCursor(source);
        if (!ReferenceEquals(source.Owner, this)
            || source.IsDisposed
            || source.InspectionExhausted) {
            throw new ArgumentException(
                "The source forward cursor is unavailable for forking.",
                nameof(source));
        }
        SessionSelectedLineageForwardCursor fork =
            OpenSelectedLineageForwardCursorCore(
                source.SnapshotLease.AddReference(),
                source.OwnerBoundLifecycleAudit,
                cancellationToken);
        try {
            fork.SeekToBoundary(boundary, setups, cancellationToken);
            return fork;
        }
        catch {
            fork.Dispose();
            throw;
        }
    }

    internal SessionSelectedLineageForwardRange?
        ReadNextSelectedLineageForwardRange(
        SessionSelectedLineageForwardCursor cursor,
        int maxRawEventCount,
        CancellationToken cancellationToken
    ) {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(cursor);
        if (!ReferenceEquals(cursor.Owner, this)) {
            throw new ArgumentException(
                "Selected-lineage forward cursor belongs to another engine.",
                nameof(cursor)
            );
        }
        if (cursor.IsDisposed) {
            throw new ObjectDisposedException(nameof(cursor));
        }
        if (maxRawEventCount is <= 0
            or > SessionSelectedLineageAuditLimits
                .MaximumForwardRangeEventCount) {
            throw new ArgumentOutOfRangeException(
                nameof(maxRawEventCount)
            );
        }
        if (cursor.Finished) {
            return null;
        }
        if (cursor.InspectionExhausted) {
            throw new InvalidOperationException(
                "The forward cursor inspection is exhausted; reopen a fresh cursor."
            );
        }
        if (cursor.PendingRange is not null) {
            throw new InvalidOperationException(
                "The pending forward range must be materialized before another range can be read."
            );
        }
        RequireSelectedLineageCaptureCurrent(
            cursor.Authority.Capture
        );
        var entries = new List<SessionSelectedLineageAuditEntry>(
            maxRawEventCount
        );
        EventAddress expectedParent = cursor.CurrentSeed.Address;
        while (entries.Count < maxRawEventCount
               && cursor.MoveNext(out
                   SessionSelectedLineageAuditEntry entry)) {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Parent != expectedParent) {
                throw new InvalidDataException(
                    $"Forward spool enumeration has a gap or overlap at {entry.Address}."
                );
            }
            ValidateSelectedLineageEntryAgainstRaw(
                entry,
                reconstructPrepared: false,
                cancellationToken
            );
            entries.Add(entry);
            expectedParent = entry.Address;
        }
        if (entries.Count == 0) {
            if (expectedParent
                != cursor.Authority.Capture.CapturedHead) {
                throw new InvalidDataException(
                    "Forward spool enumeration ended before the captured raw head."
                );
            }
            throw new InvalidDataException(
                "Forward spool enumeration ended without issuing a final range."
            );
        }
        bool isFinal = entries[^1].Address
            == cursor.Authority.Capture.CapturedHead;
        RequireSelectedLineageCaptureCurrent(
            cursor.Authority.Capture
        );
        var range = new SessionSelectedLineageForwardRange(
            cursor.Authority,
            entries[0].Parent!.Value,
            entries.AsReadOnly(),
            isFinal
        );
        cursor.SetPending(range);
        return range;
    }

    internal SessionSelectedLineageForwardRange
        ExtendPendingSelectedLineageForwardRange(
        SessionSelectedLineageForwardCursor cursor,
        SessionSelectedLineageForwardRange exactPending,
        int maxTotalRawEventCount,
        CancellationToken cancellationToken
    ) {
        ValidateForwardCursorRange(cursor, exactPending);
        if (maxTotalRawEventCount is <= 0
            or > SessionSelectedLineageAuditLimits
                .MaximumForwardRangeEventCount
            || maxTotalRawEventCount
                < exactPending.Entries.Count) {
            throw new ArgumentOutOfRangeException(
                nameof(maxTotalRawEventCount)
            );
        }
        RequireSelectedLineageCaptureCurrent(
            cursor.Authority.Capture
        );
        var entries = new List<SessionSelectedLineageAuditEntry>(
            maxTotalRawEventCount
        );
        EventAddress expectedParent = exactPending.StartExclusive;
        ulong priorSequence = 0;
        foreach (SessionSelectedLineageAuditEntry entry
                 in exactPending.Entries) {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Parent != expectedParent
                || entry.SequenceNumber <= priorSequence) {
                throw new InvalidDataException(
                    $"Pending forward range has a gap, overlap, or invalid sequence at {entry.Address}."
                );
            }
            ValidateSelectedLineageEntryAgainstRaw(
                entry,
                reconstructPrepared: false,
                cancellationToken
            );
            entries.Add(entry);
            expectedParent = entry.Address;
            priorSequence = entry.SequenceNumber;
        }

        bool consumedNewEntry = false;
        try {
            bool isFinal = exactPending.IsFinal;
            while (!isFinal
                   && entries.Count < maxTotalRawEventCount) {
                cancellationToken.ThrowIfCancellationRequested();
                if (!cursor.MoveNext(out
                        SessionSelectedLineageAuditEntry entry)) {
                    break;
                }
                consumedNewEntry = true;
                _testHooks.AfterPendingRangeExtendEntryRead?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                entry = _testHooks.RewritePendingRangeExtendEntry?.Invoke(
                    entry) ?? entry;
                if (entry.Parent != expectedParent
                    || entry.SequenceNumber <= priorSequence) {
                    throw new InvalidDataException(
                        $"Forward spool enumeration has a gap, overlap, or invalid sequence at {entry.Address}."
                    );
                }
                ValidateSelectedLineageEntryAgainstRaw(
                    entry,
                    reconstructPrepared: false,
                    cancellationToken
                );
                entries.Add(entry);
                expectedParent = entry.Address;
                priorSequence = entry.SequenceNumber;
                isFinal = entry.Address
                    == cursor.Authority.Capture.CapturedHead;
            }
            if (!isFinal
                && entries.Count < maxTotalRawEventCount) {
                throw new InvalidDataException(
                    "Forward spool enumeration ended before the captured raw head."
                );
            }

            RequireSelectedLineageCaptureCurrent(
                cursor.Authority.Capture,
                _testHooks.RewritePendingRangeExtendObservedHead
            );
            var replacement = new SessionSelectedLineageForwardRange(
                cursor.Authority,
                exactPending.StartExclusive,
                entries.AsReadOnly(),
                isFinal
            );
            cursor.ReplacePending(exactPending, replacement);
            return replacement;
        }
        catch {
            if (consumedNewEntry) {
                cursor.InvalidateForwardEnumeration();
            }
            throw;
        }
    }

    internal SessionHistoryPlanningWindow
        MaterializeSelectedLineageForwardRange(
        SessionSelectedLineageForwardCursor cursor,
        SessionSelectedLineageForwardRange range,
        CancellationToken cancellationToken
    ) {
        ValidateForwardCursorRange(cursor, range);
        SessionHistoryPlanningWindow window =
            MaterializeSelectedLineageForwardEntries(
                cursor,
                range.Entries,
                cancellationToken
            );
        SessionHistoryPlanningSeed? nextSeed = range.IsFinal
            ? null
            : CreateHistoryPlanningSeed(
                range.EndInclusive,
                window.EndSetups,
                cancellationToken
            );
        cursor.Advance(range, nextSeed);
        return window;
    }

    internal SessionHistoryPlanningWindow
        PreviewSelectedLineageForwardRange(
        SessionSelectedLineageForwardCursor cursor,
        SessionSelectedLineageForwardRange range,
        CancellationToken cancellationToken
    ) {
        ValidateForwardCursorRange(cursor, range);
        if (ReferenceEquals(cursor.PreviewedRange, range)
            && cursor.PreviewedWindow is { } existing
            && existing.ObservedRawHead == range.EndInclusive) {
            return existing;
        }
        SessionHistoryPlanningWindow window =
            MaterializeSelectedLineageForwardEntries(
                cursor,
                range.Entries,
                cancellationToken
            );
        cursor.SetPreview(range, window);
        return window;
    }

    internal SessionSelectedLineageForwardConsumption
        ConsumePreviewedSelectedLineagePrefix(
        SessionSelectedLineageForwardCursor cursor,
        SessionSelectedLineageForwardRange range,
        EventAddress endInclusive,
        CancellationToken cancellationToken
    ) {
        ValidateForwardCursorRange(cursor, range);
        if (!ReferenceEquals(cursor.PreviewedRange, range)
            || cursor.PreviewedWindow is null) {
            throw new InvalidOperationException(
                "Forward range must be previewed before consuming a prefix."
            );
        }
        int endIndex = -1;
        for (int index = 0; index < range.Entries.Count; index++) {
            if (range.Entries[index].Address == endInclusive) {
                endIndex = index;
                break;
            }
        }
        if (endIndex < 0
            || !cursor.PreviewedWindow.ReplaySafeBoundaries.Any(
                boundary => boundary.Address == endInclusive)) {
            throw new ArgumentException(
                "Consumed forward prefix must end at a replay-safe boundary inside the pending range.",
                nameof(endInclusive)
            );
        }
        int prefixCount = checked(endIndex + 1);
        SessionHistoryPlanningWindow window =
            prefixCount == range.Entries.Count
                ? cursor.PreviewedWindow
                : MaterializeSelectedLineageForwardEntries(
                    cursor,
                    Array.AsReadOnly([
                        .. range.Entries.Take(prefixCount)
                    ]),
                    cancellationToken
                );
        SessionSelectedLineageForwardRange? remaining =
            prefixCount == range.Entries.Count
                ? null
                : new SessionSelectedLineageForwardRange(
                    cursor.Authority,
                    endInclusive,
                    Array.AsReadOnly([
                        .. range.Entries.Skip(prefixCount)
                    ]),
                    range.IsFinal
                );
        SessionHistoryPlanningSeed nextSeed =
            CreateHistoryPlanningSeed(
                endInclusive,
                window.EndSetups,
                cancellationToken
            );
        cursor.AdvancePrefix(range, nextSeed, remaining);
        return new SessionSelectedLineageForwardConsumption(
            window,
            remaining
        );
    }

    internal void SeekSelectedLineageForwardCursor(
        SessionSelectedLineageForwardCursor cursor,
        EventAddress boundary,
        SessionContextAnchorSetupReferences setups,
        CancellationToken cancellationToken
    ) {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(cursor);
        RequireOfflineAuditCursor(cursor);
        ArgumentNullException.ThrowIfNull(setups);
        if (!ReferenceEquals(cursor.Owner, this)
            || cursor.IsDisposed
            || cursor.PendingRange is not null) {
            throw new ArgumentException(
                "Forward cursor is unavailable for seeking.",
                nameof(cursor)
            );
        }
        if (cursor.InspectionExhausted
            || cursor.IsForwardEnumerationInvalid) {
            throw new InvalidOperationException(
                "The forward cursor inspection is exhausted or invalid; reopen a fresh cursor."
            );
        }
        if (boundary == cursor.CurrentSeed.Address) {
            if (setups != cursor.CurrentSeed.Setups) {
                throw new InvalidDataException(
                    "Seek setup authority differs at the current boundary."
                );
            }
            return;
        }
        SessionContextSetupReference runtime =
            cursor.CurrentSeed.Setups.RuntimeConfig;
        SessionContextSetupReference prompt =
            cursor.CurrentSeed.Setups.SystemPrompt;
        bool found = false;
        while (cursor.MoveNext(out SessionSelectedLineageAuditEntry entry)) {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateSelectedLineageEntryAgainstRaw(
                entry,
                reconstructPrepared: false,
                cancellationToken
            );
            if (entry.Kind == SessionEventKind.RuntimeConfigSetup) {
                runtime = new SessionContextSetupReference(
                    entry.Address,
                    entry.BodySchemaVersion,
                    entry.PayloadSha256
                );
            }
            else if (entry.Kind == SessionEventKind.SystemPromptSetup) {
                prompt = new SessionContextSetupReference(
                    entry.Address,
                    entry.BodySchemaVersion,
                    entry.PayloadSha256
                );
            }
            if (entry.Address == boundary) {
                found = true;
                break;
            }
        }
        if (!found) {
            throw new InvalidDataException(
                "Seek boundary is not a forward member of the audited selected lineage."
            );
        }
        var observed = new SessionContextAnchorSetupReferences(
            runtime,
            prompt
        );
        if (observed != setups) {
            throw new InvalidDataException(
                "Seek boundary setup authority differs from audited selected-lineage provenance."
            );
        }
        RequireSelectedLineageCaptureCurrent(cursor.Authority.Capture);
        SessionHistoryPlanningSeed seed = CreateHistoryPlanningSeed(
            boundary,
            setups,
            cancellationToken
        );
        cursor.Seek(
            seed,
            boundary == cursor.Authority.Capture.CapturedHead
        );
    }

    internal EventAddress? FindLatestSelectedLineageBoundary(
        SessionSelectedLineageForwardCursor cursor,
        IReadOnlySet<EventAddress> candidates,
        CancellationToken cancellationToken
    ) {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(cursor);
        RequireOfflineAuditCursor(cursor);
        ArgumentNullException.ThrowIfNull(candidates);
        if (!ReferenceEquals(cursor.Owner, this)
            || cursor.IsDisposed
            || cursor.PendingRange is not null) {
            throw new ArgumentException(
                "Forward cursor is unavailable for a membership pass.",
                nameof(cursor)
            );
        }
        if (cursor.InspectionExhausted
            || cursor.IsForwardEnumerationInvalid) {
            throw new InvalidOperationException(
                "The forward cursor inspection is exhausted or invalid; reopen a fresh cursor."
            );
        }
        if (candidates.Count
            > SessionSelectedLineageAuditLimits
                .MaximumForwardRangeEventCount
            || candidates.Contains(default)) {
            throw new ArgumentException(
                "Forward membership candidates are invalid or unbounded.",
                nameof(candidates)
            );
        }
        try {
            EventAddress? latest = candidates.Contains(
                cursor.CurrentSeed.Address
            )
                ? cursor.CurrentSeed.Address
                : null;
            while (cursor.MoveNext(out
                       SessionSelectedLineageAuditEntry entry)) {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateSelectedLineageEntryAgainstRaw(
                    entry,
                    reconstructPrepared: false,
                    cancellationToken
                );
                if (candidates.Contains(entry.Address)) {
                    latest = entry.Address;
                }
            }
            RequireSelectedLineageCaptureCurrent(
                cursor.Authority.Capture
            );
            cursor.CompleteInspection();
            return latest;
        }
        catch {
            cursor.InvalidateForwardEnumeration();
            cursor.CompleteInspection();
            throw;
        }
    }

    internal SessionSelectedLineageBoundaryProbeResult
        ProbeSelectedLineageForwardBoundaries(
        SessionSelectedLineageForwardCursor cursor,
        Func<
            EventAddress,
            SessionSelectedLineageBoundaryProbeDecision
        > probe,
        CancellationToken cancellationToken
    ) {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(cursor);
        RequireOfflineAuditCursor(cursor);
        ArgumentNullException.ThrowIfNull(probe);
        if (!ReferenceEquals(cursor.Owner, this)
            || cursor.IsDisposed
            || cursor.PendingRange is not null) {
            throw new ArgumentException(
                "Forward cursor is unavailable for a bootstrap boundary probe.",
                nameof(cursor)
            );
        }
        if (cursor.InspectionExhausted
            || cursor.IsForwardEnumerationInvalid) {
            throw new InvalidOperationException(
                "The forward cursor inspection is exhausted or invalid; reopen a fresh cursor."
            );
        }
        if (cursor.CurrentBoundary
                != cursor.Authority.BootstrapSeed.Address
            || cursor.CurrentSetups
                != cursor.Authority.BootstrapSeed.Setups) {
            throw new ArgumentException(
                "A boundary probe requires a fresh cursor at its audited bootstrap seed.",
                nameof(cursor)
            );
        }

        EventAddress? latest = null;
        bool stopped = false;
        EventAddress expectedParent = cursor.CurrentBoundary;
        ulong priorSequence = 0;
        try {
            cancellationToken.ThrowIfCancellationRequested();
            SessionSelectedLineageBoundaryProbeDecision bootstrap =
                probe(cursor.CurrentBoundary);
            ValidateBoundaryProbeDecision(bootstrap);
            if (bootstrap
                == SessionSelectedLineageBoundaryProbeDecision.Match) {
                latest = cursor.CurrentBoundary;
            }
            else if (bootstrap
                == SessionSelectedLineageBoundaryProbeDecision.Stop) {
                stopped = true;
            }

            while (!stopped
                   && cursor.MoveNext(out
                       SessionSelectedLineageAuditEntry entry)) {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Parent != expectedParent
                    || entry.SequenceNumber <= priorSequence) {
                    throw new InvalidDataException(
                        $"Forward boundary probe has a gap, overlap, or invalid sequence at {entry.Address}."
                    );
                }
                ValidateSelectedLineageEntryAgainstRaw(
                    entry,
                    reconstructPrepared: false,
                    cancellationToken
                );
                expectedParent = entry.Address;
                priorSequence = entry.SequenceNumber;
                SessionSelectedLineageBoundaryProbeDecision decision =
                    probe(entry.Address);
                ValidateBoundaryProbeDecision(decision);
                if (decision
                    == SessionSelectedLineageBoundaryProbeDecision.Match) {
                    latest = entry.Address;
                }
                else if (decision
                    == SessionSelectedLineageBoundaryProbeDecision.Stop) {
                    stopped = true;
                }
            }
            if (!stopped
                && expectedParent
                    != cursor.Authority.Capture.CapturedHead) {
                throw new InvalidDataException(
                    "Forward boundary probe ended before the captured raw head."
                );
            }
            RequireSelectedLineageCaptureCurrent(
                cursor.Authority.Capture,
                _testHooks.RewriteForwardBoundaryProbeObservedHead
            );
            cursor.CompleteInspection();
            return new SessionSelectedLineageBoundaryProbeResult(
                latest,
                stopped
            );
        }
        catch {
            cursor.InvalidateForwardEnumeration();
            cursor.CompleteInspection();
            throw;
        }
    }

    private static void ValidateBoundaryProbeDecision(
        SessionSelectedLineageBoundaryProbeDecision decision
    ) {
        if (!Enum.IsDefined(decision)) {
            throw new InvalidDataException(
                "Forward boundary probe returned an unknown decision."
            );
        }
    }

    private void ValidateForwardCursorRange(
        SessionSelectedLineageForwardCursor cursor,
        SessionSelectedLineageForwardRange range
    ) {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(cursor);
        RequireOfflineAuditCursor(cursor);
        ArgumentNullException.ThrowIfNull(range);
        if (cursor.IsDisposed) {
            throw new ObjectDisposedException(nameof(cursor));
        }
        if (cursor.IsForwardEnumerationInvalid) {
            throw new InvalidOperationException(
                "The forward cursor must be reopened after an interrupted enumeration."
            );
        }
        if (cursor.InspectionExhausted) {
            throw new InvalidOperationException(
                "The forward cursor inspection is exhausted; reopen a fresh cursor."
            );
        }
        if (!ReferenceEquals(cursor.Owner, this)
            || !ReferenceEquals(range.Owner, cursor.Authority)
            || !ReferenceEquals(range, cursor.PendingRange)) {
            throw new ArgumentException(
                "Forward range is not the exact pending range of this cursor.",
                nameof(range)
            );
        }
    }

    private SessionHistoryPlanningWindow
        MaterializeSelectedLineageForwardEntries(
        SessionSelectedLineageForwardCursor cursor,
        IReadOnlyList<SessionSelectedLineageAuditEntry> entries,
        CancellationToken cancellationToken
    ) {
        SessionHistoryPlanningSeed startSeed = cursor.CurrentSeed;
        if (entries.Count == 0
            || startSeed.Address != entries[0].Parent) {
            throw new ArgumentException(
                "Planning seed does not match the forward entries start.",
                nameof(entries)
            );
        }
        EventAddress expectedParent = startSeed.Address;
        ulong priorSequence = 0;
        foreach (SessionSelectedLineageAuditEntry entry in entries) {
            if (entry.Parent != expectedParent) {
                throw new InvalidDataException(
                    $"Forward audit range has a gap or overlap at {entry.Address}."
                );
            }
            if (entry.SequenceNumber <= priorSequence) {
                throw new InvalidDataException(
                    $"Forward audit range sequence is not strictly increasing at {entry.Address}."
                );
            }
            expectedParent = entry.Address;
            priorSequence = entry.SequenceNumber;
        }
        RequireSelectedLineageCaptureCurrent(cursor.Authority.Capture);
        SessionHistoryPlanningWindowReadResult read =
            ReadHistoryPlanningWindowAtBounded(
                entries[^1].Address,
                startSeed,
                entries.Count,
                cancellationToken
            );
        SessionHistoryPlanningWindow window = read switch {
            SessionHistoryPlanningWindowReadResult.Available available
                => available.Window,
            _ => throw new InvalidDataException(
                "Validated forward audit range exceeded its declared bound."
            )
        };
        if (window.RawAddresses.Count != entries.Count
            || window.RawHashEntries.Count != entries.Count) {
            throw new InvalidDataException(
                "Materialized forward range has a different raw-event count."
            );
        }
        for (int index = 0; index < entries.Count; index++) {
            SessionSelectedLineageAuditEntry expected = entries[index];
            SessionRawRangeHashEntry actual = window.RawHashEntries[index];
            EventFrameHeader header =
                _reader.ReadEventHeaderPreview(expected.Address).Unwrap();
            ValidateSessionHeaderPreview(expected.Address, header);
            if (actual.Address != expected.Address
                || actual.Parent != expected.Parent
                || actual.EventKind != (uint)expected.Kind
                || actual.BodySchemaVersion != expected.BodySchemaVersion
                || !string.Equals(
                    actual.PayloadSha256,
                    expected.PayloadSha256,
                    StringComparison.Ordinal
                )
                || header.SequenceNumber != expected.SequenceNumber
                || header.PayloadLength != expected.LogicalPayloadBytes) {
                throw new InvalidDataException(
                    $"Materialized forward range does not match audit entry {expected.Address}."
                );
            }
        }
        RequireSelectedLineageCaptureCurrent(cursor.Authority.Capture);
        return window;
    }

    private void RequireOfflineAuditEngine() {
        if (!_isReadOnly) {
            throw new InvalidOperationException(
                "Complete selected-lineage audit requires a read-only SessionJournalEngine."
            );
        }
    }

    private void RequireAuditSession(
        SessionSelectedLineageAuditSession session
    ) {
        if (_isReadOnly) {
            return;
        }
        if (!session.OwnerBoundLifecycleAudit) {
            RequireOfflineAuditEngine();
            return;
        }
        RequireOwnerBoundAuditToken();
    }

    private void RequireOfflineAuditCursor(
        SessionSelectedLineageForwardCursor cursor
    ) {
        if (_isReadOnly) {
            return;
        }
        if (!cursor.OwnerBoundLifecycleAudit) {
            RequireOfflineAuditEngine();
            return;
        }
        RequireOwnerBoundAuditToken();
    }

    private LifecycleAuditToken RequireLifecycleAuditToken() {
        LifecycleAuditToken? token = _lifecycleAuditToken.Value;
        MutationOwnerToken? active = _activeMutationOwner;
        if (token is null
            || token.Closed != 0
            || !ReferenceEquals(token.Owner, this)
            || active is null
            || !ReferenceEquals(token.MutationOwner, active)) {
            throw new InvalidOperationException(
                "Owner-bound selected-lineage audit is available only inside the active lifecycle callback."
            );
        }
        return token;
    }

    private void RequireOwnerBoundAuditToken() {
        SessionSelectedLineageDerivedAuditToken? derived =
            _derivedAuditToken.Value;
        if (derived is not null
            && derived.IsActive
            && ReferenceEquals(derived.Owner, this)) {
            return;
        }
        _ = RequireLifecycleAuditToken();
    }

    private async ValueTask<SessionContextLifecycleResult>
        InvokeLifecycleWithAuditScopeAsync(
        ISessionContextLifecycleCoordinator lifecycle,
        SessionContextLifecycleRequest request,
        CancellationToken cancellationToken
    ) {
        MutationOwnerToken owner = _activeMutationOwner
            ?? throw new InvalidOperationException(
                "Lifecycle invocation requires the active mutation owner."
            );
        if (_lifecycleAuditToken.Value is not null) {
            throw new InvalidOperationException(
                "Lifecycle audit scope cannot be nested."
            );
        }
        var token = new LifecycleAuditToken(this, owner);
        _lifecycleAuditToken.Value = token;
        try {
            return await lifecycle.PrepareAsync(
                ReadView,
                request,
                cancellationToken
            ).ConfigureAwait(false);
        }
        finally {
            Volatile.Write(ref token.Closed, 1);
            _lifecycleAuditToken.Value = null;
        }
    }

    private sealed class LifecycleAuditToken(
        SessionJournalEngine owner,
        MutationOwnerToken mutationOwner
    ) {
        internal SessionJournalEngine Owner { get; } = owner;
        internal MutationOwnerToken MutationOwner { get; } = mutationOwner;
        internal int ActiveCapture;
        internal int Closed;
    }

    private static bool SelectedLineagePagesEqual(
        SessionSelectedLineageAuditPage left,
        SessionSelectedLineageAuditPage right
    ) {
        if (left.Ordinal != right.Ordinal
            || left.PageHead != right.PageHead
            || left.Continuation != right.Continuation
            || left.HeadToOldest.Count
                != right.HeadToOldest.Count) {
            return false;
        }
        for (int index = 0;
             index < left.HeadToOldest.Count;
             index++) {
            if (left.HeadToOldest[index]
                != right.HeadToOldest[index]) {
                return false;
            }
        }
        return true;
    }

    private void ValidateSelectedLineageForwardSnapshot(
        ISessionSelectedLineageAuditPageSnapshot snapshot,
        SessionSelectedLineageAuditAuthority authority,
        CancellationToken cancellationToken
    ) {
        long expectedOrdinal = snapshot.PageCount - 1;
        long pageCount = 0;
        long eventCount = 0;
        long logicalPayloadBytes = 0;
        EventAddress? expectedParent = null;
        ulong priorSequence = 0;
        foreach (SessionSelectedLineageAuditPage page
                 in snapshot.ReadOldestToHeadPages()) {
            cancellationToken.ThrowIfCancellationRequested();
            if (page.Ordinal != expectedOrdinal
                || page.HeadToOldest.Count is <= 0
                    or > SessionSelectedLineageAuditLimits
                        .MaximumPageEventCount
                || page.PageHead
                    != page.HeadToOldest[0].Address
                || page.Continuation
                    != page.HeadToOldest[^1].Parent) {
                throw new InvalidDataException(
                    "Forward selected-lineage page structure is invalid."
                );
            }
            for (int index = page.HeadToOldest.Count - 1;
                 index >= 0;
                 index--) {
                SessionSelectedLineageAuditEntry entry =
                    page.HeadToOldest[index];
                if (entry.Parent != expectedParent
                    || entry.SequenceNumber <= priorSequence) {
                    throw new InvalidDataException(
                        $"Forward selected lineage is not root-to-head contiguous at {entry.Address}."
                    );
                }
                ValidateSelectedLineageEntryAgainstRaw(
                    entry,
                    reconstructPrepared: false,
                    cancellationToken
                );
                expectedParent = entry.Address;
                priorSequence = entry.SequenceNumber;
                eventCount = checked(eventCount + 1);
                logicalPayloadBytes = checked(
                    logicalPayloadBytes
                    + entry.LogicalPayloadBytes
                );
            }
            expectedOrdinal--;
            pageCount = checked(pageCount + 1);
        }
        if (pageCount != snapshot.PageCount
            || expectedOrdinal != -1
            || expectedParent != authority.Capture.CapturedHead
            || eventCount != authority.EventCount
            || logicalPayloadBytes
                != authority.LogicalPayloadBytes) {
            throw new InvalidDataException(
                "Forward selected-lineage snapshot does not match its complete captured authority."
            );
        }
        RequireSelectedLineageCaptureCurrent(authority.Capture);
    }

    private static IEnumerable<SessionSelectedLineageAuditEntry>
        EnumerateSelectedLineageForwardEntries(
        ISessionSelectedLineageAuditPageSnapshot snapshot
    ) {
        foreach (SessionSelectedLineageAuditPage page
                 in snapshot.ReadOldestToHeadPages()) {
            for (int index = page.HeadToOldest.Count - 1;
                 index >= 0;
                 index--) {
                yield return page.HeadToOldest[index];
            }
        }
    }

    private void ValidateSelectedLineageEntryAgainstRaw(
        SessionSelectedLineageAuditEntry entry,
        bool reconstructPrepared,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        using SessionJournalEventFrame frame =
            _reader.ReadEvent(entry.Address).Unwrap();
        ValidateSessionHeaderPreview(entry.Address, frame.Header);
        var kind =
            (SessionEventKind)frame.Header.OpaqueEventKind;
        object body = SessionEventCodec.Decode(
            kind,
            frame.Payload,
            out int bodySchemaVersion
        );
        if (reconstructPrepared
            && kind == SessionEventKind.CompletionRequestPrepared) {
            SessionPreparedRequestAuditVerifier.Verify(
                _reader,
                entry.Address,
                bodySchemaVersion,
                cancellationToken
            );
        }
        if (frame.Address != entry.Address
            || frame.Header.Parent != entry.Parent
            || frame.Header.SequenceNumber != entry.SequenceNumber
            || kind != entry.Kind
            || bodySchemaVersion != entry.BodySchemaVersion
            || frame.Header.PayloadLength
                != entry.LogicalPayloadBytes
            || !string.Equals(
                SessionRequestCanonicalizer.Sha256Hex(
                    frame.Payload
                ),
                entry.PayloadSha256,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                $"Selected-lineage audit entry {entry.Address} does not match raw authority."
            );
        }
    }

    private void RequireSelectedLineageCaptureCurrent(
        SessionSelectedLineageAuditCapture capture,
        Func<EventAddress?, EventAddress?>? rewriteObservedHead = null
    ) {
        if (capture.BranchRefId != _branchRefId) {
            throw new SessionSelectedLineageAuditChangedException(
                SessionSelectedLineageAuditChangeKind.SourceChanged,
                capture.CapturedHead,
                _journal.GetHead(_branchRefId),
                "Selected-lineage audit branch/ref identity changed."
            );
        }
        EventAddress? observedHead = _journal.GetHead(_branchRefId);
        if (rewriteObservedHead is not null) {
            observedHead = rewriteObservedHead(observedHead);
        }
        if (observedHead != capture.CapturedHead) {
            throw new SessionSelectedLineageAuditChangedException(
                SessionSelectedLineageAuditChangeKind.RawHeadChanged,
                capture.CapturedHead,
                observedHead,
                "Selected-lineage audit raw head changed."
            );
        }
    }
}
