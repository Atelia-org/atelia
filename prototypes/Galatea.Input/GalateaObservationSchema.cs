using System.Globalization;
using System.Text.Json;
using Atelia.EventJournal;
using Atelia.MemoPod;
using Atelia.SessionJournal;

namespace Atelia.Galatea.Input;

/// <summary>The one JSON schema and external-string selection authority for galatea.observation.v1.</summary>
internal static class GalateaObservationSchema {
    internal const string SchemaId = "galatea.observation.v1";
    internal const int MaximumContentUtf8Bytes = GalateaObservationLimits.MaximumContentUtf8Bytes;
    internal static void Validate(JsonElement value) {
        GalateaInputValidation.RequireObject(value, "v", "kind", "sender", "externalLocalTimestamp", "action", "notices", "recalls");
        GalateaInputValidation.RequireVersion(value);
        GalateaInputSource sender = GalateaInputValidation.ReadSender(value.GetProperty("sender"));
        string timestamp = Text(value, "externalLocalTimestamp", 128);
        if (!DateTimeOffset.TryParseExact(timestamp, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset time)
            || time.Ticks % TimeSpan.TicksPerSecond != 0) { throw new InvalidDataException("Invalid Observation timestamp."); }
        string kind = Text(value, "kind", 64);
        JsonElement action = value.GetProperty("action");
        JsonElement notices = value.GetProperty("notices");
        JsonElement recalls = value.GetProperty("recalls");
        if (notices.ValueKind != JsonValueKind.Array || notices.GetArrayLength() > GalateaObservationLimits.MaximumNoticeCount
            || recalls.ValueKind != JsonValueKind.Array || recalls.GetArrayLength() > GalateaObservationLimits.MaximumRecallCount) {
            throw new InvalidDataException("Invalid Observation content arrays.");
        }
        switch (kind) {
            case "player-action":
                RequireSenderKind(sender, "player");
                GalateaInputValidation.RequireObject(action, "text");
                if (GalateaObservationRules.ValidatePlayerText(Text(action, "text", MaximumContentUtf8Bytes)) is { } error) { throw new InvalidDataException(error); }
                break;
            case "heartbeat-activation":
                RequireSenderKind(sender, "runtime");
                GalateaInputValidation.RequireObject(action, "character", "externalIntervalMinutes");
                RequireSenderKind(GalateaInputValidation.ReadSender(action.GetProperty("character")), "character");
                if (!action.GetProperty("externalIntervalMinutes").TryGetInt32(out int minutes)
                    || minutes != GalateaObservationLimits.ExternalIntervalMinutes) {
                    throw new InvalidDataException("Unsupported heartbeat activation interval.");
                }
                break;
            case "delegate-reply":
                RequireSenderKind(sender, "runtime");
                GalateaInputValidation.RequireObject(action);
                break;
            case "inbound-mail":
                if (sender.Kind is not ("player" or "character")) { throw new InvalidDataException("Inbound sender must be a Player or Character."); }
                ValidateMailbox(action);
                JsonElement injector = action.GetProperty("injectedBy");
                if (injector.ValueKind != JsonValueKind.Null) {
                    GalateaInputSource acceptedBy = GalateaInputValidation.ReadSender(injector);
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
            string noticeKind = ValidateNotice(notice);
            if (noticeKind is "note-save-receipt" or "legacy-note-save-receipt") {
                receiptCount++;
                if (index != notices.GetArrayLength() - 1 || receiptCount > 1) { throw new InvalidDataException("Receipt must be the single final notice."); }
            }
            else { externalCount++; }
            index++;
        }
        if (kind == "heartbeat-activation" && externalCount != 0 || kind == "delegate-reply" && externalCount == 0) {
            throw new InvalidDataException("Notice kinds do not match the trigger.");
        }
        var sources = new HashSet<(string Kind, string SourceId)>();
        foreach (JsonElement recall in recalls.EnumerateArray()) {
            if (!sources.Add(ValidateRecall(recall))) { throw new InvalidDataException("Duplicate recall source."); }
        }
        if (GalateaInputValidation.StrictUtf8.GetByteCount(value.GetRawText()) > MaximumContentUtf8Bytes) {
            throw new ArgumentOutOfRangeException(nameof(value), "Structured Observation exceeds its machine-content byte limit.");
        }
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

    internal static string ValidateNotice(JsonElement value) {
        string kind = Text(value, "kind", 64);
        switch (kind) {
            case "reply":
                GalateaInputValidation.RequireObject(value, "kind", "sender", "dispatchId", "threadId", "turnId", "noticeId", "body");
                _ = GalateaInputValidation.ReadSender(value.GetProperty("sender"));
                SingleLine(value, "dispatchId", 1024);
                _ = Text(value, "body", GalateaObservationLimits.MaximumReplyUtf8Bytes);
                ValidateNoticeLocators(value);
                break;
            case "delivery-failure":
                GalateaInputValidation.RequireObject(value, "kind", "sender", "dispatchId", "code", "detail", "stage", "threadId", "turnId", "noticeId");
                _ = GalateaInputValidation.ReadSender(value.GetProperty("sender"));
                SingleLine(value, "dispatchId", 1024);
                SingleLine(value, "code", 128);
                _ = OptionalText(value, "detail", GalateaObservationLimits.MaximumFailureUtf8Bytes);
                _ = OptionalText(value, "stage", 128);
                ValidateNoticeLocators(value);
                break;
            case "note-save-receipt":
                GalateaInputValidation.RequireObject(value, "kind", "sender", "receipt");
                RequireSenderKind(GalateaInputValidation.ReadSender(value.GetProperty("sender")), "runtime");
                ValidateReceipt(value.GetProperty("receipt"));
                break;
            case "legacy-reply":
            case "legacy-delivery-failure":
                if (kind == "legacy-reply") { GalateaInputValidation.RequireObject(value, "kind", "body", "dispatchId", "threadId", "turnId", "noticeId"); }
                else {
                    GalateaInputValidation.RequireObject(value, "kind", "body", "dispatchId", "threadId", "turnId", "noticeId", "stage", "code");
                    _ = OptionalText(value, "stage", 128);
                    _ = OptionalText(value, "code", 128);
                }
                _ = Text(value, "body", kind == "legacy-reply" ? GalateaObservationLimits.MaximumReplyUtf8Bytes : GalateaObservationLimits.MaximumFailureUtf8Bytes);
                _ = OptionalText(value, "dispatchId", 1024);
                ValidateNoticeLocators(value);
                break;
            case "legacy-note-save-receipt":
                GalateaInputValidation.RequireObject(value, "kind", "body", "sourceActionAddress");
                string? source = OptionalText(value, "sourceActionAddress", 256);
                if (source is not null) { _ = EventAddressTextCodec.Parse(source); }
                _ = Text(value, "body", GalateaObservationLimits.MaximumNoteSaveReceiptUtf8Bytes);
                break;
            default: throw new InvalidDataException("Unknown notice kind.");
        }
        return kind;
    }

    internal static void ValidateReceipt(JsonElement value) {
        GalateaInputValidation.RequireObject(value, "sourceActionAddress", "podId", "saved", "exactTexts");
        _ = EventAddressTextCodec.Parse(Text(value, "sourceActionAddress", 256));
        if (Text(value, "podId", 64) != GalateaObservationLimits.DefaultNotePodId) { throw new InvalidDataException("Unsupported receipt pod."); }
        JsonElement ids = value.GetProperty("saved");
        JsonElement texts = value.GetProperty("exactTexts");
        if (ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() is < 1 or > GalateaObservationLimits.MaximumNoteIntentCount
            || texts.ValueKind != JsonValueKind.Array || texts.GetArrayLength() != 0 && texts.GetArrayLength() != ids.GetArrayLength()) {
            throw new InvalidDataException("Receipt must contain all identities and either all texts or no texts.");
        }
        var seen = new HashSet<MemoId>();
        long bytes = 0;
        int ordinal = 0;
        foreach (JsonElement item in ids.EnumerateArray()) {
            GalateaInputValidation.RequireObject(item, "ordinal", "memoId");
            if (item.GetProperty("ordinal").GetInt32() != ordinal) { throw new InvalidDataException("Receipt ordinal is invalid."); }
            if (!seen.Add(MemoId.Parse(Text(item, "memoId", 64)))) { throw new InvalidDataException("Duplicate receipt Memo ID."); }
            if (texts.GetArrayLength() != 0) {
                string text = texts[ordinal].GetString() ?? throw new InvalidDataException("Receipt text is null.");
                GalateaInputValidation.RequireText(text, GalateaObservationLimits.MaximumNoteExactTextUtf8Bytes, nameof(texts));
                bytes += GalateaInputValidation.StrictUtf8.GetByteCount(text);
            }
            ordinal++;
        }
        if (bytes > GalateaObservationLimits.MaximumNoteTotalExactTextUtf8Bytes) {
            throw new InvalidDataException("Selected receipt texts exceed the Applied batch content limit.");
        }
    }

    internal static (string Kind, string SourceId) ValidateRecall(JsonElement value) {
        string kind = Text(value, "kind", 64);
        if (kind is "memo-gist" or "memo-summary") {
            GalateaInputValidation.RequireObject(value, "kind", "sourceId", "sourceVersion", "text");
            SingleLine(value, "sourceId", GalateaObservationLimits.MaximumRecallSourceIdUtf8Bytes);
            SingleLine(value, "sourceVersion", 256);
            _ = Text(value, "text", GalateaObservationLimits.MaximumRecallBodyUtf8Bytes);
            return (kind, value.GetProperty("sourceId").GetString()!);
        }
        GalateaInputValidation.RequireObject(value, "kind", "sourceId", "podId", "memoId", "podStateIdentity", "title", "exactText");
        if (kind != "memo-exact-text") { throw new InvalidDataException("Unsupported recall kind."); }
        string source = Text(value, "sourceId", GalateaObservationLimits.MaximumRecallSourceIdUtf8Bytes);
        if (!GalateaMemoRecallSourceIdCodec.TryParse(source, out MemoPodId pod, out MemoId memo)
            || pod.Value != Text(value, "podId", 64) || memo.Value != Text(value, "memoId", 64)) {
            throw new InvalidDataException("Recall source components disagree.");
        }
        SingleLine(value, "podStateIdentity", 256);
        _ = Text(value, "title", MemoPodLimits.MaximumMemoTitleUtf8Bytes);
        _ = Text(value, "exactText", MemoPodLimits.MaximumMemoExactTextUtf8Bytes);
        return (kind, source);
    }

    internal static void ValidateMailbox(JsonElement action) {
        GalateaInputValidation.RequireObject(action, "messageId", "from", "to", "subject", "body", "injectedBy");
        GalateaObservationRules.ValidateMailbox(Text(action, "messageId", 64), Text(action, "from", 1024),
            Text(action, "to", 1024), OptionalText(action, "subject", GalateaObservationLimits.MaximumMailSubjectUtf8Bytes),
            Text(action, "body", GalateaObservationLimits.MaximumMailBodyUtf8Bytes));
    }

    private static void ValidateNoticeLocators(JsonElement value) {
        foreach (string field in new[] { "threadId", "turnId", "noticeId" }) { _ = OptionalText(value, field, 1024); }
    }
    private static void SingleLine(JsonElement value, string field, int bytes)
        => GalateaInputValidation.RequireText(Text(value, field, bytes), bytes, field, singleLine: true);
    private static string Text(JsonElement value, string field, int bytes) => GalateaInputValidation.ReadText(value, field, bytes);
    private static string? OptionalText(JsonElement value, string field, int bytes) => value.GetProperty(field).ValueKind == JsonValueKind.Null ? null : Text(value, field, bytes);
    private static void RequireSenderKind(GalateaInputSource sender, string expected) {
        if (sender.Kind != expected) { throw new InvalidDataException("Unexpected sender kind."); }
    }
}
