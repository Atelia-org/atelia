using System.Globalization;
using System.Text.Json;
using Atelia.EventJournal;
using Atelia.Galatea.Prompts;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.Galatea.Server.Mailbox;
using Atelia.MemoPod;
using Atelia.SessionJournal;

namespace Atelia.Galatea.Server;

/// <summary>Stable input facts. Encoding, decoding and proofs do not invoke an LLM renderer.</summary>
internal static class GalateaObservationContent {
    internal const string SchemaId = "galatea.observation.v1";
    internal const int MaximumContentUtf8Bytes = 1024 * 1024;
    internal static GalateaSenderSnapshot RuntimeSender { get; } = new("runtime", "galatea", "Galatea runtime");

    internal static SessionInputContent CreatePlayerAction(GalateaSenderSnapshot sender, string text, DateTimeOffset timestamp) {
        ArgumentNullException.ThrowIfNull(sender);
        if (sender.Kind != "player") { throw new ArgumentException("Player action requires a Player identity.", nameof(sender)); }
        if (GalateaHttpV1.ValidateMessage(text) is { } error) { throw new ArgumentException(error, nameof(text)); }
        if (timestamp.Ticks % TimeSpan.TicksPerSecond != 0) {
            throw new ArgumentException("Observation time must be truncated to whole seconds.", nameof(timestamp));
        }
        return Encode("player-action", sender, timestamp, new { text }, [], []);
    }

    internal static SessionInputContent Create(
        GalateaFreshInput fresh, DateTimeOffset timestamp, GalateaSenderSnapshot character,
        IReadOnlyList<PlayerTurnNotice>? notices = null, IReadOnlyList<PlayerTurnRecall>? recalls = null
    ) {
        ArgumentNullException.ThrowIfNull(fresh);
        if (character.Kind != "character") { throw new ArgumentException("A target Character snapshot is required.", nameof(character)); }
        IReadOnlyList<PlayerTurnNotice> selected = notices ?? fresh switch {
            GalateaFreshInput.PlayerAction player => player.Notices,
            GalateaFreshInput.DelegateReply reply => reply.Notices,
            _ => []
        };
        return fresh switch {
            GalateaFreshInput.PlayerAction player => Encode("player-action", player.Sender, timestamp,
                new { text = player.Text }, selected, recalls ?? []),
            GalateaFreshInput.HeartbeatActivation => Encode("heartbeat-activation", RuntimeSender, timestamp,
                new { character = SenderJson(character), externalIntervalMinutes = GalateaFreshInput.HeartbeatActivation.ExternalIntervalMinutes }, selected, recalls ?? []),
            GalateaFreshInput.DelegateReply => Encode("delegate-reply", RuntimeSender, timestamp,
                new { }, selected, recalls ?? []),
            GalateaFreshInput.InboundMail mail => Encode("inbound-mail",
                mail.Sender ?? mail.InjectedBy ?? throw new InvalidDataException("Inbound mail requires its accepted sender identity."),
                timestamp, new {
                    messageId = mail.Message.MessageId, from = mail.Message.From, to = mail.Message.To,
                    subject = mail.Message.Subject, body = mail.Message.Body,
                    injectedBy = mail.InjectedBy is null ? null : SenderJson(mail.InjectedBy)
                }, selected, recalls ?? []),
            _ => throw new NotSupportedException("Unsupported Galatea fresh input.")
        };
    }

    private static SessionInputContent Encode(string kind, GalateaSenderSnapshot sender, DateTimeOffset timestamp,
        object action, IReadOnlyList<PlayerTurnNotice> notices, IReadOnlyList<PlayerTurnRecall> recalls) {
        JsonElement value = JsonSerializer.SerializeToElement(new {
            v = 1, kind, sender = SenderJson(sender),
            externalLocalTimestamp = timestamp.ToString("O", CultureInfo.InvariantCulture), action,
            notices = notices.Select(NoticeJson).ToArray(), recalls = recalls.Select(RecallJson).ToArray()
        });
        Validate(value);
        return SessionInputContent.Structured(SchemaId, value);
    }

    private static object SenderJson(GalateaSenderSnapshot sender) => new { kind = sender.Kind, id = sender.Id, name = sender.Name };

    internal static bool FitsEveryValidPlayerText(IReadOnlyList<PlayerTurnNotice> notices) {
        if (notices.Count > PlayerTurnObservationEnvelope.MaximumNoticeCount) { return false; }
        JsonElement value = JsonSerializer.SerializeToElement(notices.Select(NoticeJson).ToArray());
        // JSON can encode one control character in six bytes. Reserve the complete
        // admitted Player text budget and bounded wrapper/identity fields.
        return GalateaBoundedJson.StrictUtf8.GetByteCount(value.GetRawText())
            + GalateaHttpV1.MaximumMessageUtf8Bytes * 6L + 16 * 1024 <= MaximumContentUtf8Bytes;
    }

    internal static bool FitsPlayerTurnContent(PlayerTurnObservation observation) {
        JsonElement value = JsonSerializer.SerializeToElement(new {
            text = observation.TriggerKind == PlayerTurnObservationTriggerKind.PlayerAction ? observation.PlayerText : null,
            notices = observation.Notices.Select(NoticeJson).ToArray(),
            recalls = observation.Recalls.Select(RecallJson).ToArray()
        });
        return GalateaBoundedJson.StrictUtf8.GetByteCount(value.GetRawText()) + 16 * 1024 <= MaximumContentUtf8Bytes;
    }

    internal static object NoticeJson(PlayerTurnNotice notice) => notice switch {
        PlayerTurnNotice.Reply { Sender: { } sender } reply => new {
            kind = "reply", sender = SenderJson(sender), dispatchId = reply.DispatchId,
            threadId = reply.ThreadId, turnId = reply.TurnId, noticeId = reply.NoticeId, body = reply.Body
        },
        PlayerTurnNotice.DeliveryFailure { Sender: { } sender } failure => new {
            kind = "delivery-failure", sender = SenderJson(sender), dispatchId = failure.DispatchId,
            code = failure.Code, detail = failure.Detail, stage = failure.Stage,
            threadId = failure.ThreadId, turnId = failure.TurnId, noticeId = failure.NoticeId
        },
        PlayerTurnNotice.NoteSaveReceipt { Selection: { } selection } => new {
            kind = "note-save-receipt", sender = SenderJson(RuntimeSender), receipt = selection.ToJson()
        },
        PlayerTurnNotice.Reply { IsLegacyDurable: true } reply => new { kind = "legacy-reply", body = reply.Body,
            dispatchId = reply.DispatchId, threadId = reply.ThreadId, turnId = reply.TurnId, noticeId = reply.NoticeId },
        PlayerTurnNotice.DeliveryFailure { IsLegacyDurable: true } failure => new { kind = "legacy-delivery-failure", body = failure.Body,
            dispatchId = failure.DispatchId, threadId = failure.ThreadId, turnId = failure.TurnId, noticeId = failure.NoticeId,
            stage = failure.Stage, code = failure.Code },
        PlayerTurnNotice.NoteSaveReceipt { IsLegacyDurable: true } receipt => new { kind = "legacy-note-save-receipt", body = receipt.Body,
            sourceActionAddress = receipt.LegacySourceActionAddress },
        _ => throw new InvalidDataException("A new notice must contain semantic provenance; only explicitly imported durable legacy notices may carry old text.")
    };

    private static object RecallJson(PlayerTurnRecall recall) {
        if (!recall.IsLegacy && recall.Entry.RecallType is RecallType.MemoGist or RecallType.MemoSummary) {
            return new { kind = recall.Entry.RecallType == RecallType.MemoGist ? "memo-gist" : "memo-summary",
                sourceId = recall.Entry.SourceId, sourceVersion = recall.SourceVersion, text = recall.ContentText };
        }
        if (recall.IsLegacy || !GalateaMemoRecallSourceIdCodec.TryParse(recall.Entry.SourceId, out MemoPodId pod, out MemoId memo)) {
            throw new InvalidDataException("New recall requires immutable source, title and exactText.");
        }
        return new {
            kind = "memo-exact-text", sourceId = recall.Entry.SourceId,
            podId = pod.Value, memoId = memo.Value, podStateIdentity = recall.PodStateIdentity,
            title = recall.Title, exactText = recall.ExactText
        };
    }

    internal static void Validate(JsonElement value) {
        GalateaInputContentValidation.RequireObject(value, "v", "kind", "sender", "externalLocalTimestamp", "action", "notices", "recalls");
        GalateaInputContentValidation.RequireVersion(value);
        GalateaSenderSnapshot sender = GalateaInputContentValidation.ReadSender(value.GetProperty("sender"));
        string timestamp = Text(value, "externalLocalTimestamp", 128);
        if (!DateTimeOffset.TryParseExact(timestamp, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset time)
            || time.Ticks % TimeSpan.TicksPerSecond != 0) { throw new InvalidDataException("Invalid Observation timestamp."); }
        string kind = Text(value, "kind", 64);
        JsonElement action = value.GetProperty("action");
        JsonElement notices = value.GetProperty("notices");
        JsonElement recalls = value.GetProperty("recalls");
        if (notices.ValueKind != JsonValueKind.Array || notices.GetArrayLength() > PlayerTurnObservationEnvelope.MaximumNoticeCount
            || recalls.ValueKind != JsonValueKind.Array || recalls.GetArrayLength() > PlayerTurnObservationEnvelope.MaximumRecallCount) {
            throw new InvalidDataException("Invalid Observation content arrays.");
        }
        switch (kind) {
            case "player-action":
                RequireSenderKind(sender, "player");
                GalateaInputContentValidation.RequireObject(action, "text");
                if (GalateaHttpV1.ValidateMessage(Text(action, "text", MaximumContentUtf8Bytes)) is { } error) { throw new InvalidDataException(error); }
                break;
            case "heartbeat-activation":
                RequireSenderKind(sender, "runtime");
                GalateaInputContentValidation.RequireObject(action, "character", "externalIntervalMinutes");
                RequireSenderKind(GalateaInputContentValidation.ReadSender(action.GetProperty("character")), "character");
                if (!action.GetProperty("externalIntervalMinutes").TryGetInt32(out int minutes)
                    || minutes != GalateaFreshInput.HeartbeatActivation.ExternalIntervalMinutes) {
                    throw new InvalidDataException("Unsupported heartbeat activation interval.");
                }
                break;
            case "delegate-reply":
                RequireSenderKind(sender, "runtime");
                GalateaInputContentValidation.RequireObject(action);
                break;
            case "inbound-mail":
                if (sender.Kind is not ("player" or "character")) { throw new InvalidDataException("Inbound sender must be a Player or Character."); }
                _ = ReadMailbox(action);
                JsonElement injector = action.GetProperty("injectedBy");
                if (injector.ValueKind != JsonValueKind.Null) {
                    GalateaSenderSnapshot acceptedBy = GalateaInputContentValidation.ReadSender(injector);
                    RequireSenderKind(acceptedBy, "player");
                    if (sender != acceptedBy) { throw new InvalidDataException("HTTP mail sender must identify its accepting Player, independently of declared from."); }
                }
                else { RequireSenderKind(sender, "character"); }
                if (notices.GetArrayLength() != 0 || recalls.GetArrayLength() != 0) { throw new InvalidDataException("Inbound mail does not carry player-turn enrichment."); }
                break;
            default: throw new InvalidDataException("Unknown Observation kind.");
        }
        int receiptCount = 0;
        int externalCount = 0;
        int index = 0;
        foreach (JsonElement notice in notices.EnumerateArray()) {
            PlayerTurnNotice parsed = ReadNotice(notice);
            if (parsed is PlayerTurnNotice.NoteSaveReceipt) {
                receiptCount++;
                if (index != notices.GetArrayLength() - 1 || receiptCount > 1) { throw new InvalidDataException("Receipt must be the single final notice."); }
            }
            else { externalCount++; }
            index++;
        }
        if (kind == "heartbeat-activation" && externalCount != 0 || kind == "delegate-reply" && externalCount == 0) {
            throw new InvalidDataException("Notice kinds do not match the trigger.");
        }
        var sources = new HashSet<RecallEntry>();
        foreach (JsonElement recall in recalls.EnumerateArray()) {
            if (!sources.Add(ReadRecall(recall).Entry)) { throw new InvalidDataException("Duplicate recall source."); }
        }
        if (GalateaBoundedJson.StrictUtf8.GetByteCount(value.GetRawText()) > MaximumContentUtf8Bytes) {
            throw new ArgumentOutOfRangeException(nameof(value), "Structured Observation exceeds its machine-content byte limit.");
        }
    }

    internal static PlayerTurnNotice ReadNotice(JsonElement value) {
        string kind = Text(value, "kind", 64);
        switch (kind) {
            case "reply":
                GalateaInputContentValidation.RequireObject(value, "kind", "sender", "dispatchId", "threadId", "turnId", "noticeId", "body");
                return new PlayerTurnNotice.Reply(Text(value, "body", PlayerTurnObservationEnvelope.MaximumReplyUtf8Bytes),
                    GalateaInputContentValidation.ReadSender(value.GetProperty("sender")), Text(value, "dispatchId", 1024),
                    OptionalText(value, "threadId", 1024), OptionalText(value, "turnId", 1024), OptionalText(value, "noticeId", 1024));
            case "delivery-failure":
                GalateaInputContentValidation.RequireObject(value, "kind", "sender", "dispatchId", "code", "detail", "stage", "threadId", "turnId", "noticeId");
                return new PlayerTurnNotice.DeliveryFailure(Text(value, "code", 128), OptionalText(value, "detail", PlayerTurnObservationEnvelope.MaximumFailureUtf8Bytes),
                    GalateaInputContentValidation.ReadSender(value.GetProperty("sender")), Text(value, "dispatchId", 1024),
                    OptionalText(value, "stage", 128), OptionalText(value, "threadId", 1024), OptionalText(value, "turnId", 1024), OptionalText(value, "noticeId", 1024));
            case "note-save-receipt":
                GalateaInputContentValidation.RequireObject(value, "kind", "sender", "receipt");
                RequireSenderKind(GalateaInputContentValidation.ReadSender(value.GetProperty("sender")), "runtime");
                return new PlayerTurnNotice.NoteSaveReceipt(ReadReceipt(value.GetProperty("receipt")));
            case "legacy-reply":
            case "legacy-delivery-failure":
                if (kind == "legacy-reply") { GalateaInputContentValidation.RequireObject(value, "kind", "body", "dispatchId", "threadId", "turnId", "noticeId"); }
                else { GalateaInputContentValidation.RequireObject(value, "kind", "body", "dispatchId", "threadId", "turnId", "noticeId", "stage", "code"); }
                string body = Text(value, "body", PlayerTurnObservationEnvelope.MaximumNoteSaveReceiptUtf8Bytes);
                return kind == "legacy-reply"
                    ? PlayerTurnNotice.Reply.FromLegacyDurable(body, OptionalText(value, "dispatchId", 1024), OptionalText(value, "threadId", 1024), OptionalText(value, "turnId", 1024), OptionalText(value, "noticeId", 1024))
                    : PlayerTurnNotice.DeliveryFailure.FromLegacyDurable(body, OptionalText(value, "dispatchId", 1024), OptionalText(value, "threadId", 1024), OptionalText(value, "turnId", 1024), OptionalText(value, "noticeId", 1024), OptionalText(value, "stage", 128), OptionalText(value, "code", 128));
            case "legacy-note-save-receipt":
                GalateaInputContentValidation.RequireObject(value, "kind", "body", "sourceActionAddress");
                string? source = OptionalText(value, "sourceActionAddress", 256);
                if (source is not null) { _ = EventAddressTextCodec.Parse(source); }
                return PlayerTurnNotice.NoteSaveReceipt.FromLegacyDurable(Text(value, "body", PlayerTurnObservationEnvelope.MaximumNoteSaveReceiptUtf8Bytes), source);
            default: throw new InvalidDataException("Unknown notice kind.");
        }
    }

    internal static CharacterNoteReceiptSelection ReadReceipt(JsonElement value) {
        GalateaInputContentValidation.RequireObject(value, "sourceActionAddress", "podId", "saved", "exactTexts");
        string source = Text(value, "sourceActionAddress", 256);
        _ = EventAddressTextCodec.Parse(source);
        if (Text(value, "podId", 64) != CharacterNoteDefaultPodV1.PodIdText) { throw new InvalidDataException("Unsupported receipt pod."); }
        JsonElement ids = value.GetProperty("saved");
        JsonElement texts = value.GetProperty("exactTexts");
        if (ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() is < 1 or > CharacterNoteBounds.MaximumIntentCount
            || texts.ValueKind != JsonValueKind.Array || texts.GetArrayLength() != 0 && texts.GetArrayLength() != ids.GetArrayLength()) {
            throw new InvalidDataException("Receipt must contain all identities and either all texts or no texts.");
        }
        bool full = texts.GetArrayLength() != 0;
        var memos = new List<MemoId>();
        var selectedTexts = new List<string>();
        int ordinal = 0;
        foreach (JsonElement id in ids.EnumerateArray()) {
            GalateaInputContentValidation.RequireObject(id, "ordinal", "memoId");
            if (id.GetProperty("ordinal").GetInt32() != ordinal) { throw new InvalidDataException("Receipt ordinal is invalid."); }
            if (full) { selectedTexts.Add(texts[ordinal].GetString() ?? throw new InvalidDataException("Receipt text is null.")); }
            memos.Add(MemoId.Parse(Text(id, "memoId", 64)));
            ordinal++;
        }
        return new CharacterNoteReceiptSelection(source, memos, selectedTexts);
    }

    private static PlayerTurnRecall ReadRecall(JsonElement value) {
        string kind = Text(value, "kind", 64);
        if (kind is "memo-gist" or "memo-summary") {
            GalateaInputContentValidation.RequireObject(value, "kind", "sourceId", "sourceVersion", "text");
            return PlayerTurnRecall.FromText(new RecallEntry(kind == "memo-gist" ? RecallType.MemoGist : RecallType.MemoSummary,
                Text(value, "sourceId", PlayerTurnObservationEnvelope.MaximumRecallSourceIdUtf8Bytes)),
                Text(value, "sourceVersion", 256), Text(value, "text", PlayerTurnObservationEnvelope.MaximumRecallBodyUtf8Bytes));
        }
        GalateaInputContentValidation.RequireObject(value, "kind", "sourceId", "podId", "memoId", "podStateIdentity", "title", "exactText");
        if (Text(value, "kind", 64) != "memo-exact-text") { throw new InvalidDataException("Unsupported recall kind."); }
        string source = Text(value, "sourceId", PlayerTurnObservationEnvelope.MaximumRecallSourceIdUtf8Bytes);
        if (!GalateaMemoRecallSourceIdCodec.TryParse(source, out MemoPodId pod, out MemoId memo)
            || pod.Value != Text(value, "podId", 64) || memo.Value != Text(value, "memoId", 64)) {
            throw new InvalidDataException("Recall source components disagree.");
        }
        return new(new RecallEntry(RecallType.MemoExactText, source), Text(value, "podStateIdentity", 256),
            Text(value, "title", MemoPodLimits.MaximumMemoTitleUtf8Bytes), Text(value, "exactText", MemoPodLimits.MaximumMemoExactTextUtf8Bytes));
    }

    private static MailboxMessage ReadMailbox(JsonElement action) {
        GalateaInputContentValidation.RequireObject(action, "messageId", "from", "to", "subject", "body", "injectedBy");
        return MailboxMessage.FromCanonicalEnvelope(Text(action, "messageId", 64), Text(action, "from", 1024),
            Text(action, "to", 1024), OptionalText(action, "subject", GalateaMailboxBounds.MaximumSubjectUtf8Bytes),
            Text(action, "body", GalateaMailboxBounds.MaximumBodyUtf8Bytes));
    }

    internal static bool TryReadPlayerText(SessionInputContent content, out string text) {
        text = string.Empty;
        if (!content.IsStructured || content.SchemaId != SchemaId) { return false; }
        Validate(content.JsonValue);
        if (content.JsonValue.GetProperty("kind").GetString() != "player-action") { return false; }
        text = content.JsonValue.GetProperty("action").GetProperty("text").GetString()!;
        return true;
    }

    internal static PlayerTurnObservation ReadPlayerTurn(SessionInputContent content) {
        if (!content.IsStructured) {
            return PlayerTurnObservationEnvelope.TryUnwrap(content.TextValue, out PlayerTurnObservation old)
                ? old : throw new InvalidDataException("Unsupported legacy player-turn Observation.");
        }
        if (content.SchemaId != SchemaId) { throw new InvalidDataException("Unsupported Observation schema."); }
        JsonElement value = content.JsonValue;
        Validate(value);
        DateTimeOffset timestamp = DateTimeOffset.ParseExact(value.GetProperty("externalLocalTimestamp").GetString()!, "O", CultureInfo.InvariantCulture);
        PlayerTurnNotice[] notices = value.GetProperty("notices").EnumerateArray().Select(ReadNotice).ToArray();
        PlayerTurnRecall[] recalls = value.GetProperty("recalls").EnumerateArray().Select(ReadRecall).ToArray();
        JsonElement action = value.GetProperty("action");
        return value.GetProperty("kind").GetString() switch {
            "player-action" => new(action.GetProperty("text").GetString()!, timestamp, notices, recalls),
            "heartbeat-activation" => PlayerTurnObservation.CreateHeartbeatActivation(timestamp,
                new GalateaCharacterName(action.GetProperty("character").GetProperty("name").GetString()!), notices, recalls),
            "delegate-reply" => PlayerTurnObservation.CreateDelegateReply(timestamp, notices, recalls),
            _ => throw new InvalidDataException("Inbound mail is not a player-turn Observation.")
        };
    }

    internal static IReadOnlyList<string> ExternalStringPaths(JsonElement value) {
        Validate(value);
        var paths = new List<string>();
        string? kind = value.GetProperty("kind").GetString();
        if (kind == "player-action") { paths.Add("/action/text"); }
        if (kind == "inbound-mail") { paths.Add("/action/body"); }
        int i = 0;
        foreach (JsonElement notice in value.GetProperty("notices").EnumerateArray()) {
            if (notice.TryGetProperty("body", out _)) { paths.Add($"/notices/{i}/body"); }
            if (notice.TryGetProperty("detail", out JsonElement detail) && detail.ValueKind == JsonValueKind.String) { paths.Add($"/notices/{i}/detail"); }
            if (notice.TryGetProperty("receipt", out JsonElement receipt)) {
                for (int j = 0; j < receipt.GetProperty("exactTexts").GetArrayLength(); j++) { paths.Add($"/notices/{i}/receipt/exactTexts/{j}"); }
            }
            i++;
        }
        i = 0;
        foreach (JsonElement recall in value.GetProperty("recalls").EnumerateArray()) {
            if (recall.GetProperty("kind").GetString() == "memo-exact-text") {
                paths.Add($"/recalls/{i}/title"); paths.Add($"/recalls/{i}/exactText");
            }
            else { paths.Add($"/recalls/{i}/text"); }
            i++;
        }
        return paths;
    }

    internal static string DisplayText(SessionInputContent content) {
        Validate(content.JsonValue);
        JsonElement value = content.JsonValue;
        JsonElement action = value.GetProperty("action");
        return value.GetProperty("kind").GetString() == "inbound-mail"
            ? GalateaMailboxObservationEnvelope.FormatForDisplay(ReadMailbox(action))
            : PlayerTurnObservationEnvelope.FormatForDisplay(ReadPlayerTurn(content));
    }

    internal static MailboxMessage ReadMailboxContent(SessionInputContent content) {
        if (!content.IsStructured) {
            return GalateaMailboxObservationEnvelope.TryUnwrap(content.TextValue, out MailboxMessage legacy)
                ? legacy : throw new InvalidDataException("Unsupported legacy mailbox input.");
        }
        if (content.SchemaId != SchemaId) { throw new InvalidDataException("Unsupported mailbox schema."); }
        Validate(content.JsonValue);
        if (content.JsonValue.GetProperty("kind").GetString() != "inbound-mail") { throw new InvalidDataException("Expected inbound-mail input."); }
        return ReadMailbox(content.JsonValue.GetProperty("action"));
    }

    private static string Text(JsonElement value, string field, int bytes) => GalateaInputContentValidation.ReadText(value, field, bytes);
    private static string? OptionalText(JsonElement value, string field, int bytes) => value.GetProperty(field).ValueKind == JsonValueKind.Null ? null : Text(value, field, bytes);
    private static void RequireSenderKind(GalateaSenderSnapshot sender, string expected) {
        if (sender.Kind != expected) { throw new InvalidDataException("Unexpected sender kind."); }
    }
}
