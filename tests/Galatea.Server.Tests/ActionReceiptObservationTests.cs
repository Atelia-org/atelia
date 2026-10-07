using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Galatea.Input;
using Atelia.Galatea.Prompts;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.MdJson;
using Atelia.MemoPod;
using Atelia.SessionJournal;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class ActionReceiptObservationTests {
    private static readonly DateTimeOffset Timestamp = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
    private static readonly GalateaSenderSnapshot Player = new("player", "player", "Player");
    private static readonly GalateaSenderSnapshot Character = new("character", "alice", "Alice");
    private static readonly string LongBody = new string('H', 32) + "distinct-long-body-middle-must-never-appear-in-receipt" + new string('T', 16);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothProjectionsRoundTripInSharedProjectorAndStayPure(bool compact) {
        PlayerTurnNotice[] notices = Receipts(compact);
        SessionInputContent content = CreateInput(notices);
        byte[] before = content.ToUtf8Json();
        string projected = GalateaObservationInputProjector.Instance.Project(content);
        Assert.Equal(projected, GalateaInputProjector.Instance.Project(content));
        JsonElement visible = MdJsonSerializer.Read(projected);
        Assert.Equal("当前运行配置：Test。", visible.GetProperty("connectionState").GetString());
        foreach (JsonProperty property in content.JsonValue.EnumerateObject().Where(property => property.Name != "connectionState")) {
            Assert.True(JsonElement.DeepEquals(property.Value, visible.GetProperty(property.Name)));
        }
        Assert.Equal(before, content.ToUtf8Json());
        PlayerTurnObservation decoded = GalateaObservationContent.ReadPlayerTurn(content);
        Assert.Equal(2, decoded.Notices.Count);
        for (int i = 0; i < notices.Length; i++) {
            Assert.Equal(((PlayerTurnNotice.ActionReceipt)notices[i]).Batch, Assert.IsType<PlayerTurnNotice.ActionReceipt>(decoded.Notices[i]).Batch);
        }
        string display = GalateaObservationContent.DisplayText(content);
        Assert.DoesNotContain("distinct-long-body-middle", projected, StringComparison.Ordinal);
        Assert.DoesNotContain("distinct-long-body-middle", display, StringComparison.Ordinal);
        Assert.Contains(ActionReceiptContentTests.Source(1), projected, StringComparison.Ordinal);
        Assert.Contains(ActionReceiptContentTests.Dispatch(1), display, StringComparison.Ordinal);
        Assert.Contains("m1:00000001", display, StringComparison.Ordinal);
        Assert.Contains("本次邮件提交已受理", display, StringComparison.Ordinal);
        Assert.Contains("本次邮件未进入投递队列", display, StringComparison.Ordinal);
        if (!compact) { Assert.Contains(ActionReceiptPreview.Create(LongBody), display, StringComparison.Ordinal); }
        else { Assert.DoesNotContain("正文识别预览", display, StringComparison.Ordinal); }
        Assert.Equal(projected, GalateaObservationInputProjector.Instance.Project(content));
        Assert.True(PlayerTurnObservationClassifier.TryProject(content, out var classification));
        Assert.Equal(PlayerTurnObservationTriggerKind.PlayerAction, classification.TriggerKind);
    }

    [Fact]
    public void ReceiptClassificationPreservesHeartbeatReplyAndSixteenSlotContracts() {
        PlayerTurnNotice[] receipts = Receipts(false);
        var heartbeat = new GalateaFreshInput.HeartbeatActivation(new GalateaCharacterName("Alice"), 10);
        SessionInputContent heartbeatContent = GalateaObservationContent.Create(heartbeat, Timestamp, Character, receipts, connectionState: new GalateaConnectionStateSnapshot(null, "test", "test", EffectiveName: "Test", TurnName: "Test"));
        Assert.Equal(PlayerTurnObservationTriggerKind.HeartbeatActivation, GalateaObservationContent.ReadPlayerTurn(heartbeatContent).TriggerKind);
        Assert.Throws<ArgumentException>(() => new GalateaFreshInput.DelegateReply(receipts));
        JsonObject receiptOnlyReply = JsonNode.Parse(heartbeatContent.JsonValue.GetRawText())!.AsObject();
        receiptOnlyReply.Remove("connectionState");
        receiptOnlyReply["kind"] = "delegate-reply";
        receiptOnlyReply["action"] = new JsonObject();
        Assert.Throws<InvalidDataException>(() => GalateaObservationContent.Validate(Content(receiptOnlyReply, GalateaObservationSchema.V1SchemaId)));
        PlayerTurnNotice reply = new PlayerTurnNotice.Reply("real reply", new GalateaSenderSnapshot("delegate", "codex", "Codex"), "reply-dispatch");
        var sixteen = receipts.Concat(Enumerable.Repeat(reply, 14)).ToArray();
        SessionInputContent content = CreateInput(sixteen);
        Assert.Equal(16, content.JsonValue.GetProperty("notices").GetArrayLength());
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateInput(receipts.Concat(Enumerable.Repeat(reply, 15)).ToArray()));
        var delegated = new GalateaFreshInput.DelegateReply(sixteen);
        Assert.Equal(PlayerTurnObservationTriggerKind.DelegateReply,
            GalateaObservationContent.ReadPlayerTurn(GalateaObservationContent.Create(delegated, Timestamp, Character, connectionState: new GalateaConnectionStateSnapshot(null, "test", "test", EffectiveName: "Test", TurnName: "Test"))).TriggerKind);
        Assert.Throws<ArgumentException>(() => CreateInput([reply, .. receipts]));
        Assert.Throws<ArgumentException>(() => CreateInput([receipts[1], receipts[0]]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentV5ProjectionTransformsOnlyConnectionPresentation(bool compact) {
        JsonObject input = JsonNode.Parse(CreateInput(Receipts(compact)).JsonValue.GetRawText())!.AsObject();
        input["connectionState"] = new JsonObject {
            ["runtimeOverrideConnectionId"] = null, ["effectiveConnectionId"] = "main", ["turnConnectionId"] = "main",
            ["effectiveName"] = "常规连接", ["turnName"] = "常规连接", ["lastChange"] = null
        };
        SessionInputContent content = Content(input, GalateaObservationSchema.V5SchemaId);
        byte[] before = content.ToUtf8Json();
        string projected = GalateaObservationInputProjector.Instance.Project(content);
        JsonElement visible = MdJsonSerializer.Read(projected);
        Assert.Equal("当前运行配置：常规连接。", visible.GetProperty("connectionState").GetString());
        Assert.True(JsonElement.DeepEquals(content.JsonValue.GetProperty("notices"), visible.GetProperty("notices")));
        Assert.Equal(before, content.ToUtf8Json());
        Assert.Equal(projected, GalateaInputProjector.Instance.Project(content));
        Assert.Equal(2, GalateaObservationContent.ReadPlayerTurn(content).Notices.Count);
    }

    [Fact]
    public void InboundMailRetainsItsNoEnrichmentContract() {
        JsonObject input = JsonNode.Parse(CreateInput(Receipts(false)).JsonValue.GetRawText())!.AsObject();
        input["kind"] = "inbound-mail";
        input["sender"] = new JsonObject { ["kind"] = "character", ["id"] = "alice", ["name"] = "Alice" };
        input["action"] = new JsonObject {
            ["messageId"] = new string('a', 32), ["from"] = "Alice", ["to"] = "Bob", ["subject"] = null,
            ["body"] = "real message", ["injectedBy"] = null
        };
        SessionInputContent content = Content(input);
        Assert.Throws<InvalidDataException>(() => GalateaObservationContent.Validate(content));
        Assert.Throws<InvalidDataException>(() => GalateaObservationInputProjector.Instance.Project(content));
    }

    [Theory]
    [InlineData("missing-preview")]
    [InlineData("extra-item-field")]
    [InlineData("extra-notice-field")]
    [InlineData("unknown-domain")]
    [InlineData("wrong-source")]
    [InlineData("wrong-pod")]
    [InlineData("duplicate-dispatch")]
    [InlineData("duplicate-memo")]
    [InlineData("noncanonical-dispatch")]
    [InlineData("wrong-outcome")]
    [InlineData("partial-null-batch")]
    [InlineData("partial-null-recipient")]
    [InlineData("mixed-domain-projection")]
    [InlineData("preview-too-long")]
    [InlineData("wrong-preview-type")]
    [InlineData("empty-items")]
    [InlineData("duplicate-domain")]
    [InlineData("wrong-order")]
    [InlineData("wrong-sender")]
    public void SharedAndHostReadersRejectMalformedNewContract(string scenario) {
        JsonObject input = JsonNode.Parse(CreateInput(Receipts(false)).JsonValue.GetRawText())!.AsObject();
        JsonArray notices = input["notices"]!.AsArray();
        JsonObject mail = notices[0]!["receipt"]!.AsObject();
        JsonObject note = notices[1]!["receipt"]!.AsObject();
        JsonObject mailItem = mail["items"]![0]!.AsObject();
        switch (scenario) {
            case "missing-preview": mailItem.Remove("preview"); break;
            case "extra-item-field": mailItem["subject"] = "forbidden"; break;
            case "extra-notice-field": notices[0]!["mode"] = "full"; break;
            case "unknown-domain": mail["kind"] = "progress"; break;
            case "wrong-source": mail["sourceActionAddress"] = "bad"; break;
            case "wrong-pod": note["podId"] = new string('a', 32); break;
            case "duplicate-dispatch": mail["items"]!.AsArray().Add(mailItem.DeepClone()); break;
            case "duplicate-memo": note["items"]!.AsArray().Add(note["items"]![0]!.DeepClone()); break;
            case "noncanonical-dispatch": mailItem["dispatchId"] = ActionReceiptContentTests.Dispatch(1).ToUpperInvariant(); break;
            case "wrong-outcome": mailItem["outcome"] = "delivered"; break;
            case "partial-null-batch": mailItem["preview"] = null; mailItem["recipientPreview"] = null; break;
            case "partial-null-recipient": mailItem["recipientPreview"] = null; break;
            case "mixed-domain-projection": note["items"]![0]!["preview"] = null; break;
            case "preview-too-long": mailItem["preview"] = new string('x', 57); break;
            case "wrong-preview-type": mailItem["preview"] = 42; break;
            case "empty-items": mail["items"] = new JsonArray(); break;
            case "duplicate-domain": notices.Add(notices[1]!.DeepClone()); break;
            case "wrong-order": input["notices"] = new JsonArray(notices[1]!.DeepClone(), notices[0]!.DeepClone()); break;
            case "wrong-sender": notices[0]!["sender"]!["id"] = "another-runtime"; break;
        }
        SessionInputContent invalid = Content(input);
        Assert.ThrowsAny<Exception>(() => GalateaObservationContent.ReadPlayerTurn(invalid));
        Assert.ThrowsAny<Exception>(() => GalateaObservationInputProjector.Instance.Project(invalid));
    }

    [Fact]
    public void Strict128KiBBudgetIncludesNoticeWrappingAndRawJsonEscaping() {
        SessionInputContent original = CreateInput(Receipts(false));
        JsonElement input = original.JsonValue;
        JsonElement notices = input.GetProperty("notices");
        int arrayBytes = GalateaInputValidation.StrictUtf8.GetByteCount(notices.GetRawText());
        int paddingToLimit = GalateaObservationLimits.MaximumActionReceiptNoticesUtf8Bytes - arrayBytes;
        Assert.True(paddingToLimit > 0);
        string mailJson = notices[0].GetRawText();
        string exactlyAtLimit = mailJson.Insert(1, new string(' ', paddingToLimit));
        using JsonDocument atLimit = JsonDocument.Parse(input.GetRawText().Replace(mailJson, exactlyAtLimit, StringComparison.Ordinal));
        Assert.Equal(128 * 1024, GalateaInputValidation.StrictUtf8.GetByteCount(atLimit.RootElement.GetProperty("notices").GetRawText()));
        GalateaObservationSchema.Validate(original.SchemaId, atLimit.RootElement);

        string overLimit = mailJson.Insert(1, new string(' ', paddingToLimit + 1));
        using JsonDocument oversized = JsonDocument.Parse(input.GetRawText().Replace(mailJson, overLimit, StringComparison.Ordinal));
        Assert.Equal(128 * 1024 + 1, GalateaInputValidation.StrictUtf8.GetByteCount(oversized.RootElement.GetProperty("notices").GetRawText()));
        Assert.True(GalateaInputValidation.StrictUtf8.GetByteCount(oversized.RootElement.GetRawText()) < GalateaObservationLimits.MaximumContentUtf8Bytes);
        Assert.Throws<InvalidDataException>(() => GalateaObservationSchema.Validate(original.SchemaId, oversized.RootElement));
        Assert.Throws<InvalidDataException>(() => GalateaObservationContent.Validate(original.SchemaId, oversized.RootElement));

        // SessionInputContent owns canonical machine JSON and removes insignificant whitespace.
        SessionInputContent canonical = SessionInputContent.Structured(original.SchemaId!, oversized.RootElement);
        Assert.Equal(original, canonical);
        GalateaObservationContent.Validate(canonical);
        Assert.Equal(GalateaObservationInputProjector.Instance.Project(original), GalateaObservationInputProjector.Instance.Project(canonical));
    }

    [Fact]
    public void HistoricalReceiptVariantsKeepTheirFullTextAndFinalNoticeRule() {
        string fullText = new string('a', 32 * 1024) + "historical-full-middle" + new string('z', 31 * 1024);
        var selection = new CharacterNoteReceiptSelection(ActionReceiptContentTests.Source(1), [MemoId.Parse("m1:00000001")], [fullText]);
        PlayerTurnNotice[] historical = [new PlayerTurnNotice.NoteSaveReceipt(selection),
            PlayerTurnNotice.NoteSaveReceipt.FromLegacyDurable(fullText, ActionReceiptContentTests.Source(1))];
        foreach (PlayerTurnNotice notice in historical) {
            SessionInputContent content = HistoricalInput([notice]);
            string projection = GalateaObservationInputProjector.Instance.Project(content);
            Assert.Contains(fullText, projection, StringComparison.Ordinal);
            Assert.Contains(fullText, GalateaObservationContent.DisplayText(content), StringComparison.Ordinal);
            Assert.True(JsonElement.DeepEquals(content.JsonValue, MdJsonSerializer.Read(projection)));
            Assert.IsType<PlayerTurnNotice.NoteSaveReceipt>(Assert.Single(GalateaObservationContent.ReadPlayerTurn(content).Notices));
            Assert.Throws<ArgumentException>(() => CreateInput([notice, new PlayerTurnNotice.Reply("reply")]));
        }
        // The new global projection rule does not reinterpret the old IDs-only/full-text choice.
        SessionInputContent mixedHistory = HistoricalInput([.. Receipts(true), historical[0]]);
        GalateaObservationContent.Validate(mixedHistory);
        Assert.Contains(fullText, GalateaObservationInputProjector.Instance.Project(mixedHistory), StringComparison.Ordinal);
    }

    internal static SessionInputContent CreateInput(IReadOnlyList<PlayerTurnNotice> notices) => GalateaObservationContent.Create(
        new GalateaFreshInput.PlayerAction("current action", Player, notices), Timestamp, Character, connectionState: new GalateaConnectionStateSnapshot(null, "test", "test", EffectiveName: "Test", TurnName: "Test"));

    private static SessionInputContent HistoricalInput(IReadOnlyList<PlayerTurnNotice> notices) {
        JsonObject value = JsonNode.Parse(CreateInput(notices).JsonValue.GetRawText())!.AsObject();
        value.Remove("connectionState");
        return Content(value, GalateaObservationSchema.V1SchemaId);
    }

    private static PlayerTurnNotice[] Receipts(bool compact) {
        ActionReceiptBatch mail = new MailReceiptBatch(ActionReceiptContentTests.Source(1), [
            new(ActionReceiptContentTests.Dispatch(1), "accepted", ActionReceiptPreview.Create(new string('R', 100)), ActionReceiptPreview.Create(LongBody)),
            new(ActionReceiptContentTests.Dispatch(2), "unrouted", "unknown", "short")
        ]);
        ActionReceiptBatch note = ActionReceiptContentTests.Note([new NoteReceiptItem(MemoId.Parse("m1:00000001"), ActionReceiptPreview.Create(LongBody))]);
        return [new PlayerTurnNotice.ActionReceipt(compact ? mail.Compact() : mail), new PlayerTurnNotice.ActionReceipt(compact ? note.Compact() : note)];
    }
    private static SessionInputContent Content(JsonObject value, string schema = GalateaObservationSchema.V5SchemaId)
        => SessionInputContent.Structured(schema, JsonSerializer.SerializeToElement(value));
}
