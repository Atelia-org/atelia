using CodeCortexV2;
using CodeCortexV2.Workspace;
using Microsoft.CodeAnalysis;

namespace CodeCortexV2.DevCli;

public static class E2eGenericBaseSearchCommand {
    public static async Task<int> RunAsync() {
        // Defaults for hands-free automation
        var slnPath = @".\e2e\CodeCortex.E2E\CodeCortex.E2E.sln";
        var ns = "E2E.GenericBase";
        var typeName = "GList"; // generic base simple name
        var waitPollMs = 250;
        var maxPoll = 60; // ~15s

        string? file = null;
        try {
            Console.WriteLine($"[e2e] generic-base-search for '{typeName}' using {slnPath}");

            // Choose a C# project to drop a temporary file
            var host = await RoslynWorkspaceHost.LoadAsync(slnPath, CancellationToken.None).ConfigureAwait(false);
            var proj = host.Workspace.CurrentSolution.Projects.FirstOrDefault(p => p.Language == LanguageNames.CSharp);
            if (proj is null || string.IsNullOrEmpty(proj.FilePath)) {
                Console.Error.WriteLine("[e2e] no C# project found in solution.");
                return 3;
            }
            var projDir = Path.GetDirectoryName(proj.FilePath)!;
            file = Path.Combine(projDir, "E2E_TEMP_GenericBase.cs");

            // Generic type definition; avoid suffix match by ensuring the simple name appears only as generic definition (with <T>)
            string src = $"namespace {ns} {{ public class {typeName}<T> {{ /* E2E_TEMP_GENERIC_BASE */ }} }}\n";
            await File.WriteAllTextAsync(file, src).ConfigureAwait(false);

            // Poll: create fresh WTI each loop to ensure reload without relying on file watchers
            var observed = false;
            for (int i = 0; i < maxPoll; i++) {
                await Task.Delay(waitPollMs).ConfigureAwait(false);
                var wti = await WorkspaceTextInterface.CreateAsync(slnPath, CancellationToken.None).ConfigureAwait(false);
                var json = await wti.FindAsync(typeName, limit: 20, offset: 0, json: true, CancellationToken.None).ConfigureAwait(false);
                if (IsGenericBaseSatisfied(json, ns, typeName)) {
                    Console.WriteLine($"[e2e] generic-base detected after {i + 1} polls.");
                    observed = true;
                    break;
                }
            }

            if (!observed) {
                Console.Error.WriteLine("[e2e] FAIL: GenericBase match not observed within timeout.");
                return 5;
            }

            Console.WriteLine("[e2e] PASS.");
            return 0;
        }
        catch (Exception ex) {
            Console.Error.WriteLine("[e2e] error: " + ex.Message);
            return 2;
        }
        finally {
            try {
                if (file != null && File.Exists(file)) {
                    File.Delete(file);
                }
            }
            catch { }
        }
    }

    private static bool IsGenericBaseSatisfied(string json, string ns, string typeName) {
        try {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Items", out var items) || items.ValueKind != System.Text.Json.JsonValueKind.Array) { return false; }
            foreach (var it in items.EnumerateArray()) {
                var name = it.TryGetProperty("Name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                // MatchFlags is serialized as a number; GenericBase == 7 per V2 enum
                var mk = it.TryGetProperty("MatchFlags", out var m) ? m.GetInt32() : -1;
                if (mk == 7 && name.Contains($"{ns}.{typeName}", StringComparison.Ordinal)) { return true; }
            }
        }
        catch { }
        return false;
    }
}
