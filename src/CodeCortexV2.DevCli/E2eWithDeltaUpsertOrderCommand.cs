using CodeCortexV2;
using CodeCortexV2.Workspace;
using Microsoft.CodeAnalysis;

namespace CodeCortexV2.DevCli;

public static class E2eWithDeltaUpsertOrderCommand {
    public static async Task<int> RunAsync() {
        // Defaults for hands-free automation
        var slnPath = @".\e2e\CodeCortex.E2E\CodeCortex.E2E.sln";
        var oldFqn = "E2E.Target.TypeA"; // stable type
        var oldDocId = "T:" + oldFqn;

        using var cts = new CancellationTokenSource();
        string? filePath = null;
        string? original = null;
        try {
            Console.WriteLine($"[e2e] with-delta-upsert-order on {oldFqn} using {slnPath}");

            // Load workspace and locate source file for the type
            var host = await RoslynWorkspaceHost.LoadAsync(slnPath, cts.Token).ConfigureAwait(false);
            INamedTypeSymbol? target = null;
            foreach (var proj in host.Workspace.CurrentSolution.Projects) {
                var comp = await proj.GetCompilationAsync(cts.Token).ConfigureAwait(false);
                if (comp is null) {
                    continue;
                }

                var t = comp.GetTypeByMetadataName(oldFqn);
                if (t is not null) {
                    target = t;
                    break;
                }
            }
            if (target is null) {
                Console.Error.WriteLine("[e2e] target type not found.");
                return 3;
            }
            filePath = target.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.FilePath;
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) {
                Console.Error.WriteLine("[e2e] cannot locate source file.");
                return 3;
            }

            // Prepare new type name and expected new doc-id by renaming the class identifier
            var stamp = DateTime.UtcNow.Ticks;
            var newTypeName = $"TypeA_Upsert_{stamp}";
            var newFqn = $"E2E.Target.{newTypeName}";
            var newDocId = "T:" + newFqn;

            // Mutate: rename class identifier (works for "public partial class TypeA")
            original = await File.ReadAllTextAsync(filePath!, cts.Token).ConfigureAwait(false);
            var mutated = original.Replace("class TypeA", $"class {newTypeName}", StringComparison.Ordinal);
            if (mutated == original) {
                Console.Error.WriteLine("[e2e] mutation produced no change.");
                return 4;
            }
            await File.WriteAllTextAsync(filePath!, mutated, cts.Token).ConfigureAwait(false);

            // Poll: recreate WTI each time to ensure fresh load; success when old id gone and new id present
            var ok = false;
            for (int i = 0; i < 60; i++) {
                await Task.Delay(250, cts.Token).ConfigureAwait(false);
                var wti = await WorkspaceTextInterface.CreateAsync(slnPath, cts.Token).ConfigureAwait(false);
                var oldJson = await wti.FindAsync(oldDocId, limit: 5, offset: 0, json: true, cts.Token).ConfigureAwait(false);
                var newJson = await wti.FindAsync(newDocId, limit: 5, offset: 0, json: true, cts.Token).ConfigureAwait(false);
                var oldGone = ParseTotal(oldJson) == 0;
                var newPresent = ParseTotal(newJson) >= 1;
                if (oldGone && newPresent) {
                    ok = true;
                    break;
                }
            }

            // Revert
            await File.WriteAllTextAsync(filePath!, original, cts.Token).ConfigureAwait(false);
            Console.WriteLine("[e2e] reverted mutation.");

            if (!ok) {
                Console.Error.WriteLine("[e2e] FAIL: old id not removed and/or new id not added within timeout.");
                return 5;
            }

            Console.WriteLine("[e2e] PASS.");
            return 0;
        } catch (Exception ex) {
            Console.Error.WriteLine("[e2e] error: " + ex.Message);
            if (filePath != null && original != null) {
                try { await File.WriteAllTextAsync(filePath, original); } catch { }
            }
            return 2;
        }
    }

    private static int ParseTotal(string json) {
        try {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("Total", out var t) ? t.GetInt32() : -1;
        } catch { return -1; }
    }
}
