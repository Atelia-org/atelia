using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Atelia.LiveContextProto.Context;

namespace Atelia.LiveContextProto.Tools;

internal static class ToolArgumentParser {
    private static readonly JsonDocumentOptions DocumentOptions = new() {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public static ToolCallRequest CreateRequest(ITool tool, string toolCallId, string rawArguments) {
        if (tool is null) { throw new ArgumentNullException(nameof(tool)); }
        if (toolCallId is null) { throw new ArgumentNullException(nameof(toolCallId)); }

        var result = ParseArguments(tool, rawArguments);
        return new ToolCallRequest(tool.Name, toolCallId, rawArguments ?? string.Empty, result.Arguments, result.ParseError, result.ParseWarning);
    }

    public static ToolArgumentParsingResult ParseArguments(ITool tool, string rawArguments) {
        if (tool is null) { throw new ArgumentNullException(nameof(tool)); }

        var warnings = new List<string>();
        var errors = new List<string>();
        var arguments = ImmutableDictionary<string, object?>.Empty;
        var text = string.IsNullOrWhiteSpace(rawArguments) ? "{}" : rawArguments;

        try {
            using var document = JsonDocument.Parse(text, DocumentOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object) {
                errors.Add("arguments_must_be_object");
            }
            else {
                arguments = ParseObject(tool, document.RootElement, warnings, errors);
            }
        }
        catch (JsonException ex) {
            errors.Add($"json_parse_error:{ex.Message}");
        }

        foreach (var parameter in tool.Parameters) {
            if (!parameter.IsRequired) { continue; }
            if (arguments.ContainsKey(parameter.Name)) { continue; }
            errors.Add($"missing_required:{parameter.Name}");
        }

        var parseError = errors.Count == 0 ? null : string.Join("; ", errors);
        var parseWarning = warnings.Count == 0 ? null : string.Join("; ", warnings);

        return new ToolArgumentParsingResult(arguments, parseError, parseWarning);
    }

    private static ImmutableDictionary<string, object?> ParseObject(
        ITool tool,
        JsonElement element,
        List<string> warnings,
        List<string> errors
    ) {
        var builder = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.OrdinalIgnoreCase);
        var lookup = CreateParameterLookup(tool.Parameters);

        foreach (var property in element.EnumerateObject()) {
            if (!lookup.TryGetValue(property.Name, out var parameter)) {
                builder[property.Name] = ConvertUntyped(property.Value);
                warnings.Add($"unknown_parameter:{property.Name}");
                continue;
            }

            var parsed = ParseValue(parameter, property.Value);
            if (!parsed.IsSuccess) {
                errors.Add($"{parameter.Name}:{parsed.Error}");
                continue;
            }

            if (!string.IsNullOrEmpty(parsed.Warning)) {
                warnings.Add($"{parameter.Name}:{parsed.Warning}");
            }

            builder[parameter.Name] = parsed.Value;
        }

        return builder.ToImmutable();
    }

    private static Dictionary<string, ToolParameter> CreateParameterLookup(IReadOnlyList<ToolParameter> parameters) {
        var lookup = new Dictionary<string, ToolParameter>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in parameters) {
            lookup[parameter.Name] = parameter;
        }

        return lookup;
    }

    private static ParseResult ParseValue(ToolParameter parameter, JsonElement element) {
        return parameter.Cardinality switch {
            ToolParameterCardinality.Single => ParseScalar(parameter, element),
            ToolParameterCardinality.Optional => ParseScalar(parameter, element),
            ToolParameterCardinality.List => ParseList(parameter, element),
            ToolParameterCardinality.Map => ParseMap(parameter, element),
            _ => ParseResult.CreateError("unsupported_cardinality")
        };
    }

    private static ParseResult ParseScalar(ToolParameter parameter, JsonElement element) {
        if (element.ValueKind == JsonValueKind.Null) {
            return parameter.IsRequired
                ? ParseResult.CreateWarning(null, "null_literal")
                : ParseResult.CreateSuccess(null);
        }

        return parameter.ValueKind switch {
            ToolParameterValueKind.String => ParseString(element),
            ToolParameterValueKind.Boolean => ParseBoolean(element),
            ToolParameterValueKind.Integer => ParseInteger(element),
            ToolParameterValueKind.Number => ParseNumber(element),
            ToolParameterValueKind.JsonObject => ParseJsonObject(element),
            ToolParameterValueKind.JsonArray => ParseJsonArray(element),
            ToolParameterValueKind.Timestamp => ParseTimestamp(element),
            ToolParameterValueKind.Uri => ParseUri(element),
            ToolParameterValueKind.EnumToken => ParseEnumToken(parameter, element),
            ToolParameterValueKind.AttachmentReference => ParseString(element),
            _ => ParseResult.CreateError("unsupported_value_kind")
        };
    }

    private static ParseResult ParseList(ToolParameter parameter, JsonElement element) {
        if (element.ValueKind != JsonValueKind.Array) {
            var coerced = ParseScalar(parameter, element);
            if (!coerced.IsSuccess) { return coerced; }
            return ParseResult.CreateSuccess(ImmutableArray.Create(coerced.Value), CombineWarnings(coerced.Warning, "scalar_coerced_to_list"));
        }

        var builder = ImmutableArray.CreateBuilder<object?>(element.GetArrayLength());
        string? aggregatedWarning = null;

        foreach (var item in element.EnumerateArray()) {
            var parsed = ParseScalar(parameter, item);
            if (!parsed.IsSuccess) { return parsed; }
            builder.Add(parsed.Value);
            aggregatedWarning = CombineWarnings(aggregatedWarning, parsed.Warning);
        }

        return ParseResult.CreateSuccess(builder.ToImmutable(), aggregatedWarning);
    }

    private static ParseResult ParseMap(ToolParameter parameter, JsonElement element) {
        if (element.ValueKind == JsonValueKind.Object) { return ParseResult.CreateSuccess(ConvertObject(element)); }

        if (element.ValueKind == JsonValueKind.String) {
            var text = element.GetString();
            if (string.IsNullOrWhiteSpace(text)) { return ParseResult.CreateSuccess(ImmutableDictionary<string, object?>.Empty, "empty_map_string"); }

            try {
                using var document = JsonDocument.Parse(text, DocumentOptions);
                if (document.RootElement.ValueKind == JsonValueKind.Object) { return ParseResult.CreateSuccess(ConvertObject(document.RootElement), "string_literal_converted_to_object"); }
            }
            catch (JsonException ex) {
                return ParseResult.CreateError($"map_parse_error:{ex.Message}");
            }
        }

        return ParseResult.CreateError("map_requires_object");
    }

    private static ParseResult ParseString(JsonElement element) {
        return element.ValueKind switch {
            JsonValueKind.String => ParseResult.CreateSuccess(element.GetString()),
            JsonValueKind.True or JsonValueKind.False => ParseResult.CreateSuccess(element.GetBoolean().ToString()),
            JsonValueKind.Number => ParseResult.CreateSuccess(element.GetRawText(), "non_string_literal_retained"),
            _ => ParseResult.CreateSuccess(element.GetRawText(), "non_string_literal_retained")
        };
    }

    private static ParseResult ParseBoolean(JsonElement element) {
        return element.ValueKind switch {
            JsonValueKind.True => ParseResult.CreateSuccess(true),
            JsonValueKind.False => ParseResult.CreateSuccess(false),
            JsonValueKind.String => ParseBooleanString(element.GetString()),
            JsonValueKind.Number => ParseBooleanNumber(element),
            _ => ParseResult.CreateError("unsupported_boolean_literal")
        };
    }

    private static ParseResult ParseBooleanString(string? text) {
        if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)) { return ParseResult.CreateSuccess(true, "string_literal_converted_to_boolean"); }

        if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase)) { return ParseResult.CreateSuccess(false, "string_literal_converted_to_boolean"); }

        return ParseResult.CreateError("invalid_boolean_string");
    }

    private static ParseResult ParseBooleanNumber(JsonElement element) {
        if (!element.TryGetDouble(out var value)) { return ParseResult.CreateError("invalid_boolean_number"); }
        return ParseResult.CreateSuccess(Math.Abs(value) > double.Epsilon, "number_coerced_to_boolean");
    }

    private static ParseResult ParseInteger(JsonElement element) {
        if (element.ValueKind == JsonValueKind.Number) {
            if (element.TryGetInt64(out var integer)) { return ParseResult.CreateSuccess(integer); }

            if (element.TryGetDouble(out var number)) {
                var rounded = Math.Truncate(number);
                var warning = Math.Abs(number - rounded) > double.Epsilon
                    ? "fractional_number_truncated_to_integer"
                    : "number_coerced_to_integer";
                return ParseResult.CreateSuccess((long)rounded, warning);
            }

            return ParseResult.CreateError("invalid_integer_number");
        }

        if (element.ValueKind == JsonValueKind.String) {
            var text = element.GetString();
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) { return ParseResult.CreateSuccess(integer, "string_literal_converted_to_integer"); }

            return ParseResult.CreateError("invalid_integer_string");
        }

        return ParseResult.CreateError("unsupported_integer_literal");
    }

    private static ParseResult ParseNumber(JsonElement element) {
        if (element.ValueKind == JsonValueKind.Number) {
            return element.TryGetDouble(out var number)
                ? ParseResult.CreateSuccess(number)
                : ParseResult.CreateError("invalid_number_literal");
        }

        if (element.ValueKind == JsonValueKind.String) {
            var text = element.GetString();
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) { return ParseResult.CreateSuccess(number, "string_literal_converted_to_number"); }

            return ParseResult.CreateError("invalid_number_string");
        }

        return ParseResult.CreateError("unsupported_number_literal");
    }

    private static ParseResult ParseJsonObject(JsonElement element) {
        if (element.ValueKind == JsonValueKind.Object) { return ParseResult.CreateSuccess(ConvertObject(element)); }

        if (element.ValueKind == JsonValueKind.String) {
            var text = element.GetString();
            if (string.IsNullOrWhiteSpace(text)) { return ParseResult.CreateSuccess(ImmutableDictionary<string, object?>.Empty, "empty_object_string"); }

            try {
                using var document = JsonDocument.Parse(text, DocumentOptions);
                if (document.RootElement.ValueKind == JsonValueKind.Object) { return ParseResult.CreateSuccess(ConvertObject(document.RootElement), "string_literal_converted_to_object"); }
            }
            catch (JsonException ex) {
                return ParseResult.CreateError($"object_parse_error:{ex.Message}");
            }
        }

        return ParseResult.CreateError("object_requires_json_object");
    }

    private static ParseResult ParseJsonArray(JsonElement element) {
        if (element.ValueKind == JsonValueKind.Array) { return ParseResult.CreateSuccess(ConvertArray(element)); }

        if (element.ValueKind == JsonValueKind.String) {
            var text = element.GetString();
            if (string.IsNullOrWhiteSpace(text)) { return ParseResult.CreateSuccess(ImmutableArray<object?>.Empty, "empty_array_string"); }

            try {
                using var document = JsonDocument.Parse(text, DocumentOptions);
                if (document.RootElement.ValueKind == JsonValueKind.Array) { return ParseResult.CreateSuccess(ConvertArray(document.RootElement), "string_literal_converted_to_array"); }
            }
            catch (JsonException ex) {
                return ParseResult.CreateError($"array_parse_error:{ex.Message}");
            }
        }

        return ParseResult.CreateError("array_requires_json_array");
    }

    private static ParseResult ParseTimestamp(JsonElement element) {
        if (element.ValueKind != JsonValueKind.String) { return ParseResult.CreateError("timestamp_requires_string"); }

        var text = element.GetString();
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp)
            ? ParseResult.CreateSuccess(timestamp)
            : ParseResult.CreateError("invalid_timestamp_format");
    }

    private static ParseResult ParseUri(JsonElement element) {
        if (element.ValueKind != JsonValueKind.String) { return ParseResult.CreateError("uri_requires_string"); }

        var text = element.GetString();
        return Uri.TryCreate(text, UriKind.Absolute, out var uri)
            ? ParseResult.CreateSuccess(uri)
            : ParseResult.CreateError("invalid_uri_format");
    }

    private static ParseResult ParseEnumToken(ToolParameter parameter, JsonElement element) {
        var scalar = ParseString(element);
        if (!scalar.IsSuccess) { return scalar; }

        if (scalar.Value is string token && parameter.EnumConstraint is not null && !parameter.EnumConstraint.Contains(token)) { return ParseResult.CreateError($"enum_out_of_range:{token}"); }

        return scalar;
    }

    private static ImmutableDictionary<string, object?> ConvertObject(JsonElement element) {
        var builder = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject()) {
            builder[property.Name] = ConvertUntyped(property.Value);
        }
        return builder.ToImmutable();
    }

    private static ImmutableArray<object?> ConvertArray(JsonElement element) {
        var builder = ImmutableArray.CreateBuilder<object?>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray()) {
            builder.Add(ConvertUntyped(item));
        }
        return builder.ToImmutable();
    }

    private static object? ConvertUntyped(JsonElement element) {
        return element.ValueKind switch {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : element.GetDouble(),
            JsonValueKind.Object => ConvertObject(element),
            JsonValueKind.Array => ConvertArray(element),
            JsonValueKind.Null => null,
            _ => element.GetRawText()
        };
    }

    private static string? CombineWarnings(string? first, string? second) {
        if (string.IsNullOrEmpty(first)) { return second; }
        if (string.IsNullOrEmpty(second)) { return first; }
        return string.Concat(first, "; ", second);
    }

    private readonly struct ParseResult {
        private ParseResult(bool isSuccess, object? value, string? warning, string? error) {
            IsSuccess = isSuccess;
            Value = value;
            Warning = warning;
            Error = error;
        }

        public bool IsSuccess { get; }
        public object? Value { get; }
        public string? Warning { get; }
        public string? Error { get; }

        public static ParseResult CreateSuccess(object? value, string? warning = null)
            => new(true, value, warning, null);

        public static ParseResult CreateWarning(object? value, string warning)
            => new(true, value, warning, null);

        public static ParseResult CreateError(string error)
            => new(false, null, null, error);
    }
}

internal sealed record ToolArgumentParsingResult(
    ImmutableDictionary<string, object?> Arguments,
    string? ParseError,
    string? ParseWarning
);
