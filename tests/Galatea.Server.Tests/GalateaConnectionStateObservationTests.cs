using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Data;
using Atelia.EventJournal;
using Atelia.Galatea.Input;
using Atelia.Galatea.Prompts;
using Atelia.Galatea.Server.Mailbox;
using Atelia.MdJson;
using Atelia.SessionJournal;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaConnectionStateObservationTests {
    private static readonly DateTimeOffset Timestamp = new(2026, 9, 25, 10, 20, 30, TimeSpan.FromHours(8));
    private static readonly GalateaSenderSnapshot Character = new("character", "alice", "Alice");
    private static readonly GalateaSenderSnapshot Player = new("player", "player", "Player");
    private const string Evidence = "  她戴着眼镜。\r\n~~~json\n{\"state\":\"on\"}\n~~~\n";

    [Theory]
    [InlineData("player-action")]
    [InlineData("heartbeat-activation")]
    [InlineData("delegate-reply")]
    [InlineData("inbound-mail")]
    public void V4SnapshotSurvivesStructuredStorageAndRequestProjection(string kind) {
        string address = EventAddressTextCodec.Format(new EventAddress(SizedPtr.Create(4, 4), 1, AddressHint.None));
        var change = new GalateaConnectionStateChange(address, "old", "new", "戴眼镜", Evidence);
        var snapshot = new GalateaConnectionStateSnapshot("new", "new", "diagnostic", change);
        GalateaFreshInput input = kind switch {
            "player-action" => new GalateaFreshInput.PlayerAction("继续", Player),
            "heartbeat-activation" => new GalateaFreshInput.HeartbeatActivation(new GalateaCharacterName("Alice"), 7),
            "delegate-reply" => new GalateaFreshInput.DelegateReply([
                new PlayerTurnNotice.Reply("回复", Player, "dispatch")]),
            _ => new GalateaFreshInput.InboundMail(
                MailboxMessage.FromCanonicalEnvelope(new string('a', 32), "Sender", "Alice", null, "邮件正文"), Sender: Character)
        };
        SessionInputContent content = GalateaObservationContent.Create(input, Timestamp, Character, connectionState: snapshot);

        Assert.Equal(GalateaObservationContent.V4SchemaId, content.SchemaId);
        Assert.Equal(snapshot, GalateaObservationContent.ReadConnectionState(content));
        string projected = GalateaObservationInputProjector.Instance.Project(content);
        Assert.Equal(JsonValueKind.String, MdJsonSerializer.Read(projected).GetProperty("connectionState").ValueKind);
        Assert.Contains(Evidence, projected, StringComparison.Ordinal);
        Assert.Contains("/connectionState/lastChange/evidence",
            GalateaObservationContent.ExternalStringPaths(content.SchemaId, content.JsonValue));
        if (kind == "inbound-mail") { Assert.Equal("邮件正文", GalateaObservationContent.ReadMailboxContent(content).Body); }
        else { _ = GalateaObservationContent.ReadPlayerTurn(content); }
    }

    [Fact]
    public void NullChangeAndNullOverrideRoundTripWithoutExternalEvidencePath() {
        var snapshot = new GalateaConnectionStateSnapshot(null, "default", "default");
        SessionInputContent content = GalateaObservationContent.Create(
            new GalateaFreshInput.PlayerAction("继续", Player), Timestamp, Character, connectionState: snapshot);
        Assert.Equal(snapshot, GalateaObservationContent.ReadConnectionState(content));
        Assert.DoesNotContain("/connectionState/lastChange/evidence",
            GalateaObservationContent.ExternalStringPaths(content.SchemaId, content.JsonValue));
    }

    [Theory]
    [InlineData("")]
    [InlineData("第一行\n第二行")]
    public void V4AcceptsConfiguredOptionNamesIncludingEmptyAndMultiline(string name) {
        string address = EventAddressTextCodec.Format(new EventAddress(SizedPtr.Create(4, 4), 1, AddressHint.None));
        var snapshot = new GalateaConnectionStateSnapshot("new", "new", "new",
            new GalateaConnectionStateChange(address, "old", "new", name, Evidence));
        SessionInputContent content = GalateaObservationContent.Create(
            new GalateaFreshInput.PlayerAction("继续", Player), Timestamp, Character, connectionState: snapshot);
        Assert.Equal(snapshot, GalateaObservationContent.ReadConnectionState(content));
    }

    [Fact]
    public void V4AcceptsExactConfigurationBounds() {
        string address = EventAddressTextCodec.Format(new EventAddress(SizedPtr.Create(4, 4), 1, AddressHint.None));
        string id = new string('x', 128);
        var snapshot = new GalateaConnectionStateSnapshot(id, id, id,
            new GalateaConnectionStateChange(address, " old ", id, new string('名', 1365) + "a", new string('e', 2048)));
        SessionInputContent content = GalateaObservationContent.Create(
            new GalateaFreshInput.PlayerAction("继续", Player), Timestamp, Character, connectionState: snapshot);
        Assert.Equal(snapshot, GalateaObservationContent.ReadConnectionState(content));
    }

    [Theory]
    [InlineData("galatea.observation.v1")]
    [InlineData("galatea.observation.v2")]
    public void LegacySchemasRejectInjectedConnectionState(string schema) {
        SessionInputContent source = GalateaObservationContent.Create(
            new GalateaFreshInput.HeartbeatActivation(new GalateaCharacterName("Alice"), 10), Timestamp, Character,
            connectionState: new GalateaConnectionStateSnapshot(null, "default", "default"));
        JsonObject value = JsonNode.Parse(source.JsonValue.GetRawText())!.AsObject();
        Assert.Throws<InvalidDataException>(() => GalateaObservationInputProjector.Instance.Project(
            SessionInputContent.Structured(schema, JsonSerializer.SerializeToElement(value))));
    }

    [Theory]
    [InlineData("missing-state")]
    [InlineData("bad-address")]
    [InlineData("bad-id")]
    [InlineData("oversize-name")]
    [InlineData("oversize-evidence")]
    [InlineData("unknown-field")]
    public void V4RejectsMalformedSnapshot(string defect) {
        SessionInputContent source = GalateaObservationContent.Create(
            new GalateaFreshInput.PlayerAction("继续", Player), Timestamp, Character,
            connectionState: new GalateaConnectionStateSnapshot(null, "default", "default"));
        JsonObject value = JsonNode.Parse(source.JsonValue.GetRawText())!.AsObject();
        JsonObject state = value["connectionState"]!.AsObject();
        if (defect == "missing-state") { value.Remove("connectionState"); }
        else if (defect == "bad-id") { state["effectiveConnectionId"] = new string('x', 129); }
        else if (defect == "unknown-field") { state["other"] = true; }
        else {
            state["runtimeOverrideConnectionId"] = "new";
            state["effectiveConnectionId"] = "new";
            state["lastChange"] = new JsonObject {
                ["sourceActionAddress"] = defect == "bad-address" ? "bad" :
                    EventAddressTextCodec.Format(new EventAddress(SizedPtr.Create(4, 4), 1, AddressHint.None)),
                ["previousConnectionId"] = "old", ["connectionId"] = "new",
                ["previousName"] = "previous",
                ["name"] = defect == "oversize-name" ? new string('x', 4097) : "name",
                ["evidence"] = defect == "oversize-evidence" ? new string('x', 2049) : Evidence
            };
        }
        Assert.ThrowsAny<Exception>(() => GalateaObservationInputProjector.Instance.Project(
            SessionInputContent.Structured(GalateaObservationContent.V4SchemaId, JsonSerializer.SerializeToElement(value))));
    }
}
