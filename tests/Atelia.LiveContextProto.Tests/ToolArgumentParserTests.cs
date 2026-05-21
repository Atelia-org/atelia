using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Xunit;

namespace Atelia.LiveContextProto.Tests;

public sealed class ToolArgumentParserTests {
    [Fact]
    public void ParseArguments_Float32PrecisionLossProducesWarning() {
        var schema = new ToolSchema.Object(
            [
                new ToolSchema.Property(
                    "value",
                    new ToolSchema.Value(ToolParamType.Float32, description: "float32-value"),
                    isRequired: true
                )
            ]
        );

        var payload = "{\"value\": 123456.7890123}";
        var result = JsonArgumentParser.ParseArguments(schema, payload);

        Assert.Null(result.ParseError);
        Assert.NotNull(result.ParseWarning);
        Assert.Contains("value:float64_precision_loss", result.ParseWarning!, StringComparison.Ordinal);

        Assert.Equal("123456.7890123", result.RawArguments["value"]);

        Assert.True(result.Arguments.TryGetValue("value", out var value));
        var floatValue = Assert.IsType<float>(value);
        Assert.Equal((float)123456.7890123, floatValue);
    }

    [Fact]
    public void ParseArguments_DecimalFromStringReturnsTypeError() {
        var schema = new ToolSchema.Object(
            [
                new ToolSchema.Property(
                    "amount",
                    new ToolSchema.Value(ToolParamType.Decimal, description: "amount"),
                    isRequired: true
                )
            ]
        );

        var payload = "{\"amount\": \"42.42\"}";
        var result = JsonArgumentParser.ParseArguments(schema, payload);

        Assert.NotNull(result.ParseError);
        Assert.Contains("amount:expected_number", result.ParseError!, StringComparison.Ordinal);
        Assert.Null(result.ParseWarning);

        Assert.Equal("42.42", result.RawArguments["amount"]);
        Assert.False(result.Arguments.ContainsKey("amount"));
    }

    [Fact]
    public void ParseArguments_Int32OutOfRangeReturnsError() {
        var schema = new ToolSchema.Object(
            [
                new ToolSchema.Property(
                    "count",
                    new ToolSchema.Value(ToolParamType.Int32, description: "count"),
                    isRequired: true
                )
            ]
        );

        var payload = "{\"count\": 5000000000}";
        var result = JsonArgumentParser.ParseArguments(schema, payload);

        Assert.Null(result.ParseWarning);
        Assert.NotNull(result.ParseError);
        Assert.Contains("count:int32_out_of_range", result.ParseError!, StringComparison.Ordinal);

        Assert.Equal("5000000000", result.RawArguments["count"]);
        Assert.False(result.Arguments.ContainsKey("count"));
    }

    [Fact]
    public void ParseArguments_OptionalParameterMissing_NoError() {
        var schema = new ToolSchema.Object(
            [
                new ToolSchema.Property(
                    "note",
                    new ToolSchema.Value(
                        ToolParamType.String,
                        isNullable: true,
                        defaultValue: new ParamDefault(null),
                        description: "note"
                    ),
                    isRequired: false
                )
            ]
        );

        var result = JsonArgumentParser.ParseArguments(schema, "{}");

        Assert.Null(result.ParseError);
        Assert.Null(result.ParseWarning);
        Assert.Empty(result.Arguments);
        Assert.Empty(result.RawArguments);
    }

    [Fact]
    public void ParseArguments_RequiredStringIsNullable_SucceedsWithNullValue() {
        var schema = new ToolSchema.Object(
            [
                new ToolSchema.Property(
                    "comment",
                    new ToolSchema.Value(ToolParamType.String, isNullable: true, description: "comment"),
                    isRequired: true
                )
            ]
        );

        var result = JsonArgumentParser.ParseArguments(schema, "{\"comment\": null}");

        Assert.Null(result.ParseError);
        Assert.Null(result.ParseWarning);

        Assert.Equal("null", result.RawArguments["comment"]);
        Assert.True(result.Arguments.TryGetValue("comment", out var value));
        Assert.Null(value);
    }

    [Fact]
    public void ParseArguments_NullForNonNullableOptionalParameter_ProducesError() {
        var schema = new ToolSchema.Object(
            [
                new ToolSchema.Property(
                    "count",
                    new ToolSchema.Value(
                        ToolParamType.Int32,
                        defaultValue: new ParamDefault(0),
                        description: "count"
                    ),
                    isRequired: false
                )
            ]
        );

        var result = JsonArgumentParser.ParseArguments(schema, "{\"count\": null}");

        Assert.Null(result.ParseWarning);
        Assert.NotNull(result.ParseError);
        Assert.Contains("count:null_not_allowed", result.ParseError!, StringComparison.Ordinal);
        Assert.Equal("null", result.RawArguments["count"]);
    }

    [Fact]
    public void ParseArguments_NestedSchemaProducesPathAwareErrorsAndStableShapes() {
        var schema = new ToolSchema.Object(
            [
                new ToolSchema.Property(
                    "filters",
                    new ToolSchema.Object(
                        [
                            new ToolSchema.Property(
                                "tags",
                                new ToolSchema.Array(
                                    new ToolSchema.Value(ToolParamType.String, minLength: 2, description: "tag"),
                                    description: "tags"
                                ),
                                isRequired: true
                            )
                        ],
                        description: "filters"
                    ),
                    isRequired: true
                )
            ]
        );

        var invalidResult = JsonArgumentParser.ParseArguments(schema, "{\"filters\":{\"tags\":[\"ok\",\"x\"]}}");
        Assert.NotNull(invalidResult.ParseError);
        Assert.Contains("filters.tags[1]:string_too_short", invalidResult.ParseError!, StringComparison.Ordinal);
        Assert.Equal("x", invalidResult.RawArguments["filters.tags[1]"]);
        Assert.False(invalidResult.Arguments.ContainsKey("filters"));

        var validResult = JsonArgumentParser.ParseArguments(schema, "{\"filters\":{\"tags\":[\"ok\",\"go\"]}}");
        Assert.Null(validResult.ParseError);

        var filters = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(validResult.Arguments["filters"]);
        var tags = Assert.IsAssignableFrom<IReadOnlyList<object?>>(filters["tags"]);
        Assert.Equal(["ok", "go"], tags);
    }

    [Fact]
    public void ParseArguments_AdditionalPropertiesFalseRejectsUnknownPropertyWithExactCaseMatching() {
        var schema = new ToolSchema.Object(
            [
                new ToolSchema.Property(
                    "payload",
                    new ToolSchema.Value(ToolParamType.String, description: "payload"),
                    isRequired: true
                )
            ],
            additionalProperties: false
        );

        var result = JsonArgumentParser.ParseArguments(schema, "{\"Payload\":\"value\"}");

        Assert.NotNull(result.ParseError);
        Assert.Contains("Payload:unknown_property", result.ParseError!, StringComparison.Ordinal);
        Assert.Contains("payload:missing_required", result.ParseError!, StringComparison.Ordinal);
        Assert.Null(result.ParseWarning);
    }

    [Fact]
    public void ParseArguments_AdditionalPropertiesTrueConvertsUnknownPropertyToStableUntypedShapes() {
        var schema = new ToolSchema.Object(additionalProperties: true);

        var result = JsonArgumentParser.ParseArguments(schema, "{\"filters\":{\"tags\":[\"alpha\",2,true]}}");

        Assert.Null(result.ParseError);
        Assert.Null(result.ParseWarning);

        var filters = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(result.Arguments["filters"]);
        var tags = Assert.IsAssignableFrom<IReadOnlyList<object?>>(filters["tags"]);
        Assert.Equal("alpha", tags[0]);
        Assert.Equal(2d, Convert.ToDouble(tags[1], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(true, tags[2]);
    }

    [Fact]
    public void ParseArguments_ValueConstraintsAreValidatedAtExecutionTime() {
        var schema = new ToolSchema.Object(
            [
                new ToolSchema.Property(
                    "mode",
                    new ToolSchema.Value(
                        ToolParamType.String,
                        description: "mode",
                        stringEnumValues: ["fast", "safe"],
                        minLength: 4,
                        maxLength: 4,
                        pattern: "^[a-z]+$"
                    ),
                    isRequired: true
                ),
                new ToolSchema.Property(
                    "count",
                    new ToolSchema.Value(
                        ToolParamType.Int32,
                        description: "count",
                        minimum: 1,
                        maximum: 3
                    ),
                    isRequired: true
                )
            ]
        );

        var enumMismatch = JsonArgumentParser.ParseArguments(schema, "{\"mode\":\"slow\",\"count\":2}");
        Assert.Contains("mode:string_enum_mismatch", enumMismatch.ParseError!, StringComparison.Ordinal);

        var patternSchema = new ToolSchema.Object(
            [
                new ToolSchema.Property(
                    "token",
                    new ToolSchema.Value(
                        ToolParamType.String,
                        description: "token",
                        pattern: "^[a-z]+$"
                    ),
                    isRequired: true
                )
            ]
        );

        var patternMismatch = JsonArgumentParser.ParseArguments(patternSchema, "{\"token\":\"FASt\"}");
        Assert.Contains("token:string_pattern_mismatch", patternMismatch.ParseError!, StringComparison.Ordinal);

        var belowMinimum = JsonArgumentParser.ParseArguments(schema, "{\"mode\":\"fast\",\"count\":0}");
        Assert.Contains("count:number_below_minimum", belowMinimum.ParseError!, StringComparison.Ordinal);

        var aboveMaximum = JsonArgumentParser.ParseArguments(schema, "{\"mode\":\"safe\",\"count\":5}");
        Assert.Contains("count:number_above_maximum", aboveMaximum.ParseError!, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseArguments_StringSchemaRejectsNonStringJsonTypes() {
        var schema = new ToolSchema.Object(
            [
                new ToolSchema.Property(
                    "text",
                    new ToolSchema.Value(ToolParamType.String, description: "text"),
                    isRequired: true
                )
            ]
        );

        Assert.Contains("text:expected_string", JsonArgumentParser.ParseArguments(schema, "{\"text\":123}").ParseError!, StringComparison.Ordinal);
        Assert.Contains("text:expected_string", JsonArgumentParser.ParseArguments(schema, "{\"text\":true}").ParseError!, StringComparison.Ordinal);
        Assert.Contains("text:expected_string", JsonArgumentParser.ParseArguments(schema, "{\"text\":{}}").ParseError!, StringComparison.Ordinal);
        Assert.Contains("text:expected_string", JsonArgumentParser.ParseArguments(schema, "{\"text\":[]}").ParseError!, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseArguments_BooleanSchemaRejectsStringAndNumber() {
        var schema = new ToolSchema.Object(
            [
                new ToolSchema.Property(
                    "flag",
                    new ToolSchema.Value(ToolParamType.Boolean, description: "flag"),
                    isRequired: true
                )
            ]
        );

        Assert.Contains("flag:expected_boolean", JsonArgumentParser.ParseArguments(schema, "{\"flag\":\"true\"}").ParseError!, StringComparison.Ordinal);
        Assert.Contains("flag:expected_boolean", JsonArgumentParser.ParseArguments(schema, "{\"flag\":1}").ParseError!, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseArguments_IntegerSchemasRejectStringAndFractionalNumber() {
        var int32Schema = new ToolSchema.Object(
            [
                new ToolSchema.Property(
                    "count",
                    new ToolSchema.Value(ToolParamType.Int32, description: "count"),
                    isRequired: true
                )
            ]
        );
        var int64Schema = new ToolSchema.Object(
            [
                new ToolSchema.Property(
                    "total",
                    new ToolSchema.Value(ToolParamType.Int64, description: "total"),
                    isRequired: true
                )
            ]
        );

        Assert.Contains("count:expected_integer", JsonArgumentParser.ParseArguments(int32Schema, "{\"count\":\"12\"}").ParseError!, StringComparison.Ordinal);
        Assert.Contains("count:expected_integer", JsonArgumentParser.ParseArguments(int32Schema, "{\"count\":12.5}").ParseError!, StringComparison.Ordinal);
        Assert.Contains("total:expected_integer", JsonArgumentParser.ParseArguments(int64Schema, "{\"total\":\"12\"}").ParseError!, StringComparison.Ordinal);
        Assert.Contains("total:expected_integer", JsonArgumentParser.ParseArguments(int64Schema, "{\"total\":12.5}").ParseError!, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseArguments_FloatingPointSchemasRejectStringValues() {
        var float64Schema = new ToolSchema.Object(
            [
                new ToolSchema.Property(
                    "score",
                    new ToolSchema.Value(ToolParamType.Float64, description: "score"),
                    isRequired: true
                )
            ]
        );
        var decimalSchema = new ToolSchema.Object(
            [
                new ToolSchema.Property(
                    "amount",
                    new ToolSchema.Value(ToolParamType.Decimal, description: "amount"),
                    isRequired: true
                )
            ]
        );

        Assert.Contains("score:expected_number", JsonArgumentParser.ParseArguments(float64Schema, "{\"score\":\"3.14\"}").ParseError!, StringComparison.Ordinal);
        Assert.Contains("amount:expected_number", JsonArgumentParser.ParseArguments(decimalSchema, "{\"amount\":\"3.14\"}").ParseError!, StringComparison.Ordinal);
    }
}
