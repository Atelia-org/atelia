using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

public abstract record PreparedRecapOperationAuthority {
    private PreparedRecapOperationAuthority(
        SessionCurrentLineageSnapshot lineage,
        DerivedRecapOperationBinding binding
    ) {
        Lineage = lineage
            ?? throw new ArgumentNullException(nameof(lineage));
        Binding = binding
            ?? throw new ArgumentNullException(nameof(binding));
    }

    public SessionCurrentLineageSnapshot Lineage { get; }
    internal DerivedRecapOperationBinding Binding { get; }

    public sealed record FrozenBuilding
        : PreparedRecapOperationAuthority {
        internal FrozenBuilding(
            SessionCurrentLineageSnapshot lineage,
            DerivedRecapOperationBinding binding,
            BuildingDescriptor descriptor
        ) : base(lineage, binding) {
            Descriptor = descriptor
                ?? throw new ArgumentNullException(nameof(descriptor));
        }

        public BuildingDescriptor Descriptor { get; }
    }

    public sealed record NewPlanning
        : PreparedRecapOperationAuthority {
        internal NewPlanning(
            SessionCurrentLineageSnapshot lineage,
            DerivedRecapOperationBinding binding,
            ResolvedRecapPlanningConfiguration configuration,
            DerivedRecapPlanningBaseline baseline
        ) : base(lineage, binding) {
            Configuration = configuration
                ?? throw new ArgumentNullException(
                    nameof(configuration)
                );
            Baseline = baseline
                ?? throw new ArgumentNullException(nameof(baseline));
            if (baseline.CapturedRawHead != lineage.CapturedHead) {
                throw new ArgumentException(
                    "Planning baseline must match the captured lineage.",
                    nameof(baseline)
                );
            }
        }

        public ResolvedRecapPlanningConfiguration Configuration {
            get;
        }
        public DerivedRecapPlanningBaseline Baseline { get; }
    }
}

public enum DerivedRecapOperationPreparationRetryKind {
    RawHeadChanged = 1,
    SourceChanged = 2
}

public sealed record DerivedRecapOperationPreparationDefect(
    string Code,
    string Detail
);

public static class DerivedRecapOperationPreparationDefectCodes {
    public const string RawLineageUnavailable =
        nameof(RawLineageUnavailable);
    public const string StoreUnavailable = nameof(StoreUnavailable);
    public const string StaleCurrentLineageBuilding =
        nameof(StaleCurrentLineageBuilding);
    public const string MultipleCurrentLineageBuildings =
        nameof(MultipleCurrentLineageBuildings);
    public const string PlannerConfigMissing =
        nameof(PlannerConfigMissing);
    public const string PlannerConfigUnavailable =
        nameof(PlannerConfigUnavailable);
    public const string PlannerConfigSourceMismatch =
        nameof(PlannerConfigSourceMismatch);
    public const string PublishedPlanMissing =
        nameof(PublishedPlanMissing);
    public const string LatestPublishedUnavailable =
        nameof(LatestPublishedUnavailable);
}

public abstract record DerivedRecapOperationPreparationResult {
    private DerivedRecapOperationPreparationResult() {
    }

    public sealed record Ready
        : DerivedRecapOperationPreparationResult {
        internal Ready(PreparedRecapOperationAuthority authority) {
            Authority = authority
                ?? throw new ArgumentNullException(nameof(authority));
        }

        public PreparedRecapOperationAuthority Authority { get; }
    }

    public sealed record Retryable
        : DerivedRecapOperationPreparationResult {
        internal Retryable(
            DerivedRecapOperationPreparationRetryKind kind,
            string detail,
            ResolvedRecapPlanningConfiguration? configuration = null
        ) {
            if (!Enum.IsDefined(kind)) {
                throw new ArgumentOutOfRangeException(nameof(kind));
            }
            Kind = kind;
            Detail = string.IsNullOrWhiteSpace(detail)
                ? throw new ArgumentException(
                    "Retry detail cannot be empty.",
                    nameof(detail)
                )
                : detail;
            Configuration = configuration;
        }

        public DerivedRecapOperationPreparationRetryKind Kind { get; }
        public string Detail { get; }
        public ResolvedRecapPlanningConfiguration? Configuration {
            get;
        }
        public string Code => Kind switch {
            DerivedRecapOperationPreparationRetryKind.RawHeadChanged =>
                DerivedRecapExecutionDefectCodes.RawHeadChanged,
            DerivedRecapOperationPreparationRetryKind.SourceChanged =>
                DerivedRecapExecutionDefectCodes.SourceChanged,
            _ => throw new InvalidOperationException(
                "Unknown preparation retry kind."
            )
        };
    }

    public sealed record Unavailable
        : DerivedRecapOperationPreparationResult {
        internal Unavailable(
            IReadOnlyList<DerivedRecapOperationPreparationDefect>
                defects,
            ResolvedRecapPlanningConfiguration? configuration = null,
            RecapPlannerConfigSnapshot? configSnapshot = null
        ) {
            ArgumentNullException.ThrowIfNull(defects);
            if (defects.Count == 0
                || defects.Any(static defect => defect is null)) {
                throw new ArgumentException(
                    "Unavailable preparation requires defects.",
                    nameof(defects)
                );
            }
            if (configuration is not null
                && configSnapshot is not null
                && !ReferenceEquals(
                    configuration.Snapshot,
                    configSnapshot
                )) {
                throw new ArgumentException(
                    "Preparation config provenance is inconsistent.",
                    nameof(configSnapshot)
                );
            }
            Defects = Array.AsReadOnly([.. defects]);
            Configuration = configuration;
            ConfigSnapshot = configSnapshot
                ?? configuration?.Snapshot;
        }

        public IReadOnlyList<DerivedRecapOperationPreparationDefect>
            Defects { get; }
        public ResolvedRecapPlanningConfiguration? Configuration {
            get;
        }
        public RecapPlannerConfigSnapshot? ConfigSnapshot { get; }
    }
}

/// <summary>
/// Captures one Building-first authority before a Host constructs provider
/// clients, concrete Maintainers, or call logs.
/// </summary>
public static class DerivedRecapOperationPreparer {
    public static ValueTask<DerivedRecapOperationPreparationResult>
        PrepareAsync(
        SessionJournalEngine engine,
        DerivedRecapStore store,
        RecapMaintainerCapabilitySnapshot capabilities,
        IRecapActivePlanningConfigurationSource activeConfiguration,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(activeConfiguration);
        RequireSameBinding(store, engine);

        return PrepareCoreAsync(
            new DerivedRecapOperationPreparationServices(
                DerivedRecapOperationBinding.Create(
                    engine.Path,
                    engine.BranchRefId
                ),
                engine.ReadCurrentLineageHeaders,
                store.SelectCurrentLineageBuildingAsync,
                activeConfiguration.Load,
                store.SelectNthPreviousAsync,
                store.ReadPublishedPlanAsync,
                store.ReadPublishedPlanAtAnchorAsync,
                engine.ReadCurrentHead
            ),
            capabilities,
            cancellationToken
        );
    }

    internal static async ValueTask<
        DerivedRecapOperationPreparationResult
    > PrepareCoreAsync(
        DerivedRecapOperationPreparationServices services,
        RecapMaintainerCapabilitySnapshot capabilities,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(capabilities);

        SessionCurrentLineageSnapshot lineage;
        try {
            lineage = services.ReadLineage(cancellationToken);
        }
        catch (Exception exception) when (
            IsAvailabilityException(exception)
        ) {
            return Unavailable(
                DerivedRecapOperationPreparationDefectCodes
                    .RawLineageUnavailable,
                exception.Message
            );
        }

        CurrentLineageBuildingSelection building;
        try {
            building =
                await services.SelectBuilding(
                        lineage,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            IsAvailabilityException(exception)
        ) {
            return Unavailable(
                DerivedRecapOperationPreparationDefectCodes
                    .StoreUnavailable,
                exception.Message
            );
        }

        switch (building) {
            case CurrentLineageBuildingSelection.Available available:
                foreach (MaintainRecapBlockPlan plan
                    in available.Snapshot.Manifest.Blocks
                        .OfType<MaintainRecapBlockPlan>()) {
                    if (!capabilities.SupportsFrozen(
                            plan.MaintainerId,
                            plan.Target
                        )) {
                        return Unavailable(
                            DerivedRecapExecutionDefectCodes
                                .MaintainerUnavailable,
                            "Frozen Building maintainer binding for "
                            + $"'{plan.RecapBlockId}' is unavailable."
                        );
                    }
                }
                if (CheckRawHeadFence(
                        services,
                        lineage.CapturedHead
                    ) is { } frozenFenceFailure) {
                    return frozenFenceFailure;
                }
                return new DerivedRecapOperationPreparationResult.Ready(
                    new PreparedRecapOperationAuthority.FrozenBuilding(
                        lineage,
                        services.Binding,
                        available.Snapshot.Descriptor
                    )
                );
            case CurrentLineageBuildingSelection.Invalid invalid:
                return Unavailable(invalid.Defects);
            case CurrentLineageBuildingSelection.Stale stale:
                return Unavailable(
                    DerivedRecapOperationPreparationDefectCodes
                        .StaleCurrentLineageBuilding,
                    $"Building '{stale.SetAdmissionAnchor}' is not "
                    + "strictly newer than latest Published "
                    + $"'{stale.LatestPublishedAnchor}'. Explicitly "
                    + "abandon the stale Building."
                );
            case CurrentLineageBuildingSelection.Multiple multiple:
                return Unavailable(
                    DerivedRecapOperationPreparationDefectCodes
                        .MultipleCurrentLineageBuildings,
                    "Current raw lineage has multiple Building "
                    + "memberships: "
                    + string.Join(
                        ", ",
                        multiple.SetAdmissionAnchors.Select(
                            EventAddressTextCodec.Format
                        )
                    )
                    + ". Use exact resume or abandon-building."
                );
            case CurrentLineageBuildingSelection.StoreUnavailable
                unavailable:
                return Unavailable(
                    DerivedRecapOperationPreparationDefectCodes
                        .StoreUnavailable,
                    unavailable.Reason
                );
            case CurrentLineageBuildingSelection.None:
                break;
            default:
                throw new InvalidDataException(
                    "Unknown current-lineage Building selection."
                );
        }

        RecapActivePlanningConfigurationLoadResult loaded;
        try {
            loaded = services.LoadActiveConfiguration();
        }
        catch (Exception exception) when (
            IsAvailabilityException(exception)
        ) {
            return Unavailable(
                DerivedRecapOperationPreparationDefectCodes
                    .PlannerConfigUnavailable,
                exception.Message
            );
        }
        if (loaded
            is not RecapActivePlanningConfigurationLoadResult.Available
                configAvailable) {
            return loaded switch {
                RecapActivePlanningConfigurationLoadResult.Missing
                    missing => Unavailable(
                        DerivedRecapOperationPreparationDefectCodes
                            .PlannerConfigMissing,
                        $"Recap planner config is missing: "
                        + missing.Path
                    ),
                RecapActivePlanningConfigurationLoadResult.Invalid
                    invalid => Unavailable(
                        invalid.Defects,
                        configSnapshot: invalid.Snapshot
                    ),
                RecapActivePlanningConfigurationLoadResult.Unavailable
                    unavailable => Unavailable(
                        DerivedRecapOperationPreparationDefectCodes
                            .PlannerConfigUnavailable,
                        unavailable.Reason,
                        configSnapshot: unavailable.Snapshot
                    ),
                _ => throw new InvalidDataException(
                    "Unknown active planning configuration result."
                )
            };
        }

        ResolvedRecapPlanningConfiguration configuration =
            configAvailable.Configuration;
        if (ValidateConfigurationSource(
                services.Binding,
                capabilities,
                configuration
            ) is { } sourceMismatch) {
            return sourceMismatch;
        }
        DerivedRecapSelection latest;
        try {
            latest = await services.SelectLatest(
                    lineage,
                    0,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            IsAvailabilityException(exception)
        ) {
            return Unavailable(
                DerivedRecapOperationPreparationDefectCodes
                    .StoreUnavailable,
                exception.Message,
                configuration
            );
        }

        FrozenCatalogReadResult frozenCatalog;
        try {
            frozenCatalog = await ReadLatestFrozenCatalogAsync(
                    services,
                    latest,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            IsAvailabilityException(exception)
        ) {
            return Unavailable(
                DerivedRecapOperationPreparationDefectCodes
                    .StoreUnavailable,
                exception.Message,
                configuration
            );
        }

        if (frozenCatalog is FrozenCatalogReadResult.Unavailable
            catalogUnavailable) {
            return new DerivedRecapOperationPreparationResult
                .Unavailable(
                catalogUnavailable.Defects,
                configuration
            );
        }
        if (frozenCatalog is FrozenCatalogReadResult.SourceChanged
            sourceChanged) {
            return new DerivedRecapOperationPreparationResult.Retryable(
                DerivedRecapOperationPreparationRetryKind.SourceChanged,
                sourceChanged.Detail,
                configuration
            );
        }
        if (frozenCatalog is FrozenCatalogReadResult.Available
            catalogAvailable) {
            RecapCatalogShapeComparison comparison =
                RecapCatalogShape.Compare(
                    RecapCatalogShape.ProjectActive(
                        configuration.PlanningInputs.OrderedCatalog
                    ),
                    RecapCatalogShape.ProjectFrozen(
                        catalogAvailable.Blocks
                    )
                );
            if (!comparison.IsExactMatch) {
                return Unavailable(
                    DerivedRecapExecutionDefectCodes
                        .CatalogMigrationRequired,
                    comparison.Detail,
                    configuration
                );
            }
        }

        if (CheckRawHeadFence(
                services,
                lineage.CapturedHead,
                configuration
            ) is { } planningFenceFailure) {
            return planningFenceFailure;
        }
        try {
            return new DerivedRecapOperationPreparationResult.Ready(
                new PreparedRecapOperationAuthority.NewPlanning(
                    lineage,
                    services.Binding,
                    configuration,
                    DerivedRecapPlanningBaseline.FromSelection(
                        lineage.CapturedHead,
                        latest
                    )
                )
            );
        }
        catch (ArgumentException exception) {
            return Unavailable(
                DerivedRecapOperationPreparationDefectCodes
                    .LatestPublishedUnavailable,
                exception.Message,
                configuration
            );
        }
    }

    private static async ValueTask<FrozenCatalogReadResult>
        ReadLatestFrozenCatalogAsync(
        DerivedRecapOperationPreparationServices services,
        DerivedRecapSelection latest,
        CancellationToken cancellationToken
    ) {
        switch (latest) {
            case DerivedRecapSelection.EmptyLineage:
                return new FrozenCatalogReadResult.Empty();
            case DerivedRecapSelection.Selected selected:
                PublishedPlanReadResult plan =
                    await services.ReadPublishedPlan(
                            selected.Descriptor,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                return plan switch {
                    PublishedPlanReadResult.Available available =>
                        new FrozenCatalogReadResult.Available(
                            available.Snapshot.FrozenPlan.Blocks
                        ),
                    PublishedPlanReadResult.Changed changed =>
                        new FrozenCatalogReadResult.SourceChanged(
                            "Latest Published plan changed during "
                            + "preparation: expected "
                            + $"'{changed.Expected.EnvelopeSha256}', "
                            + "observed "
                            + $"'{changed.Observed.EnvelopeSha256}'."
                        ),
                    PublishedPlanReadResult.Unavailable unavailable =>
                        FrozenCatalogUnavailable(unavailable.Defects),
                    _ => throw new InvalidDataException(
                        "Unknown Published plan read result."
                    )
                };
            case DerivedRecapSelection.ExactPublishedSetInvalid invalid:
                PublishedPlanAtAnchorReadResult atAnchor =
                    await services.ReadPublishedPlanAtAnchor(
                            invalid.SetAdmissionAnchor,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                return atAnchor switch {
                    PublishedPlanAtAnchorReadResult.Available available =>
                        new FrozenCatalogReadResult.Available(
                            available.Snapshot.FrozenPlan.Blocks
                        ),
                    PublishedPlanAtAnchorReadResult.Missing missing =>
                        FrozenCatalogUnavailable(
                            DerivedRecapOperationPreparationDefectCodes
                                .PublishedPlanMissing,
                            "Latest Published plan disappeared at "
                            + $"'{EventAddressTextCodec.Format(
                                missing.SetAdmissionAnchor
                            )}'."
                        ),
                    PublishedPlanAtAnchorReadResult.Changed changed =>
                        new FrozenCatalogReadResult.SourceChanged(
                            "Latest Published plan changed during "
                            + "preparation: before "
                            + $"'{changed.Before.EnvelopeSha256}', after "
                            + $"'{changed.After?.EnvelopeSha256
                                ?? "<missing>"}'."
                        ),
                    PublishedPlanAtAnchorReadResult.Unavailable
                        unavailable => FrozenCatalogUnavailable(
                            unavailable.Defects
                        ),
                    _ => throw new InvalidDataException(
                        "Unknown Published plan-at-anchor read result."
                    )
                };
            case DerivedRecapSelection.StoreUnavailable unavailable:
                return FrozenCatalogUnavailable(
                    DerivedRecapOperationPreparationDefectCodes
                        .StoreUnavailable,
                    unavailable.Reason
                );
            case DerivedRecapSelection.OrdinalUnavailable:
                return FrozenCatalogUnavailable(
                    DerivedRecapOperationPreparationDefectCodes
                        .LatestPublishedUnavailable,
                    "Latest strict Published ordinal is unavailable."
                );
            default:
                return FrozenCatalogUnavailable(
                    DerivedRecapOperationPreparationDefectCodes
                        .LatestPublishedUnavailable,
                    $"Unsupported latest selection "
                    + $"'{latest.GetType().Name}'."
                );
        }
    }

    private static DerivedRecapOperationPreparationResult.Retryable
        RawHeadChanged(
        EventAddress expected,
        ResolvedRecapPlanningConfiguration? configuration = null
    ) => new(
        DerivedRecapOperationPreparationRetryKind.RawHeadChanged,
        $"Raw SessionJournal head changed after recap preparation "
        + $"capture '{EventAddressTextCodec.Format(expected)}'.",
        configuration
    );

    private static DerivedRecapOperationPreparationResult?
        CheckRawHeadFence(
        DerivedRecapOperationPreparationServices services,
        EventAddress expected,
        ResolvedRecapPlanningConfiguration? configuration = null
    ) {
        EventAddress? current;
        try {
            current = services.ReadCurrentHead();
        }
        catch (Exception exception) when (
            IsAvailabilityException(exception)
        ) {
            return Unavailable(
                DerivedRecapOperationPreparationDefectCodes
                    .RawLineageUnavailable,
                exception.Message,
                configuration
            );
        }
        return current == expected
            ? null
            : RawHeadChanged(expected, configuration);
    }

    private static DerivedRecapOperationPreparationResult?
        ValidateConfigurationSource(
        DerivedRecapOperationBinding binding,
        RecapMaintainerCapabilitySnapshot capabilities,
        ResolvedRecapPlanningConfiguration configuration
    ) {
        string? canonicalPath = configuration.Snapshot.CanonicalPath;
        if (canonicalPath is not null) {
            string expectedPath =
                RecapPlannerConfigLoader.GetCanonicalPath(
                    binding.RepositoryPath
                );
            if (!PathsEqual(canonicalPath, expectedPath)) {
                return Unavailable(
                    DerivedRecapOperationPreparationDefectCodes
                        .PlannerConfigSourceMismatch,
                    "Resolved planner config belongs to a different "
                    + $"repository: expected '{expectedPath}', "
                    + $"observed '{canonicalPath}'.",
                    configSnapshot: configuration.Snapshot
                );
            }
        }

        foreach (ResolvedActiveRecapProfile active
            in configuration.ActiveProfiles) {
            if (!capabilities.TryResolveProfileName(
                    active.ProfileName,
                    out RecapProfilePlanningDescriptor supported
                )
                || supported != active.Capability) {
                return Unavailable(
                    DerivedRecapOperationPreparationDefectCodes
                        .PlannerConfigSourceMismatch,
                    "Resolved planner profile "
                    + $"'{active.ProfileName}' does not belong to the "
                    + "capability snapshot supplied to the preparer.",
                    configSnapshot: configuration.Snapshot
                );
            }
        }
        return null;
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal
        );

    private static DerivedRecapOperationPreparationResult.Unavailable
        Unavailable(
        string code,
        string detail,
        ResolvedRecapPlanningConfiguration? configuration = null,
        RecapPlannerConfigSnapshot? configSnapshot = null
    ) => new(
        [new DerivedRecapOperationPreparationDefect(code, detail)],
        configuration,
        configSnapshot
    );

    private static DerivedRecapOperationPreparationResult.Unavailable
        Unavailable(
        IEnumerable<RecapStructuralDefect> defects,
        ResolvedRecapPlanningConfiguration? configuration = null
    ) => new(
        Array.AsReadOnly([
            .. defects.Select(static defect =>
                new DerivedRecapOperationPreparationDefect(
                    defect.Code,
                    defect.Detail
                )
            )
        ]),
        configuration
    );

    private static DerivedRecapOperationPreparationResult.Unavailable
        Unavailable(
        IEnumerable<RecapActivePlanningConfigurationDefect> defects,
        ResolvedRecapPlanningConfiguration? configuration = null,
        RecapPlannerConfigSnapshot? configSnapshot = null
    ) => new(
        Array.AsReadOnly([
            .. defects.Select(static defect =>
                new DerivedRecapOperationPreparationDefect(
                    defect.Code,
                    defect.Detail
                )
            )
        ]),
        configuration,
        configSnapshot
    );

    private static FrozenCatalogReadResult.Unavailable
        FrozenCatalogUnavailable(
        string code,
        string detail
    ) => new([
        new DerivedRecapOperationPreparationDefect(code, detail)
    ]);

    private static FrozenCatalogReadResult.Unavailable
        FrozenCatalogUnavailable(
        IEnumerable<RecapStructuralDefect> defects
    ) => new(Array.AsReadOnly([
        .. defects.Select(static defect =>
            new DerivedRecapOperationPreparationDefect(
                defect.Code,
                defect.Detail
            )
        )
    ]));

    private static bool IsAvailabilityException(Exception exception)
        => exception is InvalidDataException
            or InvalidOperationException
            or ArgumentException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or KeyNotFoundException;

    private static void RequireSameBinding(
        DerivedRecapStore store,
        SessionJournalEngine engine
    ) {
        string storePath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(store.SessionRepositoryPath)
        );
        string enginePath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(engine.Path)
        );
        if (!string.Equals(
                storePath,
                enginePath,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal
            )
            || store.RefId != engine.BranchRefId) {
            throw new ArgumentException(
                "DerivedRecap preparer, Store, and "
                + "SessionJournalEngine must bind the same repository "
                + "and RefId."
            );
        }
    }

    private abstract record FrozenCatalogReadResult {
        private FrozenCatalogReadResult() {
        }

        internal sealed record Empty : FrozenCatalogReadResult;
        internal sealed record Available(
            IReadOnlyList<RecapBlockPlan> Blocks
        ) : FrozenCatalogReadResult;
        internal sealed record SourceChanged(string Detail)
            : FrozenCatalogReadResult;
        internal sealed record Unavailable(
            IReadOnlyList<DerivedRecapOperationPreparationDefect>
                Defects
        ) : FrozenCatalogReadResult;
    }
}

internal sealed class DerivedRecapOperationPreparationServices {
    internal DerivedRecapOperationPreparationServices(
        DerivedRecapOperationBinding binding,
        Func<CancellationToken, SessionCurrentLineageSnapshot>
            readLineage,
        Func<
            SessionCurrentLineageSnapshot,
            CancellationToken,
            ValueTask<CurrentLineageBuildingSelection>
        > selectBuilding,
        Func<RecapActivePlanningConfigurationLoadResult>
            loadActiveConfiguration,
        Func<
            SessionCurrentLineageSnapshot,
            int,
            CancellationToken,
            ValueTask<DerivedRecapSelection>
        > selectLatest,
        Func<
            PublishedRecapDescriptor,
            CancellationToken,
            ValueTask<PublishedPlanReadResult>
        > readPublishedPlan,
        Func<
            EventAddress,
            CancellationToken,
            ValueTask<PublishedPlanAtAnchorReadResult>
        > readPublishedPlanAtAnchor,
        Func<EventAddress?> readCurrentHead
    ) {
        Binding = binding
            ?? throw new ArgumentNullException(nameof(binding));
        ReadLineage = readLineage;
        SelectBuilding = selectBuilding;
        LoadActiveConfiguration = loadActiveConfiguration;
        SelectLatest = selectLatest;
        ReadPublishedPlan = readPublishedPlan;
        ReadPublishedPlanAtAnchor = readPublishedPlanAtAnchor;
        ReadCurrentHead = readCurrentHead;
    }

    internal DerivedRecapOperationBinding Binding { get; }
    internal Func<CancellationToken, SessionCurrentLineageSnapshot>
        ReadLineage { get; }
    internal Func<
        SessionCurrentLineageSnapshot,
        CancellationToken,
        ValueTask<CurrentLineageBuildingSelection>
    > SelectBuilding { get; }
    internal Func<RecapActivePlanningConfigurationLoadResult>
        LoadActiveConfiguration { get; }
    internal Func<
        SessionCurrentLineageSnapshot,
        int,
        CancellationToken,
        ValueTask<DerivedRecapSelection>
    > SelectLatest { get; }
    internal Func<
        PublishedRecapDescriptor,
        CancellationToken,
        ValueTask<PublishedPlanReadResult>
    > ReadPublishedPlan { get; }
    internal Func<
        EventAddress,
        CancellationToken,
        ValueTask<PublishedPlanAtAnchorReadResult>
    > ReadPublishedPlanAtAnchor { get; }
    internal Func<EventAddress?> ReadCurrentHead { get; }
}

internal sealed class DerivedRecapOperationBinding {
    private DerivedRecapOperationBinding(
        string repositoryPath,
        RefId refId
    ) {
        RepositoryPath = repositoryPath;
        RefId = refId;
    }

    internal string RepositoryPath { get; }
    internal RefId RefId { get; }

    internal static DerivedRecapOperationBinding Create(
        string repositoryPath,
        RefId refId
    ) => new(
        Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(repositoryPath)
        ),
        refId
    );

    internal bool Matches(string repositoryPath, RefId refId) {
        string normalized = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(repositoryPath)
        );
        return RefId == refId
            && string.Equals(
                RepositoryPath,
                normalized,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal
            );
    }
}
