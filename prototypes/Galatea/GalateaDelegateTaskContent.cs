using System.Text.Json;
using Atelia.Galatea.Server.Mailbox;
using Atelia.MdJson;
using Atelia.SessionJournal;

namespace Atelia.Galatea.Server;

/// <summary>Transient assembly from captured mail facts; the rendered task is never stored.</summary>
internal static class GalateaDelegateTaskContent {
    internal const string SchemaId = "galatea.delegate-task.v1";

    internal static SessionInputContent Create(
        GalateaSenderSnapshot sender, string recipient, string? subject,
        string body, string? inReplyToMessageId, string sourceActionAddress,
        string dispatchId
    ) {
        ArgumentNullException.ThrowIfNull(sender);
        JsonElement value = JsonSerializer.SerializeToElement(new {
            v = 1,
            kind = "delegate-task",
            sender = new { kind = sender.Kind, id = sender.Id, name = sender.Name },
            recipient, subject, body, inReplyToMessageId,
            source = new { actionAddress = sourceActionAddress, dispatchId }
        });
        Validate(value);
        return SessionInputContent.Structured(SchemaId, value);
    }

    internal static string Project(JsonElement value) {
        Validate(value);
        return MdJsonSerializer.Write(value, ["/body"]);
    }

    private static void Validate(JsonElement value) {
        GalateaInputContentValidation.RequireObject(value, "v", "kind", "sender", "recipient", "subject", "body", "inReplyToMessageId", "source");
        GalateaInputContentValidation.RequireVersion(value);
        if (value.GetProperty("kind").GetString() != "delegate-task"
            || GalateaInputContentValidation.ReadSender(value.GetProperty("sender")).Kind != "character"
            || value.GetProperty("recipient").GetString() != GalateaDelegateConfigReader.CanonicalRecipient) {
            throw new InvalidDataException("Invalid delegate task kind, sender or recipient.");
        }
        _ = GalateaInputContentValidation.ReadText(value, "body", GalateaMailboxBounds.MaximumBodyUtf8Bytes);
        if (value.GetProperty("subject").ValueKind != JsonValueKind.Null) {
            _ = GalateaInputContentValidation.ReadText(value, "subject", GalateaMailboxBounds.MaximumSubjectUtf8Bytes);
        }
        if (value.GetProperty("inReplyToMessageId").ValueKind != JsonValueKind.Null) {
            _ = GalateaInputContentValidation.ReadText(value, "inReplyToMessageId", 512);
        }
        JsonElement source = value.GetProperty("source");
        GalateaInputContentValidation.RequireObject(source, "actionAddress", "dispatchId");
        if (!EventAddressTextCodec.TryParse(source.GetProperty("actionAddress").GetString(), out _)) {
            throw new InvalidDataException("Invalid delegate task source Action address.");
        }
        _ = GalateaInputContentValidation.ReadText(source, "dispatchId", 200);
    }
}
