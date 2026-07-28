using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.EventJournal;

namespace Atelia.SessionJournal.DerivedMemory;

public sealed class DerivedMemoryOrchestrationStore {
    public const string TransactionSchema =
        "atelia.session-journal.derived-memory-transaction.v2";
    public const string SettlementSchema =
        "atelia.session-journal.derived-memory-role-settlement.v1";
    public const string FinalizationSchema =
        "atelia.session-journal.derived-memory-finalization.v1";
    public const long MaxTransactionFileBytes = 1024 * 1024;
    public const long MaxSettlementFileBytes = 64 * 1024;
    public const long MaxFinalizationFileBytes = 256 * 1024;

    private const string JobFingerprintDomain =
        "atelia.session-journal.derived-memory-job.v2";
    private const string TransactionIdDomain =
        "atelia.session-journal.derived-memory-transaction-id.v2";
    private const string RoleFileDomain =
        "atelia.session-journal.derived-memory-settlement-role.v1";

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

    internal DerivedMemoryOrchestrationStore(
        DerivedMemoryRepository repository
    ) {
        _repository = repository;
        TransactionsDirectory = Path.Combine(
            repository.MemoryRoot,
            "transactions"
        );
        SettlementsDirectory = Path.Combine(
            repository.MemoryRoot,
            "settlements"
        );
        FinalizationsDirectory = Path.Combine(
            repository.MemoryRoot,
            "finalizations"
        );
    }

    public string TransactionsDirectory { get; }
    public string SettlementsDirectory { get; }
    public string FinalizationsDirectory { get; }

    internal async ValueTask<DerivedMemoryOrchestrationTransaction>
        GetOrCreateAsync(
        DerivedArtifactEpochPlan epoch,
        DerivedArtifactSetPolicy policy,
        IReadOnlyList<DerivedMemoryRoleProvisioning> roles,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(epoch);
        ArgumentNullException.ThrowIfNull(policy);
        if (!string.Equals(
                epoch.CoherenceGroup,
                policy.CoherenceGroup,
                StringComparison.Ordinal
            )) {
            throw new ArgumentException(
                "ArtifactSet policy coherence group must match the epoch.",
                nameof(policy)
            );
        }
        DerivedMemoryRoleProvisioning[] snapshot =
            ValidateAndCanonicalize(policy, roles);
        string epochPlanFingerprint =
            DerivedMemoryMaintainerRunner.GetEpochPlanFingerprint(epoch);
        var jobIdentity = new JobIdentityDto(
            epoch.EpochId,
            epochPlanFingerprint,
            epoch.BranchRefId,
            epoch.CoherenceGroup,
            epoch.TopologyVersion,
            epoch.InputSetId,
            policy.PolicyId,
            policy.PolicyFingerprint,
            snapshot.Select(ToDto).ToArray()
        );
        string jobFingerprint =
            "sha256:" + ComputeDomainHash(JobFingerprintDomain, jobIdentity);
        string transactionId =
            "dmt_" + ComputeDomainHash(
                TransactionIdDomain,
                new TransactionIdentityDto(
                    epoch.EpochId,
                    jobFingerprint
                )
            );
        var transaction = new DerivedMemoryOrchestrationTransaction(
            transactionId,
            jobFingerprint,
            epoch.EpochId,
            epochPlanFingerprint,
            epoch.BranchRefId,
            epoch.CoherenceGroup,
            epoch.TopologyVersion,
            epoch.InputSetId,
            policy.PolicyId,
            policy.PolicyFingerprint,
            Array.AsReadOnly(snapshot)
        );
        var dto = ToDto(transaction);
        string json = JsonSerializer.Serialize(dto, JsonOptions);
        EnsureSize(json, MaxTransactionFileBytes, "transaction");

        await using FileStream writeLock = await _repository
            .AcquireWriteLockAsync(cancellationToken)
            .ConfigureAwait(false);
        _repository.EnsureDirectory(TransactionsDirectory);
        string path = GetTransactionPath(transactionId);
        if (File.Exists(path)) {
            DerivedMemoryOrchestrationTransaction existing =
                await ReadTransactionRequiredAsync(
                        path,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            if (!TransactionsEquivalent(existing, transaction)) {
                throw new InvalidDataException(
                    $"Immutable orchestration transaction collision at '{transactionId}'."
                );
            }
            return existing;
        }
        await _repository.WriteFileAtomicallyAsync(
                path,
                json,
                overwrite: false,
                cancellationToken
            )
            .ConfigureAwait(false);
        return transaction;
    }

    public async ValueTask<DerivedMemoryOrchestrationTransaction?>
        TryReadTransactionAsync(
        string transactionId,
        CancellationToken cancellationToken = default
    ) {
        ValidateHashId(transactionId, "dmt_", nameof(transactionId));
        string path = GetTransactionPath(transactionId);
        DerivedMemoryPathGuard.EnsureSafeDescendant(
            _repository.SessionJournalRepositoryPath,
            path
        );
        return !File.Exists(path)
            ? null
            : await ReadTransactionRequiredAsync(path, cancellationToken)
                .ConfigureAwait(false);
    }

    public async ValueTask<DerivedMemoryOrchestrationInventory>
        ReadInventoryAsync(
        CancellationToken cancellationToken = default
    ) {
        DerivedMemoryPathGuard.EnsureSafeDescendant(
            _repository.SessionJournalRepositoryPath,
            TransactionsDirectory
        );
        DerivedMemoryPathGuard.EnsureSafeDescendant(
            _repository.SessionJournalRepositoryPath,
            SettlementsDirectory
        );
        DerivedMemoryPathGuard.EnsureSafeDescendant(
            _repository.SessionJournalRepositoryPath,
            FinalizationsDirectory
        );
        var transactions =
            new List<DerivedMemoryOrchestrationTransaction>();
        if (Directory.Exists(TransactionsDirectory)) {
            RejectUnexpectedDirectories(
                TransactionsDirectory,
                "transaction"
            );
            foreach (string path in EnumerateJsonFiles(
                         TransactionsDirectory,
                         "transaction"
                     )) {
                transactions.Add(
                    await ReadTransactionRequiredAsync(
                            path,
                            cancellationToken
                        )
                        .ConfigureAwait(false)
                );
            }
        }
        var settlements = new List<DerivedMemoryRoleSettlement>();
        var transactionIds = transactions
            .Select(static transaction => transaction.TransactionId)
            .ToHashSet(StringComparer.Ordinal);
        if (Directory.Exists(SettlementsDirectory)) {
            RejectUnexpectedFiles(
                SettlementsDirectory,
                "settlement root"
            );
            foreach (string directory in Directory
                         .EnumerateDirectories(SettlementsDirectory)
                         .OrderBy(
                             static path => Path.GetFileName(path),
                             StringComparer.Ordinal
                         )) {
                string transactionId = Path.GetFileName(directory);
                if (!transactionIds.Contains(transactionId)) {
                    throw new InvalidDataException(
                        $"Settlement directory references missing transaction '{transactionId}'."
                    );
                }
            }
            foreach (DerivedMemoryOrchestrationTransaction transaction in
                     transactions) {
                IReadOnlyList<DerivedMemoryRoleSettlement> items =
                    await ReadSettlementsAsync(
                            transaction,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                foreach (DerivedMemoryRoleSettlement settlement in items) {
                    DerivedMemoryRoleProvisioning provision =
                        transaction.Roles.Single(role =>
                            string.Equals(
                                role.RoleId,
                                settlement.RoleId,
                                StringComparison.Ordinal
                            ));
                    DerivedMemoryArtifact artifact =
                        await _repository.Artifacts
                            .TryReadArtifactAsync(
                                settlement.ArtifactId,
                                cancellationToken
                            )
                            .ConfigureAwait(false)
                        ?? throw new InvalidDataException(
                            $"Settlement artifact '{settlement.ArtifactId}' is missing."
                        );
                    ValidateArtifact(
                        transaction,
                        provision,
                        settlement,
                        artifact
                    );
                    settlements.Add(settlement);
                }
            }
        }
        var finalizations =
            new List<DerivedMemoryOrchestrationFinalization>();
        if (Directory.Exists(FinalizationsDirectory)) {
            RejectUnexpectedDirectories(
                FinalizationsDirectory,
                "finalization"
            );
            foreach (string path in EnumerateJsonFiles(
                         FinalizationsDirectory,
                         "finalization"
                     )) {
                DerivedMemoryOrchestrationFinalization finalization =
                    await ReadFinalizationRequiredAsync(
                            path,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                DerivedMemoryOrchestrationTransaction transaction =
                    transactions.SingleOrDefault(item => string.Equals(
                        item.TransactionId,
                        finalization.TransactionId,
                        StringComparison.Ordinal
                    )) ?? throw new InvalidDataException(
                        $"Finalization references missing transaction '{finalization.TransactionId}'."
                    );
                ValidateFinalizationShape(transaction, finalization);
                ValidateFinalizationSettlements(
                    finalization,
                    settlements.Where(settlement => string.Equals(
                        settlement.TransactionId,
                        finalization.TransactionId,
                        StringComparison.Ordinal
                    )).ToArray()
                );
                finalizations.Add(finalization);
            }
        }
        return new DerivedMemoryOrchestrationInventory(
            transactions.AsReadOnly(),
            settlements.AsReadOnly(),
            finalizations.AsReadOnly()
        );
    }

    public async ValueTask<IReadOnlyList<DerivedMemoryRoleSettlement>>
        ReadSettlementsAsync(
        DerivedMemoryOrchestrationTransaction transaction,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(transaction);
        string directory = GetSettlementDirectory(
            transaction.TransactionId
        );
        DerivedMemoryPathGuard.EnsureSafeDescendant(
            _repository.SessionJournalRepositoryPath,
            directory
        );
        if (!Directory.Exists(directory)) {
            return Array.Empty<DerivedMemoryRoleSettlement>();
        }
        RejectUnexpectedDirectories(directory, "settlement");
        var results = new List<DerivedMemoryRoleSettlement>();
        foreach (string path in Directory
                     .EnumerateFiles(directory)
                     .OrderBy(
                         static path => Path.GetFileName(path),
                         StringComparer.Ordinal
                     )) {
            if (!string.Equals(
                    Path.GetExtension(path),
                    ".json",
                    StringComparison.Ordinal
                )) {
                throw new InvalidDataException(
                    $"Settlement directory contains an unexpected file: {path}"
                );
            }
            DerivedMemoryRoleSettlement settlement =
                await ReadSettlementRequiredAsync(
                        path,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            ValidateSettlementShape(transaction, settlement);
            string expectedFile = GetSettlementFileName(
                settlement.RoleId
            );
            if (!string.Equals(
                    Path.GetFileName(path),
                    expectedFile,
                    StringComparison.Ordinal
                )) {
                throw new InvalidDataException(
                    $"Settlement filename does not match role '{settlement.RoleId}'."
                );
            }
            results.Add(settlement);
        }
        if (results.Select(static item => item.RoleId)
            .Distinct(StringComparer.Ordinal).Count() != results.Count) {
            throw new InvalidDataException(
                "Transaction contains duplicate role settlements."
            );
        }
        return results.AsReadOnly();
    }

    public async ValueTask<DerivedMemoryOrchestrationFinalization?>
        TryReadFinalizationAsync(
        DerivedMemoryOrchestrationTransaction transaction,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(transaction);
        string path = GetFinalizationPath(transaction.TransactionId);
        DerivedMemoryPathGuard.EnsureSafeDescendant(
            _repository.SessionJournalRepositoryPath,
            path
        );
        if (!File.Exists(path)) {
            return null;
        }
        DerivedMemoryOrchestrationFinalization finalization =
            await ReadFinalizationRequiredAsync(
                    path,
                    cancellationToken
                )
                .ConfigureAwait(false);
        ValidateFinalizationShape(transaction, finalization);
        ValidateFinalizationSettlements(
            finalization,
            await ReadSettlementsAsync(
                    transaction,
                    cancellationToken
                )
                .ConfigureAwait(false)
        );
        return finalization;
    }

    internal async ValueTask<DerivedMemoryOrchestrationFinalization>
        GetOrCreateFinalizationAsync(
        DerivedMemoryOrchestrationTransaction transaction,
        SessionContextAnchorSetupReferences anchorSetups,
        IReadOnlyList<DerivedMemoryRoleSettlement>
            includedSettlements,
        string expectedSetId,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(anchorSetups);
        ArgumentNullException.ThrowIfNull(includedSettlements);
        ValidateHashId(expectedSetId, "das_", nameof(expectedSetId));
        DerivedMemoryRoleSettlement[] included = [
            .. includedSettlements.OrderBy(
                static settlement => settlement.RoleId,
                StringComparer.Ordinal
            )
        ];
        string[] omitted = [
            .. transaction.Roles
                .Where(role => !role.Required
                    && !included.Any(settlement => string.Equals(
                        settlement.RoleId,
                        role.RoleId,
                        StringComparison.Ordinal
                    )))
                .Select(static role => role.RoleId)
                .OrderBy(static roleId => roleId, StringComparer.Ordinal)
        ];
        var candidate = new DerivedMemoryOrchestrationFinalization(
            transaction.TransactionId,
            transaction.JobFingerprint,
            transaction.EpochId,
            transaction.EpochPlanFingerprint,
            transaction.PolicyId,
            transaction.PolicyFingerprint,
            transaction.InputSetId,
            anchorSetups,
            Array.AsReadOnly(included),
            Array.AsReadOnly(omitted),
            expectedSetId
        );
        ValidateFinalizationShape(transaction, candidate);
        IReadOnlyList<DerivedMemoryRoleSettlement> durable =
            await ReadSettlementsAsync(
                    transaction,
                    cancellationToken
                )
                .ConfigureAwait(false);
        ValidateFinalizationSettlements(candidate, durable);
        string json = JsonSerializer.Serialize(
            ToDto(candidate),
            JsonOptions
        );
        EnsureSize(json, MaxFinalizationFileBytes, "finalization");

        await using FileStream writeLock = await _repository
            .AcquireWriteLockAsync(cancellationToken)
            .ConfigureAwait(false);
        _repository.EnsureDirectory(FinalizationsDirectory);
        string path = GetFinalizationPath(transaction.TransactionId);
        if (File.Exists(path)) {
            DerivedMemoryOrchestrationFinalization existing =
                await ReadFinalizationRequiredAsync(
                        path,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            ValidateFinalizationShape(transaction, existing);
            ValidateFinalizationSettlements(existing, durable);
            return existing;
        }
        await _repository.WriteFileAtomicallyAsync(
                path,
                json,
                overwrite: false,
                cancellationToken
            )
            .ConfigureAwait(false);
        return candidate;
    }

    internal async ValueTask<DerivedMemoryRoleSettlement> SettleAsync(
        DerivedMemoryOrchestrationTransaction transaction,
        DerivedMemoryRoleSettlement settlement,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(settlement);
        ValidateSettlementShape(transaction, settlement);
        DerivedMemoryRoleProvisioning provision = transaction.Roles.Single(
            role => string.Equals(
                role.RoleId,
                settlement.RoleId,
                StringComparison.Ordinal
            ));
        DerivedMemoryArtifact artifact =
            await _repository.Artifacts.TryReadArtifactAsync(
                    settlement.ArtifactId,
                    cancellationToken
                )
                .ConfigureAwait(false)
            ?? throw new InvalidDataException(
                $"Settlement artifact '{settlement.ArtifactId}' is missing."
            );
        ValidateArtifact(transaction, provision, settlement, artifact);

        var dto = new SettlementDto(
            SettlementSchema,
            settlement.TransactionId,
            settlement.RoleId,
            settlement.ArtifactId,
            settlement.ArtifactOutcome
        );
        string json = JsonSerializer.Serialize(dto, JsonOptions);
        EnsureSize(json, MaxSettlementFileBytes, "settlement");
        await using FileStream writeLock = await _repository
            .AcquireWriteLockAsync(cancellationToken)
            .ConfigureAwait(false);
        string directory = GetSettlementDirectory(
            transaction.TransactionId
        );
        _repository.EnsureDirectory(directory);
        string path = Path.Combine(
            directory,
            GetSettlementFileName(settlement.RoleId)
        );
        if (File.Exists(path)) {
            DerivedMemoryRoleSettlement existing =
                await ReadSettlementRequiredAsync(
                        path,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            if (existing != settlement) {
                throw new InvalidDataException(
                    $"Immutable role settlement collision for '{settlement.RoleId}'."
                );
            }
            return existing;
        }
        await _repository.WriteFileAtomicallyAsync(
                path,
                json,
                overwrite: false,
                cancellationToken
            )
            .ConfigureAwait(false);
        return settlement;
    }

    internal static DerivedMemoryRoleProvisioning[]
        ValidateAndCanonicalize(
        DerivedArtifactSetPolicy policy,
        IReadOnlyList<DerivedMemoryRoleProvisioning> roles
    ) {
        IReadOnlyDictionary<string, DerivedArtifactSetRoleRequirement>
            requirements = policy.ValidateAndSnapshot();
        ArgumentNullException.ThrowIfNull(roles);
        if (roles.Count != requirements.Count) {
            throw new ArgumentException(
                "Role provisioning must exactly cover the ArtifactSet policy.",
                nameof(roles)
            );
        }
        var seenRoles = new HashSet<string>(StringComparer.Ordinal);
        var seenTargets =
            new HashSet<(MemoryPackCarrier Carrier, string BlockKey)>();
        foreach (DerivedMemoryRoleProvisioning role in roles) {
            ArgumentNullException.ThrowIfNull(role);
            RequireToken(role.RoleId, nameof(role.RoleId));
            RequireToken(role.ProfileId, nameof(role.ProfileId));
            RequireToken(role.Producer, nameof(role.Producer));
            RequireToken(role.CandidateId, nameof(role.CandidateId));
            RequireToken(role.AttemptId, nameof(role.AttemptId));
            RequireFingerprint(
                role.ProducerFingerprint,
                nameof(role.ProducerFingerprint)
            );
            RequireFingerprint(
                role.PromptFingerprint,
                nameof(role.PromptFingerprint)
            );
            RequireFingerprint(
                role.ModelFingerprint,
                nameof(role.ModelFingerprint)
            );
            DerivedArtifactSetPolicy.ValidateTarget(
                role.Target,
                nameof(role.Target)
            );
            if (!requirements.TryGetValue(
                    role.RoleId,
                    out DerivedArtifactSetRoleRequirement? requirement
                )
                || requirement.Required != role.Required
                || requirement.Target != role.Target) {
                throw new ArgumentException(
                    $"Role provisioning '{role.RoleId}' does not match policy.",
                    nameof(roles)
                );
            }
            if (!seenRoles.Add(role.RoleId)
                || !seenTargets.Add((
                    role.Target.Carrier,
                    role.Target.BlockKey
                ))) {
                throw new ArgumentException(
                    "Role provisioning identities and targets must be unique.",
                    nameof(roles)
                );
            }
            if (!DerivedMemoryRoleExecutionModes.IsDefined(
                    role.ExecutionMode
                )) {
                throw new ArgumentException(
                    $"Role '{role.RoleId}' execution mode is invalid.",
                    nameof(roles)
                );
            }
            bool selecting = string.Equals(
                role.ExecutionMode,
                DerivedMemoryRoleExecutionModes.SelectExisting,
                StringComparison.Ordinal
            );
            if (selecting != (role.SelectedArtifactId is not null)) {
                throw new ArgumentException(
                    $"Role '{role.RoleId}' exact artifact selection is inconsistent.",
                    nameof(roles)
                );
            }
            if (role.SelectedArtifactId is not null) {
                ValidateHashId(
                    role.SelectedArtifactId,
                    "dma_",
                    nameof(role.SelectedArtifactId)
                );
            }
        }
        return [
            .. roles.OrderBy(
                static role => role.RoleId,
                StringComparer.Ordinal
            )
        ];
    }

    public static void ValidateProvisioningStructure(
        DerivedArtifactSetPolicy policy,
        IReadOnlyList<DerivedMemoryRoleProvisioning> roles
    ) => _ = ValidateAndCanonicalize(policy, roles);

    internal static void ValidateArtifact(
        DerivedMemoryOrchestrationTransaction transaction,
        DerivedMemoryRoleProvisioning provision,
        DerivedMemoryRoleSettlement settlement,
        DerivedMemoryArtifact artifact
    ) {
        if (!string.Equals(
                artifact.ArtifactId,
                settlement.ArtifactId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                artifact.Outcome,
                settlement.ArtifactOutcome,
                StringComparison.Ordinal
            )
            || !string.Equals(
                artifact.EpochId,
                transaction.EpochId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                artifact.EpochPlanFingerprint,
                transaction.EpochPlanFingerprint,
                StringComparison.Ordinal
            )
            || !string.Equals(
                artifact.RoleId,
                provision.RoleId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                artifact.ProfileId,
                provision.ProfileId,
                StringComparison.Ordinal
            )
            || artifact.Target != provision.Target
            || !string.Equals(
                artifact.Producer,
                provision.Producer,
                StringComparison.Ordinal
            )
            || !string.Equals(
                artifact.ProducerFingerprint,
                provision.ProducerFingerprint,
                StringComparison.Ordinal
            )
            || !string.Equals(
                artifact.PromptFingerprint,
                provision.PromptFingerprint,
                StringComparison.Ordinal
            )
            || !string.Equals(
                artifact.ModelFingerprint,
                provision.ModelFingerprint,
                StringComparison.Ordinal
            )
            || !string.Equals(
                artifact.CandidateId,
                provision.CandidateId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                artifact.AttemptId,
                provision.AttemptId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                artifact.InputSetId,
                transaction.InputSetId,
                StringComparison.Ordinal
            )
            || provision.SelectedArtifactId is not null
                && !string.Equals(
                    provision.SelectedArtifactId,
                    artifact.ArtifactId,
                    StringComparison.Ordinal
                )) {
            throw new InvalidDataException(
                $"Artifact '{artifact.ArtifactId}' does not satisfy role job '{provision.RoleId}'."
            );
        }
        bool identityMode = string.Equals(
            provision.ExecutionMode,
            DerivedMemoryRoleExecutionModes.Identity,
            StringComparison.Ordinal
        );
        bool produceMode = string.Equals(
            provision.ExecutionMode,
            DerivedMemoryRoleExecutionModes.Produce,
            StringComparison.Ordinal
        );
        bool identityArtifact = string.Equals(
            artifact.Outcome,
            DerivedMemoryArtifactOutcomes.Identity,
            StringComparison.Ordinal
        );
        if (identityMode && !identityArtifact
            || produceMode && identityArtifact) {
            throw new InvalidDataException(
                $"Artifact '{artifact.ArtifactId}' outcome does not match role execution mode."
            );
        }
    }

    internal static bool TransactionsEquivalent(
        DerivedMemoryOrchestrationTransaction left,
        DerivedMemoryOrchestrationTransaction right
    ) =>
        string.Equals(
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
            left.InputSetId,
            right.InputSetId,
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
        && left.Roles.SequenceEqual(right.Roles);

    private async ValueTask<DerivedMemoryOrchestrationTransaction>
        ReadTransactionRequiredAsync(
        string path,
        CancellationToken cancellationToken
    ) {
        TransactionDto dto = await ReadAsync<TransactionDto>(
                path,
                MaxTransactionFileBytes,
                "transaction",
                cancellationToken
            )
            .ConfigureAwait(false);
        if (!string.Equals(
                dto.Schema,
                TransactionSchema,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                $"Orchestration transaction schema is invalid: {path}"
            );
        }
        var policy = new DerivedArtifactSetPolicy(
            dto.PolicyId,
            dto.PolicyFingerprint,
            dto.CoherenceGroup,
            dto.Roles.Select(static role =>
                new DerivedArtifactSetRoleRequirement(
                    role.RoleId,
                    role.Target.ToContract(),
                    role.Required
                )).ToArray()
        );
        DerivedMemoryRoleProvisioning[] roles = ValidateAndCanonicalize(
            policy,
            dto.Roles.Select(static role => role.ToContract()).ToArray()
        );
        var transaction = new DerivedMemoryOrchestrationTransaction(
            dto.TransactionId,
            dto.JobFingerprint,
            dto.EpochId,
            dto.EpochPlanFingerprint,
            dto.BranchRefId,
            dto.CoherenceGroup,
            dto.TopologyVersion,
            dto.InputSetId,
            dto.PolicyId,
            dto.PolicyFingerprint,
            Array.AsReadOnly(roles)
        );
        TransactionDto canonical = ToDto(transaction);
        string expectedJob =
            "sha256:" + ComputeDomainHash(
                JobFingerprintDomain,
                new JobIdentityDto(
                    transaction.EpochId,
                    transaction.EpochPlanFingerprint,
                    transaction.BranchRefId,
                    transaction.CoherenceGroup,
                    transaction.TopologyVersion,
                    transaction.InputSetId,
                    transaction.PolicyId,
                    transaction.PolicyFingerprint,
                    canonical.Roles
                )
            );
        string expectedId =
            "dmt_" + ComputeDomainHash(
                TransactionIdDomain,
                new TransactionIdentityDto(
                    transaction.EpochId,
                    expectedJob
                )
            );
        if (!string.Equals(
                transaction.JobFingerprint,
                expectedJob,
                StringComparison.Ordinal
            )
            || !string.Equals(
                transaction.TransactionId,
                expectedId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                Path.GetFileName(path),
                $"{expectedId}.json",
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                $"Orchestration transaction identity is invalid: {path}"
            );
        }
        return transaction;
    }

    private async ValueTask<DerivedMemoryRoleSettlement>
        ReadSettlementRequiredAsync(
        string path,
        CancellationToken cancellationToken
    ) {
        SettlementDto dto = await ReadAsync<SettlementDto>(
                path,
                MaxSettlementFileBytes,
                "settlement",
                cancellationToken
            )
            .ConfigureAwait(false);
        if (!string.Equals(
                dto.Schema,
                SettlementSchema,
                StringComparison.Ordinal
            )
            || !DerivedMemoryArtifactOutcomes.IsDefined(
                dto.ArtifactOutcome
            )) {
            throw new InvalidDataException(
                $"Role settlement is invalid: {path}"
            );
        }
        return new DerivedMemoryRoleSettlement(
            dto.TransactionId,
            dto.RoleId,
            dto.ArtifactId,
            dto.ArtifactOutcome
        );
    }

    private async ValueTask<DerivedMemoryOrchestrationFinalization>
        ReadFinalizationRequiredAsync(
        string path,
        CancellationToken cancellationToken
    ) {
        FinalizationDto dto = await ReadAsync<FinalizationDto>(
                path,
                MaxFinalizationFileBytes,
                "finalization",
                cancellationToken
            )
            .ConfigureAwait(false);
        if (!string.Equals(
                dto.Schema,
                FinalizationSchema,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                $"Orchestration finalization schema is invalid: {path}"
            );
        }
        var finalization = new DerivedMemoryOrchestrationFinalization(
            dto.TransactionId,
            dto.JobFingerprint,
            dto.EpochId,
            dto.EpochPlanFingerprint,
            dto.PolicyId,
            dto.PolicyFingerprint,
            dto.ExpectedPreviousSetId,
            dto.AnchorSetups,
            dto.IncludedSettlements,
            dto.OmittedOptionalRoleIds,
            dto.ExpectedSetId
        );
        if (!string.Equals(
                Path.GetFileName(path),
                $"{finalization.TransactionId}.json",
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                $"Orchestration finalization filename is invalid: {path}"
            );
        }
        return finalization;
    }

    private static void ValidateSettlementShape(
        DerivedMemoryOrchestrationTransaction transaction,
        DerivedMemoryRoleSettlement settlement
    ) {
        if (!string.Equals(
                settlement.TransactionId,
                transaction.TransactionId,
                StringComparison.Ordinal
            )
            || !transaction.Roles.Any(role => string.Equals(
                role.RoleId,
                settlement.RoleId,
                StringComparison.Ordinal
            ))) {
            throw new InvalidDataException(
                "Role settlement does not belong to its transaction."
            );
        }
        ValidateHashId(
            settlement.ArtifactId,
            "dma_",
            nameof(settlement.ArtifactId)
        );
        if (!DerivedMemoryArtifactOutcomes.IsDefined(
                settlement.ArtifactOutcome
            )) {
            throw new InvalidDataException(
                "Role settlement artifact outcome is invalid."
            );
        }
    }

    private static void ValidateFinalizationShape(
        DerivedMemoryOrchestrationTransaction transaction,
        DerivedMemoryOrchestrationFinalization finalization
    ) {
        ArgumentNullException.ThrowIfNull(finalization.AnchorSetups);
        ArgumentNullException.ThrowIfNull(
            finalization.IncludedSettlements
        );
        ArgumentNullException.ThrowIfNull(
            finalization.OmittedOptionalRoleIds
        );
        ValidateHashId(
            finalization.ExpectedSetId,
            "das_",
            nameof(finalization.ExpectedSetId)
        );
        if (!string.Equals(
                finalization.TransactionId,
                transaction.TransactionId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                finalization.JobFingerprint,
                transaction.JobFingerprint,
                StringComparison.Ordinal
            )
            || !string.Equals(
                finalization.EpochId,
                transaction.EpochId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                finalization.EpochPlanFingerprint,
                transaction.EpochPlanFingerprint,
                StringComparison.Ordinal
            )
            || !string.Equals(
                finalization.PolicyId,
                transaction.PolicyId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                finalization.PolicyFingerprint,
                transaction.PolicyFingerprint,
                StringComparison.Ordinal
            )
            || !string.Equals(
                finalization.ExpectedPreviousSetId,
                transaction.InputSetId,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "Orchestration finalization does not match its transaction."
            );
        }
        DerivedMemoryRoleSettlement[] included = [
            .. finalization.IncludedSettlements.OrderBy(
                static settlement => settlement.RoleId,
                StringComparer.Ordinal
            )
        ];
        string[] omitted = [
            .. finalization.OmittedOptionalRoleIds.OrderBy(
                static roleId => roleId,
                StringComparer.Ordinal
            )
        ];
        if (!included.SequenceEqual(
                finalization.IncludedSettlements
            )
            || !omitted.SequenceEqual(
                finalization.OmittedOptionalRoleIds
            )
            || included.Select(static item => item.RoleId)
                .Distinct(StringComparer.Ordinal).Count()
                != included.Length
            || omitted.Distinct(StringComparer.Ordinal).Count()
                != omitted.Length) {
            throw new InvalidDataException(
                "Orchestration finalization roles are not canonical and unique."
            );
        }
        var includedRoles = included
            .Select(static item => item.RoleId)
            .ToHashSet(StringComparer.Ordinal);
        var omittedRoles = omitted.ToHashSet(StringComparer.Ordinal);
        foreach (DerivedMemoryRoleSettlement settlement in included) {
            ValidateSettlementShape(transaction, settlement);
        }
        foreach (DerivedMemoryRoleProvisioning role in transaction.Roles) {
            bool includedRole = includedRoles.Contains(role.RoleId);
            bool omittedRole = omittedRoles.Contains(role.RoleId);
            if (role.Required && !includedRole
                || role.Required && omittedRole
                || !role.Required && includedRole == omittedRole) {
                throw new InvalidDataException(
                    $"Finalization role '{role.RoleId}' has an invalid included/omitted disposition."
                );
            }
        }
        if (includedRoles.Count + omittedRoles.Count
            != transaction.Roles.Count) {
            throw new InvalidDataException(
                "Orchestration finalization contains an unknown role."
            );
        }
    }

    private static void ValidateFinalizationSettlements(
        DerivedMemoryOrchestrationFinalization finalization,
        IReadOnlyList<DerivedMemoryRoleSettlement> durableSettlements
    ) {
        IReadOnlyDictionary<string, DerivedMemoryRoleSettlement> durable =
            durableSettlements.ToDictionary(
                static settlement => settlement.RoleId,
                StringComparer.Ordinal
            );
        foreach (DerivedMemoryRoleSettlement included in
                 finalization.IncludedSettlements) {
            if (!durable.TryGetValue(
                    included.RoleId,
                    out DerivedMemoryRoleSettlement? settlement
                )
                || settlement != included) {
                throw new InvalidDataException(
                    $"Finalization role '{included.RoleId}' is not its exact durable settlement."
                );
            }
        }
    }

    private static TransactionDto ToDto(
        DerivedMemoryOrchestrationTransaction transaction
    ) => new(
        TransactionSchema,
        transaction.TransactionId,
        transaction.JobFingerprint,
        transaction.EpochId,
        transaction.EpochPlanFingerprint,
        transaction.BranchRefId,
        transaction.CoherenceGroup,
        transaction.TopologyVersion,
        transaction.InputSetId,
        transaction.PolicyId,
        transaction.PolicyFingerprint,
        transaction.Roles.Select(ToDto).ToArray()
    );

    private static FinalizationDto ToDto(
        DerivedMemoryOrchestrationFinalization finalization
    ) => new(
        FinalizationSchema,
        finalization.TransactionId,
        finalization.JobFingerprint,
        finalization.EpochId,
        finalization.EpochPlanFingerprint,
        finalization.PolicyId,
        finalization.PolicyFingerprint,
        finalization.ExpectedPreviousSetId,
        finalization.AnchorSetups,
        finalization.IncludedSettlements,
        finalization.OmittedOptionalRoleIds,
        finalization.ExpectedSetId
    );

    private static RoleDto ToDto(
        DerivedMemoryRoleProvisioning role
    ) => new(
        role.RoleId,
        role.ProfileId,
        TargetDto.FromContract(role.Target),
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

    private async ValueTask<T> ReadAsync<T>(
        string path,
        long maximumBytes,
        string description,
        CancellationToken cancellationToken
    ) {
        DerivedMemoryPathGuard.EnsureSafeDescendant(
            _repository.SessionJournalRepositoryPath,
            path
        );
        try {
            await using FileStream stream = File.OpenRead(path);
            if (stream.Length > maximumBytes) {
                throw new InvalidDataException(
                    $"Derived-memory {description} exceeds its size limit: {path}"
                );
            }
            return await JsonSerializer.DeserializeAsync<T>(
                    stream,
                    JsonOptions,
                    cancellationToken
                )
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    $"Derived-memory {description} is empty: {path}"
                );
        }
        catch (JsonException exception) {
            throw new InvalidDataException(
                $"Derived-memory {description} is malformed: {path}",
                exception
            );
        }
    }

    private string GetTransactionPath(string transactionId) {
        ValidateHashId(transactionId, "dmt_", nameof(transactionId));
        return Path.Combine(
            TransactionsDirectory,
            $"{transactionId}.json"
        );
    }

    private string GetSettlementDirectory(string transactionId) {
        ValidateHashId(transactionId, "dmt_", nameof(transactionId));
        return Path.Combine(SettlementsDirectory, transactionId);
    }

    private string GetFinalizationPath(string transactionId) {
        ValidateHashId(transactionId, "dmt_", nameof(transactionId));
        return Path.Combine(
            FinalizationsDirectory,
            $"{transactionId}.json"
        );
    }

    private static string GetSettlementFileName(string roleId) =>
        "role_" + ComputeDomainHash(RoleFileDomain, roleId) + ".json";

    private static IEnumerable<string> EnumerateJsonFiles(
        string directory,
        string description
    ) {
        foreach (string path in Directory
                     .EnumerateFiles(directory)
                     .OrderBy(
                         static path => Path.GetFileName(path),
                         StringComparer.Ordinal
                     )) {
            if (!string.Equals(
                    Path.GetExtension(path),
                    ".json",
                    StringComparison.Ordinal
                )) {
                throw new InvalidDataException(
                    $"Derived-memory {description} directory contains an unexpected file: {path}"
                );
            }
            yield return path;
        }
    }

    private static void RejectUnexpectedDirectories(
        string directory,
        string description
    ) {
        string? unexpected = Directory.EnumerateDirectories(directory)
            .OrderBy(
                static path => Path.GetFileName(path),
                StringComparer.Ordinal
            )
            .FirstOrDefault();
        if (unexpected is not null) {
            throw new InvalidDataException(
                $"Derived-memory {description} directory contains an unexpected directory: {unexpected}"
            );
        }
    }

    private static void RejectUnexpectedFiles(
        string directory,
        string description
    ) {
        string? unexpected = Directory.EnumerateFiles(directory)
            .OrderBy(
                static path => Path.GetFileName(path),
                StringComparer.Ordinal
            )
            .FirstOrDefault();
        if (unexpected is not null) {
            throw new InvalidDataException(
                $"Derived-memory {description} directory contains an unexpected file: {unexpected}"
            );
        }
    }

    private static string ComputeDomainHash<T>(
        string domain,
        T value
    ) {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            value,
            IdentityJsonOptions
        );
        byte[] prefix = Encoding.UTF8.GetBytes(domain + "\0");
        byte[] input = new byte[checked(prefix.Length + payload.Length)];
        prefix.CopyTo(input, 0);
        payload.CopyTo(input, prefix.Length);
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }

    private static string ComputeDomainHash(
        string domain,
        string value
    ) => ComputeDomainHash(domain, new StringIdentityDto(value));

    private static void EnsureSize(
        string json,
        long maximumBytes,
        string description
    ) {
        if (Encoding.UTF8.GetByteCount(json) > maximumBytes) {
            throw new InvalidDataException(
                $"Derived-memory {description} exceeds its size limit."
            );
        }
    }

    private static void RequireToken(
        string value,
        string parameterName
    ) => DerivedArtifactSetPolicy.ValidateToken(value, parameterName);

    private static void RequireFingerprint(
        string value,
        string parameterName
    ) {
        if (value is not { Length: 71 }
            || !value.StartsWith("sha256:", StringComparison.Ordinal)
            || !value[7..].All(
                static ch => ch is >= '0' and <= '9'
                    or >= 'a' and <= 'f'
            )) {
            throw new ArgumentException(
                "Fingerprint must be canonical lowercase sha256.",
                parameterName
            );
        }
    }

    private static void ValidateHashId(
        string value,
        string prefix,
        string parameterName
    ) {
        if (value is null
            || value.Length != 68
            || !value.StartsWith(prefix, StringComparison.Ordinal)
            || !value.AsSpan(4).ToString().All(
                static ch => ch is >= '0' and <= '9'
                    or >= 'a' and <= 'f'
            )) {
            throw new ArgumentException(
                $"Identity must be a canonical {prefix} hash id.",
                parameterName
            );
        }
    }

    private sealed record JobIdentityDto(
        string EpochId,
        string EpochPlanFingerprint,
        RefId BranchRefId,
        string CoherenceGroup,
        string TopologyVersion,
        string? InputSetId,
        string PolicyId,
        string PolicyFingerprint,
        IReadOnlyList<RoleDto> Roles
    );

    private sealed record TransactionIdentityDto(
        string EpochId,
        string JobFingerprint
    );

    private sealed record StringIdentityDto(string Value);

    private sealed record TransactionDto(
        [property: JsonPropertyOrder(0)] string Schema,
        [property: JsonPropertyOrder(1)] string TransactionId,
        [property: JsonPropertyOrder(2)] string JobFingerprint,
        [property: JsonPropertyOrder(3)] string EpochId,
        [property: JsonPropertyOrder(4)] string EpochPlanFingerprint,
        [property: JsonPropertyOrder(5)] RefId BranchRefId,
        [property: JsonPropertyOrder(6)] string CoherenceGroup,
        [property: JsonPropertyOrder(7)] string TopologyVersion,
        [property: JsonPropertyOrder(8)] string? InputSetId,
        [property: JsonPropertyOrder(9)] string PolicyId,
        [property: JsonPropertyOrder(10)] string PolicyFingerprint,
        [property: JsonPropertyOrder(11)] IReadOnlyList<RoleDto> Roles
    );

    private sealed record RoleDto(
        [property: JsonPropertyOrder(0)] string RoleId,
        [property: JsonPropertyOrder(1)] string ProfileId,
        [property: JsonPropertyOrder(2)] TargetDto Target,
        [property: JsonPropertyOrder(3)] bool Required,
        [property: JsonPropertyOrder(4)] string Producer,
        [property: JsonPropertyOrder(5)] string ProducerFingerprint,
        [property: JsonPropertyOrder(6)] string PromptFingerprint,
        [property: JsonPropertyOrder(7)] string ModelFingerprint,
        [property: JsonPropertyOrder(8)] string ExecutionMode,
        [property: JsonPropertyOrder(9)] string CandidateId,
        [property: JsonPropertyOrder(10)] string AttemptId,
        [property: JsonPropertyOrder(11)] string? SelectedArtifactId
    ) {
        public DerivedMemoryRoleProvisioning ToContract() => new(
            RoleId,
            ProfileId,
            Target.ToContract(),
            Required,
            Producer,
            ProducerFingerprint,
            PromptFingerprint,
            ModelFingerprint,
            ExecutionMode,
            CandidateId,
            AttemptId,
            SelectedArtifactId
        );
    }

    private sealed record TargetDto(
        [property: JsonPropertyOrder(0)] string Carrier,
        [property: JsonPropertyOrder(1)] string BlockKey
    ) {
        public static TargetDto FromContract(
            MemoryPackBlockPath target
        ) => new(
            MemoryPackCarrierTokens.ToStorageToken(target.Carrier),
            target.BlockKey
        );

        public MemoryPackBlockPath ToContract() {
            if (!MemoryPackCarrierTokens.TryParseStorageToken(
                    Carrier,
                    out MemoryPackCarrier carrier
                )) {
                throw new InvalidDataException(
                    $"Unknown memory-pack carrier '{Carrier}'."
                );
            }
            return new MemoryPackBlockPath(carrier, BlockKey);
        }
    }

    private sealed record SettlementDto(
        [property: JsonPropertyOrder(0)] string Schema,
        [property: JsonPropertyOrder(1)] string TransactionId,
        [property: JsonPropertyOrder(2)] string RoleId,
        [property: JsonPropertyOrder(3)] string ArtifactId,
        [property: JsonPropertyOrder(4)] string ArtifactOutcome
    );

    private sealed record FinalizationDto(
        [property: JsonPropertyOrder(0)] string Schema,
        [property: JsonPropertyOrder(1)] string TransactionId,
        [property: JsonPropertyOrder(2)] string JobFingerprint,
        [property: JsonPropertyOrder(3)] string EpochId,
        [property: JsonPropertyOrder(4)] string EpochPlanFingerprint,
        [property: JsonPropertyOrder(5)] string PolicyId,
        [property: JsonPropertyOrder(6)] string PolicyFingerprint,
        [property: JsonPropertyOrder(7)] string? ExpectedPreviousSetId,
        [property: JsonPropertyOrder(8)]
            SessionContextAnchorSetupReferences AnchorSetups,
        [property: JsonPropertyOrder(9)]
            IReadOnlyList<DerivedMemoryRoleSettlement>
                IncludedSettlements,
        [property: JsonPropertyOrder(10)]
            IReadOnlyList<string> OmittedOptionalRoleIds,
        [property: JsonPropertyOrder(11)] string ExpectedSetId
    );
}
