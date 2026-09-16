using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Atelia.Galatea.Server.Tests;

internal static class GalateaLabAdmission {
    // Startup now inspects existing zero-interval sessions under TurnLock.
    // Retry only an explicit rejection proving this request was not accepted.
    // Never retry 202, lost responses, transport failures, or recovery conflicts.
    internal static async Task<HttpResponseMessage> PostFreshAsync(HttpClient client, ChatStreamRequest request) {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true) {
            HttpResponseMessage response = await client.PostAsJsonAsync(
                "/api/v1/characters/alice/chat/turns", request, deadline.Token);
            if (response.StatusCode != HttpStatusCode.Conflict) { return response; }
            string json = await response.Content.ReadAsStringAsync(deadline.Token);
            using JsonDocument body = JsonDocument.Parse(json);
            if (!body.RootElement.TryGetProperty("code", out JsonElement code)
                || code.GetString() != "turn-busy"
                || !body.RootElement.TryGetProperty("turnId", out JsonElement turnId)
                || turnId.ValueKind != JsonValueKind.Null) {
                response.Dispose();
                throw new InvalidOperationException("Unexpected fresh admission conflict: "
                    + (body.RootElement.TryGetProperty("code", out var errorCode) ? errorCode.GetString() : "missing-code"));
            }
            response.Dispose();
            await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
        }
    }
}
