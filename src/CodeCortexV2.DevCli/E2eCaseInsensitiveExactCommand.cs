using System.Text.Json;
using CodeCortexV2;
using CodeCortexV2.Workspace;

namespace CodeCortexV2.DevCli;

public static class E2eCaseInsensitiveExactCommand {
    public static async Task<int> RunAsync() {
        // Defaults for hands-free automation
        var slnPath = @".\e2e\CodeCortex.E2E\CodeCortex.E2E.sln";
        // Choose a known type with stable FQN; we'll vary case in the query (keep global:: to hit FQN path)
        var properFqn = "E2E.Target.TypeA";
        var mixedCaseFqn = "global::e2e.target.tYpeA"; // intentionally wrong case with global::
        try {
            Console.WriteLine($"[e2e] case-insensitive-exact on '{mixedCaseFqn}' using {slnPath}");

            // Fresh WTI per run (no mutation needed)
            var wti = await WorkspaceTextInterface.CreateAsync(slnPath, CancellationToken.None).ConfigureAwait(false);
            var json = await wti.FindAsync(mixedCaseFqn, limit: 5, offset: 0, json: true, CancellationToken.None).ConfigureAwait(false);
            if (!IsExactIgnoreCaseSatisfied(json, properFqn)) {
                Console.Error.WriteLine("[e2e] FAIL: ExactIgnoreCase match not observed or incorrect ordering.");
                return 5;
            }

            Console.WriteLine("[e2e] PASS.");
            return 0;
        } catch (Exception ex) {
            Console.Error.WriteLine("[e2e] error: " + ex.Message);
            return 2;
        }
    }

    private static bool IsExactIgnoreCaseSatisfied(string json, string expectedFqnNoGlobal) {
        try {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array) {
                return false;
            }
            // Validations:
            // - First item should be ExactIgnoreCase (enum value likely 2) OR have score==10 (as assigned for case-insensitive path)
            // - Name should equal the expected FQN (no global::)
            if (items.GetArrayLength() == 0) {
                return false;
            }

            var first = items[0];
            var name = first.TryGetProperty("Name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
            var matchKind = first.TryGetProperty("MatchKind", out var mk) ? mk.GetInt32() : -1;
            var score = first.TryGetProperty("Score", out var sc) ? sc.GetInt32() : int.MinValue;
            var nameOk = string.Equals(name, expectedFqnNoGlobal, StringComparison.Ordinal);
            var mkOk = matchKind == 2; // ExactIgnoreCase expected
            var scoreOk = score == 10; // SymbolIndex assigns 10 for ExactIgnoreCase
            return nameOk && (mkOk || scoreOk);
        } catch {
            return false;
        }
    }
}
