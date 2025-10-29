using System;
using Atelia.LiveContextProto.Context;
using Xunit;

namespace Atelia.LiveContextProto.Tests;

public sealed class ToolParamSpecTests {
    [Fact]
    public void Ctor_NonNullableStringWithNullDefault_Throws() {
        var exception = Assert.Throws<ArgumentException>(
            () =>
            new ToolParamSpec(
                name: "id",
                description: "identifier",
                valueKind: ToolParamValueKind.String,
                isNullable: false,
                defaultValue: new ParamDefault(null)
            )
        );

        Assert.Contains("cannot be null", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ctor_Int32WithNonIntegerDefault_Throws() {
        var exception = Assert.Throws<ArgumentException>(
            () =>
            new ToolParamSpec(
                name: "count",
                description: "count",
                valueKind: ToolParamValueKind.Int32,
                defaultValue: new ParamDefault(3.14)
            )
        );

        Assert.Contains("cannot be assigned", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
