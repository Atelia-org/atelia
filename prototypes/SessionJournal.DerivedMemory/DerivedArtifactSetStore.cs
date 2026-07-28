using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.EventJournal;

namespace Atelia.SessionJournal.DerivedMemory;

public sealed class DerivedArtifactSetStore {
    public const string SetSchema =
        "atelia.session-journal.derived-artifact-set.v3";
    public const string LatestPointerSchema =
        "atelia.session-journal.derived-artifact-set.latest-pointer.v3";
    public const long MaxSetFileBytes = 1024 * 1024;
    public const long MaxLatestPointerFileBytes = 64 * 1024;

    private const string SetIdDomain =
        "atelia.session-journal.derived-artifact-set-id.v3";
    private const string LatestKeyDomain =
        "atelia.session-journal.derived-artifact-set-latest-key.v2";
    private const int MaxMemberCount = 128;

    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new DerivedMemoryBranchRefIdJsonConverter() }
    };
    private static readonly JsonSerializerOptions IdentityJsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new DerivedMemoryBranchRefIdJsonConverter() }
    };

    private readonly DerivedMemoryRepository _repository;

    internal DerivedArtifactSetStore(DerivedMemoryRepository repository) {
        _repository = repository;
        SetsDirectory = Path.Combine(repository.MemoryRoot, "sets");
        LatestPointersDirectory = Path.Combine(
            repository.MemoryRoot,
            "indexes",
            "latest-sets"
        );
    }

    public string SetsDirectory { get; }

    public string LatestPointersDirectory { get; }

    public async ValueTask<DerivedArtifactSet> PreparePublicationAsync(
        SessionJournalEngine engine,
        DerivedArtifactSetPublicationRequest request,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Policy);
        ArgumentNullException.ThrowIfNull(request.Transaction);
        ArgumentNullException.ThrowIfNull(request.AnchorSetups);
        ArgumentNullException.ThrowIfNull(request.Members);
        IReadOnlyDictionary<string, DerivedArtifactSetRoleRequirement> roles =
            request.Policy.ValidateAndSnapshot();
        DerivedMemoryBranchScope scope = _repository.Bind(engine);
        ValidateTransaction(request, roles);
        DerivedArtifactSetPolicy.ValidateBranchRefId(request.BranchRefId);
        if (request.BranchRefId != scope.BranchRefId) {
            throw new ArgumentException(
                "ArtifactSet publication belongs to a different branch ref.",
                nameof(request)
            );
        }
        ValidateSetupReferences(request.AnchorSetups);
        if (request.ExpectedPreviousSetId is not null) {
            ValidateSetId(request.ExpectedPreviousSetId);
        }
        DerivedArtifactSetMemberSelection[] selections =
            SnapshotSelections(request.Members);
        _ = await ValidatePublicationClosureAsync(
                engine,
                request,
                selections,
                cancellationToken
            )
            .ConfigureAwait(false);
        return await CreateSetAsync(
                request,
                roles,
                selections,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    internal async ValueTask<DerivedArtifactSet>
        RebuildFinalizedCandidateAsync(
        DerivedMemoryOrchestrationTransaction transaction,
        DerivedMemoryOrchestrationFinalization finalization,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(finalization);
        var policy = new DerivedArtifactSetPolicy(
            transaction.PolicyId,
            transaction.PolicyFingerprint,
            transaction.CoherenceGroup,
            transaction.Roles.Select(static role =>
                new DerivedArtifactSetRoleRequirement(
                    role.RoleId,
                    role.Target,
                    role.Required
                )).ToArray()
        );
        var request = new DerivedArtifactSetPublicationRequest(
            policy,
            transaction,
            finalization.AnchorSetups,
            finalization.IncludedSettlements.Select(
                static settlement =>
                    new DerivedArtifactSetMemberSelection(
                        settlement.RoleId,
                        settlement.ArtifactId
                    )
            ).ToArray(),
            finalization.ExpectedPreviousSetId
        );
        IReadOnlyDictionary<string, DerivedArtifactSetRoleRequirement> roles =
            policy.ValidateAndSnapshot();
        ValidateTransaction(request, roles);
        ValidateSetupReferences(finalization.AnchorSetups);
        DerivedArtifactSet candidate = await CreateSetAsync(
                request,
                roles,
                SnapshotSelections(request.Members),
                cancellationToken
            )
            .ConfigureAwait(false);
        ValidateFinalizationCandidate(finalization, candidate);
        return candidate;
    }

    public async ValueTask<DerivedArtifactSet> PublishAsync(
        SessionJournalEngine engine,
        DerivedArtifactSetPublicationRequest request,
        CancellationToken cancellationToken = default
    ) {
        DerivedArtifactSet candidate =
            await PreparePublicationAsync(
                    engine,
                    request,
                    cancellationToken
                )
                .ConfigureAwait(false);
        DerivedMemoryOrchestrationFinalization finalization =
            await _repository.Orchestrations.TryReadFinalizationAsync(
                    request.Transaction,
                    cancellationToken
                )
                .ConfigureAwait(false)
            ?? throw new InvalidDataException(
                $"Orchestration transaction '{request.Transaction.TransactionId}' is not finalized."
            );
        ValidateFinalizationCandidate(finalization, candidate);

        await using FileStream writeLock = await _repository
            .AcquireWriteLockAsync(cancellationToken)
            .ConfigureAwait(false);
        _repository.EnsureDirectory(SetsDirectory);
        _repository.EnsureDirectory(LatestPointersDirectory);

        DerivedArtifactSetLatestPointerDto? currentPointer =
            await TryReadLatestPointerDtoAsync(
                    request.Policy,
                    request.BranchRefId,
                    cancellationToken
                )
                .ConfigureAwait(false);
        string? currentSetId = currentPointer?.SetId;

        if (string.Equals(
                currentSetId,
                candidate.SetId,
                StringComparison.Ordinal
            )) {
            DerivedArtifactSet existing = await ReadSetRequiredAsync(
                    candidate.SetId,
                    request.Policy,
                    request.BranchRefId,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (!SetsEquivalent(existing, candidate)) {
                throw new InvalidDataException(
                    $"ArtifactSet idempotent retry found a non-identical set '{candidate.SetId}'."
                );
            }
            return existing;
        }

        if (!string.Equals(
                currentSetId,
                request.ExpectedPreviousSetId,
                StringComparison.Ordinal
            )) {
            throw new DerivedArtifactSetConcurrencyException(
                "Derived ArtifactSet latest pointer changed. "
                + $"Expected '{request.ExpectedPreviousSetId ?? "<none>"}', "
                + $"observed '{currentSetId ?? "<none>"}'."
            );
        }

        string setPath = GetSetPath(candidate.SetId);
        var dto = ToDto(candidate);
        string serialized = JsonSerializer.Serialize(dto, JsonOptions);
        EnsureSerializedSize(
            serialized,
            MaxSetFileBytes,
            "Derived ArtifactSet"
        );
        if (File.Exists(setPath)) {
            DerivedArtifactSet existing = await ReadSetRequiredAsync(
                    candidate.SetId,
                    request.Policy,
                    request.BranchRefId,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (!SetsEquivalent(existing, candidate)) {
                throw new InvalidDataException(
                    $"Immutable ArtifactSet collision at '{candidate.SetId}'."
                );
            }
        }
        else {
            await _repository.WriteFileAtomicallyAsync(
                    setPath,
                    serialized,
                    overwrite: false,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        var pointer = new DerivedArtifactSetLatestPointerDto(
            LatestPointerSchema,
            request.BranchRefId,
            request.Policy.CoherenceGroup,
            request.Policy.PolicyId,
            request.Policy.PolicyFingerprint,
            candidate.SetId
        );
        string pointerJson = JsonSerializer.Serialize(pointer, JsonOptions);
        EnsureSerializedSize(
            pointerJson,
            MaxLatestPointerFileBytes,
            "Derived ArtifactSet latest pointer"
        );
        await _repository.WriteFileAtomicallyAsync(
                GetLatestPointerPath(
                    request.Policy,
                    request.BranchRefId
                ),
                pointerJson,
                overwrite: true,
                cancellationToken
            )
            .ConfigureAwait(false);
        return candidate;
    }

    private static void ValidateFinalizationCandidate(
        DerivedMemoryOrchestrationFinalization finalization,
        DerivedArtifactSet candidate
    ) {
        if (!string.Equals(
                finalization.ExpectedSetId,
                candidate.SetId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                finalization.TransactionId,
                candidate.TransactionId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                finalization.ExpectedPreviousSetId,
                candidate.PreviousSetId,
                StringComparison.Ordinal
            )
            || finalization.AnchorSetups != candidate.AnchorSetups) {
            throw new InvalidDataException(
                "ArtifactSet candidate does not match its durable finalization."
            );
        }
        DerivedArtifactSetMember[] members = [
            .. candidate.Members.OrderBy(
                static member => member.RoleId,
                StringComparer.Ordinal
            )
        ];
        DerivedMemoryRoleSettlement[] settlements = [
            .. finalization.IncludedSettlements.OrderBy(
                static settlement => settlement.RoleId,
                StringComparer.Ordinal
            )
        ];
        if (members.Length != settlements.Length
            || members.Where((member, index) =>
                    !string.Equals(
                        member.RoleId,
                        settlements[index].RoleId,
                        StringComparison.Ordinal
                    )
                    || !string.Equals(
                        member.ArtifactId,
                        settlements[index].ArtifactId,
                        StringComparison.Ordinal
                    )
                    || !string.Equals(
                        member.Outcome,
                        settlements[index].ArtifactOutcome,
                        StringComparison.Ordinal
                    ))
                .Any()) {
            throw new InvalidDataException(
                "ArtifactSet members do not match their durable finalization."
            );
        }
    }

    public async ValueTask<DerivedArtifactSet?> TryReadLatestAsync(
        DerivedArtifactSetPolicy policy,
        DerivedMemoryBranchScope scope,
        CancellationToken cancellationToken = default
    ) {
        _repository.RequireScope(scope);
        return await TryReadLatestAsync(
                policy,
                scope.BranchRefId,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    internal async ValueTask<DerivedArtifactSet?> TryReadLatestAsync(
        DerivedArtifactSetPolicy policy,
        RefId branchRefId,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(policy);
        _ = policy.ValidateAndSnapshot();
        DerivedArtifactSetPolicy.ValidateBranchRefId(branchRefId);
        DerivedArtifactSetLatestPointerDto? pointer =
            await TryReadLatestPointerDtoAsync(
                    policy,
                    branchRefId,
                    cancellationToken
                )
                .ConfigureAwait(false);
        return pointer is null
            ? null
            : await ReadSetRequiredAsync(
                    pointer.SetId,
                    policy,
                    branchRefId,
                    cancellationToken
                )
                .ConfigureAwait(false);
    }

    public async ValueTask<DerivedArtifactSet?> TryReadAsync(
        string setId,
        DerivedArtifactSetPolicy policy,
        DerivedMemoryBranchScope scope,
        CancellationToken cancellationToken = default
    ) {
        _repository.RequireScope(scope);
        return await TryReadAsync(
                setId,
                policy,
                scope.BranchRefId,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    internal async ValueTask<DerivedArtifactSet?> TryReadAsync(
        string setId,
        DerivedArtifactSetPolicy policy,
        RefId branchRefId,
        CancellationToken cancellationToken = default
    ) {
        ValidateSetId(setId);
        ArgumentNullException.ThrowIfNull(policy);
        _ = policy.ValidateAndSnapshot();
        DerivedArtifactSetPolicy.ValidateBranchRefId(branchRefId);
        string path = GetSetPath(setId);
        DerivedMemoryPathGuard.EnsureSafeDescendant(
            _repository.SessionJournalRepositoryPath,
            path
        );
        if (!File.Exists(path)) {
            return null;
        }
        return await ReadSetRequiredAsync(
                setId,
                policy,
                branchRefId,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async ValueTask<DerivedArtifactSetInventory> ReadInventoryAsync(
        CancellationToken cancellationToken = default
    ) => await ReadInventoryAsync(
            strictArtifactsById: null,
            cancellationToken
        )
        .ConfigureAwait(false);

    /// <summary>
    /// Reads one exact persisted set while validating its identity and every referenced artifact.
    /// This is intentionally independent of a current policy pointer.
    /// </summary>
    public async ValueTask<DerivedArtifactSet?> TryReadExactAsync(
        string setId,
        CancellationToken cancellationToken = default
    ) {
        ValidateSetId(setId);
        string path = GetSetPath(setId);
        DerivedMemoryPathGuard.EnsureSafeDescendant(
            _repository.SessionJournalRepositoryPath,
            path
        );
        if (!File.Exists(path)) {
            return null;
        }
        DerivedArtifactSetDto dto = await ReadDtoRequiredAsync(
                path,
                cancellationToken
            )
            .ConfigureAwait(false);
        DerivedArtifactSet set = MaterializeAndValidateSelf(dto);
        RequireExactSetFileName(path, set.SetId);
        if (!string.Equals(set.SetId, setId, StringComparison.Ordinal)) {
            throw new InvalidDataException(
                $"Derived ArtifactSet filename/id mismatch for '{setId}'."
            );
        }
        foreach (DerivedArtifactSetMember member in set.Members) {
            DerivedMemoryArtifact artifact =
                await _repository.Artifacts.TryReadArtifactAsync(
                        member.ArtifactId,
                        cancellationToken
                    )
                    .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    $"ArtifactSet member '{member.ArtifactId}' is missing or unusable."
                );
            ValidateArtifactAgainstMember(
                artifact,
                member,
                set.CommonAnchor,
                set.AnchorSetups
            );
        }
        return set;
    }

    internal async ValueTask<DerivedArtifactSetInventory> ReadInventoryAsync(
        IReadOnlyDictionary<string, DerivedMemoryArtifact>?
            strictArtifactsById,
        CancellationToken cancellationToken
    ) {
        var sets = new List<DerivedArtifactSet>();
        var memberArtifactCache =
            strictArtifactsById is null
                ? new Dictionary<string, DerivedMemoryArtifact>(
                    StringComparer.Ordinal
                )
                : null;
        DerivedMemoryPathGuard.EnsureSafeDescendant(
            _repository.SessionJournalRepositoryPath,
            SetsDirectory
        );
        if (Directory.Exists(SetsDirectory)) {
            foreach (string path in EnumeratePersistedJsonFiles(
                         SetsDirectory,
                         "Derived ArtifactSet"
                     )) {
                cancellationToken.ThrowIfCancellationRequested();
                DerivedArtifactSetDto dto = await ReadDtoRequiredAsync(
                        path,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                DerivedArtifactSet set = MaterializeAndValidateSelf(dto);
                RequireExactSetFileName(path, set.SetId);
                foreach (DerivedArtifactSetMember member in set.Members) {
                    DerivedMemoryArtifact artifact;
                    if (strictArtifactsById is not null) {
                        artifact = strictArtifactsById.TryGetValue(
                            member.ArtifactId,
                            out DerivedMemoryArtifact? strictArtifact
                        )
                            ? strictArtifact
                            : throw new InvalidDataException(
                                $"ArtifactSet member '{member.ArtifactId}' is missing."
                            );
                    }
                    else if (!memberArtifactCache!.TryGetValue(
                                 member.ArtifactId,
                                 out artifact!
                             )) {
                        artifact = await _repository.Artifacts
                            .TryReadArtifactAsync(
                                member.ArtifactId,
                                cancellationToken
                            )
                            .ConfigureAwait(false)
                            ?? throw new InvalidDataException(
                                $"ArtifactSet member '{member.ArtifactId}' is missing or unusable."
                            );
                        memberArtifactCache.Add(
                            member.ArtifactId,
                            artifact
                        );
                    }
                    ValidateArtifactAgainstMember(
                        artifact,
                        member,
                        set.CommonAnchor,
                        set.AnchorSetups
                    );
                }
                sets.Add(set);
            }
        }

        var pointers = new List<DerivedArtifactSetLatestPointer>();
        DerivedMemoryPathGuard.EnsureSafeDescendant(
            _repository.SessionJournalRepositoryPath,
            LatestPointersDirectory
        );
        if (Directory.Exists(LatestPointersDirectory)) {
            foreach (string path in EnumeratePersistedJsonFiles(
                         LatestPointersDirectory,
                         "Derived ArtifactSet latest pointer"
                     )) {
                cancellationToken.ThrowIfCancellationRequested();
                pointers.Add(
                    await ReadAndValidatePointerSelfAsync(
                            path,
                            cancellationToken
                        )
                        .ConfigureAwait(false)
                );
            }
        }

        return new DerivedArtifactSetInventory(
            Array.AsReadOnly([
                .. sets
                    .OrderBy(static set => set.BranchRefId.Packed)
                    .ThenBy(static set => set.CoherenceGroup, StringComparer.Ordinal)
                    .ThenBy(static set => set.PolicyId, StringComparer.Ordinal)
                    .ThenBy(static set => set.PolicyFingerprint, StringComparer.Ordinal)
                    .ThenBy(static set => set.SetId, StringComparer.Ordinal)
            ]),
            Array.AsReadOnly([
                .. pointers
                    .OrderBy(static pointer => pointer.BranchRefId.Packed)
                    .ThenBy(static pointer => pointer.CoherenceGroup, StringComparer.Ordinal)
                    .ThenBy(static pointer => pointer.PolicyId, StringComparer.Ordinal)
                    .ThenBy(static pointer => pointer.PolicyFingerprint, StringComparer.Ordinal)
                    .ThenBy(static pointer => pointer.SetId, StringComparer.Ordinal)
            ])
        );
    }

    public async ValueTask<DerivedArtifactSet?> RebuildLatestPointerAsync(
        SessionJournalEngine engine,
        DerivedArtifactSetPolicy policy,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        DerivedMemoryBranchScope scope = _repository.Bind(engine);
        DerivedArtifactEpochInventory inventory =
            await _repository.EpochPlanner.ReadInventoryAsync(
                    cancellationToken
                )
                .ConfigureAwait(false);
        DerivedArtifactEpochPlan[] epochs = [
            .. inventory.Epochs.Where(
                epoch => epoch.BranchRefId == scope.BranchRefId
            )
        ];
        if (epochs.Length == 0) {
            DerivedArtifactSet? existing = await TryReadLatestAsync(
                    policy,
                    scope.BranchRefId,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (existing is not null) {
                throw new InvalidDataException(
                    "An ArtifactSet lineage exists without a branch-scoped epoch lineage."
                );
            }
        }
        else {
            _ = DerivedMemoryEngineReadGate.Run(
                engine,
                () => _repository.EpochPlanner
                    .ValidateRawAuthorityDetailed(
                        engine,
                        epochs,
                        inventory.Configs.Where(
                            config => config.BranchRefId
                                == scope.BranchRefId
                        ),
                        cancellationToken
                    )
            );
        }
        return await RebuildLatestPointerAsync(
                policy,
                scope.BranchRefId,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    internal async ValueTask<DerivedArtifactSet?> RebuildLatestPointerAsync(
        DerivedArtifactSetPolicy policy,
        RefId branchRefId,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(policy);
        _ = policy.ValidateAndSnapshot();
        DerivedArtifactSetPolicy.ValidateBranchRefId(branchRefId);

        await using FileStream writeLock = await _repository
            .AcquireWriteLockAsync(cancellationToken)
            .ConfigureAwait(false);
        _repository.EnsureDirectory(SetsDirectory);
        _repository.EnsureDirectory(LatestPointersDirectory);

        var matching = new Dictionary<string, DerivedArtifactSet>(
            StringComparer.Ordinal
        );
        foreach (string path in Directory.EnumerateFiles(
                     SetsDirectory,
                     "das_*.json"
                 )) {
            cancellationToken.ThrowIfCancellationRequested();
            DerivedArtifactSetDto dto = await ReadDtoRequiredAsync(
                    path,
                    cancellationToken
                )
                .ConfigureAwait(false);
            RequireExactSetFileName(path, dto.SetId);
            if (dto.BranchRefId != branchRefId
                || !string.Equals(
                    dto.CoherenceGroup,
                    policy.CoherenceGroup,
                    StringComparison.Ordinal
                )
                || !string.Equals(
                    dto.PolicyId,
                    policy.PolicyId,
                    StringComparison.Ordinal
                )
                || !string.Equals(
                    dto.PolicyFingerprint,
                    policy.PolicyFingerprint,
                    StringComparison.Ordinal
                )) {
                continue;
            }
            DerivedArtifactSet set = MaterializeAndValidate(
                dto,
                policy,
                branchRefId
            );
            if (!matching.TryAdd(set.SetId, set)) {
                throw new InvalidDataException(
                    $"Duplicate ArtifactSet id '{set.SetId}'."
                );
            }
        }
        if (matching.Count == 0) {
            return null;
        }

        var predecessorIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (DerivedArtifactSet set in matching.Values) {
            if (set.PreviousSetId is { } previous) {
                if (!matching.ContainsKey(previous)) {
                    throw new InvalidDataException(
                        $"ArtifactSet '{set.SetId}' references missing previous set '{previous}'."
                    );
                }
                predecessorIds.Add(previous);
            }
        }
        DerivedArtifactSet[] tips = [
            .. matching.Values.Where(
                set => !predecessorIds.Contains(set.SetId)
            )
        ];
        if (tips.Length != 1) {
            throw new InvalidDataException(
                "Cannot rebuild latest ArtifactSet pointer from a forked or cyclic lineage."
            );
        }
        ValidateCompleteAcyclicLineage(tips[0], matching);

        var pointer = new DerivedArtifactSetLatestPointerDto(
            LatestPointerSchema,
            branchRefId,
            policy.CoherenceGroup,
            policy.PolicyId,
            policy.PolicyFingerprint,
            tips[0].SetId
        );
        string pointerJson = JsonSerializer.Serialize(pointer, JsonOptions);
        EnsureSerializedSize(
            pointerJson,
            MaxLatestPointerFileBytes,
            "Derived ArtifactSet latest pointer"
        );
        await _repository.WriteFileAtomicallyAsync(
                GetLatestPointerPath(policy, branchRefId),
                pointerJson,
                overwrite: true,
                cancellationToken
            )
            .ConfigureAwait(false);
        return tips[0];
    }

    internal async ValueTask<DerivedMemoryArtifact>
        ReadAndValidateMemberArtifactAsync(
        DerivedArtifactSet set,
        DerivedArtifactSetMember member,
        CancellationToken cancellationToken
    ) {
        DerivedMemoryArtifact artifact = await _repository.Artifacts
            .TryReadArtifactAsync(member.ArtifactId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException(
                $"ArtifactSet member '{member.ArtifactId}' is missing or unusable."
            );
        ValidateArtifactAgainstMember(
            artifact,
            member,
            set.CommonAnchor,
            set.AnchorSetups
        );
        return artifact;
    }

    private async ValueTask<DerivedArtifactEpochPlan>
        ValidatePublicationClosureAsync(
        SessionJournalEngine engine,
        DerivedArtifactSetPublicationRequest request,
        IReadOnlyList<DerivedArtifactSetMemberSelection> selections,
        CancellationToken cancellationToken
    ) {
        DerivedMemoryOrchestrationTransaction durableTransaction =
            await _repository.Orchestrations.TryReadTransactionAsync(
                    request.Transaction.TransactionId,
                    cancellationToken
                )
                .ConfigureAwait(false)
            ?? throw new InvalidDataException(
                $"Orchestration transaction '{request.Transaction.TransactionId}' is missing."
            );
        if (!DerivedMemoryOrchestrationStore.TransactionsEquivalent(
                durableTransaction,
                request.Transaction
            )) {
            throw new InvalidDataException(
                "ArtifactSet publication transaction does not match its durable snapshot."
            );
        }
        IReadOnlyList<DerivedMemoryRoleSettlement> settlements =
            await _repository.Orchestrations.ReadSettlementsAsync(
                    durableTransaction,
                    cancellationToken
                )
                .ConfigureAwait(false);
        IReadOnlyDictionary<string, DerivedMemoryRoleSettlement>
            settlementsByRole = settlements.ToDictionary(
                static item => item.RoleId,
                StringComparer.Ordinal
            );
        foreach (DerivedMemoryRoleProvisioning required in
                 durableTransaction.Roles.Where(
                     static role => role.Required
                 )) {
            if (!settlementsByRole.ContainsKey(required.RoleId)) {
                throw new InvalidDataException(
                    $"Required role '{required.RoleId}' is not durably settled."
                );
            }
        }
        foreach (DerivedArtifactSetMemberSelection selection in
                 selections) {
            if (!settlementsByRole.TryGetValue(
                    selection.RoleId,
                    out DerivedMemoryRoleSettlement? settlement
                )
                || !string.Equals(
                    settlement.ArtifactId,
                    selection.ArtifactId,
                    StringComparison.Ordinal
                )) {
                throw new InvalidDataException(
                    $"ArtifactSet member '{selection.RoleId}' is not the exact durable settlement."
                );
            }
        }
        DerivedArtifactEpochPlan epoch =
            await _repository.EpochPlanner.TryReadEpochAsync(
                    durableTransaction.EpochId,
                    cancellationToken
                )
                .ConfigureAwait(false)
            ?? throw new InvalidDataException(
                $"Transaction epoch '{durableTransaction.EpochId}' is missing."
            );
        if (!string.Equals(
                durableTransaction.EpochPlanFingerprint,
                DerivedMemoryMaintainerRunner.GetEpochPlanFingerprint(
                    epoch
                ),
                StringComparison.Ordinal
            )
            || durableTransaction.BranchRefId
                != epoch.BranchRefId
            || !string.Equals(
                durableTransaction.CoherenceGroup,
                epoch.CoherenceGroup,
                StringComparison.Ordinal
            )
            || !string.Equals(
                durableTransaction.TopologyVersion,
                epoch.TopologyVersion,
                StringComparison.Ordinal
            )
            || !string.Equals(
                durableTransaction.InputSetId,
                epoch.InputSetId,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "Orchestration transaction does not match its exact epoch."
            );
        }
        await ValidateCurrentLineageAuthorityAsync(
                engine,
                epoch,
                request.AnchorSetups,
                cancellationToken
            )
            .ConfigureAwait(false);
        return epoch;
    }

    private async ValueTask ValidateCurrentLineageAuthorityAsync(
        SessionJournalEngine engine,
        DerivedArtifactEpochPlan epoch,
        SessionContextAnchorSetupReferences expectedAnchorSetups,
        CancellationToken cancellationToken
    ) {
        DerivedArtifactPlannerConfig config =
            await _repository.EpochPlanner.TryReadConfigAsync(
                    epoch.ConfigId,
                    cancellationToken
                )
                .ConfigureAwait(false)
            ?? throw new InvalidDataException(
                $"Epoch planner config '{epoch.ConfigId}' is missing."
            );
        DerivedArtifactEpochRawAuthorityValidation authority =
            DerivedMemoryEngineReadGate.Run(
                engine,
                () => _repository.EpochPlanner
                    .ValidateRawAuthorityDetailed(
                    engine,
                    [epoch],
                    [config],
                    cancellationToken
                )
            );
        if (!authority.EndSetupsByEpochId.TryGetValue(
                epoch.EpochId,
                out SessionContextAnchorSetupReferences? setups
            )
            || setups != expectedAnchorSetups) {
            throw new InvalidDataException(
                $"Epoch '{epoch.EpochId}' current-lineage anchor authority changed."
            );
        }
    }

    private static void ValidateTransaction(
        DerivedArtifactSetPublicationRequest request,
        IReadOnlyDictionary<string, DerivedArtifactSetRoleRequirement> roles
    ) {
        DerivedMemoryOrchestrationTransaction transaction =
            request.Transaction;
        if (transaction.BranchRefId != request.BranchRefId
            || !string.Equals(
                transaction.CoherenceGroup,
                request.Policy.CoherenceGroup,
                StringComparison.Ordinal
            )
            || !string.Equals(
                transaction.PolicyId,
                request.Policy.PolicyId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                transaction.PolicyFingerprint,
                request.Policy.PolicyFingerprint,
                StringComparison.Ordinal
            )
            || !string.Equals(
                request.ExpectedPreviousSetId,
                transaction.InputSetId,
                StringComparison.Ordinal
            )) {
            throw new ArgumentException(
                "ArtifactSet publication policy/input does not match its transaction.",
                nameof(request)
            );
        }
        DerivedMemoryRoleProvisioning[] provisioning =
            DerivedMemoryOrchestrationStore.ValidateAndCanonicalize(
                request.Policy,
                transaction.Roles
            );
        if (!provisioning.SequenceEqual(transaction.Roles)
            || roles.Count != provisioning.Length) {
            throw new ArgumentException(
                "ArtifactSet transaction role provisioning is not canonical.",
                nameof(request)
            );
        }
        ValidateTransactionIdentityShape(transaction);
    }

    private async ValueTask<DerivedArtifactSet> CreateSetAsync(
        DerivedArtifactSetPublicationRequest request,
        IReadOnlyDictionary<string, DerivedArtifactSetRoleRequirement> roles,
        IReadOnlyList<DerivedArtifactSetMemberSelection> selections,
        CancellationToken cancellationToken
    ) {
        IReadOnlyDictionary<string, DerivedMemoryRoleProvisioning>
            provisioning = request.Transaction.Roles.ToDictionary(
                static role => role.RoleId,
                StringComparer.Ordinal
            );
        var selectedRoles = new HashSet<string>(StringComparer.Ordinal);
        var artifactIds = new HashSet<string>(StringComparer.Ordinal);
        var targets =
            new HashSet<(MemoryPackCarrier Carrier, string BlockKey)>();
        var members = new List<DerivedArtifactSetMember>(selections.Count);
        EventAddress? commonAnchor = null;
        foreach (DerivedArtifactSetMemberSelection selection in selections) {
            if (!roles.TryGetValue(
                    selection.RoleId,
                    out DerivedArtifactSetRoleRequirement? requirement
                )) {
                throw new ArgumentException(
                    $"Artifact-set role '{selection.RoleId}' is not declared by policy.",
                    nameof(request)
                );
            }
            if (!selectedRoles.Add(selection.RoleId)
                || !artifactIds.Add(selection.ArtifactId)) {
                throw new ArgumentException(
                    "Artifact-set roles and exact artifact ids must be unique.",
                    nameof(request)
                );
            }
            DerivedMemoryArtifact artifact = await _repository.Artifacts
                .TryReadArtifactAsync(
                    selection.ArtifactId,
                    cancellationToken
                )
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    $"Exact derived artifact '{selection.ArtifactId}' is missing or unusable."
                );
            if (!string.Equals(
                    artifact.RoleId,
                    selection.RoleId,
                    StringComparison.Ordinal
                )) {
                throw new InvalidDataException(
                    $"Artifact '{artifact.ArtifactId}' role does not match selection '{selection.RoleId}'."
                );
            }
            if (artifact.Target != requirement.Target) {
                throw new InvalidDataException(
                    $"Artifact '{artifact.ArtifactId}' target does not match role '{selection.RoleId}'."
                );
            }
            DerivedMemoryRoleProvisioning provision =
                provisioning[selection.RoleId];
            var settlement = new DerivedMemoryRoleSettlement(
                request.Transaction.TransactionId,
                selection.RoleId,
                artifact.ArtifactId,
                artifact.Outcome
            );
            DerivedMemoryOrchestrationStore.ValidateArtifact(
                request.Transaction,
                provision,
                settlement,
                artifact
            );
            if (!targets.Add((
                    artifact.Target.Carrier,
                    artifact.Target.BlockKey
                ))) {
                throw new InvalidDataException(
                    "Artifact-set member targets must be unique."
                );
            }
            commonAnchor ??= artifact.AnchorRawEvent;
            if (artifact.AnchorRawEvent != commonAnchor
                || artifact.SourceEndInclusive != commonAnchor) {
                throw new InvalidDataException(
                    "Artifact-set members require one exact common coverage anchor."
                );
            }
            if (artifact.AnchorSetups != request.AnchorSetups) {
                throw new InvalidDataException(
                    $"Artifact '{artifact.ArtifactId}' exact anchor setup references do not match publication."
                );
            }
            if (!artifact.MemoryPack.TryGetBlock(
                    artifact.Target,
                    out MemoryPackBlock block
                )
                || !string.Equals(
                    block.Text,
                    artifact.Content,
                    StringComparison.Ordinal
                )) {
                throw new InvalidDataException(
                    $"Artifact '{artifact.ArtifactId}' target content is inconsistent."
                );
            }
            members.Add(new DerivedArtifactSetMember(
                selection.RoleId,
                artifact.ArtifactId,
                artifact.ArtifactKind,
                artifact.Target,
                SessionContextContributionHasher.CodecId,
                SessionContextContributionHasher.ComputeSha256(block.Text),
                artifact.SourceRawHead,
                artifact.Outcome
            ));
        }
        foreach (DerivedArtifactSetRoleRequirement required in
                 roles.Values.Where(static role => role.Required)) {
            if (!selectedRoles.Contains(required.RoleId)) {
                throw new InvalidDataException(
                    $"Artifact-set publication is missing required role '{required.RoleId}'."
                );
            }
        }
        if (commonAnchor is null) {
            throw new InvalidDataException(
                "Artifact-set publication requires at least one member."
            );
        }

        DerivedArtifactSetMember[] canonicalMembers = [
            .. members.OrderBy(
                static member => member.RoleId,
                StringComparer.Ordinal
            )
        ];
        DerivedArtifactSetRoleRequirement[] canonicalRoleRequirements =
            CanonicalizeRoleRequirements(roles.Values);
        var identity = CreateIdentityDto(
            request.Transaction,
            request.Policy,
            canonicalRoleRequirements,
            request.ExpectedPreviousSetId,
            commonAnchor.Value,
            request.AnchorSetups,
            canonicalMembers
        );
        string setId = ComputeSetId(identity);
        return new DerivedArtifactSet(
            setId,
            request.Transaction.TransactionId,
            request.Transaction.JobFingerprint,
            request.Transaction.EpochId,
            request.Transaction.EpochPlanFingerprint,
            request.Transaction.BranchRefId,
            request.Policy.CoherenceGroup,
            request.Transaction.TopologyVersion,
            request.Policy.PolicyId,
            request.Policy.PolicyFingerprint,
            Array.AsReadOnly(canonicalRoleRequirements),
            request.Transaction.Roles,
            request.ExpectedPreviousSetId,
            commonAnchor.Value,
            request.AnchorSetups,
            Array.AsReadOnly(canonicalMembers)
        );
    }

    private static DerivedArtifactSetMemberSelection[] SnapshotSelections(
        IReadOnlyList<DerivedArtifactSetMemberSelection> selections
    ) {
        var result = new List<DerivedArtifactSetMemberSelection>();
        foreach (DerivedArtifactSetMemberSelection selection in selections) {
            if (result.Count == MaxMemberCount) {
                throw new ArgumentException(
                    $"Artifact-set publication supports at most {MaxMemberCount} members.",
                    nameof(selections)
                );
            }
            ArgumentNullException.ThrowIfNull(selection);
            DerivedArtifactSetPolicy.ValidateToken(
                selection.RoleId,
                nameof(selection.RoleId)
            );
            ValidateArtifactId(selection.ArtifactId);
            result.Add(selection);
        }
        if (result.Count == 0) {
            throw new ArgumentException(
                "Artifact-set publication requires at least one member.",
                nameof(selections)
            );
        }
        return [.. result];
    }

    private async ValueTask<DerivedArtifactSetLatestPointerDto?>
        TryReadLatestPointerDtoAsync(
        DerivedArtifactSetPolicy policy,
        RefId branchRefId,
        CancellationToken cancellationToken
    ) {
        string path = GetLatestPointerPath(policy, branchRefId);
        DerivedMemoryPathGuard.EnsureSafeDescendant(
            _repository.SessionJournalRepositoryPath,
            path
        );
        if (!File.Exists(path)) {
            return null;
        }
        DerivedArtifactSetLatestPointerDto? pointer;
        try {
            DerivedMemoryPathGuard.EnsureSafeDescendant(
                _repository.SessionJournalRepositoryPath,
                path
            );
            await using FileStream stream = File.OpenRead(path);
            EnsureStreamWithinLimit(
                stream,
                MaxLatestPointerFileBytes,
                "Derived ArtifactSet latest pointer",
                path
            );
            pointer = await JsonSerializer.DeserializeAsync<
                    DerivedArtifactSetLatestPointerDto
                >(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException exception) {
            throw new InvalidDataException(
                $"Derived ArtifactSet latest pointer is malformed: {path}",
                exception
            );
        }
        if (pointer is null
            || !string.Equals(
                pointer.Schema,
                LatestPointerSchema,
                StringComparison.Ordinal
            )
            || pointer.BranchRefId != branchRefId
            || !string.Equals(
                pointer.CoherenceGroup,
                policy.CoherenceGroup,
                StringComparison.Ordinal
            )
            || !string.Equals(
                pointer.PolicyId,
                policy.PolicyId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                pointer.PolicyFingerprint,
                policy.PolicyFingerprint,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                $"Derived ArtifactSet latest pointer does not match its exact lineage/policy key: {path}"
            );
        }
        ValidateSetId(pointer.SetId);
        return pointer;
    }

    private async ValueTask<DerivedArtifactSetLatestPointer>
        ReadAndValidatePointerSelfAsync(
        string path,
        CancellationToken cancellationToken
    ) {
        DerivedArtifactSetLatestPointerDto? dto;
        try {
            DerivedMemoryPathGuard.EnsureSafeDescendant(
                _repository.SessionJournalRepositoryPath,
                path
            );
            await using FileStream stream = File.OpenRead(path);
            EnsureStreamWithinLimit(
                stream,
                MaxLatestPointerFileBytes,
                "Derived ArtifactSet latest pointer",
                path
            );
            dto = await JsonSerializer.DeserializeAsync<
                    DerivedArtifactSetLatestPointerDto
                >(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException exception) {
            throw new InvalidDataException(
                $"Derived ArtifactSet latest pointer is malformed: {path}",
                exception
            );
        }
        if (dto is null
            || !string.Equals(
                dto.Schema,
                LatestPointerSchema,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                $"Derived ArtifactSet latest pointer schema is invalid: {path}"
            );
        }
        try {
            DerivedArtifactSetPolicy.ValidateBranchRefId(dto.BranchRefId);
            DerivedArtifactSetPolicy.ValidateToken(
                dto.CoherenceGroup,
                nameof(dto.CoherenceGroup)
            );
            DerivedArtifactSetPolicy.ValidateToken(
                dto.PolicyId,
                nameof(dto.PolicyId)
            );
            DerivedArtifactSetPolicy.ValidateToken(
                dto.PolicyFingerprint,
                nameof(dto.PolicyFingerprint)
            );
            ValidateSetId(dto.SetId);
        }
        catch (ArgumentException exception) {
            throw new InvalidDataException(
                $"Derived ArtifactSet latest pointer identity is invalid: {path}",
                exception
            );
        }
        string expectedFileName =
            ComputeLatestKey(
                dto.BranchRefId,
                dto.CoherenceGroup,
                dto.PolicyId,
                dto.PolicyFingerprint
            ) + ".json";
        if (!string.Equals(
                Path.GetFileName(path),
                expectedFileName,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                $"Derived ArtifactSet latest pointer filename does not match its exact key: {path}"
            );
        }
        return new DerivedArtifactSetLatestPointer(
            dto.BranchRefId,
            dto.CoherenceGroup,
            dto.PolicyId,
            dto.PolicyFingerprint,
            dto.SetId
        );
    }

    private async ValueTask<DerivedArtifactSet> ReadSetRequiredAsync(
        string setId,
        DerivedArtifactSetPolicy policy,
        RefId branchRefId,
        CancellationToken cancellationToken
    ) {
        string path = GetSetPath(setId);
        DerivedMemoryPathGuard.EnsureSafeDescendant(
            _repository.SessionJournalRepositoryPath,
            path
        );
        if (!File.Exists(path)) {
            throw new InvalidDataException(
                $"Derived ArtifactSet '{setId}' is missing."
            );
        }
        DerivedArtifactSetDto dto = await ReadDtoRequiredAsync(
                path,
                cancellationToken
            )
            .ConfigureAwait(false);
        DerivedArtifactSet set = MaterializeAndValidate(
            dto,
            policy,
            branchRefId
        );
        if (!string.Equals(set.SetId, setId, StringComparison.Ordinal)) {
            throw new InvalidDataException(
                $"Derived ArtifactSet filename/id mismatch for '{setId}'."
            );
        }
        return set;
    }

    private async ValueTask<DerivedArtifactSetDto> ReadDtoRequiredAsync(
        string path,
        CancellationToken cancellationToken
    ) {
        try {
            DerivedMemoryPathGuard.EnsureSafeDescendant(
                _repository.SessionJournalRepositoryPath,
                path
            );
            await using FileStream stream = File.OpenRead(path);
            EnsureStreamWithinLimit(
                stream,
                MaxSetFileBytes,
                "Derived ArtifactSet",
                path
            );
            return await JsonSerializer.DeserializeAsync<DerivedArtifactSetDto>(
                    stream,
                    JsonOptions,
                    cancellationToken
                )
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    $"Derived ArtifactSet is empty: {path}"
                );
        }
        catch (JsonException exception) {
            throw new InvalidDataException(
                $"Derived ArtifactSet is malformed: {path}",
                exception
            );
        }
    }

    private static DerivedArtifactSet MaterializeAndValidate(
        DerivedArtifactSetDto dto,
        DerivedArtifactSetPolicy policy,
        RefId branchRefId
    ) {
        DerivedArtifactSet set = MaterializeAndValidateSelf(dto);
        if (set.BranchRefId != branchRefId
            || !string.Equals(
                set.CoherenceGroup,
                policy.CoherenceGroup,
                StringComparison.Ordinal
            )
            || !string.Equals(set.PolicyId, policy.PolicyId, StringComparison.Ordinal)
            || !string.Equals(
                set.PolicyFingerprint,
                policy.PolicyFingerprint,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "Derived ArtifactSet schema, lineage, or policy mismatch."
            );
        }
        DerivedArtifactSetRoleRequirement[] callerRoleRequirements =
            CanonicalizeRoleRequirements(
                policy.ValidateAndSnapshot().Values
            );
        if (!set.RoleRequirements.SequenceEqual(
                callerRoleRequirements
            )) {
            throw new InvalidDataException(
                "Derived ArtifactSet role requirements do not match the caller policy."
            );
        }
        return set;
    }

    private static DerivedArtifactSet MaterializeAndValidateSelf(
        DerivedArtifactSetDto dto
    ) {
        try {
            return MaterializeAndValidateSelfCore(dto);
        }
        catch (Exception exception) when (
            exception is ArgumentException or FormatException
        ) {
            throw new InvalidDataException(
                "Derived ArtifactSet contains an invalid persisted value.",
                exception
            );
        }
    }

    private static DerivedArtifactSet MaterializeAndValidateSelfCore(
        DerivedArtifactSetDto dto
    ) {
        if (!string.Equals(dto.Schema, SetSchema, StringComparison.Ordinal)) {
            throw new InvalidDataException(
                "Derived ArtifactSet schema is invalid."
            );
        }
        DerivedArtifactSetRoleRequirement[] persistedRoleRequirements =
            MaterializeRoleRequirements(dto.RoleRequirements);
        var persistedPolicy = new DerivedArtifactSetPolicy(
            dto.PolicyId,
            dto.PolicyFingerprint,
            dto.CoherenceGroup,
            persistedRoleRequirements
        );
        try {
            DerivedArtifactSetPolicy.ValidateBranchRefId(dto.BranchRefId);
            _ = persistedPolicy.ValidateAndSnapshot();
        }
        catch (ArgumentException exception) {
            throw new InvalidDataException(
                "Derived ArtifactSet lineage or policy identity is invalid.",
                exception
            );
        }
        IReadOnlyDictionary<string, DerivedArtifactSetRoleRequirement> roles =
            persistedRoleRequirements.ToDictionary(
                static role => role.RoleId,
                StringComparer.Ordinal
            );
        DerivedMemoryRoleProvisioning[] persistedProvisioning =
            MaterializeRoleProvisioning(
                dto.RoleProvisioning,
                persistedPolicy
            );
        var transaction = new DerivedMemoryOrchestrationTransaction(
            dto.TransactionId,
            dto.JobFingerprint,
            dto.EpochId,
            dto.EpochPlanFingerprint,
            dto.BranchRefId,
            dto.CoherenceGroup,
            dto.TopologyVersion,
            dto.PreviousSetId,
            dto.PolicyId,
            dto.PolicyFingerprint,
            Array.AsReadOnly(persistedProvisioning)
        );
        ValidateTransactionIdentityShape(transaction);
        ValidateSetId(dto.SetId);
        if (dto.PreviousSetId is not null) {
            ValidateSetId(dto.PreviousSetId);
        }
        EventAddress commonAnchor =
            EventAddressTextCodec.Parse(dto.CommonAnchor);
        SessionContextAnchorSetupReferences setups =
            MaterializeSetupReferences(dto.AnchorSetups);
        ValidateSetupReferences(setups);
        if (dto.Members is null
            || dto.Members.Count is 0 or > MaxMemberCount) {
            throw new InvalidDataException(
                $"Derived ArtifactSet must contain 1 through {MaxMemberCount} members."
            );
        }

        var roleIds = new HashSet<string>(StringComparer.Ordinal);
        var artifactIds = new HashSet<string>(StringComparer.Ordinal);
        var targets =
            new HashSet<(MemoryPackCarrier Carrier, string BlockKey)>();
        var members = new List<DerivedArtifactSetMember>(
            dto.Members.Count
        );
        string? previousRole = null;
        foreach (DerivedArtifactSetMemberDto member in dto.Members) {
            if (member is null) {
                throw new InvalidDataException(
                    "Derived ArtifactSet contains a null member."
                );
            }
            DerivedArtifactSetPolicy.ValidateToken(
                member.RoleId,
                nameof(member.RoleId)
            );
            ValidateArtifactId(member.ArtifactId);
            DerivedArtifactSetPolicy.ValidateToken(
                member.ArtifactKind,
                nameof(member.ArtifactKind)
            );
            if (!roles.TryGetValue(
                    member.RoleId,
                    out DerivedArtifactSetRoleRequirement? role
                )) {
                throw new InvalidDataException(
                    $"Derived ArtifactSet contains undeclared role '{member.RoleId}'."
                );
            }
            MemoryPackBlockPath target = MaterializeTarget(member.Target);
            if (target != role.Target
                || !roleIds.Add(member.RoleId)
                || !artifactIds.Add(member.ArtifactId)
                || !targets.Add((target.Carrier, target.BlockKey))) {
                throw new InvalidDataException(
                    "Derived ArtifactSet role, artifact, or target membership is invalid."
                );
            }
            if (previousRole is not null
                && string.CompareOrdinal(previousRole, member.RoleId) >= 0) {
                throw new InvalidDataException(
                    "Derived ArtifactSet members are not in canonical role order."
                );
            }
            previousRole = member.RoleId;
            if (!string.Equals(
                    member.ContentCodecId,
                    SessionContextContributionHasher.CodecId,
                    StringComparison.Ordinal
                )
                || !IsLowerSha256(member.ContentSha256)
                || !DerivedMemoryArtifactOutcomes.IsDefined(
                    member.Outcome
                )) {
                throw new InvalidDataException(
                    "Derived ArtifactSet member content identity is invalid."
                );
            }
            EventAddress sourceRawHead =
                EventAddressTextCodec.Parse(member.SourceRawHead);
            members.Add(new DerivedArtifactSetMember(
                member.RoleId,
                member.ArtifactId,
                member.ArtifactKind,
                target,
                member.ContentCodecId,
                member.ContentSha256,
                sourceRawHead,
                member.Outcome
            ));
        }
        foreach (DerivedArtifactSetRoleRequirement required in
                 roles.Values.Where(static role => role.Required)) {
            if (!roleIds.Contains(required.RoleId)) {
                throw new InvalidDataException(
                    $"Derived ArtifactSet is missing required role '{required.RoleId}'."
                );
            }
        }
        DerivedArtifactSetMember[] frozenMembers = [.. members];
        var identity = CreateIdentityDto(
            transaction,
            persistedPolicy,
            persistedRoleRequirements,
            dto.PreviousSetId,
            commonAnchor,
            setups,
            frozenMembers
        );
        if (!string.Equals(
                dto.SetId,
                ComputeSetId(identity),
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                $"Derived ArtifactSet '{dto.SetId}' identity hash is invalid."
            );
        }
        return new DerivedArtifactSet(
            dto.SetId,
            dto.TransactionId,
            dto.JobFingerprint,
            dto.EpochId,
            dto.EpochPlanFingerprint,
            dto.BranchRefId,
            dto.CoherenceGroup,
            dto.TopologyVersion,
            dto.PolicyId,
            dto.PolicyFingerprint,
            Array.AsReadOnly(persistedRoleRequirements),
            Array.AsReadOnly(persistedProvisioning),
            dto.PreviousSetId,
            commonAnchor,
            setups,
            Array.AsReadOnly(frozenMembers)
        );
    }

    private static void ValidateArtifactAgainstMember(
        DerivedMemoryArtifact artifact,
        DerivedArtifactSetMember member,
        EventAddress commonAnchor,
        SessionContextAnchorSetupReferences anchorSetups
    ) {
        if (!string.Equals(
                artifact.Status,
                DerivedMemoryArtifactStatus.Produced,
                StringComparison.Ordinal
            )
            || !string.Equals(
                artifact.RoleId,
                member.RoleId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                artifact.ArtifactId,
                member.ArtifactId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                artifact.ArtifactKind,
                member.ArtifactKind,
                StringComparison.Ordinal
            )
            || artifact.Target != member.Target
            || artifact.SourceRawHead != member.SourceRawHead
            || artifact.AnchorRawEvent != commonAnchor
            || artifact.SourceEndInclusive != commonAnchor
            || artifact.AnchorSetups != anchorSetups
            || !string.Equals(
                artifact.Outcome,
                member.Outcome,
                StringComparison.Ordinal
            )
            || !artifact.MemoryPack.TryGetBlock(
                artifact.Target,
                out MemoryPackBlock block
            )
            || !string.Equals(
                artifact.Content,
                block.Text,
                StringComparison.Ordinal
            )
            || !string.Equals(
                member.ContentCodecId,
                SessionContextContributionHasher.CodecId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                member.ContentSha256,
                SessionContextContributionHasher.ComputeSha256(block.Text),
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                $"Derived ArtifactSet member '{member.ArtifactId}' does not match its exact artifact."
            );
        }
    }

    private static void ValidateCompleteAcyclicLineage(
        DerivedArtifactSet tip,
        IReadOnlyDictionary<string, DerivedArtifactSet> sets
    ) {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        DerivedArtifactSet? cursor = tip;
        while (cursor is not null) {
            if (!visited.Add(cursor.SetId)) {
                throw new InvalidDataException(
                    "Derived ArtifactSet lineage contains a cycle."
                );
            }
            cursor = cursor.PreviousSetId is { } previous
                ? sets[previous]
                : null;
        }
        if (visited.Count != sets.Count) {
            throw new InvalidDataException(
                "Derived ArtifactSet lineage is disconnected or forked."
            );
        }
    }

    private static bool SetsEquivalent(
        DerivedArtifactSet left,
        DerivedArtifactSet right
    ) =>
        string.Equals(left.SetId, right.SetId, StringComparison.Ordinal)
        && string.Equals(
            left.TransactionId,
            right.TransactionId,
            StringComparison.Ordinal
        )
        && string.Equals(
            left.JobFingerprint,
            right.JobFingerprint,
            StringComparison.Ordinal
        )
        && string.Equals(left.EpochId, right.EpochId, StringComparison.Ordinal)
        && string.Equals(
            left.EpochPlanFingerprint,
            right.EpochPlanFingerprint,
            StringComparison.Ordinal
        )
        && left.BranchRefId == right.BranchRefId
        && string.Equals(
            left.CoherenceGroup,
            right.CoherenceGroup,
            StringComparison.Ordinal
        )
        && string.Equals(
            left.TopologyVersion,
            right.TopologyVersion,
            StringComparison.Ordinal
        )
        && string.Equals(
            left.PolicyId,
            right.PolicyId,
            StringComparison.Ordinal
        )
        && string.Equals(
            left.PolicyFingerprint,
            right.PolicyFingerprint,
            StringComparison.Ordinal
        )
        && left.RoleRequirements.SequenceEqual(right.RoleRequirements)
        && left.RoleProvisioning.SequenceEqual(right.RoleProvisioning)
        && string.Equals(
            left.PreviousSetId,
            right.PreviousSetId,
            StringComparison.Ordinal
        )
        && left.CommonAnchor == right.CommonAnchor
        && left.AnchorSetups == right.AnchorSetups
        && left.Members.SequenceEqual(right.Members);

    private static void RequireExactSetFileName(
        string path,
        string setId
    ) {
        try {
            ValidateSetId(setId);
        }
        catch (ArgumentException exception) {
            throw new InvalidDataException(
                $"Derived ArtifactSet id is invalid in persisted file: {path}",
                exception
            );
        }
        if (!string.Equals(
                Path.GetFileName(path),
                $"{setId}.json",
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                $"Derived ArtifactSet filename does not exactly match its set id: {path}"
            );
        }
    }

    private static void EnsureStreamWithinLimit(
        FileStream stream,
        long maximumBytes,
        string description,
        string path
    ) {
        if (stream.Length > maximumBytes) {
            throw new InvalidDataException(
                $"{description} exceeds its {maximumBytes}-byte limit: {path}"
            );
        }
    }

    private static void EnsureSerializedSize(
        string serialized,
        long maximumBytes,
        string description
    ) {
        if (Encoding.UTF8.GetByteCount(serialized) > maximumBytes) {
            throw new InvalidDataException(
                $"{description} exceeds its {maximumBytes}-byte limit."
            );
        }
    }

    private string GetSetPath(string setId) {
        ValidateSetId(setId);
        return Path.Combine(SetsDirectory, $"{setId}.json");
    }

    private string GetLatestPointerPath(
        DerivedArtifactSetPolicy policy,
        RefId branchRefId
    ) => Path.Combine(
        LatestPointersDirectory,
        $"{ComputeLatestKey(policy, branchRefId)}.json"
    );

    private static string ComputeLatestKey(
        DerivedArtifactSetPolicy policy,
        RefId branchRefId
    ) => ComputeLatestKey(
        branchRefId,
        policy.CoherenceGroup,
        policy.PolicyId,
        policy.PolicyFingerprint
    );

    private static string ComputeLatestKey(
        RefId branchRefId,
        string coherenceGroup,
        string policyId,
        string policyFingerprint
    ) {
        string identity = string.Join(
            '\0',
            branchRefId.ToHexString(),
            coherenceGroup,
            policyId,
            policyFingerprint
        );
        return "latest_" + ComputeDomainHash(LatestKeyDomain, identity);
    }

    private static IEnumerable<string> EnumeratePersistedJsonFiles(
        string directory,
        string description
    ) {
        foreach (string path in Directory
                     .EnumerateFiles(directory)
                     .OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal)) {
            if (!string.Equals(
                    Path.GetExtension(path),
                    ".json",
                    StringComparison.Ordinal
                )) {
                throw new InvalidDataException(
                    $"{description} directory contains an unexpected file: {path}"
                );
            }
            yield return path;
        }
    }

    private static string ComputeSetId(
        DerivedArtifactSetIdentityDto identity
    ) {
        byte[] canonical =
            JsonSerializer.SerializeToUtf8Bytes(
                identity,
                IdentityJsonOptions
            );
        return "das_" + ComputeDomainHash(SetIdDomain, canonical);
    }

    private static string ComputeDomainHash(string domain, string value) =>
        ComputeDomainHash(domain, Encoding.UTF8.GetBytes(value));

    private static string ComputeDomainHash(
        string domain,
        ReadOnlySpan<byte> value
    ) {
        byte[] prefix = Encoding.UTF8.GetBytes(domain + "\0");
        byte[] input = new byte[checked(prefix.Length + value.Length)];
        prefix.CopyTo(input, 0);
        value.CopyTo(input.AsSpan(prefix.Length));
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }

    private static DerivedArtifactSetDto ToDto(DerivedArtifactSet set) =>
        new(
            SetSchema,
            set.SetId,
            set.TransactionId,
            set.JobFingerprint,
            set.EpochId,
            set.EpochPlanFingerprint,
            set.BranchRefId,
            set.CoherenceGroup,
            set.TopologyVersion,
            set.PolicyId,
            set.PolicyFingerprint,
            set.RoleRequirements.Select(ToDto).ToArray(),
            set.RoleProvisioning.Select(ToDto).ToArray(),
            set.PreviousSetId,
            EventAddressTextCodec.Format(set.CommonAnchor),
            ToDto(set.AnchorSetups),
            set.Members.Select(ToDto).ToArray()
        );

    private static DerivedArtifactSetIdentityDto CreateIdentityDto(
        DerivedMemoryOrchestrationTransaction transaction,
        DerivedArtifactSetPolicy policy,
        IReadOnlyList<DerivedArtifactSetRoleRequirement> roleRequirements,
        string? previousSetId,
        EventAddress commonAnchor,
        SessionContextAnchorSetupReferences setups,
        IReadOnlyList<DerivedArtifactSetMember> members
    ) => new(
        SetSchema,
        transaction.TransactionId,
        transaction.JobFingerprint,
        transaction.EpochId,
        transaction.EpochPlanFingerprint,
        transaction.BranchRefId,
        policy.CoherenceGroup,
        transaction.TopologyVersion,
        policy.PolicyId,
        policy.PolicyFingerprint,
        roleRequirements.Select(ToDto).ToArray(),
        transaction.Roles.Select(ToDto).ToArray(),
        previousSetId,
        EventAddressTextCodec.Format(commonAnchor),
        ToDto(setups),
        members.Select(ToDto).ToArray()
    );

    private static DerivedArtifactSetMemberDto ToDto(
        DerivedArtifactSetMember member
    ) => new(
        member.RoleId,
        member.ArtifactId,
        member.ArtifactKind,
        new DerivedArtifactSetTargetDto(
            MemoryPackCarrierTokens.ToStorageToken(member.Target.Carrier),
            member.Target.BlockKey
        ),
        member.ContentCodecId,
        member.ContentSha256,
        EventAddressTextCodec.Format(member.SourceRawHead),
        member.Outcome
    );

    private static DerivedArtifactSetRoleProvisioningDto ToDto(
        DerivedMemoryRoleProvisioning role
    ) => new(
        role.RoleId,
        role.ProfileId,
        new DerivedArtifactSetTargetDto(
            MemoryPackCarrierTokens.ToStorageToken(role.Target.Carrier),
            role.Target.BlockKey
        ),
        role.Required,
        role.Producer,
        role.ProducerFingerprint,
        role.PromptFingerprint,
        role.ModelFingerprint,
        role.ExecutionMode,
        role.CandidateId,
        role.AttemptId,
        role.SelectedArtifactId
    );

    private static DerivedArtifactSetRoleRequirementDto ToDto(
        DerivedArtifactSetRoleRequirement requirement
    ) => new(
        requirement.RoleId,
        new DerivedArtifactSetTargetDto(
            MemoryPackCarrierTokens.ToStorageToken(
                requirement.Target.Carrier
            ),
            requirement.Target.BlockKey
        ),
        requirement.Required
    );

    private static DerivedArtifactSetRoleRequirement[]
        CanonicalizeRoleRequirements(
        IEnumerable<DerivedArtifactSetRoleRequirement> requirements
    ) => [
        .. requirements
            .OrderBy(static role => role.RoleId, StringComparer.Ordinal)
            .Select(static role =>
                new DerivedArtifactSetRoleRequirement(
                    role.RoleId,
                    new MemoryPackBlockPath(
                        role.Target.Carrier,
                        role.Target.BlockKey
                    ),
                    role.Required
                )
            )
    ];

    private static DerivedArtifactSetRoleRequirement[]
        MaterializeRoleRequirements(
        IReadOnlyList<DerivedArtifactSetRoleRequirementDto>? dtos
    ) {
        if (dtos is null
            || dtos.Count is 0 or > DerivedArtifactSetPolicy.MaxRoleCount) {
            throw new InvalidDataException(
                $"Derived ArtifactSet must persist 1 through {DerivedArtifactSetPolicy.MaxRoleCount} role requirements."
            );
        }
        var roleIds = new HashSet<string>(StringComparer.Ordinal);
        var targets =
            new HashSet<(MemoryPackCarrier Carrier, string BlockKey)>();
        var requirements = new List<DerivedArtifactSetRoleRequirement>(
            dtos.Count
        );
        string? previousRole = null;
        bool hasRequiredRole = false;
        foreach (DerivedArtifactSetRoleRequirementDto dto in dtos) {
            if (dto is null) {
                throw new InvalidDataException(
                    "Derived ArtifactSet contains a null role requirement."
                );
            }
            if (dto.Required is null) {
                throw new InvalidDataException(
                    "Derived ArtifactSet role requirement is missing required."
                );
            }
            try {
                DerivedArtifactSetPolicy.ValidateToken(
                    dto.RoleId,
                    nameof(dto.RoleId)
                );
            }
            catch (ArgumentException exception) {
                throw new InvalidDataException(
                    "Derived ArtifactSet role requirement id is invalid.",
                    exception
                );
            }
            MemoryPackBlockPath target = MaterializeTarget(dto.Target);
            if (!roleIds.Add(dto.RoleId)
                || !targets.Add((target.Carrier, target.BlockKey))
                || previousRole is not null
                    && string.CompareOrdinal(
                        previousRole,
                        dto.RoleId
                    ) >= 0) {
                throw new InvalidDataException(
                    "Derived ArtifactSet role requirements are duplicate or not in canonical order."
                );
            }
            previousRole = dto.RoleId;
            hasRequiredRole |= dto.Required.Value;
            requirements.Add(new DerivedArtifactSetRoleRequirement(
                dto.RoleId,
                target,
                dto.Required.Value
            ));
        }
        if (!hasRequiredRole) {
            throw new InvalidDataException(
                "Derived ArtifactSet role requirements need at least one required role."
            );
        }
        return [.. requirements];
    }

    private static DerivedMemoryRoleProvisioning[]
        MaterializeRoleProvisioning(
        IReadOnlyList<DerivedArtifactSetRoleProvisioningDto>? dtos,
        DerivedArtifactSetPolicy policy
    ) {
        if (dtos is null) {
            throw new InvalidDataException(
                "Derived ArtifactSet role provisioning is missing."
            );
        }
        DerivedMemoryRoleProvisioning[] roles = [
            .. dtos.Select(dto => {
                if (dto is null) {
                    throw new InvalidDataException(
                        "Derived ArtifactSet contains null role provisioning."
                    );
                }
                return new DerivedMemoryRoleProvisioning(
                    dto.RoleId,
                    dto.ProfileId,
                    MaterializeTarget(dto.Target),
                    dto.Required,
                    dto.Producer,
                    dto.ProducerFingerprint,
                    dto.PromptFingerprint,
                    dto.ModelFingerprint,
                    dto.ExecutionMode,
                    dto.CandidateId,
                    dto.AttemptId,
                    dto.SelectedArtifactId
                );
            })
        ];
        try {
            DerivedMemoryRoleProvisioning[] canonical =
                DerivedMemoryOrchestrationStore.ValidateAndCanonicalize(
                    policy,
                    roles
                );
            if (!canonical.SequenceEqual(roles)) {
                throw new InvalidDataException(
                    "Derived ArtifactSet role provisioning is not in canonical order."
                );
            }
            return canonical;
        }
        catch (ArgumentException exception) {
            throw new InvalidDataException(
                "Derived ArtifactSet role provisioning is invalid.",
                exception
            );
        }
    }

    private static void ValidateTransactionIdentityShape(
        DerivedMemoryOrchestrationTransaction transaction
    ) {
        ValidateHashIdentity(
            transaction.TransactionId,
            "dmt_",
            "transaction id"
        );
        ValidateHashIdentity(
            transaction.EpochId,
            "dae_",
            "epoch id"
        );
        if (!IsSha256Fingerprint(transaction.JobFingerprint)
            || !IsSha256Fingerprint(
                transaction.EpochPlanFingerprint
            )) {
            throw new InvalidDataException(
                "Derived ArtifactSet transaction fingerprints are invalid."
            );
        }
        DerivedArtifactSetPolicy.ValidateBranchRefId(
            transaction.BranchRefId
        );
        DerivedArtifactSetPolicy.ValidateToken(
            transaction.CoherenceGroup,
            nameof(transaction.CoherenceGroup)
        );
        DerivedArtifactSetPolicy.ValidateToken(
            transaction.TopologyVersion,
            nameof(transaction.TopologyVersion)
        );
        if (transaction.InputSetId is not null) {
            ValidateSetId(transaction.InputSetId);
        }
    }

    private static void ValidateHashIdentity(
        string value,
        string prefix,
        string description
    ) {
        if (value is null
            || value.Length != 68
            || !value.StartsWith(prefix, StringComparison.Ordinal)
            || !value.AsSpan(4).ToString().All(
                static ch => ch is >= '0' and <= '9'
                    or >= 'a' and <= 'f'
            )) {
            throw new InvalidDataException(
                $"Derived ArtifactSet {description} is invalid."
            );
        }
    }

    private static bool IsSha256Fingerprint(string? value) =>
        value is { Length: 71 }
        && value.StartsWith("sha256:", StringComparison.Ordinal)
        && IsLowerSha256(value[7..]);

    private static DerivedArtifactSetSetupReferencesDto ToDto(
        SessionContextAnchorSetupReferences references
    ) => new(
        ToDto(references.RuntimeConfig),
        ToDto(references.SystemPrompt)
    );

    private static DerivedArtifactSetSetupReferenceDto ToDto(
        SessionContextSetupReference reference
    ) => new(
        EventAddressTextCodec.Format(reference.Address),
        reference.BodySchemaVersion,
        reference.PayloadSha256
    );

    private static SessionContextAnchorSetupReferences
        MaterializeSetupReferences(
        DerivedArtifactSetSetupReferencesDto dto
    ) {
        if (dto is null
            || dto.RuntimeConfig is null
            || dto.SystemPrompt is null) {
            throw new InvalidDataException(
                "Derived ArtifactSet anchor setup references are missing."
            );
        }
        return new SessionContextAnchorSetupReferences(
            MaterializeSetupReference(dto.RuntimeConfig),
            MaterializeSetupReference(dto.SystemPrompt)
        );
    }

    private static SessionContextSetupReference MaterializeSetupReference(
        DerivedArtifactSetSetupReferenceDto dto
    ) => new(
        EventAddressTextCodec.Parse(dto.Address),
        dto.BodySchemaVersion,
        dto.PayloadSha256
    );

    private static MemoryPackBlockPath MaterializeTarget(
        DerivedArtifactSetTargetDto dto
    ) {
        if (dto is null
            || !MemoryPackCarrierTokens.TryParseStorageToken(
                dto.Carrier,
                out MemoryPackCarrier carrier
            )) {
            throw new InvalidDataException(
                "Derived ArtifactSet member target carrier is invalid."
            );
        }
        var target = new MemoryPackBlockPath(carrier, dto.BlockKey);
        DerivedArtifactSetPolicy.ValidateTarget(target, nameof(dto));
        return target;
    }

    private static void ValidateSetupReferences(
        SessionContextAnchorSetupReferences references
    ) {
        ArgumentNullException.ThrowIfNull(references.RuntimeConfig);
        ArgumentNullException.ThrowIfNull(references.SystemPrompt);
        ValidateSetupReference(references.RuntimeConfig);
        ValidateSetupReference(references.SystemPrompt);
        if (references.RuntimeConfig.Address
            == references.SystemPrompt.Address) {
            throw new ArgumentException(
                "Runtime and system-prompt setup references must be distinct.",
                nameof(references)
            );
        }
    }

    private static void ValidateSetupReference(
        SessionContextSetupReference reference
    ) {
        if (reference.Address == default
            || reference.BodySchemaVersion <= 0
            || !IsLowerSha256(reference.PayloadSha256)) {
            throw new ArgumentException(
                "Derived ArtifactSet setup reference is invalid.",
                nameof(reference)
            );
        }
    }

    private static bool IsLowerSha256(string? value) =>
        value is { Length: 64 }
        && value.All(
            static ch => ch is >= '0' and <= '9'
                or >= 'a' and <= 'f'
        );

    private static void ValidateArtifactId(string artifactId) {
        if (string.IsNullOrWhiteSpace(artifactId)
            || artifactId.Length > 256
            || artifactId.Any(
                static ch => !(char.IsAsciiLetterOrDigit(ch)
                    || ch is '_' or '-' or '.')
            )) {
            throw new ArgumentException(
                "Derived artifact id is invalid.",
                nameof(artifactId)
            );
        }
    }

    private static void ValidateSetId(string setId) {
        if (setId is null
            || setId.Length != 68
            || !setId.StartsWith("das_", StringComparison.Ordinal)
            || !setId[4..].All(
                static ch => ch is >= '0' and <= '9'
                    or >= 'a' and <= 'f'
            )) {
            throw new ArgumentException(
                "Derived ArtifactSet id is invalid.",
                nameof(setId)
            );
        }
    }
}

internal sealed record DerivedArtifactSetDto(
    [property: JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] string SetId,
    [property: JsonPropertyOrder(2)] string TransactionId,
    [property: JsonPropertyOrder(3)] string JobFingerprint,
    [property: JsonPropertyOrder(4)] string EpochId,
    [property: JsonPropertyOrder(5)] string EpochPlanFingerprint,
    [property: JsonPropertyOrder(6)] RefId BranchRefId,
    [property: JsonPropertyOrder(7)] string CoherenceGroup,
    [property: JsonPropertyOrder(8)] string TopologyVersion,
    [property: JsonPropertyOrder(9)] string PolicyId,
    [property: JsonPropertyOrder(10)] string PolicyFingerprint,
    [property: JsonPropertyOrder(11)]
        IReadOnlyList<DerivedArtifactSetRoleRequirementDto> RoleRequirements,
    [property: JsonPropertyOrder(12)]
        IReadOnlyList<DerivedArtifactSetRoleProvisioningDto> RoleProvisioning,
    [property: JsonPropertyOrder(13)] string? PreviousSetId,
    [property: JsonPropertyOrder(14)] string CommonAnchor,
    [property: JsonPropertyOrder(15)]
        DerivedArtifactSetSetupReferencesDto AnchorSetups,
    [property: JsonPropertyOrder(16)]
        IReadOnlyList<DerivedArtifactSetMemberDto> Members
);

internal sealed record DerivedArtifactSetIdentityDto(
    [property: JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] string TransactionId,
    [property: JsonPropertyOrder(2)] string JobFingerprint,
    [property: JsonPropertyOrder(3)] string EpochId,
    [property: JsonPropertyOrder(4)] string EpochPlanFingerprint,
    [property: JsonPropertyOrder(5)] RefId BranchRefId,
    [property: JsonPropertyOrder(6)] string CoherenceGroup,
    [property: JsonPropertyOrder(7)] string TopologyVersion,
    [property: JsonPropertyOrder(8)] string PolicyId,
    [property: JsonPropertyOrder(9)] string PolicyFingerprint,
    [property: JsonPropertyOrder(10)]
        IReadOnlyList<DerivedArtifactSetRoleRequirementDto> RoleRequirements,
    [property: JsonPropertyOrder(11)]
        IReadOnlyList<DerivedArtifactSetRoleProvisioningDto> RoleProvisioning,
    [property: JsonPropertyOrder(12)] string? PreviousSetId,
    [property: JsonPropertyOrder(13)] string CommonAnchor,
    [property: JsonPropertyOrder(14)]
        DerivedArtifactSetSetupReferencesDto AnchorSetups,
    [property: JsonPropertyOrder(15)]
        IReadOnlyList<DerivedArtifactSetMemberDto> Members
);

internal sealed record DerivedArtifactSetRoleRequirementDto(
    [property: JsonPropertyOrder(0)] string RoleId,
    [property: JsonPropertyOrder(1)] DerivedArtifactSetTargetDto Target,
    [property: JsonPropertyOrder(2)] bool? Required
);

internal sealed record DerivedArtifactSetMemberDto(
    [property: JsonPropertyOrder(0)] string RoleId,
    [property: JsonPropertyOrder(1)] string ArtifactId,
    [property: JsonPropertyOrder(2)] string ArtifactKind,
    [property: JsonPropertyOrder(3)] DerivedArtifactSetTargetDto Target,
    [property: JsonPropertyOrder(4)] string ContentCodecId,
    [property: JsonPropertyOrder(5)] string ContentSha256,
    [property: JsonPropertyOrder(6)] string SourceRawHead,
    [property: JsonPropertyOrder(7)] string Outcome
);

internal sealed record DerivedArtifactSetRoleProvisioningDto(
    [property: JsonPropertyOrder(0)] string RoleId,
    [property: JsonPropertyOrder(1)] string ProfileId,
    [property: JsonPropertyOrder(2)] DerivedArtifactSetTargetDto Target,
    [property: JsonPropertyOrder(3)] bool Required,
    [property: JsonPropertyOrder(4)] string Producer,
    [property: JsonPropertyOrder(5)] string ProducerFingerprint,
    [property: JsonPropertyOrder(6)] string PromptFingerprint,
    [property: JsonPropertyOrder(7)] string ModelFingerprint,
    [property: JsonPropertyOrder(8)] string ExecutionMode,
    [property: JsonPropertyOrder(9)] string CandidateId,
    [property: JsonPropertyOrder(10)] string AttemptId,
    [property: JsonPropertyOrder(11)] string? SelectedArtifactId
);

internal sealed record DerivedArtifactSetTargetDto(
    [property: JsonPropertyOrder(0)] string Carrier,
    [property: JsonPropertyOrder(1)] string BlockKey
);

internal sealed record DerivedArtifactSetSetupReferencesDto(
    [property: JsonPropertyOrder(0)]
        DerivedArtifactSetSetupReferenceDto RuntimeConfig,
    [property: JsonPropertyOrder(1)]
        DerivedArtifactSetSetupReferenceDto SystemPrompt
);

internal sealed record DerivedArtifactSetSetupReferenceDto(
    [property: JsonPropertyOrder(0)] string Address,
    [property: JsonPropertyOrder(1)] int BodySchemaVersion,
    [property: JsonPropertyOrder(2)] string PayloadSha256
);

internal sealed record DerivedArtifactSetLatestPointerDto(
    [property: JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] RefId BranchRefId,
    [property: JsonPropertyOrder(2)] string CoherenceGroup,
    [property: JsonPropertyOrder(3)] string PolicyId,
    [property: JsonPropertyOrder(4)] string PolicyFingerprint,
    [property: JsonPropertyOrder(5)] string SetId
);
