using System.Text.Json;
using CodeCortexV2;

namespace CodeCortexV2.DevCli;

public static class E2eFuzzyFallbackCommand {
    public static async Task<int> RunAsync() {
        // Defaults for hands-free automation
        var slnPath = @".\e2e\CodeCortex.E2E\CodeCortex.E2E.sln";
        // Choose a near-miss for a known simple name so only fuzzy layer can match
        // "TypeA" → "TydeA" (edit distance 1, length <= 12 → threshold 1)
        var query = "TydeA";
        var expectedSuffix = ".TypeA";

        try {
            Console.WriteLine($"[e2e] fuzzy-fallback '{query}' using {slnPath}");

            var wti = await WorkspaceTextInterface.CreateAsync(slnPath, CancellationToken.None).ConfigureAwait(false);
            var json = await wti.FindAsync(query, limit: 20, offset: 0, json: true, CancellationToken.None).ConfigureAwait(false);

            if (!ValidateFuzzy(json, expectedSuffix)) {
                Console.Error.WriteLine("[e2e] FAIL: Fuzzy fallback not observed as expected.");
                return 5;
            }

            Console.WriteLine("[e2e] PASS.");
            return 0;
        } catch (Exception ex) {
            Console.Error.WriteLine("[e2e] error: " + ex.Message);
            return 2;
        }
    }

    private static bool ValidateFuzzy(string json, string expectedSuffix) {
        try {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array) {
                return false;
            }
            if (items.GetArrayLength() == 0) {
                return false;
            }

            // Expect: only fuzzy layer produces results (MatchKind == 8), and at least one ends with .TypeA
            bool anyExpected = false;
            foreach (var it in items.EnumerateArray()) {
                var mk = it.TryGetProperty("MatchKind", out var m) ? m.GetInt32() : -1;
                if (mk != 8) {
                    return false; // found a non-fuzzy result, not a fallback
                }

                var name = it.TryGetProperty("Name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                if (name.EndsWith(expectedSuffix, StringComparison.Ordinal)) {
                    anyExpected = true;
                }
            }
            return anyExpected;
        } catch {
            return false;
        }
    }
}
