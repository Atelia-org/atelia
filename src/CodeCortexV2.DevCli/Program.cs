using System.Text.Json;
using CodeCortexV2;
using CodeCortexV2.Workspace;
using Microsoft.CodeAnalysis;


if (args.Length == 0 || args[0] is "-h" or "--help") {
    Console.WriteLine(
        "CodeCortexV2.DevCli - dev-time one-shot CLI\n" +
                    "Usage:\n  ccv2 <sln|csproj> find <query> [--limit N] [--offset M] [--json]\n  ccv2 <sln|csproj> outline <query-or-id> [--limit N] [--offset M] [--json]\n\n  # E2E test commands (no arguments; use defaults)\n  ccv2 e2e-add-method\n  ccv2 e2e-doc-removed\n  ccv2 e2e-partial-type-remove\n  ccv2 e2e-namespace-deep-cascade\n  ccv2 e2e-suffix-ambiguity\n"
    );
    return 0;
}

// E2E commands (no-arg; defaulted paths)
if (args[0] == "e2e-add-method") {
    return await RunE2eAddMethodAsync();
}
if (args[0] == "e2e-doc-removed") {
    return await CodeCortexV2.DevCli.E2eDocRemovedCommand.RunAsync();
}
if (args[0] == "e2e-partial-type-remove") {
    return await CodeCortexV2.DevCli.E2ePartialTypeRemoveCommand.RunAsync();
}
if (args[0] == "e2e-namespace-deep-cascade") {
    return await CodeCortexV2.DevCli.E2eNamespaceDeepCascadeCommand.RunAsync();
}
if (args[0] == "e2e-suffix-ambiguity") {
    return await CodeCortexV2.DevCli.E2eSuffixAmbiguityCommand.RunAsync();
}

var sln = args[0];
if (args.Length < 2) {
    Console.WriteLine("Missing command. Try --help");
    return 1;
}
var cmd = args[1];
using var cts = new CancellationTokenSource();
var service = await WorkspaceTextInterface.CreateAsync(sln, cts.Token);

switch (cmd) {
    case "find": {
        if (args.Length < 3) {
            Console.WriteLine("Missing query. Usage: ccv2 <sln> find <query> [--limit N] [--json]");
            return 1;
        }
        var query = args[2];
        int limit = 30;
        int offset = 0;
        bool json = false;
        for (int i = 3; i < args.Length; i++) {
            if (args[i] == "--json") {
                json = true;
            } else if (args[i] == "--limit" && i + 1 < args.Length && int.TryParse(args[i + 1], out var n)) {
                limit = n;
                i++;
            } else if (args[i] == "--offset" && i + 1 < args.Length && int.TryParse(args[i + 1], out var m)) {
                offset = m;
                i++;
            }
        }
        var output = await service.FindAsync(query, limit, offset, json, cts.Token);
        Console.WriteLine(output);
        return 0;
    }
    case "outline": {
        if (args.Length < 3) {
            Console.WriteLine("Missing query-or-id. Usage: ccv2 <sln> outline <query-or-id> [--limit N] [--offset M] [--json]");
            return 1;
        }
        var query = args[2];
        int limit = 30;
        int offset = 0;
        bool json = false;
        for (int i = 3; i < args.Length; i++) {
            if (args[i] == "--json") {
                json = true;
            } else if (args[i] == "--limit" && i + 1 < args.Length && int.TryParse(args[i + 1], out var n)) {
                limit = n;
                i++;
            } else if (args[i] == "--offset" && i + 1 < args.Length && int.TryParse(args[i + 1], out var m)) {
                offset = m;
                i++;
            }
        }
        var output = await service.GetOutlineAsync(query, limit, offset, json, cts.Token);
        Console.WriteLine(output);
        return 0;
    }
    default:
        Console.WriteLine($"Unknown command '{cmd}'. Try --help");
        return 1;
}

static async Task<int> RunE2eAddMethodAsync() {
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
            if (comp is null) {
                continue;
            }

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
        } else {
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
    } catch (Exception ex) {
        Console.Error.WriteLine("[e2e] error: " + ex.Message);
        return 2;
    }
}

