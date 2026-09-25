using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Galatea.Input;
using Atelia.MdJson;
using Atelia.SessionJournal;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaObservationSharedProjectionTests {
    private const string Body = "  exact\r\n~~~text\n${characterName}\n```\nend \n";

    [Theory]
    [InlineData("player-action")]
    [InlineData("heartbeat-activation")]
    [InlineData("delegate-reply")]
    [InlineData("inbound-mail")]
    public void SharedProjectorPreservesEveryTriggerAndMatchesHostProjection(string kind) {
        JsonObject input = Observation(kind);
        SessionInputContent content = Content(input);
        byte[] before = content.ToUtf8Json();
        string projection = GalateaObservationInputProjector.Instance.Project(content);
        Assert.Equal(GalateaInputProjector.Instance.Project(content), projection);
        Assert.True(JsonElement.DeepEquals(content.JsonValue, MdJsonSerializer.Read(projection)));
        Assert.Equal(before, content.ToUtf8Json());
        Assert.DoesNotContain(typeof(GalateaObservationInputProjector).Assembly.GetReferencedAssemblies(),
            name => name.Name == "Atelia.Galatea.Server");
        if (kind is "player-action" or "inbound-mail") { Assert.Contains(Body, projection, StringComparison.Ordinal); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(525_600)]
    public void V2HeartbeatAcceptsConfiguredIntervalAndPreservesProjection(int intervalMinutes) {
        JsonObject input = Observation("heartbeat-activation");
        input["action"]!["externalIntervalMinutes"] = intervalMinutes;
        SessionInputContent content = Content(input, GalateaObservationSchema.V2SchemaId);

        string projection = GalateaObservationInputProjector.Instance.Project(content);

        Assert.True(JsonElement.DeepEquals(content.JsonValue, MdJsonSerializer.Read(projection)));
        Assert.Equal(intervalMinutes, GalateaObservationContent.ReadPlayerTurn(content).HeartbeatIntervalMinutes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(525_601)]
    public void V2HeartbeatRejectsOutOfRangeInterval(int intervalMinutes) {
        JsonObject input = Observation("heartbeat-activation");
        input["action"]!["externalIntervalMinutes"] = intervalMinutes;
        Assert.ThrowsAny<Exception>(() => GalateaObservationInputProjector.Instance.Project(
            Content(input, GalateaObservationSchema.V2SchemaId)));
    }

    [Fact]
    public void V2HeartbeatRejectsNonIntegerOtherKindsAndUnknownFields() {
        JsonObject fractional = Observation("heartbeat-activation");
        fractional["action"]!["externalIntervalMinutes"] = 7.5;
        Assert.ThrowsAny<Exception>(() => GalateaObservationInputProjector.Instance.Project(
            Content(fractional, GalateaObservationSchema.V2SchemaId)));

        JsonObject overflow = Observation("heartbeat-activation");
        overflow["action"]!["externalIntervalMinutes"] = 3_000_000_000L;
        Assert.ThrowsAny<Exception>(() => GalateaObservationInputProjector.Instance.Project(
            Content(overflow, GalateaObservationSchema.V2SchemaId)));

        Assert.ThrowsAny<Exception>(() => GalateaObservationInputProjector.Instance.Project(
            Content(Observation("player-action"), GalateaObservationSchema.V2SchemaId)));

        JsonObject unknown = Observation("heartbeat-activation");
        unknown["unknown"] = true;
        Assert.ThrowsAny<Exception>(() => GalateaObservationInputProjector.Instance.Project(
            Content(unknown, GalateaObservationSchema.V2SchemaId)));
    }

    [Fact]
    public void V1HeartbeatRemainsStrictAndReopensWithHistoricalTenMinuteNarrative() {
        SessionInputContent content = Content(Observation("heartbeat-activation"));
        PlayerTurnObservation observed = GalateaObservationContent.ReadPlayerTurn(content);

        Assert.Equal(10, observed.HeartbeatIntervalMinutes);
        Assert.Contains("十分钟", PlayerTurnObservationEnvelope.Wrap(observed), StringComparison.Ordinal);

        JsonObject invalid = Observation("heartbeat-activation");
        invalid["action"]!["externalIntervalMinutes"] = 11;
        Assert.ThrowsAny<Exception>(() => GalateaObservationInputProjector.Instance.Project(Content(invalid)));
    }

    [Theory]
    [InlineData("reply")]
    [InlineData("delivery-failure")]
    [InlineData("note-save-receipt")]
    [InlineData("legacy-reply")]
    [InlineData("legacy-delivery-failure")]
    [InlineData("legacy-note-save-receipt")]
    public void NoticeFieldsAreValidatedAndBodySelectionRemainsLossless(string kind) {
        JsonObject input = Observation("player-action");
        input["notices"] = new JsonArray(Notice(kind));
        SessionInputContent content = Content(input);
        string projection = GalateaObservationInputProjector.Instance.Project(content);
        Assert.True(JsonElement.DeepEquals(content.JsonValue, MdJsonSerializer.Read(projection)));
        Assert.Single(GalateaObservationContent.ReadPlayerTurn(content).Notices);
        Assert.Contains(Body, projection, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("memo-gist")]
    [InlineData("memo-summary")]
    [InlineData("memo-exact-text")]
    public void RecallsRetainTheirSourceVersionAndExactSelectedContent(string kind) {
        JsonObject input = Observation("player-action");
        input["recalls"] = new JsonArray(Recall(kind));
        var content = Content(input);
        string projection = GalateaObservationInputProjector.Instance.Project(content);
        Assert.True(JsonElement.DeepEquals(content.JsonValue, MdJsonSerializer.Read(projection)));
        Assert.Single(GalateaObservationContent.ReadPlayerTurn(content).Recalls);
    }

    [Theory]
    [InlineData("sender-kind")]
    [InlineData("sender-id-space")]
    [InlineData("timestamp-fraction")]
    [InlineData("timestamp-no-offset")]
    [InlineData("unknown-root-field")]
    [InlineData("player-text-too-large")]
    [InlineData("heartbeat-interval")]
    [InlineData("heartbeat-with-reply")]
    [InlineData("delegate-without-reply")]
    [InlineData("receipt-not-last")]
    [InlineData("receipt-duplicate-id")]
    [InlineData("receipt-ordinal")]
    [InlineData("receipt-partial-texts")]
    [InlineData("receipt-text-too-large")]
    [InlineData("receipt-total-too-large")]
    [InlineData("receipt-wrong-pod")]
    [InlineData("receipt-source-address")]
    [InlineData("duplicate-recall")]
    [InlineData("recall-source-components")]
    [InlineData("recall-version-newline")]
    [InlineData("legacy-failure-too-large")]
    [InlineData("reply-dispatch-space")]
    [InlineData("mail-id")]
    [InlineData("mail-header-newline")]
    [InlineData("mail-recipient-marker")]
    [InlineData("mail-xml-control")]
    [InlineData("mail-injector-mismatch")]
    [InlineData("mail-enrichment")]
    public void ExtractionKeepsOriginalStrictObservationBoundaries(string scenario) {
        JsonObject input = Observation("player-action");
        switch (scenario) {
            case "sender-kind": input["sender"]!["kind"] = "character"; break;
            case "sender-id-space": input["sender"]!["id"] = " player "; break;
            case "timestamp-fraction": input["externalLocalTimestamp"] = "2026-09-15T00:00:00.0000001+00:00"; break;
            case "timestamp-no-offset": input["externalLocalTimestamp"] = "2026-09-15T00:00:00"; break;
            case "unknown-root-field": input["unknown"] = 1; break;
            case "player-text-too-large": input["action"]!["text"] = new string('x', 65537); break;
            case "heartbeat-interval": input = Observation("heartbeat-activation"); input["action"]!["externalIntervalMinutes"] = 11; break;
            case "heartbeat-with-reply": input = Observation("heartbeat-activation"); input["notices"] = new JsonArray(Notice("reply")); break;
            case "delegate-without-reply": input = Observation("delegate-reply"); input["notices"] = new JsonArray(); break;
            case "receipt-not-last": input["notices"] = new JsonArray(Notice("note-save-receipt"), Notice("reply")); break;
            case "receipt-duplicate-id":
            case "receipt-ordinal":
            case "receipt-partial-texts":
            case "receipt-text-too-large":
            case "receipt-total-too-large":
            case "receipt-wrong-pod":
            case "receipt-source-address": {
                var receipt = Notice("note-save-receipt");
                JsonObject facts = receipt["receipt"]!.AsObject();
                if (scenario == "receipt-duplicate-id") { facts["saved"]![1]!["memoId"] = "m1:00000001"; }
                if (scenario == "receipt-ordinal") { facts["saved"]![1]!["ordinal"] = 3; }
                if (scenario == "receipt-partial-texts") { facts["exactTexts"] = new JsonArray("one"); }
                if (scenario == "receipt-text-too-large") { facts["exactTexts"]![0] = new string('x', 65537); }
                if (scenario == "receipt-wrong-pod") { facts["podId"] = "00000000000000000000000000000002"; }
                if (scenario == "receipt-source-address") { facts["sourceActionAddress"] = "bad"; }
                if (scenario == "receipt-total-too-large") {
                    facts["saved"] = new JsonArray(Enumerable.Range(0, 5).Select(index => (JsonNode)new JsonObject {
                        ["ordinal"] = index, ["memoId"] = "m1:" + (index + 1).ToString("x8")
                    }).ToArray());
                    facts["exactTexts"] = new JsonArray(Enumerable.Range(0, 5).Select(_ => (JsonNode)JsonValue.Create(new string('x', 65536))!).ToArray());
                }
                input["notices"] = new JsonArray(receipt);
                break;
            }
            case "duplicate-recall": input["recalls"] = new JsonArray(Recall("memo-gist"), Recall("memo-gist")); break;
            case "recall-source-components": {
                var recall = Recall("memo-exact-text"); recall["memoId"] = "m1:00000002"; input["recalls"] = new JsonArray(recall); break;
            }
            case "recall-version-newline": {
                var recall = Recall("memo-gist"); recall["sourceVersion"] = "v\n2"; input["recalls"] = new JsonArray(recall); break;
            }
            case "legacy-failure-too-large": {
                var notice = Notice("legacy-delivery-failure"); notice["body"] = new string('x', 4097); input["notices"] = new JsonArray(notice); break;
            }
            case "reply-dispatch-space": {
                var notice = Notice("reply"); notice["dispatchId"] = " dispatch "; input["notices"] = new JsonArray(notice); break;
            }
            default:
                input = Observation("inbound-mail");
                if (scenario == "mail-id") { input["action"]!["messageId"] = "ABC"; }
                if (scenario == "mail-header-newline") { input["action"]!["from"] = "Name\u2028fake header"; }
                if (scenario == "mail-recipient-marker") { input["action"]!["to"] = "[Alice]"; }
                if (scenario == "mail-xml-control") { input["action"]!["body"] = "xml\u0001control"; }
                if (scenario == "mail-injector-mismatch") { input["action"]!["injectedBy"] = Sender("player", "other", "Visitor"); }
                if (scenario == "mail-enrichment") { input["notices"] = new JsonArray(Notice("reply")); }
                break;
        }
        SessionInputContent content = Content(input);
        Assert.ThrowsAny<Exception>(() => GalateaObservationInputProjector.Instance.Project(content));
        Assert.ThrowsAny<Exception>(() => GalateaObservationContent.Validate(content));
    }

    [Fact]
    public void ReceiptIdsOnlyAndHttpInjectorRemainSupported() {
        JsonObject input = Observation("player-action");
        var receipt = Notice("note-save-receipt"); receipt["receipt"]!["exactTexts"] = new JsonArray();
        input["notices"] = new JsonArray(receipt);
        var content = Content(input);
        Assert.True(JsonElement.DeepEquals(content.JsonValue, MdJsonSerializer.Read(GalateaObservationInputProjector.Instance.Project(content))));
        input = Observation("inbound-mail");
        input["sender"] = Sender("player", "visitor", "Visitor");
        input["action"]!["injectedBy"] = input["sender"]!.DeepClone();
        input["action"]!["from"] = "Codex";
        content = Content(input);
        Assert.True(JsonElement.DeepEquals(content.JsonValue, MdJsonSerializer.Read(GalateaObservationInputProjector.Instance.Project(content))));
    }

    [Fact]
    public void PlainTextIsPreservedAndOtherSchemasDoNotGetAJsonFallback() {
        Assert.Equal(Body, GalateaObservationInputProjector.Instance.Project(SessionInputContent.Text(Body)));
        foreach (string schema in new[] { "unknown", "galatea.system-instructions.v1", "galatea.delegate-task.v1" }) {
            Assert.Throws<NotSupportedException>(() => GalateaObservationInputProjector.Instance.Project(
                SessionInputContent.Structured(schema, JsonSerializer.SerializeToElement(new { v = 1 }))));
        }
    }

    private static SessionInputContent Content(JsonObject value, string schemaId = GalateaObservationSchema.V1SchemaId)
        => SessionInputContent.Structured(schemaId, JsonSerializer.SerializeToElement(value));
    private static JsonObject Sender(string kind, string id, string name) => new() { ["kind"] = kind, ["id"] = id, ["name"] = name };
    private static JsonObject Observation(string kind) {
        bool runtime = kind is "heartbeat-activation" or "delegate-reply";
        return new JsonObject {
            ["v"] = 1, ["kind"] = kind,
            ["sender"] = Sender(runtime ? "runtime" : kind == "inbound-mail" ? "character" : "player", "source", "Source"),
            ["externalLocalTimestamp"] = "2026-09-15T00:00:00.0000000+00:00",
            ["action"] = kind switch {
                "player-action" => new JsonObject { ["text"] = Body },
                "heartbeat-activation" => new JsonObject { ["character"] = Sender("character", "alice", "Alice"), ["externalIntervalMinutes"] = 10 },
                "delegate-reply" => new JsonObject(),
                _ => new JsonObject { ["messageId"] = new string('a', 32), ["from"] = "Sender", ["to"] = "Alice", ["subject"] = null, ["body"] = Body, ["injectedBy"] = null }
            },
            ["notices"] = kind == "delegate-reply" ? new JsonArray(Notice("reply")) : new JsonArray(),
            ["recalls"] = new JsonArray()
        };
    }
    private static JsonObject Notice(string kind) {
        if (kind == "note-save-receipt") {
            string address = EventAddressTextCodec.Format(new Atelia.EventJournal.EventAddress(Atelia.Data.SizedPtr.Create(4, 4), 1, Atelia.EventJournal.AddressHint.None));
            return new JsonObject { ["kind"] = kind, ["sender"] = Sender("runtime", "galatea", "Runtime"), ["receipt"] = new JsonObject {
                ["sourceActionAddress"] = address, ["podId"] = GalateaObservationLimits.DefaultNotePodId,
                ["saved"] = new JsonArray(new JsonObject { ["ordinal"] = 0, ["memoId"] = "m1:00000001" }, new JsonObject { ["ordinal"] = 1, ["memoId"] = "m1:00000002" }),
                ["exactTexts"] = new JsonArray(Body, "second text")
            }};
        }
        if (kind == "legacy-note-save-receipt") { return new JsonObject { ["kind"] = kind, ["body"] = Body, ["sourceActionAddress"] = null }; }
        var value = new JsonObject { ["kind"] = kind, ["dispatchId"] = "dispatch", ["threadId"] = "thread", ["turnId"] = "turn", ["noticeId"] = "notice" };
        if (!kind.StartsWith("legacy-", StringComparison.Ordinal)) { value["sender"] = Sender("delegate", "codex", "Codex"); }
        if (kind.Contains("delivery-failure", StringComparison.Ordinal)) { value["code"] = "failure"; value["stage"] = null; }
        value[kind == "delivery-failure" ? "detail" : "body"] = Body;
        return value;
    }
    private static JsonObject Recall(string kind) => kind == "memo-exact-text"
        ? new JsonObject { ["kind"] = kind, ["sourceId"] = "memo-pod:v1/" + GalateaObservationLimits.DefaultNotePodId + "/m1:00000001",
            ["podId"] = GalateaObservationLimits.DefaultNotePodId, ["memoId"] = "m1:00000001", ["podStateIdentity"] = "epoch-1", ["title"] = "title", ["exactText"] = Body }
        : new JsonObject { ["kind"] = kind, ["sourceId"] = "source-1", ["sourceVersion"] = "v1", ["text"] = Body };
}
