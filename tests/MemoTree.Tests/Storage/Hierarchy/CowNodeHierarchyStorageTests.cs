using System;
using System.Threading.Tasks;
using MemoTree.Core.Storage.Hierarchy;
using MemoTree.Core.Storage.Versioned;
using MemoTree.Core.Types;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Xunit;

namespace MemoTree.Tests.Storage.Hierarchy {
    public class CowNodeHierarchyStorageTests : IDisposable {
        private readonly string _testWorkspace;
        private readonly ILogger<VersionedStorageImpl<NodeId, HierarchyInfo>> _versionedLogger;
        private readonly ILogger<CowNodeHierarchyStorage> _hierarchyLogger;
        private readonly ILoggerFactory _loggerFactory;

        public CowNodeHierarchyStorageTests() {
            _testWorkspace = Path.Combine(Path.GetTempPath(), "MemoTreeTests", Guid.NewGuid().ToString());
            Directory.CreateDirectory(_testWorkspace);
            _loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            _versionedLogger = _loggerFactory.CreateLogger<VersionedStorageImpl<NodeId, HierarchyInfo>>();
            _hierarchyLogger = _loggerFactory.CreateLogger<CowNodeHierarchyStorage>();
        }
        [Fact]
        public async Task AddChild_ShouldThrow_WhenCreatesCycle() {
            var versioned = await VersionedStorageFactory.CreateHierarchyStorageAsync(_testWorkspace, _versionedLogger);
            var storage = new CowNodeHierarchyStorage(versioned, _hierarchyLogger);

            var a = NodeId.Generate();
            var b = NodeId.Generate();

            await storage.AddChildAsync(a, b);

            // attempt to create cycle: add A under B
            await Assert.ThrowsAsync<InvalidOperationException>(
                async () => {
                    await storage.AddChildAsync(b, a);
                }
            );
        }

        [Fact]
        public async Task MoveNode_ShouldThrow_WhenCreatesCycle() {
            var versioned = await VersionedStorageFactory.CreateHierarchyStorageAsync(_testWorkspace, _versionedLogger);
            var storage = new CowNodeHierarchyStorage(versioned, _hierarchyLogger);

            var a = NodeId.Generate();
            var b = NodeId.Generate();
            var c = NodeId.Generate();

            await storage.AddChildAsync(a, b);
            await storage.AddChildAsync(b, c);

            // move A under C would form a cycle A->B->C->A
            await Assert.ThrowsAsync<InvalidOperationException>(
                async () => {
                    await storage.MoveNodeAsync(a, c);
                }
            );
        }

        public void Dispose() {
            if (Directory.Exists(_testWorkspace)) {
                Directory.Delete(_testWorkspace, true);
            }
            _loggerFactory.Dispose();
        }
    }
}
