using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Store;

namespace Atelia.SessionJournal.RecapGrid.Manager;

public sealed partial class RecapGridManager {
    private sealed record FrozenRecipeRowUnit(
        FrozenRecipePlan Plan,
        HistoryTimelineSelectedRow Selected,
        bool IsOverlayBootstrap
    );

    private sealed record FrozenProgression(
        IReadOnlyList<FrozenRecipeRowUnit> OrderedUnits,
        IReadOnlyDictionary<GridBuildRecipeDigest, BuiltRow> Anchors,
        BuiltRow? RequestedExact
    );

    private sealed record ProgressionAttempt(
        FrozenProgression? Value,
        RecapGridBuildResult? Error
    );

    private ProgressionAttempt DiscoverProgression(
        FrozenOperation frozen,
        BuildState state,
        CancellationToken cancellationToken
    ) {
        var headToOldest = new List<HistoryTimelineSelectedRow> {
            frozen.Through
        };
        var rowIndexes = new Dictionary<HistoryRowId, int> {
            [frozen.Through.Descriptor.RowId] = 0
        };
        var anchors = new Dictionary<GridBuildRecipeDigest, BuiltRow>();
        var workByRecipe = new Dictionary<GridBuildRecipeDigest,
            List<(int Depth, bool IsOverlayBootstrap)>>();

        RecapGridBuildResult? ExtendOne() {
            HistoryRowId? previous = headToOldest[^1]
                .Descriptor.PreviousRowId;
            if (previous is null) {
                return Invalid(
                    "ProgressionRowUnavailable",
                    "The selected path ended before a required recipe anchor."
                );
            }
            if (cancellationToken.IsCancellationRequested) {
                return new RecapGridBuildResult.Cancelled();
            }
            (HistoryTimelineSelectedRow? row,
                RecapGridBuildResult? error) = ReadSelectedRow(
                    frozen.TimelineHead,
                    previous.Value
                );
            if (error is not null) {
                return error;
            }
            if (row!.Descriptor.RowId != previous
                || row.Descriptor.RefId != frozen.TimelineHead.RefId
                || row.Descriptor.TimelineId
                    != frozen.TimelineHead.TimelineId
                || !rowIndexes.TryAdd(
                    row.Descriptor.RowId,
                    headToOldest.Count)) {
                return Invalid(
                    "ProgressionSelectedChainInvalid",
                    "The selected predecessor chain is not exact."
                );
            }
            headToOldest.Add(row);
            state.SelectedRows++;
            return null;
        }

        RecapGridBuildResult? EnsureIndex(int index) {
            while (headToOldest.Count <= index) {
                RecapGridBuildResult? error = ExtendOne();
                if (error is not null) {
                    return error;
                }
            }
            return null;
        }

        RecapGridBuildResult? DiscoverRecipe(
            int planIndex,
            int requiredIndex
        ) {
            FrozenRecipePlan plan = frozen.BaseToCandidate[planIndex];
            if (workByRecipe.ContainsKey(plan.Recipe.Digest)
                || anchors.ContainsKey(plan.Recipe.Digest)) {
                return null;
            }
            var newestToOldest = new List<int>();
            BuiltRow? anchor = null;
            int index = requiredIndex;
            while (true) {
                RecapGridBuildResult? ensure = EnsureIndex(index);
                if (ensure is not null) {
                    return ensure;
                }
                HistoryTimelineSelectedRow selected = headToOldest[index];
                RecapGridStoreReadResult<RecapRowView> read =
                    _store.Reader.ReadViewAt(new RowViewAssignmentKey(
                        frozen.TimelineHead.RefId,
                        frozen.TimelineHead.TimelineId,
                        plan.Recipe.Digest,
                        selected.Descriptor.RowId
                    ));
                if (read is RecapGridStoreReadResult<RecapRowView>.Found
                    found) {
                    (BuiltRow? hydrated,
                        RecapGridBuildResult? hydrationError) =
                        HydrateAssignedView(plan, selected, found.Value);
                    if (hydrationError is not null) {
                        return hydrationError;
                    }
                    anchor = hydrated;
                    anchors.Add(plan.Recipe.Digest, hydrated!);
                    break;
                }
                if (read is RecapGridStoreReadResult<RecapRowView>.Busy) {
                    return Unavailable(
                        RecapGridBuildDependency.Store,
                        "StoreBusy"
                    );
                }
                if (read is RecapGridStoreReadResult<RecapRowView>.Disposed) {
                    return Unavailable(
                        RecapGridBuildDependency.Store,
                        "StoreDisposed"
                    );
                }
                if (read is RecapGridStoreReadResult<RecapRowView>.Invalid
                    invalid) {
                    return Unavailable(
                        RecapGridBuildDependency.Store,
                        invalid.Code,
                        invalid.Detail
                    );
                }
                if (read is not RecapGridStoreReadResult<RecapRowView>
                    .Missing) {
                    return Invalid(
                        "ProgressionViewReadOutcomeInvalid",
                        "The Store returned an unknown assignment read outcome."
                    );
                }
                newestToOldest.Add(index);
                if (selected.Descriptor.PreviousRowId is null) {
                    break;
                }
                index++;
            }

            bool bootstrapCompleted = plan.Recipe.Kind
                    == GridBuildRecipeKind.Full
                || plan.Recipe.BootstrapThroughRowId is null
                || anchor?.View.BootstrapCompleted == true;
            int? newestBaseRequirement = null;
            var work = new List<(int Depth, bool IsOverlayBootstrap)>(
                newestToOldest.Count
            );
            for (int reverse = newestToOldest.Count - 1;
                 reverse >= 0;
                 reverse--) {
                int depth = newestToOldest[reverse];
                HistoryRowId rowId = headToOldest[depth]
                    .Descriptor.RowId;
                bool overlayBootstrap = plan.Recipe.Kind
                        == GridBuildRecipeKind.Overlay
                    && !bootstrapCompleted;
                work.Add((depth, overlayBootstrap));
                if (overlayBootstrap) {
                    newestBaseRequirement = newestBaseRequirement is null
                        ? depth
                        : Math.Min(newestBaseRequirement.Value, depth);
                }
                if (plan.Recipe.BootstrapThroughRowId == rowId) {
                    bootstrapCompleted = true;
                }
            }
            workByRecipe.Add(plan.Recipe.Digest, work);
            if (newestBaseRequirement is { } baseIndex) {
                if (planIndex == 0
                    || plan.Recipe.BaseRecipeDigest
                        != frozen.BaseToCandidate[planIndex - 1]
                            .Recipe.Digest) {
                    return Invalid(
                        "ProgressionBaseClosureInvalid",
                        "An overlay bootstrap lacks its exact base recipe plan."
                    );
                }
                return DiscoverRecipe(planIndex - 1, baseIndex);
            }
            return null;
        }

        state.SelectedRows = 1;
        RecapGridBuildResult? discovery = DiscoverRecipe(
            frozen.BaseToCandidate.Count - 1,
            0
        );
        if (discovery is not null) {
            return new ProgressionAttempt(null, discovery);
        }

        FrozenRecipePlan requested = frozen.RequestedRecipe;
        BuiltRow? requestedExact = anchors.TryGetValue(
            requested.Recipe.Digest,
            out BuiltRow? exact)
            && exact.View.HistoryRowId == frozen.Through.Descriptor.RowId
                ? exact
                : null;
        var units = new List<(int Depth, int PlanIndex,
            FrozenRecipeRowUnit Unit)>();
        for (int planIndex = 0;
             planIndex < frozen.BaseToCandidate.Count;
             planIndex++) {
            FrozenRecipePlan plan = frozen.BaseToCandidate[planIndex];
            if (!workByRecipe.TryGetValue(plan.Recipe.Digest,
                    out List<(int Depth, bool IsOverlayBootstrap)>? work)) {
                continue;
            }
            foreach ((int depth, bool overlayBootstrap) in work) {
                units.Add((depth, planIndex, new FrozenRecipeRowUnit(
                    plan,
                    headToOldest[depth],
                    overlayBootstrap
                )));
            }
        }
        units.Sort(static (left, right) => {
            int row = right.Depth.CompareTo(left.Depth);
            return row != 0
                ? row
                : left.PlanIndex.CompareTo(right.PlanIndex);
        });
        return new ProgressionAttempt(
            new FrozenProgression(
                units.Select(static value => value.Unit).ToArray(),
                anchors,
                requestedExact
            ),
            null
        );
    }

    private (BuiltRow?, RecapGridBuildResult?) HydrateAssignedView(
        FrozenRecipePlan plan,
        HistoryTimelineSelectedRow selected,
        RecapRowView view
    ) {
        HistorySegmentDescriptor descriptor = selected.Descriptor;
        if (view.RefId != descriptor.RefId
            || view.TimelineId != descriptor.TimelineId
            || view.HistoryRowId != descriptor.RowId
            || view.RowDescriptorDigest != descriptor.DescriptorDigest
            || view.RecipeDigest != plan.Recipe.Digest
            || view.TargetDigest != plan.Recipe.Target.Digest
            || view.PreviousHistoryRowId != descriptor.PreviousRowId
            || view.OrderedCells.Count
                != plan.Recipe.Target.OrderedColumns.Count) {
            return (null, Invalid(
                "AssignedViewScopeMismatch",
                "The exact assignment view differs from frozen Timeline or recipe authority."
            ));
        }
        var cells = new RecapCellArtifact[view.OrderedCells.Count];
        for (int index = 0; index < cells.Length; index++) {
            RecapRowViewCell manifest = view.OrderedCells[index];
            BuildTargetColumn target = plan.Recipe.Target
                .OrderedColumns[index];
            if (manifest.LogicalColumnId != target.LogicalColumnId
                || manifest.DefinitionDigest != target.DefinitionDigest) {
                return (null, Invalid(
                    "AssignedViewMemberMismatch",
                    "The assignment manifest differs from the frozen target."
                ));
            }
            RecapGridStoreReadResult<RecapCellArtifact> read =
                _store.Reader.ReadCell(manifest.CellDigest);
            if (read is not RecapGridStoreReadResult<RecapCellArtifact>
                .Found found) {
                return (null, MapStoreCellRead(read));
            }
            RecapCellArtifact cell = found.Value;
            if (cell.CellDigest != manifest.CellDigest
                || cell.LogicalColumnId != manifest.LogicalColumnId
                || cell.DefinitionDigest != manifest.DefinitionDigest
                || cell.EvaluationKey.HistorySegmentDigest
                    != descriptor.DescriptorDigest) {
                return (null, Invalid(
                    "AssignedViewCellMismatch",
                    "An assignment Cell differs from its manifest or Timeline row."
                ));
            }
            cells[index] = cell;
        }
        var built = new BuiltRow(view, Array.AsReadOnly(cells));
        RecapGridBuildResult? validation = ValidateBuiltRow(
            built,
            plan,
            descriptor
        );
        return validation is null
            ? (built, null)
            : (null, validation);
    }

    private (BuiltRow?, RecapGridBuildResult?) ReadAssignedView(
        FrozenRecipePlan plan,
        HistoryTimelineSelectedRow selected
    ) {
        RecapGridStoreReadResult<RecapRowView> read =
            _store.Reader.ReadViewAt(new RowViewAssignmentKey(
                selected.Descriptor.RefId,
                selected.Descriptor.TimelineId,
                plan.Recipe.Digest,
                selected.Descriptor.RowId
            ));
        return read switch {
            RecapGridStoreReadResult<RecapRowView>.Found found
                => HydrateAssignedView(plan, selected, found.Value),
            RecapGridStoreReadResult<RecapRowView>.Missing
                => (null, Invalid(
                    "BaseSameRowUnavailable",
                    "The row-major base recipe assignment is unavailable.")),
            RecapGridStoreReadResult<RecapRowView>.Busy
                => (null, Unavailable(
                    RecapGridBuildDependency.Store,
                    "StoreBusy")),
            RecapGridStoreReadResult<RecapRowView>.Disposed
                => (null, Unavailable(
                    RecapGridBuildDependency.Store,
                    "StoreDisposed")),
            RecapGridStoreReadResult<RecapRowView>.Invalid invalid
                => (null, Unavailable(
                    RecapGridBuildDependency.Store,
                    invalid.Code,
                    invalid.Detail)),
            _ => (null, Invalid(
                "BaseSameRowReadOutcomeInvalid",
                "The Store returned an unknown base assignment outcome."))
        };
    }
}
