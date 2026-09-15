using System.Text;
using System.Text.Json;

namespace Atelia.Galatea.Input;

internal sealed record GalateaInputSource(string Kind, string Id, string Name);

internal static class GalateaInputValidation {
    internal static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal static void RequireObject(JsonElement value, params string[] fields) {
        if (value.ValueKind != JsonValueKind.Object) { throw new InvalidDataException("Expected a structured input object."); }
        var remaining = new HashSet<string>(fields, StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject()) {
            if (!remaining.Remove(property.Name)) {
                throw new InvalidDataException("Unknown or duplicate structured input field: " + property.Name);
            }
        }
        if (remaining.Count != 0) { throw new InvalidDataException("Structured input has missing fields."); }
    }

    internal static void RequireVersion(JsonElement value) {
        if (!value.GetProperty("v").TryGetInt32(out int version) || version != 1) {
            throw new InvalidDataException("Unsupported Galatea content version.");
        }
    }

    internal static GalateaInputSource ReadSender(JsonElement value) {
        RequireObject(value, "kind", "id", "name");
        string kind = ReadText(value, "kind", 32);
        string id = ReadText(value, "id", 512);
        string name = ReadText(value, "name", 1024);
        ValidateSource(kind, id, name);
        return new GalateaInputSource(kind, id, name);
    }

    internal static void ValidateSource(string kind, string id, string name) {
        if (kind is not ("player" or "character" or "runtime" or "delegate")) {
            throw new ArgumentException("Unknown sender kind.", nameof(kind));
        }
        RequireText(id, 512, nameof(id), singleLine: true);
        RequireText(name, 1024, nameof(name), singleLine: true);
    }

    internal static string ReadText(JsonElement value, string name, int maximumBytes) {
        JsonElement field = value.GetProperty(name);
        if (field.ValueKind != JsonValueKind.String) { throw new InvalidDataException("Expected string field: " + name); }
        string text = field.GetString()!;
        RequireText(text, maximumBytes, name);
        return text;
    }

    internal static void RequireText(string text, int maximumBytes, string name, bool singleLine = false) {
        ArgumentException.ThrowIfNullOrWhiteSpace(text, name);
        if (singleLine && (text != text.Trim() || text.IndexOfAny(['\r', '\n', '\0']) >= 0)) {
            throw new ArgumentException("Identity must be a canonical single line.", name);
        }
        try {
            if (StrictUtf8.GetByteCount(text) > maximumBytes) {
                throw new ArgumentOutOfRangeException(name, "Structured content field exceeds its UTF-8 byte limit.");
            }
        }
        catch (EncoderFallbackException exception) {
            throw new ArgumentException("Structured content must contain valid Unicode.", name, exception);
        }
    }
}
