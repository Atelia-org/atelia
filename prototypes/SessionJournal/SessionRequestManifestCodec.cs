using System.Buffers;
using System.Collections.Immutable;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;

namespace Atelia.SessionJournal;

internal static class SessionRequestManifestCodec {
    public static byte[] Encode(CompletionRequestPreparedBody body) {
        Validate(body);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, SessionRequestCanonicalizer.WriterOptions)) {
            writer.WriteStartObject();
            WriteAttempt(writer, body.Attempt);
            WriteExecution(writer, body.Execution);
            WritePlan(writer, body.Plan);
            WriteSetups(writer, body.Setups);
            WriteParameters(writer, body.Parameters);
            WriteToolSet(writer, body.ToolSet);
            WriteRendering(writer, body.Rendering);
            WriteTarget(writer, body.Target);
            WriteCommitment(writer, body.Commitment);
            writer.WriteEndObject();
        }
        return buffer.WrittenMemory.ToArray();
    }

    public static CompletionRequestPreparedBody Decode(JsonElement body) {
        RequireExactProperties(
            body,
            "completion-request-prepared body",
            "attempt",
            "execution",
            "plan",
            "setups",
            "parameters",
            "toolSet",
            "rendering",
            "target",
            "commitment"
        );
        var result = new CompletionRequestPreparedBody(
            ReadAttempt(ReadRequiredObject(body, "attempt")),
            ReadExecution(ReadRequiredObject(body, "execution")),
            ReadPlan(ReadRequiredObject(body, "plan")),
            ReadSetups(ReadRequiredObject(body, "setups")),
            ReadParameters(ReadRequiredObject(body, "parameters")),
            ReadToolSet(ReadRequiredObject(body, "toolSet")),
            ReadRendering(ReadRequiredObject(body, "rendering")),
            ReadTarget(ReadRequiredObject(body, "target")),
            ReadCommitment(ReadRequiredObject(body, "commitment"))
        );
        Validate(result);
        return result;
    }

    public static void Validate(CompletionRequestPreparedBody body) {
        ArgumentNullException.ThrowIfNull(body);
        RequireText(body.Attempt.AttemptId, "attempt.attemptId");
        RequireText(body.Attempt.CorrelationId, "attempt.correlationId");
        RequireText(body.Attempt.Reason, "attempt.reason");
        if (body.Attempt.ReplacesAttemptId is not null) {
            throw new InvalidDataException(
                "completion-request-prepared attempt.replacesAttemptId must be null; retries are represented by completion-attempt-restarted."
            );
        }
        if (body.Execution.LastIssuedToolExecutionSequence < 0) {
            throw new ArgumentOutOfRangeException(
                nameof(body),
                "execution.lastIssuedToolExecutionSequence cannot be negative."
            );
        }

        RequireText(body.Plan.SelectionPolicyId, "plan.selectionPolicyId");
        RequireText(body.Plan.PlannerFingerprint, "plan.plannerFingerprint");
        if (body.Plan.RawStartExclusive is EventAddress rawStartExclusive) {
            _ = EventAddressTextCodec.Format(rawStartExclusive);
        }
        RequireSha256(body.Plan.RawRangeSha256, "plan.rawRangeSha256");
        RequireText(body.Plan.RenderingProfileId, "plan.renderingProfileId");
        RequireText(body.Plan.ModelProfileId, "plan.modelProfileId");
        RequireText(body.Plan.Reason, "plan.reason");
        if (body.Plan.EstimatedInputTokens < 0) {
            throw new ArgumentOutOfRangeException(nameof(body), "plan.estimatedInputTokens cannot be negative.");
        }
        if (!string.Equals(body.Attempt.Reason, body.Plan.Reason, StringComparison.Ordinal)) {
            throw new InvalidDataException("attempt.reason must match plan.reason.");
        }
        if (!string.Equals(body.Plan.ModelProfileId, body.Parameters.ModelId, StringComparison.Ordinal)) {
            throw new InvalidDataException("plan.modelProfileId must match parameters.modelId.");
        }
        ValidatePlanPolicy(body);

        ValidateSetup(body.Setups.RuntimeConfig, "setups.runtimeConfig");
        ValidateSetup(body.Setups.SystemPrompt, "setups.systemPrompt");

        RequireText(body.Parameters.ModelId, "parameters.modelId");
        if (body.Parameters.MaxTokens is <= 0) {
            throw new ArgumentOutOfRangeException(nameof(body), "parameters.maxTokens must be positive when present.");
        }

        if (!string.Equals(body.ToolSet.CodecId, SessionRequestManifestDefaults.ToolCodecId, StringComparison.Ordinal)) {
            throw new NotSupportedException($"Unsupported tool set codec '{body.ToolSet.CodecId}'.");
        }
        RequireSha256(body.ToolSet.Sha256, "toolSet.sha256");
        string actualToolHash = SessionRequestCanonicalizer.ComputeToolSetSha256(body.ToolSet.Definitions);
        if (!string.Equals(body.ToolSet.Sha256, actualToolHash, StringComparison.Ordinal)) {
            throw new InvalidDataException("toolSet.sha256 does not match the inline canonical tool definitions.");
        }
        if (body.ToolSet.Definitions.IsEmpty) {
            if (body.ToolSet.RuntimeIdentity is not null) {
                throw new InvalidDataException("An empty tool set must not pin a tool runtime identity.");
            }
        }
        else {
            ValidateToolRuntimeIdentity(
                body.ToolSet.RuntimeIdentity
                    ?? throw new InvalidDataException("A non-empty tool set requires a tool runtime identity."),
                "toolSet.runtimeIdentity"
            );
        }

        RequireText(body.Rendering.ContextRendererId, "rendering.contextRendererId");
        RequireText(body.Rendering.ContextRendererFingerprint, "rendering.contextRendererFingerprint");
        if (!string.Equals(
                body.Rendering.ReasoningCodecSetFingerprint,
                SessionRequestManifestDefaults.ReasoningCodecSetFingerprint,
                StringComparison.Ordinal
            )) {
            throw new NotSupportedException(
                $"Unsupported reasoning codec set fingerprint '{body.Rendering.ReasoningCodecSetFingerprint}'."
            );
        }
        if (!string.Equals(
                body.Rendering.CanonicalRequestCodecId,
                SessionRequestManifestDefaults.CanonicalRequestCodecId,
                StringComparison.Ordinal
            )) {
            throw new NotSupportedException($"Unsupported canonical request codec '{body.Rendering.CanonicalRequestCodecId}'.");
        }
        if (!string.Equals(body.Rendering.ToolCodecId, body.ToolSet.CodecId, StringComparison.Ordinal)) {
            throw new InvalidDataException("rendering.toolCodecId must match toolSet.codecId.");
        }
        RequireText(body.Rendering.ReasoningCodecSetFingerprint, "rendering.reasoningCodecSetFingerprint");

        ValidateConnection(body.Target.Connection);
        RequireText(body.Target.CompletionSurfaceId, "target.completionSurfaceId");
        RequireText(body.Target.ClientName, "target.clientName");
        RequireText(body.Target.ApiSpecId, "target.apiSpecId");

        if (!string.Equals(
                body.Commitment.Algorithm,
                SessionRequestManifestDefaults.CommitmentAlgorithm,
                StringComparison.Ordinal
            )) {
            throw new NotSupportedException($"Unsupported request commitment algorithm '{body.Commitment.Algorithm}'.");
        }
        if (body.Commitment.ByteLength <= 0) {
            throw new ArgumentOutOfRangeException(nameof(body), "commitment.byteLength must be positive.");
        }
        RequireSha256(body.Commitment.Sha256, "commitment.sha256");
    }

    private static void ValidatePlanPolicy(CompletionRequestPreparedBody body) {
        switch (body.Plan.SelectionPolicyId) {
            case SessionRequestManifestDefaults.FullRawSelectionPolicyId:
                ValidatePolicyIdentities(
                    body,
                    SessionRequestManifestDefaults.FullRawPlannerFingerprint,
                    SessionRequestManifestDefaults.FullRawRenderingProfileId,
                    SessionRequestManifestDefaults.FullRawContextRendererId,
                    SessionRequestManifestDefaults.FullRawContextRendererFingerprint
                );
                if (body.Plan.RawStartExclusive is not null) {
                    throw new InvalidDataException("full-raw plans require plan.rawStartExclusive to be null.");
                }
                if (!body.Plan.ArtifactInputs.IsEmpty || !body.Plan.RecalledInputs.IsEmpty) {
                    throw new InvalidDataException(
                        "full-raw plans require plan.artifactInputs and plan.recalledInputs to be empty."
                    );
                }
                if (body.Plan.ActiveArtifactSet is not null) {
                    throw new InvalidDataException("full-raw plans require plan.activeArtifactSet to be null.");
                }
                break;

            case SessionRequestManifestDefaults.ExplicitArtifactTailSelectionPolicyId:
                ValidatePolicyIdentities(
                    body,
                    SessionRequestManifestDefaults.ExplicitArtifactTailPlannerFingerprint,
                    SessionRequestManifestDefaults.ExplicitArtifactTailRenderingProfileId,
                    SessionRequestManifestDefaults.ExplicitArtifactTailContextRendererId,
                    SessionRequestManifestDefaults.ExplicitArtifactTailContextRendererFingerprint
                );
                if (body.Plan.RawStartExclusive is null) {
                    throw new InvalidDataException("explicit-artifact-tail plans require plan.rawStartExclusive.");
                }
                if (body.Plan.ArtifactInputs.Length != 1) {
                    throw new InvalidDataException(
                        "explicit-artifact-tail plans require exactly one plan.artifactInputs entry."
                    );
                }
                if (!body.Plan.RecalledInputs.IsEmpty) {
                    throw new InvalidDataException(
                        "explicit-artifact-tail plans require plan.recalledInputs to be empty."
                    );
                }
                if (!body.ToolSet.Definitions.IsEmpty) {
                    throw new InvalidDataException(
                        "explicit-artifact-tail plans require an empty tool definition set."
                    );
                }
                if (body.Plan.ActiveArtifactSet is not null) {
                    throw new InvalidDataException(
                        "legacy explicit-artifact-tail plans require plan.activeArtifactSet to be null."
                    );
                }
                ValidateArtifactInput(body.Plan.ArtifactInputs[0]);
                break;

            case SessionRequestManifestDefaults.CoherentArtifactTailSelectionPolicyId:
                ValidatePolicyIdentities(
                    body,
                    SessionRequestManifestDefaults.CoherentArtifactTailPlannerFingerprint,
                    SessionRequestManifestDefaults.CoherentArtifactTailRenderingProfileId,
                    SessionRequestManifestDefaults.CoherentArtifactTailContextRendererId,
                    SessionRequestManifestDefaults.CoherentArtifactTailContextRendererFingerprint
                );
                if (body.Plan.RawStartExclusive is null) {
                    throw new InvalidDataException("coherent-artifact-tail plans require plan.rawStartExclusive.");
                }
                if (body.Plan.ArtifactInputs.Length < 2) {
                    throw new InvalidDataException(
                        "coherent-artifact-tail plans require at least two plan.artifactInputs entries."
                    );
                }
                if (!body.Plan.RecalledInputs.IsEmpty) {
                    throw new InvalidDataException(
                        "coherent-artifact-tail plans require plan.recalledInputs to be empty."
                    );
                }
                ValidateArtifactSetReference(
                    body.Plan.ActiveArtifactSet
                        ?? throw new InvalidDataException(
                            "coherent-artifact-tail plans require plan.activeArtifactSet."
                        )
                );
                var artifactIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (SessionRequestArtifactInput input in body.Plan.ArtifactInputs) {
                    ValidateArtifactInput(input);
                    int populatedCarriers =
                        (string.IsNullOrWhiteSpace(input.ContextSnapshot.SystemPromptFragment) ? 0 : 1)
                        + (string.IsNullOrWhiteSpace(input.ContextSnapshot.ObservationMessage) ? 0 : 1)
                        + (string.IsNullOrWhiteSpace(input.ContextSnapshot.ActionMessage) ? 0 : 1);
                    if (populatedCarriers != 1) {
                        throw new InvalidDataException(
                            "coherent-artifact-tail contributions must populate exactly one contextSnapshot carrier."
                        );
                    }
                    if (!artifactIds.Add(input.ArtifactId)) {
                        throw new InvalidDataException(
                            "coherent-artifact-tail plans require exact artifact ids to be unique."
                        );
                    }
                }
                break;

            default:
                throw new NotSupportedException(
                    $"Unsupported selection policy '{body.Plan.SelectionPolicyId}'."
                );
        }
    }

    private static void ValidatePolicyIdentities(
        CompletionRequestPreparedBody body,
        string plannerFingerprint,
        string renderingProfileId,
        string contextRendererId,
        string contextRendererFingerprint
    ) {
        if (!string.Equals(body.Plan.PlannerFingerprint, plannerFingerprint, StringComparison.Ordinal)
            || !string.Equals(body.Plan.RenderingProfileId, renderingProfileId, StringComparison.Ordinal)
            || !string.Equals(body.Rendering.ContextRendererId, contextRendererId, StringComparison.Ordinal)
            || !string.Equals(
                body.Rendering.ContextRendererFingerprint,
                contextRendererFingerprint,
                StringComparison.Ordinal
            )) {
            throw new NotSupportedException(
                $"Selection policy '{body.Plan.SelectionPolicyId}' contains mismatched planner or rendering identities."
            );
        }
    }

    private static void ValidateArtifactInput(SessionRequestArtifactInput input) {
        ArgumentNullException.ThrowIfNull(input);
        RequireText(input.ArtifactId, "plan.artifactInputs[].artifactId");
        RequireText(input.ArtifactKind, "plan.artifactInputs[].artifactKind");
        RequireSha256(input.ContentSha256, "plan.artifactInputs[].contentSha256");
        SessionArtifactContextSnapshotHasher.ValidateSnapshot(input.ContextSnapshot);
        string actualContentHash = SessionArtifactContextSnapshotHasher.ComputeSha256(input.ContextSnapshot);
        if (!string.Equals(input.ContentSha256, actualContentHash, StringComparison.Ordinal)) {
            throw new InvalidDataException(
                "plan.artifactInputs[].contentSha256 does not match the exact materialized contextSnapshot."
            );
        }
    }

    private static void ValidateArtifactSetReference(
        SessionArtifactSetReference reference
    ) {
        _ = EventAddressTextCodec.Format(reference.Address);
        if (reference.BodySchemaVersion <= 0) {
            throw new InvalidDataException(
                "plan.activeArtifactSet.bodySchemaVersion must be positive."
            );
        }
        RequireSha256(
            reference.PayloadSha256,
            "plan.activeArtifactSet.payloadSha256"
        );
    }

    private static void WriteAttempt(Utf8JsonWriter writer, SessionRequestAttempt value) {
        writer.WriteStartObject("attempt");
        writer.WriteString("attemptId", value.AttemptId);
        writer.WriteString("correlationId", value.CorrelationId);
        writer.WriteString("reason", value.Reason);
        WriteNullableString(writer, "replacesAttemptId", value.ReplacesAttemptId);
        writer.WriteEndObject();
    }

    private static void WriteExecution(Utf8JsonWriter writer, SessionExecutionCheckpoint value) {
        writer.WriteStartObject("execution");
        writer.WriteNumber("lastIssuedToolExecutionSequence", value.LastIssuedToolExecutionSequence);
        writer.WriteEndObject();
    }

    private static void WritePlan(Utf8JsonWriter writer, SessionContextPlan value) {
        writer.WriteStartObject("plan");
        writer.WriteString("selectionPolicyId", value.SelectionPolicyId);
        writer.WriteString("plannerFingerprint", value.PlannerFingerprint);
        WriteNullableAddress(writer, "rawStartExclusive", value.RawStartExclusive);
        writer.WriteString("rawRangeSha256", value.RawRangeSha256);
        writer.WriteStartArray("artifactInputs");
        foreach (SessionRequestArtifactInput input in value.ArtifactInputs) {
            writer.WriteStartObject();
            writer.WriteString("artifactId", input.ArtifactId);
            writer.WriteString("artifactKind", input.ArtifactKind);
            writer.WriteString("contentSha256", input.ContentSha256);
            writer.WriteStartObject("contextSnapshot");
            writer.WriteString("systemPromptFragment", input.ContextSnapshot.SystemPromptFragment);
            writer.WriteString("observationMessage", input.ContextSnapshot.ObservationMessage);
            writer.WriteString("actionMessage", input.ContextSnapshot.ActionMessage);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("recalledInputs");
        foreach (SessionRequestRecalledInput input in value.RecalledInputs) {
            writer.WriteStartObject();
            writer.WriteString("sourceId", input.SourceId);
            writer.WriteString("contentSha256", input.ContentSha256);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        WriteArtifactSetReference(writer, value.ActiveArtifactSet);
        writer.WriteString("renderingProfileId", value.RenderingProfileId);
        writer.WriteString("modelProfileId", value.ModelProfileId);
        writer.WriteNumber("estimatedInputTokens", value.EstimatedInputTokens);
        writer.WriteString("reason", value.Reason);
        writer.WriteEndObject();
    }

    private static void WriteSetups(Utf8JsonWriter writer, SessionGoverningSetupReferences value) {
        writer.WriteStartObject("setups");
        WriteSetup(writer, "runtimeConfig", value.RuntimeConfig);
        WriteSetup(writer, "systemPrompt", value.SystemPrompt);
        writer.WriteEndObject();
    }

    private static void WriteSetup(Utf8JsonWriter writer, string propertyName, SessionSetupReference value) {
        writer.WriteStartObject(propertyName);
        writer.WriteString("address", EventAddressTextCodec.Format(value.Address));
        writer.WriteNumber("bodySchemaVersion", value.BodySchemaVersion);
        writer.WriteString("payloadSha256", value.PayloadSha256);
        writer.WriteEndObject();
    }

    private static void WriteParameters(Utf8JsonWriter writer, SessionRequestParameters value) {
        writer.WriteStartObject("parameters");
        writer.WriteString("modelId", value.ModelId);
        if (value.MaxTokens is int maxTokens) {
            writer.WriteNumber("maxTokens", maxTokens);
        }
        else {
            writer.WriteNull("maxTokens");
        }
        writer.WriteEndObject();
    }

    private static void WriteToolSet(Utf8JsonWriter writer, SessionRequestToolSet value) {
        writer.WriteStartObject("toolSet");
        writer.WriteString("codecId", value.CodecId);
        writer.WriteString("sha256", value.Sha256);
        WriteToolRuntimeIdentity(writer, "runtimeIdentity", value.RuntimeIdentity);
        writer.WriteStartArray("definitions");
        foreach (ToolDefinition definition in value.Definitions) {
            SessionRequestCanonicalizer.WriteToolDefinition(writer, definition);
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteRendering(Utf8JsonWriter writer, SessionRequestRendering value) {
        writer.WriteStartObject("rendering");
        writer.WriteString("contextRendererId", value.ContextRendererId);
        writer.WriteString("contextRendererFingerprint", value.ContextRendererFingerprint);
        writer.WriteString("canonicalRequestCodecId", value.CanonicalRequestCodecId);
        writer.WriteString("toolCodecId", value.ToolCodecId);
        writer.WriteString("reasoningCodecSetFingerprint", value.ReasoningCodecSetFingerprint);
        writer.WriteEndObject();
    }

    private static void WriteTarget(Utf8JsonWriter writer, SessionRequestTarget value) {
        writer.WriteStartObject("target");
        writer.WriteStartObject("connection");
        writer.WriteString("connectionId", value.Connection.ConnectionId);
        writer.WriteString("kind", value.Connection.Kind);
        writer.WriteString("connectionFingerprint", value.Connection.ConnectionFingerprint);
        writer.WriteString("requestAdapterFingerprint", value.Connection.RequestAdapterFingerprint);
        writer.WriteEndObject();
        writer.WriteString("completionSurfaceId", value.CompletionSurfaceId);
        writer.WriteString("clientName", value.ClientName);
        writer.WriteString("apiSpecId", value.ApiSpecId);
        writer.WriteEndObject();
    }

    private static void WriteCommitment(Utf8JsonWriter writer, SessionRequestCommitment value) {
        writer.WriteStartObject("commitment");
        writer.WriteString("algorithm", value.Algorithm);
        writer.WriteNumber("byteLength", value.ByteLength);
        writer.WriteString("sha256", value.Sha256);
        writer.WriteEndObject();
    }

    private static SessionRequestAttempt ReadAttempt(JsonElement element) {
        RequireExactProperties(element, "attempt", "attemptId", "correlationId", "reason", "replacesAttemptId");
        return new(
            ReadRequiredString(element, "attemptId"),
            ReadRequiredString(element, "correlationId"),
            ReadRequiredString(element, "reason"),
            ReadNullableString(element, "replacesAttemptId")
        );
    }

    private static SessionExecutionCheckpoint ReadExecution(JsonElement element) {
        RequireExactProperties(element, "execution", "lastIssuedToolExecutionSequence");
        return new SessionExecutionCheckpoint(
            ReadRequiredInt64(element, "lastIssuedToolExecutionSequence")
        );
    }

    private static SessionContextPlan ReadPlan(JsonElement element) {
        RequireExactProperties(
            element,
            "plan",
            "selectionPolicyId",
            "plannerFingerprint",
            "rawStartExclusive",
            "rawRangeSha256",
            "artifactInputs",
            "recalledInputs",
            "activeArtifactSet",
            "renderingProfileId",
            "modelProfileId",
            "estimatedInputTokens",
            "reason"
        );
        var artifacts = ReadArray(element, "artifactInputs")
            .Select(ReadArtifactInput)
            .ToImmutableArray();
        var recalled = ReadArray(element, "recalledInputs")
            .Select(ReadRecalledInput)
            .ToImmutableArray();
        return new SessionContextPlan(
            ReadRequiredString(element, "selectionPolicyId"),
            ReadRequiredString(element, "plannerFingerprint"),
            ReadNullableAddress(element, "rawStartExclusive"),
            ReadRequiredString(element, "rawRangeSha256"),
            artifacts,
            recalled,
            ReadRequiredString(element, "renderingProfileId"),
            ReadRequiredString(element, "modelProfileId"),
            ReadRequiredInt32(element, "estimatedInputTokens"),
            ReadRequiredString(element, "reason"),
            ReadArtifactSetReference(element)
        );
    }

    private static void WriteArtifactSetReference(
        Utf8JsonWriter writer,
        SessionArtifactSetReference? reference
    ) {
        if (reference is null) {
            writer.WriteNull("activeArtifactSet");
            return;
        }
        writer.WriteStartObject("activeArtifactSet");
        writer.WriteString("address", EventAddressTextCodec.Format(reference.Address));
        writer.WriteNumber("bodySchemaVersion", reference.BodySchemaVersion);
        writer.WriteString("payloadSha256", reference.PayloadSha256);
        writer.WriteEndObject();
    }

    private static SessionArtifactSetReference? ReadArtifactSetReference(
        JsonElement plan
    ) {
        if (!plan.TryGetProperty("activeArtifactSet", out JsonElement element)) {
            throw new InvalidDataException("plan.activeArtifactSet is required.");
        }
        if (element.ValueKind == JsonValueKind.Null) { return null; }
        RequireExactProperties(
            element,
            "active artifact set reference",
            "address",
            "bodySchemaVersion",
            "payloadSha256"
        );
        return new SessionArtifactSetReference(
            EventAddressTextCodec.Parse(ReadRequiredString(element, "address")),
            ReadRequiredInt32(element, "bodySchemaVersion"),
            ReadRequiredString(element, "payloadSha256")
        );
    }

    private static SessionRequestArtifactInput ReadArtifactInput(JsonElement element) {
        RequireExactProperties(
            element,
            "artifact input",
            "artifactId",
            "artifactKind",
            "contentSha256",
            "contextSnapshot"
        );
        return new(
            ReadRequiredString(element, "artifactId"),
            ReadRequiredString(element, "artifactKind"),
            ReadRequiredString(element, "contentSha256"),
            ReadArtifactContextSnapshot(ReadRequiredObject(element, "contextSnapshot"))
        );
    }

    private static SessionRequestArtifactContextSnapshot ReadArtifactContextSnapshot(JsonElement element) {
        RequireExactProperties(
            element,
            "artifact context snapshot",
            "systemPromptFragment",
            "observationMessage",
            "actionMessage"
        );
        return new SessionRequestArtifactContextSnapshot(
            ReadRequiredString(element, "systemPromptFragment"),
            ReadRequiredString(element, "observationMessage"),
            ReadRequiredString(element, "actionMessage")
        );
    }

    private static SessionRequestRecalledInput ReadRecalledInput(JsonElement element) {
        RequireExactProperties(element, "recalled input", "sourceId", "contentSha256");
        return new(
            ReadRequiredString(element, "sourceId"),
            ReadRequiredString(element, "contentSha256")
        );
    }

    private static SessionGoverningSetupReferences ReadSetups(JsonElement element) {
        RequireExactProperties(element, "setups", "runtimeConfig", "systemPrompt");
        return new(
            ReadSetup(ReadRequiredObject(element, "runtimeConfig")),
            ReadSetup(ReadRequiredObject(element, "systemPrompt"))
        );
    }

    private static SessionSetupReference ReadSetup(JsonElement element) {
        RequireExactProperties(element, "setup reference", "address", "bodySchemaVersion", "payloadSha256");
        return new(
            ReadRequiredAddress(element, "address"),
            ReadRequiredInt32(element, "bodySchemaVersion"),
            ReadRequiredString(element, "payloadSha256")
        );
    }

    private static SessionRequestParameters ReadParameters(JsonElement element) {
        RequireExactProperties(element, "parameters", "modelId", "maxTokens");
        return new(
            ReadRequiredString(element, "modelId"),
            ReadNullableInt32(element, "maxTokens")
        );
    }

    private static SessionRequestToolSet ReadToolSet(JsonElement element) {
        RequireExactProperties(element, "toolSet", "codecId", "sha256", "runtimeIdentity", "definitions");
        return new(
            ReadRequiredString(element, "codecId"),
            ReadRequiredString(element, "sha256"),
            ReadArray(element, "definitions")
                .Select(SessionRequestCanonicalizer.ReadToolDefinition)
                .ToImmutableArray(),
            ReadToolRuntimeIdentity(element, "runtimeIdentity")
        );
    }

    private static SessionRequestRendering ReadRendering(JsonElement element) {
        RequireExactProperties(
            element,
            "rendering",
            "contextRendererId",
            "contextRendererFingerprint",
            "canonicalRequestCodecId",
            "toolCodecId",
            "reasoningCodecSetFingerprint"
        );
        return new(
            ReadRequiredString(element, "contextRendererId"),
            ReadRequiredString(element, "contextRendererFingerprint"),
            ReadRequiredString(element, "canonicalRequestCodecId"),
            ReadRequiredString(element, "toolCodecId"),
            ReadRequiredString(element, "reasoningCodecSetFingerprint")
        );
    }

    private static SessionRequestTarget ReadTarget(JsonElement element) {
        RequireExactProperties(
            element,
            "target",
            "connection",
            "completionSurfaceId",
            "clientName",
            "apiSpecId"
        );
        JsonElement connection = ReadRequiredObject(element, "connection");
        RequireExactProperties(
            connection,
            "target connection",
            "connectionId",
            "kind",
            "connectionFingerprint",
            "requestAdapterFingerprint"
        );
        return new SessionRequestTarget(
            new SessionCompletionTargetIdentity(
                ReadRequiredString(connection, "connectionId"),
                ReadRequiredString(connection, "kind"),
                ReadRequiredString(connection, "connectionFingerprint"),
                ReadRequiredString(connection, "requestAdapterFingerprint")
            ),
            ReadRequiredString(element, "completionSurfaceId"),
            ReadRequiredString(element, "clientName"),
            ReadRequiredString(element, "apiSpecId")
        );
    }

    private static SessionRequestCommitment ReadCommitment(JsonElement element) {
        RequireExactProperties(element, "commitment", "algorithm", "byteLength", "sha256");
        return new(
            ReadRequiredString(element, "algorithm"),
            ReadRequiredInt32(element, "byteLength"),
            ReadRequiredString(element, "sha256")
        );
    }

    private static void ValidateSetup(SessionSetupReference value, string path) {
        if (value.BodySchemaVersion <= 0) {
            throw new ArgumentOutOfRangeException(nameof(value), $"{path}.bodySchemaVersion must be positive.");
        }
        RequireSha256(value.PayloadSha256, $"{path}.payloadSha256");
        _ = EventAddressTextCodec.Format(value.Address);
    }

    private static void ValidateConnection(SessionCompletionTargetIdentity value) {
        ArgumentNullException.ThrowIfNull(value);
        RequireText(value.ConnectionId, "target.connection.connectionId");
        RequireText(value.Kind, "target.connection.kind");
        RequireText(value.ConnectionFingerprint, "target.connection.connectionFingerprint");
        RequireText(value.RequestAdapterFingerprint, "target.connection.requestAdapterFingerprint");
    }

    private static void ValidateToolRuntimeIdentity(SessionToolRuntimeIdentity value, string path) {
        ArgumentNullException.ThrowIfNull(value);
        RequireText(value.HostId, $"{path}.hostId");
        RequireText(value.ImplementationSetFingerprint, $"{path}.implementationSetFingerprint");
        RequireText(value.CapabilitySetFingerprint, $"{path}.capabilitySetFingerprint");
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
        JsonElement property = ReadRequiredProperty(element, propertyName);
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

    private static void RequireSha256(string value, string path) {
        if (value.Length != 64 || value.Any(static ch => !((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f')))) {
            throw new ArgumentException($"{path} must be a lowercase 64-character SHA-256 hex digest.", nameof(value));
        }
    }

    private static void RequireText(string value, string path) {
        if (string.IsNullOrWhiteSpace(value)) {
            throw new ArgumentException($"{path} cannot be empty.", nameof(value));
        }
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string propertyName, string? value) {
        if (value is null) {
            writer.WriteNull(propertyName);
        }
        else {
            writer.WriteString(propertyName, value);
        }
    }

    private static void WriteNullableAddress(Utf8JsonWriter writer, string propertyName, EventAddress? value) {
        if (value is EventAddress address) {
            writer.WriteString(propertyName, EventAddressTextCodec.Format(address));
        }
        else {
            writer.WriteNull(propertyName);
        }
    }

    private static JsonElement ReadRequiredObject(JsonElement element, string propertyName) {
        JsonElement property = ReadRequiredProperty(element, propertyName);
        RequireObject(property, propertyName);
        return property;
    }

    private static IEnumerable<JsonElement> ReadArray(JsonElement element, string propertyName) {
        JsonElement property = ReadRequiredProperty(element, propertyName);
        if (property.ValueKind != JsonValueKind.Array) {
            throw new InvalidDataException($"Required array property '{propertyName}' is invalid.");
        }
        return property.EnumerateArray().ToArray();
    }

    private static JsonElement ReadRequiredProperty(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out JsonElement property)
            ? property
            : throw new InvalidDataException($"Required property '{propertyName}' is missing.");

    private static string ReadRequiredString(JsonElement element, string propertyName) {
        JsonElement property = ReadRequiredProperty(element, propertyName);
        return property.ValueKind == JsonValueKind.String
            ? property.GetString()!
            : throw new InvalidDataException($"Required string property '{propertyName}' is invalid.");
    }

    private static string? ReadNullableString(JsonElement element, string propertyName) {
        JsonElement property = ReadRequiredProperty(element, propertyName);
        return property.ValueKind switch {
            JsonValueKind.Null => null,
            JsonValueKind.String => property.GetString(),
            _ => throw new InvalidDataException($"Nullable string property '{propertyName}' is invalid.")
        };
    }

    private static int ReadRequiredInt32(JsonElement element, string propertyName) {
        JsonElement property = ReadRequiredProperty(element, propertyName);
        return property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out int value)
            ? value
            : throw new InvalidDataException($"Required integer property '{propertyName}' is invalid.");
    }

    private static long ReadRequiredInt64(JsonElement element, string propertyName) {
        JsonElement property = ReadRequiredProperty(element, propertyName);
        return property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out long value)
            ? value
            : throw new InvalidDataException($"Required long integer property '{propertyName}' is invalid.");
    }

    private static int? ReadNullableInt32(JsonElement element, string propertyName) {
        JsonElement property = ReadRequiredProperty(element, propertyName);
        if (property.ValueKind == JsonValueKind.Null) { return null; }
        return property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out int value)
            ? value
            : throw new InvalidDataException($"Nullable integer property '{propertyName}' is invalid.");
    }

    private static EventAddress ReadRequiredAddress(JsonElement element, string propertyName) {
        string value = ReadRequiredString(element, propertyName);
        try {
            return EventAddressTextCodec.Parse(value);
        }
        catch (FormatException ex) {
            throw new InvalidDataException($"EventAddress property '{propertyName}' is invalid.", ex);
        }
    }

    private static EventAddress? ReadNullableAddress(JsonElement element, string propertyName) {
        string? value = ReadNullableString(element, propertyName);
        if (value is null) { return null; }
        try {
            return EventAddressTextCodec.Parse(value);
        }
        catch (FormatException ex) {
            throw new InvalidDataException($"EventAddress property '{propertyName}' is invalid.", ex);
        }
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
}
