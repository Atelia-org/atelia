using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Store;

namespace Atelia.SessionJournal.RecapGrid.Getter;

public sealed partial class RecapGridContextHandle : IDisposable,
    ICoherentContextCandidateSource,
    ISessionContextLifecycleCoordinator {
    private const string HandlePrefix = "recap-grid-v1";

    private readonly SessionJournalReadView _selectedRef;
    private readonly string _repositoryPath;
    private readonly RefId _refId;
    private readonly GetterLifetime _lifetime;
    private readonly GetterTestHooks _hooks;

    internal RecapGridContextHandle(
        SessionJournalReadView selectedRef,
        string repositoryPath,
        RefId refId,
        GetterLifetime lifetime,
        GetterTestHooks hooks
    ) {
        _selectedRef = selectedRef;
        _repositoryPath = repositoryPath;
        _refId = refId;
        _lifetime = lifetime;
        _hooks = hooks;
    }

    public RecapGridContextResolveResult Resolve(
        EventAddress completionBoundary,
        int nthPrevious,
        CancellationToken cancellationToken = default
    ) {
        using GetterLifetime.Operation? operation = _lifetime.TryEnter();
        if (operation is null) {
            return new RecapGridContextResolveResult.Disposed(
                RecapGridContextComponent.RawAuthority
            );
        }
        return ResolveCore(
            completionBoundary,
            nthPrevious,
            cancellationToken
        );
    }

    private RecapGridContextResolveResult ResolveCore(
        EventAddress completionBoundary,
        int nthPrevious,
        CancellationToken cancellationToken
    ) {
        if (completionBoundary == default) {
            return Invalid(
                RecapGridContextComponent.RawAuthority,
                "CompletionBoundaryInvalid",
                "The completion boundary cannot be default."
            );
        }
        if (nthPrevious < 0) {
            return Invalid(
                RecapGridContextComponent.RawAuthority,
                "NthPreviousInvalid",
                "NthPrevious cannot be negative."
            );
        }
        if (nthPrevious > RecapGridGetterLimits.MaximumNthPrevious) {
            return new RecapGridContextResolveResult.LimitExceeded(
                nameof(RecapGridGetterLimits.MaximumNthPrevious)
            );
        }
        cancellationToken.ThrowIfCancellationRequested();
        RecapGridContextResolveResult? raw = RequireRawHead(
            completionBoundary
        );
        if (raw is not null) {
            return raw;
        }

        HistoryTimelineSnapshotResult timelineRead =
            _lifetime.Timeline.ReadSnapshot();
        if (timelineRead is not HistoryTimelineSnapshotResult.Available
                timelineAvailable) {
            return MapTimelineSnapshot(timelineRead);
        }
        TimelineHeadRef timelineHead = timelineAvailable.Head;
        if (timelineHead.RefId != _refId) {
            return Invalid(
                RecapGridContextComponent.Timeline,
                "TimelineRefMismatch",
                "The Timeline snapshot belongs to another Ref."
            );
        }

        RecapGridControlSnapshotResult controlRead =
            _lifetime.Control.ReadSnapshot();
        if (controlRead is not RecapGridControlSnapshotResult.Available
                controlAvailable) {
            return MapControlSnapshot(controlRead);
        }
        RecapGridControlSnapshot control = controlAvailable.Snapshot;
        if (control.Head.RefId != _refId
            || control.Head.TimelineId != timelineHead.TimelineId) {
            return Invalid(
                RecapGridContextComponent.Control,
                "ControlScopeMismatch",
                "Control and Timeline do not bind the same Ref and Timeline."
            );
        }

        RegisteredGridRecipe? active = null;
        if (control.Head.ActiveRecipeDigest is { } activeDigest) {
            RegisteredGridRecipe[] matches = [
                .. control.Recipes.Where(candidate =>
                    candidate.Recipe.Digest == activeDigest)
            ];
            if (matches.Length != 1) {
                return Invalid(
                    RecapGridContextComponent.Control,
                    "ActiveRecipeInvalid",
                    "The active recipe is absent or duplicated."
                );
            }
            active = matches[0];
        }

        // No sealed prefix exists yet. Even a recipe registered and activated
        // at the empty bootstrap must allow raw history to accumulate until
        // the first Timeline row can be sealed.
        if (timelineHead.HeadRowId is null || active is null) {
            return CompleteTerminal(
                new RecapGridContextResolveResult.RawHistoryAuthorized(),
                completionBoundary,
                timelineHead,
                control.Head
            );
        }

        GridBuildRecipe recipe = active.Recipe;
        RecapGridContextResolveResult? recipeFailure = ValidateActiveRecipe(
            control,
            timelineHead,
            recipe
        );
        if (recipeFailure is not null) {
            return recipeFailure;
        }

        HistoryTimelineReaderRowResult headRowRead =
            _lifetime.Timeline.ReadSelectedRow(
                timelineHead,
                timelineHead.HeadRowId.Value
            );
        if (headRowRead is not HistoryTimelineReaderRowResult.Selected
                headSelected) {
            return MapTimelineRow(headRowRead);
        }
        HistoryTimelineSelectedRow currentRow = headSelected.Row;
        FulfilledViewKey currentKey;
        try {
            currentKey = FulfilledViewKey.Create(
                _refId,
                timelineHead,
                currentRow.Descriptor.DescriptorDigest,
                recipe
            );
        }
        catch (Exception exception) when (IsContractFailure(exception)) {
            return Invalid(
                RecapGridContextComponent.Control,
                "FulfilledKeyInvalid",
                exception.Message
            );
        }

        GetterLifetime.StoreOpen storeOpen = _lifetime.OpenStore();
        if (storeOpen is not GetterLifetime.StoreOpen.Opened store) {
            RecapGridContextResolveResult unavailable = MapStoreOpen(
                storeOpen,
                currentKey
            );
            return unavailable is RecapGridContextResolveResult.Unfulfilled
                ? CompleteTerminal(
                    unavailable,
                    completionBoundary,
                    timelineHead,
                    control.Head
                )
                : unavailable;
        }
        RecapGridStoreReader reader = store.Handle.Reader;
        RecapGridStoreReadResult<RecapGridFulfilledView> fulfilledRead =
            reader.ReadFulfilled(currentKey);
        if (fulfilledRead is RecapGridStoreReadResult<
                RecapGridFulfilledView>.Missing) {
            return CompleteTerminal(
                new RecapGridContextResolveResult.Unfulfilled(currentKey),
                completionBoundary,
                timelineHead,
                control.Head
            );
        }
        if (fulfilledRead is not RecapGridStoreReadResult<
                RecapGridFulfilledView>.Found fulfilled) {
            return MapStoreRead(fulfilledRead);
        }
        RecapGridStoreReadResult<RecapRowView> currentViewRead =
            reader.ReadView(fulfilled.Value.ViewDigest);
        if (currentViewRead is not RecapGridStoreReadResult<
                RecapRowView>.Found currentViewFound) {
            return currentViewRead is RecapGridStoreReadResult<
                    RecapRowView>.Missing
                ? Invalid(
                    RecapGridContextComponent.Store,
                    "FulfilledViewMissing",
                    "The current fulfillment refers to a missing RowView."
                )
                : MapStoreRead(currentViewRead);
        }
        RecapRowView currentView = currentViewFound.Value;
        RecapGridContextResolveResult? currentFailure = ValidateView(
            currentView,
            currentRow.Descriptor,
            recipe
        );
        if (currentFailure is not null) {
            return currentFailure;
        }

        HistoryTimelineSelectedRow selectedRow = currentRow;
        RecapRowView selectedView = currentView;
        for (int ordinal = 0; ordinal < nthPrevious; ordinal++) {
            HistoryRowId? previousRowId = selectedRow.Descriptor.PreviousRowId;
            RowViewDigest? previousViewDigest =
                selectedView.PreviousViewDigest;
            if (previousRowId is null && previousViewDigest is null) {
                return CompleteTerminal(
                    new RecapGridContextResolveResult.OrdinalUnavailable(),
                    completionBoundary,
                    timelineHead,
                    control.Head
                );
            }
            if (previousRowId is null || previousViewDigest is null) {
                return Invalid(
                    RecapGridContextComponent.Store,
                    "PreviousViewChainInvalid",
                    "Timeline and RowView predecessor links disagree."
                );
            }
            HistoryTimelineReaderRowResult previousRowRead =
                _lifetime.Timeline.ReadSelectedRow(
                    timelineHead,
                    previousRowId.Value
                );
            if (previousRowRead is not
                    HistoryTimelineReaderRowResult.Selected previousSelected) {
                return MapTimelineRow(previousRowRead);
            }
            RecapGridStoreReadResult<RecapRowView> previousViewRead =
                reader.ReadView(previousViewDigest.Value);
            if (previousViewRead is not RecapGridStoreReadResult<
                    RecapRowView>.Found previousFound) {
                return previousViewRead is RecapGridStoreReadResult<
                        RecapRowView>.Missing
                    ? Invalid(
                        RecapGridContextComponent.Store,
                        "PreviousViewMissing",
                        "The exact predecessor RowView is missing."
                    )
                    : MapStoreRead(previousViewRead);
            }
            RecapGridContextResolveResult? previousFailure = ValidateView(
                previousFound.Value,
                previousSelected.Row.Descriptor,
                recipe
            );
            if (previousFailure is not null) {
                return previousFailure;
            }
            selectedRow = previousSelected.Row;
            selectedView = previousFound.Value;
        }

        string handle = FormatHandle(completionBoundary, nthPrevious);
        string snapshot = ComputeSnapshotToken(
            timelineHead,
            control.Head,
            store.Handle.Identity,
            recipe,
            currentKey,
            fulfilled.Value.ViewDigest,
            selectedRow.Descriptor,
            selectedView
        );
        var selectedResult = new RecapGridContextResolveResult.Selected(
            new RecapGridContextSelection(
                completionBoundary,
                nthPrevious,
                timelineHead,
                control.Head,
                store.Handle.Identity,
                recipe,
                selectedRow,
                selectedView,
                currentKey,
                fulfilled.Value.ViewDigest,
                _lifetime,
                _lifetime.OwnerNonce,
                handle,
                snapshot
            )
        );
        return CompleteTerminal(
            selectedResult,
            completionBoundary,
            timelineHead,
            control.Head
        );
    }

    private RecapGridContextResolveResult? ValidateActiveRecipe(
        RecapGridControlSnapshot control,
        TimelineHeadRef timelineHead,
        GridBuildRecipe recipe
    ) {
        if (recipe.TimelineId != timelineHead.TimelineId
            || recipe.Target.OrderedColumns.Count == 0) {
            return Invalid(
                RecapGridContextComponent.Control,
                "ActiveRecipeScopeInvalid",
                "The active recipe scope or target is invalid."
            );
        }
        var definitions = control.Definitions.ToDictionary(
            static value => value.Digest
        );
        var targets = new HashSet<(
            ContextHeaderCarrier Carrier,
            string BlockKey
        )>();
        foreach (BuildTargetColumn column in recipe.Target.OrderedColumns) {
            if (!definitions.TryGetValue(
                    column.DefinitionDigest,
                    out MaintainerDefinitionRevision? definition)
                || definition.LogicalColumnId != column.LogicalColumnId) {
                return Invalid(
                    RecapGridContextComponent.Control,
                    "ActiveRecipeDefinitionInvalid",
                    "The active recipe target definition is absent or mismatched."
                );
            }
            if (definition.MaxContentUtf8Bytes
                    > SessionContextContributionContract
                        .MaxContributionUtf8Bytes
                || !targets.Add((
                    definition.Target.Carrier,
                    definition.Target.BlockKey))) {
                return Invalid(
                    RecapGridContextComponent.Control,
                    "ActiveRecipeContextShapeInvalid",
                    "The active recipe is not context-composable."
                );
            }
        }
        return null;
    }

    private static RecapGridContextResolveResult? ValidateView(
        RecapRowView view,
        HistorySegmentDescriptor descriptor,
        GridBuildRecipe recipe
    ) {
        if (view.TimelineId != descriptor.TimelineId
            || view.HistoryRowId != descriptor.RowId
            || view.RowDescriptorDigest != descriptor.DescriptorDigest
            || view.RecipeDigest != recipe.Digest
            || view.TargetDigest != recipe.Target.Digest
            || (view.PreviousViewDigest is null)
                != (descriptor.PreviousRowId is null)
            || view.OrderedCells.Count
                != recipe.Target.OrderedColumns.Count) {
            return Invalid(
                RecapGridContextComponent.Store,
                "RowViewAuthorityMismatch",
                "The RowView differs from its Timeline row or active recipe."
            );
        }
        for (int index = 0; index < view.OrderedCells.Count; index++) {
            RecapRowViewCell member = view.OrderedCells[index];
            BuildTargetColumn target = recipe.Target.OrderedColumns[index];
            if (member.LogicalColumnId != target.LogicalColumnId
                || member.DefinitionDigest != target.DefinitionDigest) {
                return Invalid(
                    RecapGridContextComponent.Store,
                    "RowViewMembershipMismatch",
                    "The RowView does not exactly cover the active target."
                );
            }
        }
        return null;
    }

    private RecapGridContextResolveResult? CheckFences(
        EventAddress completionBoundary,
        TimelineHeadRef timelineHead,
        ControlHeadRef controlHead
    ) {
        RecapGridContextResolveResult? raw = RequireRawHead(
            completionBoundary
        );
        if (raw is not null) {
            return raw;
        }
        HistoryTimelineSnapshotResult timeline =
            _lifetime.Timeline.ReadSnapshot();
        if (timeline is not HistoryTimelineSnapshotResult.Available available) {
            return MapTimelineSnapshot(timeline);
        }
        if (available.Head != timelineHead) {
            return new RecapGridContextResolveResult.Stale(
                RecapGridContextComponent.Timeline,
                "The whole Timeline head changed."
            );
        }
        RecapGridControlSnapshotResult control =
            _lifetime.Control.ReadSnapshot();
        if (control is not RecapGridControlSnapshotResult.Available controlValue) {
            return MapControlSnapshot(control);
        }
        if (controlValue.Snapshot.Head != controlHead) {
            return new RecapGridContextResolveResult.Stale(
                RecapGridContextComponent.Control,
                "The whole Control head changed."
            );
        }
        return null;
    }

    private RecapGridContextResolveResult? RequireRawHead(
        EventAddress expected
    ) {
        try {
            EventAddress? actual = _selectedRef.ReadCurrentHead();
            return actual == expected
                ? null
                : new RecapGridContextResolveResult.Stale(
                    RecapGridContextComponent.RawAuthority,
                    $"Expected raw head '{expected}', observed '{actual}'."
                );
        }
        catch (ObjectDisposedException) {
            return new RecapGridContextResolveResult.Disposed(
                RecapGridContextComponent.RawAuthority
            );
        }
    }

    private string FormatHandle(
        EventAddress completionBoundary,
        int nthPrevious
    ) => string.Join(
        '|',
        HandlePrefix,
        _lifetime.OwnerNonce,
        EventAddressTextCodec.Format(completionBoundary),
        nthPrevious.ToString(System.Globalization.CultureInfo.InvariantCulture)
    );

    private HandleParseResult TryParseHandle(
        string value,
        out EventAddress completionBoundary,
        out int nthPrevious
    ) {
        completionBoundary = default;
        nthPrevious = default;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512) {
            return HandleParseResult.Invalid;
        }
        string[] parts = value.Split('|');
        bool parsed = parts.Length == 4
            && string.Equals(parts[0], HandlePrefix, StringComparison.Ordinal)
            && IsLowerHex(parts[1], 32)
            && EventAddressTextCodec.TryParse(parts[2], out completionBoundary)
            && int.TryParse(
                parts[3],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out nthPrevious
            )
            && nthPrevious >= 0;
        if (!parsed) {
            return HandleParseResult.Invalid;
        }
        string canonical = string.Join(
            '|',
            HandlePrefix,
            parts[1],
            EventAddressTextCodec.Format(completionBoundary),
            nthPrevious.ToString(
                System.Globalization.CultureInfo.InvariantCulture
            )
        );
        if (!string.Equals(value, canonical, StringComparison.Ordinal)) {
            return HandleParseResult.Invalid;
        }
        return string.Equals(
            parts[1],
            _lifetime.OwnerNonce,
            StringComparison.Ordinal
        ) ? HandleParseResult.Parsed : HandleParseResult.ForeignOwner;
    }

    private string ComputeSnapshotToken(
        TimelineHeadRef timelineHead,
        ControlHeadRef controlHead,
        RecapGridStoreIdentity storeIdentity,
        GridBuildRecipe recipe,
        FulfilledViewKey currentKey,
        RowViewDigest currentViewDigest,
        HistorySegmentDescriptor selectedDescriptor,
        RecapRowView selectedView
    ) {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256
        );
        AppendText("atelia.recap-grid.context-selection.v1");
        AppendText(_lifetime.OwnerNonce);
        AppendText(_repositoryPath);
        AppendText(_refId.ToHexString());
        AppendBytes(timelineHead.ToCanonicalBytes());
        AppendText(controlHead.InstanceId.Value);
        AppendText(controlHead.RefId.ToHexString());
        AppendText(controlHead.TimelineId.Value);
        AppendText(controlHead.Generation.ToString(
            System.Globalization.CultureInfo.InvariantCulture
        ));
        AppendText(controlHead.StateDigest.Value);
        AppendText(controlHead.ActiveRecipeDigest?.Value ?? string.Empty);
        AppendText(storeIdentity.InstanceId.Value);
        AppendText(storeIdentity.SchemaVersion.ToString(
            System.Globalization.CultureInfo.InvariantCulture
        ));
        AppendBytes(recipe.ToCanonicalBytes());
        AppendBytes(currentKey.ToCanonicalBytes());
        AppendText(currentViewDigest.Value);
        AppendBytes(selectedDescriptor.ToCanonicalBytes());
        AppendBytes(selectedView.ToCanonicalBytes());
        return Convert.ToHexStringLower(hash.GetHashAndReset());

        void AppendText(string value) => AppendBytes(
            Encoding.UTF8.GetBytes(value)
        );
        void AppendBytes(ReadOnlySpan<byte> value) {
            Span<byte> length = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
            hash.AppendData(length);
            hash.AppendData(value);
        }
    }

    private RecapGridContextResolveResult CompleteTerminal(
        RecapGridContextResolveResult result,
        EventAddress completionBoundary,
        TimelineHeadRef timelineHead,
        ControlHeadRef controlHead
    ) {
        _hooks.BeforeTerminalFence?.Invoke(result);
        return CheckFences(completionBoundary, timelineHead, controlHead)
            ?? result;
    }

    private static bool IsLowerHex(string value, int length) =>
        value.Length == length
        && value.All(static character => character is >= '0' and <= '9'
            or >= 'a' and <= 'f');

    private enum HandleParseResult {
        Invalid,
        ForeignOwner,
        Parsed
    }

    private static RecapGridContextResolveResult MapTimelineSnapshot(
        HistoryTimelineSnapshotResult result
    ) => result switch {
        HistoryTimelineSnapshotResult.Busy
            => new RecapGridContextResolveResult.Busy(
                RecapGridContextComponent.Timeline
            ),
        HistoryTimelineSnapshotResult.UnsupportedSchema schema
            => new RecapGridContextResolveResult.UnsupportedSchema(
                RecapGridContextComponent.Timeline,
                schema.SchemaVersion
            ),
        HistoryTimelineSnapshotResult.Invalid invalid
            when invalid.Code == "HistoryTimelineDisposed"
            => new RecapGridContextResolveResult.Disposed(
                RecapGridContextComponent.Timeline
            ),
        HistoryTimelineSnapshotResult.Invalid invalid
            => Invalid(
                RecapGridContextComponent.Timeline,
                invalid.Code,
                invalid.Detail
            ),
        _ => Invalid(
            RecapGridContextComponent.Timeline,
            "TimelineSnapshotOutcomeInvalid",
            "HistoryTimeline returned an unknown snapshot outcome."
        )
    };

    private static RecapGridContextResolveResult MapTimelineRow(
        HistoryTimelineReaderRowResult result
    ) => result switch {
        HistoryTimelineReaderRowResult.NotOnSelectedPath missing
            => new RecapGridContextResolveResult.NotOnSelectedPath(
                missing.RowId
            ),
        HistoryTimelineReaderRowResult.StaleTimelineHead
            => new RecapGridContextResolveResult.Stale(
                RecapGridContextComponent.Timeline,
                "The whole Timeline head changed."
            ),
        HistoryTimelineReaderRowResult.Busy
            => new RecapGridContextResolveResult.Busy(
                RecapGridContextComponent.Timeline
            ),
        HistoryTimelineReaderRowResult.Invalid invalid
            when invalid.Code == "HistoryTimelineDisposed"
            => new RecapGridContextResolveResult.Disposed(
                RecapGridContextComponent.Timeline
            ),
        HistoryTimelineReaderRowResult.Invalid invalid
            => Invalid(
                RecapGridContextComponent.Timeline,
                invalid.Code,
                invalid.Detail
            ),
        _ => Invalid(
            RecapGridContextComponent.Timeline,
            "TimelineRowOutcomeInvalid",
            "HistoryTimeline returned an unknown row outcome."
        )
    };

    private static RecapGridContextResolveResult MapControlSnapshot(
        RecapGridControlSnapshotResult result
    ) => result switch {
        RecapGridControlSnapshotResult.Busy
            => new RecapGridContextResolveResult.Busy(
                RecapGridContextComponent.Control
            ),
        RecapGridControlSnapshotResult.Disposed
            => new RecapGridContextResolveResult.Disposed(
                RecapGridContextComponent.Control
            ),
        RecapGridControlSnapshotResult.UnsupportedSchema schema
            => new RecapGridContextResolveResult.UnsupportedSchema(
                RecapGridContextComponent.Control,
                schema.SchemaVersion
            ),
        RecapGridControlSnapshotResult.Invalid invalid
            => Invalid(
                RecapGridContextComponent.Control,
                invalid.Code,
                invalid.Detail
            ),
        _ => Invalid(
            RecapGridContextComponent.Control,
            "ControlSnapshotOutcomeInvalid",
            "Control returned an unknown snapshot outcome."
        )
    };

    private static RecapGridContextResolveResult MapStoreOpen(
        GetterLifetime.StoreOpen result,
        FulfilledViewKey currentKey
    ) => result switch {
        GetterLifetime.StoreOpen.Absent
            => new RecapGridContextResolveResult.Unfulfilled(currentKey),
        GetterLifetime.StoreOpen.Busy
            => new RecapGridContextResolveResult.Busy(
                RecapGridContextComponent.Store
            ),
        GetterLifetime.StoreOpen.Disposed
            => new RecapGridContextResolveResult.Disposed(
                RecapGridContextComponent.Store
            ),
        GetterLifetime.StoreOpen.UnsupportedSchema schema
            => new RecapGridContextResolveResult.UnsupportedSchema(
                RecapGridContextComponent.Store,
                schema.SchemaVersion
            ),
        GetterLifetime.StoreOpen.Invalid invalid
            => Invalid(
                RecapGridContextComponent.Store,
                invalid.Code,
                invalid.Detail
            ),
        _ => Invalid(
            RecapGridContextComponent.Store,
            "StoreOpenOutcomeInvalid",
            "RecapGrid Store returned an unknown open outcome."
        )
    };

    private static RecapGridContextResolveResult MapStoreRead<T>(
        RecapGridStoreReadResult<T> result
    ) where T : class => result switch {
        RecapGridStoreReadResult<T>.Busy
            => new RecapGridContextResolveResult.Busy(
                RecapGridContextComponent.Store
            ),
        RecapGridStoreReadResult<T>.Disposed
            => new RecapGridContextResolveResult.Disposed(
                RecapGridContextComponent.Store
            ),
        RecapGridStoreReadResult<T>.Invalid invalid
            => Invalid(
                RecapGridContextComponent.Store,
                invalid.Code,
                invalid.Detail
            ),
        RecapGridStoreReadResult<T>.Missing
            => Invalid(
                RecapGridContextComponent.Store,
                "StoreArtifactMissing",
                "An exact selected Store artifact is missing."
            ),
        _ => Invalid(
            RecapGridContextComponent.Store,
            "StoreReadOutcomeInvalid",
            "RecapGrid Store returned an unknown read outcome."
        )
    };

    private static RecapGridContextResolveResult.Invalid Invalid(
        RecapGridContextComponent component,
        string code,
        string detail
    ) => new(component, code, detail);

    private static bool IsContractFailure(Exception exception)
        => exception is ArgumentException
            or InvalidDataException
            or InvalidOperationException
            or OverflowException;

    public void Dispose() => _lifetime.Dispose();
}
