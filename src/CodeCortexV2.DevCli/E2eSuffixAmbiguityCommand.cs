using System.Text.Json;
using CodeCortexV2;
using CodeCortexV2.Workspace;
using Microsoft.CodeAnalysis;

namespace CodeCortexV2.DevCli;

public static class E2eSuffixAmbiguityCommand {
    public static async Task<int> RunAsync() {
        // Defaults for hands-free automation
        var slnPath = @".\e2e\CodeCortex.E2E\CodeCortex.E2E.sln";
        var className = "E2eAmbDupTemp"; // same simple name in two namespaces
        var ns1 = "E2E.SuffixAmb.N1";
        var ns2 = "E2E.SuffixAmb.N2";
        var waitPollMs = 250;
        var maxPoll = 60; // ~15s

        string? file1 = null;
        string? file2 = null;
        try {
            Console.WriteLine($"[e2e] suffix-ambiguity on '{className}' using {slnPath}");

            // Locate a project folder to place temporary files
            var host = await RoslynWorkspaceHost.LoadAsync(slnPath, CancellationToken.None).ConfigureAwait(false);
            var proj = host.Workspace.CurrentSolution.Projects.FirstOrDefault(p => p.Language == LanguageNames.CSharp);
            if (proj is null || string.IsNullOrEmpty(proj.FilePath)) {
                Console.Error.WriteLine("[e2e] no C# project found in solution.");
                return 3;
            }
            var projDir = Path.GetDirectoryName(proj.FilePath)!;
            file1 = Path.Combine(projDir, "E2E_TEMP_SuffixAmb1.cs");
            file2 = Path.Combine(projDir, "E2E_TEMP_SuffixAmb2.cs");

            // Prepare source content
            string src1 = $"namespace {ns1} {{ public class {className} {{ /* E2E_TEMP_SUFFIX_AMBIGUITY */ }} }}\n";
            string src2 = $"namespace {ns2} {{ public class {className} {{ /* E2E_TEMP_SUFFIX_AMBIGUITY */ }} }}\n";
            await File.WriteAllTextAsync(file1, src1).ConfigureAwait(false);
            await File.WriteAllTextAsync(file2, src2).ConfigureAwait(false);

            // Poll: create fresh WTI each time to ensure MSBuild picks new files via SDK wildcards
            var foundAmbiguous = false;
            for (int i = 0; i < maxPoll; i++) {
                await Task.Delay(waitPollMs).ConfigureAwait(false);
                var wti = await WorkspaceTextInterface.CreateAsync(slnPath, CancellationToken.None).ConfigureAwait(false);
                var json = await wti.FindAsync(className, limit: 20, offset: 0, json: true, CancellationToken.None).ConfigureAwait(false);
                if (IsAmbiguitySatisfied(json, className)) {
                    Console.WriteLine($"[e2e] ambiguity detected after {i + 1} polls.");
                    foundAmbiguous = true;
                    break;
                }
            }

            if (!foundAmbiguous) {
                Console.Error.WriteLine("[e2e] FAIL: ambiguity not observed within timeout.");
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
            // Cleanup temp files
            try {
                if (file1 != null && File.Exists(file1)) {
                    File.Delete(file1);
                }
            }
            catch { }
            try {
                if (file2 != null && File.Exists(file2)) {
                    File.Delete(file2);
                }
            }
            catch { }
        }
    }

    private static bool IsAmbiguitySatisfied(string json, string className) {
        try {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array) { return false; }
            int suffixCount = 0;
            int ambiguousCount = 0;
            foreach (var it in items.EnumerateArray()) {
                var name = it.GetProperty("Name").GetString() ?? string.Empty;
                var isAmb = it.TryGetProperty("IsAmbiguous", out var ia) && ia.GetBoolean();
                // FQNNoGlobal ends with ".<className>" for types in namespaces; also allow plain equal just in case
                bool suffix = name.Equals(className, StringComparison.Ordinal) || name.EndsWith("." + className, StringComparison.Ordinal);
                if (suffix) {
                    suffixCount++;
                    if (isAmb) { ambiguousCount++; }
                }
            }
            return suffixCount >= 2 && ambiguousCount >= 2;
        }
        catch {
            return false;
        }
    }
}
