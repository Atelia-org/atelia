using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;

namespace Atelia.SessionJournal;

internal static class SessionEventCodec {
    private const string ToolResultBlockKindText = "text";
    private static readonly JsonWriterOptions WriterOptions = new() {
        Indented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        SkipValidation = false
    };

    public static byte[] Encode(SessionEventKind kind, object body) {
        int bodySchemaVersion = GetExpectedBodySchemaVersion(kind);
        return kind switch {
            SessionEventKind.RuntimeConfigSetup => EncodeRuntimeConfiguration((SessionRuntimeConfiguration)body, bodySchemaVersion),
            SessionEventKind.SystemPromptSetup => EncodeSystemPromptSetup((SystemPromptSetupBody)body, bodySchemaVersion),
            SessionEventKind.SessionCreated => EncodeSessionCreated((SessionCreatedBody)body, bodySchemaVersion),
            SessionEventKind.ObservationAccepted => EncodeObservationAccepted((ObservationAcceptedBody)body, bodySchemaVersion),
            SessionEventKind.AgentActionProduced => EncodeAgentActionProduced((AgentActionProducedBody)body, bodySchemaVersion),
            SessionEventKind.ToolExecutionStarted => EncodeToolExecutionStarted((ToolExecutionStartedBody)body, bodySchemaVersion),
            SessionEventKind.ToolResultObserved => EncodeToolResultObserved((ToolResultObservedBody)body, bodySchemaVersion),
            SessionEventKind.CompletionRequestPrepared => EncodeCompletionRequestPrepared((CompletionRequestPreparedBody)body, bodySchemaVersion),
            SessionEventKind.CompletionAttemptFailed => EncodeCompletionAttemptFailed((CompletionAttemptFailedBody)body, bodySchemaVersion),
            SessionEventKind.ImportedAgentAction => EncodeAgentActionProduced((AgentActionProducedBody)body, bodySchemaVersion),
            SessionEventKind.ArtifactSetCommitted => EncodeArtifactSetCommitted((ArtifactSetCommittedBody)body, bodySchemaVersion),
            SessionEventKind.CompletionAttemptStarted => EncodeCompletionAttemptStarted((CompletionAttemptStartedBody)body, bodySchemaVersion),
            _ => throw new NotSupportedException($"Session event kind '{kind}' is not implemented.")
        };
    }

    public static object Decode(SessionEventKind kind, ReadOnlySpan<byte> payload, out int bodySchemaVersion) {
        int expectedBodySchemaVersion = GetExpectedBodySchemaVersion(kind);
        using var document = JsonDocument.Parse(payload.ToArray());
        JsonElement root = document.RootElement;
        RequireObject(root, "envelope");
        bodySchemaVersion = ReadRequiredInt32(root, "v");
        if (bodySchemaVersion != expectedBodySchemaVersion) {
            throw new NotSupportedException(
                $"Unsupported body schema version for session event kind '{kind}': "
                + $"actual={bodySchemaVersion}, expected={expectedBodySchemaVersion}."
            );
        }

        if (!root.TryGetProperty("body", out JsonElement body)) {
            throw new InvalidDataException("Session event envelope is missing required property 'body'.");
        }
        if (kind is SessionEventKind.AgentActionProduced
            or SessionEventKind.ImportedAgentAction
            or SessionEventKind.ToolExecutionStarted
            or SessionEventKind.ToolResultObserved
            or SessionEventKind.CompletionRequestPrepared
            or SessionEventKind.CompletionAttemptFailed
            or SessionEventKind.CompletionAttemptStarted) {
            // ArtifactSetCommitted is also an exact envelope below.
            RequireExactProperties(root, $"{kind} envelope", "v", "body");
        }
        if (kind == SessionEventKind.ArtifactSetCommitted) {
            RequireExactProperties(root, $"{kind} envelope", "v", "body");
        }

        return kind switch {
            SessionEventKind.RuntimeConfigSetup => DecodeRuntimeConfiguration(body),
            SessionEventKind.SystemPromptSetup => DecodeSystemPromptSetup(body),
            SessionEventKind.SessionCreated => DecodeSessionCreated(body),
            SessionEventKind.ObservationAccepted => DecodeObservationAccepted(body),
            SessionEventKind.AgentActionProduced => DecodeAgentActionProduced(body, bodySchemaVersion),
            SessionEventKind.ToolExecutionStarted => DecodeToolExecutionStarted(body),
            SessionEventKind.ToolResultObserved => DecodeToolResultObserved(body),
            SessionEventKind.CompletionRequestPrepared => SessionRequestManifestCodec.Decode(body),
            SessionEventKind.CompletionAttemptFailed => DecodeCompletionAttemptFailed(body),
            SessionEventKind.ImportedAgentAction => DecodeAgentActionProduced(body, bodySchemaVersion),
            SessionEventKind.ArtifactSetCommitted => DecodeArtifactSetCommitted(body),
            SessionEventKind.CompletionAttemptStarted => DecodeCompletionAttemptStarted(body),
            _ => throw new NotSupportedException($"Session event kind '{kind}' is not implemented.")
        };
    }

    internal static int GetExpectedBodySchemaVersion(SessionEventKind kind)
        => kind switch {
            SessionEventKind.RuntimeConfigSetup => 1,
            SessionEventKind.SystemPromptSetup => 1,
            SessionEventKind.SessionCreated => 1,
            SessionEventKind.ObservationAccepted => 1,
            SessionEventKind.AgentActionProduced => 1,
            SessionEventKind.ToolExecutionStarted => 1,
            SessionEventKind.ToolResultObserved => 1,
            SessionEventKind.CompletionRequestPrepared => 3,
            SessionEventKind.CompletionAttemptFailed => 2,
            SessionEventKind.ImportedAgentAction => 1,
            SessionEventKind.ArtifactSetCommitted => 1,
            SessionEventKind.CompletionAttemptStarted => 1,
            _ => throw new NotSupportedException($"Session event kind '{kind}' is not implemented.")
        };

    private static byte[] EncodeRuntimeConfiguration(
        SessionRuntimeConfiguration body,
        int bodySchemaVersion
    ) {
        ArgumentNullException.ThrowIfNull(body);
        ValidateRequired(body.ModelId, nameof(body.ModelId));
        ValidateRequired(body.CompletionSurfaceId, nameof(body.CompletionSurfaceId));
        ValidateRequired(body.Schema, nameof(body.Schema));

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) {
            WriteEnvelopeStart(writer, bodySchemaVersion);
            writer.WriteStartObject("body");
            writer.WriteString("modelId", body.ModelId);
            writer.WriteString("completionSurfaceId", body.CompletionSurfaceId);
            writer.WriteString("schema", body.Schema);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.WrittenMemory.ToArray();
    }

    private static byte[] EncodeSystemPromptSetup(
        SystemPromptSetupBody body,
        int bodySchemaVersion
    ) {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Content is null) { throw new ArgumentNullException(nameof(body.Content)); }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) {
            WriteEnvelopeStart(writer, bodySchemaVersion);
            writer.WriteStartObject("body");
            writer.WriteString("content", body.Content);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.WrittenMemory.ToArray();
    }

    private static byte[] EncodeSessionCreated(
        SessionCreatedBody body,
        int bodySchemaVersion
    ) {
        ArgumentNullException.ThrowIfNull(body);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) {
            WriteEnvelopeStart(writer, bodySchemaVersion);
            writer.WriteStartObject("body");
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.WrittenMemory.ToArray();
    }

    private static byte[] EncodeObservationAccepted(
        ObservationAcceptedBody body,
        int bodySchemaVersion
    ) {
        ArgumentNullException.ThrowIfNull(body);
        ValidateRequired(body.Content, nameof(body.Content));

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) {
            WriteEnvelopeStart(writer, bodySchemaVersion);
            writer.WriteStartObject("body");
            writer.WriteString("content", body.Content);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.WrittenMemory.ToArray();
    }

    private static byte[] EncodeAgentActionProduced(
        AgentActionProducedBody body,
        int bodySchemaVersion
    ) {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(body.Action);
        ArgumentNullException.ThrowIfNull(body.Invocation);
        ValidateRequired(body.CorrelationId, nameof(body.CorrelationId));
        ValidateExecutionCheckpoint(body.Execution, "agent-action-produced execution");
        if (body.Action.ToolCalls.Count == 0) {
            if (body.ToolRuntimeIdentity is not null) {
                throw new ArgumentException(
                    "A terminal agent action must not pin a tool runtime identity.",
                    nameof(body)
                );
            }
        }
        else {
            ValidateToolRuntimeIdentity(
                body.ToolRuntimeIdentity
                    ?? throw new ArgumentException(
                        "An agent action containing tool calls requires a tool runtime identity.",
                        nameof(body)
                    )
            );
        }

        var blocks = ActionMessageSerialization.ToSerializedBlocks(body.Action.Blocks);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) {
            WriteEnvelopeStart(writer, bodySchemaVersion);
            writer.WriteStartObject("body");
            writer.WriteStartArray("action");
            foreach (var block in blocks) {
                WriteSerializedActionBlock(writer, block);
            }
            writer.WriteEndArray();
            writer.WriteStartObject("invocation");
            writer.WriteString("providerId", body.Invocation.ProviderId);
            writer.WriteString("apiSpecId", body.Invocation.ApiSpecId);
            writer.WriteString("model", body.Invocation.Model);
            writer.WriteEndObject();
            writer.WriteString("correlationId", body.CorrelationId);
            WriteExecutionCheckpoint(writer, "execution", body.Execution);
            WriteToolRuntimeIdentity(writer, "toolRuntimeIdentity", body.ToolRuntimeIdentity);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.WrittenMemory.ToArray();
    }

    private static byte[] EncodeToolExecutionStarted(
        ToolExecutionStartedBody body,
        int bodySchemaVersion
    ) {
        ArgumentNullException.ThrowIfNull(body);
        ValidateRequired(body.ToolCallId, nameof(body.ToolCallId));
        ValidateRequired(body.ToolName, nameof(body.ToolName));
        ValidateRequired(body.RawArgumentsJson, nameof(body.RawArgumentsJson));
        ValidateRequired(body.OperationId, nameof(body.OperationId));
        ValidateExecutionSequence(body.ExecutionSequence, nameof(body.ExecutionSequence));
        ValidateToolRuntimeIdentity(body.ToolRuntimeIdentity);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) {
            WriteEnvelopeStart(writer, bodySchemaVersion);
            writer.WriteStartObject("body");
            writer.WriteString("toolCallId", body.ToolCallId);
            writer.WriteString("toolName", body.ToolName);
            writer.WriteString("rawArgumentsJson", body.RawArgumentsJson);
            writer.WriteString("operationId", body.OperationId);
            writer.WriteNumber("executionSequence", body.ExecutionSequence);
            WriteToolRuntimeIdentity(writer, "toolRuntimeIdentity", body.ToolRuntimeIdentity);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.WrittenMemory.ToArray();
    }

    private static byte[] EncodeToolResultObserved(
        ToolResultObservedBody body,
        int bodySchemaVersion
    ) {
        ArgumentNullException.ThrowIfNull(body);
        ValidateRequired(body.ToolCallId, nameof(body.ToolCallId));
        ValidateRequired(body.ToolName, nameof(body.ToolName));
        ValidateExecutionSequence(body.ExecutionSequence, nameof(body.ExecutionSequence));
        ArgumentNullException.ThrowIfNull(body.Blocks);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) {
            WriteEnvelopeStart(writer, bodySchemaVersion);
            writer.WriteStartObject("body");
            writer.WriteString("toolCallId", body.ToolCallId);
            writer.WriteString("toolName", body.ToolName);
            writer.WriteNumber("executionSequence", body.ExecutionSequence);
            writer.WriteString("status", WriteStatus(body.Status));
            writer.WriteStartArray("blocks");
            foreach (var block in body.Blocks) {
                WriteToolResultBlock(writer, block);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.WrittenMemory.ToArray();
    }

    private static byte[] EncodeCompletionRequestPrepared(
        CompletionRequestPreparedBody body,
        int bodySchemaVersion
    ) {
        byte[] canonicalBody = SessionRequestManifestCodec.Encode(body);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) {
            WriteEnvelopeStart(writer, bodySchemaVersion);
            writer.WritePropertyName("body");
            writer.WriteRawValue(canonicalBody, skipInputValidation: false);
            writer.WriteEndObject();
        }
        return buffer.WrittenMemory.ToArray();
    }

    private static byte[] EncodeCompletionAttemptFailed(
        CompletionAttemptFailedBody body,
        int bodySchemaVersion
    ) {
        ArgumentNullException.ThrowIfNull(body);
        ValidateFailureTerminationKind(body.TerminationKind);
        ArgumentNullException.ThrowIfNull(body.Errors);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) {
            WriteEnvelopeStart(writer, bodySchemaVersion);
            writer.WriteStartObject("body");
            writer.WriteString("terminationKind", WriteFailureTerminationKind(body.TerminationKind));
            WriteNullableString(writer, "providerReason", body.ProviderReason);
            WriteNullableString(writer, "detail", body.Detail);
            writer.WriteStartArray("errors");
            foreach (string error in body.Errors) {
                if (error is null) { throw new ArgumentException("Completion failure errors cannot contain null.", nameof(body)); }
                writer.WriteStringValue(error);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return buffer.WrittenMemory.ToArray();
    }

    private static byte[] EncodeCompletionAttemptStarted(
        CompletionAttemptStartedBody body,
        int bodySchemaVersion
    ) {
        ArgumentNullException.ThrowIfNull(body);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) {
            WriteEnvelopeStart(writer, bodySchemaVersion);
            writer.WriteStartObject("body");
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return buffer.WrittenMemory.ToArray();
    }

    private static byte[] EncodeArtifactSetCommitted(
        ArtifactSetCommittedBody body,
        int bodySchemaVersion
    ) {
        ValidateArtifactSetCommitted(body);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) {
            WriteEnvelopeStart(writer, bodySchemaVersion);
            writer.WriteStartObject("body");
            writer.WriteString("policyId", body.PolicyId);
            writer.WriteString("policyFingerprint", body.PolicyFingerprint);
            writer.WriteString("commonAnchor", EventAddressTextCodec.Format(body.CommonAnchor));
            WriteSetupReferences(writer, "coverageSetups", body.CoverageSetups);
            WriteSetupReferences(writer, "currentSetups", body.CurrentSetups);
            writer.WriteStartArray("members");
            foreach (SessionArtifactSetMember member in body.Members) {
                writer.WriteStartObject();
                writer.WriteString("roleId", member.RoleId);
                writer.WriteString("artifactId", member.ArtifactId);
                writer.WriteString("artifactKind", member.ArtifactKind);
                writer.WriteStartObject("target");
                writer.WriteString(
                    "carrier",
                    MemoryPackCarrierTokens.ToStorageToken(member.Target.Carrier)
                );
                writer.WriteString("blockKey", member.Target.BlockKey);
                writer.WriteEndObject();
                writer.WriteString("contentSha256", member.ContentSha256);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return buffer.WrittenMemory.ToArray();
    }

    private static SessionRuntimeConfiguration DecodeRuntimeConfiguration(JsonElement body) {
        RequireObject(body, "runtime-config-setup body");
        return new SessionRuntimeConfiguration(
            ReadRequiredString(body, "modelId"),
            ReadRequiredString(body, "completionSurfaceId"),
            ReadRequiredString(body, "schema")
        );
    }

    private static SystemPromptSetupBody DecodeSystemPromptSetup(JsonElement body) {
        RequireObject(body, "system-prompt-setup body");
        return new SystemPromptSetupBody(ReadRequiredString(body, "content"));
    }

    private static SessionCreatedBody DecodeSessionCreated(JsonElement body) {
        RequireObject(body, "session-created body");
        if (body.EnumerateObject().Any()) {
            throw new InvalidDataException("session-created body must be empty.");
        }

        return new SessionCreatedBody();
    }

    private static ObservationAcceptedBody DecodeObservationAccepted(JsonElement body) {
        RequireObject(body, "observation-accepted body");
        return new ObservationAcceptedBody(ReadRequiredString(body, "content"));
    }

    private static AgentActionProducedBody DecodeAgentActionProduced(
        JsonElement body,
        int bodySchemaVersion
    ) {
        RequireExactProperties(
            body,
            "agent-action-produced body",
            "action",
            "invocation",
            "correlationId",
            "execution",
            "toolRuntimeIdentity"
        );
        if (!body.TryGetProperty("action", out JsonElement actionElement) || actionElement.ValueKind != JsonValueKind.Array) {
            throw new InvalidDataException("agent-action-produced body requires array property 'action'.");
        }

        var blocks = new List<SerializedActionBlock>();
        foreach (JsonElement blockElement in actionElement.EnumerateArray()) {
            blocks.Add(ReadSerializedActionBlock(blockElement));
        }

        if (!body.TryGetProperty("invocation", out JsonElement invocationElement)) {
            throw new InvalidDataException("agent-action-produced body requires object property 'invocation'.");
        }

        RequireExactProperties(invocationElement, "invocation", "providerId", "apiSpecId", "model");
        var action = new ActionMessage(ActionMessageSerialization.FromSerializedBlocks(blocks));
        var invocation = new CompletionDescriptor(
            ReadRequiredString(invocationElement, "providerId"),
            ReadRequiredString(invocationElement, "apiSpecId"),
            ReadRequiredString(invocationElement, "model")
        );
        var result = new AgentActionProducedBody(
            action,
            invocation,
            ReadRequiredString(body, "correlationId"),
            ReadExecutionCheckpoint(ReadRequiredObject(body, "execution")),
            ReadToolRuntimeIdentity(body, "toolRuntimeIdentity")
        );
        try {
            _ = EncodeAgentActionProduced(result, bodySchemaVersion);
        }
        catch (ArgumentException ex) {
            throw new InvalidDataException("agent-action-produced body is invalid.", ex);
        }
        return result;
    }

    private static ToolExecutionStartedBody DecodeToolExecutionStarted(JsonElement body) {
        RequireExactProperties(
            body,
            "tool-execution-started body",
            "toolCallId",
            "toolName",
            "rawArgumentsJson",
            "operationId",
            "executionSequence",
            "toolRuntimeIdentity"
        );
        var result = new ToolExecutionStartedBody(
            ReadRequiredString(body, "toolCallId"),
            ReadRequiredString(body, "toolName"),
            ReadRequiredString(body, "rawArgumentsJson"),
            ReadRequiredString(body, "operationId"),
            ReadRequiredInt64(body, "executionSequence"),
            ReadToolRuntimeIdentity(body, "toolRuntimeIdentity")
                ?? throw new InvalidDataException(
                    "tool-execution-started body requires toolRuntimeIdentity."
                )
        );
        ValidateExecutionSequence(result.ExecutionSequence, "executionSequence");
        ValidateToolRuntimeIdentity(result.ToolRuntimeIdentity);
        return result;
    }

    private static ToolResultObservedBody DecodeToolResultObserved(JsonElement body) {
        RequireExactProperties(
            body,
            "tool-result-observed body",
            "toolCallId",
            "toolName",
            "executionSequence",
            "status",
            "blocks"
        );
        if (!body.TryGetProperty("blocks", out JsonElement blocksElement) || blocksElement.ValueKind != JsonValueKind.Array) {
            throw new InvalidDataException("tool-result-observed body requires array property 'blocks'.");
        }

        var blocks = new List<ToolResultBlock>();
        foreach (JsonElement blockElement in blocksElement.EnumerateArray()) {
            blocks.Add(ReadToolResultBlock(blockElement));
        }

        var result = new ToolResultObservedBody(
            ReadRequiredString(body, "toolCallId"),
            ReadRequiredString(body, "toolName"),
            ReadRequiredInt64(body, "executionSequence"),
            ReadStatus(ReadRequiredString(body, "status")),
            blocks
        );
        ValidateExecutionSequence(result.ExecutionSequence, "executionSequence");
        return result;
    }

    private static CompletionAttemptFailedBody DecodeCompletionAttemptFailed(JsonElement body) {
        RequireExactProperties(
            body,
            "completion-attempt-failed body",
            "terminationKind",
            "providerReason",
            "detail",
            "errors"
        );
        if (!body.TryGetProperty("errors", out JsonElement errorsElement)
            || errorsElement.ValueKind != JsonValueKind.Array) {
            throw new InvalidDataException("completion-attempt-failed body requires array property 'errors'.");
        }
        var errors = new List<string>();
        foreach (JsonElement errorElement in errorsElement.EnumerateArray()) {
            if (errorElement.ValueKind != JsonValueKind.String) {
                throw new InvalidDataException("completion-attempt-failed errors must be strings.");
            }
            errors.Add(errorElement.GetString()!);
        }

        CompletionTerminationKind terminationKind = ReadFailureTerminationKind(
            ReadRequiredString(body, "terminationKind")
        );
        return new CompletionAttemptFailedBody(
            terminationKind,
            ReadRequiredNullableString(body, "providerReason"),
            ReadRequiredNullableString(body, "detail"),
            Array.AsReadOnly(errors.ToArray())
        );
    }

    private static CompletionAttemptStartedBody DecodeCompletionAttemptStarted(JsonElement body) {
        RequireObject(body, "completion-attempt-started body");
        if (body.EnumerateObject().Any()) {
            throw new InvalidDataException("completion-attempt-started body must be empty.");
        }
        return new CompletionAttemptStartedBody();
    }

    private static ArtifactSetCommittedBody DecodeArtifactSetCommitted(JsonElement body) {
        RequireExactProperties(
            body,
            "artifact-set-committed body",
            "policyId",
            "policyFingerprint",
            "commonAnchor",
            "coverageSetups",
            "currentSetups",
            "members"
        );
        EventAddress commonAnchor = EventAddressTextCodec.Parse(
            ReadRequiredString(body, "commonAnchor")
        );
        JsonElement membersElement = body.GetProperty("members");
        if (membersElement.ValueKind != JsonValueKind.Array) {
            throw new InvalidDataException("artifact-set-committed members must be an array.");
        }
        var members = ImmutableArray.CreateBuilder<SessionArtifactSetMember>();
        foreach (JsonElement member in membersElement.EnumerateArray()) {
            RequireExactProperties(
                member,
                "artifact-set member",
                "roleId",
                "artifactId",
                "artifactKind",
                "target",
                "contentSha256"
            );
            JsonElement target = member.GetProperty("target");
            RequireExactProperties(target, "artifact-set member target", "carrier", "blockKey");
            if (!MemoryPackCarrierTokens.TryParseStorageToken(
                    ReadRequiredString(target, "carrier"),
                    out MemoryPackCarrier carrier
                )) {
                throw new InvalidDataException("artifact-set member target carrier is unknown.");
            }
            members.Add(new SessionArtifactSetMember(
                ReadRequiredString(member, "roleId"),
                ReadRequiredString(member, "artifactId"),
                ReadRequiredString(member, "artifactKind"),
                new MemoryPackBlockPath(
                    carrier,
                    ReadRequiredString(target, "blockKey")
                ),
                ReadRequiredString(member, "contentSha256")
            ));
        }
        var result = new ArtifactSetCommittedBody(
            ReadRequiredString(body, "policyId"),
            ReadRequiredString(body, "policyFingerprint"),
            commonAnchor,
            ReadSetupReferences(body.GetProperty("coverageSetups")),
            ReadSetupReferences(body.GetProperty("currentSetups")),
            members.ToImmutable()
        );
        ValidateArtifactSetCommitted(result);
        return result;
    }

    private static void WriteSetupReferences(
        Utf8JsonWriter writer,
        string propertyName,
        SessionGoverningSetupReferences references
    ) {
        writer.WriteStartObject(propertyName);
        WriteSetupReference(writer, "runtimeConfig", references.RuntimeConfig);
        WriteSetupReference(writer, "systemPrompt", references.SystemPrompt);
        writer.WriteEndObject();
    }

    private static void WriteSetupReference(
        Utf8JsonWriter writer,
        string propertyName,
        SessionSetupReference reference
    ) {
        writer.WriteStartObject(propertyName);
        writer.WriteString("address", EventAddressTextCodec.Format(reference.Address));
        writer.WriteNumber("bodySchemaVersion", reference.BodySchemaVersion);
        writer.WriteString("payloadSha256", reference.PayloadSha256);
        writer.WriteEndObject();
    }

    private static SessionGoverningSetupReferences ReadSetupReferences(
        JsonElement element
    ) {
        RequireExactProperties(
            element,
            "artifact-set setup references",
            "runtimeConfig",
            "systemPrompt"
        );
        return new SessionGoverningSetupReferences(
            ReadSetupReference(element.GetProperty("runtimeConfig")),
            ReadSetupReference(element.GetProperty("systemPrompt"))
        );
    }

    private static SessionSetupReference ReadSetupReference(JsonElement element) {
        RequireExactProperties(
            element,
            "artifact-set setup reference",
            "address",
            "bodySchemaVersion",
            "payloadSha256"
        );
        return new SessionSetupReference(
            EventAddressTextCodec.Parse(ReadRequiredString(element, "address")),
            ReadRequiredInt32(element, "bodySchemaVersion"),
            ReadRequiredString(element, "payloadSha256")
        );
    }

    private static void ValidateArtifactSetCommitted(ArtifactSetCommittedBody body) {
        ArgumentNullException.ThrowIfNull(body);
        ValidateRequired(body.PolicyId, nameof(body.PolicyId));
        ValidateRequired(body.PolicyFingerprint, nameof(body.PolicyFingerprint));
        if (!string.Equals(
                body.PolicyId,
                SessionRequestManifestDefaults.ActiveArtifactSetPolicyId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                body.PolicyFingerprint,
                SessionRequestManifestDefaults.ActiveArtifactSetPolicyFingerprint,
                StringComparison.Ordinal
            )) {
            throw new NotSupportedException(
                $"Unsupported artifact-set policy '{body.PolicyId}'."
            );
        }
        ValidateArtifactSetupReferences(body.CoverageSetups);
        ValidateArtifactSetupReferences(body.CurrentSetups);
        if (body.Members.Length < 2) {
            throw new InvalidDataException("ArtifactSetCommitted requires at least two members.");
        }
        var roles = new HashSet<string>(StringComparer.Ordinal);
        var artifacts = new HashSet<string>(StringComparer.Ordinal);
        var targets = new HashSet<(MemoryPackCarrier Carrier, string BlockKey)>();
        string? priorRole = null;
        foreach (SessionArtifactSetMember member in body.Members) {
            ValidateRequired(member.RoleId, nameof(member.RoleId));
            ValidateRequired(member.ArtifactId, nameof(member.ArtifactId));
            ValidateRequired(member.ArtifactKind, nameof(member.ArtifactKind));
            if (priorRole is not null
                && string.CompareOrdinal(priorRole, member.RoleId) >= 0) {
                throw new InvalidDataException(
                    "ArtifactSetCommitted members must be uniquely ordered by roleId."
                );
            }
            priorRole = member.RoleId;
            if (!roles.Add(member.RoleId)
                || !artifacts.Add(member.ArtifactId)
                || !targets.Add((
                    member.Target.Carrier,
                    member.Target.BlockKey
                ))) {
                throw new InvalidDataException(
                    "ArtifactSetCommitted members require unique roles, artifact ids, and targets."
                );
            }
            if (member.ContentSha256.Length != 64
                || member.ContentSha256.Any(static ch =>
                    ch is not (>= '0' and <= '9')
                    && ch is not (>= 'a' and <= 'f'))) {
                throw new InvalidDataException(
                    "ArtifactSetCommitted member contentSha256 must be lowercase SHA-256."
                );
            }
        }
    }

    private static void ValidateArtifactSetupReferences(
        SessionGoverningSetupReferences references
    ) {
        foreach (SessionSetupReference reference in new[] {
            references.RuntimeConfig,
            references.SystemPrompt
        }) {
            _ = EventAddressTextCodec.Format(reference.Address);
            if (reference.BodySchemaVersion <= 0
                || reference.PayloadSha256.Length != 64
                || reference.PayloadSha256.Any(static ch =>
                    ch is not (>= '0' and <= '9')
                    && ch is not (>= 'a' and <= 'f'))) {
                throw new InvalidDataException(
                    "ArtifactSetCommitted setup reference is invalid."
                );
            }
        }
    }

    private static void WriteEnvelopeStart(
        Utf8JsonWriter writer,
        int bodySchemaVersion
    ) {
        writer.WriteStartObject();
        writer.WriteNumber("v", bodySchemaVersion);
    }

    private static void WriteExecutionCheckpoint(
        Utf8JsonWriter writer,
        string propertyName,
        SessionExecutionCheckpoint value
    ) {
        writer.WriteStartObject(propertyName);
        writer.WriteNumber("lastIssuedToolExecutionSequence", value.LastIssuedToolExecutionSequence);
        writer.WriteEndObject();
    }

    private static SessionExecutionCheckpoint ReadExecutionCheckpoint(JsonElement element) {
        RequireExactProperties(element, "execution checkpoint", "lastIssuedToolExecutionSequence");
        var result = new SessionExecutionCheckpoint(
            ReadRequiredInt64(element, "lastIssuedToolExecutionSequence")
        );
        ValidateExecutionCheckpoint(result, "execution checkpoint");
        return result;
    }

    private static void WriteToolRuntimeIdentity(
        Utf8JsonWriter writer,
        string propertyName,
        SessionToolRuntimeIdentity? value
    ) {
        if (value is null) {
            writer.WriteNull(propertyName);
            return;
        }
        writer.WriteStartObject(propertyName);
        writer.WriteString("hostId", value.HostId);
        writer.WriteString("implementationSetFingerprint", value.ImplementationSetFingerprint);
        writer.WriteString("capabilitySetFingerprint", value.CapabilitySetFingerprint);
        writer.WriteEndObject();
    }

    private static SessionToolRuntimeIdentity? ReadToolRuntimeIdentity(
        JsonElement element,
        string propertyName
    ) {
        if (!element.TryGetProperty(propertyName, out JsonElement property)) {
            throw new InvalidDataException($"Required property '{propertyName}' is missing.");
        }
        if (property.ValueKind == JsonValueKind.Null) { return null; }
        RequireExactProperties(
            property,
            propertyName,
            "hostId",
            "implementationSetFingerprint",
            "capabilitySetFingerprint"
        );
        return new SessionToolRuntimeIdentity(
            ReadRequiredString(property, "hostId"),
            ReadRequiredString(property, "implementationSetFingerprint"),
            ReadRequiredString(property, "capabilitySetFingerprint")
        );
    }

    private static void WriteSerializedActionBlock(Utf8JsonWriter writer, SerializedActionBlock block) {
        writer.WriteStartObject();
        writer.WriteString("kind", block.Kind);
        if (block.Content is not null) { writer.WriteString("content", block.Content); }
        if (block.ToolName is not null) { writer.WriteString("toolName", block.ToolName); }
        if (block.ToolCallId is not null) { writer.WriteString("toolCallId", block.ToolCallId); }
        if (block.RawArgumentsJson is not null) { writer.WriteString("rawArgumentsJson", block.RawArgumentsJson); }
        if (block.Reasoning is not null) {
            writer.WriteStartObject("reasoning");
            writer.WriteString("codecId", block.Reasoning.CodecId);
            writer.WriteString("originProviderId", block.Reasoning.OriginProviderId);
            writer.WriteString("originApiSpecId", block.Reasoning.OriginApiSpecId);
            writer.WriteString("originModel", block.Reasoning.OriginModel);
            writer.WriteBase64String("payload", block.Reasoning.Payload);
            if (block.Reasoning.PlainTextForDebug is not null) { writer.WriteString("plainTextForDebug", block.Reasoning.PlainTextForDebug); }
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    private static SerializedActionBlock ReadSerializedActionBlock(JsonElement block) {
        RequireObject(block, "action block");
        SerializedReasoningBlock? reasoning = null;
        if (block.TryGetProperty("reasoning", out JsonElement reasoningElement)) {
            RequireObject(reasoningElement, "reasoning");
            reasoning = new SerializedReasoningBlock(
                ReadRequiredString(reasoningElement, "codecId"),
                ReadRequiredString(reasoningElement, "originProviderId"),
                ReadRequiredString(reasoningElement, "originApiSpecId"),
                ReadRequiredString(reasoningElement, "originModel"),
                ReadRequiredBytes(reasoningElement, "payload"),
                ReadOptionalString(reasoningElement, "plainTextForDebug")
            );
        }

        return new SerializedActionBlock(
            ReadRequiredString(block, "kind"),
            ReadOptionalString(block, "content"),
            ReadOptionalString(block, "toolName"),
            ReadOptionalString(block, "toolCallId"),
            ReadOptionalString(block, "rawArgumentsJson"),
            reasoning
        );
    }

    private static void WriteToolResultBlock(Utf8JsonWriter writer, ToolResultBlock block) {
        writer.WriteStartObject();
        switch (block) {
            case ToolResultBlock.Text text:
                writer.WriteString("kind", ToolResultBlockKindText);
                writer.WriteString("content", text.Content);
                break;
            default:
                throw new InvalidOperationException($"Unsupported tool result block type '{block.GetType().FullName}'.");
        }
        writer.WriteEndObject();
    }

    private static ToolResultBlock ReadToolResultBlock(JsonElement block) {
        RequireObject(block, "tool result block");
        string kind = ReadRequiredString(block, "kind");
        return kind switch {
            ToolResultBlockKindText => new ToolResultBlock.Text(ReadRequiredString(block, "content")),
            _ => throw new InvalidDataException($"Unsupported tool result block kind '{kind}'.")
        };
    }

    private static string WriteStatus(ToolExecutionStatus status)
        => status switch {
            ToolExecutionStatus.Success => "success",
            ToolExecutionStatus.Failed => "failed",
            ToolExecutionStatus.Skipped => "skipped",
            _ => throw new InvalidOperationException($"Unsupported tool execution status '{status}'.")
        };

    private static ToolExecutionStatus ReadStatus(string value)
        => value switch {
            "success" => ToolExecutionStatus.Success,
            "failed" => ToolExecutionStatus.Failed,
            "skipped" => ToolExecutionStatus.Skipped,
            _ => throw new InvalidDataException($"Unsupported tool execution status '{value}'.")
        };

    private static string WriteFailureTerminationKind(CompletionTerminationKind kind)
        => kind switch {
            CompletionTerminationKind.Incomplete => "incomplete",
            CompletionTerminationKind.Failed => "failed",
            _ => throw new InvalidOperationException($"Unsupported durable completion failure kind '{kind}'.")
        };

    private static CompletionTerminationKind ReadFailureTerminationKind(string value)
        => value switch {
            "incomplete" => CompletionTerminationKind.Incomplete,
            "failed" => CompletionTerminationKind.Failed,
            _ => throw new InvalidDataException($"Unsupported durable completion failure kind '{value}'.")
        };

    private static void ValidateFailureTerminationKind(CompletionTerminationKind kind) {
        if (kind is not (CompletionTerminationKind.Incomplete or CompletionTerminationKind.Failed)) {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only known incomplete/failed outcomes are durable failure events.");
        }
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string propertyName, string? value) {
        if (value is null) { writer.WriteNull(propertyName); }
        else { writer.WriteString(propertyName, value); }
    }

    private static void RequireObject(JsonElement element, string name) {
        if (element.ValueKind != JsonValueKind.Object) {
            throw new InvalidDataException($"Expected {name} to be a JSON object.");
        }
    }

    private static void RequireExactProperties(JsonElement element, string name, params string[] allowedProperties) {
        RequireObject(element, name);
        var allowed = new HashSet<string>(allowedProperties, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject()) {
            if (!seen.Add(property.Name)) {
                throw new InvalidDataException($"{name} contains duplicate property '{property.Name}'.");
            }
            if (!allowed.Contains(property.Name)) {
                throw new InvalidDataException($"{name} contains unknown property '{property.Name}'.");
            }
        }
    }

    private static int ReadRequiredInt32(JsonElement element, string propertyName) {
        if (!element.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out int value)) {
            throw new InvalidDataException($"Required numeric property '{propertyName}' is missing or invalid.");
        }
        return value;
    }

    private static long ReadRequiredInt64(JsonElement element, string propertyName) {
        if (!element.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt64(out long value)) {
            throw new InvalidDataException($"Required long numeric property '{propertyName}' is missing or invalid.");
        }
        return value;
    }

    private static JsonElement ReadRequiredObject(JsonElement element, string propertyName) {
        if (!element.TryGetProperty(propertyName, out JsonElement property)) {
            throw new InvalidDataException($"Required property '{propertyName}' is missing.");
        }
        RequireObject(property, propertyName);
        return property;
    }

    private static void ValidateExecutionCheckpoint(SessionExecutionCheckpoint value, string name) {
        ArgumentNullException.ThrowIfNull(value);
        if (value.LastIssuedToolExecutionSequence < 0) {
            throw new ArgumentOutOfRangeException(
                name,
                value.LastIssuedToolExecutionSequence,
                "Last-issued tool execution sequence cannot be negative."
            );
        }
    }

    private static void ValidateExecutionSequence(long value, string name) {
        if (value <= 0) {
            throw new ArgumentOutOfRangeException(name, value, "Execution sequence must be greater than zero.");
        }
    }

    private static void ValidateToolRuntimeIdentity(SessionToolRuntimeIdentity value) {
        ArgumentNullException.ThrowIfNull(value);
        ValidateRequired(value.HostId, "toolRuntimeIdentity.hostId");
        ValidateRequired(value.ImplementationSetFingerprint, "toolRuntimeIdentity.implementationSetFingerprint");
        ValidateRequired(value.CapabilitySetFingerprint, "toolRuntimeIdentity.capabilitySetFingerprint");
    }

    private static string ReadRequiredString(JsonElement element, string propertyName) {
        if (!element.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.String) {
            throw new InvalidDataException($"Required string property '{propertyName}' is missing or invalid.");
        }
        return property.GetString()!;
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName) {
        if (!element.TryGetProperty(propertyName, out JsonElement property)) { return null; }
        if (property.ValueKind == JsonValueKind.Null) { return null; }
        if (property.ValueKind != JsonValueKind.String) {
            throw new InvalidDataException($"Optional string property '{propertyName}' is invalid.");
        }
        return property.GetString();
    }

    private static string? ReadRequiredNullableString(JsonElement element, string propertyName) {
        if (!element.TryGetProperty(propertyName, out JsonElement property)) {
            throw new InvalidDataException($"Required nullable string property '{propertyName}' is missing.");
        }
        if (property.ValueKind == JsonValueKind.Null) { return null; }
        if (property.ValueKind != JsonValueKind.String) {
            throw new InvalidDataException($"Required nullable string property '{propertyName}' is invalid.");
        }
        return property.GetString();
    }

    private static byte[] ReadRequiredBytes(JsonElement element, string propertyName) {
        if (!element.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.String) {
            throw new InvalidDataException($"Required base64 property '{propertyName}' is missing or invalid.");
        }
        return property.GetBytesFromBase64();
    }

    private static void ValidateRequired(string value, string name) {
        if (string.IsNullOrWhiteSpace(value)) {
            throw new ArgumentException("Value must not be null, empty, or whitespace.", name);
        }
    }

    public static string ToUtf8String(byte[] payload) => Encoding.UTF8.GetString(payload);
}
