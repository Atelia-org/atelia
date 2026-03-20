using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Atelia.StateJournal;

public sealed partial class Repository {
    private static bool DirectoryIsEmpty(string directoryPath) {
        using var entries = Directory.EnumerateFileSystemEntries(directoryPath).GetEnumerator();
        return !entries.MoveNext();
    }

    private static bool DirectoryContainsOnlyLockFile(string directoryPath) {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directoryPath)) {
            if (string.Equals(Path.GetFileName(entry), LockFileName, StringComparison.Ordinal)) { continue; }
            return false;
        }

        return true;
    }

    private static void TryDeleteLockFile(string repoDir) {
        try {
            File.Delete(Path.Combine(repoDir, LockFileName));
        }
        catch {
            // best-effort cleanup
        }
    }

    private static void WriteJsonAtomically<T>(string filePath, T data, JsonTypeInfo<T> typeInfo, bool overwrite = true) {
        var json = JsonSerializer.Serialize(data, typeInfo);
        var tmpPath = filePath + ".tmp";
        File.WriteAllText(tmpPath, json);
        File.Move(tmpPath, filePath, overwrite: overwrite);
    }

    private static Dictionary<string, BranchState> BuildBranchStates(IEnumerable<LoadedBranch> loadedBranches) {
        var branches = new Dictionary<string, BranchState>(StringComparer.Ordinal);
        foreach (var branch in loadedBranches) {
            branches.Add(branch.BranchName, new BranchState {
                BranchName = branch.BranchName,
                Head = branch.Head,
                LoadedRevision = null,
            });
        }
        return branches;
    }

    [JsonSerializable(typeof(BranchData))]
    internal sealed partial class RepositoryJsonContext : JsonSerializerContext { }
}
