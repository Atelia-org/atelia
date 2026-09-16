using System.Buffers;
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
        int bodySchemaVersion = kind switch {
            SessionEventKind.CompletionRequestPrepared when body is CompletionRequestPreparedBody { Commitment: not null }
                => SessionRequestManifestDefaults.LegacyBodySchemaVersionV8,
            SessionEventKind.CompletionAttemptStarted when body is CompletionAttemptStartedBody { Commitment: null } => 1,
            _ => GetExpectedBodySchemaVersion(kind)
        };
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
            SessionEventKind.CompletionAttemptStarted => EncodeCompletionAttemptStarted((CompletionAttemptStartedBody)body, bodySchemaVersion),
            SessionEventKind.TurnEnded => EncodeTurnEnded((TurnEndedBody)body, bodySchemaVersion),
            _ => throw new NotSupportedException($"Session event kind '{kind}' is not implemented.")
        };
    }

    public static object Decode(SessionEventKind kind, ReadOnlySpan<byte> payload, out int bodySchemaVersion) {
        int currentBodySchemaVersion = GetExpectedBodySchemaVersion(kind);
        JsonDocument document;
        try {
            document = JsonDocument.Parse(payload.ToArray(), new JsonDocumentOptions { MaxDepth = 128 });
        }
        catch (JsonException exception) {
            throw new InvalidDataException(
                $"Session event '{kind}' payload is not valid JSON.",
                exception
            );
        }
        using (document) {
            JsonElement root = document.RootElement;
            RequireObject(root, "envelope");
            bodySchemaVersion = ReadRequiredInt32(root, "v");
            // Only the new typed input envelope needs additional nesting. Preserve the
            // historical 64-container acceptance limit for all other event contracts.
            if (!(bodySchemaVersion == 2 && kind is (SessionEventKind.SystemPromptSetup or SessionEventKind.ObservationAccepted))) {
                ValidateHistoricalJsonDepth(root);
            }
            bool supportedHistoricalPrepared =
                kind == SessionEventKind.CompletionRequestPrepared
                && bodySchemaVersion is
                    SessionRequestManifestDefaults.HistoricalBodySchemaVersionV5
                    or SessionRequestManifestDefaults.LegacyBodySchemaVersionV7
                    or SessionRequestManifestDefaults.LegacyBodySchemaVersionV8;
            bool supportedHistoricalInput = bodySchemaVersion == 1 && kind is (SessionEventKind.SystemPromptSetup or SessionEventKind.ObservationAccepted or SessionEventKind.CompletionAttemptStarted or SessionEventKind.AgentActionProduced);
            if (bodySchemaVersion != currentBodySchemaVersion
                && !supportedHistoricalPrepared && !supportedHistoricalInput) {
                throw new NotSupportedException(
                    $"Unsupported body schema version for session event kind '{kind}': "
                    + $"actual={bodySchemaVersion}, expected={currentBodySchemaVersion}"
                    + (kind == SessionEventKind.CompletionRequestPrepared
                        ? $", readableHistorical={SessionRequestManifestDefaults.HistoricalBodySchemaVersionV5},{SessionRequestManifestDefaults.LegacyBodySchemaVersionV7},{SessionRequestManifestDefaults.LegacyBodySchemaVersionV8}."
                        : ".")
                );
            }

            RequireExactProperties(root, $"{kind} envelope", "v", "body");
            if (!root.TryGetProperty("body", out JsonElement body)) { throw new InvalidDataException("Session event envelope is missing required property 'body'."); }

            try {
                return kind switch {
                    SessionEventKind.RuntimeConfigSetup => DecodeRuntimeConfiguration(body),
                    SessionEventKind.SystemPromptSetup => DecodeSystemPromptSetup(body, bodySchemaVersion),
                    SessionEventKind.SessionCreated => DecodeSessionCreated(body),
                    SessionEventKind.ObservationAccepted => DecodeObservationAccepted(body, bodySchemaVersion),
                    SessionEventKind.AgentActionProduced => DecodeAgentActionProduced(body, bodySchemaVersion),
                    SessionEventKind.ToolExecutionStarted => DecodeToolExecutionStarted(body),
                    SessionEventKind.ToolResultObserved => DecodeToolResultObserved(body),
                    SessionEventKind.CompletionRequestPrepared => bodySchemaVersion switch {
                        SessionRequestManifestDefaults.CurrentBodySchemaVersion =>
                            SessionRequestManifestCodec.Decode(body, semantic: true),
                        SessionRequestManifestDefaults.LegacyBodySchemaVersionV8 => SessionRequestManifestCodec.Decode(body),
                        SessionRequestManifestDefaults.LegacyBodySchemaVersionV7 =>
                            SessionRequestManifestCodec.Decode(body, legacyTarget: true),
                        SessionRequestManifestDefaults.HistoricalBodySchemaVersionV5 =>
                            SessionRequestManifestV5HistoricalCodec.Decode(body),
                        _ => throw new NotSupportedException(
                            $"Unsupported CompletionRequestPrepared body schema version '{bodySchemaVersion}'."
                        )
                    },
                    SessionEventKind.CompletionAttemptFailed => DecodeCompletionAttemptFailed(body),
                    SessionEventKind.ImportedAgentAction => DecodeAgentActionProduced(body, bodySchemaVersion),
                    SessionEventKind.CompletionAttemptStarted => DecodeCompletionAttemptStarted(body, bodySchemaVersion),
                    SessionEventKind.TurnEnded => DecodeTurnEnded(body),
                    _ => throw new NotSupportedException($"Session event kind '{kind}' is not implemented.")
                };
            }
            catch (InvalidDataException) {
                throw;
            }
            catch (NotSupportedException) {
                throw;
            }
            catch (Exception exception) when (exception is ArgumentException
                or FormatException
                or InvalidOperationException
                or KeyNotFoundException) {
                throw new InvalidDataException(
                    $"Session event '{kind}' body violates its semantic contract.",
                    exception
                );
            }
        }
    }

    private static void ValidateHistoricalJsonDepth(JsonElement value, int depth = 0) {
        if (value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) { return; }
        if (++depth > 64) { throw new InvalidDataException("Session event payload exceeds the historical 64-level JSON depth limit."); }
        if (value.ValueKind == JsonValueKind.Object) {
            foreach (JsonProperty property in value.EnumerateObject()) { ValidateHistoricalJsonDepth(property.Value, depth); }
        }
        else {
            foreach (JsonElement item in value.EnumerateArray()) { ValidateHistoricalJsonDepth(item, depth); }
        }
    }

    internal static int GetExpectedBodySchemaVersion(SessionEventKind kind)
        => kind switch {
            SessionEventKind.RuntimeConfigSetup => 2,
            SessionEventKind.SystemPromptSetup => 2,
            SessionEventKind.SessionCreated => 2,
            SessionEventKind.ObservationAccepted => 2,
            SessionEventKind.AgentActionProduced => 2,
            SessionEventKind.ToolExecutionStarted => 1,
            SessionEventKind.ToolResultObserved => 1,
            SessionEventKind.CompletionRequestPrepared =>
                SessionRequestManifestDefaults.CurrentBodySchemaVersion,
            SessionEventKind.CompletionAttemptFailed => 2,
            SessionEventKind.ImportedAgentAction => 1,
            SessionEventKind.CompletionAttemptStarted => 2,
            SessionEventKind.TurnEnded => 1,
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
        ArgumentNullException.ThrowIfNull(body.DerivedContext);
        if (body.DerivedContext.NthPrevious < 0) {
            throw new ArgumentOutOfRangeException(
                nameof(body.DerivedContext.NthPrevious),
                "Derived context nth-previous ordinal cannot be negative."
            );
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) {
            WriteEnvelopeStart(writer, bodySchemaVersion);
            writer.WriteStartObject("body");
            writer.WriteString("modelId", body.ModelId);
            writer.WriteString("completionSurfaceId", body.CompletionSurfaceId);
            writer.WriteString("schema", body.Schema);
            writer.WriteStartObject("derivedContext");
            writer.WriteNumber(
                "nthPrevious",
                body.DerivedContext.NthPrevious
            );
            writer.WriteEndObject();
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
            writer.WritePropertyName("content");
            body.Content.Write(writer);
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
            writer.WriteString(
                "origin",
                body.Origin switch {
                    SessionCreationOrigin.Native => "native",
                    SessionCreationOrigin.LegacyImport => "legacy-import",
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(body),
                        body.Origin,
                        "Unknown session creation origin."
                    )
                }
            );
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
        ArgumentNullException.ThrowIfNull(body.Content);
        if (!body.Content.IsStructured) { ValidateRequired(body.Content.TextValue, nameof(body.Content)); }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) {
            WriteEnvelopeStart(writer, bodySchemaVersion);
            writer.WriteStartObject("body");
            writer.WritePropertyName("content");
            body.Content.Write(writer);
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

    private static byte[] EncodeTurnEnded(TurnEndedBody body, int bodySchemaVersion) {
        if (!Enum.IsDefined(body.Reason)) { throw new InvalidDataException("Unknown turn end reason."); }
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) {
            WriteEnvelopeStart(writer, bodySchemaVersion);
            writer.WriteStartObject("body");
            writer.WriteString("reason", body.Reason.ToString());
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return buffer.WrittenMemory.ToArray();
    }

    private static TurnEndedBody DecodeTurnEnded(JsonElement body) {
        RequireExactProperties(body, "TurnEnded body", "reason");
        string reason = ReadRequiredString(body, "reason");
        return new TurnEndedBody(reason switch {
            "Stopped" => SessionTurnEndReason.Stopped,
            "Rejected" => SessionTurnEndReason.Rejected,
            "Incomplete" => SessionTurnEndReason.Incomplete,
            _ => throw new InvalidDataException("Unknown turn end reason.")
        });
    }

    private static byte[] EncodeCompletionAttemptStarted(CompletionAttemptStartedBody body, int bodySchemaVersion) {
        ArgumentNullException.ThrowIfNull(body);
        if (bodySchemaVersion == 1 && body.CanonicalRequestCodecId is not null) {
            throw new InvalidDataException("Legacy Started cannot carry partial evidence.");
        }
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) {
            WriteEnvelopeStart(writer, bodySchemaVersion);
            writer.WriteStartObject("body");
            if (bodySchemaVersion == 2) {
                ValidateStartedEvidence(body);
                writer.WriteString("canonicalRequestCodecId", body.CanonicalRequestCodecId);
                writer.WriteNumber("byteLength", body.Commitment!.ByteLength);
                writer.WriteString("sha256", body.Commitment.Sha256);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return buffer.WrittenMemory.ToArray();
    }

    internal static void ValidateStartedEvidence(CompletionAttemptStartedBody body) {
        if (body.CanonicalRequestCodecId != SessionRequestManifestDefaults.CanonicalRequestCodecId
            || body.Commitment is not { ByteLength: > 0 } evidence
            || evidence.Sha256 is not { Length: 64 }
            || evidence.Sha256.Any(static c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))) {
            throw new InvalidDataException("Invalid canonical request evidence on Started.");
        }
    }

    internal static void ValidateStartedForPrepared(int version, CompletionAttemptStartedBody body, SessionPreparedManifestView manifest) {
        if (manifest.BodySchemaVersion == SessionRequestManifestDefaults.CurrentBodySchemaVersion) {
            if (version != 2) { throw new InvalidDataException("Semantic Prepared requires Started v2 evidence."); }
            ValidateStartedEvidence(body);
        }
        else if (version == 2) {
            ValidateStartedEvidence(body);
            if (body.CanonicalRequestCodecId != manifest.Recipe.CanonicalRequestCodecId
                || body.Commitment != manifest.Commitment) {
                throw new InvalidDataException("Started evidence differs from the legacy exact request codec or commitment.");
            }
        }
    }

    private static SessionRuntimeConfiguration DecodeRuntimeConfiguration(JsonElement body) {
        RequireObject(body, "runtime-config-setup body");
        RequireExactProperties(
            body,
            "runtime-config-setup body",
            "modelId",
            "completionSurfaceId",
            "schema",
            "derivedContext"
        );
        if (!body.TryGetProperty(
            "derivedContext",
            out JsonElement derivedContext
        )) {
            throw new InvalidDataException(
                "runtime-config-setup body is missing required property 'derivedContext'."
            );
        }
        RequireObject(
            derivedContext,
            "runtime-config-setup derivedContext"
        );
        RequireExactProperties(
            derivedContext,
            "runtime-config-setup derivedContext",
            "nthPrevious"
        );
        int nthPrevious =
            ReadRequiredInt32(derivedContext, "nthPrevious");
        if (nthPrevious < 0) {
            throw new InvalidDataException(
                "runtime-config-setup derivedContext.nthPrevious cannot be negative."
            );
        }
        var result = new SessionRuntimeConfiguration(
            ReadRequiredString(body, "modelId"),
            ReadRequiredString(body, "completionSurfaceId"),
            ReadRequiredString(body, "schema"),
            new SessionDerivedContextConfiguration(nthPrevious)
        );
        ValidateRequired(result.ModelId, "modelId");
        ValidateRequired(result.CompletionSurfaceId, "completionSurfaceId");
        ValidateRequired(result.Schema, "schema");
        return result;
    }

    private static SystemPromptSetupBody DecodeSystemPromptSetup(JsonElement body, int version) {
        RequireObject(body, "system-prompt-setup body");
        RequireExactProperties(body, "system-prompt-setup body", "content");
        return new SystemPromptSetupBody(version == 1 ? SessionInputContent.Text(ReadRequiredString(body, "content")) : SessionInputContent.Read(body.GetProperty("content")));
    }

    private static SessionCreatedBody DecodeSessionCreated(JsonElement body) {
        RequireObject(body, "session-created body");
        RequireExactProperties(body, "session-created body", "origin");
        return new SessionCreatedBody(
            ReadRequiredString(body, "origin") switch {
                "native" => SessionCreationOrigin.Native,
                "legacy-import" => SessionCreationOrigin.LegacyImport,
                string origin => throw new InvalidDataException(
                    $"Unknown session creation origin '{origin}'."
                )
            }
        );
    }

    private static ObservationAcceptedBody DecodeObservationAccepted(JsonElement body, int version) {
        RequireObject(body, "observation-accepted body");
        RequireExactProperties(body, "observation-accepted body", "content");
        var result = new ObservationAcceptedBody(version == 1 ? SessionInputContent.Text(ReadRequiredString(body, "content")) : SessionInputContent.Read(body.GetProperty("content")));
        if (!result.Content.IsStructured) { ValidateRequired(result.Content.TextValue, "content"); }
        return result;
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
        if (!body.TryGetProperty("action", out JsonElement actionElement) || actionElement.ValueKind != JsonValueKind.Array) { throw new InvalidDataException("agent-action-produced body requires array property 'action'."); }

        var blocks = new List<SerializedActionBlock>();
        foreach (JsonElement blockElement in actionElement.EnumerateArray()) {
            blocks.Add(ReadSerializedActionBlock(blockElement));
        }

        if (!body.TryGetProperty("invocation", out JsonElement invocationElement)) { throw new InvalidDataException("agent-action-produced body requires object property 'invocation'."); }

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
        ValidateRequired(result.ToolCallId, "toolCallId");
        ValidateRequired(result.ToolName, "toolName");
        ValidateRequired(result.RawArgumentsJson, "rawArgumentsJson");
        ValidateRequired(result.OperationId, "operationId");
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
        if (!body.TryGetProperty("blocks", out JsonElement blocksElement) || blocksElement.ValueKind != JsonValueKind.Array) { throw new InvalidDataException("tool-result-observed body requires array property 'blocks'."); }

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
        ValidateRequired(result.ToolCallId, "toolCallId");
        ValidateRequired(result.ToolName, "toolName");
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
            || errorsElement.ValueKind != JsonValueKind.Array) { throw new InvalidDataException("completion-attempt-failed body requires array property 'errors'."); }
        var errors = new List<string>();
        foreach (JsonElement errorElement in errorsElement.EnumerateArray()) {
            if (errorElement.ValueKind != JsonValueKind.String) { throw new InvalidDataException("completion-attempt-failed errors must be strings."); }
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

    private static CompletionAttemptStartedBody DecodeCompletionAttemptStarted(JsonElement body, int version) {
        RequireObject(body, "completion-attempt-started body");
        if (version == 1) {
            if (body.EnumerateObject().Any()) { throw new InvalidDataException("Legacy Started body must be empty."); }
            return new CompletionAttemptStartedBody();
        }
        RequireExactProperties(body, "Started evidence", "canonicalRequestCodecId", "byteLength", "sha256");
        var result = new CompletionAttemptStartedBody(ReadRequiredString(body, "canonicalRequestCodecId"),
            new SessionRequestCommitment(ReadRequiredInt32(body, "byteLength"), ReadRequiredString(body, "sha256")));
        ValidateStartedEvidence(result);
        return result;
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
        if (!element.TryGetProperty(propertyName, out JsonElement property)) { throw new InvalidDataException($"Required property '{propertyName}' is missing."); }
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
        ArgumentNullException.ThrowIfNull(block);
        switch (block.Kind) {
            case ActionMessageSerialization.BlockKindText:
                ArgumentNullException.ThrowIfNull(block.Content);
                break;
            case ActionMessageSerialization.BlockKindToolCall:
                ArgumentNullException.ThrowIfNull(block.ToolName);
                ArgumentNullException.ThrowIfNull(block.ToolCallId);
                ArgumentNullException.ThrowIfNull(block.RawArgumentsJson);
                break;
            case ActionMessageSerialization.BlockKindReasoning:
                SerializedReasoningBlock reasoning = block.Reasoning
                    ?? throw new ArgumentException(
                        "A reasoning action block requires its serialized reasoning payload.",
                        nameof(block)
                    );
                ValidateRequired(reasoning.CodecId, "action.reasoning.codecId");
                ValidateRequired(reasoning.OriginProviderId, "action.reasoning.originProviderId");
                ValidateRequired(reasoning.OriginApiSpecId, "action.reasoning.originApiSpecId");
                ValidateRequired(reasoning.OriginModel, "action.reasoning.originModel");
                ArgumentNullException.ThrowIfNull(reasoning.Payload);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported serialized action block kind '{block.Kind}'."
                );
        }
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
            // Stable v1 wire name; the public CLR view is ReasoningBlock.PlainText.
            if (block.Reasoning.PlainText is not null) { writer.WriteString("plainTextForDebug", block.Reasoning.PlainText); }
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    private static SerializedActionBlock ReadSerializedActionBlock(JsonElement block) {
        RequireObject(block, "action block");
        string kind = ReadRequiredString(block, "kind");
        switch (kind) {
            case ActionMessageSerialization.BlockKindText:
                RequireExactProperties(block, "text action block", "kind", "content");
                break;
            case ActionMessageSerialization.BlockKindToolCall:
                RequireExactProperties(
                    block,
                    "tool-call action block",
                    "kind",
                    "toolName",
                    "toolCallId",
                    "rawArgumentsJson"
                );
                break;
            case ActionMessageSerialization.BlockKindReasoning:
                RequireExactProperties(block, "reasoning action block", "kind", "reasoning");
                break;
            default:
                throw new InvalidDataException(
                    $"Unsupported serialized action block kind '{kind}'."
                );
        }
        SerializedReasoningBlock? reasoning = null;
        if (block.TryGetProperty("reasoning", out JsonElement reasoningElement)) {
            RequireObject(reasoningElement, "reasoning");
            RequireExactProperties(
                reasoningElement,
                "reasoning",
                "codecId",
                "originProviderId",
                "originApiSpecId",
                "originModel",
                "payload",
                "plainTextForDebug"
            );
            reasoning = new SerializedReasoningBlock(
                ReadRequiredString(reasoningElement, "codecId"),
                ReadRequiredString(reasoningElement, "originProviderId"),
                ReadRequiredString(reasoningElement, "originApiSpecId"),
                ReadRequiredString(reasoningElement, "originModel"),
                ReadRequiredBytes(reasoningElement, "payload"),
                ReadOptionalString(reasoningElement, "plainTextForDebug")
            );
            ValidateRequired(reasoning.CodecId, "reasoning.codecId");
            ValidateRequired(reasoning.OriginProviderId, "reasoning.originProviderId");
            ValidateRequired(reasoning.OriginApiSpecId, "reasoning.originApiSpecId");
            ValidateRequired(reasoning.OriginModel, "reasoning.originModel");
        }

        return new SerializedActionBlock(
            kind,
            ReadOptionalString(block, "content"),
            ReadOptionalString(block, "toolName"),
            ReadOptionalString(block, "toolCallId"),
            ReadOptionalString(block, "rawArgumentsJson"),
            reasoning
        );
    }

    private static void WriteToolResultBlock(Utf8JsonWriter writer, ToolResultBlock block) {
        ArgumentNullException.ThrowIfNull(block);
        writer.WriteStartObject();
        switch (block) {
            case ToolResultBlock.Text text:
                ArgumentNullException.ThrowIfNull(text.Content);
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
            ToolResultBlockKindText => ReadTextToolResultBlock(block),
            _ => throw new InvalidDataException($"Unsupported tool result block kind '{kind}'.")
        };
    }

    private static ToolResultBlock ReadTextToolResultBlock(JsonElement block) {
        RequireExactProperties(block, "text tool result block", "kind", "content");
        return new ToolResultBlock.Text(ReadRequiredString(block, "content"));
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
        if (kind is not (CompletionTerminationKind.Incomplete or CompletionTerminationKind.Failed)) { throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only known incomplete/failed outcomes are durable failure events."); }
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string propertyName, string? value) {
        if (value is null) { writer.WriteNull(propertyName); }
        else { writer.WriteString(propertyName, value); }
    }

    private static void RequireObject(JsonElement element, string name) {
        if (element.ValueKind != JsonValueKind.Object) { throw new InvalidDataException($"Expected {name} to be a JSON object."); }
    }

    private static void RequireExactProperties(JsonElement element, string name, params string[] allowedProperties) {
        RequireObject(element, name);
        var allowed = new HashSet<string>(allowedProperties, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject()) {
            if (!seen.Add(property.Name)) { throw new InvalidDataException($"{name} contains duplicate property '{property.Name}'."); }
            if (!allowed.Contains(property.Name)) { throw new InvalidDataException($"{name} contains unknown property '{property.Name}'."); }
        }
    }

    private static int ReadRequiredInt32(JsonElement element, string propertyName) {
        if (!element.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out int value)) { throw new InvalidDataException($"Required numeric property '{propertyName}' is missing or invalid."); }
        return value;
    }

    private static long ReadRequiredInt64(JsonElement element, string propertyName) {
        if (!element.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt64(out long value)) { throw new InvalidDataException($"Required long numeric property '{propertyName}' is missing or invalid."); }
        return value;
    }

    private static JsonElement ReadRequiredObject(JsonElement element, string propertyName) {
        if (!element.TryGetProperty(propertyName, out JsonElement property)) { throw new InvalidDataException($"Required property '{propertyName}' is missing."); }
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
        if (value <= 0) { throw new ArgumentOutOfRangeException(name, value, "Execution sequence must be greater than zero."); }
    }

    private static void ValidateToolRuntimeIdentity(SessionToolRuntimeIdentity value) {
        ArgumentNullException.ThrowIfNull(value);
        ValidateRequired(value.HostId, "toolRuntimeIdentity.hostId");
        ValidateRequired(value.ImplementationSetFingerprint, "toolRuntimeIdentity.implementationSetFingerprint");
        ValidateRequired(value.CapabilitySetFingerprint, "toolRuntimeIdentity.capabilitySetFingerprint");
    }

    private static string ReadRequiredString(JsonElement element, string propertyName) {
        if (!element.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.String) { throw new InvalidDataException($"Required string property '{propertyName}' is missing or invalid."); }
        return property.GetString()!;
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName) {
        if (!element.TryGetProperty(propertyName, out JsonElement property)) { return null; }
        if (property.ValueKind == JsonValueKind.Null) { return null; }
        if (property.ValueKind != JsonValueKind.String) { throw new InvalidDataException($"Optional string property '{propertyName}' is invalid."); }
        return property.GetString();
    }

    private static string? ReadRequiredNullableString(JsonElement element, string propertyName) {
        if (!element.TryGetProperty(propertyName, out JsonElement property)) { throw new InvalidDataException($"Required nullable string property '{propertyName}' is missing."); }
        if (property.ValueKind == JsonValueKind.Null) { return null; }
        if (property.ValueKind != JsonValueKind.String) { throw new InvalidDataException($"Required nullable string property '{propertyName}' is invalid."); }
        return property.GetString();
    }

    private static byte[] ReadRequiredBytes(JsonElement element, string propertyName) {
        if (!element.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.String) { throw new InvalidDataException($"Required base64 property '{propertyName}' is missing or invalid."); }
        return property.GetBytesFromBase64();
    }

    private static void ValidateRequired(string value, string name) {
        if (string.IsNullOrWhiteSpace(value)) { throw new ArgumentException("Value must not be null, empty, or whitespace.", name); }
    }

    public static string ToUtf8String(byte[] payload) => Encoding.UTF8.GetString(payload);
}
