using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Store;

namespace Atelia.SessionJournal.RecapGrid.Manager;

public sealed partial class RecapGridManager {
    private sealed record DerivedRowPlan(
        RowBuildSpec Spec,
        IReadOnlyList<RecapCellArtifact> PreviousCells
    );

    private (DerivedRowPlan?, RecapGridBuildResult?) DeriveRowPlan(
        FrozenOperation frozen,
        FrozenRecipePlan plan,
        HistoryTimelineSelectedRow selected,
        bool isOverlayBootstrap,
        BuiltRow? previousRow,
        BuiltRow? baseRow,
        bool allowNewWorkSelection = false
    ) {
        HistorySegmentDescriptor descriptor = selected.Descriptor;
        if ((descriptor.PreviousRowId is null) != (previousRow is null)) {
            return (null, Invalid(
                "PreviousCandidateViewMismatch",
                "Candidate row provenance does not match Timeline order."
            ));
        }
        RowResultId? previousRowResultId = null;
        IReadOnlyList<RecapCellArtifact> previousCells = [];
        if (previousRow is not null) {
            if (previousRow.View.HistoryRowId
                    != descriptor.PreviousRowId
                || previousRow.View.RefId != descriptor.RefId
                || previousRow.View.TimelineId != descriptor.TimelineId
                || previousRow.View.RecipeDigest != plan.Recipe.Digest) {
                return (null, Invalid(
                    "PreviousCandidateViewMismatch",
                    "The predecessor assignment differs from the exact selected predecessor."
                ));
            }
            previousCells = previousRow.Cells;
            previousRowResultId = previousRow.View.Id;
        }

        (RowWork? work, RecapGridBuildResult? workError) = ReadRowWork(
            plan,
            descriptor
        );
        if (workError is not null) {
            return (null, workError);
        }
        if (work is null && plan.NewWorkProducerTarget is null) {
            return (null, new RecapGridBuildResult.ProducerPolicyRequired(
                plan.Recipe.Digest, descriptor.RowId));
        }
        BuildTarget producerTarget = work?.ProducerTarget
            ?? plan.NewWorkProducerTarget!;
        RowBuildAssignment[] assignments;
        try {
            assignments = DeriveAssignments(
                plan,
                descriptor,
                isOverlayBootstrap,
                baseRow,
                producerTarget,
                work
            );
        }
        catch (OverlaySourceIncompatibleException exception) {
            return (null, exception.Result);
        }
        catch (Exception exception) when (IsContractFailure(exception)) {
            return (null, Invalid("RowWorkAssignmentDerivationInvalid", exception.Message));
        }
        if (work is null && allowNewWorkSelection) {
            RecapGridBuildResult? targetError = ValidateNewWorkProducerTarget(
                plan, producerTarget);
            if (targetError is not null) {
                return (null, targetError);
            }
            (work, workError) = SelectNewRowWork(
                plan, descriptor, previousRowResultId, assignments, producerTarget);
            if (workError is not null) {
                return (null, workError);
            }
            producerTarget = work!.ProducerTarget;
            try {
                assignments = DeriveAssignments(
                    plan, descriptor, isOverlayBootstrap, baseRow,
                    producerTarget, work);
            }
            catch (OverlaySourceIncompatibleException exception) {
                return (null, exception.Result);
            }
            catch (Exception exception) when (IsContractFailure(exception)) {
                return (null, Invalid("RowWorkAssignmentDerivationInvalid", exception.Message));
            }
        }
        try {
            var coordinate = new RowViewCoordinate(
                descriptor.RefId,
                descriptor.TimelineId,
                descriptor.RowId,
                plan.Recipe.Digest,
                producerTarget.Digest,
                descriptor.PreviousRowId,
                previousRowResultId,
                !isOverlayBootstrap
                    || plan.Recipe.BootstrapThroughRowId
                        == descriptor.RowId
            );
            RowBuildSpec spec = plan.Recipe.Kind switch {
                GridBuildRecipeKind.Full when work is null => RowBuildSpec.CreateFullProposed(
                    plan.Recipe,
                    coordinate,
                    assignments,
                    producerTarget
                ),
                GridBuildRecipeKind.Full => RowBuildSpec.CreateFull(
                    plan.Recipe, coordinate, assignments, work
                ),
                GridBuildRecipeKind.Overlay
                    when isOverlayBootstrap && work is null
                    => RowBuildSpec.CreateOverlayBootstrapProposed(
                        plan.Recipe, coordinate, assignments, producerTarget
                    ),
                GridBuildRecipeKind.Overlay
                    when isOverlayBootstrap
                    => RowBuildSpec.CreateOverlayBootstrap(
                        plan.Recipe,
                        coordinate,
                        assignments,
                    work
                ),
                GridBuildRecipeKind.Overlay
                    when work is null
                    => RowBuildSpec.CreateNormalProposed(
                        plan.Recipe, coordinate, assignments, producerTarget
                    ),
                GridBuildRecipeKind.Overlay
                    => RowBuildSpec.CreateNormal(
                        plan.Recipe,
                        coordinate,
                        assignments,
                    work
                    ),
                _ => throw new InvalidOperationException(
                    "The recipe kind is unsupported."
                )
            };
            return (new DerivedRowPlan(
                spec,
                previousCells
            ), null);
        }
        catch (Exception exception) when (IsContractFailure(exception)) {
            return (null, Invalid(
                "RowBuildSpecInvalid",
                exception.Message
            ));
        }
    }

    private RowBuildAssignment[] DeriveAssignments(
        FrozenRecipePlan plan,
        HistorySegmentDescriptor descriptor,
        bool isOverlayBootstrap,
        BuiltRow? baseRow,
        BuildTarget producerTarget,
        RowWork? work
    ) {
        HashSet<LogicalColumnId> recomputed = plan.Recipe
            .RecomputedColumns.ToHashSet();
        bool overlayBootstrap = plan.Recipe.Kind
                == GridBuildRecipeKind.Overlay
            && isOverlayBootstrap;
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
            producerTarget.OrderedColumns.Count
        ];
        for (int index = 0; index < assignments.Length; index++) {
            BuildTargetColumn target =
                producerTarget.OrderedColumns[index];
            bool mustReuse = overlayBootstrap
                && !recomputed.Contains(target.LogicalColumnId);
            RowWorkAssignment? frozen = work is null
                ? null
                : work.OrderedAssignments[index];
            if (!mustReuse) {
                if (frozen is not null && !frozen.IsEvaluate) {
                    throw Incompatible(plan, descriptor,
                        target.LogicalColumnId, "FrozenAssignmentMustEvaluate");
                }
                assignments[index] = new RowBuildAssignment.Evaluate(
                    work is null
                        ? new CellSlot(plan.Recipe.Digest, descriptor.RowId, target.LogicalColumnId)
                        : new CellSlot(plan.Recipe.Digest, descriptor.RowId, work.WorkId, target.LogicalColumnId)
                );
                continue;
            }
            if (frozen is not null && frozen.IsEvaluate) {
                throw Incompatible(plan, descriptor,
                    target.LogicalColumnId, "FrozenAssignmentMustReuse");
            }
            RecapCellArtifact? cell;
            if (frozen?.ReusedCellId is { } frozenCellId) {
                cell = baseRow!.Cells.SingleOrDefault(
                    candidate => candidate.Id == frozenCellId);
                if (cell is null) {
                    throw Incompatible(plan, descriptor,
                        target.LogicalColumnId,
                        "FrozenReuseMissingFromExactBaseView");
                }
            }
            else if (!reusable!.TryGetValue(
                         target.LogicalColumnId,
                         out cell)) {
                throw Incompatible(plan, descriptor,
                    target.LogicalColumnId, "BaseViewMissingLogicalColumn");
            }
            if (cell.LogicalColumnId != target.LogicalColumnId
                || cell.DefinitionDigest != target.DefinitionDigest
                || cell.Slot.HistoryRowId != descriptor.RowId) {
                throw Incompatible(plan, descriptor, target.LogicalColumnId,
                    frozen is null
                        ? "BaseViewCellDoesNotMatchOverlayTarget"
                        : "FrozenReuseDoesNotMatchOverlayTarget");
            }
            assignments[index] = new RowBuildAssignment.Reuse(
                target.LogicalColumnId,
                cell
            );
        }
        return assignments;
    }

    private static OverlaySourceIncompatibleException Incompatible(
        FrozenRecipePlan plan,
        HistorySegmentDescriptor descriptor,
        LogicalColumnId logicalColumnId,
        string reason
    ) => new(new RecapGridBuildResult.OverlaySourceIncompatible(
        plan.Recipe.Digest, descriptor.RowId, logicalColumnId, reason));

    private sealed class OverlaySourceIncompatibleException(
        RecapGridBuildResult.OverlaySourceIncompatible result
    ) : Exception(result.Reason) {
        internal RecapGridBuildResult.OverlaySourceIncompatible Result { get; }
            = result;
    }

    private static RecapGridBuildResult? ValidateNewWorkProducerTarget(
        FrozenRecipePlan plan,
        BuildTarget producerTarget
    ) {
        foreach (BuildTargetColumn column in producerTarget.OrderedColumns) {
            if (!plan.RegisteredDefinitions.TryGetValue(
                    column.DefinitionDigest,
                    out MaintainerDefinitionRevision? definition)
                || definition.LogicalColumnId != column.LogicalColumnId
                || !plan.RegisteredFamilies.ContainsKey(definition.FamilyDigest)) {
                return Invalid(
                    "RecipeDefinitionClosureInvalid",
                    "A new RowWork producer target lacks its exact definition or family."
                );
            }
        }
        return null;
    }

    private (RowWork? Work, RecapGridBuildResult? Error) ReadRowWork(
        FrozenRecipePlan plan,
        HistorySegmentDescriptor descriptor
    ) {
        var key = new RowWorkKey(
            descriptor.RefId,
            descriptor.TimelineId,
            plan.Recipe.Digest,
            descriptor.RowId
        );
        RecapGridStoreReadResult<RowWork> read = _store.Reader.ReadRowWork(key);
        if (read is RecapGridStoreReadResult<RowWork>.Found found) {
            return (found.Value, null);
        }
        if (read is not RecapGridStoreReadResult<RowWork>.Missing) {
            return (null, MapRowWorkRead(read));
        }
        return (null, null);
    }

    private (RowWork? Work, RecapGridBuildResult? Error) SelectNewRowWork(
        FrozenRecipePlan plan,
        HistorySegmentDescriptor descriptor,
        RowResultId? previousRowResultId,
        IReadOnlyList<RowBuildAssignment> assignments,
        BuildTarget producerTarget
    ) {
        var key = new RowWorkKey(
            descriptor.RefId, descriptor.TimelineId, plan.Recipe.Digest,
            descriptor.RowId);
        var selected = new RowWork(
            key,
            producerTarget,
            descriptor.PreviousRowId,
            previousRowResultId,
            assignments.Select(static assignment => assignment switch {
                RowBuildAssignment.Evaluate evaluate => new RowWorkAssignment(evaluate.LogicalColumnId, null),
                RowBuildAssignment.Reuse reuse => new RowWorkAssignment(reuse.LogicalColumnId, reuse.Cell.Id),
                _ => throw new InvalidOperationException("Unsupported RowWork assignment.")
            })
        );
        RecapGridRowWorkPutResult put = _store.Writer.PutRowWork(selected);
        return put switch {
            RecapGridRowWorkPutResult.Inserted inserted => (inserted.Winner, null),
            RecapGridRowWorkPutResult.AlreadyPresent present => (present.Winner, null),
            RecapGridRowWorkPutResult.SelectionConflict conflict => (conflict.Winner, null),
            RecapGridRowWorkPutResult.Busy => (null, Unavailable(RecapGridBuildDependency.Store, "StoreBusy")),
            RecapGridRowWorkPutResult.Disposed => (null, Unavailable(RecapGridBuildDependency.Store, "StoreDisposed")),
            RecapGridRowWorkPutResult.Invalid invalid => (null, Unavailable(RecapGridBuildDependency.Store, invalid.Code, invalid.Detail)),
            _ => (null, Invalid("RowWorkPutOutcomeInvalid", "The Store returned an unknown RowWork outcome."))
        };
    }

    private static RecapGridBuildResult MapRowWorkRead(
        RecapGridStoreReadResult<RowWork> read
    ) => read switch {
        RecapGridStoreReadResult<RowWork>.Busy => Unavailable(RecapGridBuildDependency.Store, "StoreBusy"),
        RecapGridStoreReadResult<RowWork>.Disposed => Unavailable(RecapGridBuildDependency.Store, "StoreDisposed"),
        RecapGridStoreReadResult<RowWork>.Invalid invalid => Unavailable(RecapGridBuildDependency.Store, invalid.Code, invalid.Detail),
        _ => Invalid("RowWorkReadOutcomeInvalid", "The Store returned an unknown RowWork read outcome.")
    };

    private (FrozenRecapCellWork[]?, RecapGridBuildResult?)
        CreateMissingWork(
            FrozenRecipePlan plan,
            RowBuildSpec spec,
            IReadOnlyList<CellSlot> missing
        ) {
        var evaluate = spec.OrderedAssignments
            .OfType<RowBuildAssignment.Evaluate>()
            .ToDictionary(
                static assignment => assignment.Slot
            );
        var seen = new HashSet<CellSlot>();
        var positions = spec.OrderedAssignments
            .Select((assignment, index) => (assignment, index))
            .OfType<(RowBuildAssignment assignment, int index)>()
            .ToDictionary(pair => pair.assignment.LogicalColumnId,
                pair => pair.index);
        var work = new List<FrozenRecapCellWork>(missing.Count);
        int previousPosition = -1;
        foreach (CellSlot key in missing) {
            if (key is null
                || !seen.Add(key)
                || !evaluate.TryGetValue(
                    key,
                    out RowBuildAssignment.Evaluate? assignment)) {
                return (null, Invalid(
                    "MissingCellSlotInvalid",
                    "Store missing keys are not an exact subset of Evaluate assignments."
                ));
            }
            int position = positions[assignment.LogicalColumnId];
            if (position <= previousPosition) {
                return (null, Invalid(
                    "MissingCellSlotOrderInvalid",
                    "Store missing keys are not in target order."
                ));
            }
            previousPosition = position;
            MaintainerDefinitionDigest definitionDigest =
                spec.DefinitionAt(position);
            if (!plan.RegisteredDefinitions.TryGetValue(
                    definitionDigest,
                    out MaintainerDefinitionRevision? definition)
                || definition.LogicalColumnId != assignment.LogicalColumnId
                || !plan.RegisteredFamilies.TryGetValue(
                    definition.FamilyDigest,
                    out FamilyDefinition? family)) {
                return (null, Invalid(
                    "RowWorkDefinitionUnavailable",
                    "The persisted producer definition or family is unavailable."
                ));
            }
            work.Add(new FrozenRecapCellWork(
                position,
                key,
                definition,
                family
            ));
        }
        return (work.ToArray(), null);
    }

    private (RecapCellDraft?, RecapGridBuildResult?) CreateCell(
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
            return (RecapCellDraft.Create(
                item.Slot,
                item.Definition.Digest,
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
        RowBuildSpec spec,
        FrozenRecapCellWork item,
        RecapCellDraft proposed,
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
            ? _store.Writer.PutCell(spec, proposed)
            : _testHooks.PutCell(
                spec,
                proposed,
                () => _store.Writer.PutCell(spec, proposed)
            );
        RecapCellArtifact? winner;
        switch (result) {
            case RecapGridCellPutResult.Inserted inserted:
                state.CellsCommitted++;
                winner = inserted.Winner;
                break;
            case RecapGridCellPutResult.AlreadyFilled already:
                winner = already.Winner;
                break;
            case RecapGridCellPutResult.CommitIndeterminate indeterminate:
                if (indeterminate.IntendedSlot
                    != item.Slot) {
                    return (null, Invalid(
                        "CellSettlementIntendedMismatch",
                        "The indeterminate Cell identity differs from the proposed CellSlot."
                    ));
                }
                winner = indeterminate.Observed;
                if (winner is null) {
                    RecapGridStoreReadResult<RecapCellArtifact> observed =
                        _store.Reader.TryReadCell(item.Slot);
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
                            indeterminate.IntendedSlot.ToString(),
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
            item.Slot,
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
            IReadOnlyDictionary<CellSlot,
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
                            evaluate.Slot,
                            out RecapCellArtifact? cell)) {
                        RecapGridStoreReadResult<RecapCellArtifact> read =
                            _store.Reader.TryReadCell(
                                evaluate.Slot
                            );
                        if (read is not RecapGridStoreReadResult<
                                RecapCellArtifact>.Found found) {
                            return (null, MapStoreCellRead(read));
                        }
                        cell = found.Value;
                    }
                    MaintainerDefinitionDigest definitionDigest =
                        spec.DefinitionAt(index);
                    if (!plan.RegisteredDefinitions.TryGetValue(
                            definitionDigest,
                            out MaintainerDefinitionRevision? definition)
                        || !plan.RegisteredFamilies.ContainsKey(
                            definition.FamilyDigest)) {
                        return (null, Invalid(
                            "RowWorkDefinitionUnavailable",
                            "The persisted producer definition is unavailable."
                        ));
                    }
                    if (definition.LogicalColumnId
                        != assignment.LogicalColumnId) {
                        return (null, Invalid(
                            "RowWorkDefinitionInvalid",
                            "The persisted producer definition does not match the assignment logical column."
                        ));
                    }
                    RecapGridBuildResult? invalid = ValidateCell(
                        cell,
                        assignment.LogicalColumnId,
                        definition.Digest,
                        definition.MaxContentUtf8Bytes,
                        evaluate.Slot,
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
