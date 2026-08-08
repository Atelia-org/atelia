using Atelia.EventJournal;

namespace Atelia.SessionJournal;

public sealed partial class SessionJournalEngine {
    /// <summary>
    /// Begins an explicitly requested offline audit of the complete selected
    /// Parent lineage. Normal online/read-view paths cannot call this API.
    /// </summary>
    public SessionSelectedLineageAuditSession BeginSelectedLineageAudit(
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        RequireOfflineAuditEngine();
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
            capture
        );
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
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(committedPages);
        RequireSelectedLineageCaptureCurrent(capture);

        var session = new SessionSelectedLineageAuditSession(
            this,
            capture
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
        RequireOfflineAuditEngine();
        ArgumentNullException.ThrowIfNull(session);
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
            if (body is CompletionRequestPreparedBody) {
                _ = SessionPreparedRequestReconstructor.Reconstruct(
                    _reader,
                    address,
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
        RequireOfflineAuditEngine();
        ArgumentNullException.ThrowIfNull(session);
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
        ArgumentNullException.ThrowIfNull(snapshot);
        try {
            if (snapshot.PageCount <= 0) {
                throw new InvalidDataException(
                    "A sealed selected-lineage page snapshot must contain at least one page."
                );
            }
            SessionSelectedLineageAuditSession replay =
                ResumeSelectedLineageAudit(
                    snapshot.Capture,
                    snapshot.ReadHeadToOldestPages(),
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
                snapshot,
                authority,
                entries,
                authority.BootstrapSeed
            );
        }
        catch {
            snapshot.Dispose();
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

    internal SessionHistoryPlanningWindow
        MaterializeSelectedLineageForwardRange(
        SessionSelectedLineageForwardCursor cursor,
        SessionSelectedLineageForwardRange range,
        CancellationToken cancellationToken
    ) {
        ThrowIfDisposed();
        RequireOfflineAuditEngine();
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(range);
        if (!ReferenceEquals(cursor.Owner, this)
            || !ReferenceEquals(range.Owner, cursor.Authority)
            || !ReferenceEquals(range, cursor.PendingRange)) {
            throw new ArgumentException(
                "Forward range is not the exact pending range of this cursor.",
                nameof(range)
            );
        }
        SessionHistoryPlanningSeed startSeed = cursor.CurrentSeed;
        if (startSeed.Address != range.StartExclusive) {
            throw new ArgumentException(
                "Planning seed does not match the opaque forward range start.",
                nameof(startSeed)
            );
        }
        EventAddress expectedParent = startSeed.Address;
        ulong priorSequence = 0;
        foreach (SessionSelectedLineageAuditEntry entry
                 in range.Entries) {
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

        RequireSelectedLineageCaptureCurrent(
            cursor.Authority.Capture
        );
        EventAddress endInclusive = range.EndInclusive;
        SessionHistoryPlanningWindowReadResult read =
            ReadHistoryPlanningWindowAtBounded(
                endInclusive,
                startSeed,
                range.Entries.Count,
                cancellationToken
            );
        SessionHistoryPlanningWindow window = read switch {
            SessionHistoryPlanningWindowReadResult.Available available
                => available.Window,
            SessionHistoryPlanningWindowReadResult.BeyondPrefix
                => throw new InvalidDataException(
                    "Validated forward audit range exceeded its declared bound."
                ),
            _ => throw new InvalidDataException(
                "Unknown history planning read result."
            )
        };
        if (window.RawAddresses.Count != range.Entries.Count
            || window.RawHashEntries.Count
                != range.Entries.Count) {
            throw new InvalidDataException(
                "Materialized forward range has a different raw-event count."
            );
        }
        for (int index = 0;
             index < range.Entries.Count;
             index++) {
            SessionSelectedLineageAuditEntry expected =
                range.Entries[index];
            SessionRawRangeHashEntry actual =
                window.RawHashEntries[index];
            EventFrameHeader header =
                _reader.ReadEventHeaderPreview(expected.Address)
                    .Unwrap();
            ValidateSessionHeaderPreview(expected.Address, header);
            if (actual.Address != expected.Address
                || actual.Parent != expected.Parent
                || actual.EventKind != (uint)expected.Kind
                || actual.BodySchemaVersion
                    != expected.BodySchemaVersion
                || !string.Equals(
                    actual.PayloadSha256,
                    expected.PayloadSha256,
                    StringComparison.Ordinal
                )
                || header.SequenceNumber != expected.SequenceNumber
                || header.PayloadLength
                    != expected.LogicalPayloadBytes) {
                throw new InvalidDataException(
                    $"Materialized forward range does not match audit entry {expected.Address}."
                );
            }
        }
        RequireSelectedLineageCaptureCurrent(
            cursor.Authority.Capture
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

    private void RequireOfflineAuditEngine() {
        if (!_isReadOnly) {
            throw new InvalidOperationException(
                "Complete selected-lineage audit requires a read-only SessionJournalEngine."
            );
        }
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
            && body is CompletionRequestPreparedBody) {
            _ = SessionPreparedRequestReconstructor.Reconstruct(
                _reader,
                entry.Address,
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
        SessionSelectedLineageAuditCapture capture
    ) {
        if (capture.BranchRefId != _branchRefId) {
            throw new SessionSelectedLineageAuditChangedException(
                SessionSelectedLineageAuditChangeKind.SourceChanged,
                capture.CapturedHead,
                _journal.GetHead(_branchRefId),
                "Selected-lineage audit branch/ref identity changed."
            );
        }
        EventAddress? observedHead =
            _journal.GetHead(_branchRefId);
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
