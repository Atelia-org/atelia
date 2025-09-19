using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using MemoTree.Core.Types;

namespace MemoTree.Core.Json {
    /// <summary>
    /// NodeId的JSON转换器，确保正确的序列化和反序列化
    /// </summary>
    public class NodeIdJsonConverter : JsonConverter<NodeId> {
        public override NodeId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
            if (reader.TokenType == JsonTokenType.String) {
                var value = reader.GetString();
                if (string.IsNullOrEmpty(value)) { throw new JsonException("NodeId value cannot be null or empty"); }
                return new NodeId(value);
            }

            if (reader.TokenType == JsonTokenType.StartObject) {
                // 处理对象形式的JSON: {"value": "..."}
                string? nodeIdValue = null;

                while (reader.Read()) {
                    if (reader.TokenType == JsonTokenType.EndObject) { break; }
                    if (reader.TokenType == JsonTokenType.PropertyName) {
                        var propertyName = reader.GetString();
                        reader.Read(); // 移动到属性值

                        if (string.Equals(propertyName, "value", StringComparison.OrdinalIgnoreCase)) {
                            nodeIdValue = reader.GetString();
                        }
                    }
                }

                if (string.IsNullOrEmpty(nodeIdValue)) { throw new JsonException("NodeId object must contain a 'value' property"); }
                return new NodeId(nodeIdValue);
            }

            throw new JsonException($"Unexpected token type {reader.TokenType} when parsing NodeId");
        }

        public override void Write(Utf8JsonWriter writer, NodeId value, JsonSerializerOptions options) {
            // 直接将 NodeId 序列化为字符串，而不是对象
            writer.WriteStringValue(value.Value);
        }
    }
}
