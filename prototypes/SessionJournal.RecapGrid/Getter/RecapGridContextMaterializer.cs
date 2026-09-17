using System.Text;
using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Store;

namespace Atelia.SessionJournal.RecapGrid.Getter;

public sealed partial class RecapGridContextHandle {
    public RecapGridContextMaterializeResult Materialize(
        RecapGridContextSelection selection,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(selection);
        using GetterLifetime.Operation? operation = _lifetime.TryEnter();
        if (operation is null) {
            return new RecapGridContextMaterializeResult.Disposed(
                RecapGridContextComponent.RawAuthority
            );
        }
        return MaterializeCore(selection, cancellationToken);
    }

    private RecapGridContextMaterializeResult MaterializeCore(
        RecapGridContextSelection expected,
        CancellationToken cancellationToken
    ) {
        if (!ReferenceEquals(expected.Owner, _lifetime)
            || !string.Equals(
                expected.OwnerNonce,
                _lifetime.OwnerNonce,
                StringComparison.Ordinal)) {
            return new RecapGridContextMaterializeResult.Stale(
                RecapGridContextComponent.Store,
                "The selection belongs to another Getter-owned Store reader."
            );
        }
        HistoryTimelineReaderRowResult witness =
            _lifetime.Timeline.ValidateWitness(
                expected.TimelineHead,
                expected.SelectedRow.Witness
            );
        if (witness is not HistoryTimelineReaderRowResult.Selected validated) {
            return MapTimelineWitnessForMaterialize(witness);
        }
        if (!validated.Row.Descriptor.ToCanonicalBytes().SequenceEqual(
                expected.SelectedRow.Descriptor.ToCanonicalBytes())) {
            return InvalidMaterialization(
                RecapGridContextComponent.Timeline,
                "AncestorWitnessDescriptorMismatch",
                "The validated selected row differs from the original selection."
            );
        }
        RecapGridContextResolveResult? authorityFence = CheckFences(
            expected.CompletionBoundary,
            expected.TimelineHead,
            expected.CadenceHead,
            expected.ControlHead
        );
        if (authorityFence is not null) {
            return MapResolveForMaterialize(authorityFence);
        }
        RecapGridContextResolveResult resolved = ResolveCore(
            expected.CompletionBoundary,
            expected.NthPrevious,
            cancellationToken
        );
        if (resolved is not RecapGridContextResolveResult.Selected selected) {
            return MapResolveForMaterialize(resolved);
        }
        RecapGridContextSelection current = selected.Selection;
        if (current.SnapshotToken != expected.SnapshotToken
            || current.HandleToken != expected.HandleToken
            || current.StoreIdentity != expected.StoreIdentity) {
            return new RecapGridContextMaterializeResult.Stale(
                RecapGridContextComponent.Control,
                "The exact context selection changed before materialization."
            );
        }
        return MaterializeSelected(current, cancellationToken);
    }

    private RecapGridContextMaterializeResult MaterializeSelected(
        RecapGridContextSelection selection,
        CancellationToken cancellationToken
    ) {
        GetterLifetime.StoreOpen storeOpen = _lifetime.OpenStore();
        if (storeOpen is not GetterLifetime.StoreOpen.Opened store) {
            return MapStoreOpenForMaterialize(storeOpen);
        }
        if (store.Handle.Identity != selection.StoreIdentity) {
            return new RecapGridContextMaterializeResult.Stale(
                RecapGridContextComponent.Store,
                "The RecapGrid Store identity changed."
            );
        }
        RecapGridStoreReader reader = store.Handle.Reader;
        RecapGridControlSnapshotResult controlRead =
            _lifetime.Control.ReadSnapshot();
        if (controlRead is not RecapGridControlSnapshotResult.Available
                controlAvailable) {
            return MapControlForMaterialize(controlRead);
        }
        if (controlAvailable.Snapshot.Head != selection.ControlHead) {
            return new RecapGridContextMaterializeResult.Stale(
                RecapGridContextComponent.Control,
                "The whole Control head changed."
            );
        }
        var definitions = controlAvailable.Snapshot.Definitions
            .ToDictionary(static value => value.Digest);
        var cells = new RecapCellArtifact[
            selection.SelectedView.OrderedCells.Count
        ];
        for (int index = 0; index < cells.Length; index++) {
            cancellationToken.ThrowIfCancellationRequested();
            RecapRowViewCell member =
                selection.SelectedView.OrderedCells[index];
            RecapGridStoreReadResult<RecapCellArtifact> read =
                reader.ReadCell(member.CellId);
            if (read is not RecapGridStoreReadResult<
                    RecapCellArtifact>.Found found) {
                return read is RecapGridStoreReadResult<
                        RecapCellArtifact>.Missing
                    ? InvalidMaterialization(
                        RecapGridContextComponent.Store,
                        "SelectedCellMissing",
                        "An exact selected Cell is missing."
                    )
                    : MapStoreReadForMaterialize(read);
            }
            RecapCellArtifact cell = found.Value;
            if (cell.Id != member.CellId
                || cell.LogicalColumnId != member.LogicalColumnId
                || cell.DefinitionDigest != member.DefinitionDigest
                || cell.Slot.HistoryRowId
                    != selection.SelectedRowId
                || !definitions.TryGetValue(
                    member.DefinitionDigest,
                    out MaintainerDefinitionRevision? definition)
                || definition.LogicalColumnId != member.LogicalColumnId) {
                return InvalidMaterialization(
                    RecapGridContextComponent.Store,
                    "SelectedCellAuthorityMismatch",
                    "A selected Cell differs from its RowView, row, or definition."
                );
            }
            int contentBytes;
            try {
                contentBytes = new UTF8Encoding(false, true).GetByteCount(
                    cell.Content
                );
            }
            catch (EncoderFallbackException exception) {
                return InvalidMaterialization(
                    RecapGridContextComponent.Store,
                    "SelectedCellContentInvalid",
                    exception.Message
                );
            }
            if (string.IsNullOrEmpty(cell.Content)
                || contentBytes > definition.MaxContentUtf8Bytes
                || contentBytes > SessionContextContributionContract
                    .MaxContributionUtf8Bytes) {
                return InvalidMaterialization(
                    RecapGridContextComponent.Store,
                    "SelectedCellContentLimit",
                    "A selected Cell cannot be represented by the neutral context contract."
                );
            }
            cells[index] = cell;
        }

        SessionContextContribution[] contributions = new
            SessionContextContribution[cells.Length];
        for (int index = 0; index < cells.Length; index++) {
            MaintainerDefinitionRevision definition = definitions[
                cells[index].DefinitionDigest
            ];
            contributions[index] = new SessionContextContribution(
                definition.Target,
                cells[index].Content,
                SessionContextContributionHasher.CodecId,
                SessionContextContributionHasher.ComputeSha256(
                    cells[index].Content
                ),
                selection.SelectedRow.Descriptor.EndInclusive
            );
        }
        IReadOnlyList<SessionContextContribution> normalized;
        try {
            normalized = SessionContextContributionContract
                .ValidateAndNormalize(contributions);
        }
        catch (Exception exception) when (IsContractFailure(exception)) {
            return InvalidMaterialization(
                RecapGridContextComponent.Store,
                "ContextContributionInvalid",
                exception.Message
            );
        }

        RecapGridContextProvenance provenance = ComputeProvenance(
            selection,
            cells,
            reader,
            definitions,
            cancellationToken
        );
        RecapGridStoreReadResult<RecapRowView> health = reader.ReadView(
            selection.SelectedView.Id
        );
        if (health is not RecapGridStoreReadResult<RecapRowView>.Found
                healthy
            || healthy.Value.Id != selection.SelectedView.Id) {
            return health is RecapGridStoreReadResult<RecapRowView>.Busy
                ? new RecapGridContextMaterializeResult.Busy(
                    RecapGridContextComponent.Store
                )
                : health is RecapGridStoreReadResult<RecapRowView>.Disposed
                    ? new RecapGridContextMaterializeResult.Disposed(
                        RecapGridContextComponent.Store
                    )
                    : InvalidMaterialization(
                        RecapGridContextComponent.Store,
                        "SelectedViewHealthChanged",
                        "The selected RowView changed or the Store became invalid."
                    );
        }

        RecapGridContextResolveResult? fence = CheckFences(
            selection.CompletionBoundary,
            selection.TimelineHead,
            selection.CadenceHead,
            selection.ControlHead
        );
        if (fence is not null) {
            return MapResolveForMaterialize(fence);
        }
        return new RecapGridContextMaterializeResult.Available(
            new SessionContextCandidate(
                selection.SelectedRow.Descriptor.EndInclusive,
                selection.SelectedRow.Descriptor.EndSetups,
                normalized
            ),
            provenance
        );
    }

    private RecapGridContextProvenance ComputeProvenance(
        RecapGridContextSelection selection,
        IReadOnlyList<RecapCellArtifact> selectedCells,
        RecapGridStoreReader reader,
        IReadOnlyDictionary<MaintainerDefinitionDigest,
            MaintainerDefinitionRevision> definitions,
        CancellationToken cancellationToken
    ) {
        var tracker = new ProvenanceBudgetTracker(
            _hooks.ProvenanceBudget
                ?? GetterProvenanceReadBudget.Production
        );
        var current = new ProvenanceRow(
            selection.SelectedRow,
            selection.SelectedView,
            [.. selectedCells]
        );
        if (!tracker.TryIncludeKnown(current)) {
            return new RecapGridContextProvenance(
                RecapGridProvenanceStatus.Verified,
                RecapGridProvenanceStatus.Incomplete,
                selection.Recipe.Kind == GridBuildRecipeKind.Full
                    ? RecapGridProvenanceStatus.Incomplete
                    : RecapGridProvenanceStatus.NotSatisfied,
                tracker.ExaminedRows,
                tracker.ExaminedCells,
                tracker.ExaminedMembers,
                tracker.ExaminedContentUtf8Bytes
            );
        }

        RecapGridProvenanceStatus prior = EvaluatePrior(
            selection,
            current,
            reader,
            tracker,
            definitions,
            cancellationToken,
            out ProvenanceRow? predecessor
        );
        RecapGridProvenanceStatus full = selection.Recipe.Kind
            == GridBuildRecipeKind.Full
                ? prior
                : RecapGridProvenanceStatus.NotSatisfied;
        while (selection.Recipe.Kind == GridBuildRecipeKind.Full
            && full == RecapGridProvenanceStatus.Verified
            && predecessor is not null) {
            cancellationToken.ThrowIfCancellationRequested();
            current = predecessor;
            full = EvaluatePrior(
                selection,
                current,
                reader,
                tracker,
                definitions,
                cancellationToken,
                out predecessor
            );
        }
        return new RecapGridContextProvenance(
            RecapGridProvenanceStatus.Verified,
            prior,
            full,
            tracker.ExaminedRows,
            tracker.ExaminedCells,
            tracker.ExaminedMembers,
            tracker.ExaminedContentUtf8Bytes
        );
    }

    private RecapGridProvenanceStatus EvaluatePrior(
        RecapGridContextSelection selection,
        ProvenanceRow current,
        RecapGridStoreReader reader,
        ProvenanceBudgetTracker tracker,
        IReadOnlyDictionary<MaintainerDefinitionDigest,
            MaintainerDefinitionRevision> definitions,
        CancellationToken cancellationToken,
        out ProvenanceRow? predecessor
    ) {
        predecessor = null;
        HistorySegmentDescriptor descriptor = current.Row.Descriptor;
        if (descriptor.PreviousRowId is null) {
            return current.Cells.All(cell => cell.Slot.HistoryRowId == descriptor.RowId)
                ? RecapGridProvenanceStatus.Verified
                : RecapGridProvenanceStatus.NotSatisfied;
        }
        if (current.View.PreviousRowResultId is not { } previousId) {
            return RecapGridProvenanceStatus.NotSatisfied;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!tracker.CanReadRow()) {
            return RecapGridProvenanceStatus.Incomplete;
        }
        _hooks.BeforeProvenancePredecessorLookup?.Invoke();
        HistoryTimelineReaderRowResult predecessorRead =
            _lifetime.Timeline.ReadSelectedRow(
                selection.TimelineHead,
                descriptor.PreviousRowId.Value
            );
        if (predecessorRead is not HistoryTimelineReaderRowResult.Selected
                selectedPredecessor) {
            return RecapGridProvenanceStatus.Incomplete;
        }
        RecapGridStoreReadResult<RecapRowView> previousRead =
            reader.ReadView(previousId);
        if (previousRead is not RecapGridStoreReadResult<
                RecapRowView>.Found previous) {
            return RecapGridProvenanceStatus.Incomplete;
        }
        if (!tracker.RecordReadRow(previous.Value)) {
            return RecapGridProvenanceStatus.Incomplete;
        }
        if (ValidateView(
                reader,
                previous.Value,
                selectedPredecessor.Row.Descriptor,
                selection.Recipe,
                definitions,
                out RowWork? previousWork
            ) is not null) {
            return RecapGridProvenanceStatus.Incomplete;
        }
        var previousCells = new RecapCellArtifact[
            previous.Value.OrderedCells.Count
        ];
        for (int index = 0; index < previousCells.Length; index++) {
            cancellationToken.ThrowIfCancellationRequested();
            if (!tracker.CanReadCell()) {
                return RecapGridProvenanceStatus.Incomplete;
            }
            RecapRowViewCell member = previous.Value.OrderedCells[index];
            RecapGridStoreReadResult<RecapCellArtifact> read =
                reader.ReadCell(member.CellId);
            if (read is not RecapGridStoreReadResult<
                    RecapCellArtifact>.Found found) {
                return RecapGridProvenanceStatus.Incomplete;
            }
            if (!tracker.RecordReadCell(found.Value)
                || found.Value.Id != member.CellId
                || found.Value.LogicalColumnId != member.LogicalColumnId
                || found.Value.DefinitionDigest != member.DefinitionDigest
                || found.Value.Slot.HistoryRowId
                    != selectedPredecessor.Row.Descriptor.RowId
                || previousWork is { } work
                    && (work.OrderedAssignments[index].IsEvaluate
                        ? found.Value.Slot.WorkId != work.WorkId
                        : member.CellId
                            != work.OrderedAssignments[index].ReusedCellId)) {
                return RecapGridProvenanceStatus.Incomplete;
            }
            previousCells[index] = found.Value;
        }
        predecessor = new ProvenanceRow(
            selectedPredecessor.Row,
            previous.Value,
            previousCells
        );
        // A cell keeps its producer Slot even when an Overlay explicitly reuses it.
        // Its source predecessor exists before that producer's row is published.
        foreach (GridBuildRecipeDigest sourceRecipe in current.Cells
                     .Select(static cell => cell.Slot.RecipeDigest).Distinct()) {
            if (sourceRecipe == current.View.RecipeDigest) { continue; }
            cancellationToken.ThrowIfCancellationRequested();
            if (!tracker.CanReadRow()) { return RecapGridProvenanceStatus.Incomplete; }
            _hooks.BeforeProvenancePredecessorLookup?.Invoke();
            RecapGridStoreReadResult<RecapRowView> sourceRead = reader.ReadViewAt(
                new RowViewAssignmentKey(current.View.RefId, current.View.TimelineId,
                    sourceRecipe, descriptor.PreviousRowId.Value));
            if (sourceRead is not RecapGridStoreReadResult<RecapRowView>.Found source
                || !tracker.RecordReadRow(source.Value)) {
                return RecapGridProvenanceStatus.Incomplete;
            }
            if (source.Value.RefId != current.View.RefId
                || source.Value.TimelineId != current.View.TimelineId
                || source.Value.RecipeDigest != sourceRecipe
                || source.Value.HistoryRowId != descriptor.PreviousRowId.Value) {
                return RecapGridProvenanceStatus.Incomplete;
            }
            if (source.Value.Id != previous.Value.Id) {
                return RecapGridProvenanceStatus.NotSatisfied;
            }
        }
        return RecapGridProvenanceStatus.Verified;
    }

    private sealed record ProvenanceRow(
        HistoryTimelineSelectedRow Row,
        RecapRowView View,
        RecapCellArtifact[] Cells
    );

    private sealed class ProvenanceBudgetTracker {
        private readonly GetterProvenanceReadBudget _budget;

        internal ProvenanceBudgetTracker(
            GetterProvenanceReadBudget budget
        ) => _budget = budget;

        internal int ExaminedRows { get; private set; }
        internal int ExaminedCells { get; private set; }
        internal int ExaminedMembers { get; private set; }
        internal int ExaminedContentUtf8Bytes { get; private set; }

        internal bool TryIncludeKnown(ProvenanceRow row) {
            int bytes;
            try { bytes = checked(row.Cells.Sum(static cell => Encoding.UTF8.GetByteCount(cell.Content))); }
            catch (OverflowException) { return false; }
            if (ExaminedRows >= _budget.MaximumRows
                || row.Cells.Length > _budget.MaximumCells - ExaminedCells
                || row.View.OrderedCells.Count > _budget.MaximumMembers - ExaminedMembers
                || bytes > _budget.MaximumContentUtf8Bytes - ExaminedContentUtf8Bytes) {
                return false;
            }
            ExaminedRows++;
            ExaminedCells += row.Cells.Length;
            ExaminedMembers += row.View.OrderedCells.Count;
            ExaminedContentUtf8Bytes += bytes;
            return true;
        }

        internal bool CanReadRow() => ExaminedRows < _budget.MaximumRows
            && ExaminedMembers <= _budget.MaximumMembers;

        internal bool RecordReadRow(RecapRowView view) {
            ExaminedRows++;
            ExaminedMembers = checked(ExaminedMembers + view.OrderedCells.Count);
            return ExaminedMembers <= _budget.MaximumMembers;
        }

        internal bool CanReadCell() => ExaminedCells < _budget.MaximumCells
            && ExaminedContentUtf8Bytes <= _budget.MaximumContentUtf8Bytes;

        internal bool RecordReadCell(RecapCellArtifact cell) {
            ExaminedCells++;
            ExaminedContentUtf8Bytes = checked(ExaminedContentUtf8Bytes
                + Encoding.UTF8.GetByteCount(cell.Content));
            return ExaminedContentUtf8Bytes <= _budget.MaximumContentUtf8Bytes;
        }
    }

    internal static RecapGridContextMaterializeResult MapResolveForMaterialize(
        RecapGridContextResolveResult result
    ) => result switch {
        RecapGridContextResolveResult.Stale stale
            => new RecapGridContextMaterializeResult.Stale(
                stale.Component,
                stale.Detail
            ),
        RecapGridContextResolveResult.Busy busy
            => new RecapGridContextMaterializeResult.Busy(busy.Component),
        RecapGridContextResolveResult.Disposed disposed
            => new RecapGridContextMaterializeResult.Disposed(
                disposed.Component
            ),
        RecapGridContextResolveResult.UnsupportedSchema schema
            => InvalidMaterialization(
                schema.Component,
                "UnsupportedSchema",
                $"{schema.Component} schema {schema.SchemaVersion} is unsupported."
            ),
        RecapGridContextResolveResult.Invalid invalid
            => new RecapGridContextMaterializeResult.Invalid(
                invalid.Component,
                invalid.Code,
                invalid.Detail
            ),
        RecapGridContextResolveResult.NotOnSelectedPath missing
            => InvalidMaterialization(
                RecapGridContextComponent.Timeline,
                "NotOnSelectedPath",
                $"Row '{missing.RowId}' is not on the selected path."
            ),
        _ => new RecapGridContextMaterializeResult.Stale(
            RecapGridContextComponent.Control,
            "The selected context is no longer selected."
        )
    };

    private static RecapGridContextMaterializeResult
        MapTimelineWitnessForMaterialize(
        HistoryTimelineReaderRowResult result
    ) => result switch {
        HistoryTimelineReaderRowResult.StaleTimelineHead
            => new RecapGridContextMaterializeResult.Stale(
                RecapGridContextComponent.Timeline,
                "The whole Timeline head changed."
            ),
        HistoryTimelineReaderRowResult.NotOnSelectedPath missing
            => InvalidMaterialization(
                RecapGridContextComponent.Timeline,
                "NotOnSelectedPath",
                $"Row '{missing.RowId}' is not on the selected path."
            ),
        HistoryTimelineReaderRowResult.Busy
            => new RecapGridContextMaterializeResult.Busy(
                RecapGridContextComponent.Timeline
            ),
        HistoryTimelineReaderRowResult.Invalid invalid
            when invalid.Code == "HistoryTimelineDisposed"
            => new RecapGridContextMaterializeResult.Disposed(
                RecapGridContextComponent.Timeline
            ),
        HistoryTimelineReaderRowResult.Invalid invalid
            => InvalidMaterialization(
                RecapGridContextComponent.Timeline,
                invalid.Code,
                invalid.Detail
            ),
        _ => InvalidMaterialization(
            RecapGridContextComponent.Timeline,
            "AncestorWitnessOutcomeInvalid",
            "HistoryTimeline returned an unknown witness-validation outcome."
        )
    };

    private static RecapGridContextMaterializeResult MapStoreOpenForMaterialize(
        GetterLifetime.StoreOpen result
    ) => result switch {
        GetterLifetime.StoreOpen.Busy
            => new RecapGridContextMaterializeResult.Busy(
                RecapGridContextComponent.Store
            ),
        GetterLifetime.StoreOpen.Disposed
            => new RecapGridContextMaterializeResult.Disposed(
                RecapGridContextComponent.Store
            ),
        GetterLifetime.StoreOpen.Invalid invalid
            => new RecapGridContextMaterializeResult.Invalid(
                RecapGridContextComponent.Store,
                invalid.Code,
                invalid.Detail
            ),
        GetterLifetime.StoreOpen.UnsupportedSchema schema
            => InvalidMaterialization(
                RecapGridContextComponent.Store,
                "StoreUnsupportedSchema",
                $"Store schema {schema.SchemaVersion} is unsupported."
            ),
        _ => InvalidMaterialization(
            RecapGridContextComponent.Store,
            "StoreUnavailable",
            "The selected RecapGrid Store is unavailable."
        )
    };

    private static RecapGridContextMaterializeResult MapStoreReadForMaterialize<T>(
        RecapGridStoreReadResult<T> result
    ) where T : class => result switch {
        RecapGridStoreReadResult<T>.Busy
            => new RecapGridContextMaterializeResult.Busy(
                RecapGridContextComponent.Store
            ),
        RecapGridStoreReadResult<T>.Disposed
            => new RecapGridContextMaterializeResult.Disposed(
                RecapGridContextComponent.Store
            ),
        RecapGridStoreReadResult<T>.Invalid invalid
            => new RecapGridContextMaterializeResult.Invalid(
                RecapGridContextComponent.Store,
                invalid.Code,
                invalid.Detail
            ),
        _ => InvalidMaterialization(
            RecapGridContextComponent.Store,
            "StoreArtifactMissing",
            "An exact selected Store artifact is unavailable."
        )
    };

    private static RecapGridContextMaterializeResult MapControlForMaterialize(
        RecapGridControlSnapshotResult result
    ) => result switch {
        RecapGridControlSnapshotResult.Busy
            => new RecapGridContextMaterializeResult.Busy(
                RecapGridContextComponent.Control
            ),
        RecapGridControlSnapshotResult.Disposed
            => new RecapGridContextMaterializeResult.Disposed(
                RecapGridContextComponent.Control
            ),
        RecapGridControlSnapshotResult.Invalid invalid
            => new RecapGridContextMaterializeResult.Invalid(
                RecapGridContextComponent.Control,
                invalid.Code,
                invalid.Detail
            ),
        RecapGridControlSnapshotResult.UnsupportedSchema schema
            => InvalidMaterialization(
                RecapGridContextComponent.Control,
                "ControlUnsupportedSchema",
                $"Control schema {schema.SchemaVersion} is unsupported."
            ),
        _ => InvalidMaterialization(
            RecapGridContextComponent.Control,
            "ControlSnapshotOutcomeInvalid",
            "Control returned an unknown snapshot outcome."
        )
    };

    private static RecapGridContextMaterializeResult.Invalid
        InvalidMaterialization(
        RecapGridContextComponent component,
        string code,
        string detail
    ) => new(
            component,
            code,
            detail
        );
}
