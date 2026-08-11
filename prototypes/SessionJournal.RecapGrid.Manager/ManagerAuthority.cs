using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Store;

namespace Atelia.SessionJournal.RecapGrid.Manager;

public sealed partial class RecapGridManager {
    private sealed class BuildState(
        RecapGridBuildBudget budget,
        TimeProvider timeProvider
    ) {
        private readonly long _started = timeProvider.GetTimestamp();

        internal RecapGridBuildBudget Budget { get; } = budget;
        internal int SelectedRows { get; set; }
        internal int RecipeRowSteps { get; set; }
        internal int NewCalls { get; set; }
        internal int CellsCommitted { get; set; }
        internal int RowViewsCommitted { get; set; }

        internal bool HasElapsed()
            => timeProvider.GetElapsedTime(_started)
                >= Budget.MaximumElapsed;

        internal RecapGridBuildMetrics Metrics() => new(
            SelectedRows,
            RecipeRowSteps,
            NewCalls,
            CellsCommitted,
            RowViewsCommitted
        );
    }

    private sealed record FrozenRecipePlan(
        RegisteredGridRecipe Registered,
        int BootstrapIndex,
        int RequiredThroughIndex,
        IReadOnlyDictionary<LogicalColumnId,
            MaintainerDefinitionRevision> Definitions,
        IReadOnlyDictionary<LogicalColumnId, FamilyDefinition> Families
    ) {
        internal GridBuildRecipe Recipe => Registered.Recipe;
    }

    private sealed record FrozenOperation(
        TimelineHeadRef TimelineHead,
        RecapGridControlSnapshot ControlSnapshot,
        RecapGridStoreIdentity StoreIdentity,
        bool IsLive,
        FrozenRecipePlan RequestedRecipe,
        IReadOnlyList<FrozenRecipePlan> BaseToCandidate,
        IReadOnlyList<HistoryTimelineSelectedRow> RootToThrough,
        HistoryTimelineSelectedRow SelectedHead,
        IReadOnlyDictionary<HistoryRowId, int> WholePathIndexes,
        OnlineSelectedRawCapture RawCapture
    );

    private sealed record FreezeAttempt(
        FrozenOperation? Value,
        RecapGridBuildResult? Error
    );

    private FreezeAttempt FreezeOperation(
        RecapGridBuildRequest request,
        BuildState state,
        CancellationToken cancellationToken
    ) {
        if (cancellationToken.IsCancellationRequested) {
            return Error(new RecapGridBuildResult.Cancelled());
        }
        HistoryTimelineSnapshotResult timelineRead =
            _timeline.Reader.ReadSnapshot();
        if (timelineRead is not HistoryTimelineSnapshotResult.Available
            timelineAvailable) {
            return Error(MapTimelineSnapshot(timelineRead));
        }
        TimelineHeadRef timelineHead = timelineAvailable.Head;
        RecapGridControlSnapshotResult controlRead =
            _control.Reader.ReadSnapshot();
        if (controlRead is not RecapGridControlSnapshotResult.Available
            controlAvailable) {
            return Error(MapControlSnapshot(controlRead));
        }
        RecapGridControlSnapshot control = controlAvailable.Snapshot;
        if (control.Head.RefId != timelineHead.RefId
            || control.Head.TimelineId != timelineHead.TimelineId
            || _timeline.Locator.RefId != timelineHead.RefId
            || _timeline.Locator.ActiveTimelineId
                != timelineHead.TimelineId) {
            return Error(Invalid(
                "FrozenAuthorityScopeMismatch",
                "Timeline and Control do not bind one exact Ref and Timeline."
            ));
        }
        RegisteredGridRecipe? requested = request.Selection switch {
            RecapGridBuildSelection.LiveActive => control.ActiveRecipe,
            RecapGridBuildSelection.ExplicitCandidate candidate
                => control.Recipes.SingleOrDefault(recipe =>
                    recipe.Recipe.Digest == candidate.RecipeDigest),
            _ => null
        };
        if (requested is null) {
            return request.Selection switch {
                RecapGridBuildSelection.LiveActive
                    => Error(new RecapGridBuildResult.NoActiveRecipe()),
                RecapGridBuildSelection.ExplicitCandidate candidate
                    => Error(new RecapGridBuildResult.RecipeAbsent(
                        candidate.RecipeDigest
                    )),
                _ => Error(Invalid(
                    "BuildSelectionInvalid",
                    "The build selection subtype is unsupported."
                ))
            };
        }
        if (requested.Recipe.TimelineId != timelineHead.TimelineId) {
            return Error(Invalid(
                "RecipeTimelineMismatch",
                "The requested recipe belongs to another Timeline."
            ));
        }

        if (timelineHead.HeadRowId is null) {
            if (request.ThroughRowId is { } impossible) {
                return Error(
                    new RecapGridBuildResult.ThroughRowNotSelected(
                        impossible
                    )
                );
            }
            if (requested.Recipe.BootstrapThroughRowId is not null) {
                return Error(Invalid(
                    "EmptyTimelineBootstrapMismatch",
                    "A recipe on an empty Timeline must have an empty bootstrap."
                ));
            }
            return Error(new RecapGridBuildResult.NoRows(
                timelineHead,
                requested.Recipe.Digest
            ));
        }

        (IReadOnlyList<HistoryTimelineSelectedRow>? wholePath,
            RecapGridBuildResult? pathError) = ReadWholeSelectedPath(
            timelineHead,
            request.Budget.MaximumSelectedRows,
            state,
            cancellationToken
        );
        if (pathError is not null) {
            return Error(pathError);
        }
        IReadOnlyList<HistoryTimelineSelectedRow> rootToHead = wholePath!;
        var indexes = new Dictionary<HistoryRowId, int>();
        for (int index = 0; index < rootToHead.Count; index++) {
            if (!indexes.TryAdd(
                    rootToHead[index].Descriptor.RowId,
                    index)) {
                return Error(Invalid(
                    "SelectedPathDuplicateRow",
                    "The selected path contains a duplicate row identity."
                ));
            }
        }
        HistoryRowId through = request.ThroughRowId
            ?? timelineHead.HeadRowId.Value;
        if (!indexes.TryGetValue(through, out int throughIndex)) {
            return Error(
                new RecapGridBuildResult.ThroughRowNotSelected(through)
            );
        }

        (IReadOnlyList<FrozenRecipePlan>? closure,
            RecapGridBuildResult? closureError) = FreezeRecipeClosure(
            requested,
            throughIndex,
            indexes,
            control,
            request.Budget.MaximumRecipeRowSteps
        );
        if (closureError is not null) {
            return Error(closureError);
        }
        IReadOnlyList<FrozenRecipePlan> plans = closure!;
        int stepCount = plans.Sum(plan => checked(
            plan.RequiredThroughIndex + 1
        ));
        if (stepCount > request.Budget.MaximumRecipeRowSteps) {
            return Error(new RecapGridBuildResult.BudgetExceeded(
                RecapGridBuildBudgetKind.RecipeRowSteps,
                through
            ));
        }
        if (state.HasElapsed()) {
            return Error(new RecapGridBuildResult.BudgetExceeded(
                RecapGridBuildBudgetKind.Elapsed,
                through
            ));
        }

        OnlineSelectedRawCaptureResult capture;
        try {
            _testHooks.BeforeCaptureRaw?.Invoke();
            capture = _timeline.CaptureRaw(
                timelineHead,
                cancellationToken
            );
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested) {
            return Error(new RecapGridBuildResult.Cancelled());
        }
        if (capture is not OnlineSelectedRawCaptureResult.Captured
            captured) {
            return Error(MapRawCapture(capture));
        }
        RecapGridBuildResult? fence = CheckTimelineFence(timelineHead);
        if (fence is not null) {
            return Error(fence);
        }
        return new FreezeAttempt(
            new FrozenOperation(
                timelineHead,
                control,
                _store.Identity,
                request.Selection is RecapGridBuildSelection.LiveActive,
                plans[^1],
                plans,
                rootToHead.Take(throughIndex + 1).ToArray(),
                rootToHead[^1],
                indexes,
                captured.Capture
            ),
            null
        );
    }

    private (IReadOnlyList<HistoryTimelineSelectedRow>?,
        RecapGridBuildResult?) ReadWholeSelectedPath(
        TimelineHeadRef expected,
        int maximumRows,
        BuildState state,
        CancellationToken cancellationToken
    ) {
        var headToRoot = new List<HistoryTimelineSelectedRow>();
        HistoryTimelinePathCursor? cursor = null;
        do {
            if (cancellationToken.IsCancellationRequested) {
                return (null, new RecapGridBuildResult.Cancelled());
            }
            HistoryTimelinePathPageResult result =
                _timeline.Reader.ReadSelectedPathPage(
                    expected,
                    cursor,
                    HistoryTimelineStoreLimits.MaximumPathPageRows
                );
            if (result is not HistoryTimelinePathPageResult.Page page) {
                return (null, MapTimelinePath(result));
            }
            foreach (HistoryTimelineSelectedRow row in page.Value.Rows) {
                headToRoot.Add(row);
                state.SelectedRows = headToRoot.Count;
                if (headToRoot.Count > maximumRows) {
                    return (null,
                        new RecapGridBuildResult.BudgetExceeded(
                            RecapGridBuildBudgetKind.SelectedRows,
                            row.Descriptor.RowId
                        ));
                }
            }
            cursor = page.Value.Next;
        } while (cursor is not null);
        if (headToRoot.Count == 0
            || headToRoot[0].Descriptor.RowId != expected.HeadRowId) {
            return (null, Invalid(
                "SelectedPathHeadMismatch",
                "The selected path does not begin at the expected head."
            ));
        }
        for (int index = 0; index < headToRoot.Count; index++) {
            HistorySegmentDescriptor descriptor =
                headToRoot[index].Descriptor;
            HistoryRowId? next = index + 1 < headToRoot.Count
                ? headToRoot[index + 1].Descriptor.RowId
                : null;
            if (descriptor.TimelineId != expected.TimelineId
                || descriptor.RefId != expected.RefId
                || descriptor.PreviousRowId != next) {
                return (null, Invalid(
                    "SelectedPathChainMismatch",
                    "The selected path descriptor chain is not exact."
                ));
            }
        }
        headToRoot.Reverse();
        return (headToRoot.AsReadOnly(), null);
    }

    private (IReadOnlyList<FrozenRecipePlan>?, RecapGridBuildResult?)
        FreezeRecipeClosure(
            RegisteredGridRecipe requested,
            int requestedThroughIndex,
            IReadOnlyDictionary<HistoryRowId, int> pathIndexes,
            RecapGridControlSnapshot control,
            int maximumRecipeRowSteps
        ) {
        Dictionary<GridBuildRecipeDigest, RegisteredGridRecipe> recipes;
        Dictionary<MaintainerDefinitionDigest,
            MaintainerDefinitionRevision> definitions;
        Dictionary<FamilyDefinitionDigest, FamilyDefinition> families;
        try {
            recipes = control.Recipes.ToDictionary(
                static recipe => recipe.Recipe.Digest
            );
            definitions = control.Definitions.ToDictionary(
                static definition => definition.Digest
            );
            families = control.Families.ToDictionary(
                static family => family.Digest
            );
        }
        catch (ArgumentException) {
            return (null, Invalid(
                "ControlSnapshotDuplicateIdentity",
                "The Control snapshot contains duplicate identities."
            ));
        }

        var candidateToBase = new List<RegisteredGridRecipe>();
        var seen = new HashSet<GridBuildRecipeDigest>();
        RegisteredGridRecipe current = requested;
        while (true) {
            if (!seen.Add(current.Recipe.Digest)) {
                return (null, Invalid(
                    "RecipeBaseCycle",
                    "The frozen recipe closure contains a cycle."
                ));
            }
            if (current.Recipe.TimelineId != control.Head.TimelineId) {
                return (null, Invalid(
                    "RecipeTimelineMismatch",
                    "A recipe closure member belongs to another Timeline."
                ));
            }
            candidateToBase.Add(current);
            if (current.Recipe.BaseRecipeDigest is not { } baseDigest) {
                break;
            }
            if (!recipes.TryGetValue(baseDigest, out RegisteredGridRecipe?
                    baseRecipe)) {
                return (null, Invalid(
                    "RecipeBaseUnavailable",
                    "A frozen base recipe is unavailable."
                ));
            }
            try {
                current.Recipe.ValidateBase(baseRecipe.Recipe);
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidOperationException) {
                return (null, Invalid(
                    "RecipeBaseInvalid",
                    exception.Message
                ));
            }
            current = baseRecipe;
        }

        var required = new Dictionary<GridBuildRecipeDigest, int> {
            [requested.Recipe.Digest] = requestedThroughIndex
        };
        for (int index = 0; index < candidateToBase.Count; index++) {
            RegisteredGridRecipe child = candidateToBase[index];
            int bootstrapIndex;
            if (child.Recipe.BootstrapThroughRowId is { } bootstrap) {
                if (!pathIndexes.TryGetValue(bootstrap,
                        out bootstrapIndex)
                    || child.Bootstrap.RowId != bootstrap
                    || child.Bootstrap.DescriptorDigest is null) {
                    return (null, Invalid(
                        "RecipeBootstrapNotSelected",
                        "A frozen recipe bootstrap is not on the selected path."
                    ));
                }
            }
            else if (child.Bootstrap.RowId is null
                     && child.Bootstrap.DescriptorDigest is null) {
                bootstrapIndex = -1;
            }
            else {
                return (null, Invalid(
                    "RecipeBootstrapInvalid",
                    "An empty bootstrap has inconsistent stored evidence."
                ));
            }
            int childRequired = required[child.Recipe.Digest];
            if (index + 1 < candidateToBase.Count) {
                RegisteredGridRecipe parent = candidateToBase[index + 1];
                int parentRequired = Math.Min(
                    childRequired,
                    bootstrapIndex
                );
                required[parent.Recipe.Digest] = required.TryGetValue(
                    parent.Recipe.Digest,
                    out int existing
                ) ? Math.Max(existing, parentRequired) : parentRequired;
            }
        }
        candidateToBase.Reverse();
        var plans = new List<FrozenRecipePlan>(candidateToBase.Count);
        int totalSteps = 0;
        foreach (RegisteredGridRecipe registered in candidateToBase) {
            GridBuildRecipe recipe = registered.Recipe;
            int bootstrapIndex = recipe.BootstrapThroughRowId is { } rowId
                ? pathIndexes[rowId]
                : -1;
            var recipeDefinitions = new Dictionary<LogicalColumnId,
                MaintainerDefinitionRevision>();
            var recipeFamilies = new Dictionary<LogicalColumnId,
                FamilyDefinition>();
            foreach (BuildTargetColumn column in
                     recipe.Target.OrderedColumns) {
                if (!definitions.TryGetValue(
                        column.DefinitionDigest,
                        out MaintainerDefinitionRevision? definition)
                    || definition.LogicalColumnId != column.LogicalColumnId
                    || !families.TryGetValue(
                        definition.FamilyDigest,
                        out FamilyDefinition? family)) {
                    return (null, Invalid(
                        "RecipeDefinitionClosureInvalid",
                        "A recipe target lacks its exact definition or family."
                    ));
                }
                recipeDefinitions.Add(column.LogicalColumnId, definition);
                recipeFamilies.Add(column.LogicalColumnId, family);
            }
            int through = required[recipe.Digest];
            totalSteps = checked(totalSteps + through + 1);
            if (totalSteps > maximumRecipeRowSteps) {
                return (null,
                    new RecapGridBuildResult.BudgetExceeded(
                        RecapGridBuildBudgetKind.RecipeRowSteps,
                        null
                    ));
            }
            plans.Add(new FrozenRecipePlan(
                registered,
                bootstrapIndex,
                through,
                recipeDefinitions,
                recipeFamilies
            ));
        }
        return (plans.AsReadOnly(), null);
    }

    private RecapGridBuildResult? CheckTimelineFence(
        TimelineHeadRef expected
    ) => _timeline.Reader.ReadSnapshot() switch {
        HistoryTimelineSnapshotResult.Available available
            when available.Head == expected => null,
        HistoryTimelineSnapshotResult.Available available
            => new RecapGridBuildResult.StaleTimelineHead(available.Head),
        { } other => MapTimelineSnapshot(other)
    };

    private RecapGridBuildResult? CheckControlFence(
        FrozenOperation frozen
    ) {
        RecapGridControlSnapshotResult result =
            _control.Reader.ReadSnapshot();
        if (result is not RecapGridControlSnapshotResult.Available
            available) {
            return MapControlSnapshot(result);
        }
        ControlHeadRef actual = available.Snapshot.Head;
        bool valid = frozen.IsLive
            ? actual == frozen.ControlSnapshot.Head
                && actual.ActiveRecipeDigest
                    == frozen.RequestedRecipe.Recipe.Digest
            : actual.InstanceId == frozen.ControlSnapshot.Head.InstanceId
                && actual.RefId == frozen.ControlSnapshot.Head.RefId
                && actual.TimelineId
                    == frozen.ControlSnapshot.Head.TimelineId;
        return valid
            ? null
            : new RecapGridBuildResult.StaleControlAuthority(actual);
    }

    private static FreezeAttempt Error(RecapGridBuildResult error)
        => new(null, error);

    private static RecapGridBuildResult MapTimelineSnapshot(
        HistoryTimelineSnapshotResult result
    ) => result switch {
        HistoryTimelineSnapshotResult.Busy
            => Unavailable(RecapGridBuildDependency.Timeline,
                "TimelineBusy"),
        HistoryTimelineSnapshotResult.UnsupportedSchema schema
            => Unavailable(RecapGridBuildDependency.Timeline,
                "TimelineUnsupportedSchema",
                schema.SchemaVersion.ToString()),
        HistoryTimelineSnapshotResult.Invalid invalid
            => Unavailable(RecapGridBuildDependency.Timeline,
                invalid.Code, invalid.Detail),
        _ => Invalid("TimelineSnapshotOutcomeInvalid",
            "The Timeline reader returned an unknown snapshot outcome.")
    };

    private static RecapGridBuildResult MapTimelinePath(
        HistoryTimelinePathPageResult result
    ) => result switch {
        HistoryTimelinePathPageResult.StaleTimelineHead stale
            => new RecapGridBuildResult.StaleTimelineHead(stale.Actual),
        HistoryTimelinePathPageResult.Busy
            => Unavailable(RecapGridBuildDependency.Timeline,
                "TimelineBusy"),
        HistoryTimelinePathPageResult.Invalid invalid
            => Unavailable(RecapGridBuildDependency.Timeline,
                invalid.Code, invalid.Detail),
        _ => Invalid("TimelinePathOutcomeInvalid",
            "The Timeline reader returned an unknown path outcome.")
    };

    private static RecapGridBuildResult MapControlSnapshot(
        RecapGridControlSnapshotResult result
    ) => result switch {
        RecapGridControlSnapshotResult.Busy
            => Unavailable(RecapGridBuildDependency.Control,
                "ControlBusy"),
        RecapGridControlSnapshotResult.UnsupportedSchema schema
            => Unavailable(RecapGridBuildDependency.Control,
                "ControlUnsupportedSchema",
                schema.SchemaVersion.ToString()),
        RecapGridControlSnapshotResult.Disposed
            => Unavailable(RecapGridBuildDependency.Control,
                "ControlDisposed"),
        RecapGridControlSnapshotResult.Invalid invalid
            => Unavailable(RecapGridBuildDependency.Control,
                invalid.Code, invalid.Detail),
        _ => Invalid("ControlSnapshotOutcomeInvalid",
            "The Control reader returned an unknown snapshot outcome.")
    };

    private static RecapGridBuildResult MapRawCapture(
        OnlineSelectedRawCaptureResult result
    ) => result switch {
        OnlineSelectedRawCaptureResult.Empty
            => Unavailable(RecapGridBuildDependency.RawHistory,
                "RawHistoryUnexpectedlyEmpty"),
        OnlineSelectedRawCaptureResult.StaleTimelineHead stale
            => new RecapGridBuildResult.StaleTimelineHead(stale.Actual),
        OnlineSelectedRawCaptureResult.PartitionPolicyUnavailable missing
            => Unavailable(RecapGridBuildDependency.Timeline,
                "PartitionPolicyUnavailable", missing.PolicyDigest),
        OnlineSelectedRawCaptureResult.BackendBusy
            => Unavailable(RecapGridBuildDependency.Timeline,
                "TimelineBusy"),
        OnlineSelectedRawCaptureResult.Invalid invalid
            => Unavailable(RecapGridBuildDependency.RawHistory,
                invalid.Code, invalid.Detail),
        _ => Invalid("RawCaptureOutcomeInvalid",
            "The Timeline returned an unknown raw-capture outcome.")
    };

    private static RecapGridBuildResult.Unavailable Unavailable(
        RecapGridBuildDependency dependency,
        string code,
        string detail = "The dependency is unavailable."
    ) => new(dependency, code, detail);

    private static RecapGridBuildResult.Invalid Invalid(
        string code,
        string detail
    ) => new(code, detail);
}
