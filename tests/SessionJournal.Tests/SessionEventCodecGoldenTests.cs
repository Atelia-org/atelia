using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionEventCodecGoldenTests {
    private static readonly SessionToolRuntimeIdentity ToolIdentity = new(
        "host-A",
        "implementation-A",
        "capability-A"
    );

    [Theory]
    [InlineData(SessionEventKind.AgentActionProduced)]
    [InlineData(SessionEventKind.ImportedAgentAction)]
    public void AgentActionKinds_HaveExactSharedV1Utf8BodyShape(SessionEventKind kind) {
        ReadOnlySpan<byte> expected =
            """{"v":1,"body":{"action":[{"kind":"text","content":"hello"}],"invocation":{"providerId":"provider-A","apiSpecId":"api-A","model":"model-A"},"correlationId":"correlation-A","execution":{"lastIssuedToolExecutionSequence":0},"toolRuntimeIdentity":null}}"""u8;
        var body = new AgentActionProducedBody(
            new ActionMessage([new ActionBlock.Text("hello")]),
            new CompletionDescriptor("provider-A", "api-A", "model-A"),
            "correlation-A",
            new SessionExecutionCheckpoint(0),
            null
        );

        AssertExactUtf8WriterAndLiteralRoundtrip(kind, body, expected, expectedVersion: 1);
    }

    [Fact]
    public void ToolExecutionStarted_HasExactV1Utf8BodyShape() {
        ReadOnlySpan<byte> expected =
            """{"v":1,"body":{"toolCallId":"call-1","toolName":"lookup","rawArgumentsJson":"{}","operationId":"operation-1","executionSequence":1,"toolRuntimeIdentity":{"hostId":"host-A","implementationSetFingerprint":"implementation-A","capabilitySetFingerprint":"capability-A"}}}"""u8;
        var body = new ToolExecutionStartedBody(
            "call-1",
            "lookup",
            "{}",
            "operation-1",
            1,
            ToolIdentity
        );

        AssertExactUtf8WriterAndLiteralRoundtrip(
            SessionEventKind.ToolExecutionStarted,
            body,
            expected,
            expectedVersion: 1
        );
    }

    [Fact]
    public void ToolResultObserved_HasExactV1Utf8BodyShape() {
        ReadOnlySpan<byte> expected =
            """{"v":1,"body":{"toolCallId":"call-1","toolName":"lookup","executionSequence":1,"status":"success","blocks":[{"kind":"text","content":"done"}]}}"""u8;
        var body = new ToolResultObservedBody(
            "call-1",
            "lookup",
            1,
            ToolExecutionStatus.Success,
            [new ToolResultBlock.Text("done")]
        );

        AssertExactUtf8WriterAndLiteralRoundtrip(
            SessionEventKind.ToolResultObserved,
            body,
            expected,
            expectedVersion: 1
        );
    }

    [Fact]
    public void CompletionAttemptFailed_HasExactV2Utf8BodyShape() {
        ReadOnlySpan<byte> expected =
            """{"v":2,"body":{"terminationKind":"incomplete","providerReason":"length","detail":"max tokens","errors":["stream warning"]}}"""u8;
        var body = new CompletionAttemptFailedBody(
            CompletionTerminationKind.Incomplete,
            "length",
            "max tokens",
            ["stream warning"]
        );

        AssertExactUtf8WriterAndLiteralRoundtrip(
            SessionEventKind.CompletionAttemptFailed,
            body,
            expected,
            expectedVersion: 2
        );
    }

    private static void AssertExactUtf8WriterAndLiteralRoundtrip(
        SessionEventKind kind,
        object body,
        ReadOnlySpan<byte> expected,
        int expectedVersion
    ) {
        byte[] encoded = SessionEventCodec.Encode(kind, body);
        Assert.True(expected.SequenceEqual(encoded));

        object decoded = SessionEventCodec.Decode(kind, expected, out int version);
        Assert.Equal(expectedVersion, version);
        Assert.IsType(body.GetType(), decoded);
        Assert.True(expected.SequenceEqual(SessionEventCodec.Encode(kind, decoded)));
    }
}
