using System.Text.Json;
using CodeCortexV2;
using CodeCortexV2.Workspace;
using Microsoft.CodeAnalysis;

namespace CodeCortexV2.DevCli;

public static class E2ePartialTypeRemoveCommand {
    // Zero-arg, defaults-only command for CI-friendly runs
    public static async Task<int> RunAsync() {
        var slnPath = @".\e2e\CodeCortex.E2E\CodeCortex.E2E.sln";
        var targetFqn = "E2E.Target.TypeA";
        var typeId = "T:" + targetFqn;
        var waitPollMs = 250;
        var maxPoll = 60; // ~15s

        string? originalPath = null;
        string? originalBak = null;
        string? partialPath = null;

        using var cts = new CancellationTokenSource();
        try {
            Console.WriteLine($"[e2e] partial-type-remove on {targetFqn} using {slnPath}");

            // Ensure type exists initially
            var pre = await WorkspaceTextInterface.CreateAsync(slnPath, cts.Token);
            var preJson = await pre.FindAsync(typeId, limit: 5, offset: 0, json: true, cts.Token);
            if (!HasTotal(preJson, out var preTotal) || preTotal == 0) {
                Console.Error.WriteLine("[e2e] precondition failed: type not found before test.");
                return 3;
            }

            // Locate original file via Roslyn
            var host = await RoslynWorkspaceHost.LoadAsync(slnPath, cts.Token);
            INamedTypeSymbol? target = null;
            foreach (var proj in host.Workspace.CurrentSolution.Projects) {
                var comp = await proj.GetCompilationAsync(cts.Token).ConfigureAwait(false);
                if (comp is null) { continue; }
                target = comp.GetTypeByMetadataName(targetFqn);
                if (target is not null) { break; }
            }
            if (target is null) {
                Console.Error.WriteLine("[e2e] target type not found via Roslyn.");
                return 3;
            }
            originalPath = target.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.FilePath;
            if (string.IsNullOrEmpty(originalPath) || !File.Exists(originalPath)) {
                Console.Error.WriteLine("[e2e] cannot locate original source file.");
                return 3;
            }
            var dir = Path.GetDirectoryName(originalPath)!;
            partialPath = Path.Combine(dir, "TypeA.Partial.e2e.cs");

            // 1) Create a second partial declaration file
            var partialContent = "namespace E2E.Target { public partial class TypeA { /* e2e partial */ } }\n";
            await File.WriteAllTextAsync(partialPath, partialContent, cts.Token).ConfigureAwait(false);
            Console.WriteLine($"[e2e] created partial: {partialPath}");

            // Poll until type still PRESENT (should remain since original exists)
            if (!await WaitForTypePresenceAsync(slnPath, typeId, expectPresent: true, waitPollMs, maxPoll, cts.Token)) {
                Console.Error.WriteLine("[e2e] FAIL: type not observed after adding partial file.");
                await CleanupAsync(originalPath, originalBak, partialPath);
                return 5;
            }

            // 2) Remove the created partial file; type should STILL exist (original remains)
            File.Delete(partialPath);
            Console.WriteLine("[e2e] deleted partial.");
            partialPath = null; // mark deleted

            if (!await WaitForTypePresenceAsync(slnPath, typeId, expectPresent: true, waitPollMs, maxPoll, cts.Token)) {
                Console.Error.WriteLine("[e2e] FAIL: type disappeared after removing one partial file.");
                await CleanupAsync(originalPath, originalBak, partialPath);
                return 5;
            }

            // 3) Remove the original file; now type should DISAPPEAR (no declarations remain)
            originalBak = originalPath + ".e2e.bak";
            File.Move(originalPath, originalBak, overwrite: true);
            Console.WriteLine("[e2e] moved original -> .bak");

            if (!await WaitForTypePresenceAsync(slnPath, typeId, expectPresent: false, waitPollMs, maxPoll, cts.Token)) {
                Console.Error.WriteLine("[e2e] FAIL: type still present after removing all partials.");
                await CleanupAsync(originalPath, originalBak, partialPath);
                return 5;
            }

            // 4) Restore original; type should return (best-effort, not a hard assert for CI speed)
            if (originalBak is not null && !File.Exists(originalPath)) {
                File.Move(originalBak, originalPath);
                Console.WriteLine("[e2e] restored original.");
                originalBak = null;
            }

            Console.WriteLine("[e2e] PASS.");
            return 0;
        }
        catch (Exception ex) {
            Console.Error.WriteLine("[e2e] error: " + ex.Message);
            try { await CleanupAsync(originalPath, originalBak, partialPath); } catch { }
            return 2;
        }
    }

    private static Task CleanupAsync(string? originalPath, string? originalBak, string? partialPath) {
        if (partialPath is not null && File.Exists(partialPath)) {
            File.Delete(partialPath);
            Console.WriteLine("[e2e] cleanup: deleted partial");
        }
        if (originalBak is not null && originalPath is not null && !File.Exists(originalPath) && File.Exists(originalBak)) {
            File.Move(originalBak, originalPath);
            Console.WriteLine("[e2e] cleanup: restored original");
        }
        return Task.CompletedTask;
    }

    private static async Task<bool> WaitForTypePresenceAsync(string sln, string typeId, bool expectPresent, int waitMs, int maxPoll, CancellationToken ct) {
        for (int i = 0; i < maxPoll; i++) {
            await Task.Delay(waitMs, ct).ConfigureAwait(false);
            var wti = await WorkspaceTextInterface.CreateAsync(sln, ct).ConfigureAwait(false);
            var res = await wti.FindAsync(typeId, limit: 5, offset: 0, json: true, ct).ConfigureAwait(false);
            if (HasTotal(res, out var total)) {
                if (expectPresent && total > 0) {
                    Console.WriteLine($"[e2e] observed PRESENT after {i + 1} polls.");
                    return true;
                }
                if (!expectPresent && total == 0) {
                    Console.WriteLine($"[e2e] observed ABSENT after {i + 1} polls.");
                    return true;
                }
            }
        }
        return false;
    }

    private static bool HasTotal(string json, out int total) {
        try {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("Total", out var t) && t.ValueKind == JsonValueKind.Number) {
                total = t.GetInt32();
                return true;
            }
        }
        catch { }
        total = -1;
        return false;
    }
}
