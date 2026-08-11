using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Store;

namespace Atelia.SessionJournal.RecapGrid.Manager;

public sealed partial class RecapGridManager {
    private RowBuildAssignment[] DeriveAssignments(
        FrozenRecipePlan plan,
        HistorySegmentDescriptor descriptor,
        int rowIndex,
        PriorInputReference prior,
        BuiltRow? baseRow
    ) {
        HashSet<LogicalColumnId> recomputed = plan.Recipe
            .RecomputedColumns.ToHashSet();
        bool overlayBootstrap = plan.Recipe.Kind
                == GridBuildRecipeKind.Overlay
            && rowIndex <= plan.BootstrapIndex;
        Dictionary<LogicalColumnId, RecapCellArtifact>? reusable = null;
        if (overlayBootstrap) {
            if (baseRow is null) {
                throw new InvalidOperationException(
                    "Overlay bootstrap requires its exact same-row base view."
                );
            }
            reusable = baseRow.Cells.ToDictionary(
                static cell => cell.LogicalColumnId
            );
        }
        var assignments = new RowBuildAssignment[
            plan.Recipe.Target.OrderedColumns.Count
        ];
        for (int index = 0; index < assignments.Length; index++) {
            BuildTargetColumn target =
                plan.Recipe.Target.OrderedColumns[index];
            if (!overlayBootstrap
                || recomputed.Contains(target.LogicalColumnId)) {
                assignments[index] = new RowBuildAssignment.Evaluate(
                    target.LogicalColumnId,
                    EvaluationKey.Create(
                        descriptor.DescriptorDigest,
                        target.DefinitionDigest,
                        prior
                    )
                );
                continue;
            }
            if (!reusable!.TryGetValue(
                    target.LogicalColumnId,
                    out RecapCellArtifact? cell)
                || cell.DefinitionDigest != target.DefinitionDigest
                || cell.EvaluationKey.HistorySegmentDigest
                    != descriptor.DescriptorDigest) {
                throw new InvalidOperationException(
                    "The overlay base view lacks an exact reusable cell."
                );
            }
            assignments[index] = new RowBuildAssignment.Reuse(
                target.LogicalColumnId,
                cell
            );
        }
        return assignments;
    }

    private (FrozenRecapCellWork[]?, RecapGridBuildResult?)
        CreateMissingWork(
            FrozenRecipePlan plan,
            RowBuildSpec spec,
            IReadOnlyList<EvaluationKey> missing
        ) {
        var evaluate = spec.OrderedAssignments
            .OfType<RowBuildAssignment.Evaluate>()
            .ToDictionary(
                static assignment => assignment.EvaluationKey.Digest
            );
        var seen = new HashSet<EvaluationKeyDigest>();
        var positions = spec.OrderedAssignments
            .Select((assignment, index) => (assignment, index))
            .OfType<(RowBuildAssignment assignment, int index)>()
            .ToDictionary(pair => pair.assignment.LogicalColumnId,
                pair => pair.index);
        var work = new List<FrozenRecapCellWork>(missing.Count);
        int previousPosition = -1;
        foreach (EvaluationKey key in missing) {
            if (key is null
                || !seen.Add(key.Digest)
                || !evaluate.TryGetValue(
                    key.Digest,
                    out RowBuildAssignment.Evaluate? assignment)
                || !key.ToCanonicalBytes().SequenceEqual(
                    assignment.EvaluationKey.ToCanonicalBytes())) {
                return (null, Invalid(
                    "MissingEvaluationKeyInvalid",
                    "Store missing keys are not an exact subset of Evaluate assignments."
                ));
            }
            int position = positions[assignment.LogicalColumnId];
            if (position <= previousPosition) {
                return (null, Invalid(
                    "MissingEvaluationKeyOrderInvalid",
                    "Store missing keys are not in target order."
                ));
            }
            previousPosition = position;
            work.Add(new FrozenRecapCellWork(
                position,
                assignment.LogicalColumnId,
                key,
                plan.Definitions[assignment.LogicalColumnId],
                plan.Families[assignment.LogicalColumnId]
            ));
        }
        return (work.ToArray(), null);
    }

    private (RecapCellArtifact?, RecapGridBuildResult?) CreateCell(
        FrozenRecapCellWork item,
        RecapCellExecutionOutcome outcome,
        IReadOnlyList<RecapCellArtifact> previousCells
    ) {
        string content;
        RecapCellOutcome cellOutcome;
        switch (outcome) {
            case RecapCellExecutionOutcome.Updated updated:
                content = updated.Content;
                cellOutcome = RecapCellOutcome.Updated;
                break;
            case RecapCellExecutionOutcome.KeepUnchanged:
                RecapCellArtifact? prior = previousCells.SingleOrDefault(
                    cell => cell.LogicalColumnId == item.LogicalColumnId
                );
                if (prior is null) {
                    return (null, Invalid(
                        "KeepUnchangedPriorUnavailable",
                        "KeepUnchanged requires an exact same-column prior cell."
                    ));
                }
                content = prior.Content;
                cellOutcome = RecapCellOutcome.KeepUnchanged;
                break;
            default:
                return (null, new RecapGridBuildResult.ExecutorFailed(
                    "ExecutorOutcomeInvalid",
                    "A successful executor outcome subtype is unsupported."
                ));
        }
        try {
            return (RecapCellArtifact.Create(
                item.LogicalColumnId,
                item.Definition.Digest,
                item.EvaluationKey,
                cellOutcome,
                content,
                item.Definition.MaxContentUtf8Bytes
            ), null);
        }
        catch (Exception exception) when (IsContractFailure(exception)) {
            return (null, new RecapGridBuildResult.ExecutorFailed(
                "ExecutorContentInvalid",
                exception.Message
            ));
        }
    }

    private (RecapCellArtifact?, RecapGridBuildResult?) PutCell(
        FrozenOperation frozen,
        FrozenRecapCellWork item,
        RecapCellArtifact proposed,
        IReadOnlyList<RecapCellArtifact> previousCells,
        BuildState state
    ) {
        if (_store.Identity != frozen.StoreIdentity) {
            return (null, Invalid(
                "StoreIdentityChanged",
                "The Store identity changed during the build operation."
            ));
        }
        RecapGridCellPutResult result = _testHooks.PutCell is null
            ? _store.Writer.PutCell(proposed)
            : _testHooks.PutCell(
                proposed,
                () => _store.Writer.PutCell(proposed)
            );
        RecapCellArtifact? winner;
        switch (result) {
            case RecapGridCellPutResult.Inserted:
                state.CellsCommitted++;
                winner = proposed;
                break;
            case RecapGridCellPutResult.AlreadyFilled already:
                winner = already.Winner;
                break;
            case RecapGridCellPutResult.CommitIndeterminate indeterminate:
                if (indeterminate.IntendedKey
                    != item.EvaluationKey.Digest) {
                    return (null, Invalid(
                        "CellSettlementIntendedMismatch",
                        "The indeterminate Cell identity differs from the proposed EvaluationKey."
                    ));
                }
                winner = indeterminate.Observed;
                if (winner is null) {
                    RecapGridStoreReadResult<RecapCellArtifact> observed =
                        _store.Reader.TryReadCell(item.EvaluationKey);
                    if (observed is RecapGridStoreReadResult<
                            RecapCellArtifact>.Found found) {
                        winner = found.Value;
                    }
                    else if (observed is RecapGridStoreReadResult<
                                 RecapCellArtifact>.Missing
                             or RecapGridStoreReadResult<
                                 RecapCellArtifact>.Busy) {
                        return (null, Settlement(
                            RecapGridBuildCommitKind.Cell,
                            indeterminate.IntendedKey.Value,
                            null,
                            state
                        ));
                    }
                    else {
                        return (null, MapStoreCellRead(observed));
                    }
                }
                state.CellsCommitted++;
                break;
            case RecapGridCellPutResult.Busy:
                return (null, Unavailable(
                    RecapGridBuildDependency.Store,
                    "StoreBusy"
                ));
            case RecapGridCellPutResult.Limit limit:
                return (null, Unavailable(
                    RecapGridBuildDependency.Store,
                    "StoreLimit",
                    limit.Name
                ));
            case RecapGridCellPutResult.Disposed:
                return (null, Unavailable(
                    RecapGridBuildDependency.Store,
                    "StoreDisposed"
                ));
            case RecapGridCellPutResult.Rejected rejected:
                return (null, Invalid(
                    "CellRejected",
                    rejected.Code
                ));
            case RecapGridCellPutResult.Invalid invalid:
                return (null, Unavailable(
                    RecapGridBuildDependency.Store,
                    invalid.Code,
                    invalid.Detail
                ));
            default:
                return (null, Invalid(
                    "CellPutOutcomeInvalid",
                    "The Store returned an unknown Cell put outcome."
                ));
        }
        RecapGridBuildResult? validation = ValidateCell(
            winner,
            item.LogicalColumnId,
            item.Definition.Digest,
            item.Definition.MaxContentUtf8Bytes,
            item.EvaluationKey,
            previousCells
        );
        return validation is null
            ? (winner, null)
            : (null, validation);
    }

    private (RecapCellArtifact[]?, RecapGridBuildResult?)
        ResolveSelectedCells(
            FrozenRecipePlan plan,
            RowBuildSpec spec,
            IReadOnlyDictionary<EvaluationKeyDigest,
                RecapCellArtifact> settled,
            IReadOnlyList<RecapCellArtifact> previousCells
        ) {
        var cells = new RecapCellArtifact[spec.OrderedAssignments.Count];
        for (int index = 0; index < cells.Length; index++) {
            RowBuildAssignment assignment = spec.OrderedAssignments[index];
            switch (assignment) {
                case RowBuildAssignment.Reuse reuse:
                    cells[index] = reuse.Cell;
                    break;
                case RowBuildAssignment.Evaluate evaluate:
                    if (!settled.TryGetValue(
                            evaluate.EvaluationKey.Digest,
                            out RecapCellArtifact? cell)) {
                        RecapGridStoreReadResult<RecapCellArtifact> read =
                            _store.Reader.TryReadCell(
                                evaluate.EvaluationKey
                            );
                        if (read is not RecapGridStoreReadResult<
                                RecapCellArtifact>.Found found) {
                            return (null, MapStoreCellRead(read));
                        }
                        cell = found.Value;
                    }
                    RecapGridBuildResult? invalid = ValidateCell(
                        cell,
                        assignment.LogicalColumnId,
                        plan.Definitions[
                            assignment.LogicalColumnId
                        ].Digest,
                        plan.Definitions[
                            assignment.LogicalColumnId
                        ].MaxContentUtf8Bytes,
                        evaluate.EvaluationKey,
                        previousCells
                    );
                    if (invalid is not null) {
                        return (null, invalid);
                    }
                    cells[index] = cell;
                    break;
                default:
                    return (null, Invalid(
                        "RowAssignmentInvalid",
                        "The RowBuildSpec contains an unsupported assignment."
                    ));
            }
        }
        return (cells, null);
    }

}
