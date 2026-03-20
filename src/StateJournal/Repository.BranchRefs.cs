using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atelia.StateJournal;

public sealed partial class Repository {
    private sealed class BranchState {
        public required string BranchName { get; init; }
        public CommitAddress? Head { get; set; }
        public Revision? LoadedRevision { get; set; }
    }

    private static void CompareAndSwapBranchAtomically(
        string repoDir,
        string branchName,
        CommitAddress? expectedHead,
        CommitAddress newHead
    ) {
        var branchPath = GetBranchFilePath(repoDir, branchName);
        var currentHead = ReadBranchAddressOrDefault(branchPath);

        if (currentHead != expectedHead) {
            var expectedSeg = expectedHead?.SegmentNumber ?? 0;
            var expectedTicket = expectedHead?.CommitId.Ticket.Serialize() ?? 0;
            var actualSeg = currentHead?.SegmentNumber ?? 0;
            var actualTicket = currentHead?.CommitId.Ticket.Serialize() ?? 0;
            throw new InvalidOperationException(
                $"branch CAS mismatch: expected seg={expectedSeg}, ticket={expectedTicket}, actual seg={actualSeg}, ticket={actualTicket}."
            );
        }

        WriteBranchAtomically(branchPath, newHead, overwrite: true);
    }

    private static void WriteNewBranchAtomically(string repoDir, string branchName, CommitAddress? head) {
        var branchPath = GetBranchFilePath(repoDir, branchName);
        if (File.Exists(branchPath)) { throw new InvalidOperationException($"Branch file '{branchName}' already exists on disk."); }
        WriteBranchAtomically(branchPath, head, overwrite: false);
    }

    private static void WriteBranchAtomically(string branchPath, CommitAddress? head, bool overwrite) {
        var data = new BranchData {
            Version = 1,
            SegmentNumber = head?.SegmentNumber ?? 0,
            Ticket = head?.CommitId.Ticket.Serialize() ?? 0,
        };

        Directory.CreateDirectory(Path.GetDirectoryName(branchPath)!);
        WriteJsonAtomically(branchPath, data, RepositoryJsonContext.Default.BranchData, overwrite);
    }

    private static CommitAddress? ReadBranchAddress(string branchPath) {
        var data = ReadBranchData(branchPath);
        return ToBranchAddress(data, branchPath);
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

    private static CommitAddress? ToBranchAddress(BranchData data, string branchPath) {
        if (data.Version != 1) { throw new InvalidDataException($"Branch file '{branchPath}' has unsupported version {data.Version}."); }

        // segmentNumber == 0 && ticket == 0 → unborn branch (null)
        if (data.SegmentNumber == 0 && data.Ticket == 0) { return null; }
        return CommitAddress.FromPersisted(data.SegmentNumber, data.Ticket, branchPath);
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

    private static string GetBranchesDirectoryPath(string repoFullPath) {
        Debug.Assert(Path.IsPathFullyQualified(repoFullPath));
        return Path.Combine(repoFullPath, RefsDirName, BranchesDirName);
    }

    internal sealed class BranchData {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("segmentNumber")]
        public uint SegmentNumber { get; set; }

        [JsonPropertyName("ticket")]
        public ulong Ticket { get; set; }
    }
}
