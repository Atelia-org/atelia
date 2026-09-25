using Atelia.Galatea.Input;
using GalateaMemoRecallSourceIdCodec = Atelia.Galatea.Server.CharacterMemory.GalateaMemoRecallSourceIdCodec;
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
    // Five IDs, four option names, source address and evidence; JSON may escape each
    // input byte as six ASCII bytes. Reserve before claiming a new reply lease.
    internal const int MaximumConnectionStateJsonUtf8Bytes = (5 * 128 + 4 * 4096 + 256 + 2048) * 6 + 1024;
    internal const string V1SchemaId = GalateaObservationSchema.V1SchemaId;
    internal const string V2SchemaId = GalateaObservationSchema.V2SchemaId;
    internal const string V3SchemaId = GalateaObservationSchema.V3SchemaId;
    internal const string V4SchemaId = GalateaObservationSchema.V4SchemaId;
    internal const int MaximumContentUtf8Bytes = GalateaObservationLimits.MaximumContentUtf8Bytes;
    internal static GalateaSenderSnapshot RuntimeSender { get; } = new("runtime", "galatea", "Galatea runtime");

    internal static SessionInputContent CreatePlayerAction(GalateaSenderSnapshot sender, string text, DateTimeOffset timestamp) {
        ArgumentNullException.ThrowIfNull(sender);
        if (sender.Kind != "player") { throw new ArgumentException("Player action requires a Player identity.", nameof(sender)); }
        if (GalateaHttpV1.ValidateMessage(text) is { } error) { throw new ArgumentException(error, nameof(text)); }
        if (timestamp.Ticks % TimeSpan.TicksPerSecond != 0) {
            throw new ArgumentException("Observation time must be truncated to whole seconds.", nameof(timestamp));
        }
        return Encode(V1SchemaId, "player-action", sender, timestamp, new { text }, [], []);
    }

    internal static SessionInputContent Create(
        GalateaFreshInput fresh, DateTimeOffset timestamp, GalateaSenderSnapshot character,
        IReadOnlyList<PlayerTurnNotice>? notices = null, IReadOnlyList<PlayerTurnRecall>? recalls = null,
        GalateaConnectionStateSnapshot? connectionState = null
    ) {
        ArgumentNullException.ThrowIfNull(fresh);
        if (character.Kind != "character") { throw new ArgumentException("A target Character snapshot is required.", nameof(character)); }
        IReadOnlyList<PlayerTurnNotice> selected = notices ?? fresh switch {
            GalateaFreshInput.PlayerAction player => player.Notices,
            GalateaFreshInput.DelegateReply reply => reply.Notices,
            _ => []
        };
        return fresh switch {
            GalateaFreshInput.PlayerAction player => Encode(connectionState is null ? V1SchemaId : V4SchemaId, "player-action", player.Sender, timestamp,
                new { text = player.Text }, selected, recalls ?? [], connectionState),
            GalateaFreshInput.HeartbeatActivation heartbeat => Encode(connectionState is null ? V2SchemaId : V4SchemaId, "heartbeat-activation", RuntimeSender, timestamp,
                new { character = SenderJson(character), externalIntervalMinutes = heartbeat.IntervalMinutes }, selected, recalls ?? [], connectionState),
            GalateaFreshInput.DelegateReply => Encode(connectionState is null ? V1SchemaId : V4SchemaId, "delegate-reply", RuntimeSender, timestamp,
                new { }, selected, recalls ?? [], connectionState),
            GalateaFreshInput.InboundMail mail => Encode(connectionState is null ? V1SchemaId : V4SchemaId, "inbound-mail",
                mail.Sender ?? mail.InjectedBy ?? throw new InvalidDataException("Inbound mail requires its accepted sender identity."),
                timestamp, new {
                    messageId = mail.Message.MessageId, from = mail.Message.From, to = mail.Message.To,
                    subject = mail.Message.Subject, body = mail.Message.Body,
                    injectedBy = mail.InjectedBy is null ? null : SenderJson(mail.InjectedBy)
                }, selected, recalls ?? [], connectionState),
            _ => throw new NotSupportedException("Unsupported Galatea fresh input.")
        };
    }

    private static SessionInputContent Encode(string schemaId, string kind, GalateaSenderSnapshot sender, DateTimeOffset timestamp,
        object action, IReadOnlyList<PlayerTurnNotice> notices, IReadOnlyList<PlayerTurnRecall> recalls,
        GalateaConnectionStateSnapshot? connectionState = null) {
        object[] noticeValues = notices.Select(NoticeJson).ToArray();
        object[] recallValues = recalls.Select(RecallJson).ToArray();
        JsonElement value = connectionState is null
            ? JsonSerializer.SerializeToElement(new {
                v = 1, kind, sender = SenderJson(sender),
                externalLocalTimestamp = timestamp.ToString("O", CultureInfo.InvariantCulture), action,
                notices = noticeValues, recalls = recallValues
            })
            : JsonSerializer.SerializeToElement(new {
                v = 1, kind, sender = SenderJson(sender),
                externalLocalTimestamp = timestamp.ToString("O", CultureInfo.InvariantCulture), action,
                notices = noticeValues, recalls = recallValues,
                connectionState = new {
                    runtimeOverrideConnectionId = connectionState.RuntimeOverrideConnectionId,
                    effectiveConnectionId = connectionState.EffectiveConnectionId,
                    turnConnectionId = connectionState.TurnConnectionId,
                    effectiveName = connectionState.EffectiveName,
                    turnName = connectionState.TurnName,
                    lastChange = connectionState.LastChange is { } change ? new {
                        sourceActionAddress = change.SourceActionAddress,
                        previousConnectionId = change.PreviousConnectionId,
                        connectionId = change.ConnectionId,
                        name = change.Name,
                        previousName = change.PreviousName,
                        evidence = change.Evidence
                    } : null
                }
            });
        Validate(schemaId, value);
        return SessionInputContent.Structured(schemaId, value);
    }

    private static object SenderJson(GalateaSenderSnapshot sender) => new { kind = sender.Kind, id = sender.Id, name = sender.Name };

    internal static bool FitsEveryValidPlayerText(IReadOnlyList<PlayerTurnNotice> notices, bool reserveConnectionState = false) {
        if (notices.Count > PlayerTurnObservationEnvelope.MaximumNoticeCount) { return false; }
        JsonElement value = JsonSerializer.SerializeToElement(notices.Select(NoticeJson).ToArray());
        // JSON can encode one control character in six bytes. Reserve the complete
        // admitted Player text budget and bounded wrapper/identity fields.
        return GalateaBoundedJson.StrictUtf8.GetByteCount(value.GetRawText())
            + GalateaHttpV1.MaximumMessageUtf8Bytes * 6L + 16 * 1024
            + (reserveConnectionState ? MaximumConnectionStateJsonUtf8Bytes : 0) <= MaximumContentUtf8Bytes;
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

    internal static bool IsSupportedSchemaId(string? schemaId) => GalateaObservationSchema.IsSupportedSchemaId(schemaId);
    internal static void Validate(string? schemaId, JsonElement value) => GalateaObservationSchema.Validate(schemaId, value);
    internal static void Validate(SessionInputContent content) {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.IsStructured) { throw new InvalidDataException("Expected structured Galatea Observation content."); }
        Validate(content.SchemaId, content.JsonValue);
    }

    internal static PlayerTurnNotice ReadNotice(JsonElement value) {
        string kind = GalateaObservationSchema.ValidateNotice(value);
        return kind switch {
            "reply" => new PlayerTurnNotice.Reply(value.GetProperty("body").GetString()!,
                GalateaInputContentValidation.ReadSender(value.GetProperty("sender")), value.GetProperty("dispatchId").GetString()!,
                NullableString(value, "threadId"), NullableString(value, "turnId"), NullableString(value, "noticeId")),
            "delivery-failure" => new PlayerTurnNotice.DeliveryFailure(value.GetProperty("code").GetString()!, NullableString(value, "detail"),
                GalateaInputContentValidation.ReadSender(value.GetProperty("sender")), value.GetProperty("dispatchId").GetString()!,
                NullableString(value, "stage"), NullableString(value, "threadId"), NullableString(value, "turnId"), NullableString(value, "noticeId")),
            "note-save-receipt" => new PlayerTurnNotice.NoteSaveReceipt(ReadReceipt(value.GetProperty("receipt"))),
            "legacy-reply" => PlayerTurnNotice.Reply.FromLegacyDurable(value.GetProperty("body").GetString()!, NullableString(value, "dispatchId"),
                NullableString(value, "threadId"), NullableString(value, "turnId"), NullableString(value, "noticeId")),
            "legacy-delivery-failure" => PlayerTurnNotice.DeliveryFailure.FromLegacyDurable(value.GetProperty("body").GetString()!, NullableString(value, "dispatchId"),
                NullableString(value, "threadId"), NullableString(value, "turnId"), NullableString(value, "noticeId"), NullableString(value, "stage"), NullableString(value, "code")),
            "legacy-note-save-receipt" => PlayerTurnNotice.NoteSaveReceipt.FromLegacyDurable(value.GetProperty("body").GetString()!, NullableString(value, "sourceActionAddress")),
            _ => throw new InvalidDataException("Unknown notice kind.")
        };
    }

    internal static CharacterNoteReceiptSelection ReadReceipt(JsonElement value) {
        GalateaObservationSchema.ValidateReceipt(value);
        return new CharacterNoteReceiptSelection(value.GetProperty("sourceActionAddress").GetString()!,
            value.GetProperty("saved").EnumerateArray().Select(item => MemoId.Parse(item.GetProperty("memoId").GetString()!)).ToArray(),
            value.GetProperty("exactTexts").EnumerateArray().Select(item => item.GetString()!).ToArray());
    }

    private static PlayerTurnRecall ReadRecall(JsonElement value) {
        var (kind, source) = GalateaObservationSchema.ValidateRecall(value);
        return kind switch {
            "memo-gist" or "memo-summary" => PlayerTurnRecall.FromText(new RecallEntry(kind == "memo-gist" ? RecallType.MemoGist : RecallType.MemoSummary, source),
                value.GetProperty("sourceVersion").GetString()!, value.GetProperty("text").GetString()!),
            _ => new PlayerTurnRecall(new RecallEntry(RecallType.MemoExactText, source), value.GetProperty("podStateIdentity").GetString()!,
                value.GetProperty("title").GetString()!, value.GetProperty("exactText").GetString()!)
        };
    }

    private static MailboxMessage ReadMailbox(JsonElement action) {
        GalateaObservationSchema.ValidateMailbox(action);
        return MailboxMessage.FromCanonicalEnvelope(action.GetProperty("messageId").GetString()!, action.GetProperty("from").GetString()!,
            action.GetProperty("to").GetString()!, NullableString(action, "subject"), action.GetProperty("body").GetString()!);
    }

    internal static bool TryReadPlayerText(SessionInputContent content, out string text) {
        text = string.Empty;
        if (!content.IsStructured || !IsSupportedSchemaId(content.SchemaId)) { return false; }
        Validate(content);
        if (content.JsonValue.GetProperty("kind").GetString() != "player-action") { return false; }
        text = content.JsonValue.GetProperty("action").GetProperty("text").GetString()!;
        return true;
    }

    internal static PlayerTurnObservation ReadPlayerTurn(SessionInputContent content) {
        if (!content.IsStructured) {
            return PlayerTurnObservationEnvelope.TryUnwrap(content.TextValue, out PlayerTurnObservation old)
                ? old : throw new InvalidDataException("Unsupported legacy player-turn Observation.");
        }
        if (!IsSupportedSchemaId(content.SchemaId)) { throw new InvalidDataException("Unsupported Observation schema."); }
        JsonElement value = content.JsonValue;
        Validate(content);
        DateTimeOffset timestamp = DateTimeOffset.ParseExact(value.GetProperty("externalLocalTimestamp").GetString()!, "O", CultureInfo.InvariantCulture);
        PlayerTurnNotice[] notices = value.GetProperty("notices").EnumerateArray().Select(ReadNotice).ToArray();
        PlayerTurnRecall[] recalls = value.GetProperty("recalls").EnumerateArray().Select(ReadRecall).ToArray();
        JsonElement action = value.GetProperty("action");
        return value.GetProperty("kind").GetString() switch {
            "player-action" => new(action.GetProperty("text").GetString()!, timestamp, notices, recalls),
            "heartbeat-activation" => PlayerTurnObservation.CreateHeartbeatActivation(timestamp,
                new GalateaCharacterName(action.GetProperty("character").GetProperty("name").GetString()!), notices, recalls,
                action.GetProperty("externalIntervalMinutes").GetInt32()),
            "delegate-reply" => PlayerTurnObservation.CreateDelegateReply(timestamp, notices, recalls),
            _ => throw new InvalidDataException("Inbound mail is not a player-turn Observation.")
        };
    }

    internal static GalateaConnectionStateSnapshot? ReadConnectionState(SessionInputContent content) {
        if (!content.IsStructured || !IsSupportedSchemaId(content.SchemaId)) { return null; }
        Validate(content);
        if (content.SchemaId is not (V3SchemaId or V4SchemaId)) { return null; }
        JsonElement state = content.JsonValue.GetProperty("connectionState");
        JsonElement change = state.GetProperty("lastChange");
        return new GalateaConnectionStateSnapshot(
            NullableString(state, "runtimeOverrideConnectionId"),
            state.GetProperty("effectiveConnectionId").GetString()!,
            state.GetProperty("turnConnectionId").GetString()!,
            change.ValueKind == JsonValueKind.Null ? null : new GalateaConnectionStateChange(
                change.GetProperty("sourceActionAddress").GetString()!,
                change.GetProperty("previousConnectionId").GetString()!,
                change.GetProperty("connectionId").GetString()!,
                change.GetProperty("name").GetString()!,
                change.GetProperty("evidence").GetString()!,
                content.SchemaId == V4SchemaId ? change.GetProperty("previousName").GetString()! : ""),
            content.SchemaId == V4SchemaId ? state.GetProperty("effectiveName").GetString()! : "",
            content.SchemaId == V4SchemaId ? state.GetProperty("turnName").GetString()! : "");
    }

    internal static string DisplayText(SessionInputContent content) {
        Validate(content);
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
        if (content.SchemaId is not (V1SchemaId or V3SchemaId or V4SchemaId)) { throw new InvalidDataException("Unsupported mailbox schema."); }
        Validate(content);
        if (content.JsonValue.GetProperty("kind").GetString() != "inbound-mail") { throw new InvalidDataException("Expected inbound-mail input."); }
        return ReadMailbox(content.JsonValue.GetProperty("action"));
    }

    private static string? NullableString(JsonElement value, string field) => value.GetProperty(field).GetString();
}
