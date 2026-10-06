using System.Text;
using System.Text.Json;
using Atelia.MemoPod;
using Atelia.SessionJournal;

namespace Atelia.Galatea.Input;

/// <summary>The closed action-receipt-v1 content contract, shared by every input consumer.</summary>
internal static class GalateaActionReceiptSchema {
    internal static bool Validate(JsonElement value) {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("kind", out JsonElement discriminator)
            || discriminator.ValueKind != JsonValueKind.String) {
            throw new InvalidDataException("Action receipt must be an object with a string kind.");
        }
        string kind = GalateaInputValidation.ReadText(value, "kind", 32);
        if (kind == "mail") {
            GalateaInputValidation.RequireObject(value, "kind", "sourceActionAddress", "items");
        }
        else if (kind == "note-save") {
            GalateaInputValidation.RequireObject(value, "kind", "sourceActionAddress", "podId", "items");
            if (GalateaInputValidation.ReadText(value, "podId", 64) != GalateaObservationLimits.DefaultNotePodId) {
                throw new InvalidDataException("Unsupported action receipt pod.");
            }
        }
        else { throw new InvalidDataException("Unknown action receipt kind."); }
        _ = EventAddressTextCodec.Parse(GalateaInputValidation.ReadText(value, "sourceActionAddress", 256));
        JsonElement items = value.GetProperty("items");
        int maximumCount = kind == "mail" ? GalateaObservationLimits.MaximumMailReceiptItemCount : GalateaObservationLimits.MaximumNoteIntentCount;
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() < 1 || items.GetArrayLength() > maximumCount) {
            throw new InvalidDataException("Invalid action receipt batch size.");
        }
        bool? hasPreviews = null;
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement item in items.EnumerateArray()) {
            string identity;
            if (kind == "mail") {
                GalateaInputValidation.RequireObject(item, "dispatchId", "outcome", "recipientPreview", "preview");
                identity = GalateaInputValidation.ReadText(item, "dispatchId", 68);
                if (identity.Length != 68 || !identity.StartsWith("gd1-", StringComparison.Ordinal)
                    || identity[4..].Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) {
                    throw new InvalidDataException("Action receipt dispatch ID is not canonical.");
                }
                if (GalateaInputValidation.ReadText(item, "outcome", 32) is not ("accepted" or "unrouted")) {
                    throw new InvalidDataException("Unknown action receipt mail outcome.");
                }
                MatchPreviewMode(item.GetProperty("recipientPreview"), ref hasPreviews);
            }
            else {
                GalateaInputValidation.RequireObject(item, "memoId", "preview");
                identity = MemoId.Parse(GalateaInputValidation.ReadText(item, "memoId", 64)).Value;
            }
            if (!identities.Add(identity)) { throw new InvalidDataException("Duplicate action receipt identity."); }
            MatchPreviewMode(item.GetProperty("preview"), ref hasPreviews);
        }
        return hasPreviews!.Value;
    }

    internal static void RequirePreview(string preview) {
        ArgumentNullException.ThrowIfNull(preview);
        _ = GalateaInputValidation.StrictUtf8.GetByteCount(preview);
        if (preview.EnumerateRunes().Count() > GalateaObservationLimits.MaximumActionReceiptPreviewScalars) {
            throw new InvalidDataException("Action receipt preview exceeds its scalar limit.");
        }
    }

    private static void MatchPreviewMode(JsonElement preview, ref bool? hasPreviews) {
        bool present = preview.ValueKind switch {
            JsonValueKind.String => true,
            JsonValueKind.Null => false,
            _ => throw new InvalidDataException("Action receipt preview must be a string or null.")
        };
        if (present) { RequirePreview(preview.GetString()!); }
        if (hasPreviews is { } expected && expected != present) {
            throw new InvalidDataException("Action receipt previews must be all strings or all null.");
        }
        hasPreviews = present;
    }
}
