using System;
using System.Threading;
using System.Threading.Tasks;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Xunit;

namespace Atelia.LiveContextProto.Tests;

public sealed class MethodToolWrapperTests {
    private sealed class SampleToolHost {
        public int InvocationCount { get; private set; }
        public string? LastNote { get; private set; }
        public int LastOptionalValue { get; private set; }
        public int LastCount { get; private set; }

        [Tool("sample_tool", "Sample tool for unit testing")]
        public ValueTask<ToolExecuteResult> SampleAsync(
            [ToolParam("nullable note")] string? note,
            [ToolParam("optional value")] int optionalValue = 0,
            [ToolParam("explicit default parameter")] int count = 42,
            CancellationToken cancellationToken = default
        ) {
            InvocationCount++;
            LastNote = note;
            LastOptionalValue = optionalValue;
            LastCount = count;
            return ValueTask.FromResult(new ToolExecuteResult(ToolExecutionStatus.Success, $"note={note ?? "<null>"} optional={optionalValue} count={count}"));
        }
    }

    private sealed class MissingAttributeHost {
        [Tool("missing_attribute_tool", "Tool missing parameter attribute for testing")]
        public ValueTask<ToolExecuteResult> ExecuteAsync(string note, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    private sealed class FormattedToolHost {
        [Tool("{0}_formatted", "内测工具：{2} -> {1}")]
        public ValueTask<ToolExecuteResult> FormatAsync(
            [ToolParam("{2} 输入文本")] string text,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();
    }

    [Fact]
    public void FromMethod_PopulatesDefaultMetadataAndDescriptionHints() {
        var host = new SampleToolHost();
        var method = typeof(SampleToolHost).GetMethod(nameof(SampleToolHost.SampleAsync))!;
        var wrapper = MethodToolWrapper.FromMethod(host, method);

        var inputSchema = Assert.IsType<ToolSchema.Object>(wrapper.Definition.InputSchema);
        Assert.Equal(3, inputSchema.Properties.Length);

        var noteProperty = inputSchema.Properties[0];
        Assert.Equal("note", noteProperty.Name);
        Assert.True(noteProperty.IsRequired);
        var noteSchema = Assert.IsType<ToolSchema.Value>(noteProperty.Schema);
        Assert.True(noteSchema.IsNullable);
        Assert.False(noteSchema.Default.HasValue);
        Assert.Contains("必填", noteSchema.Description);
        Assert.DoesNotContain("可省略", noteSchema.Description);
        Assert.DoesNotContain("默认值:", noteSchema.Description);
        Assert.Contains("允许 null", noteSchema.Description);

        var optionalValueProperty = inputSchema.Properties[1];
        Assert.Equal("optionalValue", optionalValueProperty.Name);
        Assert.False(optionalValueProperty.IsRequired);
        var optionalValueSchema = Assert.IsType<ToolSchema.Value>(optionalValueProperty.Schema);
        Assert.False(optionalValueSchema.IsNullable);
        Assert.True(optionalValueSchema.Default.HasValue);
        Assert.Equal(0, Assert.IsType<int>(optionalValueSchema.Default.Value.Value));
        Assert.Contains("可省略", optionalValueSchema.Description);
        Assert.Contains("默认值: 0", optionalValueSchema.Description);
        Assert.DoesNotContain("允许 null", optionalValueSchema.Description);

        var countProperty = inputSchema.Properties[2];
        Assert.Equal("count", countProperty.Name);
        Assert.False(countProperty.IsRequired);
        var countSchema = Assert.IsType<ToolSchema.Value>(countProperty.Schema);
        Assert.False(countSchema.IsNullable);
        Assert.True(countSchema.Default.HasValue);
        Assert.Equal(42, Assert.IsType<int>(countSchema.Default.Value.Value));
        Assert.Contains("可省略", countSchema.Description);
        Assert.Contains("默认值: 42", countSchema.Description);
        Assert.DoesNotContain("允许 null", countSchema.Description);
    }

    [Fact]
    public void FromMethod_WithoutParameterAttribute_Throws() {
        var host = new MissingAttributeHost();
        var method = typeof(MissingAttributeHost).GetMethod(nameof(MissingAttributeHost.ExecuteAsync))!;

        var exception = Assert.Throws<InvalidOperationException>(() => MethodToolWrapper.FromMethod(host, method));
        Assert.Contains("missing ToolParamAttribute", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromMethod_AppliesFormatArguments() {
        var host = new FormattedToolHost();
        var method = typeof(FormattedToolHost).GetMethod(nameof(FormattedToolHost.FormatAsync))!;

        var wrapper = MethodToolWrapper.FromMethod(host, method, "demo", "目标", "展示");

        Assert.Equal("demo_formatted", wrapper.Definition.Name);
        Assert.Equal("内测工具：展示 -> 目标", wrapper.Definition.Description);

        var inputSchema = Assert.IsType<ToolSchema.Object>(wrapper.Definition.InputSchema);
        var property = Assert.Single(inputSchema.Properties);
        Assert.Equal("text", property.Name);
        var paramSchema = Assert.IsType<ToolSchema.Value>(property.Schema);
        Assert.True(property.IsRequired);
        Assert.Contains("必填", paramSchema.Description);
        Assert.Contains("展示 输入文本", paramSchema.Description);
    }

    [Fact]
    public async Task ExecuteAsync_RawToolCall_BindsJsonAndAppliesDefaults() {
        var host = new SampleToolHost();
        var method = typeof(SampleToolHost).GetMethod(nameof(SampleToolHost.SampleAsync))!;
        var wrapper = MethodToolWrapper.FromMethod(host, method);
        var request = new RawToolCall("sample_tool", "call-1", "{\"note\":null,\"count\":7}");

        var result = await wrapper.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.Success, result.Status);
        Assert.Equal(1, host.InvocationCount);
        Assert.Null(host.LastNote);
        Assert.Equal(0, host.LastOptionalValue);
        Assert.Equal(7, host.LastCount);
        Assert.Equal("note=<null> optional=0 count=7", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_RawToolCall_ParseFailureReturnsFailedResultWithoutInvokingMethod() {
        var host = new SampleToolHost();
        var method = typeof(SampleToolHost).GetMethod(nameof(SampleToolHost.SampleAsync))!;
        var wrapper = MethodToolWrapper.FromMethod(host, method);
        const string rawArguments = "{\"note\":null,\"count\":\"oops\"}";
        var request = new RawToolCall("sample_tool", "call-2", rawArguments);

        var result = await wrapper.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(0, host.InvocationCount);
        Assert.Contains("工具参数解析失败。", result.Content, StringComparison.Ordinal);
        Assert.Contains("count:expected_integer", result.Content, StringComparison.Ordinal);
        Assert.Contains($"raw_arguments_json: {rawArguments}", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolExecutor_ShouldAcceptNestedDefinitionAfterSchemaDrivenParsing() {
        var tool = new SchemaTool(
            new ToolDefinition(
                "nested_schema_tool",
                "Tool with nested input schema",
                new ToolSchema.Object(
                    [
                        new ToolSchema.Property(
                            "payload",
                            new ToolSchema.Object(
                                [
                                    new ToolSchema.Property(
                                        "note",
                                        new ToolSchema.Value(ToolParamType.String, description: "nested note"),
                                        isRequired: true
                                    )
                                ],
                                description: "nested payload"
                            ),
                            isRequired: true
                        )
                    ]
                )
            )
        );

        var executor = new ToolExecutor([tool]);

        Assert.Single(executor.AllToolDefinitions);
        Assert.Same(tool.Definition, executor.AllToolDefinitions[0]);
    }

    private sealed class SchemaTool : ITool {
        public SchemaTool(ToolDefinition definition) {
            Definition = definition;
        }

        public ToolDefinition Definition { get; }
        public bool Visible { get; set; } = true;

        public ValueTask<ToolExecuteResult> ExecuteAsync(IReadOnlyDictionary<string, object?>? arguments, CancellationToken cancellationToken)
            => throw new NotImplementedException();
    }
}
