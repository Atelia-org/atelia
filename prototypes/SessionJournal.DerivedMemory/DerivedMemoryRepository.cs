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
        Recaps = new DerivedRecapStore(this);
        ArtifactSets = new DerivedArtifactSetStore(this);
        EpochPlanner = new DerivedArtifactEpochPlanner(this);
    }

    public string SessionJournalRepositoryPath { get; }

    public string DerivedRoot { get; }

    public string MemoryRoot { get; }

    public DerivedRecapStore Recaps { get; }

    public DerivedArtifactSetStore ArtifactSets { get; }

    public DerivedArtifactEpochPlanner EpochPlanner { get; }

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
        IReadOnlyList<DerivedRecapArtifact> artifacts =
            await Recaps.ReadInventoryStrictAsync(cancellationToken)
                .ConfigureAwait(false);
        IReadOnlyDictionary<string, DerivedRecapArtifact> artifactsById =
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
        DerivedArtifactEpochPlanner.ValidateInventory(
            epochInventory,
            inventory.Sets.ToDictionary(
                static set => set.SetId,
                StringComparer.Ordinal
            )
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

        SessionJournalEngine? ownedEngine = null;
        try {
            if (epochInventory.Epochs.Count > 0) {
                SessionJournalEngine authorityEngine = engine
                    ?? (ownedEngine = SessionJournalEngine.Open(
                        SessionJournalRepositoryPath
                    ));
                _ = EpochPlanner.ValidateRawAuthority(
                    authorityEngine,
                    epochInventory.Epochs,
                    epochInventory.Configs,
                    cancellationToken
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
            epochInventory.LatestEpochs.Count
        );
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
