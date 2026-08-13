using SJ = Atelia.SessionJournal;
using Atelia.EventJournal;

namespace Atelia.SessionJournal.HistoryTimeline;

/// <summary>
/// Mutation-free, repository-bound HistoryTimeline capability used by Grid
/// builders. The owning SessionJournal view and estimator registry are fixed
/// when the session is opened; callers cannot substitute raw authority later.
/// </summary>
public sealed class HistoryTimelineBuildReadSession : IDisposable {
    private readonly HistoryTimelineHandle _ownedHandle;
    private readonly SJ.SessionJournalReadView _selectedRef;
    private readonly HistoryRecentReserveAnchorReadLimits
        _recentReserveLimits;
    private readonly int _rawCaptureLimit;

    internal HistoryTimelineBuildReadSession(
        HistoryTimelineHandle ownedHandle,
        SJ.SessionJournalReadView selectedRef,
        HistoryRecentReserveAnchorReadLimits? recentReserveLimits = null,
        int? rawCaptureLimit = null
    ) {
        _ownedHandle = ownedHandle;
        _selectedRef = selectedRef;
        _recentReserveLimits = recentReserveLimits
            ?? HistoryRecentReserveAnchorReadLimits.Production;
        _recentReserveLimits.Validate();
        _rawCaptureLimit = rawCaptureLimit
            ?? HistoryRecentReserveOperationLimits.MaximumRawEvents;
        if (_rawCaptureLimit is < 1
            or > HistoryRecentReserveOperationLimits.MaximumRawEvents) {
            throw new ArgumentOutOfRangeException(nameof(rawCaptureLimit));
        }
    }

    public ActiveTimelineLocator Locator => _ownedHandle.Locator;
    public HistoryTimelineReader Reader => _ownedHandle.Reader;

    public HistoryTimelineRawHeadObservationResult ObserveRawHead() {
        HistoryTimelineSnapshotResult snapshot = Reader.ReadSnapshot();
        switch (snapshot) {
            case HistoryTimelineSnapshotResult.Busy:
                return new HistoryTimelineRawHeadObservationResult.Busy();
            case HistoryTimelineSnapshotResult.UnsupportedSchema unsupported:
                return new HistoryTimelineRawHeadObservationResult
                    .UnsupportedSchema(unsupported.SchemaVersion);
            case HistoryTimelineSnapshotResult.Invalid invalid
                when string.Equals(
                    invalid.Code,
                    "HistoryTimelineDisposed",
                    StringComparison.Ordinal):
                return new HistoryTimelineRawHeadObservationResult.Disposed();
            case HistoryTimelineSnapshotResult.Invalid invalid:
                return new HistoryTimelineRawHeadObservationResult.Invalid(
                    invalid.Code,
                    invalid.Detail
                );
            case not HistoryTimelineSnapshotResult.Available:
                return new HistoryTimelineRawHeadObservationResult.Invalid(
                    "TimelineSnapshotOutcomeInvalid",
                    "Timeline returned an unknown snapshot outcome."
                );
        }
        try {
            return new HistoryTimelineRawHeadObservationResult.Available(
                _selectedRef.ReadCurrentHead()
            );
        }
        catch (ObjectDisposedException) {
            return new HistoryTimelineRawHeadObservationResult.Disposed();
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException
            or InvalidOperationException
            or UnauthorizedAccessException) {
            return new HistoryTimelineRawHeadObservationResult.Invalid(
                "RawHeadObservationFailed",
                exception.Message
            );
        }
    }

    public HistoryRecentReserveAnchorResult FindRecentReserveAnchor(
        TimelineHeadRef expectedWholeHead,
        Atelia.EventJournal.EventAddress completionBoundary,
        HistoryRecentReserveRequirement requirement,
        CancellationToken cancellationToken = default
    ) => HistoryRecentReserveAnchorFinder.Find(
        _ownedHandle.Coordinator,
        Reader,
        _selectedRef,
        expectedWholeHead,
        completionBoundary,
        requirement,
        _recentReserveLimits,
        cancellationToken);

    public OnlineSelectedRawCaptureResult CaptureRaw(
        TimelineHeadRef expectedWholeHead,
        CancellationToken cancellationToken = default
    ) => _ownedHandle.Coordinator.CaptureBuildRead(
        expectedWholeHead,
        _selectedRef,
        _rawCaptureLimit,
        cancellationToken
    );

    public HistorySegmentOpenResult OpenSelectedSegment(
        TimelineHeadRef expectedWholeHead,
        OnlineSelectedRawCapture capture,
        HistoryTimelineSelectedRow selectedRow,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(selectedRow);
        if (selectedRow.Descriptor.RowId != selectedRow.Witness.RowId
            || selectedRow.Descriptor.DescriptorDigest
                != selectedRow.Witness.DescriptorDigest) {
            return InvalidSelectedRow();
        }
        return OpenSelectedSegment(
            expectedWholeHead,
            capture,
            selectedRow.Witness,
            cancellationToken
        );
    }

    public HistorySegmentOpenResult OpenSelectedSegment(
        TimelineHeadRef expectedWholeHead,
        OnlineSelectedRawCapture capture,
        HistoryTimelineAncestorWitness witness,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(expectedWholeHead);
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(witness);
        HistoryTimelineReaderRowResult validated = Reader.ValidateWitness(
            expectedWholeHead,
            witness
        );
        return validated switch {
            HistoryTimelineReaderRowResult.Selected selected
                when selected.Row.Descriptor.RowId == witness.RowId
                    && selected.Row.Descriptor.DescriptorDigest
                        == witness.DescriptorDigest
                => _ownedHandle.Coordinator.OpenSegment(
                    expectedWholeHead,
                    capture,
                    witness.RowId,
                    cancellationToken
                ),
            HistoryTimelineReaderRowResult.NotOnSelectedPath missing
                => new HistorySegmentOpenResult.NotOnSelectedPath(
                    missing.RowId
                ),
            HistoryTimelineReaderRowResult.StaleTimelineHead stale
                => new HistorySegmentOpenResult.StaleTimelineHead(
                    stale.Actual
                ),
            HistoryTimelineReaderRowResult.Busy
                => new HistorySegmentOpenResult.BackendBusy(),
            HistoryTimelineReaderRowResult.Invalid invalid
                => new HistorySegmentOpenResult.Invalid(
                    invalid.Code,
                    invalid.Detail
                ),
            _ => InvalidSelectedRow()
        };
    }

    public void Dispose() => _ownedHandle.Dispose();

    private static HistorySegmentOpenResult.Invalid InvalidSelectedRow()
        => new(
            "SelectedRowWitnessMismatch",
            "The selected row and witness do not bind one exact descriptor."
        );
}

public abstract record HistoryTimelineRawHeadObservationResult {
    private HistoryTimelineRawHeadObservationResult() { }

    public sealed record Available(EventAddress? Head)
        : HistoryTimelineRawHeadObservationResult;

    public sealed record Busy : HistoryTimelineRawHeadObservationResult;

    public sealed record UnsupportedSchema(int SchemaVersion)
        : HistoryTimelineRawHeadObservationResult;

    public sealed record Disposed : HistoryTimelineRawHeadObservationResult;

    public sealed record Invalid(string Code, string Detail)
        : HistoryTimelineRawHeadObservationResult;
}

public abstract record HistoryTimelineBuildReadSessionOpenResult {
    private HistoryTimelineBuildReadSessionOpenResult() { }

    public sealed record Opened(HistoryTimelineBuildReadSession Session)
        : HistoryTimelineBuildReadSessionOpenResult;

    public sealed record Absent
        : HistoryTimelineBuildReadSessionOpenResult;

    public sealed record Busy
        : HistoryTimelineBuildReadSessionOpenResult;

    public sealed record UnsupportedSchema(int SchemaVersion)
        : HistoryTimelineBuildReadSessionOpenResult;

    public sealed record Invalid(string Code, string Detail)
        : HistoryTimelineBuildReadSessionOpenResult;
}
