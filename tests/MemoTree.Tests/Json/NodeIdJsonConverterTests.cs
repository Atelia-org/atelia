using System.Text.Json;
using MemoTree.Core.Json;
using MemoTree.Core.Types;
using Xunit;

namespace MemoTree.Tests.Json {
    public class NodeIdJsonConverterTests {
        private readonly JsonSerializerOptions _jsonOptions;

        public NodeIdJsonConverterTests() {
            _jsonOptions = new JsonSerializerOptions {
                Converters = { new NodeIdJsonConverter() },
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
        }

        [Fact]
        public void Serialize_NodeId_ShouldProduceStringValue() {
            // Arrange
            var nodeId = NodeId.Generate();

            // Act
            var json = JsonSerializer.Serialize(nodeId, _jsonOptions);

            // Assert
            Assert.Equal($"\"{nodeId.Value}\"", json);
        }

        [Fact]
        public void Deserialize_StringValue_ShouldProduceNodeId() {
            // Arrange
            var originalNodeId = NodeId.Generate();
            var json = $"\"{originalNodeId.Value}\"";

            // Act
            var deserializedNodeId = JsonSerializer.Deserialize<NodeId>(json, _jsonOptions);

            // Assert
            Assert.Equal(originalNodeId, deserializedNodeId);
        }

        [Fact]
        public void Deserialize_ObjectWithValueProperty_ShouldProduceNodeId() {
            // Arrange
            var nodeId = NodeId.Generate();
            var json = $"{{\"value\":\"{nodeId.Value}\"}}";

            // Act
            var deserializedNodeId = JsonSerializer.Deserialize<NodeId>(json, _jsonOptions);

            // Assert
            Assert.Equal(nodeId, deserializedNodeId);
        }

        [Fact]
        public void SerializeDeserialize_HierarchyInfo_ShouldPreserveNodeIds() {
            // Arrange
            var parentId = NodeId.Generate();
            var childId = NodeId.Generate();
            var parentInfo = HierarchyInfo.Create(parentId).AddChild(childId);

            // Act
            var json = JsonSerializer.Serialize(parentInfo, _jsonOptions);
            var deserialized = JsonSerializer.Deserialize<HierarchyInfo>(json, _jsonOptions);

            // Assert
            Assert.NotNull(deserialized);
            Assert.Equal(parentId, deserialized.ParentId);
            Assert.Single(deserialized.Children);
            Assert.Equal(childId, deserialized.Children.First().NodeId);
        }

        [Fact]
        public void Deserialize_EmptyString_ShouldThrowJsonException() {
            // Arrange
            var json = "\"\"";

            // Act & Assert
            Assert.Throws<JsonException>(
                () =>
                JsonSerializer.Deserialize<NodeId>(json, _jsonOptions)
            );
        }

        [Fact]
        public void Deserialize_ObjectWithoutValue_ShouldThrowJsonException() {
            // Arrange
            var json = "{\"notValue\":\"test\"}";

            // Act & Assert
            Assert.Throws<JsonException>(
                () =>
                JsonSerializer.Deserialize<NodeId>(json, _jsonOptions)
            );
        }
    }
}
