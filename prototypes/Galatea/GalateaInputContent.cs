using System.Globalization;
using System.Text;
using System.Text.Json;
using Atelia.MdJson;
using Atelia.SessionJournal;

namespace Atelia.Galatea.Server;

/// <summary>An identity captured by the accepting runtime, never taken from message text.</summary>
internal sealed record GalateaSenderSnapshot {
    internal static GalateaSenderSnapshot Player(GalateaPlayerConfig player) {
        ArgumentNullException.ThrowIfNull(player);
        return new("player", player.PlayerId, player.Name.Value);
    }

    internal GalateaSenderSnapshot(string kind, string id, string name) {
        if (kind is not ("player" or "character" or "runtime" or "delegate")) {
            throw new ArgumentException("Unknown sender kind.", nameof(kind));
        }
        GalateaInputContentValidation.RequireText(id, 512, nameof(id), singleLine: true);
        GalateaInputContentValidation.RequireText(name, 1024, nameof(name), singleLine: true);
        Kind = kind;
        Id = id;
        Name = name;
    }

    internal string Kind { get; }
    internal string Id { get; }
    internal string Name { get; }
}

internal static class GalateaInputContentValidation {
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

    internal static GalateaSenderSnapshot ReadSender(JsonElement value) {
        RequireObject(value, "kind", "id", "name");
        return new GalateaSenderSnapshot(ReadText(value, "kind", 32),
            ReadText(value, "id", 512), ReadText(value, "name", 1024));
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
            if (GalateaBoundedJson.StrictUtf8.GetByteCount(text) > maximumBytes) {
                throw new ArgumentOutOfRangeException(name, "Structured content field exceeds its UTF-8 byte limit.");
            }
        }
        catch (EncoderFallbackException exception) {
            throw new ArgumentException("Structured content must contain valid Unicode.", name, exception);
        }
    }
}

/// <summary>Transient request projection; never used for storage, proof, query or audit.</summary>
internal sealed class GalateaInputProjector : ISessionInputProjector {
    internal static GalateaInputProjector Instance { get; } = new();

    public string Project(SessionInputContent input) {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.IsStructured) { return input.TextValue; }
        switch (input.SchemaId) {
            case GalateaObservationContent.SchemaId:
                GalateaObservationContent.Validate(input.JsonValue);
                return MdJsonSerializer.Write(input.JsonValue, GalateaObservationContent.ExternalStringPaths(input.JsonValue));
            case GalateaSystemInstructionContent.SchemaId:
                return GalateaSystemInstructionContent.Project(input.JsonValue);
            case GalateaDelegateTaskContent.SchemaId:
                return GalateaDelegateTaskContent.Project(input.JsonValue);
            default:
                throw new NotSupportedException("Unsupported Galatea structured input schema: " + input.SchemaId);
        }
    }
}
