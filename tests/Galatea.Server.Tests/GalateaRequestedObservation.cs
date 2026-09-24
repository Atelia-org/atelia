using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.MdJson;
using Atelia.SessionJournal;

namespace Atelia.Galatea.Server.Tests;

/// <summary>Reads unchanged business fields from request projection, not durable connection facts.</summary>
internal static class GalateaRequestedObservation {
    internal static SessionInputContent BusinessContent(string rendered) {
        JsonObject value = JsonNode.Parse(MdJsonSerializer.Read(rendered).GetRawText())!.AsObject();
        value.Remove("connectionState");
        string schema = value["kind"]!.GetValue<string>() == "heartbeat-activation"
            ? GalateaObservationContent.V2SchemaId : GalateaObservationContent.V1SchemaId;
        return SessionInputContent.Structured(schema, JsonSerializer.SerializeToElement(value));
    }
}
