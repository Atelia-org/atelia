using System.Text.Json;
using MemoTree.Core.Exceptions;
using MemoTree.Core.Services;
using MemoTree.Core.Storage.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using MemoTree.Services.Storage;
using Xunit;

namespace MemoTree.Tests.Storage;

public class ViewStateStorageErrorTests {
    private sealed class TempWorkspacePathService : IWorkspacePathService {
        private readonly string _root;
        public TempWorkspacePathService(string root) => _root = root;
        public string GetWorkspaceRoot() => Path.Combine(_root, ".memotree");
        public string GetViewsDirectory() => Path.Combine(GetWorkspaceRoot(), "views");
        public string GetCogNodesDirectory() => Path.Combine(GetWorkspaceRoot(), "CogNodes");
        public string GetHierarchyDirectory() => Path.Combine(GetWorkspaceRoot(), "hierarchy");
        public string GetRelationsDirectory() => Path.Combine(GetWorkspaceRoot(), "relations");
        public string GetNodeDirectory(MemoTree.Core.Types.NodeId nodeId) => Path.Combine(GetCogNodesDirectory(), nodeId.Value);
        public string GetNodeMetadataPath(MemoTree.Core.Types.NodeId nodeId) => Path.Combine(GetNodeDirectory(nodeId), "metadata.yml");
        public string GetNodeContentPath(MemoTree.Core.Types.NodeId nodeId, MemoTree.Core.Types.LodLevel level)
            => Path.Combine(GetNodeDirectory(nodeId),
                level switch {
                    MemoTree.Core.Types.LodLevel.Gist => "gist.md",
                    MemoTree.Core.Types.LodLevel.Summary => "summary.md",
                    MemoTree.Core.Types.LodLevel.Full => "full.md",
                    _ => "content.md"
                }
            );
        public bool IsWorkspace(string? directory = null) => Directory.Exists(GetWorkspaceRoot());
        public bool IsLinkedWorkspace() => false;
        public string? GetLinkTarget() => null;
        public void EnsureDirectoriesExist() => Directory.CreateDirectory(GetWorkspaceRoot());
    }

    [Fact]
    public async Task GetViewStateAsync_ShouldThrowStorageException_OnMalformedJson() {
        var tmp = Path.Combine(Path.GetTempPath(), "mt-vs-err-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try {
            var paths = new TempWorkspacePathService(tmp);
            var logger = NullLogger<FileViewStateStorage>.Instance;
            var storage = new FileViewStateStorage(paths, logger);

            // prepare malformed json
            var dir = paths.GetViewsDirectory();
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "bad.json");
            await File.WriteAllTextAsync(file, "{ not-valid-json ");

            var ex = await Assert.ThrowsAsync<StorageException>(() => storage.GetViewStateAsync("bad"));
            Assert.Equal("bad", ex.Context["ViewName"] as string);
            Assert.EndsWith(Path.Combine("views", "bad.json"), ex.Context["Path"] as string);
        } finally {
            try {
                Directory.Delete(tmp, true);
            } catch { }
        }
    }
}
