using MemoTree.Core.Storage.Versioned;
using MemoTree.Core.Types;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Xunit;

namespace MemoTree.Tests.Storage.Versioned {
    public class VersionedStorageTests : IDisposable {
        private readonly string _testWorkspace;
        private readonly ILogger<VersionedStorageImpl<NodeId, HierarchyInfo>> _logger;

        public VersionedStorageTests() {
            _testWorkspace = Path.Combine(Path.GetTempPath(), "MemoTreeTests", Guid.NewGuid().ToString());
            Directory.CreateDirectory(_testWorkspace);

            using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            _logger = loggerFactory.CreateLogger<VersionedStorageImpl<NodeId, HierarchyInfo>>();
        }

        [Fact]
        public async Task CreateHierarchyStorage_ShouldInitializeSuccessfully() {
            // Arrange & Act
            var storage = await VersionedStorageFactory.CreateHierarchyStorageAsync(_testWorkspace, _logger);

            // Assert
            Assert.NotNull(storage);
            var version = await storage.GetCurrentVersionAsync();
            Assert.Equal(1, version);
        }

        [Fact]
        public async Task UpdateManyAsync_ShouldCreateNewVersion() {
            // Arrange
            var storage = await VersionedStorageFactory.CreateHierarchyStorageAsync(_testWorkspace, _logger);
            var parentId = NodeId.Generate();
            var childId = NodeId.Generate();

            var parentInfo = HierarchyInfo.Create(parentId)
                .AddChild(childId);

            var updates = new Dictionary<NodeId, HierarchyInfo> {
                [parentId] = parentInfo
            };

            // Act
            var newVersion = await storage.UpdateManyAsync(updates, "Test update");

            // Assert
            Assert.Equal(2, newVersion);

            var retrievedInfo = await storage.GetAsync(parentId);
            Assert.NotNull(retrievedInfo);
            Assert.Equal(parentId, retrievedInfo.ParentId);
            Assert.Single(retrievedInfo.Children);
            Assert.Equal(childId, retrievedInfo.Children.First().NodeId);
        }

        [Fact]
        public async Task GetAllKeysAsync_ShouldReturnAllParentIds() {
            // Arrange
            var storage = await VersionedStorageFactory.CreateHierarchyStorageAsync(_testWorkspace, _logger);

            var parent1 = NodeId.Generate();
            var parent2 = NodeId.Generate();
            var child1 = NodeId.Generate();
            var child2 = NodeId.Generate();

            var updates = new Dictionary<NodeId, HierarchyInfo> {
                [parent1] = HierarchyInfo.Create(parent1).AddChild(child1),
                [parent2] = HierarchyInfo.Create(parent2).AddChild(child2)
            };

            // Act
            await storage.UpdateManyAsync(updates, "Test batch update");
            var allKeys = await storage.GetAllKeysAsync();

            // Assert
            Assert.Equal(2, allKeys.Count);
            Assert.Contains(parent1, allKeys);
            Assert.Contains(parent2, allKeys);
        }

        [Fact]
        public async Task DeleteAsync_ShouldRemoveFromMemory() {
            // Arrange
            var storage = await VersionedStorageFactory.CreateHierarchyStorageAsync(_testWorkspace, _logger);
            var parentId = NodeId.Generate();
            var childId = NodeId.Generate();

            var parentInfo = HierarchyInfo.Create(parentId).AddChild(childId);
            await storage.UpdateManyAsync(new Dictionary<NodeId, HierarchyInfo> { [parentId] = parentInfo });

            // Act
            var deleteVersion = await storage.DeleteAsync(parentId, "Test delete");

            // Assert
            Assert.Equal(3, deleteVersion); // Version should increment

            var retrievedInfo = await storage.GetAsync(parentId);
            Assert.Null(retrievedInfo);

            var allKeys = await storage.GetAllKeysAsync();
            Assert.Empty(allKeys);
        }

        [Fact]
        public async Task Storage_ShouldPersistAcrossInstances() {
            // Arrange
            var parentId = NodeId.Generate();
            var childId = NodeId.Generate();
            var parentInfo = HierarchyInfo.Create(parentId).AddChild(childId);

            // Act - Create first instance and save data
            {
                var storage1 = await VersionedStorageFactory.CreateHierarchyStorageAsync(_testWorkspace, _logger);
                await storage1.UpdateManyAsync(new Dictionary<NodeId, HierarchyInfo> { [parentId] = parentInfo });
            }

            // Act - Create second instance and verify data persists
            {
                var storage2 = await VersionedStorageFactory.CreateHierarchyStorageAsync(_testWorkspace, _logger);
                var retrievedInfo = await storage2.GetAsync(parentId);

                // Assert
                Assert.NotNull(retrievedInfo);
                Assert.Equal(parentId, retrievedInfo.ParentId);
                Assert.Single(retrievedInfo.Children);
                Assert.Equal(childId, retrievedInfo.Children.First().NodeId);
            }
        }

        [Fact]
        public void NodeIdKeySerializer_ShouldSerializeAndDeserialize() {
            // Arrange
            var serializer = new NodeIdKeySerializer();
            var originalNodeId = NodeId.Generate();

            // Act
            var serialized = serializer.Serialize(originalNodeId);
            var deserialized = serializer.Deserialize(serialized);

            // Assert
            Assert.Equal(originalNodeId, deserialized);
        }

        [Fact]
        public void VersionedStoragePathProvider_ShouldParseFileName() {
            // Arrange
            var options = new VersionedStorageOptions {
                StorageRoot = _testWorkspace,
                DataDirectory = "data",
                VersionFile = "version.json",
                JournalsDirectory = "journals",
                KeepVersionCount = 10,
                FileExtension = ".json",
                EnableConcurrency = false
            };
            var keySerializer = new NodeIdKeySerializer();
            var versionFormatter = new DecimalVersionFormatter();
            var pathProvider = new VersionedStoragePathProvider<NodeId>(options, keySerializer, versionFormatter);

            var nodeId = NodeId.Generate();
            var version = 42L;
            var fileName = $"{nodeId.Value}.{version}";

            // Act
            var result = pathProvider.TryParseFileName(fileName);

            // Assert
            Assert.True(result.HasValue);
            Assert.Equal(nodeId, result.Value.key);
            Assert.Equal(version, result.Value.version);
        }

        public void Dispose() {
            if (Directory.Exists(_testWorkspace)) {
                Directory.Delete(_testWorkspace, true);
            }
        }
    }
}
