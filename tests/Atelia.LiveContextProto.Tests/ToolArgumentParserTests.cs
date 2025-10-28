using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Atelia.LiveContextProto.Context;
using Atelia.LiveContextProto.Tools;
using Xunit;

namespace Atelia.LiveContextProto.Tests;

public sealed class ToolArgumentParserTests {
    [Fact]
    public void ParseArguments_Float32PrecisionLossProducesWarning() {
        var tool = new FakeTool(
            "float32-tool",
            new ToolParamSpec(name: "value", description: "float32-value", valueKind: ToolParamValueKind.Float32)
        );

        var payload = "{\"value\": 123456.7890123}";
        var result = ToolArgumentParser.ParseArguments(tool, payload);

        Assert.Null(result.ParseError);
        Assert.NotNull(result.ParseWarning);
        Assert.Contains("float64_precision_loss", result.ParseWarning!, StringComparison.Ordinal);

        Assert.True(result.Arguments.TryGetValue("value", out var value));
        var floatValue = Assert.IsType<float>(value);
        Assert.Equal((float)123456.7890123, floatValue);
    }

    [Fact]
    public void ParseArguments_DecimalFromStringProducesWarning() {
        var tool = new FakeTool(
            "decimal-tool",
            new ToolParamSpec(name: "amount", description: "amount", valueKind: ToolParamValueKind.Decimal)
        );

        var payload = "{\"amount\": \"42.42\"}";
        var result = ToolArgumentParser.ParseArguments(tool, payload);

        Assert.Null(result.ParseError);
        Assert.NotNull(result.ParseWarning);
        Assert.Contains("string_literal_converted_to_decimal", result.ParseWarning!, StringComparison.Ordinal);

        var decimalValue = Assert.IsType<decimal>(result.Arguments["amount"]);
        Assert.Equal(42.42m, decimalValue);
    }

    [Fact]
    public void ParseArguments_Int32OutOfRangeReturnsError() {
        var tool = new FakeTool(
            "int32-tool",
            new ToolParamSpec(name: "count", description: "count", valueKind: ToolParamValueKind.Int32)
        );

        var payload = "{\"count\": 5000000000}";
        var result = ToolArgumentParser.ParseArguments(tool, payload);

        Assert.Null(result.ParseWarning);
        Assert.NotNull(result.ParseError);
        Assert.Contains("int32_out_of_range", result.ParseError!, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseArguments_OptionalParameterMissing_NoError() {
        var tool = new FakeTool(
            "optional-tool",
            new ToolParamSpec(name: "note", description: "note", valueKind: ToolParamValueKind.String,
                isNullable: true,
                defaultValue: new ParamDefault(null)
            )
        );

        var result = ToolArgumentParser.ParseArguments(tool, "{}");

        Assert.Null(result.ParseError);
        Assert.Null(result.ParseWarning);
        Assert.Empty(result.Arguments);
    }

    [Fact]
    public void ParseArguments_RequiredStringIsNullable_SucceedsWithNullValue() {
        var tool = new FakeTool(
            "nullable-string-tool",
            new ToolParamSpec(name: "comment", description: "comment", valueKind: ToolParamValueKind.String,
                isNullable: true
            )
        );

        var result = ToolArgumentParser.ParseArguments(tool, "{\"comment\": null}");

        Assert.Null(result.ParseError);
        Assert.Null(result.ParseWarning);

        Assert.True(result.Arguments.TryGetValue("comment", out var value));
        Assert.Null(value);
    }

    [Fact]
    public void ParseArguments_NullForNonNullableOptionalParameter_ProducesError() {
        var tool = new FakeTool(
            "optional-int-tool",
            new ToolParamSpec(name: "count", description: "count", valueKind: ToolParamValueKind.Int32,
                defaultValue: new ParamDefault(0)
            )
        );

        var result = ToolArgumentParser.ParseArguments(tool, "{\"count\": null}");

        Assert.Null(result.ParseWarning);
        Assert.NotNull(result.ParseError);
        Assert.Contains("count:null_not_allowed", result.ParseError!, StringComparison.Ordinal);
    }

    private sealed class FakeTool : ITool {
        private readonly ImmutableArray<ToolParamSpec> _parameters;

        public FakeTool(string name, params ToolParamSpec[] parameters) {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            _parameters = parameters is { Length: > 0 }
                ? ImmutableArray.Create(parameters)
                : ImmutableArray<ToolParamSpec>.Empty;
        }

        public string Name { get; }

        public string Description => "fake-tool";

        public IReadOnlyList<ToolParamSpec> Parameters => _parameters;

        public ValueTask<LodToolExecuteResult> ExecuteAsync(IReadOnlyDictionary<string, object?>? arguments, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
