using System.Text.Json;
using CodeCortexV2;
using CodeCortexV2.Workspace;
using Microsoft.CodeAnalysis;

namespace CodeCortexV2.DevCli;

public static class E2eNamespaceDeepCascadeCommand {
    // Zero-arg, defaults-only command for CI-friendly runs
    public static async Task<int> RunAsync() {
        var slnPath = @".\e2e\CodeCortex.E2E\CodeCortex.E2E.sln";
        var rootNs = "E2E.TempCascade"; // must not have other children to allow full cascade under this branch
        var deepNs = rootNs + ".N1.N2.N3";
        var typeName = "DeepType_E2E";
        var typeFqn = deepNs + "." + typeName;
        var typeId = "T:" + typeFqn;
        var nsIds = new[] {
            "N:" + deepNs,
            "N:" + rootNs + ".N1.N2",
            "N:" + rootNs + ".N1",
            "N:" + rootNs,
        };

        var waitPollMs = 250;
        var maxPoll = 60; // ~15s

        string? filePath = null;
        using var cts = new CancellationTokenSource();
        try {
            Console.WriteLine($"[e2e] namespace-deep-cascade using {slnPath} at {deepNs}");

            // Locate a project folder; reuse the folder that contains E2E.Target.TypeA for simplicity
            var host = await RoslynWorkspaceHost.LoadAsync(slnPath, cts.Token);
            INamedTypeSymbol? anchor = null;
            foreach (var proj in host.Workspace.CurrentSolution.Projects) {
                var comp = await proj.GetCompilationAsync(cts.Token).ConfigureAwait(false);
                if (comp is null) {
                    continue;
                }

                var t = comp.GetTypeByMetadataName("E2E.Target.TypeA");
                if (t is not null) {
                    anchor = t;
                    break;
                }
            }
            if (anchor is null) {
                Console.Error.WriteLine("[e2e] anchor type not found.");
                return 3;
            }
            var anchorPath = anchor.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.FilePath;
            if (string.IsNullOrEmpty(anchorPath)) {
                Console.Error.WriteLine("[e2e] cannot resolve anchor file path.");
                return 3;
            }
            var dir = Path.GetDirectoryName(anchorPath)!;
            filePath = Path.Combine(dir, "E2E_NsCascade_e2e.cs");

            // Ensure clean slate if previous run left artifacts
            if (File.Exists(filePath)) {
                File.Delete(filePath);
            }

            // 1) Create nested namespace + type
            var content = $"namespace {deepNs} {{ public class {typeName} {{ /* e2e */ }} }}\n";
            await File.WriteAllTextAsync(filePath, content, cts.Token).ConfigureAwait(false);
            Console.WriteLine($"[e2e] created file: {filePath}");

            // 2) Wait for deepest namespace and type to APPEAR
            if (!await WaitDocIdPresenceAsync(slnPath, typeId, expectPresent: true, waitPollMs, maxPoll, cts.Token)) {
                Console.Error.WriteLine("[e2e] FAIL: type not observed after creation.");
                await CleanupAsync(filePath);
                return 5;
            }
            if (!await WaitDocIdPresenceAsync(slnPath, nsIds[0], expectPresent: true, waitPollMs, maxPoll, cts.Token)) {
                Console.Error.WriteLine("[e2e] FAIL: deepest namespace not observed after creation.");
                await CleanupAsync(filePath);
                return 5;
            }

            // 3) Remove the file -> expect cascade removal of all temp namespaces under rootNs
            await CleanupAsync(filePath);
            Console.WriteLine("[e2e] deleted temp file.");

            // Wait for type to disappear
            if (!await WaitDocIdPresenceAsync(slnPath, typeId, expectPresent: false, waitPollMs, maxPoll, cts.Token)) {
                Console.Error.WriteLine("[e2e] FAIL: type still present after deletion.");
                return 5;
            }

            // Then each namespace id (deep → up to rootNs) must be ABSENT; E2E remains due to other content
            foreach (var nsId in nsIds) {
                if (!await WaitDocIdPresenceAsync(slnPath, nsId, expectPresent: false, waitPollMs, maxPoll, cts.Token)) {
                    Console.Error.WriteLine($"[e2e] FAIL: namespace still present after deletion: {nsId}");
                    return 5;
                }
            }

            Console.WriteLine("[e2e] PASS.");
            return 0;
        } catch (Exception ex) {
            Console.Error.WriteLine("[e2e] error: " + ex.Message);
            try { await CleanupAsync(filePath); } catch { }
            return 2;
        }
    }

    private static async Task<bool> WaitDocIdPresenceAsync(string sln, string docId, bool expectPresent, int waitMs, int maxPoll, CancellationToken ct) {
        for (int i = 0; i < maxPoll; i++) {
            await Task.Delay(waitMs, ct).ConfigureAwait(false);
            var wti = await WorkspaceTextInterface.CreateAsync(sln, ct).ConfigureAwait(false);
            var res = await wti.FindAsync(docId, limit: 5, offset: 0, json: true, ct).ConfigureAwait(false);
            if (HasTotal(res, out var total)) {
                bool present = total > 0;
                if (expectPresent && present) {
                    Console.WriteLine($"[e2e] observed PRESENT for {docId} after {i + 1} polls.");
                    return true;
                }
                if (!expectPresent && !present) {
                    Console.WriteLine($"[e2e] observed ABSENT for {docId} after {i + 1} polls.");
                    return true;
                }
            }
        }
        return false;
    }

    private static Task CleanupAsync(string? filePath) {
        if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath)) {
            File.Delete(filePath);
        }
        return Task.CompletedTask;
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
