using System.Text;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionEventCodecStrictnessTests {
    private static readonly SessionToolRuntimeIdentity ToolIdentity = new(
        "host-A",
        "implementation-A",
        "capability-A"
    );

    [Fact]
    public void EveryEventEnvelopeRejectsUnknownAndDuplicateProperties() {
        foreach (SessionEventKind kind in Enum.GetValues<SessionEventKind>()) {
            int[] versions = kind == SessionEventKind.CompletionRequestPrepared
                ? [5, 6]
                : [SessionEventCodec.GetExpectedBodySchemaVersion(kind)];
            foreach (int version in versions) {
                AssertInvalid(
                    kind,
                    $"{{\"v\":{version},\"body\":{{}},\"unexpected\":true}}"
                );
                AssertInvalid(
                    kind,
                    $"{{\"v\":{version},\"v\":{version},\"body\":{{}}}}"
                );
            }
        }
    }

    [Fact]
    public void EveryNonEmptyNonPreparedBodyRejectsUnknownAndDuplicateProperties() {
        foreach ((SessionEventKind kind, object body, string duplicate) in Bodies()) {
            string canonical = Encoding.UTF8.GetString(
                SessionEventCodec.Encode(kind, body)
            );
            AssertInvalid(
                kind,
                ReplaceOnce(canonical, "\"body\":{", "\"body\":{\"unexpected\":true,")
            );
            AssertInvalid(
                kind,
                ReplaceOnce(canonical, "\"body\":{", $"\"body\":{{{duplicate},")
            );
        }

        AssertInvalid(
            SessionEventKind.CompletionAttemptStarted,
            """{"v":1,"body":{"unexpected":true}}"""
        );
    }

    [Fact]
    public void NestedDiscriminatedObjectsRejectShapeDrift() {
        string textAction = Encode(
            SessionEventKind.AgentActionProduced,
            ActionBody(new ActionBlock.Text("hello"))
        );
        AssertInvalid(
            SessionEventKind.AgentActionProduced,
            ReplaceOnce(
                textAction,
                "\"kind\":\"text\",\"content\":\"hello\"",
                "\"kind\":\"text\",\"content\":\"hello\",\"unexpected\":true"
            )
        );
        AssertInvalid(
            SessionEventKind.AgentActionProduced,
            ReplaceOnce(
                textAction,
                "\"kind\":\"text\",\"content\":\"hello\"",
                "\"kind\":\"text\",\"content\":\"hello\",\"content\":\"again\""
            )
        );

        string toolCallAction = Encode(
            SessionEventKind.AgentActionProduced,
            ActionBody(new ActionBlock.ToolCall(
                new RawToolCall("lookup", "call-1", "{}")
            ), ToolIdentity)
        );
        AssertInvalid(
            SessionEventKind.AgentActionProduced,
            ReplaceOnce(
                toolCallAction,
                "\"rawArgumentsJson\":\"{}\"",
                "\"rawArgumentsJson\":\"{}\",\"content\":\"cross-kind\""
            )
        );

        string reasoningAction = Encode(
            SessionEventKind.AgentActionProduced,
            ActionBody(new ActionBlock.TextReasoningBlock(
                "think",
                new CompletionDescriptor("provider-A", "api-A", "model-A")
            ))
        );
        AssertInvalid(
            SessionEventKind.AgentActionProduced,
            ReplaceOnce(
                reasoningAction,
                "\"payload\":\"dGhpbms=\"",
                "\"payload\":\"dGhpbms=\",\"unexpected\":true"
            )
        );
        AssertInvalid(
            SessionEventKind.AgentActionProduced,
            ReplaceOnce(
                reasoningAction,
                "\"codecId\":\"atelia.reasoning.text.v1\"",
                "\"codecId\":\"atelia.reasoning.text.v1\",\"codecId\":\"duplicate\""
            )
        );

        string toolResult = Encode(
            SessionEventKind.ToolResultObserved,
            new ToolResultObservedBody(
                "call-1",
                "lookup",
                1,
                ToolExecutionStatus.Success,
                [new ToolResultBlock.Text("done")]
            )
        );
        AssertInvalid(
            SessionEventKind.ToolResultObserved,
            ReplaceOnce(
                toolResult,
                "\"kind\":\"text\",\"content\":\"done\"",
                "\"kind\":\"text\",\"content\":\"done\",\"unexpected\":true"
            )
        );
        AssertInvalid(
            SessionEventKind.ToolResultObserved,
            ReplaceOnce(
                toolResult,
                "\"kind\":\"text\",\"content\":\"done\"",
                "\"kind\":\"text\",\"content\":\"done\",\"content\":\"again\""
            )
        );
    }

    [Fact]
    public void DecodeRejectsEncodeDomainViolationsAsInvalidData() {
        AssertInvalid(
            SessionEventKind.RuntimeConfigSetup,
            """{"v":2,"body":{"modelId":" ","completionSurfaceId":"surface-A","schema":"atelia.session-journal.v1","derivedContext":{"nthPrevious":0}}}"""
        );
        AssertInvalid(
            SessionEventKind.ObservationAccepted,
            """{"v":1,"body":{"content":"  "}}"""
        );
        AssertInvalid(
            SessionEventKind.ToolExecutionStarted,
            """{"v":1,"body":{"toolCallId":"call-1","toolName":"lookup","rawArgumentsJson":"{}","operationId":" ","executionSequence":1,"toolRuntimeIdentity":{"hostId":"host-A","implementationSetFingerprint":"implementation-A","capabilitySetFingerprint":"capability-A"}}}"""
        );
        AssertInvalid(
            SessionEventKind.ToolResultObserved,
            """{"v":1,"body":{"toolCallId":" ","toolName":"lookup","executionSequence":1,"status":"success","blocks":[]}}"""
        );
        AssertInvalid(
            SessionEventKind.AgentActionProduced,
            """{"v":1,"body":{"action":[{"kind":"text","content":"ok"}],"invocation":{"providerId":" ","apiSpecId":"api-A","model":"model-A"},"correlationId":"correlation-A","execution":{"lastIssuedToolExecutionSequence":0},"toolRuntimeIdentity":null}}"""
        );
        AssertInvalid(
            SessionEventKind.AgentActionProduced,
            """{"v":1,"body":{"action":[{"kind":"reasoning","reasoning":{"codecId":" ","originProviderId":"provider-A","originApiSpecId":"api-A","originModel":"model-A","payload":"AA=="}}],"invocation":{"providerId":"provider-A","apiSpecId":"api-A","model":"model-A"},"correlationId":"correlation-A","execution":{"lastIssuedToolExecutionSequence":0},"toolRuntimeIdentity":null}}"""
        );
        AssertInvalid(
            SessionEventKind.ObservationAccepted,
            "{"
        );
    }

    private static IEnumerable<(SessionEventKind, object, string)> Bodies() {
        yield return (
            SessionEventKind.RuntimeConfigSetup,
            new SessionRuntimeConfiguration(
                "model-A",
                "surface-A",
                SessionJournalDefaults.Schema,
                new SessionDerivedContextConfiguration(0)
            ),
            "\"modelId\":\"duplicate\""
        );
        yield return (
            SessionEventKind.SystemPromptSetup,
            new SystemPromptSetupBody("system"),
            "\"content\":\"duplicate\""
        );
        yield return (
            SessionEventKind.SessionCreated,
            new SessionCreatedBody(SessionCreationOrigin.Native),
            "\"origin\":\"native\""
        );
        yield return (
            SessionEventKind.ObservationAccepted,
            new ObservationAcceptedBody("observation"),
            "\"content\":\"duplicate\""
        );
        yield return (
            SessionEventKind.AgentActionProduced,
            ActionBody(new ActionBlock.Text("action")),
            "\"correlationId\":\"duplicate\""
        );
        yield return (
            SessionEventKind.ImportedAgentAction,
            ActionBody(new ActionBlock.Text("imported")),
            "\"correlationId\":\"duplicate\""
        );
        yield return (
            SessionEventKind.ToolExecutionStarted,
            new ToolExecutionStartedBody(
                "call-1",
                "lookup",
                "{}",
                "operation-1",
                1,
                ToolIdentity
            ),
            "\"toolCallId\":\"duplicate\""
        );
        yield return (
            SessionEventKind.ToolResultObserved,
            new ToolResultObservedBody(
                "call-1",
                "lookup",
                1,
                ToolExecutionStatus.Success,
                [new ToolResultBlock.Text("done")]
            ),
            "\"toolCallId\":\"duplicate\""
        );
        yield return (
            SessionEventKind.CompletionAttemptFailed,
            new CompletionAttemptFailedBody(
                CompletionTerminationKind.Failed,
                null,
                null,
                []
            ),
            "\"terminationKind\":\"failed\""
        );
    }

    private static AgentActionProducedBody ActionBody(
        ActionBlock block,
        SessionToolRuntimeIdentity? toolIdentity = null
    ) => new(
        new ActionMessage([block]),
        new CompletionDescriptor("provider-A", "api-A", "model-A"),
        "correlation-A",
        new SessionExecutionCheckpoint(0),
        toolIdentity
    );

    private static string Encode(SessionEventKind kind, object body) =>
        Encoding.UTF8.GetString(SessionEventCodec.Encode(kind, body));

    private static void AssertInvalid(SessionEventKind kind, string json) {
        Assert.Throws<InvalidDataException>(() => SessionEventCodec.Decode(
            kind,
            Encoding.UTF8.GetBytes(json),
            out _
        ));
    }

    private static string ReplaceOnce(
        string source,
        string marker,
        string replacement
    ) {
        int index = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Missing mutation marker '{marker}'.");
        return string.Concat(
            source.AsSpan(0, index),
            replacement,
            source.AsSpan(index + marker.Length)
        );
    }
}
