using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;

namespace Atelia.SessionJournal.RecapGrid.Control;

public static class RecapGridControlFactory {
    public static RecapGridControlCreateResult Create(
        string repositoryPath,
        RefId refId,
        RecapGridControlAdmission admission
    ) => CreateForTest(
        repositoryPath,
        refId,
        admission,
        ControlPersistenceTestHooks.None
    );

    internal static RecapGridControlCreateResult CreateForTest(
        string repositoryPath,
        RefId refId,
        RecapGridControlAdmission admission,
        ControlPersistenceTestHooks hooks
    ) {
        if (admission is null) {
            return new RecapGridControlCreateResult.Invalid(
                "ControlAdmissionInvalid",
                "A Control admission policy is required."
            );
        }
        if (!admission.Allows(RecapGridControlPermission.Create)) {
            return new RecapGridControlCreateResult.Unauthorized("Create");
        }
        HistoryTimelineReaderOpenResult timelineOpened;
        try {
            timelineOpened = HistoryTimelineMaintenance.OpenReader(
                repositoryPath,
                refId
            );
        }
        catch (Exception exception) {
            return ControlError.Create(exception);
        }
        if (timelineOpened is not HistoryTimelineReaderOpenResult.Opened opened) {
            return timelineOpened switch {
                HistoryTimelineReaderOpenResult.Absent
                    => new RecapGridControlCreateResult.TimelineAbsent(),
                HistoryTimelineReaderOpenResult.Busy
                    => new RecapGridControlCreateResult.Busy(),
                HistoryTimelineReaderOpenResult.UnsupportedSchema schema
                    => new RecapGridControlCreateResult
                        .TimelineUnsupportedSchema(schema.SchemaVersion),
                HistoryTimelineReaderOpenResult.Invalid invalid
                    => new RecapGridControlCreateResult.Invalid(
                    invalid.Code,
                    invalid.Detail
                ),
                _ => new RecapGridControlCreateResult.Invalid(
                    "TimelineReaderOpenOutcomeInvalid",
                    "The Timeline reader returned an unknown outcome."
                )
            };
        }
        using (opened.Handle) {
            try {
                var paths = new ControlPaths(
                    repositoryPath,
                    refId,
                    opened.Handle.Locator.ActiveTimelineId
                );
                ControlDurableFiles.EnsureSlots(paths);
                using FileStream lifetime = ControlDurableFiles
                    .AcquireExclusiveLifetime(paths, create: false);
                using FileStream writer = ControlDurableFiles
                    .AcquireWriter(paths, create: false);
                if (ControlDurableFiles.StateExists(paths)) {
                    ControlState existing = ControlState.Decode(
                        ControlDurableFiles.ReadState(paths)
                    );
                    RequireScope(existing, paths);
                    return new RecapGridControlCreateResult
                        .AlreadyExists();
                }
                ControlState state = ControlState.CreateEmpty(
                    refId,
                    opened.Handle.Locator.ActiveTimelineId
                );
                try {
                    ControlDurableFiles.WriteState(
                        paths,
                        state.CanonicalBytes,
                        createNew: true,
                        hooks
                    );
                }
                catch (ControlStatePublishIndeterminateException) {
                    return new RecapGridControlCreateResult
                        .CommitIndeterminate(
                            state.Head,
                            ObserveHead(paths)
                        );
                }
                return new RecapGridControlCreateResult.Created(state.Head);
            }
            catch (ControlUnsupportedSchemaException schema) {
                return new RecapGridControlCreateResult
                    .ControlUnsupportedSchema(schema.Version);
            }
            catch (Exception exception) {
                return ControlError.Create(exception);
            }
        }
    }

    public static RecapGridControlOpenResult Open(
        string repositoryPath,
        RefId refId,
        RecapGridControlAdmission admission
    ) => OpenForTest(
        repositoryPath,
        refId,
        admission,
        ControlPersistenceTestHooks.None
    );

    internal static RecapGridControlOpenResult OpenForTest(
        string repositoryPath,
        RefId refId,
        RecapGridControlAdmission admission,
        ControlPersistenceTestHooks hooks
    ) {
        if (admission is null) {
            return new RecapGridControlOpenResult.Invalid(
                "ControlAdmissionInvalid",
                "A Control admission policy is required."
            );
        }
        OpenCoreResult core = OpenCore(repositoryPath, refId);
        return core switch {
            OpenCoreResult.Opened opened => new RecapGridControlOpenResult
                .Opened(new RecapGridControlHandle(
                    opened.Reader,
                    new RecapGridControlCoordinator(
                        opened.Paths,
                        opened.Reader,
                        opened.TimelineReader,
                        admission,
                        opened.Lifetime,
                        hooks
                    ),
                    opened.Lifetime
                )),
            OpenCoreResult.Absent => new RecapGridControlOpenResult.Absent(),
            OpenCoreResult.TimelineAbsent
                => new RecapGridControlOpenResult.TimelineAbsent(),
            OpenCoreResult.TimelineUnsupportedSchema schema
                => new RecapGridControlOpenResult
                    .TimelineUnsupportedSchema(schema.SchemaVersion),
            OpenCoreResult.Busy => new RecapGridControlOpenResult.Busy(),
            OpenCoreResult.UnsupportedSchema schema
                => new RecapGridControlOpenResult.UnsupportedSchema(
                    schema.SchemaVersion
                ),
            OpenCoreResult.Invalid invalid
                => new RecapGridControlOpenResult.Invalid(
                    invalid.Code,
                    invalid.Detail
                ),
            _ => new RecapGridControlOpenResult.Invalid(
                "ControlOpenOutcomeInvalid",
                "The Control factory returned an unknown outcome."
            )
        };
    }

    public static RecapGridControlReaderOpenResult OpenReader(
        string repositoryPath,
        RefId refId
    ) {
        OpenCoreResult core = OpenCore(repositoryPath, refId);
        return core switch {
            OpenCoreResult.Opened opened => new
                RecapGridControlReaderOpenResult.Opened(
                    new RecapGridControlReaderHandle(
                        opened.Reader,
                        opened.Lifetime
                    )
                ),
            OpenCoreResult.Absent
                => new RecapGridControlReaderOpenResult.Absent(),
            OpenCoreResult.TimelineAbsent
                => new RecapGridControlReaderOpenResult.TimelineAbsent(),
            OpenCoreResult.TimelineUnsupportedSchema schema
                => new RecapGridControlReaderOpenResult
                    .TimelineUnsupportedSchema(schema.SchemaVersion),
            OpenCoreResult.Busy
                => new RecapGridControlReaderOpenResult.Busy(),
            OpenCoreResult.UnsupportedSchema schema
                => new RecapGridControlReaderOpenResult.UnsupportedSchema(
                    schema.SchemaVersion
                ),
            OpenCoreResult.Invalid invalid
                => new RecapGridControlReaderOpenResult.Invalid(
                    invalid.Code,
                    invalid.Detail
                ),
            _ => new RecapGridControlReaderOpenResult.Invalid(
                "ControlReaderOpenOutcomeInvalid",
                "The Control reader factory returned an unknown outcome."
            )
        };
    }

    private static OpenCoreResult OpenCore(
        string repositoryPath,
        RefId refId
    ) {
        HistoryTimelineReaderHandle? timelineHandle = null;
        FileStream? controlLease = null;
        try {
            HistoryTimelineReaderOpenResult timelineOpened =
                HistoryTimelineMaintenance.OpenReader(repositoryPath, refId);
            switch (timelineOpened) {
                case HistoryTimelineReaderOpenResult.Absent:
                    return new OpenCoreResult.TimelineAbsent();
                case HistoryTimelineReaderOpenResult.Busy:
                    return new OpenCoreResult.Busy();
                case HistoryTimelineReaderOpenResult.UnsupportedSchema schema:
                    return new OpenCoreResult.TimelineUnsupportedSchema(
                        schema.SchemaVersion
                    );
                case HistoryTimelineReaderOpenResult.Invalid invalid:
                    return new OpenCoreResult.Invalid(
                        invalid.Code,
                        invalid.Detail
                    );
                case HistoryTimelineReaderOpenResult.Opened opened:
                    timelineHandle = opened.Handle;
                    break;
                default:
                    return new OpenCoreResult.Invalid(
                        "TimelineReaderOpenOutcomeInvalid",
                        "The Timeline reader returned an unknown outcome."
                    );
            }
            var paths = new ControlPaths(
                repositoryPath,
                refId,
                timelineHandle.Locator.ActiveTimelineId
            );
            if (!ControlDurableFiles.StateExists(paths)) {
                return new OpenCoreResult.Absent();
            }
            controlLease = ControlDurableFiles.AcquireSharedLifetime(paths);
            if (!ControlDurableFiles.StateExists(paths)) {
                return new OpenCoreResult.Absent();
            }
            ControlState state = ControlState.Decode(
                ControlDurableFiles.ReadState(paths)
            );
            RequireScope(state, paths);
            var lifetime = new ControlLifetime(
                controlLease,
                timelineHandle
            );
            var reader = new RecapGridControlReader(paths, lifetime);
            var result = new OpenCoreResult.Opened(
                paths,
                reader,
                timelineHandle.Reader,
                lifetime
            );
            controlLease = null;
            timelineHandle = null;
            return result;
        }
        catch (ControlUnsupportedSchemaException schema) {
            return new OpenCoreResult.UnsupportedSchema(schema.Version);
        }
        catch (ControlBusyException) {
            return new OpenCoreResult.Busy();
        }
        catch (Exception exception) {
            (string code, string detail) = ControlError.Invalid(exception);
            return new OpenCoreResult.Invalid(code, detail);
        }
        finally {
            controlLease?.Dispose();
            timelineHandle?.Dispose();
        }
    }

    internal static void RequireScope(ControlState state, ControlPaths paths) {
        if (state.Head.RefId != paths.RefId
            || state.Head.TimelineId != paths.TimelineId) {
            throw new ControlStoreException(
                "ControlScopeMismatch",
                "The Control state belongs to another Ref or Timeline."
            );
        }
    }

    internal static ControlHeadRef? ObserveHead(ControlPaths paths) {
        try {
            ControlState state = ControlState.Decode(
                ControlDurableFiles.ReadState(paths)
            );
            RequireScope(state, paths);
            return state.Head;
        }
        catch {
            return null;
        }
    }

    private abstract record OpenCoreResult {
        private OpenCoreResult() { }
        internal sealed record Opened(
            ControlPaths Paths,
            RecapGridControlReader Reader,
            HistoryTimelineReader TimelineReader,
            ControlLifetime Lifetime
        ) : OpenCoreResult;
        internal sealed record Absent : OpenCoreResult;
        internal sealed record TimelineAbsent : OpenCoreResult;
        internal sealed record TimelineUnsupportedSchema(int SchemaVersion)
            : OpenCoreResult;
        internal sealed record Busy : OpenCoreResult;
        internal sealed record UnsupportedSchema(int SchemaVersion)
            : OpenCoreResult;
        internal sealed record Invalid(string Code, string Detail)
            : OpenCoreResult;
    }
}

public sealed class RecapGridControlReader {
    private readonly ControlPaths _paths;
    private readonly ControlLifetime _lifetime;

    internal RecapGridControlReader(
        ControlPaths paths,
        ControlLifetime lifetime
    ) {
        _paths = paths;
        _lifetime = lifetime;
    }

    public RecapGridControlSnapshotResult ReadSnapshot() {
        using ControlLifetime.Operation? operation = _lifetime.TryEnter();
        if (operation is null) {
            return new RecapGridControlSnapshotResult.Disposed();
        }
        try {
            ControlState state = ControlState.Decode(
                ControlDurableFiles.ReadState(_paths)
            );
            RecapGridControlFactory.RequireScope(state, _paths);
            return new RecapGridControlSnapshotResult.Available(
                state.Snapshot()
            );
        }
        catch (ControlUnsupportedSchemaException schema) {
            return new RecapGridControlSnapshotResult.UnsupportedSchema(
                schema.Version
            );
        }
        catch (ControlBusyException) {
            return new RecapGridControlSnapshotResult.Busy();
        }
        catch (Exception exception) {
            (string code, string detail) = ControlError.Invalid(exception);
            return new RecapGridControlSnapshotResult.Invalid(code, detail);
        }
    }
}

public sealed class RecapGridControlCoordinator {
    private readonly ControlPaths _paths;
    private readonly RecapGridControlReader _reader;
    private readonly HistoryTimelineReader _timelineReader;
    private readonly RecapGridControlAdmission _admission;
    private readonly ControlLifetime _lifetime;
    private readonly ControlPersistenceTestHooks _hooks;

    internal RecapGridControlCoordinator(
        ControlPaths paths,
        RecapGridControlReader reader,
        HistoryTimelineReader timelineReader,
        RecapGridControlAdmission admission,
        ControlLifetime lifetime,
        ControlPersistenceTestHooks hooks
    ) {
        _paths = paths;
        _reader = reader;
        _timelineReader = timelineReader;
        _admission = admission;
        _lifetime = lifetime;
        _hooks = hooks;
    }

    public RecapGridControlPutResult PutFamilyDefinition(
        ControlHeadRef expectedWholeHead,
        FamilyDefinition family
    ) => Put(
        expectedWholeHead,
        RecapGridControlPermission.RegisterFamily,
        state => ValidateFamily(state, family),
        state => state.WithFamily(family)
    );

    public RecapGridControlPutResult PutMaintainerDefinition(
        ControlHeadRef expectedWholeHead,
        MaintainerDefinitionRevision definition
    ) => Put(
        expectedWholeHead,
        RecapGridControlPermission.RegisterDefinition,
        state => ValidateDefinition(state, definition),
        state => state.WithDefinition(definition)
    );

    public RecapGridControlPutResult PutBuildRecipe(
        ControlHeadRef expectedWholeControlHead,
        TimelineHeadRef expectedWholeTimelineHead,
        GridBuildRecipe recipe,
        HistoryTimelineAncestorWitness? bootstrapWitness
    ) {
        ArgumentNullException.ThrowIfNull(expectedWholeControlHead);
        ArgumentNullException.ThrowIfNull(expectedWholeTimelineHead);
        ArgumentNullException.ThrowIfNull(recipe);
        using ControlLifetime.Operation? operation = _lifetime.TryEnter();
        if (operation is null) {
            return new RecapGridControlPutResult.Disposed();
        }
        try {
            using FileStream writer = ControlDurableFiles.AcquireWriter(
                _paths,
                create: false
            );
            ControlState state = ReadCurrent();
            if (state.Head != expectedWholeControlHead) {
                return new RecapGridControlPutResult.StaleControlHead(
                    state.Head
                );
            }
            if (!_admission.Allows(
                    RecapGridControlPermission.RegisterRecipe)) {
                return new RecapGridControlPutResult.Unauthorized(
                    "RegisterRecipe"
                );
            }
            if (recipe.TimelineId != _paths.TimelineId) {
                return new RecapGridControlPutResult.Unauthorized(
                    "RecipeTimelineScope"
                );
            }
            RecapGridControlPutResult? timelineFailure =
                ValidateTimelineHead(expectedWholeTimelineHead);
            if (timelineFailure is not null) {
                return timelineFailure;
            }
            RecapGridControlPutResult? recipeFailure = ValidateRecipe(
                state,
                expectedWholeTimelineHead,
                recipe,
                bootstrapWitness
            );
            if (recipeFailure is not null) {
                return recipeFailure;
            }
            if (state.Recipes.TryGetValue(
                    recipe.Digest.Value,
                    out RegisteredGridRecipe? existing)) {
                if (!existing.Recipe.ToCanonicalBytes().SequenceEqual(
                        recipe.ToCanonicalBytes())) {
                    return new RecapGridControlPutResult.Invalid(
                        "ControlDigestCollision",
                        "A recipe digest maps to different canonical bytes."
                    );
                }
                return new RecapGridControlPutResult.AlreadyPresent(
                    state.Head
                );
            }
            HistorySegmentDescriptorDigest? descriptorDigest =
                bootstrapWitness?.DescriptorDigest;
            var registered = new RegisteredGridRecipe(
                recipe,
                new RegisteredRecipeBootstrap(
                    expectedWholeTimelineHead,
                    recipe.BootstrapThroughRowId,
                    descriptorDigest
                )
            );
            ControlState next = state.WithRecipe(registered);
            try {
                ControlDurableFiles.WriteState(
                    _paths,
                    next.CanonicalBytes,
                    createNew: false,
                    _hooks
                );
            }
            catch (ControlStatePublishIndeterminateException) {
                return new RecapGridControlPutResult.CommitIndeterminate(
                    next.Head,
                    RecapGridControlFactory.ObserveHead(_paths)
                );
            }
            return new RecapGridControlPutResult.Stored(next.Head);
        }
        catch (Exception exception) {
            return ControlError.Put(exception);
        }
    }

    public RecapGridControlActivateResult CompareExchangeActiveRecipe(
        ControlHeadRef expectedWholeControlHead,
        TimelineHeadRef expectedWholeTimelineHead,
        GridBuildRecipeDigest? nextRecipeDigest,
        RecapGridControlActivationPurpose purpose
    ) {
        ArgumentNullException.ThrowIfNull(expectedWholeControlHead);
        ArgumentNullException.ThrowIfNull(expectedWholeTimelineHead);
        using ControlLifetime.Operation? operation = _lifetime.TryEnter();
        if (operation is null) {
            return new RecapGridControlActivateResult.Disposed();
        }
        try {
            using FileStream writer = ControlDurableFiles.AcquireWriter(
                _paths,
                create: false
            );
            ControlState state = ReadCurrent();
            if (state.Head != expectedWholeControlHead) {
                return new RecapGridControlActivateResult.StaleControlHead(
                    state.Head
                );
            }
            RecapGridControlPermission requiredPermission = purpose switch {
                RecapGridControlActivationPurpose.Direct
                    => RecapGridControlPermission.Activate,
                RecapGridControlActivationPurpose.Promotion
                    => RecapGridControlPermission.Promote,
                _ => throw new ArgumentOutOfRangeException(nameof(purpose))
            };
            if (!_admission.Allows(requiredPermission)) {
                return new RecapGridControlActivateResult.Unauthorized(
                    requiredPermission.ToString()
                );
            }
            if (purpose == RecapGridControlActivationPurpose.Promotion
                && nextRecipeDigest is null) {
                return new RecapGridControlActivateResult.Invalid(
                    "PromotionCannotDeactivate",
                    "Promotion must select a concrete recipe."
                );
            }
            RecapGridControlActivateResult? timelineFailure =
                ValidateTimelineForActivation(expectedWholeTimelineHead);
            if (timelineFailure is not null) {
                return timelineFailure;
            }
            if (nextRecipeDigest is null) {
                if (state.Head.ActiveRecipeDigest is null) {
                    return new RecapGridControlActivateResult.AlreadyActive(
                        state.Head
                    );
                }
                ControlState deactivated = state.WithActive(null);
                try {
                    ControlDurableFiles.WriteState(
                        _paths,
                        deactivated.CanonicalBytes,
                        createNew: false,
                        _hooks
                    );
                }
                catch (ControlStatePublishIndeterminateException) {
                    return new RecapGridControlActivateResult
                        .CommitIndeterminate(
                            deactivated.Head,
                            RecapGridControlFactory.ObserveHead(_paths)
                        );
                }
                return new RecapGridControlActivateResult.Applied(
                    deactivated.Head
                );
            }
            if (!state.Recipes.TryGetValue(
                    nextRecipeDigest.Value.Value,
                    out RegisteredGridRecipe? registered)) {
                return new RecapGridControlActivateResult.RecipeAbsent(
                    nextRecipeDigest.Value
                );
            }
            if (registered.Recipe.Target.OrderedColumns.Count == 0) {
                return new RecapGridControlActivateResult.Unauthorized(
                    "ActiveRecipeTargetEmpty"
                );
            }
            RecapGridControlActivateResult? contextFailure =
                ValidateContextComposable(state, registered.Recipe);
            if (contextFailure is not null) {
                return contextFailure;
            }
            RecapGridControlActivateResult? admissionFailure =
                ValidateStoredRecipeAdmission(
                    state,
                    expectedWholeTimelineHead,
                    registered
                );
            if (admissionFailure is not null) {
                return admissionFailure;
            }
            if (state.Head.ActiveRecipeDigest == nextRecipeDigest) {
                return new RecapGridControlActivateResult.AlreadyActive(
                    state.Head
                );
            }
            ControlState next = state.WithActive(nextRecipeDigest);
            try {
                ControlDurableFiles.WriteState(
                    _paths,
                    next.CanonicalBytes,
                    createNew: false,
                    _hooks
                );
            }
            catch (ControlStatePublishIndeterminateException) {
                return new RecapGridControlActivateResult
                    .CommitIndeterminate(
                        next.Head,
                        RecapGridControlFactory.ObserveHead(_paths)
                    );
            }
            return new RecapGridControlActivateResult.Applied(next.Head);
        }
        catch (Exception exception) {
            return ControlError.Activate(exception);
        }
    }

    /// <summary>
    /// Atomically registers an exact bundle and its terminal durable operation
    /// receipt. This is the only Control entry point used by the Agent-facing
    /// recoverable tool.
    /// </summary>
    public RecapGridControlOperationResult ApplyRegistrationBundle(
        ControlHeadRef expectedWholeControlHead,
        TimelineHeadRef expectedWholeTimelineHead,
        RecapGridControlOperation durableOperation,
        RecapGridControlRegistrationBundle bundle
    ) {
        ArgumentNullException.ThrowIfNull(expectedWholeControlHead);
        ArgumentNullException.ThrowIfNull(expectedWholeTimelineHead);
        ArgumentNullException.ThrowIfNull(durableOperation);
        ArgumentNullException.ThrowIfNull(bundle);
        string commandDigest = ControlOperationCanonicalizer
            .RegistrationDigest(
                bundle
            );
        using ControlLifetime.Operation? lifetimeOperation =
            _lifetime.TryEnter();
        if (lifetimeOperation is null) {
            return new RecapGridControlOperationResult.Disposed();
        }
        try {
            using FileStream writer = ControlDurableFiles.AcquireWriter(
                _paths,
                create: false
            );
            ControlState state = ReadCurrent();
            RecapGridControlOperationResult? replay = TryReplay(
                state,
                durableOperation,
                commandDigest
            );
            if (replay is not null) {
                return replay;
            }
            if (state.Head != expectedWholeControlHead) {
                return new RecapGridControlOperationResult.StaleControlHead(
                    state.Head
                );
            }
            RecapGridControlOperationResult? permission =
                ValidateBundlePermissions(bundle);
            if (permission is not null) {
                return permission;
            }
            RecapGridControlOperationResult? timeline = MapOperation(
                ValidateTimelineHead(expectedWholeTimelineHead)
            );
            if (timeline is not null) {
                return timeline;
            }

            ControlState working = state;
            foreach (FamilyDefinition family in bundle.Families) {
                RecapGridControlPutResult? validation = ValidateFamily(
                    working,
                    family
                );
                if (validation is RecapGridControlPutResult.AlreadyPresent) {
                    continue;
                }
                if (validation is not null) {
                    return MapOperation(validation)!;
                }
                working = working.WithFamily(family);
            }
            foreach (MaintainerDefinitionRevision definition
                     in bundle.Definitions) {
                RecapGridControlPutResult? validation = ValidateDefinition(
                    working,
                    definition
                );
                if (validation is RecapGridControlPutResult.AlreadyPresent) {
                    continue;
                }
                if (validation is not null) {
                    return MapOperation(validation)!;
                }
                working = working.WithDefinition(definition);
            }
            foreach (RecapGridControlRecipeRegistration registration
                     in bundle.Recipes) {
                GridBuildRecipe recipe = registration.Recipe;
                if (recipe.TimelineId != _paths.TimelineId) {
                    return new RecapGridControlOperationResult.Unauthorized(
                        "RecipeTimelineScope"
                    );
                }
                RecapGridControlPutResult? validation = ValidateRecipe(
                    working,
                    expectedWholeTimelineHead,
                    recipe,
                    registration.BootstrapWitness
                );
                if (validation is not null) {
                    return MapOperation(validation)!;
                }
                if (working.Recipes.TryGetValue(
                        recipe.Digest.Value,
                        out RegisteredGridRecipe? existing)) {
                    if (!existing.Recipe.ToCanonicalBytes().SequenceEqual(
                            recipe.ToCanonicalBytes())) {
                        return new RecapGridControlOperationResult.Invalid(
                            "ControlDigestCollision",
                            "A recipe digest maps to different canonical bytes."
                        );
                    }
                    continue;
                }
                working = working.WithRecipe(new RegisteredGridRecipe(
                    recipe,
                    new RegisteredRecipeBootstrap(
                        expectedWholeTimelineHead,
                        recipe.BootstrapThroughRowId,
                        registration.BootstrapWitness?.DescriptorDigest
                    )
                ));
            }
            return PublishTerminalOperation(
                state,
                working,
                durableOperation,
                commandDigest,
                "registration"
            );
        }
        catch (Exception exception) when (!ControlError.IsFatal(exception)) {
            return ControlError.Operation(exception);
        }
    }

    /// <summary>
    /// Atomically promotes one exact recipe and records the durable tool
    /// operation. The caller must independently possess a fresh non-durable
    /// Manager proof; Control still revalidates its own whole heads/admission.
    /// </summary>
    public RecapGridControlOperationResult CompareExchangeAgentPromotion(
        ControlHeadRef expectedWholeControlHead,
        TimelineHeadRef expectedWholeTimelineHead,
        GridBuildRecipeDigest recipeDigest,
        RecapGridControlOperation durableOperation
    ) {
        ArgumentNullException.ThrowIfNull(expectedWholeControlHead);
        ArgumentNullException.ThrowIfNull(expectedWholeTimelineHead);
        ArgumentNullException.ThrowIfNull(durableOperation);
        if (recipeDigest.Value is null) {
            throw new ArgumentException(
                "Recipe digest must not be default.",
                nameof(recipeDigest)
            );
        }
        string commandDigest = ControlOperationCanonicalizer.PromotionDigest(
            recipeDigest
        );
        using ControlLifetime.Operation? lifetimeOperation =
            _lifetime.TryEnter();
        if (lifetimeOperation is null) {
            return new RecapGridControlOperationResult.Disposed();
        }
        try {
            using FileStream writer = ControlDurableFiles.AcquireWriter(
                _paths,
                create: false
            );
            ControlState state = ReadCurrent();
            RecapGridControlOperationResult? replay = TryReplay(
                state,
                durableOperation,
                commandDigest
            );
            if (replay is not null) {
                return replay;
            }
            if (state.Head != expectedWholeControlHead) {
                return new RecapGridControlOperationResult.StaleControlHead(
                    state.Head
                );
            }
            if (!_admission.Allows(RecapGridControlPermission.Promote)) {
                return new RecapGridControlOperationResult.Unauthorized(
                    "Promote"
                );
            }
            RecapGridControlOperationResult? timeline = MapOperation(
                ValidateTimelineForActivation(expectedWholeTimelineHead)
            );
            if (timeline is not null) {
                return timeline;
            }
            if (!state.Recipes.TryGetValue(
                    recipeDigest.Value,
                    out RegisteredGridRecipe? registered)) {
                return new RecapGridControlOperationResult.RecipeAbsent(
                    recipeDigest
                );
            }
            if (registered.Recipe.Target.OrderedColumns.Count == 0) {
                return new RecapGridControlOperationResult.Unauthorized(
                    "ActiveRecipeTargetEmpty"
                );
            }
            RecapGridControlOperationResult? context = MapOperation(
                ValidateContextComposable(state, registered.Recipe)
            );
            if (context is not null) {
                return context;
            }
            RecapGridControlOperationResult? admission = MapOperation(
                ValidateStoredRecipeAdmission(
                    state,
                    expectedWholeTimelineHead,
                    registered
                )
            );
            if (admission is not null) {
                return admission;
            }
            ControlState working = state.Head.ActiveRecipeDigest
                    == recipeDigest
                ? state
                : state.WithActive(recipeDigest);
            return PublishTerminalOperation(
                state,
                working,
                durableOperation,
                commandDigest,
                "promotion"
            );
        }
        catch (Exception exception) when (!ControlError.IsFatal(exception)) {
            return ControlError.Operation(exception);
        }
    }

    private RecapGridControlOperationResult PublishTerminalOperation(
        ControlState original,
        ControlState semanticState,
        RecapGridControlOperation operation,
        string commandDigest,
        string terminalKind
    ) {
        long generation = checked(original.Head.Generation + 1);
        string resultIdentity = ControlOperationCanonicalizer.ResultIdentity(
            commandDigest,
            terminalKind
        );
        var receipt = new ControlOperationReceipt(
            operation.OperationKey,
            operation.ExecutionSequence,
            operation.RuntimeIdentityDigest,
            commandDigest,
            resultIdentity,
            original.Head.InstanceId,
            generation
        );
        ControlState next = semanticState.WithTerminalOperation(
            receipt,
            generation
        );
        try {
            ControlDurableFiles.WriteState(
                _paths,
                next.CanonicalBytes,
                createNew: false,
                _hooks
            );
            return new RecapGridControlOperationResult.Applied(
                next.Head,
                resultIdentity
            );
        }
        catch (ControlStatePublishIndeterminateException exception) {
            ControlError.ThrowIfFatal(exception);
            ControlState? observed = TryReadCurrent();
            if (observed is not null) {
                RecapGridControlOperationResult? settled = TryReplay(
                    observed,
                    operation,
                    commandDigest
                );
                if (settled is RecapGridControlOperationResult.Replayed replay) {
                    return new RecapGridControlOperationResult.Applied(
                        replay.CurrentHead,
                        replay.ResultIdentity
                    );
                }
                if (settled is RecapGridControlOperationResult.Conflict) {
                    return settled;
                }
            }
            return new RecapGridControlOperationResult.CommitIndeterminate(
                operation.OperationKey,
                next.Head,
                observed?.Head
            );
        }
    }

    private static RecapGridControlOperationResult? TryReplay(
        ControlState state,
        RecapGridControlOperation operation,
        string commandDigest
    ) {
        if (!state.TryGetOperationReceipt(
                operation.OperationKey,
                out ControlOperationReceipt? receipt)) {
            return null;
        }
        if (receipt is null
            || receipt.ExecutionSequence != operation.ExecutionSequence
            || !string.Equals(
                receipt.RuntimeIdentityDigest,
                operation.RuntimeIdentityDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                receipt.CommandDigest,
                commandDigest,
                StringComparison.Ordinal)) {
            return new RecapGridControlOperationResult.Conflict(
                operation.OperationKey
            );
        }
        bool instanceReplaced = state.Head.InstanceId
            != receipt.OriginalInstanceId;
        bool headAdvancedSinceApply = state.Head.Generation
                != receipt.OriginalGeneration
            || instanceReplaced;
        return new RecapGridControlOperationResult.Replayed(
            state.Head,
            receipt.OriginalInstanceId,
            receipt.OriginalGeneration,
            receipt.ResultIdentity,
            headAdvancedSinceApply,
            instanceReplaced
        );
    }

    private RecapGridControlOperationResult? ValidateBundlePermissions(
        RecapGridControlRegistrationBundle bundle
    ) {
        if (bundle.Families.Count != 0
            && !_admission.Allows(
                RecapGridControlPermission.RegisterFamily)) {
            return new RecapGridControlOperationResult.Unauthorized(
                "RegisterFamily"
            );
        }
        if (bundle.Definitions.Count != 0
            && !_admission.Allows(
                RecapGridControlPermission.RegisterDefinition)) {
            return new RecapGridControlOperationResult.Unauthorized(
                "RegisterDefinition"
            );
        }
        if (bundle.Recipes.Count != 0
            && !_admission.Allows(
                RecapGridControlPermission.RegisterRecipe)) {
            return new RecapGridControlOperationResult.Unauthorized(
                "RegisterRecipe"
            );
        }
        return null;
    }

    private ControlState? TryReadCurrent() {
        try {
            return ReadCurrent();
        }
        catch (Exception exception) when (!ControlError.IsFatal(exception)) {
            return null;
        }
    }

    private static RecapGridControlOperationResult? MapOperation(
        RecapGridControlPutResult? result
    ) => result switch {
        null => null,
        RecapGridControlPutResult.Unauthorized value
            => new RecapGridControlOperationResult.Unauthorized(value.Rule),
        RecapGridControlPutResult.StaleControlHead value
            => new RecapGridControlOperationResult.StaleControlHead(
                value.Actual),
        RecapGridControlPutResult.StaleTimelineHead value
            => new RecapGridControlOperationResult.StaleTimelineHead(
                value.Actual),
        RecapGridControlPutResult.NotOnSelectedPath value
            => new RecapGridControlOperationResult.NotOnSelectedPath(
                value.RowId),
        RecapGridControlPutResult.Busy
            => new RecapGridControlOperationResult.Busy(),
        RecapGridControlPutResult.TimelineUnsupportedSchema value
            => new RecapGridControlOperationResult.TimelineUnsupportedSchema(
                value.SchemaVersion),
        RecapGridControlPutResult.Disposed
            => new RecapGridControlOperationResult.Disposed(),
        RecapGridControlPutResult.LimitExceeded value
            => new RecapGridControlOperationResult.LimitExceeded(value.Limit),
        RecapGridControlPutResult.Invalid value
            => new RecapGridControlOperationResult.Invalid(
                value.Code, value.Detail),
        RecapGridControlPutResult.AlreadyPresent => null,
        _ => new RecapGridControlOperationResult.Invalid(
            "ControlOperationOutcomeInvalid",
            "Control returned an unexpected registration outcome."
        )
    };

    private static RecapGridControlOperationResult? MapOperation(
        RecapGridControlActivateResult? result
    ) => result switch {
        null => null,
        RecapGridControlActivateResult.Unauthorized value
            => new RecapGridControlOperationResult.Unauthorized(value.Rule),
        RecapGridControlActivateResult.RecipeAbsent value
            => new RecapGridControlOperationResult.RecipeAbsent(
                value.RecipeDigest),
        RecapGridControlActivateResult.StaleControlHead value
            => new RecapGridControlOperationResult.StaleControlHead(
                value.Actual),
        RecapGridControlActivateResult.StaleTimelineHead value
            => new RecapGridControlOperationResult.StaleTimelineHead(
                value.Actual),
        RecapGridControlActivateResult.BootstrapNotSelected value
            => new RecapGridControlOperationResult.NotOnSelectedPath(
                value.RowId),
        RecapGridControlActivateResult.Busy
            => new RecapGridControlOperationResult.Busy(),
        RecapGridControlActivateResult.TimelineUnsupportedSchema value
            => new RecapGridControlOperationResult.TimelineUnsupportedSchema(
                value.SchemaVersion),
        RecapGridControlActivateResult.Disposed
            => new RecapGridControlOperationResult.Disposed(),
        RecapGridControlActivateResult.Invalid value
            => new RecapGridControlOperationResult.Invalid(
                value.Code, value.Detail),
        RecapGridControlActivateResult.AlreadyActive => null,
        _ => new RecapGridControlOperationResult.Invalid(
            "ControlOperationOutcomeInvalid",
            "Control returned an unexpected promotion outcome."
        )
    };

    private RecapGridControlPutResult Put(
        ControlHeadRef expectedWholeHead,
        RecapGridControlPermission permission,
        Func<ControlState, RecapGridControlPutResult?> validate,
        Func<ControlState, ControlState> mutate
    ) {
        ArgumentNullException.ThrowIfNull(expectedWholeHead);
        using ControlLifetime.Operation? operation = _lifetime.TryEnter();
        if (operation is null) {
            return new RecapGridControlPutResult.Disposed();
        }
        try {
            using FileStream writer = ControlDurableFiles.AcquireWriter(
                _paths,
                create: false
            );
            ControlState state = ReadCurrent();
            if (state.Head != expectedWholeHead) {
                return new RecapGridControlPutResult.StaleControlHead(
                    state.Head
                );
            }
            if (!_admission.Allows(permission)) {
                return new RecapGridControlPutResult.Unauthorized(
                    permission.ToString()
                );
            }
            RecapGridControlPutResult? failure = validate(state);
            if (failure is not null) {
                return failure;
            }
            ControlState next = mutate(state);
            try {
                ControlDurableFiles.WriteState(
                    _paths,
                    next.CanonicalBytes,
                    createNew: false,
                    _hooks
                );
            }
            catch (ControlStatePublishIndeterminateException) {
                return new RecapGridControlPutResult.CommitIndeterminate(
                    next.Head,
                    RecapGridControlFactory.ObserveHead(_paths)
                );
            }
            return new RecapGridControlPutResult.Stored(next.Head);
        }
        catch (Exception exception) {
            return ControlError.Put(exception);
        }
    }

    private RecapGridControlPutResult? ValidateFamily(
        ControlState state,
        FamilyDefinition family
    ) {
        ArgumentNullException.ThrowIfNull(family);
        if (!_admission.AllowsFamily(family.Digest)) {
            return new RecapGridControlPutResult.Unauthorized(
                "FamilyAllowlist"
            );
        }
        if (state.Families.TryGetValue(
                family.Digest.Value,
                out FamilyDefinition? existing)) {
            return existing.ToCanonicalBytes().SequenceEqual(
                    family.ToCanonicalBytes())
                ? new RecapGridControlPutResult.AlreadyPresent(state.Head)
                : new RecapGridControlPutResult.Invalid(
                    "ControlDigestCollision",
                    "A family digest maps to different canonical bytes."
                );
        }
        return null;
    }

    private RecapGridControlPutResult? ValidateDefinition(
        ControlState state,
        MaintainerDefinitionRevision definition
    ) {
        ArgumentNullException.ThrowIfNull(definition);
        if (!_admission.AllowsFamily(definition.FamilyDigest)
            || !_admission.AllowsCapability(
                definition.Capability.CapabilityFingerprint)
            || !_admission.AllowsTarget(definition.Target.Carrier)
            || !_admission.AllowsColumn(definition.LogicalColumnId)) {
            return new RecapGridControlPutResult.Unauthorized(
                "DefinitionAdmission"
            );
        }
        if (!state.Families.ContainsKey(definition.FamilyDigest.Value)) {
            return new RecapGridControlPutResult.Unauthorized(
                "DefinitionFamilyAbsent"
            );
        }
        if (state.Definitions.TryGetValue(
                definition.Digest.Value,
                out MaintainerDefinitionRevision? existing)) {
            return existing.ToCanonicalBytes().SequenceEqual(
                    definition.ToCanonicalBytes())
                ? new RecapGridControlPutResult.AlreadyPresent(state.Head)
                : new RecapGridControlPutResult.Invalid(
                    "ControlDigestCollision",
                    "A definition digest maps to different canonical bytes."
                );
        }
        return null;
    }

    private RecapGridControlActivateResult? ValidateStoredRecipeAdmission(
        ControlState state,
        TimelineHeadRef expectedTimelineHead,
        RegisteredGridRecipe active
    ) {
        var closure = new List<GridBuildRecipe>();
        RegisteredGridRecipe current = active;
        int depth = 0;
        while (true) {
            foreach (BuildTargetColumn column
                     in current.Recipe.Target.OrderedColumns) {
                if (!DefinitionIsAdmitted(state, column)) {
                    return new RecapGridControlActivateResult.Unauthorized(
                        "RecipeClosureAdmission"
                    );
                }
            }
            RecapGridControlActivateResult? bootstrap =
                ValidateRegisteredBootstrap(
                    expectedTimelineHead,
                    current
                );
            if (bootstrap is not null) {
                return bootstrap;
            }
            closure.Add(current.Recipe);
            if (current.Recipe.BaseRecipeDigest is not { } baseDigest) {
                return MapBudgetForActivation(
                    ValidateClosureBudget(expectedTimelineHead, closure)
                );
            }
            if (++depth > ControlStorageLimits.MaximumRecipeBaseDepth
                || !state.Recipes.TryGetValue(
                    baseDigest.Value,
                    out RegisteredGridRecipe? next)) {
                return new RecapGridControlActivateResult.Invalid(
                    "RecipeClosureInvalid",
                    "The recipe closure is absent or too deep."
                );
            }
            current = next;
        }
    }

    private RecapGridControlPutResult? ValidateRecipe(
        ControlState state,
        TimelineHeadRef expectedTimelineHead,
        GridBuildRecipe recipe,
        HistoryTimelineAncestorWitness? witness
    ) {
        GridBuildRecipe? baseRecipe = recipe.BaseRecipeDigest is { } digest
            && state.Recipes.TryGetValue(
                digest.Value,
                out RegisteredGridRecipe? found)
                ? found.Recipe
                : null;
        try {
            recipe.ValidateBase(baseRecipe);
        }
        catch (ArgumentException exception) {
            return new RecapGridControlPutResult.Invalid(
                "RecipeGraphInvalid",
                exception.Message
            );
        }
        var closure = new List<GridBuildRecipe> { recipe };
        foreach (BuildTargetColumn column in recipe.Target.OrderedColumns) {
            if (!DefinitionIsAdmitted(state, column)) {
                return new RecapGridControlPutResult.Unauthorized(
                    "RecipeClosureAdmission"
                );
            }
        }
        if (recipe.BootstrapThroughRowId is null) {
            if (witness is not null || expectedTimelineHead.HeadRowId is not null) {
                return new RecapGridControlPutResult.Invalid(
                    "EmptyBootstrapRequiresEmptyTimeline",
                    "A null bootstrap may be registered only at an empty Timeline head."
                );
            }
        }
        else {
            if (witness is null
                || witness.RowId != recipe.BootstrapThroughRowId
                || expectedTimelineHead.RefId != _paths.RefId
                || expectedTimelineHead.TimelineId != _paths.TimelineId) {
                return new RecapGridControlPutResult.Invalid(
                    "BootstrapWitnessRequired",
                    "The recipe bootstrap requires its exact selected-row witness."
                );
            }
            HistoryTimelineReaderRowResult validated =
                _timelineReader.ValidateWitness(expectedTimelineHead, witness);
            switch (validated) {
                case HistoryTimelineReaderRowResult.Selected selected
                    when selected.Row.Descriptor.DescriptorDigest
                        == witness.DescriptorDigest:
                    break;
                case HistoryTimelineReaderRowResult.NotOnSelectedPath missing:
                    return new RecapGridControlPutResult.NotOnSelectedPath(
                        missing.RowId
                    );
                case HistoryTimelineReaderRowResult.StaleTimelineHead stale:
                    return new RecapGridControlPutResult.StaleTimelineHead(
                        stale.Actual
                    );
                case HistoryTimelineReaderRowResult.Busy:
                    return new RecapGridControlPutResult.Busy();
                case HistoryTimelineReaderRowResult.Invalid invalid:
                    return new RecapGridControlPutResult.Invalid(
                        invalid.Code,
                        invalid.Detail
                    );
                default:
                    return new RecapGridControlPutResult.Invalid(
                        "BootstrapWitnessOutcomeInvalid",
                        "The Timeline reader returned an unknown witness outcome."
                    );
            }
        }

        GridBuildRecipe current = recipe;
        int depth = 0;
        while (current.BaseRecipeDigest is { } baseDigest) {
            if (++depth > ControlStorageLimits.MaximumRecipeBaseDepth
                || !state.Recipes.TryGetValue(
                    baseDigest.Value,
                    out RegisteredGridRecipe? registered)) {
                return new RecapGridControlPutResult.Invalid(
                    "RecipeClosureInvalid",
                    "The recipe closure is absent or too deep."
                );
            }
            foreach (BuildTargetColumn column
                     in registered.Recipe.Target.OrderedColumns) {
                if (!DefinitionIsAdmitted(state, column)) {
                    return new RecapGridControlPutResult.Unauthorized(
                        "RecipeClosureAdmission"
                    );
                }
            }
            RecapGridControlPutResult? bootstrap =
                ValidateRegisteredBootstrapForPut(
                    expectedTimelineHead,
                    registered
                );
            if (bootstrap is not null) {
                return bootstrap;
            }
            closure.Add(registered.Recipe);
            current = registered.Recipe;
        }
        return ValidateClosureBudget(expectedTimelineHead, closure);
    }

    private RecapGridControlPutResult? ValidateClosureBudget(
        TimelineHeadRef expectedTimelineHead,
        IReadOnlyList<GridBuildRecipe> closure
    ) {
        HistoryRowId[] required = [.. closure
            .Select(static recipe => recipe.BootstrapThroughRowId)
            .Where(static rowId => rowId is not null)
            .Select(static rowId => rowId!.Value)
            .Distinct()];
        var newestIndexes = new Dictionary<HistoryRowId, int>();
        int totalRows = 0;
        HistoryTimelinePathCursor? cursor = null;
        while (required.Length != 0) {
            HistoryTimelinePathPageResult pageResult =
                _timelineReader.ReadSelectedPathPage(
                    expectedTimelineHead,
                    cursor
                );
            switch (pageResult) {
                case HistoryTimelinePathPageResult.Page page:
                    foreach (HistoryTimelineSelectedRow row
                             in page.Value.Rows) {
                        if (required.Contains(row.Descriptor.RowId)
                            && !newestIndexes.ContainsKey(
                                row.Descriptor.RowId)) {
                            newestIndexes.Add(
                                row.Descriptor.RowId,
                                totalRows
                            );
                        }
                        totalRows = checked(totalRows + 1);
                    }
                    cursor = page.Value.Next;
                    break;
                case HistoryTimelinePathPageResult.StaleTimelineHead stale:
                    return new RecapGridControlPutResult.StaleTimelineHead(
                        stale.Actual
                    );
                case HistoryTimelinePathPageResult.Busy:
                    return new RecapGridControlPutResult.Busy();
                case HistoryTimelinePathPageResult.Invalid invalid:
                    return new RecapGridControlPutResult.Invalid(
                        invalid.Code,
                        invalid.Detail
                    );
                default:
                    return new RecapGridControlPutResult.Invalid(
                        "BootstrapPathOutcomeInvalid",
                        "The Timeline reader returned an unknown path-page outcome."
                    );
            }
            if (cursor is null) {
                break;
            }
        }
        HistoryRowId? missing = null;
        foreach (HistoryRowId rowId in required) {
            if (!newestIndexes.ContainsKey(rowId)) {
                missing = rowId;
                break;
            }
        }
        if (missing is { } missingRow) {
            return new RecapGridControlPutResult.NotOnSelectedPath(
                missingRow
            );
        }
        long projectedCalls = 0;
        foreach (GridBuildRecipe recipe in closure) {
            int rows = recipe.BootstrapThroughRowId is { } rowId
                ? checked(totalRows - newestIndexes[rowId])
                : 0;
            if (rows > _admission.MaximumBootstrapRows) {
                return new RecapGridControlPutResult.Unauthorized(
                    "MaximumBootstrapRows"
                );
            }
            int columns = recipe.Kind == GridBuildRecipeKind.Full
                ? recipe.Target.OrderedColumns.Count
                : recipe.RecomputedColumns.Count;
            projectedCalls = checked(projectedCalls + (long)rows * columns);
            if (projectedCalls > _admission.MaximumProjectedCalls) {
                return new RecapGridControlPutResult.Unauthorized(
                    "MaximumProjectedCalls"
                );
            }
        }
        return null;
    }

    private bool DefinitionIsAdmitted(
        ControlState state,
        BuildTargetColumn column
    ) => state.Definitions.TryGetValue(
            column.DefinitionDigest.Value,
            out MaintainerDefinitionRevision? definition)
        && definition.LogicalColumnId == column.LogicalColumnId
        && state.Families.ContainsKey(definition.FamilyDigest.Value)
        && _admission.AllowsFamily(definition.FamilyDigest)
        && _admission.AllowsCapability(
            definition.Capability.CapabilityFingerprint)
        && _admission.AllowsTarget(definition.Target.Carrier)
        && _admission.AllowsColumn(definition.LogicalColumnId);

    private RecapGridControlActivateResult? MapBudgetForActivation(
        RecapGridControlPutResult? budget
    ) => budget switch {
        null => null,
        RecapGridControlPutResult.Unauthorized unauthorized
            => new RecapGridControlActivateResult.Unauthorized(
                unauthorized.Rule
            ),
        RecapGridControlPutResult.StaleTimelineHead stale
            => new RecapGridControlActivateResult.StaleTimelineHead(
                stale.Actual
            ),
        RecapGridControlPutResult.NotOnSelectedPath missing
            => new RecapGridControlActivateResult.BootstrapNotSelected(
                missing.RowId
            ),
        RecapGridControlPutResult.Busy
            => new RecapGridControlActivateResult.Busy(),
        RecapGridControlPutResult.TimelineUnsupportedSchema schema
            => new RecapGridControlActivateResult.TimelineUnsupportedSchema(
                schema.SchemaVersion
            ),
        RecapGridControlPutResult.Invalid invalid
            => new RecapGridControlActivateResult.Invalid(
                invalid.Code,
                invalid.Detail
            ),
        _ => new RecapGridControlActivateResult.Invalid(
            "RecipeBudgetOutcomeInvalid",
            "The recipe budget check returned an unknown outcome."
        )
    };

    private RecapGridControlPutResult? ValidateRegisteredBootstrapForPut(
        TimelineHeadRef expected,
        RegisteredGridRecipe registered
    ) => ValidateRegisteredBootstrap(expected, registered) switch {
        null => null,
        RecapGridControlActivateResult.BootstrapNotSelected missing
            => new RecapGridControlPutResult.NotOnSelectedPath(
                missing.RowId
            ),
        RecapGridControlActivateResult.StaleTimelineHead stale
            => new RecapGridControlPutResult.StaleTimelineHead(
                stale.Actual
            ),
        RecapGridControlActivateResult.Busy
            => new RecapGridControlPutResult.Busy(),
        RecapGridControlActivateResult.TimelineUnsupportedSchema schema
            => new RecapGridControlPutResult.TimelineUnsupportedSchema(
                schema.SchemaVersion
            ),
        RecapGridControlActivateResult.Invalid invalid
            => new RecapGridControlPutResult.Invalid(
                invalid.Code,
                invalid.Detail
            ),
        _ => new RecapGridControlPutResult.Invalid(
            "BootstrapSelectionOutcomeInvalid",
            "The stored bootstrap check returned an unknown outcome."
        )
    };

    private RecapGridControlPutResult? ValidateTimelineHead(
        TimelineHeadRef expected
    ) {
        HistoryTimelineSnapshotResult snapshot =
            _timelineReader.ReadSnapshot();
        return snapshot switch {
            HistoryTimelineSnapshotResult.Available available
                when available.Head == expected => null,
            HistoryTimelineSnapshotResult.Available available
                => new RecapGridControlPutResult.StaleTimelineHead(
                    available.Head
                ),
            HistoryTimelineSnapshotResult.Busy
                => new RecapGridControlPutResult.Busy(),
            HistoryTimelineSnapshotResult.UnsupportedSchema schema
                => new RecapGridControlPutResult
                    .TimelineUnsupportedSchema(schema.SchemaVersion),
            HistoryTimelineSnapshotResult.Invalid invalid
                => new RecapGridControlPutResult.Invalid(
                    invalid.Code,
                    invalid.Detail
                ),
            _ => new RecapGridControlPutResult.Invalid(
                "TimelineSnapshotOutcomeInvalid",
                "The Timeline reader returned an unknown snapshot outcome."
            )
        };
    }

    private RecapGridControlActivateResult? ValidateTimelineForActivation(
        TimelineHeadRef expected
    ) {
        HistoryTimelineSnapshotResult snapshot =
            _timelineReader.ReadSnapshot();
        return snapshot switch {
            HistoryTimelineSnapshotResult.Available available
                when available.Head == expected => null,
            HistoryTimelineSnapshotResult.Available available
                => new RecapGridControlActivateResult.StaleTimelineHead(
                    available.Head
                ),
            HistoryTimelineSnapshotResult.Busy
                => new RecapGridControlActivateResult.Busy(),
            HistoryTimelineSnapshotResult.UnsupportedSchema schema
                => new RecapGridControlActivateResult
                    .TimelineUnsupportedSchema(schema.SchemaVersion),
            HistoryTimelineSnapshotResult.Invalid invalid
                => new RecapGridControlActivateResult.Invalid(
                    invalid.Code,
                    invalid.Detail
                ),
            _ => new RecapGridControlActivateResult.Invalid(
                "TimelineSnapshotOutcomeInvalid",
                "The Timeline reader returned an unknown snapshot outcome."
            )
        };
    }

    private RecapGridControlActivateResult? ValidateRegisteredBootstrap(
        TimelineHeadRef expected,
        RegisteredGridRecipe registered
    ) {
        if (registered.Bootstrap.RowId is null) {
            return null;
        }
        HistoryTimelineReaderRowResult selected =
            _timelineReader.ReadSelectedRow(
                expected,
                registered.Bootstrap.RowId.Value
            );
        return selected switch {
            HistoryTimelineReaderRowResult.Selected row
                when row.Row.Descriptor.DescriptorDigest
                    == registered.Bootstrap.DescriptorDigest => null,
            HistoryTimelineReaderRowResult.Selected
                => new RecapGridControlActivateResult.Invalid(
                    "BootstrapDescriptorMismatch",
                    "The stored bootstrap descriptor differs from the selected row."
                ),
            HistoryTimelineReaderRowResult.NotOnSelectedPath missing
                => new RecapGridControlActivateResult.BootstrapNotSelected(
                    missing.RowId
                ),
            HistoryTimelineReaderRowResult.StaleTimelineHead stale
                => new RecapGridControlActivateResult.StaleTimelineHead(
                    stale.Actual
                ),
            HistoryTimelineReaderRowResult.Busy
                => new RecapGridControlActivateResult.Busy(),
            HistoryTimelineReaderRowResult.Invalid invalid
                => new RecapGridControlActivateResult.Invalid(
                    invalid.Code,
                    invalid.Detail
                ),
            _ => new RecapGridControlActivateResult.Invalid(
                "BootstrapSelectionOutcomeInvalid",
                "The Timeline reader returned an unknown selected-row outcome."
            )
        };
    }

    private static RecapGridControlActivateResult?
        ValidateContextComposable(
        ControlState state,
        GridBuildRecipe recipe
    ) {
        var targets = new HashSet<(
            ContextHeaderCarrier Carrier,
            string BlockKey
        )>();
        foreach (BuildTargetColumn column in recipe.Target.OrderedColumns) {
            if (!state.Definitions.TryGetValue(
                    column.DefinitionDigest.Value,
                    out MaintainerDefinitionRevision? definition)) {
                return new RecapGridControlActivateResult.Invalid(
                    "ActiveRecipeDefinitionAbsent",
                    "An active recipe target definition is absent."
                );
            }
            if (definition.MaxContentUtf8Bytes
                > ControlStorageLimits
                    .MaximumContextComposableContentUtf8Bytes) {
                return new RecapGridControlActivateResult.Unauthorized(
                    "ActiveRecipeContentLimit"
                );
            }
            if (!targets.Add((
                    definition.Target.Carrier,
                    definition.Target.BlockKey))) {
                return new RecapGridControlActivateResult.Unauthorized(
                    "ActiveRecipeDuplicateContextTarget"
                );
            }
        }
        return null;
    }

    private ControlState ReadCurrent() {
        ControlState state = ControlState.Decode(
            ControlDurableFiles.ReadState(_paths)
        );
        RecapGridControlFactory.RequireScope(state, _paths);
        return state;
    }
}

internal static class ControlError {
    internal static bool IsFatal(Exception exception) {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException) {
            return true;
        }
        if (exception is AggregateException aggregate) {
            return aggregate.InnerExceptions.Any(IsFatal);
        }
        return exception.InnerException is { } inner && IsFatal(inner);
    }

    internal static void ThrowIfFatal(Exception exception) {
        ArgumentNullException.ThrowIfNull(exception);
        Exception? fatal = FindFatal(exception);
        if (fatal is not null) {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(fatal)
                .Throw();
        }
    }

    internal static RecapGridControlCreateResult Create(Exception exception)
    {
        ThrowIfFatal(exception);
        return exception switch {
            ControlBusyException => new RecapGridControlCreateResult.Busy(),
            ControlLimitException limit
                => new RecapGridControlCreateResult.LimitExceeded(
                    limit.Limit
                ),
            _ => InvalidCreate(exception)
        };
    }

    internal static RecapGridControlPutResult Put(Exception exception)
    {
        ThrowIfFatal(exception);
        return exception switch {
            ControlBusyException => new RecapGridControlPutResult.Busy(),
            ControlLimitException limit
                => new RecapGridControlPutResult.LimitExceeded(limit.Limit),
            _ => InvalidPut(exception)
        };
    }

    internal static RecapGridControlActivateResult Activate(
        Exception exception
    ) {
        ThrowIfFatal(exception);
        return exception switch {
        ControlBusyException => new RecapGridControlActivateResult.Busy(),
            _ => InvalidActivate(exception)
        };
    }

    internal static RecapGridControlOperationResult Operation(
        Exception exception
    ) {
        ThrowIfFatal(exception);
        return exception switch {
        ControlBusyException
            => new RecapGridControlOperationResult.Busy(),
        ControlLimitException limit
            => new RecapGridControlOperationResult.LimitExceeded(limit.Limit),
        ControlUnsupportedSchemaException schema
            => new RecapGridControlOperationResult.Invalid(
                "ControlUnsupportedSchema",
                $"The Control schema version {schema.Version} is unsupported."
            ),
        _ => InvalidOperation(exception)
    };
    }

    internal static (string Code, string Detail) Invalid(
        Exception exception
    ) {
        ThrowIfFatal(exception);
        return exception switch {
        ControlStoreException stored => (stored.Code, stored.Message),
        ControlUnsupportedSchemaException schema => (
            "ControlUnsupportedSchema",
            $"The Control schema version {schema.Version} is unsupported."
        ),
        PlatformNotSupportedException => (
            "ControlPlatformUnsupported",
            exception.Message
        ),
        UnauthorizedAccessException => (
            "ControlStoreAccessInvalid",
            exception.Message
        ),
        IOException => ("ControlStoreIoInvalid", exception.Message),
        InvalidDataException => ("ControlStateInvalid", exception.Message),
        ArgumentException => ("ControlStateInvalid", exception.Message),
        OverflowException => ("ControlStateLimitExceeded", exception.Message),
        _ => ("ControlOperationInvalid", exception.Message)
    };
    }

    private static Exception? FindFatal(Exception exception) {
        if (exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException) {
            return exception;
        }
        if (exception is AggregateException aggregate) {
            foreach (Exception inner in aggregate.InnerExceptions) {
                if (FindFatal(inner) is { } fatal) {
                    return fatal;
                }
            }
        }
        return exception.InnerException is { } cause
            ? FindFatal(cause)
            : null;
    }

    private static RecapGridControlCreateResult.Invalid InvalidCreate(
        Exception exception
    ) {
        (string code, string detail) = Invalid(exception);
        return new RecapGridControlCreateResult.Invalid(code, detail);
    }

    private static RecapGridControlPutResult.Invalid InvalidPut(
        Exception exception
    ) {
        (string code, string detail) = Invalid(exception);
        return new RecapGridControlPutResult.Invalid(code, detail);
    }

    private static RecapGridControlActivateResult.Invalid InvalidActivate(
        Exception exception
    ) {
        (string code, string detail) = Invalid(exception);
        return new RecapGridControlActivateResult.Invalid(code, detail);
    }

    private static RecapGridControlOperationResult.Invalid InvalidOperation(
        Exception exception
    ) {
        (string code, string detail) = Invalid(exception);
        return new RecapGridControlOperationResult.Invalid(code, detail);
    }
}
