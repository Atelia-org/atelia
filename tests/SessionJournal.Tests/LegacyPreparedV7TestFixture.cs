using System.Text;
using System.Text.Json.Nodes;

namespace Atelia.SessionJournal.Tests;

/// <summary>Test-only legacy encoding; production writes only the current layout.</summary>
internal static class LegacyPreparedV7TestFixture {
    public static byte[] Encode(
        CompletionRequestPreparedBody manifest,
        string adapterLabel = "historical-adapter-label"
    ) {
        JsonNode envelope = JsonNode.Parse(SessionEventCodec.Encode(
            SessionEventKind.CompletionRequestPrepared,
            manifest
        ))!;
        envelope["v"] = SessionRequestManifestDefaults.LegacyBodySchemaVersionV7;
        envelope["body"]!["target"]!["connection"]!["requestAdapterFingerprint"] =
            adapterLabel;
        return Encoding.UTF8.GetBytes(envelope.ToJsonString());
    }
}
