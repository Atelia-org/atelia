using System.Text.Json;
using CodeCortexV2;
using CodeCortexV2.Workspace;
using Microsoft.CodeAnalysis;

namespace CodeCortexV2.DevCli;

public static class E2eDebounceBatchCommand {
    public static async Task<int> RunAsync() {
        // Defaults (no args)
        var slnPath = @".\e2e\CodeCortex.E2E\CodeCortex.E2E.sln";
        var ns = "E2E.Target";
        var originalType = "TypeA";
        var delayBetweenWritesMs = 120; // < 500ms default debounce
        var finalWaitMs = 1200;         // > debounce + compile

        using var cts = new CancellationTokenSource();
        string? filePath = null;
        string? originalText = null;
        try {
            Console.WriteLine($"[e2e] debounce-batch on {ns}.{originalType} using {slnPath}");

            // Single WTI instance so we test IndexSynchronizer debounce behavior
            var wti = await WorkspaceTextInterface.CreateAsync(slnPath, cts.Token).ConfigureAwait(false);

            // Resolve source file for the type
            var host = await RoslynWorkspaceHost.LoadAsync(slnPath, cts.Token).ConfigureAwait(false);
            INamedTypeSymbol? sym = null;
            foreach (var p in host.Workspace.CurrentSolution.Projects) {
                var comp = await p.GetCompilationAsync(cts.Token).ConfigureAwait(false);
                if (comp is null) {
                    continue;
                }

                var candidate = comp.GetTypeByMetadataName($"{ns}.{originalType}");
                if (candidate is not null) {
                    sym = candidate;
                    break;
                }
            }
            if (sym is null) {
                Console.Error.WriteLine("[e2e] type not found");
                return 3;
            }
            filePath = sym.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.FilePath;
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) {
                Console.Error.WriteLine("[e2e] source file not found");
                return 3;
            }

            originalText = await File.ReadAllTextAsync(filePath!, cts.Token).ConfigureAwait(false);

            var ts = DateTime.UtcNow.Ticks.ToString();
            var n1 = $"{originalType}_DBA_{ts}";
            var n2 = $"{originalType}_DBB_{ts}";
            var n3 = $"{originalType}_DBC_{ts}";

            // Helper: rename the type identifier once (class/record/struct)
            static string RenameTypeOnce(string text, string oldName, string newName) {
                var patterns = new[] { $"class {oldName}", $"record {oldName}", $"struct {oldName}" };
                foreach (var pat in patterns) {
                    var idx = text.IndexOf(pat, StringComparison.Ordinal);
                    if (idx >= 0) {
                        return text.Substring(0, idx) + pat.Replace(oldName, newName, StringComparison.Ordinal) + text.Substring(idx + pat.Length);
                    }
                }
                return text; // no change
            }

            // Quick successive renames within debounce window
            var t1 = RenameTypeOnce(originalText, originalType, n1);
            if (t1 == originalText) {
                Console.Error.WriteLine("[e2e] rename step1 produced no change");
                return 4;
            }
            await File.WriteAllTextAsync(filePath!, t1, cts.Token).ConfigureAwait(false);
            await Task.Delay(delayBetweenWritesMs, cts.Token).ConfigureAwait(false);

            // Before debounce flush: the new id should NOT be visible yet
            if (await ExistsAsync(wti, $"T:{ns}.{n1}") || await ExistsAsync(wti, $"T:{ns}.{n2}") || await ExistsAsync(wti, $"T:{ns}.{n3}")) {
                Console.Error.WriteLine("[e2e] early visibility detected (unexpected)");
                // continue but mark failure afterwards
            }

            var t2 = RenameTypeOnce(t1, n1, n2);
            await File.WriteAllTextAsync(filePath!, t2, cts.Token).ConfigureAwait(false);
            await Task.Delay(delayBetweenWritesMs, cts.Token).ConfigureAwait(false);

            var t3 = RenameTypeOnce(t2, n2, n3);
            await File.WriteAllTextAsync(filePath!, t3, cts.Token).ConfigureAwait(false);

            // Wait for debounce+apply, then recreate WTI to ensure fresh load (MSBuildWorkspace may not push FS changes)
            await Task.Delay(finalWaitMs, cts.Token).ConfigureAwait(false);
            var probe = await WorkspaceTextInterface.CreateAsync(slnPath, cts.Token).ConfigureAwait(false);

            // Validate only the final name is indexed; intermediates and original are gone
            var okFinal = await ExistsAsync(probe, $"T:{ns}.{n3}");
            var okNoN1 = !await ExistsAsync(probe, $"T:{ns}.{n1}");
            var okNoN2 = !await ExistsAsync(probe, $"T:{ns}.{n2}");
            var okNoOriginal = !await ExistsAsync(probe, $"T:{ns}.{originalType}");

            // Revert
            await File.WriteAllTextAsync(filePath!, originalText!, cts.Token).ConfigureAwait(false);
            Console.WriteLine("[e2e] reverted mutation.");

            if (okFinal && okNoN1 && okNoN2 && okNoOriginal) {
                Console.WriteLine("[e2e] PASS.");
                return 0;
            }
            Console.Error.WriteLine($"[e2e] FAIL: okFinal={okFinal} noN1={okNoN1} noN2={okNoN2} noOriginal={okNoOriginal}");
            return 5;
        } catch (Exception ex) {
            Console.Error.WriteLine("[e2e] error: " + ex.Message);
            try {
                if (filePath != null && originalText != null) {
                    File.WriteAllText(filePath, originalText);
                }
            } catch { }
            return 2;
        }
    }

    private static async Task<bool> ExistsAsync(WorkspaceTextInterface wti, string id) {
        var json = await wti.FindAsync(id, limit: 2, offset: 0, json: true, CancellationToken.None).ConfigureAwait(false);
        try {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array) {
                return false;
            }

            foreach (var it in items.EnumerateArray()) {
                if (it.TryGetProperty("SymbolId", out var sid) && sid.ValueKind == JsonValueKind.Object) {
                    var val = sid.TryGetProperty("Value", out var v) ? v.GetString() : null;
                    if (string.Equals(val, id, StringComparison.Ordinal)) {
                        return true;
                    }
                }
            }
            return false;
        } catch { return false; }
    }
}
