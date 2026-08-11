namespace Atelia.SessionJournal.HistoryTimeline;

public sealed class HistoryTimelineReader {
    private readonly string _canonicalRepositoryPath;
    private readonly IHistoryTimelineLedgerPort _ledger;
    private readonly HistoryTimelineLifetime _lifetime;

    internal HistoryTimelineReader(
        string canonicalRepositoryPath,
        IHistoryTimelineLedgerPort ledger,
        HistoryTimelineLifetime lifetime
    ) {
        _canonicalRepositoryPath = canonicalRepositoryPath;
        _ledger = ledger;
        _lifetime = lifetime;
    }

    public HistoryTimelineSnapshotResult ReadSnapshot() {
        using HistoryTimelineLifetime.Operation? operation =
            _lifetime.TryEnterOperation();
        if (operation is null) {
            return DisposedSnapshot();
        }
        return _ledger.ReadSnapshot() switch {
            HistoryTimelineStoreReadResult<TimelineHeadRef>.Found found
                => new HistoryTimelineSnapshotResult.Available(
                    found.Value
                ),
            HistoryTimelineStoreReadResult<TimelineHeadRef>.Busy
                => new HistoryTimelineSnapshotResult.Busy(),
            HistoryTimelineStoreReadResult<TimelineHeadRef>
                .UnsupportedSchema unsupported
                => new HistoryTimelineSnapshotResult.UnsupportedSchema(
                    unsupported.SchemaVersion
                ),
            HistoryTimelineStoreReadResult<TimelineHeadRef>.Invalid invalid
                => new HistoryTimelineSnapshotResult.Invalid(
                    invalid.Code,
                    invalid.Detail
                ),
            _ => new HistoryTimelineSnapshotResult.Invalid(
                "TimelineHeadUnavailable",
                "The Timeline ledger has no canonical head."
            )
        };
    }

    public HistoryTimelineReaderRowResult ReadSelectedRow(
        TimelineHeadRef expectedWholeHead,
        HistoryRowId rowId
    ) {
        ArgumentNullException.ThrowIfNull(expectedWholeHead);
        using HistoryTimelineLifetime.Operation? operation =
            _lifetime.TryEnterOperation();
        if (operation is null) {
            return DisposedRow();
        }
        return _ledger.ReadSelectedRow(
            expectedWholeHead,
            rowId
        ) switch {
            SelectedHistoryRowResult.Selected selected
                => Selected(expectedWholeHead, selected.Descriptor),
            SelectedHistoryRowResult.NotOnSelectedPath missing
                => new HistoryTimelineReaderRowResult
                    .NotOnSelectedPath(missing.RowId),
            SelectedHistoryRowResult.StaleTimelineHead stale
                => new HistoryTimelineReaderRowResult
                    .StaleTimelineHead(stale.Actual),
            SelectedHistoryRowResult.BackendBusy
                => new HistoryTimelineReaderRowResult.Busy(),
            SelectedHistoryRowResult.Invalid invalid
                => new HistoryTimelineReaderRowResult.Invalid(
                    invalid.Code,
                    invalid.Detail
                ),
            _ => new HistoryTimelineReaderRowResult.Invalid(
                "SelectedRowOutcomeInvalid",
                "The ledger returned an unknown selected-row outcome."
            )
        };
    }

    public HistoryTimelineReaderRowResult ValidateWitness(
        TimelineHeadRef expectedWholeHead,
        HistoryTimelineAncestorWitness witness
    ) {
        ArgumentNullException.ThrowIfNull(expectedWholeHead);
        ArgumentNullException.ThrowIfNull(witness);
        using HistoryTimelineLifetime.Operation? operation =
            _lifetime.TryEnterOperation();
        if (operation is null) {
            return DisposedRow();
        }
        if (!string.Equals(
                witness.CanonicalRepositoryPath,
                _canonicalRepositoryPath,
                RepositoryPathComparison)
            || witness.WholeHead != expectedWholeHead) {
            return new HistoryTimelineReaderRowResult.Invalid(
                "AncestorWitnessScopeMismatch",
                "The ancestor witness belongs to another repository or whole head."
            );
        }
        HistoryTimelineReaderRowResult result = ReadSelectedRow(
            expectedWholeHead,
            witness.RowId
        );
        if (result is HistoryTimelineReaderRowResult.Selected selected
            && selected.Row.Descriptor.DescriptorDigest
                != witness.DescriptorDigest) {
            return new HistoryTimelineReaderRowResult.Invalid(
                "AncestorWitnessDigestMismatch",
                "The selected descriptor differs from the witness commitment."
            );
        }
        return result;
    }

    public HistoryTimelinePathPageResult ReadSelectedPathPage(
        TimelineHeadRef expectedWholeHead,
        HistoryTimelinePathCursor? cursor = null,
        int maximumRows = HistoryTimelineStoreLimits
            .MaximumPathPageRows
    ) {
        ArgumentNullException.ThrowIfNull(expectedWholeHead);
        using HistoryTimelineLifetime.Operation? operation =
            _lifetime.TryEnterOperation();
        if (operation is null) {
            return new HistoryTimelinePathPageResult.Invalid(
                "HistoryTimelineDisposed",
                "The HistoryTimeline handle has been disposed."
            );
        }
        if (maximumRows is < 1
            or > HistoryTimelineStoreLimits.MaximumPathPageRows) {
            return new HistoryTimelinePathPageResult.Invalid(
                "PathPageLimitInvalid",
                "Path pages must use the code-owned row bound."
            );
        }
        HistoryRowId? startAt = null;
        if (cursor is { } value) {
            if (value.TimelineId != expectedWholeHead.TimelineId
                || value.RefId != expectedWholeHead.RefId) {
                return new HistoryTimelinePathPageResult.Invalid(
                    "PathCursorScopeMismatch",
                    "The opaque path cursor belongs to another Timeline scope."
                );
            }
            if (value.Generation != expectedWholeHead.Generation) {
                return new HistoryTimelinePathPageResult.StaleTimelineHead(
                    expectedWholeHead
                );
            }
            startAt = value.NextRowId;
        }
        HistoryTimelineStorePathPageResult stored =
            _ledger.ReadSelectedPathPage(
                expectedWholeHead,
                startAt,
                maximumRows
            );
        switch (stored) {
            case HistoryTimelineStorePathPageResult.StaleTimelineHead stale:
                return new HistoryTimelinePathPageResult
                    .StaleTimelineHead(stale.Actual);
            case HistoryTimelineStorePathPageResult.Busy:
                return new HistoryTimelinePathPageResult.Busy();
            case HistoryTimelineStorePathPageResult.Invalid invalid:
                return new HistoryTimelinePathPageResult.Invalid(
                    invalid.Code,
                    invalid.Detail
                );
            case HistoryTimelineStorePathPageResult.Page page:
                var rows = new List<HistoryTimelineSelectedRow>(
                    page.Rows.Count
                );
                int canonicalBytes = 0;
                foreach (HistorySegmentDescriptor descriptor
                         in page.Rows) {
                    canonicalBytes = checked(
                        canonicalBytes
                        + descriptor.ToCanonicalBytes().Length
                    );
                    if (canonicalBytes
                        > HistoryTimelineStoreLimits
                            .MaximumPathPageUtf8Bytes) {
                        return new HistoryTimelinePathPageResult.Invalid(
                            "PathPageByteLimitExceeded",
                            "The selected path page exceeds the code-owned byte bound."
                        );
                    }
                    rows.Add(CreateSelectedRow(
                        expectedWholeHead,
                        descriptor
                    ));
                }
                HistoryTimelinePathCursor? next = page.Next is { } nextRow
                    ? new HistoryTimelinePathCursor(
                        expectedWholeHead.TimelineId,
                        expectedWholeHead.RefId,
                        expectedWholeHead.Generation,
                        nextRow
                    )
                    : null;
                return new HistoryTimelinePathPageResult.Page(
                    new HistoryTimelinePathPage(
                        rows.AsReadOnly(),
                        next
                    )
                );
            default:
                return new HistoryTimelinePathPageResult.Invalid(
                    "PathPageOutcomeInvalid",
                    "The ledger returned an unknown path-page outcome."
                );
        }
    }

    private HistoryTimelineReaderRowResult.Selected Selected(
        TimelineHeadRef expectedWholeHead,
        HistorySegmentDescriptor descriptor
    ) => new(CreateSelectedRow(expectedWholeHead, descriptor));

    private HistoryTimelineSelectedRow CreateSelectedRow(
        TimelineHeadRef expectedWholeHead,
        HistorySegmentDescriptor descriptor
    ) => new(
        descriptor,
        new HistoryTimelineAncestorWitness(
            _canonicalRepositoryPath,
            expectedWholeHead,
            descriptor
        )
    );

    private static HistoryTimelineSnapshotResult.Invalid
        DisposedSnapshot() => new(
            "HistoryTimelineDisposed",
            "The HistoryTimeline handle has been disposed."
        );

    private static HistoryTimelineReaderRowResult.Invalid
        DisposedRow() => new(
            "HistoryTimelineDisposed",
            "The HistoryTimeline handle has been disposed."
        );

    private static StringComparison RepositoryPathComparison
        => OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}

internal sealed class HistoryTimelineLifetime : IDisposable {
    [ThreadStatic]
    private static Dictionary<HistoryTimelineLifetime, int>?
        _enteredOnCurrentThread;

    private readonly IDisposable? _ownedLease;
    private readonly object _gate = new();
    private bool _closing;
    private bool _disposeComplete;
    private int _activeOperations;
    private readonly Action? _afterClosing;

    internal HistoryTimelineLifetime(
        IDisposable? ownedLease = null,
        Action? afterClosing = null
    ) {
        _ownedLease = ownedLease;
        _afterClosing = afterClosing;
    }

    internal bool IsDisposed {
        get {
            lock (_gate) {
                return _closing;
            }
        }
    }

    internal Operation? TryEnterOperation() {
        lock (_gate) {
            bool reentrant = _enteredOnCurrentThread?.ContainsKey(this)
                == true;
            if (_closing && !reentrant) {
                return null;
            }
            _activeOperations = checked(_activeOperations + 1);
            Dictionary<HistoryTimelineLifetime, int> entered =
                _enteredOnCurrentThread ??= [];
            entered[this] = entered.TryGetValue(this, out int count)
                ? checked(count + 1)
                : 1;
            return new Operation(this);
        }
    }

    public void Dispose() {
        bool enteredHere = _enteredOnCurrentThread?.ContainsKey(this)
            == true;
        lock (_gate) {
            if (_closing) {
                if (enteredHere) {
                    return;
                }
                while (!_disposeComplete) {
                    Monitor.Wait(_gate);
                }
                return;
            }
            _closing = true;
            _afterClosing?.Invoke();
            if (enteredHere) {
                return;
            }
            while (_activeOperations != 0) {
                Monitor.Wait(_gate);
            }
            CompleteDisposeUnderLock();
        }
    }

    private void ExitOperation() {
        Dictionary<HistoryTimelineLifetime, int>? entered =
            _enteredOnCurrentThread;
        if (entered is null
            || !entered.TryGetValue(this, out int count)
            || count < 1) {
            throw new InvalidOperationException(
                "HistoryTimeline operation exited on another thread."
            );
        }
        if (count == 1) {
            entered.Remove(this);
        }
        else {
            entered[this] = count - 1;
        }
        lock (_gate) {
            _activeOperations--;
            if (_activeOperations == 0) {
                if (_closing) {
                    CompleteDisposeUnderLock();
                }
                Monitor.PulseAll(_gate);
            }
        }
    }

    private void CompleteDisposeUnderLock() {
        if (_disposeComplete) {
            return;
        }
        _ownedLease?.Dispose();
        _disposeComplete = true;
        Monitor.PulseAll(_gate);
    }

    internal sealed class Operation : IDisposable {
        private HistoryTimelineLifetime? _owner;

        internal Operation(HistoryTimelineLifetime owner) {
            _owner = owner;
        }

        public void Dispose()
            => Interlocked.Exchange(ref _owner, null)?.ExitOperation();
    }
}
