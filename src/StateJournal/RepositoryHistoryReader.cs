using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atelia.StateJournal;

/// <summary>离线/救援用的 branch history address 来源。</summary>
public enum BranchHistoryAddressSource {
    ReflogOldHead,
    ReflogNewHead,
    BranchHead,
    BranchRecentHead,
    BranchBackupHead,
    BranchBackupRecentHead,
}

/// <summary>从 branch metadata 中发现的历史 commit 地址。</summary>
public readonly record struct BranchHistoryAddress(
    CommitAddress Address,
    BranchHistoryAddressSource Source,
    ulong? Generation,
    int? LineNumber
);

/// <summary>离线扫描 branch metadata 的结果。Warnings 表示已跳过的损坏或不支持片段。</summary>
public sealed record BranchHistoryScanResult(
    IReadOnlyList<BranchHistoryAddress> Addresses,
    IReadOnlyList<string> Warnings
);

/// <summary>
/// 离线/救援用的 StateJournal branch history reader。
/// 它只读取 branch ref / backup / reflog 元数据并枚举 <see cref="CommitAddress"/>，不打开 Repository、不反序列化对象图。
/// </summary>
public static class RepositoryHistoryReader {
    private const int CurrentBranchDataVersion = 2;
    private const int BranchReflogEntryVersion = 1;

    public static BranchHistoryScanResult EnumerateBranchCommitAddresses(string repoDir, string branchName) {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);

        var nameError = Repository.ValidateBranchName(branchName);
        if (nameError is not null) { throw new ArgumentException($"Invalid branch name '{branchName}': {nameError}", nameof(branchName)); }

        var fullRepoDir = Path.GetFullPath(repoDir);
        var addresses = new List<BranchHistoryAddress>();
        var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var branchPath = GetBranchFilePath(fullRepoDir, branchName);
        var backupPath = branchPath + ".last";
        var reflogPath = branchPath[..^".json".Length] + ".reflog.jsonl";

        AddReflogAddresses(reflogPath, addresses, warnings, seen);
        AddBranchRefAddresses(branchPath, BranchHistoryAddressSource.BranchHead, BranchHistoryAddressSource.BranchRecentHead, addresses, warnings, seen);
        AddBranchRefAddresses(backupPath, BranchHistoryAddressSource.BranchBackupHead, BranchHistoryAddressSource.BranchBackupRecentHead, addresses, warnings, seen);

        return new BranchHistoryScanResult(addresses, warnings);
    }

    public static IReadOnlyList<CommitAddress> EnumerateBranchCommitAddressValues(string repoDir, string branchName) {
        var result = EnumerateBranchCommitAddresses(repoDir, branchName);
        var addresses = new CommitAddress[result.Addresses.Count];
        for (int i = 0; i < addresses.Length; i++) { addresses[i] = result.Addresses[i].Address; }
        return addresses;
    }

    private static void AddReflogAddresses(
        string reflogPath,
        List<BranchHistoryAddress> addresses,
        List<string> warnings,
        HashSet<string> seen
    ) {
        if (!File.Exists(reflogPath)) { return; }

        var lineNumber = 0;
        try {
            foreach (var line in File.ReadLines(reflogPath)) {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line)) { continue; }

                BranchReflogEntry? entry;
                try {
                    entry = JsonSerializer.Deserialize<BranchReflogEntry>(line);
                }
                catch (JsonException ex) {
                    warnings.Add($"Skipped malformed reflog line {lineNumber} in '{reflogPath}': {ex.Message}");
                    continue;
                }

                if (entry is null) {
                    warnings.Add($"Skipped empty reflog line {lineNumber} in '{reflogPath}'.");
                    continue;
                }

                if (entry.Version != BranchReflogEntryVersion || entry.Generation == 0) {
                    warnings.Add($"Skipped unsupported reflog line {lineNumber} in '{reflogPath}': version={entry.Version}, generation={entry.Generation}.");
                    continue;
                }

                TryAddParsed(entry.OldHead, BranchHistoryAddressSource.ReflogOldHead, entry.Generation, lineNumber);
                TryAddParsed(entry.NewHead, BranchHistoryAddressSource.ReflogNewHead, entry.Generation, lineNumber);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            warnings.Add($"Stopped reading reflog '{reflogPath}' after line {lineNumber}: {ex.Message}");
        }

        void TryAddParsed(string? text, BranchHistoryAddressSource source, ulong generation, int sourceLineNumber) {
            if (string.IsNullOrWhiteSpace(text)) { return; }
            var address = CommitAddress.TryParse(text);
            if (address is null) {
                warnings.Add($"Skipped invalid {source} address on reflog line {sourceLineNumber} in '{reflogPath}': '{text}'.");
                return;
            }

            AddUnique(address.Value, source, generation, sourceLineNumber, addresses, seen);
        }
    }

    private static void AddBranchRefAddresses(
        string branchPath,
        BranchHistoryAddressSource headSource,
        BranchHistoryAddressSource recentHeadSource,
        List<BranchHistoryAddress> addresses,
        List<string> warnings,
        HashSet<string> seen
    ) {
        if (!File.Exists(branchPath)) { return; }

        BranchData? data;
        try {
            data = JsonSerializer.Deserialize<BranchData>(File.ReadAllText(branchPath));
        }
        catch (JsonException ex) {
            warnings.Add($"Skipped malformed branch ref '{branchPath}': {ex.Message}");
            return;
        }
        catch (IOException ex) {
            warnings.Add($"Skipped unreadable branch ref '{branchPath}': {ex.Message}");
            return;
        }
        catch (UnauthorizedAccessException ex) {
            warnings.Add($"Skipped unreadable branch ref '{branchPath}': {ex.Message}");
            return;
        }

        if (data is null) {
            warnings.Add($"Skipped empty branch ref '{branchPath}'.");
            return;
        }

        switch (data.Version) {
            case 1:
                AddLegacyBranchHead(data, branchPath, headSource, addresses, warnings, seen);
                return;
            case CurrentBranchDataVersion:
                AddV2BranchRef(data, branchPath, headSource, recentHeadSource, addresses, warnings, seen);
                return;
            default:
                warnings.Add($"Skipped unsupported branch ref '{branchPath}': version={data.Version}.");
                return;
        }
    }

    private static void AddLegacyBranchHead(
        BranchData data,
        string branchPath,
        BranchHistoryAddressSource source,
        List<BranchHistoryAddress> addresses,
        List<string> warnings,
        HashSet<string> seen
    ) {
        if (data.SegmentNumber == 0 && data.Ticket == 0) { return; }

        try {
            var address = CommitAddress.FromPersisted(data.SegmentNumber, data.Ticket, branchPath);
            AddUnique(address, source, generation: null, lineNumber: null, addresses, seen);
        }
        catch (InvalidDataException ex) {
            warnings.Add($"Skipped invalid legacy branch ref '{branchPath}': {ex.Message}");
        }
    }

    private static void AddV2BranchRef(
        BranchData data,
        string branchPath,
        BranchHistoryAddressSource headSource,
        BranchHistoryAddressSource recentHeadSource,
        List<BranchHistoryAddress> addresses,
        List<string> warnings,
        HashSet<string> seen
    ) {
        if (data.Generation == 0) {
            warnings.Add($"Skipped invalid branch ref '{branchPath}': generation=0.");
            return;
        }

        TryAddPersisted(data.Head, headSource);
        if (data.RecentHeads is not null) {
            foreach (var persisted in data.RecentHeads) { TryAddPersisted(persisted, recentHeadSource); }
        }

        void TryAddPersisted(string? text, BranchHistoryAddressSource source) {
            if (string.IsNullOrWhiteSpace(text)) { return; }
            var address = CommitAddress.TryParse(text);
            if (address is null) {
                warnings.Add($"Skipped invalid {source} address in branch ref '{branchPath}': '{text}'.");
                return;
            }

            AddUnique(address.Value, source, data.Generation, lineNumber: null, addresses, seen);
        }
    }

    private static void AddUnique(
        CommitAddress address,
        BranchHistoryAddressSource source,
        ulong? generation,
        int? lineNumber,
        List<BranchHistoryAddress> addresses,
        HashSet<string> seen
    ) {
        if (!seen.Add(address.ToString())) { return; }
        addresses.Add(new BranchHistoryAddress(address, source, generation, lineNumber));
    }

    private static string GetBranchFilePath(string repoDir, string branchName) {
        var branchesDir = Path.Combine(repoDir, "refs", "branches");
        var normalizedBranchesDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(branchesDir));
        var containmentPrefix = normalizedBranchesDir + Path.DirectorySeparatorChar;
        var relative = branchName.Replace('/', Path.DirectorySeparatorChar) + ".json";
        var fullPath = Path.GetFullPath(Path.Combine(normalizedBranchesDir, relative));

        if (!fullPath.StartsWith(containmentPrefix, StringComparison.Ordinal)) { throw new InvalidOperationException($"Branch name '{branchName}' resolved to a path outside the branches directory."); }

        return fullPath;
    }

    private sealed class BranchData {
        [JsonPropertyName("version")]
        public int Version { get; set; } = CurrentBranchDataVersion;

        [JsonPropertyName("generation")]
        public ulong Generation { get; set; }

        [JsonPropertyName("head")]
        public string? Head { get; set; }

        [JsonPropertyName("recentHeads")]
        public List<string>? RecentHeads { get; set; }

        [JsonPropertyName("segmentNumber")]
        public uint SegmentNumber { get; set; }

        [JsonPropertyName("ticket")]
        public ulong Ticket { get; set; }
    }

    private sealed class BranchReflogEntry {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("generation")]
        public ulong Generation { get; set; }

        [JsonPropertyName("oldHead")]
        public string? OldHead { get; set; }

        [JsonPropertyName("newHead")]
        public string? NewHead { get; set; }
    }
}
