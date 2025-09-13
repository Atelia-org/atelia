using System.Text.Json;
using CodeCortexV2.Workspace;

namespace CodeCortexV2.DevCli;

public static class E2eExactWithoutGlobalCommand {
    public static async Task<int> RunAsync() {
        var slnPath = @".\e2e\CodeCortex.E2E\CodeCortex.E2E.sln";
        var expectedFqn = "E2E.Target.TypeA"; // known stable type in E2E workspace
        var query = expectedFqn; // no global:: prefix, exact case
        try {
            Console.WriteLine($"[e2e] exact-without-global on '{query}' using {slnPath}");
            var wti = await WorkspaceTextInterface.CreateAsync(slnPath, CancellationToken.None).ConfigureAwait(false);
            var json = await wti.FindAsync(query, limit: 5, offset: 0, json: true, CancellationToken.None).ConfigureAwait(false);
            if (!IsExactSatisfied(json, expectedFqn)) {
                Console.Error.WriteLine("[e2e] FAIL: Exact match without global:: not observed or incorrect ordering.");
                return 5;
            }
            Console.WriteLine("[e2e] PASS.");
            return 0;
        } catch (Exception ex) {
            Console.Error.WriteLine("[e2e] error: " + ex.Message);
            return 2;
        }
    }

    private static bool IsExactSatisfied(string json, string expectedFqnNoGlobal) {
        try {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array) {
                return false;
            }
            if (items.GetArrayLength() == 0) {
                return false;
            }

            var first = items[0];
            var name = first.TryGetProperty("Name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
            var flagsVal = first.TryGetProperty("MatchFlags", out var mk) ? mk.GetInt32() : -1;
            var nameOk = string.Equals(name, expectedFqnNoGlobal, StringComparison.Ordinal);
            // V2: exact means no flags are set
            var mkOk = flagsVal == 0;
            return nameOk && mkOk;
        } catch {
            return false;
        }
    }
}

