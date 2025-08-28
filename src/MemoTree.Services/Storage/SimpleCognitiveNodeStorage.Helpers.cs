using MemoTree.Core.Types;

namespace MemoTree.Services.Storage;

// Partial: helper and IO utilities from SimpleCognitiveNodeStorage
public partial class SimpleCognitiveNodeStorage {
    #region Helper Methods

    private string GetNodeDirectory(NodeId nodeId) {
        return _pathService.GetNodeDirectory(nodeId);
    }

    private string GetNodeMetadataPath(NodeId nodeId) {
        return _pathService.GetNodeMetadataPath(nodeId);
    }

    private string GetNodeContentPath(NodeId nodeId, LodLevel level) {
        return _pathService.GetNodeContentPath(nodeId, level);
    }

    #endregion

    #region Atomic IO helpers
    private static async Task WriteTextAtomicAsync(string targetPath, string content, CancellationToken ct) {
        var tempPath = targetPath + ".tmp";
        var encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        await File.WriteAllTextAsync(tempPath, content, encoding, ct);

        // Ensure target directory exists
        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir)) {
            Directory.CreateDirectory(dir);
        }

        // Replace or move atomically where possible
        if (File.Exists(targetPath)) {
            // On Windows, File.Replace offers atomic semantics
            var backup = targetPath + ".bak";
            try {
                File.Replace(tempPath, targetPath, backup, ignoreMetadataErrors: true);
                // Best-effort cleanup
                if (File.Exists(backup)) {
                    File.Delete(backup);
                }
            } catch {
                // Fallback: move over
                if (File.Exists(targetPath)) {
                    File.Delete(targetPath);
                }

                File.Move(tempPath, targetPath);
            }
        } else {
            File.Move(tempPath, targetPath);
        }
    }

    private static string ComputeSha256Base64(string content) {
        if (string.IsNullOrEmpty(content)) {
            return string.Empty;
        }

        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToBase64String(hash);
    }
    #endregion
}
