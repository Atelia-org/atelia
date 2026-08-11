using System.Text;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Store;

namespace Atelia.SessionJournal.RecapGrid.Manager;

public sealed partial class RecapGridManager {
    private (HistorySegmentContent?, RecapGridBuildResult?)
        OpenHistorySegment(
            FrozenOperation frozen,
            HistoryTimelineSelectedRow selected,
            BuildState state,
            CancellationToken cancellationToken
        ) {
        HistorySegmentOpenResult result;
        try {
            result = _testHooks.OpenSelectedSegment is null
                ? _timeline.OpenSelectedSegment(
                    frozen.TimelineHead,
                    frozen.RawCapture,
                    selected,
                    cancellationToken
                )
                : _testHooks.OpenSelectedSegment(
                    selected,
                    () => _timeline.OpenSelectedSegment(
                        frozen.TimelineHead,
                        frozen.RawCapture,
                        selected,
                        cancellationToken
                    )
                );
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested) {
            return (null, new RecapGridBuildResult.Cancelled());
        }
        return result switch {
            HistorySegmentOpenResult.Opened opened
                when opened.Content.Descriptor == selected.Descriptor
                => (opened.Content, null),
            HistorySegmentOpenResult.Opened
                => (null, Invalid(
                    "HistorySegmentDescriptorMismatch",
                    "Timeline opened content for a different selected descriptor."
                )),
            HistorySegmentOpenResult.NotOnSelectedPath missing
                => (null,
                    new RecapGridBuildResult.ThroughRowNotSelected(
                        missing.RowId
                    )),
            HistorySegmentOpenResult.StaleTimelineHead stale
                => (null,
                    new RecapGridBuildResult.StaleTimelineHead(
                        stale.Actual
                    )),
            HistorySegmentOpenResult.BackendBusy
                => (null, Unavailable(
                    RecapGridBuildDependency.Timeline,
                    "TimelineBusy")),
            HistorySegmentOpenResult.OfflineBootstrapRequired
                => (null, Unavailable(
                    RecapGridBuildDependency.RawHistory,
                    "OfflineBootstrapRequired")),
            HistorySegmentOpenResult.OffLineage offLineage
                => (null, Unavailable(
                    RecapGridBuildDependency.RawHistory,
                    "OffLineage",
                    $"{offLineage.RequiredAnchor}->{offLineage.CapturedHead}")),
            HistorySegmentOpenResult.RawHeadChanged changed
                => (null, Unavailable(
                    RecapGridBuildDependency.RawHistory,
                    "RawHeadChanged",
                    $"{changed.Expected}->{changed.Observed}")),
            HistorySegmentOpenResult.PartitionPolicyUnavailable missing
                => (null, Unavailable(
                    RecapGridBuildDependency.Timeline,
                    "PartitionPolicyUnavailable", missing.PolicyDigest)),
            HistorySegmentOpenResult.HistoryLoadEstimatorUnavailable missing
                => (null, Unavailable(
                    RecapGridBuildDependency.Timeline,
                    "HistoryLoadEstimatorUnavailable", missing.EstimatorId)),
            HistorySegmentOpenResult.PartitionAlgorithmUnavailable missing
                => (null, Unavailable(
                    RecapGridBuildDependency.Timeline,
                    "PartitionAlgorithmUnavailable", missing.AlgorithmId)),
            HistorySegmentOpenResult.Invalid invalid
                => (null, Unavailable(
                    RecapGridBuildDependency.RawHistory,
                    invalid.Code, invalid.Detail)),
            _ => (null, Invalid("SegmentOpenOutcomeInvalid",
                "Timeline returned an unknown segment-open outcome."))
        };
    }

    private RecapGridBuildResult? ValidateBuiltRow(
        BuiltRow built,
        FrozenRecipePlan plan,
        HistorySegmentDescriptor descriptor
    ) {
        if (built.View.TimelineId != descriptor.TimelineId
            || built.View.HistoryRowId != descriptor.RowId
            || built.View.RowDescriptorDigest
                != descriptor.DescriptorDigest
            || built.View.RecipeDigest != plan.Recipe.Digest
            || built.View.TargetDigest != plan.Recipe.Target.Digest
            || built.Cells.Count != built.View.OrderedCells.Count) {
            return Invalid(
                "PreviousViewScopeMismatch",
                "The candidate predecessor view does not match the exact Timeline row and recipe."
            );
        }
        for (int index = 0; index < built.Cells.Count; index++) {
            RecapCellArtifact cell = built.Cells[index];
            RecapRowViewCell manifest = built.View.OrderedCells[index];
            if (cell.LogicalColumnId != manifest.LogicalColumnId
                || cell.DefinitionDigest != manifest.DefinitionDigest
                || cell.CellDigest != manifest.CellDigest) {
                return Invalid(
                    "PreviousViewMemberMismatch",
                    "A predecessor RowView member differs from its Cell."
                );
            }
        }
        return null;
    }

    private RecapGridBuildResult? ValidateCell(
        RecapCellArtifact cell,
        LogicalColumnId logicalColumnId,
        MaintainerDefinitionDigest definitionDigest,
        int maxContentUtf8Bytes,
        EvaluationKey key,
        IReadOnlyList<RecapCellArtifact> previousCells
    ) {
        if (cell.LogicalColumnId != logicalColumnId
            || cell.DefinitionDigest != definitionDigest
            || cell.EvaluationKey.Digest != key.Digest
            || !cell.EvaluationKey.ToCanonicalBytes().SequenceEqual(
                key.ToCanonicalBytes())
            || Encoding.UTF8.GetByteCount(cell.Content)
                > maxContentUtf8Bytes) {
            return Invalid(
                "CellWinnerMismatch",
                "The Cell winner differs from its exact work identity."
            );
        }
        if (cell.Outcome == RecapCellOutcome.KeepUnchanged) {
            RecapCellArtifact? prior = previousCells.SingleOrDefault(
                value => value.LogicalColumnId == logicalColumnId
            );
            if (prior is null
                || !string.Equals(
                    prior.Content,
                    cell.Content,
                    StringComparison.Ordinal)) {
                return Invalid(
                    "KeepUnchangedContentMismatch",
                    "KeepUnchanged does not copy exact prior content."
                );
            }
        }
        return null;
    }

    private static RecapGridBuildResult MapStoreCellRead(
        RecapGridStoreReadResult<RecapCellArtifact> result
    ) => result switch {
        RecapGridStoreReadResult<RecapCellArtifact>.Missing
            => Invalid("CellWinnerUnavailable",
                "A complete assignment lacks its Cell winner."),
        RecapGridStoreReadResult<RecapCellArtifact>.Busy
            => Unavailable(RecapGridBuildDependency.Store, "StoreBusy"),
        RecapGridStoreReadResult<RecapCellArtifact>.Disposed
            => Unavailable(RecapGridBuildDependency.Store,
                "StoreDisposed"),
        RecapGridStoreReadResult<RecapCellArtifact>.Invalid invalid
            => Unavailable(RecapGridBuildDependency.Store,
                invalid.Code, invalid.Detail),
        _ => Invalid("CellReadOutcomeInvalid",
            "The Store returned an unknown Cell read outcome.")
    };

    private static RecapGridBuildResult? CountView(BuildState state) {
        state.RowViewsCommitted++;
        return null;
    }

    private static RecapGridBuildResult.SettlementRequired Settlement(
        RecapGridBuildCommitKind kind,
        string intended,
        string? observed,
        BuildState state
    ) {
        _ = state;
        return new(kind, intended, observed);
    }

    private static RowAttempt RowError(RecapGridBuildResult error)
        => new(null, error);

    private static bool IsContractFailure(Exception exception)
        => exception is ArgumentException
            or InvalidOperationException
            or InvalidDataException
            or EncoderFallbackException
            or OverflowException;

    private static bool IsFatal(Exception exception)
        => exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException;

}
