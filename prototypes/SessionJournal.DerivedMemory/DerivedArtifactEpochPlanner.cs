using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal;

namespace Atelia.SessionJournal.DerivedMemory;

public sealed class DerivedArtifactEpochPlanner {
    public const string ConfigSchema =
        "atelia.session-journal.derived-artifact-planner-config.v1";
    public const string ConfigPointerSchema =
        "atelia.session-journal.derived-artifact-planner-config-pointer.v1";
    public const string EpochSchema =
        "atelia.session-journal.derived-artifact-epoch.v1";
    public const string EpochPointerSchema =
        "atelia.session-journal.derived-artifact-epoch-pointer.v1";
    public const string TokenEstimatorId =
        SessionHistoryTokenEstimator.EstimatorId;
    public const string BoundaryPolicyId =
        "atelia.session-journal.dependency-closed-replay-safe-boundary.v1";
    public const string HardLimitPolicyId =
        "atelia.session-journal.derived-memory-explicit-backpressure.v1";
    public const string GenesisPolicyId =
        "atelia.session-journal.empty-memory-pack-genesis.v1";

    public const long MaxConfigFileBytes = 64 * 1024;
    public const long MaxEpochFileBytes = 128 * 1024;
    public const long MaxPointerFileBytes = 32 * 1024;

    private const string ConfigIdDomain =
        "atelia.session-journal.derived-artifact-planner-config-id.v1";
    private const string EpochIdDomain =
        "atelia.session-journal.derived-artifact-epoch-id.v1";
    private const string KeyDomain =
        "atelia.session-journal.derived-artifact-planner-key.v1";

    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly JsonSerializerOptions IdentityJsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly DerivedMemoryRepository _repository;

    internal DerivedArtifactEpochPlanner(DerivedMemoryRepository repository) {
        _repository = repository;
        ConfigsDirectory = Path.Combine(
            repository.MemoryRoot,
            "planner-configs"
        );
        CurrentConfigsDirectory = Path.Combine(
            repository.MemoryRoot,
            "indexes",
            "current-planner-configs"
        );
        EpochsDirectory = Path.Combine(repository.MemoryRoot, "epochs");
        LatestEpochsDirectory = Path.Combine(
            repository.MemoryRoot,
            "indexes",
            "latest-epochs"
        );
    }

    public string ConfigsDirectory { get; }
    public string CurrentConfigsDirectory { get; }
    public string EpochsDirectory { get; }
    public string LatestEpochsDirectory { get; }

    internal Func<CancellationToken, ValueTask>?
        BeforeLinearizationAsync { get; set; }

    public async ValueTask<DerivedArtifactPlannerConfig> ConfigureAsync(
        DerivedArtifactPlannerConfigDefinition definition,
        string? expectedCurrentConfigId,
        CancellationToken cancellationToken = default
    ) {
        ValidateConfigDefinition(definition);
        if (expectedCurrentConfigId is not null) {
            ValidateConfigId(expectedCurrentConfigId);
        }

        await using FileStream writeLock = await _repository
            .AcquireWriteLockAsync(cancellationToken)
            .ConfigureAwait(false);
        EnsureDirectories();
        var key = new DerivedArtifactPlannerKey(
            definition.LineageKey,
            definition.CoherenceGroup
        );
        DerivedArtifactPlannerConfigPointer? current =
            await TryReadConfigPointerAsync(
                    key,
                    cancellationToken
                )
                .ConfigureAwait(false);
        DerivedArtifactPlannerConfig? currentConfig = current is null
            ? null
            : await ReadConfigRequiredAsync(
                    current.ConfigId,
                    cancellationToken
                )
                .ConfigureAwait(false);
        if (currentConfig is not null
            && HasSameDefinition(currentConfig, definition)) {
            if (string.Equals(
                    currentConfig.ConfigId,
                    expectedCurrentConfigId,
                    StringComparison.Ordinal
                )
                || string.Equals(
                    currentConfig.PreviousConfigId,
                    expectedCurrentConfigId,
                    StringComparison.Ordinal
                )) {
                return currentConfig;
            }
            throw new DerivedArtifactEpochConcurrencyException(
                "Planner config current pointer changed before an otherwise idempotent configure request."
            );
        }
        DerivedArtifactPlannerConfig candidate =
            CreateConfig(definition, expectedCurrentConfigId);
        if (string.Equals(
                current?.ConfigId,
                candidate.ConfigId,
                StringComparison.Ordinal
            )) {
            return await ReadConfigRequiredAsync(
                    candidate.ConfigId,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        if (!string.Equals(
                current?.ConfigId,
                expectedCurrentConfigId,
                StringComparison.Ordinal
            )) {
            throw new DerivedArtifactEpochConcurrencyException(
                "Planner config current pointer changed. "
                + $"Expected '{expectedCurrentConfigId ?? "<none>"}', "
                + $"observed '{current?.ConfigId ?? "<none>"}'."
            );
        }

        await WriteImmutableAsync(
                GetConfigPath(candidate.ConfigId),
                ToDto(candidate),
                MaxConfigFileBytes,
                "planner config",
                cancellationToken
            )
            .ConfigureAwait(false);
        await WritePointerAsync(
                GetConfigPointerPath(candidate.Key),
                new PlannerConfigPointerDto(
                    ConfigPointerSchema,
                    candidate.LineageKey,
                    candidate.CoherenceGroup,
                    candidate.ConfigId
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
        return candidate;
    }

    public async ValueTask<DerivedArtifactPlannerConfig?> TryReadCurrentConfigAsync(
        DerivedArtifactPlannerKey key,
        CancellationToken cancellationToken = default
    ) {
        ValidateKey(key);
        DerivedArtifactPlannerConfigPointer? pointer =
            await TryReadConfigPointerAsync(key, cancellationToken)
                .ConfigureAwait(false);
        return pointer is null
            ? null
            : await ReadConfigRequiredAsync(
                    pointer.ConfigId,
                    cancellationToken
                )
                .ConfigureAwait(false);
    }

    public async ValueTask<DerivedArtifactPlannerConfig?> TryReadConfigAsync(
        string configId,
        CancellationToken cancellationToken = default
    ) {
        ValidateConfigId(configId);
        string path = GetConfigPath(configId);
        EnsureSafePointPath(path);
        return !File.Exists(path)
            ? null
            : await ReadConfigRequiredAsync(configId, cancellationToken)
                .ConfigureAwait(false);
    }

    public async ValueTask<DerivedArtifactEpochPlan?> TryReadEpochAsync(
        string epochId,
        CancellationToken cancellationToken = default
    ) {
        ValidateEpochId(epochId);
        string path = GetEpochPath(epochId);
        EnsureSafePointPath(path);
        return !File.Exists(path)
            ? null
            : await ReadEpochRequiredAsync(epochId, cancellationToken)
                .ConfigureAwait(false);
    }

    public async ValueTask<DerivedArtifactEpochPlan?>
        TryReadLatestEpochAsync(
        DerivedArtifactPlannerKey key,
        CancellationToken cancellationToken = default
    ) {
        ValidateKey(key);
        DerivedArtifactEpochLatestPointer? pointer =
            await TryReadEpochPointerAsync(key, cancellationToken)
                .ConfigureAwait(false);
        return pointer is null
            ? null
            : await ReadEpochRequiredAsync(
                    pointer.EpochId,
                    cancellationToken
                )
                .ConfigureAwait(false);
    }

    public async ValueTask<DerivedArtifactEpochPlanningResult> PlanAsync(
        SessionJournalEngine engine,
        DerivedArtifactEpochPlanningRequest request,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(request);
        var key = new DerivedArtifactPlannerKey(
            request.LineageKey,
            request.CoherenceGroup
        );
        ValidateKey(key);
        ValidatePlanningRequest(request);
        RequireMatchingRepository(engine);

        DerivedArtifactPlannerConfigPointer configPointer =
            await TryReadConfigPointerAsync(key, cancellationToken)
                .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No current planner config exists for '{FormatKey(key)}'."
            );
        DerivedArtifactPlannerConfig config =
            await ReadConfigRequiredAsync(
                    configPointer.ConfigId,
                    cancellationToken
                )
                .ConfigureAwait(false);
        if (config.Key != key) {
            throw new InvalidDataException(
                "Current planner config pointer crosses planner keys."
            );
        }
        DerivedArtifactEpochLatestPointer? optimisticLatest =
            await TryReadEpochPointerAsync(key, cancellationToken)
                .ConfigureAwait(false);
        if (!string.Equals(
                optimisticLatest?.EpochId,
                request.ExpectedPreviousEpochId,
                StringComparison.Ordinal
            )) {
            DerivedArtifactEpochPlan? possibleDirectChild =
                optimisticLatest is null
                    ? null
                    : await ReadEpochRequiredAsync(
                            optimisticLatest.EpochId,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
            if (possibleDirectChild is null
                || possibleDirectChild.Key != key
                || !string.Equals(
                    possibleDirectChild.PreviousEpochId,
                    request.ExpectedPreviousEpochId,
                    StringComparison.Ordinal
                )
                || !string.Equals(
                    possibleDirectChild.InputSetId,
                    request.InputSetId,
                    StringComparison.Ordinal
                )) {
                throw new DerivedArtifactEpochConcurrencyException(
                    "Derived artifact epoch latest pointer is already stale or unrelated to the requested predecessor."
                );
            }
        }
        DerivedArtifactEpochPlan? previous =
            request.ExpectedPreviousEpochId is null
                ? null
                : await ReadEpochRequiredAsync(
                        request.ExpectedPreviousEpochId,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
        if (previous is not null && previous.Key != key) {
            throw new InvalidDataException(
                "Expected previous epoch belongs to a different planner key."
            );
        }
        await ValidateInputSetAsync(
                request,
                previous,
                key,
                cancellationToken
            )
            .ConfigureAwait(false);

        SessionHistoryPlanningWindow window =
            engine.ReadHistoryPlanningWindow(
                previous?.SourceEndInclusive,
                cancellationToken
            );
        long[] unitCosts = window.Units
            .Select(static unit => SessionHistoryTokenEstimator.Estimate(unit.Message))
            .ToArray();
        long totalTokens = SumChecked(unitCosts, unitCosts.Length);
        CandidateBoundary? selected = SelectBoundary(
            window,
            unitCosts,
            config
        );
        DerivedArtifactEpochPlanningDiagnostics diagnostics =
            CreateDiagnostics(
                window,
                totalTokens,
                selected?.EligibleTokens ?? 0,
                selected?.RetainedTokens ?? totalTokens
            );

        DerivedArtifactEpochPlan? candidate = null;
        if (selected is not null) {
            candidate = CreateEpoch(
                config,
                request,
                window,
                selected,
                window.StartSetups,
                diagnostics
            );
        }

        if (BeforeLinearizationAsync is { } beforeLinearization) {
            await beforeLinearization(cancellationToken)
                .ConfigureAwait(false);
        }
        await using FileStream writeLock = await _repository
            .AcquireWriteLockAsync(cancellationToken)
            .ConfigureAwait(false);
        EnsureDirectories();
        DerivedArtifactPlannerConfigPointer publishConfigPointer =
            await TryReadConfigPointerAsync(key, cancellationToken)
                .ConfigureAwait(false)
            ?? throw new DerivedArtifactEpochConcurrencyException(
                "Planner config current pointer disappeared before epoch publication."
            );
        if (!string.Equals(
                publishConfigPointer.ConfigId,
                config.ConfigId,
                StringComparison.Ordinal
            )) {
            throw new DerivedArtifactEpochConcurrencyException(
                "Planner config changed while the epoch was being planned."
            );
        }
        DerivedArtifactEpochLatestPointer? publishLatestPointer =
            await TryReadEpochPointerAsync(key, cancellationToken)
                .ConfigureAwait(false);
        if (!string.Equals(
                publishLatestPointer?.EpochId,
                request.ExpectedPreviousEpochId,
                StringComparison.Ordinal
            )) {
            if (publishLatestPointer is not null) {
                DerivedArtifactEpochPlan publishedChild =
                    await ReadEpochRequiredAsync(
                            publishLatestPointer.EpochId,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                if (publishedChild.Key == key
                    && string.Equals(
                        publishedChild.PreviousEpochId,
                        request.ExpectedPreviousEpochId,
                        StringComparison.Ordinal
                    )
                    && string.Equals(
                        publishedChild.InputSetId,
                        request.InputSetId,
                        StringComparison.Ordinal
                    )
                    && publishedChild.PlannedAtRawHead
                        == window.ObservedRawHead) {
                    DerivedArtifactPlannerConfig durableConfig =
                        await ReadConfigRequiredAsync(
                                publishedChild.ConfigId,
                                cancellationToken
                            )
                            .ConfigureAwait(false);
                    return new DerivedArtifactEpochPlanningResult(
                        DerivedArtifactEpochPlanningStatus.AlreadyPlanned,
                        durableConfig,
                        publishedChild,
                        publishedChild.PlanningDiagnostics
                    );
                }
            }
            throw new DerivedArtifactEpochConcurrencyException(
                "Derived artifact epoch latest pointer changed to an unrelated successor."
            );
        }
        if (candidate is null) {
            if (checked(totalTokens + config.SchedulingHeadroomTokens)
                >= config.HardLimitTokens) {
                throw new DerivedArtifactEpochBackpressureException(
                    "Derived-memory history reached the configured hard limit "
                    + "without a dependency-safe epoch boundary that preserves "
                    + "the minimum recent-history budget."
                );
            }
            return new DerivedArtifactEpochPlanningResult(
                DerivedArtifactEpochPlanningStatus.BelowTrigger,
                config,
                null,
                diagnostics
            );
        }
        DerivedArtifactEpochPlan durableCandidate =
            await TryReadEpochAsync(
                    candidate.EpochId,
                    cancellationToken
                )
                .ConfigureAwait(false)
            ?? candidate;
        if (durableCandidate == candidate) {
            await WriteImmutableAsync(
                    GetEpochPath(candidate.EpochId),
                    ToDto(candidate),
                    MaxEpochFileBytes,
                    "derived artifact epoch",
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        else {
            RequireSameEpochIdentity(durableCandidate, candidate);
        }
        await WritePointerAsync(
                GetEpochPointerPath(durableCandidate.Key),
                new EpochPointerDto(
                    EpochPointerSchema,
                    durableCandidate.LineageKey,
                    durableCandidate.CoherenceGroup,
                    durableCandidate.EpochId
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
        return new DerivedArtifactEpochPlanningResult(
            DerivedArtifactEpochPlanningStatus.Planned,
            config,
            durableCandidate,
            durableCandidate.PlanningDiagnostics
        );
    }

    public async ValueTask<DerivedArtifactEpochInventory> ReadInventoryAsync(
        CancellationToken cancellationToken = default
    ) {
        var configs = new List<DerivedArtifactPlannerConfig>();
        foreach (string path in EnumerateJson(ConfigsDirectory)) {
            PlannerConfigDto dto = await ReadDtoAsync<PlannerConfigDto>(
                    path,
                    MaxConfigFileBytes,
                    "planner config",
                    cancellationToken
                )
                .ConfigureAwait(false);
            DerivedArtifactPlannerConfig config = MaterializeConfig(dto);
            RequireFileName(path, config.ConfigId);
            configs.Add(config);
        }
        var configPointers = new List<DerivedArtifactPlannerConfigPointer>();
        foreach (string path in EnumerateJson(CurrentConfigsDirectory)) {
            PlannerConfigPointerDto dto =
                await ReadDtoAsync<PlannerConfigPointerDto>(
                        path,
                        MaxPointerFileBytes,
                        "planner config pointer",
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            DerivedArtifactPlannerConfigPointer pointer =
                MaterializeConfigPointer(dto);
            RequireFileName(path, ComputeKeyFileName(pointer.LineageKey, pointer.CoherenceGroup));
            configPointers.Add(pointer);
        }
        var epochs = new List<DerivedArtifactEpochPlan>();
        foreach (string path in EnumerateJson(EpochsDirectory)) {
            EpochDto dto = await ReadDtoAsync<EpochDto>(
                    path,
                    MaxEpochFileBytes,
                    "derived artifact epoch",
                    cancellationToken
                )
                .ConfigureAwait(false);
            DerivedArtifactEpochPlan epoch = MaterializeEpoch(dto);
            RequireFileName(path, epoch.EpochId);
            epochs.Add(epoch);
        }
        var epochPointers = new List<DerivedArtifactEpochLatestPointer>();
        foreach (string path in EnumerateJson(LatestEpochsDirectory)) {
            EpochPointerDto dto = await ReadDtoAsync<EpochPointerDto>(
                    path,
                    MaxPointerFileBytes,
                    "derived artifact epoch pointer",
                    cancellationToken
                )
                .ConfigureAwait(false);
            DerivedArtifactEpochLatestPointer pointer =
                MaterializeEpochPointer(dto);
            RequireFileName(path, ComputeKeyFileName(pointer.LineageKey, pointer.CoherenceGroup));
            epochPointers.Add(pointer);
        }
        return new DerivedArtifactEpochInventory(
            Freeze(configs, static item => item.ConfigId),
            Freeze(configPointers, static item =>
                $"{item.LineageKey}\0{item.CoherenceGroup}"),
            Freeze(epochs, static item => item.EpochId),
            Freeze(epochPointers, static item =>
                $"{item.LineageKey}\0{item.CoherenceGroup}")
        );
    }

    public async ValueTask<DerivedArtifactPlannerConfig?>
        RebuildCurrentConfigPointerAsync(
        DerivedArtifactPlannerKey key,
        CancellationToken cancellationToken = default
    ) {
        ValidateKey(key);
        await using FileStream writeLock = await _repository
            .AcquireWriteLockAsync(cancellationToken)
            .ConfigureAwait(false);
        EnsureDirectories();
        DerivedArtifactEpochInventory inventory =
            await ReadInventoryAsync(cancellationToken)
                .ConfigureAwait(false);
        DerivedArtifactPlannerConfig? tip = SelectUniqueTip(
            inventory.Configs.Where(config => config.Key == key),
            static config => config.ConfigId,
            static config => config.PreviousConfigId,
            "planner config"
        );
        if (tip is null) { return null; }
        await WritePointerAsync(
                GetConfigPointerPath(key),
                new PlannerConfigPointerDto(
                    ConfigPointerSchema,
                    key.LineageKey,
                    key.CoherenceGroup,
                    tip.ConfigId
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
        return tip;
    }

    public async ValueTask<DerivedArtifactEpochPlan?>
        RebuildLatestEpochPointerAsync(
        DerivedArtifactPlannerKey key,
        CancellationToken cancellationToken = default
    ) {
        using SessionJournalEngine engine =
            SessionJournalEngine.Open(
                _repository.SessionJournalRepositoryPath
            );
        return await RebuildLatestEpochPointerAsync(
                engine,
                key,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async ValueTask<DerivedArtifactEpochPlan?>
        RebuildLatestEpochPointerAsync(
        SessionJournalEngine engine,
        DerivedArtifactPlannerKey key,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        RequireMatchingRepository(engine);
        ValidateKey(key);
        await using FileStream writeLock = await _repository
            .AcquireWriteLockAsync(cancellationToken)
            .ConfigureAwait(false);
        EnsureDirectories();
        DerivedArtifactEpochInventory inventory =
            await ReadInventoryAsync(cancellationToken)
                .ConfigureAwait(false);
        DerivedArtifactEpochPlan? tip = SelectUniqueTip(
            inventory.Epochs.Where(epoch => epoch.Key == key),
            static epoch => epoch.EpochId,
            static epoch => epoch.PreviousEpochId,
            "derived artifact epoch"
        );
        if (tip is null) { return null; }
        IReadOnlyList<DerivedMemoryArtifact> artifacts =
            await _repository.Artifacts.ReadInventoryStrictAsync(
                    cancellationToken
                )
                .ConfigureAwait(false);
        DerivedArtifactSetInventory sets =
            await _repository.ArtifactSets.ReadInventoryAsync(
                    artifacts.ToDictionary(
                        static artifact => artifact.ArtifactId,
                        StringComparer.Ordinal
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
        DerivedArtifactEpochLatestPointer syntheticPointer = new(
            key.LineageKey,
            key.CoherenceGroup,
            tip.EpochId
        );
        DerivedArtifactEpochInventory validationInventory =
            inventory with {
                LatestEpochs = [
                    .. inventory.LatestEpochs.Where(pointer =>
                        pointer.LineageKey != key.LineageKey
                        || pointer.CoherenceGroup
                            != key.CoherenceGroup),
                    syntheticPointer
                ]
            };
        ValidateInventory(
            validationInventory,
            sets.Sets.ToDictionary(
                static set => set.SetId,
                StringComparer.Ordinal
            )
        );
        _ = ValidateRawAuthority(
            engine,
            validationInventory.Epochs.Where(
                epoch => epoch.Key == key
            ),
            inventory.Configs,
            cancellationToken
        );
        await WritePointerAsync(
                GetEpochPointerPath(key),
                new EpochPointerDto(
                    EpochPointerSchema,
                    key.LineageKey,
                    key.CoherenceGroup,
                    tip.EpochId
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
        return tip;
    }

    internal SessionCurrentLineageSnapshot ValidateRawAuthority(
        SessionJournalEngine engine,
        IEnumerable<DerivedArtifactEpochPlan> epochs,
        IEnumerable<DerivedArtifactPlannerConfig> configs,
        CancellationToken cancellationToken = default
    ) => ValidateRawAuthorityDetailed(
        engine,
        epochs,
        configs,
        cancellationToken
    ).Lineage;

    internal DerivedArtifactEpochRawAuthorityValidation
        ValidateRawAuthorityDetailed(
        SessionJournalEngine engine,
        IEnumerable<DerivedArtifactEpochPlan> epochs,
        IEnumerable<DerivedArtifactPlannerConfig> configs,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(epochs);
        ArgumentNullException.ThrowIfNull(configs);
        RequireMatchingRepository(engine);
        DerivedArtifactEpochPlan[] materialized = [.. epochs];
        IReadOnlyDictionary<string, DerivedArtifactPlannerConfig>
            configsById = configs.ToDictionary(
                static config => config.ConfigId,
                StringComparer.Ordinal
            );
        SessionHistoryPlanningSeedBatch seedBatch =
            engine.ReadHistoryPlanningSeeds(
                materialized.Select(
                    static epoch => epoch.SourceStartExclusive
                ),
                cancellationToken
            );
        SessionCurrentLineageSnapshot snapshot =
            seedBatch.Lineage;
        IReadOnlyDictionary<EventAddress, SessionHistoryPlanningSeed>
            seedsByAddress = seedBatch.Seeds.ToDictionary(
                static seed => seed.Address
            );
        var positions = snapshot.HeadToRoot
            .Select(static (header, index) => (
                header.Address,
                Index: index
            ))
            .ToDictionary(
                static item => item.Address,
                static item => item.Index
            );
        var endSetupsByEpochId =
            new Dictionary<
                string,
                SessionContextAnchorSetupReferences
            >(StringComparer.Ordinal);
        foreach (DerivedArtifactEpochPlan epoch in materialized) {
            ValidateKey(epoch.Key);
            if (!positions.TryGetValue(
                    epoch.SourceStartExclusive,
                    out int startPosition
                )
                || !positions.TryGetValue(
                    epoch.SourceEndInclusive,
                    out int endPosition
                )
                || !positions.TryGetValue(
                    epoch.PlannedAtRawHead,
                    out int plannedPosition
                )) {
                throw new InvalidDataException(
                    $"Epoch '{epoch.EpochId}' references raw addresses outside the current main lineage."
                );
            }
            if (startPosition <= endPosition
                || endPosition < plannedPosition) {
                throw new InvalidDataException(
                    $"Epoch '{epoch.EpochId}' raw interval ordering is invalid."
                );
            }
            if (epoch.PreviousEpochId is null
                && snapshot.HeadToRoot[startPosition].Kind
                    != SessionEventKind.SessionCreated) {
                throw new InvalidDataException(
                    $"Genesis epoch '{epoch.EpochId}' does not start at SessionCreated."
                );
            }
            SessionHistoryPlanningWindow window =
                engine.ReadHistoryPlanningWindowAt(
                    epoch.PlannedAtRawHead,
                    seedsByAddress[epoch.SourceStartExclusive],
                    cancellationToken
                );
            if (window.StartExclusive
                != epoch.SourceStartExclusive) {
                throw new InvalidDataException(
                    $"Epoch '{epoch.EpochId}' does not start at the authoritative planning boundary."
                );
            }
            if (window.StartSetups != epoch.RawStartSetups) {
                throw new InvalidDataException(
                    $"Epoch '{epoch.EpochId}' raw-start setup references do not match the authoritative main lineage."
                );
            }
            if (!window.ReplaySafeBoundarySetups.TryGetValue(
                    epoch.SourceEndInclusive,
                    out SessionContextAnchorSetupReferences?
                        sourceEndSetups
                )) {
                throw new InvalidDataException(
                    $"Epoch '{epoch.EpochId}' source end is not a replay-safe planning boundary."
                );
            }
            endSetupsByEpochId.Add(
                epoch.EpochId,
                sourceEndSetups
            );
            if (!configsById.TryGetValue(
                    epoch.ConfigId,
                    out DerivedArtifactPlannerConfig? config
                )) {
                throw new InvalidDataException(
                    $"Epoch '{epoch.EpochId}' references missing planner config '{epoch.ConfigId}'."
                );
            }
            long[] unitCosts = window.Units
                .Select(static unit => SessionHistoryTokenEstimator.Estimate(unit.Message))
                .ToArray();
            long totalTokens =
                SumChecked(unitCosts, unitCosts.Length);
            CandidateBoundary? selected =
                SelectBoundary(window, unitCosts, config);
            if (selected is null
                || selected.Address != epoch.SourceEndInclusive
                || selected.EligibleTokens != epoch.MeasuredTokens
                || epoch.PlanningDiagnostics.TotalTokens
                    != totalTokens
                || epoch.PlanningDiagnostics.EligibleTokens
                    != selected.EligibleTokens
                || epoch.PlanningDiagnostics.RetainedRecentTokens
                    != selected.RetainedTokens
                || epoch.PlanningDiagnostics.DependencyClosedUnitCount
                    != window.Units.Count
                || epoch.PlanningDiagnostics.ReplaySafeBoundaryCount
                    != window.ReplaySafeBoundaries.Count) {
                throw new InvalidDataException(
                    $"Epoch '{epoch.EpochId}' does not match the authoritative planner selection."
                );
            }
        }
        return new DerivedArtifactEpochRawAuthorityValidation(
            snapshot,
            new System.Collections.ObjectModel.ReadOnlyDictionary<
                string,
                SessionContextAnchorSetupReferences
            >(endSetupsByEpochId)
        );
    }

    internal static void ValidateInventory(
        DerivedArtifactEpochInventory inventory,
        IReadOnlyDictionary<string, DerivedArtifactSet>? artifactSetsById =
            null
    ) {
        var configs = inventory.Configs.ToDictionary(
            static config => config.ConfigId,
            StringComparer.Ordinal
        );
        foreach (IGrouping<DerivedArtifactPlannerKey, DerivedArtifactPlannerConfig>
                 group in inventory.Configs.GroupBy(static config => config.Key)) {
            DerivedArtifactPlannerConfig tip = ValidateUniqueLineage(
                group,
                static config => config.ConfigId,
                static config => config.PreviousConfigId,
                $"Planner config lineage '{FormatKey(group.Key)}'"
            );
            DerivedArtifactPlannerConfigPointer[] pointers = [
                .. inventory.CurrentConfigs.Where(pointer =>
                    pointer.LineageKey == group.Key.LineageKey
                    && pointer.CoherenceGroup == group.Key.CoherenceGroup)
            ];
            if (pointers.Length != 1
                || pointers[0].ConfigId != tip.ConfigId) {
                throw new InvalidDataException(
                    $"Planner config lineage '{FormatKey(group.Key)}' has a missing or stale current pointer."
                );
            }
        }
        var configPointerKeys = new HashSet<DerivedArtifactPlannerKey>();
        foreach (DerivedArtifactPlannerConfigPointer pointer in
                 inventory.CurrentConfigs) {
            var key = new DerivedArtifactPlannerKey(
                pointer.LineageKey,
                pointer.CoherenceGroup
            );
            if (!configPointerKeys.Add(key)
                || !configs.TryGetValue(pointer.ConfigId, out var config)
                || config.Key != key) {
                throw new InvalidDataException(
                    "Planner current-config pointer is duplicate, missing, or cross-key."
                );
            }
        }
        foreach (IGrouping<DerivedArtifactPlannerKey, DerivedArtifactPlannerConfig>
                 group in inventory.Configs.GroupBy(static config => config.Key)) {
            if (!configPointerKeys.Contains(group.Key)) {
                throw new InvalidDataException(
                    $"Planner key '{FormatKey(group.Key)}' has no current config pointer."
                );
            }
        }

        var epochs = inventory.Epochs.ToDictionary(
            static epoch => epoch.EpochId,
            StringComparer.Ordinal
        );
        var epochPointerKeys = new HashSet<DerivedArtifactPlannerKey>();
        foreach (IGrouping<DerivedArtifactPlannerKey, DerivedArtifactEpochPlan>
                 group in inventory.Epochs.GroupBy(static epoch => epoch.Key)) {
            foreach (DerivedArtifactEpochPlan epoch in group) {
                if (!configs.TryGetValue(
                        epoch.ConfigId,
                        out DerivedArtifactPlannerConfig? epochConfig
                    )
                    || epochConfig.Key != epoch.Key
                    || !string.Equals(
                        epochConfig.TopologyVersion,
                        epoch.TopologyVersion,
                        StringComparison.Ordinal
                    )) {
                    throw new InvalidDataException(
                        $"Epoch '{epoch.EpochId}' references a missing or incompatible config '{epoch.ConfigId}'."
                    );
                }
                if (epoch.PreviousEpochId is { } previous) {
                    if (!epochs.TryGetValue(previous, out var previousEpoch)
                        || previousEpoch.Key != group.Key
                        || previousEpoch.SourceEndInclusive
                            != epoch.SourceStartExclusive) {
                        throw new InvalidDataException(
                            $"Epoch '{epoch.EpochId}' has missing or incoherent previous epoch."
                        );
                    }
                    if (artifactSetsById is not null
                        && (!artifactSetsById.TryGetValue(
                                epoch.InputSetId!,
                                out DerivedArtifactSet? inputSet
                            )
                            || !string.Equals(
                                inputSet.LineageKey,
                                epoch.LineageKey,
                                StringComparison.Ordinal
                            )
                            || !string.Equals(
                                inputSet.CoherenceGroup,
                                epoch.CoherenceGroup,
                                StringComparison.Ordinal
                            )
                            || inputSet.CommonAnchor
                                != previousEpoch.SourceEndInclusive)) {
                        throw new InvalidDataException(
                            $"Epoch '{epoch.EpochId}' references a missing or incoherent input ArtifactSet."
                        );
                    }
                }
            }
            DerivedArtifactEpochPlan tip = ValidateUniqueLineage(
                group,
                static epoch => epoch.EpochId,
                static epoch => epoch.PreviousEpochId,
                $"Epoch lineage '{FormatKey(group.Key)}'"
            );
            DerivedArtifactEpochLatestPointer[] pointers = [
                .. inventory.LatestEpochs.Where(pointer =>
                    pointer.LineageKey == group.Key.LineageKey
                    && pointer.CoherenceGroup == group.Key.CoherenceGroup)
            ];
            if (pointers.Length != 1
                || pointers[0].EpochId != tip.EpochId) {
                throw new InvalidDataException(
                    $"Epoch lineage '{FormatKey(group.Key)}' has a missing or stale latest pointer."
                );
            }
            epochPointerKeys.Add(group.Key);
        }
        foreach (DerivedArtifactEpochLatestPointer pointer in
                 inventory.LatestEpochs) {
            var key = new DerivedArtifactPlannerKey(
                pointer.LineageKey,
                pointer.CoherenceGroup
            );
            if (!epochPointerKeys.Contains(key)
                || !epochs.TryGetValue(pointer.EpochId, out var epoch)
                || epoch.Key != key) {
                throw new InvalidDataException(
                    "Epoch latest pointer has no matching lineage."
                );
            }
        }
    }

    private async ValueTask ValidateInputSetAsync(
        DerivedArtifactEpochPlanningRequest request,
        DerivedArtifactEpochPlan? previous,
        DerivedArtifactPlannerKey key,
        CancellationToken cancellationToken
    ) {
        if (previous is null) {
            if (request.InputSetId is not null) {
                throw new ArgumentException(
                    "Genesis epoch must use the explicit empty-memory-pack policy and cannot bind an input set.",
                    nameof(request)
                );
            }
            return;
        }
        if (request.InputSetId is null) {
            throw new ArgumentException(
                "Every non-genesis epoch must bind one exact coherent input set.",
                nameof(request)
            );
        }
        DerivedArtifactSet set =
            await _repository.ArtifactSets.TryReadExactAsync(
                    request.InputSetId,
                    cancellationToken
                )
                .ConfigureAwait(false)
            ?? throw new InvalidDataException(
                $"Input ArtifactSet '{request.InputSetId}' is missing."
            );
        if (!string.Equals(set.LineageKey, key.LineageKey, StringComparison.Ordinal)
            || !string.Equals(set.CoherenceGroup, key.CoherenceGroup, StringComparison.Ordinal)
            || set.CommonAnchor != previous.SourceEndInclusive) {
            throw new InvalidDataException(
                "Input ArtifactSet does not match the planner lineage, coherence group, and previous epoch anchor."
            );
        }
    }

    private static CandidateBoundary? SelectBoundary(
        SessionHistoryPlanningWindow window,
        IReadOnlyList<long> unitCosts,
        DerivedArtifactPlannerConfig config
    ) {
        long total = SumChecked(unitCosts, unitCosts.Count);
        CandidateBoundary? selected = null;
        foreach (SessionHistoryPlanningBoundary boundary in
                 window.ReplaySafeBoundaries) {
            if (boundary.CompletedUnitCount < 0
                || boundary.CompletedUnitCount > unitCosts.Count) {
                throw new InvalidDataException(
                    "History planning window contains an invalid boundary unit count."
                );
            }
            long eligible = SumChecked(
                unitCosts,
                boundary.CompletedUnitCount
            );
            long retained = checked(total - eligible);
            if (eligible < config.EpochTriggerTokens
                || retained < config.MinimumRecentTokens) {
                continue;
            }
            if (selected is null
                || boundary.CompletedUnitCount
                    > selected.CompletedUnitCount
                || boundary.CompletedUnitCount
                    == selected.CompletedUnitCount) {
                selected = new CandidateBoundary(
                    boundary.Address,
                    boundary.CompletedUnitCount,
                    eligible,
                    retained
                );
            }
        }
        return selected;
    }

    private static DerivedArtifactEpochPlan CreateEpoch(
        DerivedArtifactPlannerConfig config,
        DerivedArtifactEpochPlanningRequest request,
        SessionHistoryPlanningWindow window,
        CandidateBoundary selected,
        SessionContextAnchorSetupReferences setups,
        DerivedArtifactEpochPlanningDiagnostics diagnostics
    ) {
        var identity = new EpochIdentityDto(
            EpochSchema,
            config.LineageKey,
            config.CoherenceGroup,
            config.TopologyVersion,
            config.ConfigId,
            request.ExpectedPreviousEpochId,
            request.InputSetId,
            EventAddressTextCodec.Format(window.ObservedRawHead),
            EventAddressTextCodec.Format(window.StartExclusive),
            EventAddressTextCodec.Format(selected.Address),
            ToDto(setups),
            selected.EligibleTokens
        );
        string epochId = "dae_" + ComputeHash(
            EpochIdDomain,
            JsonSerializer.SerializeToUtf8Bytes(
                identity,
                IdentityJsonOptions
            )
        );
        return new DerivedArtifactEpochPlan(
            epochId,
            config.LineageKey,
            config.CoherenceGroup,
            config.TopologyVersion,
            config.ConfigId,
            request.ExpectedPreviousEpochId,
            request.InputSetId,
            window.ObservedRawHead,
            window.StartExclusive,
            selected.Address,
            setups,
            selected.EligibleTokens,
            diagnostics
        );
    }

    private static void RequireSameEpochIdentity(
        DerivedArtifactEpochPlan durable,
        DerivedArtifactEpochPlan candidate
    ) {
        if (durable.EpochId != candidate.EpochId
            || durable.LineageKey != candidate.LineageKey
            || durable.CoherenceGroup != candidate.CoherenceGroup
            || durable.TopologyVersion != candidate.TopologyVersion
            || durable.ConfigId != candidate.ConfigId
            || durable.PreviousEpochId != candidate.PreviousEpochId
            || durable.InputSetId != candidate.InputSetId
            || durable.PlannedAtRawHead
                != candidate.PlannedAtRawHead
            || durable.SourceStartExclusive
                != candidate.SourceStartExclusive
            || durable.SourceEndInclusive
                != candidate.SourceEndInclusive
            || durable.RawStartSetups != candidate.RawStartSetups
            || durable.MeasuredTokens != candidate.MeasuredTokens
            || durable.PlanningDiagnostics.DecodedEventCount
                != candidate.PlanningDiagnostics.DecodedEventCount
            || durable.PlanningDiagnostics.DependencyClosedUnitCount
                != candidate.PlanningDiagnostics.DependencyClosedUnitCount
            || durable.PlanningDiagnostics.ReplaySafeBoundaryCount
                != candidate.PlanningDiagnostics.ReplaySafeBoundaryCount
            || durable.PlanningDiagnostics.TotalTokens
                != candidate.PlanningDiagnostics.TotalTokens
            || durable.PlanningDiagnostics.EligibleTokens
                != candidate.PlanningDiagnostics.EligibleTokens
            || durable.PlanningDiagnostics.RetainedRecentTokens
                != candidate.PlanningDiagnostics.RetainedRecentTokens) {
            throw new InvalidDataException(
                $"Durable epoch '{candidate.EpochId}' has a conflicting immutable identity."
            );
        }
    }

    private static void ValidateConfigDefinition(
        DerivedArtifactPlannerConfigDefinition definition
    ) {
        ArgumentNullException.ThrowIfNull(definition);
        ValidateKey(new DerivedArtifactPlannerKey(
            definition.LineageKey,
            definition.CoherenceGroup
        ));
        ValidateToken(definition.TopologyVersion, nameof(definition.TopologyVersion));
        RequireKnown(definition.TokenEstimatorId, TokenEstimatorId, nameof(definition.TokenEstimatorId));
        RequireKnown(definition.BoundaryPolicyId, BoundaryPolicyId, nameof(definition.BoundaryPolicyId));
        RequireKnown(definition.HardLimitPolicyId, HardLimitPolicyId, nameof(definition.HardLimitPolicyId));
        RequireKnown(definition.GenesisPolicyId, GenesisPolicyId, nameof(definition.GenesisPolicyId));
        long minimumHardLimit;
        try {
            minimumHardLimit = checked(
                definition.MinimumRecentTokens
                + definition.EpochTriggerTokens
                + definition.SchedulingHeadroomTokens
            );
        }
        catch (OverflowException) {
            throw new ArgumentOutOfRangeException(
                nameof(definition),
                "Planner token budgets overflow the supported signed 64-bit cost range."
            );
        }
        if (definition.MinimumRecentTokens <= 0
            || definition.EpochTriggerTokens <= 0
            || definition.SchedulingHeadroomTokens < 0
            || definition.HardLimitTokens <= minimumHardLimit) {
            throw new ArgumentOutOfRangeException(
                nameof(definition),
                "Planner token budgets must be positive and hardLimit must exceed minimumRecent + epochTrigger + schedulingHeadroom."
            );
        }
    }

    private static DerivedArtifactPlannerConfig CreateConfig(
        DerivedArtifactPlannerConfigDefinition definition,
        string? previousConfigId
    ) {
        ValidateConfigDefinition(definition);
        if (previousConfigId is not null) {
            ValidateConfigId(previousConfigId);
        }
        var identity = new PlannerConfigIdentityDto(
            ConfigSchema,
            definition.LineageKey,
            definition.CoherenceGroup,
            previousConfigId,
            definition.TopologyVersion,
            definition.MinimumRecentTokens,
            definition.EpochTriggerTokens,
            definition.SchedulingHeadroomTokens,
            definition.HardLimitTokens,
            definition.TokenEstimatorId,
            definition.BoundaryPolicyId,
            definition.HardLimitPolicyId,
            definition.GenesisPolicyId
        );
        string id = "dpc_" + ComputeHash(
            ConfigIdDomain,
            JsonSerializer.SerializeToUtf8Bytes(
                identity,
                IdentityJsonOptions
            )
        );
        return new DerivedArtifactPlannerConfig(
            id,
            definition.LineageKey,
            definition.CoherenceGroup,
            previousConfigId,
            definition.TopologyVersion,
            definition.MinimumRecentTokens,
            definition.EpochTriggerTokens,
            definition.SchedulingHeadroomTokens,
            definition.HardLimitTokens,
            definition.TokenEstimatorId,
            definition.BoundaryPolicyId,
            definition.HardLimitPolicyId,
            definition.GenesisPolicyId
        );
    }

    private static bool HasSameDefinition(
        DerivedArtifactPlannerConfig current,
        DerivedArtifactPlannerConfigDefinition definition
    ) => current.LineageKey == definition.LineageKey
        && current.CoherenceGroup == definition.CoherenceGroup
        && current.TopologyVersion == definition.TopologyVersion
        && current.MinimumRecentTokens == definition.MinimumRecentTokens
        && current.EpochTriggerTokens == definition.EpochTriggerTokens
        && current.SchedulingHeadroomTokens
            == definition.SchedulingHeadroomTokens
        && current.HardLimitTokens == definition.HardLimitTokens
        && current.TokenEstimatorId == definition.TokenEstimatorId
        && current.BoundaryPolicyId == definition.BoundaryPolicyId
        && current.HardLimitPolicyId == definition.HardLimitPolicyId
        && current.GenesisPolicyId == definition.GenesisPolicyId;

    private static DerivedArtifactEpochPlanningDiagnostics CreateDiagnostics(
        SessionHistoryPlanningWindow window,
        long total,
        long eligible,
        long retained
    ) => new(
        window.Diagnostics.HeaderVisits,
        window.Diagnostics.PayloadReads,
        window.Diagnostics.DecodedPayloadBytes,
        window.Diagnostics.DecodedEventCount,
        window.Units.Count,
        window.ReplaySafeBoundaries.Count,
        total,
        eligible,
        retained
    );

    private static void ValidatePlanningRequest(
        DerivedArtifactEpochPlanningRequest request
    ) {
        if (request.ExpectedPreviousEpochId is not null) {
            ValidateEpochId(request.ExpectedPreviousEpochId);
        }
        if (request.InputSetId is not null
            && (!request.InputSetId.StartsWith("das_", StringComparison.Ordinal)
                || request.InputSetId.Length != 68)) {
            throw new ArgumentException(
                "Input set id is invalid.",
                nameof(request)
            );
        }
        if ((request.ExpectedPreviousEpochId is null)
            != (request.InputSetId is null)) {
            throw new ArgumentException(
                "Genesis requires both previous epoch and input set to be null; non-genesis requires both.",
                nameof(request)
            );
        }
    }

    private void EnsureDirectories() {
        _repository.EnsureDirectory(ConfigsDirectory);
        _repository.EnsureDirectory(CurrentConfigsDirectory);
        _repository.EnsureDirectory(EpochsDirectory);
        _repository.EnsureDirectory(LatestEpochsDirectory);
    }

    private async ValueTask<DerivedArtifactPlannerConfigPointer?>
        TryReadConfigPointerAsync(
        DerivedArtifactPlannerKey key,
        CancellationToken cancellationToken
    ) {
        string path = GetConfigPointerPath(key);
        EnsureSafePointPath(path);
        if (!File.Exists(path)) { return null; }
        return MaterializeConfigPointer(
            await ReadDtoAsync<PlannerConfigPointerDto>(
                    path,
                    MaxPointerFileBytes,
                    "planner config pointer",
                    cancellationToken
                )
                .ConfigureAwait(false)
        );
    }

    private async ValueTask<DerivedArtifactEpochLatestPointer?>
        TryReadEpochPointerAsync(
        DerivedArtifactPlannerKey key,
        CancellationToken cancellationToken
    ) {
        string path = GetEpochPointerPath(key);
        EnsureSafePointPath(path);
        if (!File.Exists(path)) { return null; }
        return MaterializeEpochPointer(
            await ReadDtoAsync<EpochPointerDto>(
                    path,
                    MaxPointerFileBytes,
                    "epoch latest pointer",
                    cancellationToken
                )
                .ConfigureAwait(false)
        );
    }

    private async ValueTask<DerivedArtifactPlannerConfig>
        ReadConfigRequiredAsync(
        string configId,
        CancellationToken cancellationToken
    ) {
        string path = GetConfigPath(configId);
        EnsureSafePointPath(path);
        if (!File.Exists(path)) {
            throw new InvalidDataException(
                $"Planner config '{configId}' is missing."
            );
        }
        DerivedArtifactPlannerConfig config = MaterializeConfig(
            await ReadDtoAsync<PlannerConfigDto>(
                    path,
                    MaxConfigFileBytes,
                    "planner config",
                    cancellationToken
                )
                .ConfigureAwait(false)
        );
        if (config.ConfigId != configId) {
            throw new InvalidDataException(
                $"Planner config filename/id mismatch for '{configId}'."
            );
        }
        return config;
    }

    private async ValueTask<DerivedArtifactEpochPlan> ReadEpochRequiredAsync(
        string epochId,
        CancellationToken cancellationToken
    ) {
        string path = GetEpochPath(epochId);
        EnsureSafePointPath(path);
        if (!File.Exists(path)) {
            throw new InvalidDataException(
                $"Derived artifact epoch '{epochId}' is missing."
            );
        }
        DerivedArtifactEpochPlan epoch = MaterializeEpoch(
            await ReadDtoAsync<EpochDto>(
                    path,
                    MaxEpochFileBytes,
                    "derived artifact epoch",
                    cancellationToken
                )
                .ConfigureAwait(false)
        );
        if (epoch.EpochId != epochId) {
            throw new InvalidDataException(
                $"Derived artifact epoch filename/id mismatch for '{epochId}'."
            );
        }
        return epoch;
    }

    private async ValueTask WriteImmutableAsync<T>(
        string path,
        T dto,
        long maxBytes,
        string description,
        CancellationToken cancellationToken
    ) {
        string json = JsonSerializer.Serialize(dto, JsonOptions);
        EnsureSize(json, maxBytes, description);
        EnsureSafePointPath(path);
        if (File.Exists(path)) {
            T existing = await ReadDtoAsync<T>(
                    path,
                    maxBytes,
                    description,
                    cancellationToken
                )
                .ConfigureAwait(false);
            string existingJson = JsonSerializer.Serialize(
                existing,
                IdentityJsonOptions
            );
            string candidateJson = JsonSerializer.Serialize(
                dto,
                IdentityJsonOptions
            );
            if (!string.Equals(
                    existingJson,
                    candidateJson,
                    StringComparison.Ordinal
                )) {
                throw new InvalidDataException(
                    $"Immutable {description} collision at '{path}'."
                );
            }
            return;
        }
        await _repository.WriteFileAtomicallyAsync(
                path,
                json,
                overwrite: false,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private async ValueTask WritePointerAsync<T>(
        string path,
        T dto,
        CancellationToken cancellationToken
    ) {
        string json = JsonSerializer.Serialize(dto, JsonOptions);
        EnsureSize(json, MaxPointerFileBytes, "planner pointer");
        EnsureSafePointPath(path);
        await _repository.WriteFileAtomicallyAsync(
                path,
                json,
                overwrite: true,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private void EnsureSafePointPath(string path) =>
        DerivedMemoryPathGuard.EnsureSafeDescendant(
            _repository.SessionJournalRepositoryPath,
            path
        );

    private void RequireMatchingRepository(SessionJournalEngine engine) {
        string enginePath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(engine.Path)
        );
        string repositoryPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(_repository.SessionJournalRepositoryPath)
        );
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(
                enginePath,
                repositoryPath,
                comparison
            )) {
            throw new ArgumentException(
                "SessionJournal engine belongs to a different repository.",
                nameof(engine)
            );
        }
    }

    private async ValueTask<T> ReadDtoAsync<T>(
        string path,
        long maxBytes,
        string description,
        CancellationToken cancellationToken
    ) {
        try {
            DerivedMemoryPathGuard.EnsureSafeDescendant(
                _repository.SessionJournalRepositoryPath,
                path
            );
            await using FileStream stream = File.OpenRead(path);
            if (stream.Length > maxBytes) {
                throw new InvalidDataException(
                    $"{description} exceeds its UTF-8 size limit: {path}"
                );
            }
            return await JsonSerializer.DeserializeAsync<T>(
                    stream,
                    JsonOptions,
                    cancellationToken
                )
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    $"{description} is empty: {path}"
                );
        }
        catch (JsonException exception) {
            throw new InvalidDataException(
                $"{description} is malformed: {path}",
                exception
            );
        }
    }

    private IEnumerable<string> EnumerateJson(string directory) {
        DerivedMemoryPathGuard.EnsureSafeDescendant(
            _repository.SessionJournalRepositoryPath,
            directory
        );
        if (!Directory.Exists(directory)) { yield break; }
        foreach (string path in Directory
                     .EnumerateFiles(directory)
                     .OrderBy(static value => Path.GetFileName(value), StringComparer.Ordinal)) {
            if (!string.Equals(
                    Path.GetExtension(path),
                    ".json",
                    StringComparison.Ordinal
                )) {
                throw new InvalidDataException(
                    $"Derived planner directory contains unexpected file: {path}"
                );
            }
            yield return path;
        }
    }

    private static DerivedArtifactPlannerConfig MaterializeConfig(
        PlannerConfigDto dto
    ) {
        if (dto.Schema != ConfigSchema) {
            throw new InvalidDataException("Planner config schema is invalid.");
        }
        var definition = new DerivedArtifactPlannerConfigDefinition(
                dto.LineageKey,
                dto.CoherenceGroup,
                dto.TopologyVersion,
                dto.MinimumRecentTokens,
                dto.EpochTriggerTokens,
                dto.SchedulingHeadroomTokens,
                dto.HardLimitTokens,
                dto.TokenEstimatorId,
                dto.BoundaryPolicyId,
                dto.HardLimitPolicyId,
                dto.GenesisPolicyId
            );
        DerivedArtifactPlannerConfig computed =
            CreateConfig(definition, dto.PreviousConfigId);
        if (computed.ConfigId != dto.ConfigId) {
            throw new InvalidDataException(
                $"Planner config '{dto.ConfigId}' identity hash is invalid."
            );
        }
        return computed;
    }

    private static DerivedArtifactPlannerConfigPointer
        MaterializeConfigPointer(PlannerConfigPointerDto dto) {
        if (dto.Schema != ConfigPointerSchema) {
            throw new InvalidDataException(
                "Planner config pointer schema is invalid."
            );
        }
        ValidateKey(new(dto.LineageKey, dto.CoherenceGroup));
        ValidateConfigId(dto.ConfigId);
        return new(
            dto.LineageKey,
            dto.CoherenceGroup,
            dto.ConfigId
        );
    }

    private static DerivedArtifactEpochLatestPointer
        MaterializeEpochPointer(EpochPointerDto dto) {
        if (dto.Schema != EpochPointerSchema) {
            throw new InvalidDataException(
                "Epoch latest pointer schema is invalid."
            );
        }
        ValidateKey(new(dto.LineageKey, dto.CoherenceGroup));
        ValidateEpochId(dto.EpochId);
        return new(dto.LineageKey, dto.CoherenceGroup, dto.EpochId);
    }

    private static DerivedArtifactEpochPlan MaterializeEpoch(EpochDto dto) {
        if (dto.Schema != EpochSchema) {
            throw new InvalidDataException("Epoch schema is invalid.");
        }
        ValidateEpochId(dto.EpochId);
        ValidateKey(new(dto.LineageKey, dto.CoherenceGroup));
        ValidateToken(dto.TopologyVersion, nameof(dto.TopologyVersion));
        ValidateConfigId(dto.ConfigId);
        if (dto.PreviousEpochId is not null) {
            ValidateEpochId(dto.PreviousEpochId);
        }
        if (dto.InputSetId is not null
            && (dto.InputSetId.Length != 68
                || !dto.InputSetId.StartsWith(
                    "das_",
                    StringComparison.Ordinal
                )
                || dto.InputSetId.AsSpan(4).IndexOfAnyExcept(
                    "0123456789abcdef"
                ) >= 0)) {
            throw new InvalidDataException(
                "Epoch input ArtifactSet id is invalid."
            );
        }
        EventAddress plannedAt = EventAddressTextCodec.Parse(dto.PlannedAtRawHead);
        EventAddress start = EventAddressTextCodec.Parse(dto.SourceStartExclusive);
        EventAddress end = EventAddressTextCodec.Parse(dto.SourceEndInclusive);
        SessionContextAnchorSetupReferences setups =
            FromDto(dto.RawStartSetups);
        if (setups.RuntimeConfig.Address
            == setups.SystemPrompt.Address) {
            throw new InvalidDataException(
                "Epoch setup references must be distinct."
            );
        }
        DerivedArtifactEpochPlanningDiagnostics diagnostics =
            FromDto(dto.PlanningDiagnostics);
        var identity = new EpochIdentityDto(
            EpochSchema,
            dto.LineageKey,
            dto.CoherenceGroup,
            dto.TopologyVersion,
            dto.ConfigId,
            dto.PreviousEpochId,
            dto.InputSetId,
            dto.PlannedAtRawHead,
            dto.SourceStartExclusive,
            dto.SourceEndInclusive,
            dto.RawStartSetups,
            dto.MeasuredTokens
        );
        string expected = "dae_" + ComputeHash(
            EpochIdDomain,
            JsonSerializer.SerializeToUtf8Bytes(
                identity,
                IdentityJsonOptions
            )
        );
        if (expected != dto.EpochId
            || dto.MeasuredTokens <= 0
            || diagnostics.EligibleTokens != dto.MeasuredTokens
            || (dto.PreviousEpochId is null) != (dto.InputSetId is null)) {
            throw new InvalidDataException(
                $"Epoch '{dto.EpochId}' identity or invariant is invalid."
            );
        }
        return new(
            dto.EpochId,
            dto.LineageKey,
            dto.CoherenceGroup,
            dto.TopologyVersion,
            dto.ConfigId,
            dto.PreviousEpochId,
            dto.InputSetId,
            plannedAt,
            start,
            end,
            setups,
            dto.MeasuredTokens,
            diagnostics
        );
    }

    private static PlannerConfigDto ToDto(
        DerivedArtifactPlannerConfig config
    ) => new(
        ConfigSchema,
        config.ConfigId,
        config.LineageKey,
        config.CoherenceGroup,
        config.PreviousConfigId,
        config.TopologyVersion,
        config.MinimumRecentTokens,
        config.EpochTriggerTokens,
        config.SchedulingHeadroomTokens,
        config.HardLimitTokens,
        config.TokenEstimatorId,
        config.BoundaryPolicyId,
        config.HardLimitPolicyId,
        config.GenesisPolicyId
    );

    private static EpochDto ToDto(DerivedArtifactEpochPlan epoch) => new(
        EpochSchema,
        epoch.EpochId,
        epoch.LineageKey,
        epoch.CoherenceGroup,
        epoch.TopologyVersion,
        epoch.ConfigId,
        epoch.PreviousEpochId,
        epoch.InputSetId,
        EventAddressTextCodec.Format(epoch.PlannedAtRawHead),
        EventAddressTextCodec.Format(epoch.SourceStartExclusive),
        EventAddressTextCodec.Format(epoch.SourceEndInclusive),
        ToDto(epoch.RawStartSetups),
        epoch.MeasuredTokens,
        ToDto(epoch.PlanningDiagnostics)
    );

    private static SetupReferencesDto ToDto(
        SessionContextAnchorSetupReferences value
    ) => new(
        ToDto(value.RuntimeConfig),
        ToDto(value.SystemPrompt)
    );

    private static SetupReferenceDto ToDto(
        SessionContextSetupReference value
    ) => new(
        EventAddressTextCodec.Format(value.Address),
        value.BodySchemaVersion,
        value.PayloadSha256
    );

    private static SessionContextAnchorSetupReferences FromDto(
        SetupReferencesDto value
    ) => new(
        FromDto(value.RuntimeConfig),
        FromDto(value.SystemPrompt)
    );

    private static SessionContextSetupReference FromDto(
        SetupReferenceDto value
    ) {
        EventAddress address = EventAddressTextCodec.Parse(value.Address);
        if (address == default
            || value.BodySchemaVersion <= 0
            || value.PayloadSha256 is not { Length: 64 }
            || value.PayloadSha256.Any(static character =>
                character is not (>= '0' and <= '9')
                    and not (>= 'a' and <= 'f'))) {
            throw new InvalidDataException(
                "Epoch setup reference is invalid."
            );
        }
        return new(
            address,
            value.BodySchemaVersion,
            value.PayloadSha256
        );
    }

    private static PlanningDiagnosticsDto ToDto(
        DerivedArtifactEpochPlanningDiagnostics value
    ) => new(
        value.HeaderVisits,
        value.PayloadReads,
        value.DecodedPayloadBytes,
        value.DecodedEventCount,
        value.DependencyClosedUnitCount,
        value.ReplaySafeBoundaryCount,
        value.TotalTokens,
        value.EligibleTokens,
        value.RetainedRecentTokens
    );

    private static DerivedArtifactEpochPlanningDiagnostics FromDto(
        PlanningDiagnosticsDto value
    ) {
        if (value.HeaderVisits < 0
            || value.PayloadReads < 0
            || value.DecodedPayloadBytes < 0
            || value.DecodedEventCount < 0
            || value.DependencyClosedUnitCount < 0
            || value.ReplaySafeBoundaryCount < 0
            || value.TotalTokens < 0
            || value.EligibleTokens < 0
            || value.RetainedRecentTokens < 0
            || value.EligibleTokens + value.RetainedRecentTokens
                != value.TotalTokens) {
            throw new InvalidDataException(
                "Epoch planning diagnostics are invalid."
            );
        }
        return new(
            value.HeaderVisits,
            value.PayloadReads,
            value.DecodedPayloadBytes,
            value.DecodedEventCount,
            value.DependencyClosedUnitCount,
            value.ReplaySafeBoundaryCount,
            value.TotalTokens,
            value.EligibleTokens,
            value.RetainedRecentTokens
        );
    }

    private string GetConfigPath(string id) =>
        Path.Combine(ConfigsDirectory, id + ".json");
    private string GetEpochPath(string id) =>
        Path.Combine(EpochsDirectory, id + ".json");
    private string GetConfigPointerPath(DerivedArtifactPlannerKey key) =>
        Path.Combine(
            CurrentConfigsDirectory,
            ComputeKeyFileName(key.LineageKey, key.CoherenceGroup) + ".json"
        );
    private string GetEpochPointerPath(DerivedArtifactPlannerKey key) =>
        Path.Combine(
            LatestEpochsDirectory,
            ComputeKeyFileName(key.LineageKey, key.CoherenceGroup) + ".json"
        );

    private static string ComputeKeyFileName(
        string lineage,
        string coherence
    ) => "planner_" + ComputeHash(
        KeyDomain,
        Encoding.UTF8.GetBytes(lineage + "\0" + coherence)
    );

    private static void RequireFileName(string path, string identity) {
        if (!string.Equals(
                Path.GetFileName(path),
                identity + ".json",
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                $"Derived planner filename does not match identity: {path}"
            );
        }
    }

    private static void ValidateKey(DerivedArtifactPlannerKey key) {
        ArgumentNullException.ThrowIfNull(key);
        DerivedArtifactSetPolicy.ValidateLineageKey(key.LineageKey);
        if (!string.Equals(
                key.LineageKey,
                SessionJournalDefaults.MainBranchName,
                StringComparison.Ordinal
            )) {
            throw new ArgumentException(
                "Derived artifact epoch planner v1 supports only the current main SessionJournal lineage.",
                nameof(key)
            );
        }
        DerivedArtifactSetPolicy.ValidateToken(
            key.CoherenceGroup,
            nameof(key.CoherenceGroup)
        );
    }

    private static void ValidateToken(string value, string parameterName) =>
        DerivedArtifactSetPolicy.ValidateToken(value, parameterName);

    private static void RequireKnown(
        string actual,
        string expected,
        string parameterName
    ) {
        if (!string.Equals(actual, expected, StringComparison.Ordinal)) {
            throw new ArgumentException(
                $"Unsupported {parameterName} '{actual}'.",
                parameterName
            );
        }
    }

    private static void ValidateConfigId(string value) =>
        ValidateHashId(value, "dpc_", nameof(value));
    private static void ValidateEpochId(string value) =>
        ValidateHashId(value, "dae_", nameof(value));

    private static void ValidateHashId(
        string value,
        string prefix,
        string parameterName
    ) {
        if (value.Length != prefix.Length + 64
            || !value.StartsWith(prefix, StringComparison.Ordinal)
            || value.AsSpan(prefix.Length).IndexOfAnyExcept(
                "0123456789abcdef"
            ) >= 0) {
            throw new ArgumentException(
                $"Derived identity must be {prefix}<lower-sha256>.",
                parameterName
            );
        }
    }

    private static long SumChecked(
        IReadOnlyList<long> values,
        int count
    ) {
        long result = 0;
        for (int index = 0; index < count; index++) {
            result = checked(result + values[index]);
        }
        return result;
    }

    private static string ComputeHash(
        string domain,
        ReadOnlySpan<byte> value
    ) {
        byte[] prefix = Encoding.UTF8.GetBytes(domain + "\0");
        byte[] buffer = new byte[checked(prefix.Length + value.Length)];
        prefix.CopyTo(buffer, 0);
        value.CopyTo(buffer.AsSpan(prefix.Length));
        return Convert.ToHexStringLower(SHA256.HashData(buffer));
    }

    private static void EnsureSize(
        string json,
        long limit,
        string description
    ) {
        if (Encoding.UTF8.GetByteCount(json) > limit) {
            throw new InvalidDataException(
                $"{description} exceeds its UTF-8 size limit."
            );
        }
    }

    private static IReadOnlyList<T> Freeze<T>(
        IEnumerable<T> source,
        Func<T, string> key
    ) => Array.AsReadOnly([
        .. source.OrderBy(key, StringComparer.Ordinal)
    ]);

    private static T? SelectUniqueTip<T>(
        IEnumerable<T> source,
        Func<T, string> id,
        Func<T, string?> previous,
        string description
    ) where T : class {
        T[] items = [.. source];
        if (items.Length == 0) { return null; }
        return ValidateUniqueLineage(
            items,
            id,
            previous,
            $"{description} lineage"
        );
    }

    private static T ValidateUniqueLineage<T>(
        IEnumerable<T> source,
        Func<T, string> id,
        Func<T, string?> previous,
        string description
    ) where T : class {
        T[] items = [.. source];
        if (items.Length == 0) {
            throw new ArgumentException(
                "Lineage validation requires at least one item.",
                nameof(source)
            );
        }
        var byId = items.ToDictionary(id, StringComparer.Ordinal);
        var predecessorIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (T item in items) {
            if (previous(item) is { } predecessor) {
                if (!byId.ContainsKey(predecessor)) {
                    throw new InvalidDataException(
                        $"{description} lineage references missing predecessor '{predecessor}'."
                    );
                }
                predecessorIds.Add(predecessor);
            }
        }
        var completed = new HashSet<string>(StringComparer.Ordinal);
        foreach (T start in items) {
            var visiting = new HashSet<string>(StringComparer.Ordinal);
            T? cursor = start;
            while (cursor is not null
                   && !completed.Contains(id(cursor))) {
                string cursorId = id(cursor);
                if (!visiting.Add(cursorId)) {
                    throw new InvalidDataException(
                        $"{description} contains a cycle."
                    );
                }
                cursor = previous(cursor) is { } predecessor
                    ? byId[predecessor]
                    : null;
            }
            completed.UnionWith(visiting);
        }
        T[] tips = [
            .. items.Where(item => !predecessorIds.Contains(id(item)))
        ];
        if (tips.Length != 1) {
            throw new InvalidDataException(
                $"{description} is forked or disconnected."
            );
        }
        return tips[0];
    }

    private static string FormatKey(DerivedArtifactPlannerKey key) =>
        $"{key.LineageKey}|{key.CoherenceGroup}";

    private sealed record CandidateBoundary(
        EventAddress Address,
        int CompletedUnitCount,
        long EligibleTokens,
        long RetainedTokens
    );

    private sealed record PlannerConfigIdentityDto(
        string Schema,
        string LineageKey,
        string CoherenceGroup,
        string? PreviousConfigId,
        string TopologyVersion,
        long MinimumRecentTokens,
        long EpochTriggerTokens,
        long SchedulingHeadroomTokens,
        long HardLimitTokens,
        string TokenEstimatorId,
        string BoundaryPolicyId,
        string HardLimitPolicyId,
        string GenesisPolicyId
    );

    private sealed record PlannerConfigDto(
        string Schema,
        string ConfigId,
        string LineageKey,
        string CoherenceGroup,
        string? PreviousConfigId,
        string TopologyVersion,
        long MinimumRecentTokens,
        long EpochTriggerTokens,
        long SchedulingHeadroomTokens,
        long HardLimitTokens,
        string TokenEstimatorId,
        string BoundaryPolicyId,
        string HardLimitPolicyId,
        string GenesisPolicyId
    );

    private sealed record PlannerConfigPointerDto(
        string Schema,
        string LineageKey,
        string CoherenceGroup,
        string ConfigId
    );

    private sealed record EpochIdentityDto(
        string Schema,
        string LineageKey,
        string CoherenceGroup,
        string TopologyVersion,
        string ConfigId,
        string? PreviousEpochId,
        string? InputSetId,
        string PlannedAtRawHead,
        string SourceStartExclusive,
        string SourceEndInclusive,
        SetupReferencesDto RawStartSetups,
        long MeasuredTokens
    );

    private sealed record EpochDto(
        string Schema,
        string EpochId,
        string LineageKey,
        string CoherenceGroup,
        string TopologyVersion,
        string ConfigId,
        string? PreviousEpochId,
        string? InputSetId,
        string PlannedAtRawHead,
        string SourceStartExclusive,
        string SourceEndInclusive,
        SetupReferencesDto RawStartSetups,
        long MeasuredTokens,
        PlanningDiagnosticsDto PlanningDiagnostics
    );

    private sealed record EpochPointerDto(
        string Schema,
        string LineageKey,
        string CoherenceGroup,
        string EpochId
    );

    private sealed record SetupReferencesDto(
        SetupReferenceDto RuntimeConfig,
        SetupReferenceDto SystemPrompt
    );

    private sealed record SetupReferenceDto(
        string Address,
        int BodySchemaVersion,
        string PayloadSha256
    );

    private sealed record PlanningDiagnosticsDto(
        long HeaderVisits,
        long PayloadReads,
        long DecodedPayloadBytes,
        int DecodedEventCount,
        int DependencyClosedUnitCount,
        int ReplaySafeBoundaryCount,
        long TotalTokens,
        long EligibleTokens,
        long RetainedRecentTokens
    );
}
