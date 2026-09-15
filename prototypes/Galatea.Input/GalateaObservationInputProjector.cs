using Atelia.MdJson;
using Atelia.SessionJournal;

namespace Atelia.Galatea.Input;

/// <summary>Projects stored Galatea observations for an actual LLM request; it never opens stores or hydrates host objects.</summary>
public sealed class GalateaObservationInputProjector : ISessionInputProjector {
    public static GalateaObservationInputProjector Instance { get; } = new();

    public string Project(SessionInputContent input) {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.IsStructured) { return input.TextValue; }
        if (input.SchemaId != GalateaObservationSchema.SchemaId) {
            throw new NotSupportedException("Unsupported Galatea Observation schema: " + input.SchemaId);
        }
        return MdJsonSerializer.Write(input.JsonValue, GalateaObservationSchema.ExternalStringPaths(input.JsonValue));
    }
}
