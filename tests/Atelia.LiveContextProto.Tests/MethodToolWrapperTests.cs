using System;
using System.Threading;
using System.Threading.Tasks;
using Atelia.LiveContextProto.Tool;
using Xunit;

namespace Atelia.LiveContextProto.Tests;

public sealed class MethodToolWrapperTests {
    private sealed class SampleToolHost {
        [Tool("sample_tool", "Sample tool for unit testing")]
        public ValueTask<LodToolExecuteResult> SampleAsync(
            [ToolParam("nullable note")] string? note,
            [ToolParam("optional value")] int optionalValue = 0,
            [ToolParam("explicit default parameter")] int count = 42,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();
    }

    private sealed class MissingAttributeHost {
        [Tool("missing_attribute_tool", "Tool missing parameter attribute for testing")]
        public ValueTask<LodToolExecuteResult> ExecuteAsync(string note, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    [Fact]
    public void FromMethod_PopulatesDefaultMetadataAndDescriptionHints() {
        var host = new SampleToolHost();
        var method = typeof(SampleToolHost).GetMethod(nameof(SampleToolHost.SampleAsync))!;
        var wrapper = MethodToolWrapper.FromMethod(host, method);

        Assert.Equal(3, wrapper.Parameters.Count);

        var noteSpec = wrapper.Parameters[0];
        Assert.False(noteSpec.IsOptional);
        Assert.False(noteSpec.TryGetDefaultValue(out var noteDefault));
        Assert.Null(noteDefault);
        Assert.DoesNotContain("可省略", noteSpec.Description);
        Assert.DoesNotContain("默认值:", noteSpec.Description);
        Assert.Contains("允许 null", noteSpec.Description);

        var optionalValueSpec = wrapper.Parameters[1];
        Assert.True(optionalValueSpec.IsOptional);
        Assert.True(optionalValueSpec.TryGetDefaultValue(out var optionalDefault));
        Assert.Equal(0, Assert.IsType<int>(optionalDefault));
        Assert.Contains("可省略", optionalValueSpec.Description);
        Assert.Contains("默认值: 0", optionalValueSpec.Description);
        Assert.DoesNotContain("允许 null", optionalValueSpec.Description);

        var countSpec = wrapper.Parameters[2];
        Assert.True(countSpec.IsOptional);
        Assert.True(countSpec.TryGetDefaultValue(out var countDefault));
        Assert.Equal(42, Assert.IsType<int>(countDefault));
        Assert.Contains("可省略", countSpec.Description);
        Assert.Contains("默认值: 42", countSpec.Description);
        Assert.DoesNotContain("允许 null", countSpec.Description);
    }

    [Fact]
    public void FromMethod_WithoutParameterAttribute_Throws() {
        var host = new MissingAttributeHost();
        var method = typeof(MissingAttributeHost).GetMethod(nameof(MissingAttributeHost.ExecuteAsync))!;

        var exception = Assert.Throws<InvalidOperationException>(() => MethodToolWrapper.FromMethod(host, method));
        Assert.Contains("missing ToolParamAttribute", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
