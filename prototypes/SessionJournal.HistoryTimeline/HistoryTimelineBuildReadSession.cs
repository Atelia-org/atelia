using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.HistoryTimeline;

/// <summary>
/// Mutation-free, repository-bound HistoryTimeline capability used by Grid
/// builders. The owning SessionJournal view and estimator registry are fixed
/// when the session is opened; callers cannot substitute raw authority later.
/// </summary>
public sealed class HistoryTimelineBuildReadSession : IDisposable {
    private readonly HistoryTimelineHandle _ownedHandle;
    private readonly SJ.SessionJournalReadView _selectedRef;

    internal HistoryTimelineBuildReadSession(
        HistoryTimelineHandle ownedHandle,
        SJ.SessionJournalReadView selectedRef
    ) {
        _ownedHandle = ownedHandle;
        _selectedRef = selectedRef;
    }

    public ActiveTimelineLocator Locator => _ownedHandle.Locator;
    public HistoryTimelineReader Reader => _ownedHandle.Reader;

    public OnlineSelectedRawCaptureResult CaptureRaw(
        TimelineHeadRef expectedWholeHead,
        CancellationToken cancellationToken = default
    ) => _ownedHandle.Coordinator.CaptureOnline(
        expectedWholeHead,
        _selectedRef,
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
