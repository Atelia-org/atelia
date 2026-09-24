using Atelia.Galatea.Input;
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
        GalateaInputValidation.ValidateSource(kind, id, name);
        Kind = kind;
        Id = id;
        Name = name;
    }

    internal string Kind { get; }
    internal string Id { get; }
    internal string Name { get; }
}

internal static class GalateaInputContentValidation {
    internal static void RequireObject(JsonElement value, params string[] fields) => GalateaInputValidation.RequireObject(value, fields);
    internal static void RequireVersion(JsonElement value) => GalateaInputValidation.RequireVersion(value);
    internal static GalateaSenderSnapshot ReadSender(JsonElement value) {
        var source = GalateaInputValidation.ReadSender(value);
        return new(source.Kind, source.Id, source.Name);
    }
    internal static string ReadText(JsonElement value, string name, int maximumBytes) => GalateaInputValidation.ReadText(value, name, maximumBytes);
    internal static void RequireText(string text, int maximumBytes, string name, bool singleLine = false)
        => GalateaInputValidation.RequireText(text, maximumBytes, name, singleLine);
}

/// <summary>Transient request projection; never used for storage, proof, query or audit.</summary>
internal sealed class GalateaInputProjector : ISessionInputProjector {
    internal static GalateaInputProjector Instance { get; } = new();

    public string Project(SessionInputContent input) {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.IsStructured) { return input.TextValue; }
        switch (input.SchemaId) {
            case GalateaObservationContent.V1SchemaId:
            case GalateaObservationContent.V2SchemaId:
            case GalateaObservationContent.V3SchemaId:
                return GalateaObservationInputProjector.Instance.Project(input);
            case GalateaSystemInstructionContent.SchemaId:
            case GalateaSystemInstructionContent.V2SchemaId:
                return GalateaSystemInstructionContent.Project(input);
            case GalateaDelegateTaskContent.SchemaId:
                return GalateaDelegateTaskContent.Project(input.JsonValue);
            default:
                throw new NotSupportedException("Unsupported Galatea structured input schema: " + input.SchemaId);
        }
    }
}
