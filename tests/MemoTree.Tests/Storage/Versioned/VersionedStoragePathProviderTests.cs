using Xunit;
using MemoTree.Core.Storage.Versioned;
using MemoTree.Core.Types;
using System.IO;

namespace MemoTree.Tests.Storage.Versioned {
    public class VersionedStoragePathProviderTests {
        [Fact]
        public void PathProvider_WithHexFormatter_ShouldGenerateHexVersionInFilePath() {
            // Arrange
            var options = new VersionedStorageOptions {
                StorageRoot = Path.GetTempPath(),
                DataDirectory = "data",
                VersionFile = "version.json",
                JournalsDirectory = "journals",
                KeepVersionCount = 10,
                FileExtension = ".json",
                EnableConcurrency = false
            };
            var keySerializer = new NodeIdKeySerializer();
            var hexFormatter = new HexVersionFormatter();
            var pathProvider = new VersionedStoragePathProvider<NodeId>(options, keySerializer, hexFormatter);

            var nodeId = NodeId.Generate();
            var version = 255L; // 这将变成FF

            // Act
            var filePath = pathProvider.GetDataFilePath(nodeId, version);

            // Assert
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            Assert.Contains("FF", fileName); // 应该包含十六进制版本
            Assert.DoesNotContain("255", fileName); // 不应该包含十进制版本
        }

        [Fact]
        public void PathProvider_WithHexFormatter_ShouldParseHexVersionFromFileName() {
            // Arrange
            var options = new VersionedStorageOptions {
                StorageRoot = Path.GetTempPath(),
                DataDirectory = "data",
                VersionFile = "version.json",
                JournalsDirectory = "journals",
                KeepVersionCount = 10,
                FileExtension = ".json",
                EnableConcurrency = false
            };
            var keySerializer = new NodeIdKeySerializer();
            var hexFormatter = new HexVersionFormatter();
            var pathProvider = new VersionedStoragePathProvider<NodeId>(options, keySerializer, hexFormatter);

            var nodeId = NodeId.Generate();
            var fileName = $"{nodeId.Value}.FF"; // 十六进制的255

            // Act
            var result = pathProvider.TryParseFileName(fileName);

            // Assert
            Assert.True(result.HasValue);
            Assert.Equal(nodeId, result.Value.key);
            Assert.Equal(255L, result.Value.version);
        }

        [Fact]
        public void PathProvider_WithDecimalFormatter_ShouldParseDecimalVersionFromFileName() {
            // Arrange
            var options = new VersionedStorageOptions {
                StorageRoot = Path.GetTempPath(),
                DataDirectory = "data",
                VersionFile = "version.json",
                JournalsDirectory = "journals",
                KeepVersionCount = 10,
                FileExtension = ".json",
                EnableConcurrency = false
            };
            var keySerializer = new NodeIdKeySerializer();
            var decimalFormatter = new DecimalVersionFormatter();
            var pathProvider = new VersionedStoragePathProvider<NodeId>(options, keySerializer, decimalFormatter);

            var nodeId = NodeId.Generate();
            var fileName = $"{nodeId.Value}.255"; // 十进制的255

            // Act
            var result = pathProvider.TryParseFileName(fileName);

            // Assert
            Assert.True(result.HasValue);
            Assert.Equal(nodeId, result.Value.key);
            Assert.Equal(255L, result.Value.version);
        }

        [Fact]
        public void HexFormatter_ProducesShortFileNamesForLargeVersions() {
            // Arrange
            var options = new VersionedStorageOptions {
                StorageRoot = Path.GetTempPath(),
                DataDirectory = "data",
                VersionFile = "version.json",
                JournalsDirectory = "journals",
                KeepVersionCount = 10,
                FileExtension = ".json",
                EnableConcurrency = false
            };
            var keySerializer = new NodeIdKeySerializer();
            var hexFormatter = new HexVersionFormatter();
            var decimalFormatter = new DecimalVersionFormatter();
            var hexPathProvider = new VersionedStoragePathProvider<NodeId>(options, keySerializer, hexFormatter);
            var decimalPathProvider = new VersionedStoragePathProvider<NodeId>(options, keySerializer, decimalFormatter);

            var nodeId = NodeId.Generate();
            var largeVersion = 1000000000L; // 10亿

            // Act
            var hexFilePath = hexPathProvider.GetDataFilePath(nodeId, largeVersion);
            var decimalFilePath = decimalPathProvider.GetDataFilePath(nodeId, largeVersion);

            // Assert
            var hexFileName = Path.GetFileName(hexFilePath);
            var decimalFileName = Path.GetFileName(decimalFilePath);

            // Hex文件名应该更短
            Assert.True(hexFileName.Length < decimalFileName.Length);
        }
    }
}
