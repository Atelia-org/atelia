using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.LiveContextProto.Tools;
using Atelia.LiveContextProto.Context;

namespace Atelia.LiveContextProto.Provider;

internal static class ProviderToolSchemaBuilder {
    public static JsonElement BuildSchema(ToolDefinition definition) {
        if (definition is null) { throw new ArgumentNullException(nameof(definition)); }

        var root = new JsonObject {
            ["type"] = "object",
            ["additionalProperties"] = false
        };

        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var parameter in definition.Parameters) {
            var schema = BuildParameterSchema(parameter);
            properties[parameter.Name] = schema;

            if (parameter.IsRequired) {
                required.Add(parameter.Name);
            }
        }

        if (properties.Count > 0) {
            root["properties"] = properties;
        }

        if (required.Count > 0) {
            root["required"] = required;
        }

        return JsonSerializer.SerializeToElement(root);
    }

    private static JsonNode BuildParameterSchema(ToolParameter parameter) {
        return parameter.Cardinality switch {
            ToolParameterCardinality.List => BuildListSchema(parameter),
            ToolParameterCardinality.Map => BuildMapSchema(parameter),
            _ => BuildScalarSchema(parameter, includeMetadata: true)
        };
    }

    private static JsonNode BuildListSchema(ToolParameter parameter) {
        var schema = new JsonObject {
            ["type"] = "array"
        };

        AppendDescription(schema, parameter.Description);
        AppendExamples(schema, parameter.Example);

        var itemSchema = BuildScalarSchema(parameter, includeMetadata: false);
        schema["items"] = itemSchema;

        return schema;
    }

    private static JsonNode BuildMapSchema(ToolParameter parameter) {
        var schema = new JsonObject {
            ["type"] = "object"
        };

        AppendDescription(schema, parameter.Description);
        AppendExamples(schema, parameter.Example);

        var valueSchema = BuildScalarSchema(parameter, includeMetadata: false);
        schema["additionalProperties"] = valueSchema;

        return schema;
    }

    private static JsonNode BuildScalarSchema(ToolParameter parameter, bool includeMetadata) {
        var schema = new JsonObject();

        switch (parameter.ValueKind) {
            case ToolParameterValueKind.String:
            case ToolParameterValueKind.EnumToken:
            case ToolParameterValueKind.AttachmentReference:
                schema["type"] = "string";
                break;
            case ToolParameterValueKind.Boolean:
                schema["type"] = "boolean";
                break;
            case ToolParameterValueKind.Integer:
                schema["type"] = "integer";
                break;
            case ToolParameterValueKind.Number:
                schema["type"] = "number";
                break;
            case ToolParameterValueKind.JsonObject:
                schema["type"] = "object";
                break;
            case ToolParameterValueKind.JsonArray:
                schema["type"] = "array";
                break;
            case ToolParameterValueKind.Timestamp:
                schema["type"] = "string";
                schema["format"] = "date-time";
                break;
            case ToolParameterValueKind.Uri:
                schema["type"] = "string";
                schema["format"] = "uri";
                break;
            default:
                schema["type"] = "string";
                break;
        }

        if (includeMetadata) {
            AppendDescription(schema, parameter.Description);
            AppendExamples(schema, parameter.Example);
        }

        AppendEnumConstraint(schema, parameter.EnumConstraint);
        schema["x-ateliatype"] = parameter.ValueKind.ToString();

        return schema;
    }

    private static void AppendEnumConstraint(JsonObject schema, ToolParameterEnumConstraint? constraint) {
        if (constraint is null) { return; }

        var values = new JsonArray();
        foreach (var value in constraint.AllowedValues) {
            values.Add(JsonValue.Create(value));
        }

        schema["enum"] = values;
        if (!constraint.CaseSensitive) {
            schema["x-enum-case-sensitive"] = false;
        }
    }

    private static void AppendDescription(JsonObject schema, string? description) {
        if (string.IsNullOrWhiteSpace(description)) { return; }
        schema["description"] = description;
    }

    private static void AppendExamples(JsonObject schema, string? example) {
        if (string.IsNullOrWhiteSpace(example)) { return; }
        schema["examples"] = new JsonArray { JsonValue.Create(example) };
    }
}
