using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.DerivedRecap.Abstractions;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Maintainers.Tests;

public sealed class StructuredRecapMaintainerOutputProtocolTests {
    private static StructuredRecapMaintainerOutputProtocol Protocol =>
        StructuredRecapMaintainerOutputProtocol.Shared;

    [Fact]
    public void RequestContract_IsOneSharedStructuredSubmitTool() {
        CompletionOutputContract contract = Protocol.RequestContract;

        ToolDefinition tool = Assert.Single(contract.Tools);
        Assert.Equal(
            StructuredRecapMaintainerOutputProtocol.SubmitToolName,
            tool.Name
        );
        Assert.Equal(
            CompletionToolChoiceKind.Auto,
            contract.ToolChoice.Kind
        );
        Assert.Null(contract.AllowParallelToolCalls);
        var schema = Assert.IsType<ToolSchema.Object>(tool.InputSchema);
        Assert.False(schema.AdditionalProperties);
        Assert.Equal(["outcome", "content"], schema.Properties
            .Select(static property => property.Name));
        Assert.All(
            schema.Properties,
            static property => Assert.True(property.IsRequired)
        );
    }

    [Fact]
    public void Parse_UpdatedAndKeepUnchanged() {
        RecapMaintenanceSuccess.Updated updated = Assert.IsType<
            RecapMaintenanceSuccess.Updated
        >(Protocol.ParseAndValidate(Result(
            "{\"outcome\":\"updated\",\"content\":\"new recap\"}"
        )));
        Assert.Equal("new recap", updated.Content);

        Assert.Same(
            RecapMaintenanceSuccess.KeepUnchanged.Instance,
            Protocol.ParseAndValidate(Result(
                "{\"outcome\":\"keep-unchanged\",\"content\":null}"
            ))
        );
    }

    public static TheoryData<string> InvalidArguments => new() {
        "{}",
        "[]",
        "{\"outcome\":\"updated\"}",
        "{\"outcome\":\"updated\",\"content\":null}",
        "{\"outcome\":\"updated\",\"content\":\"   \"}",
        "{\"outcome\":\"keep-unchanged\",\"content\":\"old\"}",
        "{\"outcome\":\"other\",\"content\":null}",
        "{\"outcome\":\"updated\",\"content\":\"x\",\"extra\":1}",
        "{\"outcome\":\"updated\",\"outcome\":\"updated\",\"content\":\"x\"}",
        "{\"outcome\":\"updated\",\"content\":\"x\",}"
    };

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public void Parse_RejectsInvalidArguments(string arguments) {
        Assert.Throws<InvalidDataException>(
            () => Protocol.ParseAndValidate(Result(arguments))
        );
    }

    [Fact]
    public void Parse_RejectsTextWrongToolAndMultipleCalls() {
        Assert.Throws<InvalidDataException>(() =>
            Protocol.ParseAndValidate(new CompletionResult(
                new ActionMessage([
                    new ActionBlock.Text("plaintext"),
                    ToolCall("recap.submit", "{}")
                ]),
                Invocation()
            ))
        );
        Assert.Throws<InvalidDataException>(() =>
            Protocol.ParseAndValidate(new CompletionResult(
                new ActionMessage([
                    ToolCall("wrong", "{}")
                ]),
                Invocation()
            ))
        );
        Assert.Throws<InvalidDataException>(() =>
            Protocol.ParseAndValidate(new CompletionResult(
                new ActionMessage([
                    ToolCall("recap.submit", "{}", "one"),
                    ToolCall("recap.submit", "{}", "two")
                ]),
                Invocation()
            ))
        );
    }

    private static CompletionResult Result(string arguments) => new(
        new ActionMessage([
            ToolCall("recap.submit", arguments)
        ]),
        Invocation()
    );

    private static ActionBlock.ToolCall ToolCall(
        string name,
        string arguments,
        string id = "call-1"
    ) => new(new RawToolCall(name, id, arguments));

    private static CompletionDescriptor Invocation() => new(
        "scripted",
        "test-v1",
        "model"
    );
}
