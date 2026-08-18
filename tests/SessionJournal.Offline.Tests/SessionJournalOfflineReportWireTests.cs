using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.Offline.Tests;

public sealed class SessionJournalOfflineReportWireTests {
    private static readonly JsonSerializerOptions WebJsonOptions =
        new(JsonSerializerDefaults.Web);

    private static readonly (SessionExecutionPhase Value, string Token)[]
        PhaseTokens = [
            (SessionExecutionPhase.Empty, "empty"),
            (SessionExecutionPhase.Idle, "idle"),
            (SessionExecutionPhase.AwaitingAgentAction,
                "awaiting-agent-action"),
            (SessionExecutionPhase.AwaitingCompletionDispatch,
                "awaiting-completion-dispatch"),
            (SessionExecutionPhase.AwaitingCompletion,
                "awaiting-completion"),
            (SessionExecutionPhase.AwaitingToolExecution,
                "awaiting-tool-execution"),
            (SessionExecutionPhase.TurnFailed, "turn-failed")
        ];

    private static readonly (SessionEventKind Value, string Token)[]
        EventKindTokens = [
            (SessionEventKind.RuntimeConfigSetup,
                "runtime-config-setup"),
            (SessionEventKind.SystemPromptSetup,
                "system-prompt-setup"),
            (SessionEventKind.SessionCreated, "session-created"),
            (SessionEventKind.ObservationAccepted,
                "observation-accepted"),
            (SessionEventKind.AgentActionProduced,
                "agent-action-produced"),
            (SessionEventKind.ToolExecutionStarted,
                "tool-execution-started"),
            (SessionEventKind.ToolResultObserved,
                "tool-result-observed"),
            (SessionEventKind.CompletionRequestPrepared,
                "completion-request-prepared"),
            (SessionEventKind.CompletionAttemptFailed,
                "completion-attempt-failed"),
            (SessionEventKind.ImportedAgentAction,
                "imported-agent-action"),
            (SessionEventKind.CompletionAttemptStarted,
                "completion-attempt-started")
        ];

    [Fact]
    public void V3WriterHasExactClosedStringShapeAndTypedRoundTrip() {
        Assert.Equal(
            PhaseTokens.Select(static entry => entry.Value),
            Enum.GetValues<SessionExecutionPhase>());
        Assert.Equal(
            EventKindTokens.Select(static entry => entry.Value),
            Enum.GetValues<SessionEventKind>());

        SessionJournalOfflineValidationReport report = CreateReport(
            SessionExecutionPhase.Idle,
            SessionEventKind.ImportedAgentAction,
            [
                .. EventKindTokens.Select(
                    static (entry, index) =>
                        new SessionJournalOfflineEventKindCount(
                            entry.Value,
                            index + 1))
            ]);
        JsonElement root = JsonSerializer.SerializeToElement(
            report,
            WebJsonOptions);

        AssertExactPropertyNames(
            root,
            "schema",
            "repositoryPath",
            "branchName",
            "branchRefId",
            "head",
            "eventCount",
            "logicalPayloadBytes",
            "executionPhase",
            "headKind",
            "toolExecutionSequenceCheckpoint",
            "runtimeConfigSetup",
            "systemPromptSetup",
            "runtimeConfig",
            "systemPromptUtf8Sha256CodecId",
            "systemPromptUtf8Sha256",
            "preparedRequestCount",
            "observationCount",
            "agentActionCount",
            "importedAgentActionCount",
            "toolResultHistoryCount",
            "historyContributionCount",
            "historySemanticCommitmentCodecId",
            "historySemanticCommitmentSha256",
            "eventKindCounts",
            "scanDiagnostics");
        Assert.Equal(
            "atelia.session-journal.offline-validation.v3",
            root.GetProperty("schema").GetString());
        Assert.Equal("idle", root.GetProperty(
            "executionPhase").GetString());
        Assert.Equal("imported-agent-action", root.GetProperty(
            "headKind").GetString());

        AssertStringProperties(
            root,
            "schema",
            "repositoryPath",
            "branchName",
            "branchRefId",
            "head",
            "runtimeConfigSetup",
            "systemPromptSetup",
            "systemPromptUtf8Sha256CodecId",
            "systemPromptUtf8Sha256",
            "historySemanticCommitmentCodecId",
            "historySemanticCommitmentSha256");
        AssertNumberProperties(
            root,
            "eventCount",
            "logicalPayloadBytes",
            "toolExecutionSequenceCheckpoint",
            "preparedRequestCount",
            "observationCount",
            "agentActionCount",
            "importedAgentActionCount",
            "toolResultHistoryCount",
            "historyContributionCount");
        Assert.Equal(JsonValueKind.Object, root.GetProperty(
            "runtimeConfig").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty(
            "eventKindCounts").ValueKind);
        Assert.Equal(JsonValueKind.Object, root.GetProperty(
            "scanDiagnostics").ValueKind);

        JsonElement runtime = root.GetProperty("runtimeConfig");
        AssertExactPropertyNames(
            runtime,
            "modelId",
            "completionSurfaceId",
            "schema",
            "derivedContext");
        AssertStringProperties(
            runtime,
            "modelId",
            "completionSurfaceId",
            "schema");
        JsonElement derived = runtime.GetProperty("derivedContext");
        AssertExactPropertyNames(derived, "nthPrevious");
        Assert.Equal(JsonValueKind.Number, derived.GetProperty(
            "nthPrevious").ValueKind);

        JsonElement diagnostics = root.GetProperty("scanDiagnostics");
        AssertExactPropertyNames(
            diagnostics,
            "capturedEventCount",
            "repositoryEventReadCount",
            "indexedHeaderLookupCount",
            "indexedEventLookupCount",
            "decodedPayloadBytes",
            "preparedReconstructionCount");
        AssertNumberProperties(
            diagnostics,
            "capturedEventCount",
            "repositoryEventReadCount",
            "indexedHeaderLookupCount",
            "indexedEventLookupCount",
            "decodedPayloadBytes",
            "preparedReconstructionCount");

        JsonElement counts = root.GetProperty("eventKindCounts");
        Assert.Equal(EventKindTokens.Length, counts.GetArrayLength());
        for (int index = 0; index < EventKindTokens.Length; index++) {
            JsonElement count = counts[index];
            AssertExactPropertyNames(count, "kind", "count");
            Assert.Equal(
                EventKindTokens[index].Token,
                count.GetProperty("kind").GetString());
            Assert.Equal(
                index + 1,
                count.GetProperty("count").GetInt32());
        }

        SessionJournalOfflineValidationReport decoded =
            JsonSerializer.Deserialize<SessionJournalOfflineValidationReport>(
                root.GetRawText(),
                WebJsonOptions)
            ?? throw new Xunit.Sdk.XunitException(
                "V3 report did not deserialize.");
        Assert.Equal(report.ExecutionPhase, decoded.ExecutionPhase);
        Assert.Equal(report.HeadKind, decoded.HeadKind);
        Assert.Equal(report.EventKindCounts, decoded.EventKindCounts);

        AssertPropertyConverterIsInternalWithPublicConstructor(
            typeof(SessionJournalOfflineValidationReport),
            nameof(SessionJournalOfflineValidationReport.ExecutionPhase),
            typeof(SessionExecutionPhase));
        AssertPropertyConverterIsInternalWithPublicConstructor(
            typeof(SessionJournalOfflineValidationReport),
            nameof(SessionJournalOfflineValidationReport.HeadKind),
            typeof(SessionEventKind?));
        AssertPropertyConverterIsInternalWithPublicConstructor(
            typeof(SessionJournalOfflineEventKindCount),
            nameof(SessionJournalOfflineEventKindCount.Kind),
            typeof(SessionEventKind));
    }

    [Fact]
    public void EveryClosedTokenRoundTripsExactly() {
        foreach ((SessionExecutionPhase value, string token) in PhaseTokens) {
            JsonElement root = JsonSerializer.SerializeToElement(
                CreateReport(value, SessionEventKind.SessionCreated),
                WebJsonOptions);
            Assert.Equal(token, root.GetProperty(
                "executionPhase").GetString());
            Assert.Equal(
                value,
                JsonSerializer.Deserialize<
                    SessionJournalOfflineValidationReport>(
                    root.GetRawText(),
                    WebJsonOptions)!.ExecutionPhase);
        }

        foreach ((SessionEventKind value, string token) in EventKindTokens) {
            JsonElement root = JsonSerializer.SerializeToElement(
                CreateReport(
                    SessionExecutionPhase.Idle,
                    value,
                    [new SessionJournalOfflineEventKindCount(value, 1)]),
                WebJsonOptions);
            Assert.Equal(token, root.GetProperty("headKind").GetString());
            Assert.Equal(
                token,
                root.GetProperty("eventKindCounts")[0]
                    .GetProperty("kind").GetString());
            SessionJournalOfflineValidationReport decoded =
                JsonSerializer.Deserialize<
                    SessionJournalOfflineValidationReport>(
                    root.GetRawText(),
                    WebJsonOptions)!;
            Assert.Equal(value, decoded.HeadKind);
            Assert.Equal(value, decoded.EventKindCounts[0].Kind);
        }
    }

    [Fact]
    public void HeadKindNullRoundTrips() {
        SessionJournalOfflineValidationReport report = CreateReport(
            SessionExecutionPhase.Empty,
            headKind: null,
            eventKindCounts: []);

        string json = JsonSerializer.Serialize(report, WebJsonOptions);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(
            JsonValueKind.Null,
            document.RootElement.GetProperty("headKind").ValueKind);
        Assert.Null(JsonSerializer.Deserialize<
            SessionJournalOfflineValidationReport>(
                json,
                WebJsonOptions)!.HeadKind);
    }

    [Fact]
    public void NumericWrongCaseUnknownAndNullTokensFailClosed() {
        AssertRejected(root => root["executionPhase"] = 1);
        AssertRejected(root => root["executionPhase"] = "Idle");
        JsonException phaseError = AssertRejected(root =>
            root["executionPhase"] = "SECRET-future-phase");
        Assert.DoesNotContain(
            "SECRET-future-phase",
            phaseError.Message,
            StringComparison.Ordinal);
        AssertRejected(root => root["executionPhase"] = null);
        AssertRejected(root => root["headKind"] = 10);
        AssertRejected(root => root["headKind"] = "ImportedAgentAction");
        JsonException headKindError = AssertRejected(root =>
            root["headKind"] = "SECRET-future-kind");
        Assert.DoesNotContain(
            "SECRET-future-kind",
            headKindError.Message,
            StringComparison.Ordinal);
        AssertRejected(root =>
            root["eventKindCounts"]![0]!["kind"] = 10);
        AssertRejected(root =>
            root["eventKindCounts"]![0]!["kind"] =
                "ImportedAgentAction");
        JsonException countKindError = AssertRejected(root =>
            root["eventKindCounts"]![0]!["kind"] =
                "SECRET-future-count-kind");
        Assert.DoesNotContain(
            "SECRET-future-count-kind",
            countKindError.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FutureEnumValuesFailBeforeReportCanBeWritten() {
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(
            CreateReport(
                (SessionExecutionPhase)int.MaxValue,
                SessionEventKind.SessionCreated),
            WebJsonOptions));
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(
            CreateReport(
                SessionExecutionPhase.Idle,
                (SessionEventKind)11),
            WebJsonOptions));
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(
            CreateReport(
                SessionExecutionPhase.Idle,
                SessionEventKind.SessionCreated,
                [
                    new SessionJournalOfflineEventKindCount(
                        (SessionEventKind)12,
                        1)
                ]),
            WebJsonOptions));
    }

    private static SessionJournalOfflineValidationReport CreateReport(
        SessionExecutionPhase executionPhase,
        SessionEventKind? headKind,
        IReadOnlyList<SessionJournalOfflineEventKindCount>?
            eventKindCounts = null
    ) => new(
        SessionJournalOfflineValidator.ReportSchema,
        "/tmp/offline-report-fixture",
        "main",
        "0000000000000001",
        "ej1:00000001000000010000000100000000",
        11,
        1234,
        executionPhase,
        headKind,
        7,
        "ej1:00000001000000020000000100000000",
        "ej1:00000001000000030000000100000000",
        new SessionRuntimeConfiguration(
            "model-A",
            "surface-A",
            SessionJournalDefaults.Schema,
            new SessionDerivedContextConfiguration(2)),
        SessionJournalOfflineValidator.SystemPromptUtf8Sha256CodecId,
        new string('a', 64),
        1,
        2,
        3,
        4,
        5,
        6,
        SessionHistorySemanticCommitment.CodecId,
        new string('b', 64),
        eventKindCounts ?? [
            new SessionJournalOfflineEventKindCount(
                SessionEventKind.SessionCreated,
                1)
        ],
        new SessionJournalAuditScanDiagnostics(
            11,
            11,
            2,
            3,
            1234,
            1));

    private static JsonException AssertRejected(Action<JsonObject> mutate) {
        JsonObject root = JsonNode.Parse(JsonSerializer.Serialize(
            CreateReport(
                SessionExecutionPhase.Idle,
                SessionEventKind.ImportedAgentAction,
                [
                    new SessionJournalOfflineEventKindCount(
                        SessionEventKind.ImportedAgentAction,
                        1)
                ]),
            WebJsonOptions))!.AsObject();
        mutate(root);

        return Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<
                SessionJournalOfflineValidationReport>(
                root.ToJsonString(),
                WebJsonOptions));
    }

    private static void AssertPropertyConverterIsInternalWithPublicConstructor(
        Type declaringType,
        string propertyName,
        Type expectedPropertyType
    ) {
        PropertyInfo property = declaringType.GetProperty(propertyName)
            ?? throw new Xunit.Sdk.XunitException(
                $"Missing property {declaringType.Name}.{propertyName}.");
        Assert.Equal(expectedPropertyType, property.PropertyType);
        JsonConverterAttribute attribute = Assert.Single(
            property.GetCustomAttributes<JsonConverterAttribute>());
        Type converterType = attribute.ConverterType
            ?? throw new Xunit.Sdk.XunitException(
                $"Missing converter type for {declaringType.Name}."
                + propertyName);
        Assert.False(converterType.IsVisible);
        ConstructorInfo constructor = Assert.Single(
            converterType.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public));
        Assert.Empty(constructor.GetParameters());
    }

    private static void AssertExactPropertyNames(
        JsonElement value,
        params string[] expected
    ) => Assert.Equal(
        expected.OrderBy(static name => name, StringComparer.Ordinal),
        value.EnumerateObject()
            .Select(static property => property.Name)
            .OrderBy(static name => name, StringComparer.Ordinal));

    private static void AssertStringProperties(
        JsonElement value,
        params string[] names
    ) {
        foreach (string name in names) {
            Assert.Equal(
                JsonValueKind.String,
                value.GetProperty(name).ValueKind);
        }
    }

    private static void AssertNumberProperties(
        JsonElement value,
        params string[] names
    ) {
        foreach (string name in names) {
            Assert.Equal(
                JsonValueKind.Number,
                value.GetProperty(name).ValueKind);
        }
    }
}
