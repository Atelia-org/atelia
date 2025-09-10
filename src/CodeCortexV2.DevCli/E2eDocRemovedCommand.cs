using System.Text.Json;
using CodeCortexV2;
using CodeCortexV2.Workspace;
using Microsoft.CodeAnalysis;

namespace CodeCortexV2.DevCli;

public static class E2eDocRemovedCommand {
    // Zero-arg, defaults-only command for CI-friendly runs
    public static async Task<int> RunAsync() {
        var slnPath = @".\e2e\CodeCortex.E2E\CodeCortex.E2E.sln";
        var targetFqn = "E2E.Target.TypeA";
        var typeId = "T:" + targetFqn;
        var waitPollMs = 250;
        var maxPoll = 60; // ~15s

        string? filePath = null;
        string? movedPath = null;

        using var cts = new CancellationTokenSource();
        try {
            Console.WriteLine($"[e2e] doc-removed on {targetFqn} using {slnPath}");

            // Pre-flight: ensure the type exists
            var pre = await WorkspaceTextInterface.CreateAsync(slnPath, cts.Token);
            var preJson = await pre.FindAsync(typeId, limit: 5, offset: 0, json: true, cts.Token);
            if (!HasTotal(preJson, out var preTotal) || preTotal == 0) {
                Console.Error.WriteLine("[e2e] precondition failed: type not found before removal.");
                return 3;
            }

            // Locate source file via Roslyn
            var host = await RoslynWorkspaceHost.LoadAsync(slnPath, cts.Token);
            INamedTypeSymbol? target = null;
            foreach (var proj in host.Workspace.CurrentSolution.Projects) {
                var comp = await proj.GetCompilationAsync(cts.Token).ConfigureAwait(false);
                if (comp is null) {
                    continue;
                }

                target = comp.GetTypeByMetadataName(targetFqn);
                if (target is not null) {
                    break;
                }
            }
            if (target is null) {
                Console.Error.WriteLine("[e2e] target type not found via Roslyn.");
                return 3;
            }
            filePath = target.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.FilePath;
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) {
                Console.Error.WriteLine("[e2e] cannot locate source file.");
                return 3;
            }
            Console.WriteLine($"[e2e] removing file: {filePath}");

            // Move file out of Compile globs without editing csproj (avoid *.cs)
            movedPath = filePath + ".e2e.bak";
            File.Move(filePath, movedPath, overwrite: true);

            // Poll until type disappears from index (full reload per probe)
            var removed = false;
            for (int i = 0; i < maxPoll; i++) {
                await Task.Delay(waitPollMs, cts.Token).ConfigureAwait(false);
                var probe = await WorkspaceTextInterface.CreateAsync(slnPath, cts.Token).ConfigureAwait(false);
                var res = await probe.FindAsync(typeId, limit: 5, offset: 0, json: true, cts.Token).ConfigureAwait(false);
                if (HasTotal(res, out var total) && total == 0) {
                    removed = true;
                    Console.WriteLine($"[e2e] type removed observed after {i + 1} polls.");
                    break;
                }
            }

            // Optional: conservative namespace cascade check
            if (removed) {
                var parentNs = GetParentNamespace(targetFqn);
                if (!string.IsNullOrEmpty(parentNs)) {
                    var nsProbe = await WorkspaceTextInterface.CreateAsync(slnPath, cts.Token).ConfigureAwait(false);
                    var nsChildren = await nsProbe.FindAsync(parentNs + ".", limit: 5, offset: 0, json: true, cts.Token).ConfigureAwait(false);
                    if (HasTotal(nsChildren, out var nsTotal) && nsTotal == 0) {
                        var nsId = "N:" + parentNs;
                        var nsSelf = await nsProbe.FindAsync(nsId, limit: 5, offset: 0, json: true, cts.Token).ConfigureAwait(false);
                        if (HasTotal(nsSelf, out var nsSelfTotal) && nsSelfTotal != 0) {
                            Console.Error.WriteLine($"[e2e] namespace expected to be removed but still present: {nsId}");
                            // not a hard failure: keep as warning because other children might exist transiently
                        } else {
                            Console.WriteLine($"[e2e] namespace removed as expected (no children): {nsId}");
                        }
                    }
                }
            }

            // Revert
            if (movedPath is not null && !File.Exists(filePath)) {
                File.Move(movedPath, filePath!);
                Console.WriteLine("[e2e] restored file.");
            }

            if (!removed) {
                Console.Error.WriteLine("[e2e] FAIL: type not removed within timeout.");
                return 5;
            }

            Console.WriteLine("[e2e] PASS.");
            return 0;
        } catch (Exception ex) {
            Console.Error.WriteLine("[e2e] error: " + ex.Message);
            try {
                if (movedPath is not null && filePath is not null && !File.Exists(filePath) && File.Exists(movedPath)) {
                    File.Move(movedPath, filePath);
                    Console.WriteLine("[e2e] restored file after error.");
                }
            } catch { }
            return 2;
        }
    }

    private static string GetParentNamespace(string fqn) {
        var idx = fqn.LastIndexOf('.');
        return idx > 0 ? fqn.Substring(0, idx) : string.Empty;
    }

    private static bool HasTotal(string json, out int total) {
        try {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("Total", out var t) && t.ValueKind == JsonValueKind.Number) {
                total = t.GetInt32();
                return true;
            }
        } catch { }
        total = -1;
        return false;
    }
}
