using CodeCortexV2;
using CodeCortexV2.Workspace;
using Microsoft.CodeAnalysis;

namespace CodeCortexV2.DevCli;

public static class E2eAddMethodCommand {
    public static async Task<int> RunAsync() {
        // Defaults for hands-free automation
        var slnPath = @".\e2e\CodeCortex.E2E\CodeCortex.E2E.sln";
        var targetFqn = "E2E.Target.TypeA"; // stable type in e2e solution
        var waitPollMs = 250;
        var maxPoll = 60; // up to ~15s

        using var cts = new CancellationTokenSource();
        try {
            Console.WriteLine($"[e2e] add-method on {targetFqn} using {slnPath}");

            // 1) Start V2 interface (this wires IndexSynchronizer to track changes)
            var wti = await WorkspaceTextInterface.CreateAsync(slnPath, cts.Token);

            // 2) Resolve source file of the target type using a direct Roslyn load
            var host = await RoslynWorkspaceHost.LoadAsync(slnPath, cts.Token);
            string? filePath = null;
            INamedTypeSymbol? target = null;
            foreach (var proj in host.Workspace.CurrentSolution.Projects) {
                var comp = await proj.GetCompilationAsync(cts.Token).ConfigureAwait(false);
                if (comp is null) { continue; }
                var t = comp.GetTypeByMetadataName(targetFqn);
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

            // 3) Snapshot outline before
            var before = await wti.GetOutlineAsync(targetFqn, limit: 50, offset: 0, json: false, cts.Token);

            // 4) Mutate: inject a new public method with unique name
            var original = await File.ReadAllTextAsync(filePath, cts.Token).ConfigureAwait(false);
            var tick = DateTime.UtcNow.Ticks;
            var methodName = $"E2eAdded_{tick}";
            var methodCode = $"\n    public void {methodName}() {{ System.Console.WriteLine(\"e2e\"); }}\n";
            var mutated = original;
            const string marker = "// E2E_INSERT_HERE";
            if (mutated.Contains(marker)) {
                mutated = mutated.Replace(marker, methodCode + "    " + marker);
            }
            else {
                var lastBrace = mutated.LastIndexOf('}');
                if (lastBrace <= 0) {
                    Console.Error.WriteLine("[e2e] cannot find insertion point.");
                    return 4;
                }
                mutated = mutated.Insert(lastBrace, methodCode);
            }
            if (mutated == original) {
                Console.Error.WriteLine("[e2e] mutation produced no change.");
                return 4;
            }
            await File.WriteAllTextAsync(filePath, mutated, cts.Token).ConfigureAwait(false);

            // 5) Poll until outline reflects the new method.
            // Note: MSBuildWorkspace does not always raise file-change events; recreate WTI to ensure fresh load.
            var found = false;
            for (int i = 0; i < maxPoll; i++) {
                await Task.Delay(waitPollMs, cts.Token).ConfigureAwait(false);
                var probe = await WorkspaceTextInterface.CreateAsync(slnPath, cts.Token).ConfigureAwait(false);
                var outline = await probe.GetOutlineAsync(targetFqn, limit: 50, offset: 0, json: false, cts.Token).ConfigureAwait(false);
                if (outline != null && outline.IndexOf(methodName, StringComparison.Ordinal) >= 0) {
                    found = true;
                    Console.WriteLine($"[e2e] detected method in outline after {i + 1} polls.");
                    break;
                }
            }

            // 6) Revert regardless of result
            await File.WriteAllTextAsync(filePath, original, cts.Token).ConfigureAwait(false);
            Console.WriteLine("[e2e] reverted mutation.");

            if (!found) {
                Console.Error.WriteLine("[e2e] FAIL: new method not observed in outline within timeout.");
                return 5;
            }

            Console.WriteLine("[e2e] PASS.");
            return 0;
        }
        catch (Exception ex) {
            Console.Error.WriteLine("[e2e] error: " + ex.Message);
            return 2;
        }
    }
}
