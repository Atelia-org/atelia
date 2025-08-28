using MemoTree.Core.Types;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace MemoTree.Services.Yaml {
    /// <summary>
    /// NodeId的YAML转换器，确保正确的序列化和反序列化
    /// </summary>
    public class NodeIdYamlConverter : IYamlTypeConverter {
        public bool Accepts(Type type) {
            return type == typeof(NodeId) || type == typeof(NodeId?);
        }

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer nestedObjectDeserializer) {
            if (parser.Current is Scalar scalar) {
                parser.MoveNext();

                if (string.IsNullOrEmpty(scalar.Value)) {
                    if (type == typeof(NodeId?)) {
                        return null;
                    }

                    throw new YamlException("NodeId value cannot be null or empty");
                }

                return new NodeId(scalar.Value);
            }

            if (parser.Current is MappingStart) {
                // 手动解析对象形式的NodeId: {value: "...", isRoot: false}
                parser.MoveNext(); // 跳过 MappingStart
                string? nodeIdValue = null;

                while (parser.Current is not MappingEnd) {
                    if (parser.Current is Scalar key) {
                        var keyName = key.Value;
                        parser.MoveNext(); // 移动到值

                        if (keyName == "value" && parser.Current is Scalar value) {
                            nodeIdValue = value.Value;
                        }

                        parser.MoveNext(); // 移动到下一个键或结束
                    } else {
                        parser.MoveNext(); // 跳过其他类型的节点
                    }
                }

                parser.MoveNext(); // 跳过 MappingEnd

                if (!string.IsNullOrEmpty(nodeIdValue)) {
                    return new NodeId(nodeIdValue);
                }

                throw new YamlException("NodeId object must contain a 'value' property");
            }

            throw new YamlException($"Expected scalar or mapping for NodeId, got {parser.Current?.GetType().Name}");
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer nestedObjectSerializer) {
            if (value is NodeId nodeId) {
                emitter.Emit(new Scalar(nodeId.Value));
            } else if (value == null && type == typeof(NodeId?)) {
                emitter.Emit(new Scalar(""));
            } else {
                throw new YamlException($"Expected NodeId, got {value?.GetType().Name}");
            }
        }
    }
}
