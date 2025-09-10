using System.Text.Json;
using CodeCortexV2;

namespace CodeCortexV2.DevCli;

public static class E2eWildcardSearchCommand {
    public static async Task<int> RunAsync() {
        // Defaults for hands-free automation
        var slnPath = @".\e2e\CodeCortex.E2E\CodeCortex.E2E.sln";
        // Choose a pattern that must trigger wildcard branch and match FQN including global:: in index
        // Search step for wildcard matches against e.Fqn (with global::), so we include a leading wildcard.
        var pattern = "*E2E.Target.Type?"; // expect TypeA / TypeB etc.

        try {
            Console.WriteLine($"[e2e] wildcard-search '{pattern}' using {slnPath}");

            // Fresh WTI per run (no mutation)
            var wti = await WorkspaceTextInterface.CreateAsync(slnPath, CancellationToken.None).ConfigureAwait(false);
            var json = await wti.FindAsync(pattern, limit: 100, offset: 0, json: true, CancellationToken.None).ConfigureAwait(false);
            if (!HasWildcardHit(json, "E2E.Target.Type")) {
                Console.Error.WriteLine("[e2e] FAIL: Wildcard match not observed in results.");
                return 5;
            }

            Console.WriteLine("[e2e] PASS.");
            return 0;
        } catch (Exception ex) {
            Console.Error.WriteLine("[e2e] error: " + ex.Message);
            return 2;
        }
    }

    private static bool HasWildcardHit(string json, string mustContain) {
        try {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array) {
                return false;
            }
            foreach (var it in items.EnumerateArray()) {
                // MatchKind: Wildcard == 6 per ordering (Id=0, Exact=1, ExactIgnoreCase=2, Prefix=3, Contains=4, Suffix=5, Wildcard=6, GenericBase=7, Fuzzy=8)
                var mk = it.TryGetProperty("MatchKind", out var m) ? m.GetInt32() : -1;
                var name = it.TryGetProperty("Name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                if (mk == 6 && name.Contains(mustContain, StringComparison.Ordinal)) {
                    return true;
                }
            }
        } catch { }
        return false;
    }
}
