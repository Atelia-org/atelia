using System.Globalization;
using System.Text;

namespace Atelia.SessionJournal.DerivedMemory;

/// <summary>
/// Repository-local owner for rebuildable derived-memory state. It never owns or mutates the raw
/// SessionJournal event sequence.
/// </summary>
public sealed class DerivedMemoryRepository {
    private const int WriteLockMaxAttempts = 200;
    private static readonly TimeSpan WriteLockRetryDelay =
        TimeSpan.FromMilliseconds(25);

    private DerivedMemoryRepository(string sessionJournalRepositoryPath) {
        SessionJournalRepositoryPath = sessionJournalRepositoryPath;
        DerivedRoot = Path.Combine(SessionJournalRepositoryPath, "derived");
        MemoryRoot = Path.Combine(DerivedRoot, "memory", "v1");
        WriteLockPath = Path.Combine(DerivedRoot, ".derived-memory.lock");
        Artifacts = new DerivedMemoryArtifactStore(this);
        ArtifactSets = new DerivedArtifactSetStore(this);
        EpochPlanner = new DerivedArtifactEpochPlanner(this);
        Orchestrations = new DerivedMemoryOrchestrationStore(this);
    }

    public string SessionJournalRepositoryPath { get; }

    public string DerivedRoot { get; }

    public string MemoryRoot { get; }

    public DerivedMemoryArtifactStore Artifacts { get; }

    public DerivedArtifactSetStore ArtifactSets { get; }

    public DerivedArtifactEpochPlanner EpochPlanner { get; }

    public DerivedMemoryOrchestrationStore Orchestrations { get; }

    internal string WriteLockPath { get; }

    public static DerivedMemoryRepository Open(
        string sessionJournalRepositoryPath
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            sessionJournalRepositoryPath
        );
        string fullPath = Path.GetFullPath(sessionJournalRepositoryPath);
        DerivedMemoryPathGuard.EnsureExistingPathChainHasNoReparsePoint(
            fullPath
        );
        if (!Directory.Exists(fullPath)) {
            throw new DirectoryNotFoundException(
                $"SessionJournal repository does not exist: {fullPath}"
            );
        }
        return new DerivedMemoryRepository(fullPath);
    }

    public async ValueTask<DerivedMemoryValidationReport> ValidateAsync(
        CancellationToken cancellationToken = default
    ) => await ValidateCoreAsync(
            engine: null,
            cancellationToken
        )
        .ConfigureAwait(false);

    public async ValueTask<DerivedMemoryValidationReport> ValidateAsync(
        SessionJournalEngine engine,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        return await ValidateCoreAsync(engine, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<DerivedMemoryValidationReport> ValidateCoreAsync(
        SessionJournalEngine? engine,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<DerivedMemoryArtifact> artifacts =
            await Artifacts.ReadInventoryStrictAsync(cancellationToken)
                .ConfigureAwait(false);
        IReadOnlyDictionary<string, DerivedMemoryArtifact> artifactsById =
            artifacts.ToDictionary(
                static artifact => artifact.ArtifactId,
                StringComparer.Ordinal
            );
        DerivedArtifactSetInventory inventory =
            await ArtifactSets.ReadInventoryAsync(
                    artifactsById,
                    cancellationToken
                )
                .ConfigureAwait(false);
        DerivedArtifactEpochInventory epochInventory =
            await EpochPlanner.ReadInventoryAsync(cancellationToken)
                .ConfigureAwait(false);
        DerivedMemoryOrchestrationInventory orchestrationInventory =
            await Orchestrations.ReadInventoryAsync(cancellationToken)
                .ConfigureAwait(false);
        DerivedArtifactEpochPlanner.ValidateInventory(
            epochInventory,
            inventory.Sets.ToDictionary(
                static set => set.SetId,
                StringComparer.Ordinal
            )
        );
        IReadOnlyDictionary<string, DerivedArtifactEpochPlan> epochsById =
            epochInventory.Epochs.ToDictionary(
                static epoch => epoch.EpochId,
                StringComparer.Ordinal
            );
        IReadOnlyDictionary<string, DerivedArtifactSet> setsById =
            inventory.Sets.ToDictionary(
                static set => set.SetId,
                StringComparer.Ordinal
            );

        var setsByKey = inventory.Sets
            .GroupBy(DerivedArtifactSetExactKey.FromSet)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<DerivedArtifactSet>)[.. group]
            );
        var pointersByKey = inventory.LatestPointers
            .GroupBy(DerivedArtifactSetExactKey.FromPointer)
            .ToDictionary(
                static group => group.Key,
                static group =>
                    (IReadOnlyList<DerivedArtifactSetLatestPointer>)[.. group]
            );

        foreach ((DerivedArtifactSetExactKey key,
                     IReadOnlyList<DerivedArtifactSet> sets) in setsByKey) {
            IReadOnlyList<DerivedArtifactSetRoleRequirement> roleSnapshot =
                sets[0].RoleRequirements;
            if (sets.Skip(1).Any(
                    set => !set.RoleRequirements.SequenceEqual(roleSnapshot)
                )) {
                throw new InvalidDataException(
                    $"ArtifactSet exact key '{key}' contains role-snapshot drift."
                );
            }
            string tipId = ValidateExactKeyLineage(
                [
                    .. sets.Select(
                        static set => new DerivedArtifactSetLineageNode(
                            set.SetId,
                            set.PreviousSetId
                        )
                    )
                ]
            );
            if (!pointersByKey.TryGetValue(
                    key,
                    out IReadOnlyList<DerivedArtifactSetLatestPointer>? pointers
                )
                || pointers.Count != 1) {
                throw new InvalidDataException(
                    $"ArtifactSet exact key '{key}' requires exactly one latest pointer."
                );
            }
            if (!string.Equals(
                    pointers[0].SetId,
                    tipId,
                    StringComparison.Ordinal
                )) {
                throw new InvalidDataException(
                    $"ArtifactSet exact key '{key}' latest pointer is stale."
                );
            }
        }
        foreach (DerivedArtifactSetExactKey pointerKey in pointersByKey.Keys) {
            if (!setsByKey.ContainsKey(pointerKey)) {
                throw new InvalidDataException(
                    $"ArtifactSet latest pointer '{pointerKey}' has no matching sets."
                );
            }
        }
        ValidateArtifactEpochDependencies(
            artifacts,
            epochsById,
            setsById
        );
        ValidateSetOrchestrationDependencies(
            inventory.Sets,
            epochsById,
            orchestrationInventory
        );
        await ValidateFinalizationCandidateIdentitiesAsync(
                orchestrationInventory,
                cancellationToken
            )
            .ConfigureAwait(false);

        SessionJournalEngine? ownedEngine = null;
        try {
            if (epochInventory.Epochs.Count > 0) {
                SessionJournalEngine authorityEngine = engine
                    ?? (ownedEngine = SessionJournalEngine.Open(
                        SessionJournalRepositoryPath
                    ));
                DerivedArtifactEpochRawAuthorityValidation
                    rawAuthority =
                    EpochPlanner.ValidateRawAuthorityDetailed(
                    authorityEngine,
                    epochInventory.Epochs,
                    epochInventory.Configs,
                    cancellationToken
                );
                ValidateArtifactAnchorAuthority(
                    artifacts,
                    rawAuthority.EndSetupsByEpochId
                );
            }
        }
        finally {
            ownedEngine?.Dispose();
        }

        return new DerivedMemoryValidationReport(
            artifacts.Count,
            inventory.Sets.Count,
            inventory.LatestPointers.Count,
            setsByKey.Count,
            epochInventory.Configs.Count,
            epochInventory.CurrentConfigs.Count,
            epochInventory.Epochs.Count,
            epochInventory.LatestEpochs.Count,
            orchestrationInventory.Transactions.Count,
            orchestrationInventory.Settlements.Count,
            orchestrationInventory.Finalizations.Count
        );
    }

    private static void ValidateSetOrchestrationDependencies(
        IReadOnlyList<DerivedArtifactSet> sets,
        IReadOnlyDictionary<string, DerivedArtifactEpochPlan> epochsById,
        DerivedMemoryOrchestrationInventory orchestrationInventory
    ) {
        IReadOnlyDictionary<string, DerivedMemoryOrchestrationTransaction>
            transactions = orchestrationInventory.Transactions.ToDictionary(
                static transaction => transaction.TransactionId,
                StringComparer.Ordinal
            );
        var settlements = orchestrationInventory.Settlements
            .GroupBy(static settlement => settlement.TransactionId)
            .ToDictionary(
                static group => group.Key,
                static group => group.ToDictionary(
                    settlement => settlement.RoleId,
                    StringComparer.Ordinal
                ),
                StringComparer.Ordinal
            );
        IReadOnlyDictionary<string, DerivedMemoryOrchestrationFinalization>
            finalizations = orchestrationInventory.Finalizations
                .ToDictionary(
                    static finalization => finalization.TransactionId,
                    StringComparer.Ordinal
                );
        foreach (DerivedMemoryOrchestrationTransaction transaction in
                 orchestrationInventory.Transactions) {
            if (!epochsById.TryGetValue(
                    transaction.EpochId,
                    out DerivedArtifactEpochPlan? epoch
                )
                || !string.Equals(
                    transaction.EpochPlanFingerprint,
                    DerivedMemoryMaintainerRunner
                        .GetEpochPlanFingerprint(epoch),
                    StringComparison.Ordinal
                )
                || !string.Equals(
                    transaction.LineageKey,
                    epoch.LineageKey,
                    StringComparison.Ordinal
                )
                || !string.Equals(
                    transaction.CoherenceGroup,
                    epoch.CoherenceGroup,
                    StringComparison.Ordinal
                )
                || !string.Equals(
                    transaction.TopologyVersion,
                    epoch.TopologyVersion,
                    StringComparison.Ordinal
                )
                || !string.Equals(
                    transaction.InputSetId,
                    epoch.InputSetId,
                    StringComparison.Ordinal
                )) {
                throw new InvalidDataException(
                    $"Orchestration transaction '{transaction.TransactionId}' does not match its exact epoch."
                );
            }
        }
        foreach (DerivedMemoryOrchestrationFinalization finalization in
                 orchestrationInventory.Finalizations) {
            Dictionary<string, DerivedMemoryRoleSettlement> durable =
                settlements.TryGetValue(
                    finalization.TransactionId,
                    out Dictionary<string, DerivedMemoryRoleSettlement>?
                        value
                )
                    ? value
                    : throw new InvalidDataException(
                        $"Finalization '{finalization.TransactionId}' has no durable settlements."
                    );
            foreach (DerivedMemoryRoleSettlement included in
                     finalization.IncludedSettlements) {
                if (!durable.TryGetValue(
                        included.RoleId,
                        out DerivedMemoryRoleSettlement? settlement
                    )
                    || settlement != included) {
                    throw new InvalidDataException(
                        $"Finalization '{finalization.TransactionId}' does not reference an exact durable settlement."
                    );
                }
            }
            DerivedArtifactSet[] published = [
                .. sets.Where(set => string.Equals(
                    set.TransactionId,
                    finalization.TransactionId,
                    StringComparison.Ordinal
                ))
            ];
            if (published.Length > 1
                || published.Length == 1
                    && !string.Equals(
                        published[0].SetId,
                        finalization.ExpectedSetId,
                        StringComparison.Ordinal
                    )) {
                throw new InvalidDataException(
                    $"Finalization '{finalization.TransactionId}' does not close to one exact set."
                );
            }
        }
        foreach (DerivedArtifactSet set in sets) {
            if (!epochsById.TryGetValue(
                    set.EpochId,
                    out DerivedArtifactEpochPlan? epoch
                )
                || !transactions.TryGetValue(
                    set.TransactionId,
                    out DerivedMemoryOrchestrationTransaction? transaction
                )
                || !finalizations.TryGetValue(
                    set.TransactionId,
                    out DerivedMemoryOrchestrationFinalization?
                        finalization
                )
                || !string.Equals(
                    finalization.ExpectedSetId,
                    set.SetId,
                    StringComparison.Ordinal
                )
                || !DerivedMemoryOrchestrationStore
                    .TransactionsEquivalent(
                        transaction,
                        new DerivedMemoryOrchestrationTransaction(
                            set.TransactionId,
                            set.JobFingerprint,
                            set.EpochId,
                            set.EpochPlanFingerprint,
                            set.LineageKey,
                            set.CoherenceGroup,
                            set.TopologyVersion,
                            set.PreviousSetId,
                            set.PolicyId,
                            set.PolicyFingerprint,
                            set.RoleProvisioning
                        )
                    )
                || !string.Equals(
                    set.EpochPlanFingerprint,
                    DerivedMemoryMaintainerRunner
                        .GetEpochPlanFingerprint(epoch),
                    StringComparison.Ordinal
                )
                || !string.Equals(
                    set.PreviousSetId,
                    epoch.InputSetId,
                    StringComparison.Ordinal
                )
                || set.CommonAnchor != epoch.SourceEndInclusive) {
                throw new InvalidDataException(
                    $"ArtifactSet '{set.SetId}' does not match its exact epoch/transaction closure."
                );
            }
            if (!settlements.TryGetValue(
                    set.TransactionId,
                    out Dictionary<string, DerivedMemoryRoleSettlement>?
                        byRole
                )) {
                throw new InvalidDataException(
                    $"ArtifactSet '{set.SetId}' transaction has no settlements."
                );
            }
            foreach (DerivedArtifactSetMember member in set.Members) {
                if (!byRole.TryGetValue(
                        member.RoleId,
                        out DerivedMemoryRoleSettlement? settlement
                    )
                    || !string.Equals(
                        settlement.ArtifactId,
                        member.ArtifactId,
                        StringComparison.Ordinal
                    )
                    || !string.Equals(
                        settlement.ArtifactOutcome,
                        member.Outcome,
                        StringComparison.Ordinal
                    )) {
                    throw new InvalidDataException(
                        $"ArtifactSet '{set.SetId}' member is not its exact durable settlement."
                    );
                }
            }
        }
    }

    private async ValueTask ValidateFinalizationCandidateIdentitiesAsync(
        DerivedMemoryOrchestrationInventory orchestrationInventory,
        CancellationToken cancellationToken
    ) {
        IReadOnlyDictionary<string, DerivedMemoryOrchestrationTransaction>
            transactions = orchestrationInventory.Transactions.ToDictionary(
                static transaction => transaction.TransactionId,
                StringComparer.Ordinal
            );
        foreach (DerivedMemoryOrchestrationFinalization finalization in
                 orchestrationInventory.Finalizations) {
            DerivedMemoryOrchestrationTransaction transaction =
                transactions[finalization.TransactionId];
            DerivedArtifactSet candidate =
                await ArtifactSets.RebuildFinalizedCandidateAsync(
                        transaction,
                        finalization,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            if (!string.Equals(
                    candidate.SetId,
                    finalization.ExpectedSetId,
                    StringComparison.Ordinal
                )) {
                throw new InvalidDataException(
                    $"Finalization '{finalization.TransactionId}' expected set identity is invalid."
                );
            }
        }
    }

    private static void ValidateArtifactEpochDependencies(
        IReadOnlyList<DerivedMemoryArtifact> artifacts,
        IReadOnlyDictionary<string, DerivedArtifactEpochPlan> epochsById,
        IReadOnlyDictionary<string, DerivedArtifactSet> setsById
    ) {
        foreach (DerivedMemoryArtifact artifact in artifacts) {
            if (!epochsById.TryGetValue(
                    artifact.EpochId,
                    out DerivedArtifactEpochPlan? epoch
                )) {
                throw new InvalidDataException(
                    $"Derived-memory artifact '{artifact.ArtifactId}' references missing epoch '{artifact.EpochId}'."
                );
            }
            if (!string.Equals(
                    artifact.EpochPlanFingerprint,
                    DerivedMemoryMaintainerRunner
                        .GetEpochPlanFingerprint(epoch),
                    StringComparison.Ordinal
                )
                || artifact.SourceRawHead != epoch.PlannedAtRawHead
                || artifact.SourceStartExclusive
                    != epoch.SourceStartExclusive
                || artifact.SourceEndInclusive
                    != epoch.SourceEndInclusive
                || artifact.AnchorRawEvent
                    != epoch.SourceEndInclusive
                || artifact.RawStartSetups != epoch.RawStartSetups
                || !string.Equals(
                    artifact.InputSetId,
                    epoch.InputSetId,
                    StringComparison.Ordinal
                )) {
                throw new InvalidDataException(
                    $"Derived-memory artifact '{artifact.ArtifactId}' does not match its durable epoch identity."
                );
            }

            if (epoch.InputSetId is null) {
                if (artifact.PreviousRoleArtifact is not null
                    || artifact.InputMembers.Count != 0) {
                    throw new InvalidDataException(
                        $"Genesis artifact '{artifact.ArtifactId}' has non-empty input dependencies."
                    );
                }
                continue;
            }
            if (!setsById.TryGetValue(
                    epoch.InputSetId,
                    out DerivedArtifactSet? inputSet
                )) {
                throw new InvalidDataException(
                    $"Artifact '{artifact.ArtifactId}' input set '{epoch.InputSetId}' is missing."
                );
            }
            DerivedMemoryArtifactInputMember[] expectedMembers = [
                .. inputSet.Members
                    .OrderBy(
                        static member => member.RoleId,
                        StringComparer.Ordinal
                    )
                    .Select(static member =>
                        new DerivedMemoryArtifactInputMember(
                            member.RoleId,
                            member.ArtifactId,
                            member.Target,
                            member.ContentSha256
                        ))
            ];
            if (!artifact.InputMembers.SequenceEqual(expectedMembers)) {
                throw new InvalidDataException(
                    $"Artifact '{artifact.ArtifactId}' input-member snapshot does not match epoch input set '{inputSet.SetId}'."
                );
            }
            string? expectedPrevious = inputSet.Members
                .SingleOrDefault(member => string.Equals(
                    member.RoleId,
                    artifact.RoleId,
                    StringComparison.Ordinal
                ))
                ?.ArtifactId;
            if (!string.Equals(
                    artifact.PreviousRoleArtifact,
                    expectedPrevious,
                    StringComparison.Ordinal
                )) {
                throw new InvalidDataException(
                    $"Artifact '{artifact.ArtifactId}' previous-role dependency does not match epoch input set."
                );
            }
        }
    }

    private static void ValidateArtifactAnchorAuthority(
        IReadOnlyList<DerivedMemoryArtifact> artifacts,
        IReadOnlyDictionary<
            string,
            SessionContextAnchorSetupReferences
        > endSetupsByEpochId
    ) {
        foreach (DerivedMemoryArtifact artifact in artifacts) {
            SessionContextAnchorSetupReferences authoritative =
                endSetupsByEpochId.TryGetValue(
                    artifact.EpochId,
                    out SessionContextAnchorSetupReferences? value
                )
                    ? value
                    : throw new InvalidDataException(
                        $"Artifact '{artifact.ArtifactId}' has no validated epoch-end setup authority."
                    );
            if (artifact.AnchorSetups != authoritative) {
                throw new InvalidDataException(
                    $"Artifact '{artifact.ArtifactId}' anchor setup references do not match raw authority."
                );
            }
        }
    }

    internal static string ValidateExactKeyLineage(
        IReadOnlyList<DerivedArtifactSetLineageNode> nodes
    ) {
        if (nodes.Count == 0) {
            throw new ArgumentException(
                "ArtifactSet lineage requires at least one node.",
                nameof(nodes)
            );
        }
        var byId = new Dictionary<string, DerivedArtifactSetLineageNode>(
            StringComparer.Ordinal
        );
        foreach (DerivedArtifactSetLineageNode node in nodes) {
            if (!byId.TryAdd(node.SetId, node)) {
                throw new InvalidDataException(
                    $"Duplicate ArtifactSet id '{node.SetId}'."
                );
            }
        }
        foreach (DerivedArtifactSetLineageNode node in nodes) {
            if (node.PreviousSetId is { } previous
                && !byId.ContainsKey(previous)) {
                throw new InvalidDataException(
                    $"ArtifactSet '{node.SetId}' references missing previous set '{previous}'."
                );
            }
        }

        var completed = new HashSet<string>(StringComparer.Ordinal);
        foreach (DerivedArtifactSetLineageNode start in nodes) {
            var visiting = new HashSet<string>(StringComparer.Ordinal);
            DerivedArtifactSetLineageNode? cursor = start;
            while (cursor is not null && !completed.Contains(cursor.SetId)) {
                if (!visiting.Add(cursor.SetId)) {
                    throw new InvalidDataException(
                        "Derived ArtifactSet lineage contains a cycle."
                    );
                }
                cursor = cursor.PreviousSetId is { } previous
                    ? byId[previous]
                    : null;
            }
            completed.UnionWith(visiting);
        }

        var predecessorIds = nodes
            .Where(static node => node.PreviousSetId is not null)
            .Select(static node => node.PreviousSetId!)
            .ToHashSet(StringComparer.Ordinal);
        DerivedArtifactSetLineageNode[] tips = [
            .. nodes.Where(node => !predecessorIds.Contains(node.SetId))
        ];
        if (tips.Length != 1) {
            throw new InvalidDataException(
                "Derived ArtifactSet lineage is forked or disconnected."
            );
        }
        return tips[0].SetId;
    }

    internal async ValueTask<FileStream> AcquireWriteLockAsync(
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureDirectory(DerivedRoot);
        IOException? lastContention = null;
        for (int attempt = 0; attempt < WriteLockMaxAttempts; attempt++) {
            cancellationToken.ThrowIfCancellationRequested();
            DerivedMemoryPathGuard.EnsureSafeDescendant(
                SessionJournalRepositoryPath,
                WriteLockPath
            );
            try {
                return new FileStream(
                    WriteLockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous
                );
            }
            catch (UnauthorizedAccessException) {
                throw;
            }
            catch (IOException exception) {
                lastContention = exception;
                await Task.Delay(
                        WriteLockRetryDelay,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
        }

        throw new IOException(
            $"Timed out acquiring the derived-memory repository lock '{WriteLockPath}'.",
            lastContention
        );
    }

    internal void EnsureDirectory(string path) {
        DerivedMemoryPathGuard.EnsureSafeDescendant(
            SessionJournalRepositoryPath,
            path
        );
        Directory.CreateDirectory(path);
        DerivedMemoryPathGuard.EnsureSafeDescendant(
            SessionJournalRepositoryPath,
            path
        );
    }

    internal async ValueTask WriteFileAtomicallyAsync(
        string finalPath,
        string content,
        bool overwrite,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        string directory = Path.GetDirectoryName(finalPath)
            ?? throw new ArgumentException(
                "Derived-memory file requires a parent directory.",
                nameof(finalPath)
            );
        EnsureDirectory(directory);
        DerivedMemoryPathGuard.EnsureSafeDescendant(
            SessionJournalRepositoryPath,
            finalPath
        );

        string fileName = Path.GetFileName(finalPath);
        string temporaryPath;
        FileStream temporaryStream;
        while (true) {
            temporaryPath = Path.Combine(
                directory,
                $".{fileName}.{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)}.tmp"
            );
            try {
                temporaryStream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 4096,
                    FileOptions.Asynchronous
                );
                break;
            }
            catch (IOException) when (File.Exists(temporaryPath)) {
                // Retry only an actual generated-name collision.
            }
        }

        try {
            await using (temporaryStream.ConfigureAwait(false)) {
                byte[] bytes = new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true
                ).GetBytes(content);
                await temporaryStream
                    .WriteAsync(bytes, cancellationToken)
                    .ConfigureAwait(false);
                await temporaryStream
                    .FlushAsync(cancellationToken)
                    .ConfigureAwait(false);
                temporaryStream.Flush(flushToDisk: true);
            }
            DerivedMemoryPathGuard.EnsureSafeDescendant(
                SessionJournalRepositoryPath,
                finalPath
            );
            File.Move(temporaryPath, finalPath, overwrite);
        }
        catch {
            TryDeleteTemporaryFile(temporaryPath);
            throw;
        }
    }

    private static void TryDeleteTemporaryFile(string path) {
        try {
            if (File.Exists(path)) {
                File.Delete(path);
            }
        }
        catch {
            // Best-effort cleanup of a file created by this operation only.
        }
    }
}

internal sealed record DerivedArtifactSetLineageNode(
    string SetId,
    string? PreviousSetId
);

internal readonly record struct DerivedArtifactSetExactKey(
    string LineageKey,
    string CoherenceGroup,
    string PolicyId,
    string PolicyFingerprint
) {
    public static DerivedArtifactSetExactKey FromSet(
        DerivedArtifactSet set
    ) => new(
        set.LineageKey,
        set.CoherenceGroup,
        set.PolicyId,
        set.PolicyFingerprint
    );

    public static DerivedArtifactSetExactKey FromPointer(
        DerivedArtifactSetLatestPointer pointer
    ) => new(
        pointer.LineageKey,
        pointer.CoherenceGroup,
        pointer.PolicyId,
        pointer.PolicyFingerprint
    );

    public override string ToString() =>
        $"{LineageKey}|{CoherenceGroup}|{PolicyId}|{PolicyFingerprint}";
}

internal static class DerivedMemoryPathGuard {
    public static void EnsureSafeDescendant(string rootPath, string path) {
        string root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(rootPath)
        );
        string candidate = Path.GetFullPath(path);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string prefix = root + Path.DirectorySeparatorChar;
        if (!candidate.Equals(root, comparison)
            && !candidate.StartsWith(prefix, comparison)) {
            throw new InvalidDataException(
                $"Derived-memory path escapes its repository root: {candidate}"
            );
        }
        EnsureExistingPathChainHasNoReparsePoint(candidate);
    }

    public static void EnsureExistingPathChainHasNoReparsePoint(string path) {
        string? cursor = Path.GetFullPath(path);
        while (cursor is not null) {
            try {
                FileAttributes attributes = File.GetAttributes(cursor);
                if ((attributes & FileAttributes.ReparsePoint) != 0) {
                    throw new InvalidDataException(
                        $"Derived-memory path contains a symbolic link or reparse point: {cursor}"
                    );
                }
            }
            catch (FileNotFoundException) {
                // Missing descendants are allowed; existing ancestors are still checked.
            }
            catch (DirectoryNotFoundException) {
                // Missing descendants are allowed; existing ancestors are still checked.
            }
            cursor = Path.GetDirectoryName(cursor);
        }
    }
}
