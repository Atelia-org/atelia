using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atelia.StateJournal;

public sealed partial class Repository {
    private const int CurrentBranchDataVersion = 2;
    private const int BranchReflogEntryVersion = 1;
    private const int BranchRecentHeadCapacity = 16;

    private sealed class BranchState {
        public required string BranchName { get; init; }
        public CommitAddress? Head { get; set; }
        public Revision? LoadedRevision { get; set; }
    }

    private static void CompareAndSwapBranchAtomically(
        string repoDir,
        string branchName,
        CommitAddress? expectedHead,
        CommitAddress newHead,
        string? note
    ) {
        var branchPath = GetBranchFilePath(repoDir, branchName);
        var branchRef = ReadBestBranchRefOrDefault(repoDir, branchName);
        var currentHead = branchRef?.Head;

        if (currentHead != expectedHead) {
            var expectedSeg = expectedHead?.SegmentNumber ?? 0;
            var expectedTicket = expectedHead?.CommitTicket.Ticket.Serialize() ?? 0;
            var actualSeg = currentHead?.SegmentNumber ?? 0;
            var actualTicket = currentHead?.CommitTicket.Ticket.Serialize() ?? 0;
            throw new InvalidOperationException(
                $"branch CAS mismatch: expected seg={expectedSeg}, ticket={expectedTicket}, actual seg={actualSeg}, ticket={actualTicket}."
            );
        }

        WriteBranchAtomically(repoDir, branchName, branchRef, newHead, note, overwrite: true, operation: "advance");
    }

    private static void WriteNewBranchAtomically(string repoDir, string branchName, CommitAddress? head) {
        var branchPath = GetBranchFilePath(repoDir, branchName);
        if (File.Exists(branchPath)) { throw new InvalidOperationException($"Branch file '{branchName}' already exists on disk."); }
        WriteBranchAtomically(repoDir, branchName, previous: null, head, note: null, overwrite: false, operation: "create");
    }

    private static void WriteBranchAtomically(
        string repoDir,
        string branchName,
        BranchRefSnapshot? previous,
        CommitAddress? head,
        string? note,
        bool overwrite,
        string operation
    ) {
        var branchPath = GetBranchFilePath(repoDir, branchName);
        var backupPath = GetBranchBackupFilePath(repoDir, branchName);
        var generation = checked((previous?.Generation ?? 0UL) + 1UL);
        var recentHeads = BuildRecentHeads(head, previous?.Head, previous?.RecentHeads);
        var data = new BranchData {
            Version = CurrentBranchDataVersion,
            Generation = generation,
            Head = head?.ToString(),
            RecentHeads = recentHeads.Count == 0 ? null : recentHeads,
            LastNote = string.IsNullOrWhiteSpace(note) ? null : note,
        };

        Directory.CreateDirectory(Path.GetDirectoryName(branchPath)!);

        if (overwrite
            && File.Exists(branchPath)
            && string.Equals(previous?.SourcePath, branchPath, StringComparison.Ordinal)) {
            File.Copy(branchPath, backupPath + ".tmp", overwrite: true);
            File.Move(backupPath + ".tmp", backupPath, overwrite: true);
        }

        WriteJsonAtomically(branchPath, data, RepositoryJsonContext.Default.BranchData, overwrite);

        if (!File.Exists(backupPath)) {
            WriteJsonAtomically(backupPath, data, RepositoryJsonContext.Default.BranchData, overwrite: true);
        }

        AppendBranchReflog(
            repoDir,
            branchName,
            new BranchReflogEntry {
                Version = BranchReflogEntryVersion,
                Generation = generation,
                TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
                Operation = operation,
                OldHead = previous?.Head?.ToString(),
                NewHead = head?.ToString(),
                Note = string.IsNullOrWhiteSpace(note) ? null : note,
            }
        );
    }

    private static CommitAddress? ReadBranchAddress(string branchPath) {
        var data = ReadBranchData(branchPath);
        return ToBranchRefSnapshot(data, branchPath).Head;
    }

    private static CommitAddress? ReadBranchAddressOrDefault(string branchPath) {
        if (!File.Exists(branchPath)) { return null; }
        return ReadBranchAddress(branchPath);
    }

    private static BranchData ReadBranchData(string branchPath) {
        var json = File.ReadAllText(branchPath);
        return JsonSerializer.Deserialize(json, RepositoryJsonContext.Default.BranchData)
            ?? throw new InvalidDataException($"Branch file '{branchPath}' is empty or invalid.");
    }

    private static BranchRefSnapshot ToBranchRefSnapshot(BranchData data, string branchPath) {
        return data.Version switch {
            1 => ReadV1BranchRefSnapshot(data, branchPath),
            CurrentBranchDataVersion => ReadV2BranchRefSnapshot(data, branchPath),
            _ => throw new InvalidDataException($"Branch file '{branchPath}' has unsupported version {data.Version}."),
        };
    }

    private static BranchRefSnapshot ReadV1BranchRefSnapshot(BranchData data, string branchPath) {
        var head = ReadLegacyHead(data, branchPath);
        return new BranchRefSnapshot(
            Generation: 0,
            Head: head,
            RecentHeads: head is null ? [] : [head.Value],
            LastNote: null,
            SourcePath: branchPath
        );
    }

    private static BranchRefSnapshot ReadV2BranchRefSnapshot(BranchData data, string branchPath) {
        if (data.Generation == 0) { throw new InvalidDataException($"Branch file '{branchPath}' has invalid generation 0."); }

        var head = ParsePersistedHead(data.Head, branchPath);
        var recentHeads = NormalizeRecentHeads(data.RecentHeads, head, branchPath);
        return new BranchRefSnapshot(
            Generation: data.Generation,
            Head: head,
            RecentHeads: recentHeads,
            LastNote: string.IsNullOrWhiteSpace(data.LastNote) ? null : data.LastNote,
            SourcePath: branchPath
        );
    }

    private static CommitAddress? ReadLegacyHead(BranchData data, string branchPath) {
        if (data.Version != 1) { throw new InvalidDataException($"Branch file '{branchPath}' has unsupported version {data.Version}."); }

        // segmentNumber == 0 && ticket == 0 → unborn branch (null)
        if (data.SegmentNumber == 0 && data.Ticket == 0) { return null; }
        return CommitAddress.FromPersisted(data.SegmentNumber, data.Ticket, branchPath);
    }

    private static CommitAddress? ParsePersistedHead(string? persisted, string sourceDescription) {
        if (string.IsNullOrWhiteSpace(persisted)) { return null; }
        try {
            return CommitAddress.Parse(persisted);
        }
        catch (FormatException ex) {
            throw new InvalidDataException($"Metadata '{sourceDescription}' contains an invalid persisted head '{persisted}': {ex.Message}", ex);
        }
    }

    private static List<string> BuildRecentHeads(
        CommitAddress? newHead,
        CommitAddress? previousHead,
        IReadOnlyList<CommitAddress>? previousRecentHeads
    ) {
        var result = new List<string>(BranchRecentHeadCapacity);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        TryAdd(newHead);
        TryAdd(previousHead);
        if (previousRecentHeads is not null) {
            foreach (var address in previousRecentHeads) {
                if (result.Count >= BranchRecentHeadCapacity) { break; }
                TryAdd(address);
            }
        }

        return result;

        void TryAdd(CommitAddress? address) {
            if (address is not { } value) { return; }
            var text = value.ToString();
            if (!seen.Add(text)) { return; }
            if (result.Count >= BranchRecentHeadCapacity) { return; }
            result.Add(text);
        }
    }

    private static IReadOnlyList<CommitAddress> NormalizeRecentHeads(
        IReadOnlyList<string>? persisted,
        CommitAddress? head,
        string branchPath
    ) {
        var result = new List<CommitAddress>(BranchRecentHeadCapacity);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void TryAdd(CommitAddress? address) {
            if (address is not { } value) { return; }
            var text = value.ToString();
            if (!seen.Add(text)) { return; }
            if (result.Count >= BranchRecentHeadCapacity) { return; }
            result.Add(value);
        }

        TryAdd(head);
        if (persisted is not null) {
            foreach (var item in persisted) {
                var parsed = ParsePersistedHead(item, branchPath);
                TryAdd(parsed);
                if (result.Count >= BranchRecentHeadCapacity) { break; }
            }
        }

        return result;
    }

    private static BranchRefSnapshot? ReadBestBranchRefOrDefault(string repoDir, string branchName) {
        var candidates = new List<BranchRefSnapshot>(capacity: 3);
        var primaryPath = GetBranchFilePath(repoDir, branchName);
        var backupPath = GetBranchBackupFilePath(repoDir, branchName);
        var reflogPath = GetBranchReflogPath(repoDir, branchName);

        TryAddRefCandidate(primaryPath);
        TryAddRefCandidate(backupPath);

        var reflogCandidate = ReadLatestBranchReflogSnapshotOrDefault(reflogPath);
        if (reflogCandidate is not null) { candidates.Add(reflogCandidate.Value); }

        if (candidates.Count == 0) { return null; }

        BranchRefSnapshot best = candidates[0];
        for (int i = 1; i < candidates.Count; i++) {
            var candidate = candidates[i];
            if (candidate.Generation > best.Generation) {
                best = candidate;
                continue;
            }

            if (candidate.Generation == best.Generation
                && string.Equals(candidate.SourcePath, primaryPath, StringComparison.Ordinal)) {
                best = candidate;
            }
        }

        return best;

        void TryAddRefCandidate(string path) {
            if (!File.Exists(path)) { return; }
            try {
                var data = ReadBranchData(path);
                candidates.Add(ToBranchRefSnapshot(data, path));
            }
            catch (InvalidDataException) {
                // Best-effort recovery: ignore invalid ref candidate and continue trying backup/reflog.
            }
            catch (JsonException) {
                // Best-effort recovery: ignore invalid ref candidate and continue trying backup/reflog.
            }
        }
    }

    private static BranchRefSnapshot? ReadLatestBranchReflogSnapshotOrDefault(string reflogPath) {
        if (!File.Exists(reflogPath)) { return null; }

        BranchRefSnapshot? best = null;
        foreach (var line in File.ReadLines(reflogPath)) {
            if (string.IsNullOrWhiteSpace(line)) { continue; }

            try {
                var entry = JsonSerializer.Deserialize(line, RepositoryJsonContext.Default.BranchReflogEntry);
                if (entry is null || entry.Version != BranchReflogEntryVersion || entry.Generation == 0) { continue; }

                var newHead = ParsePersistedHead(entry.NewHead, reflogPath);
                var oldHead = ParsePersistedHead(entry.OldHead, reflogPath);
                var recentHeads = new List<CommitAddress>(capacity: 2);
                if (newHead is { } current) { recentHeads.Add(current); }
                if (oldHead is { } previous && (newHead is null || previous != newHead.Value)) { recentHeads.Add(previous); }

                var candidate = new BranchRefSnapshot(
                    Generation: entry.Generation,
                    Head: newHead,
                    RecentHeads: recentHeads,
                    LastNote: string.IsNullOrWhiteSpace(entry.Note) ? null : entry.Note,
                    SourcePath: reflogPath
                );

                if (best is null || candidate.Generation >= best.Value.Generation) {
                    best = candidate;
                }
            }
            catch (InvalidDataException) {
                // Skip malformed reflog lines; earlier valid generations may still be recoverable.
            }
            catch (JsonException) {
                // Skip malformed reflog lines; earlier valid generations may still be recoverable.
            }
        }

        return best;
    }

    private static void AppendBranchReflog(string repoDir, string branchName, BranchReflogEntry entry) {
        var reflogPath = GetBranchReflogPath(repoDir, branchName);
        Directory.CreateDirectory(Path.GetDirectoryName(reflogPath)!);
        var line = JsonSerializer.Serialize(entry, RepositoryJsonContext.Default.BranchReflogEntry);
        using var stream = new FileStream(reflogPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream);
        writer.WriteLine(line);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static string GetBranchFilePath(string repoDir, string branchName) {
        var branchesDir = GetBranchesDirectoryPath(repoDir);
        var normalizedBranchesDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(branchesDir));
        var containmentPrefix = normalizedBranchesDir + Path.DirectorySeparatorChar;
        var relative = branchName.Replace('/', Path.DirectorySeparatorChar) + ".json";
        var fullPath = Path.GetFullPath(Path.Combine(normalizedBranchesDir, relative));

        // 安全网：即使 ValidateBranchName 遗漏了某种穿越 pattern，也绝不写出 branches 目录。
        if (!fullPath.StartsWith(containmentPrefix, StringComparison.Ordinal)) {
            throw new InvalidOperationException(
                $"Branch name '{branchName}' resolved to a path outside the branches directory. This is a bug — ValidateBranchName should have caught this."
            );
        }

        return fullPath;
    }

    private static string GetBranchBackupFilePath(string repoDir, string branchName) {
        return GetBranchFilePath(repoDir, branchName) + ".last";
    }

    private static string GetBranchReflogPath(string repoDir, string branchName) {
        var branchPath = GetBranchFilePath(repoDir, branchName);
        return branchPath[..^".json".Length] + ".reflog.jsonl";
    }

    private static string GetBranchesDirectoryPath(string repoFullPath) {
        Debug.Assert(Path.IsPathFullyQualified(repoFullPath));
        return Path.Combine(repoFullPath, RefsDirName, BranchesDirName);
    }

    private readonly record struct BranchRefSnapshot(
        ulong Generation,
        CommitAddress? Head,
        IReadOnlyList<CommitAddress> RecentHeads,
        string? LastNote,
        string SourcePath
    );

    internal sealed class BranchData {
        [JsonPropertyName("version")]
        public int Version { get; set; } = CurrentBranchDataVersion;

        [JsonPropertyName("generation")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ulong Generation { get; set; }

        [JsonPropertyName("head")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Head { get; set; }

        [JsonPropertyName("recentHeads")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? RecentHeads { get; set; }

        [JsonPropertyName("lastNote")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LastNote { get; set; }

        [JsonPropertyName("segmentNumber")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public uint SegmentNumber { get; set; }

        [JsonPropertyName("ticket")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ulong Ticket { get; set; }
    }

    internal sealed class BranchReflogEntry {
        [JsonPropertyName("version")]
        public int Version { get; set; } = BranchReflogEntryVersion;

        [JsonPropertyName("generation")]
        public ulong Generation { get; set; }

        [JsonPropertyName("timestampUtc")]
        public required string TimestampUtc { get; set; }

        [JsonPropertyName("operation")]
        public required string Operation { get; set; }

        [JsonPropertyName("oldHead")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? OldHead { get; set; }

        [JsonPropertyName("newHead")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? NewHead { get; set; }

        [JsonPropertyName("note")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Note { get; set; }
    }
}
