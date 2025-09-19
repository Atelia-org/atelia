using System.CommandLine;
using CodeCortex.ServiceV2.Services;
using CodeCortex.Workspace;
using Microsoft.CodeAnalysis;

namespace CodeCortex.DevCli;

public static class V2Commands {
    public static Command CreateOutline2() {
        var pathArg = new Argument<string>("path", description: ".sln or .csproj path");
        var fqnArg = new Argument<string>("fqn", description: "Fully-qualified type name (e.g., CodeCortex.Core.Hashing.TypeHasher)");
        var cmd = new Command("outline2", "On-demand outline (ServiceV2, no cache): load workspace, resolve FQN, render outline")
        {
            pathArg, fqnArg
        };
        cmd.SetHandler(
            async (string path, string fqn) => {
                try {
                    var service = new OnDemandOutlineService();
                    var md = await service.GetOutlineByFqnAsync(path, fqn);
                    if (string.IsNullOrEmpty(md)) {
                        Console.Error.WriteLine("Type not found or outline empty.");
                        Environment.ExitCode = 3;
                        return;
                    }
                    Console.WriteLine(md);
                }
                catch (Exception ex) {
                    Console.Error.WriteLine("outline2 error: " + ex.Message);
                    Environment.ExitCode = 2;
                }
            }, pathArg, fqnArg
        );
        return cmd;
    }

    public static Command CreateE2eSim() {
        var defaultSln = @".\\e2e\\CodeCortex.E2E\\CodeCortex.E2E.sln";
        var pathArg = new Argument<string>("path", () => defaultSln, ".sln path for e2e target");
        var fqnArg = new Argument<string>("fqn", () => "E2E.Target.TypeA", "Type FQN to mutate & verify");
        var actionOpt = new Option<string>("--action", () => "add-method", "Action: add-method|xml-doc");
        var revertOpt = new Option<bool>("--revert", () => true, "Revert file after simulation");
        var waitMsOpt = new Option<int>("--wait-ms", () => 800, "Delay after write (ms)");
        var assertPubOpt = new Option<bool>("--assert-public-changed", () => false, "Assert PublicImplHash changed after mutation");
        var cmd = new Command("e2e-sim", "Simulate a source change on a dedicated E2E solution, then verify outline via ServiceV2")
        {
            pathArg, fqnArg, actionOpt, revertOpt, waitMsOpt, assertPubOpt
        };
        cmd.SetHandler(
            async (string slnPath, string fqn, string action, bool revert, int waitMs, bool assertPublicChanged) => {
                string? filePath = null;
                string? original = null;
                try {
                    Console.WriteLine($"[e2e] Using solution: {slnPath}");
                    Console.WriteLine($"[e2e] Target FQN: {fqn} Action={action} Revert={revert}");

                    static string? ParseHash(string outline, string label) {
                        foreach (var line in outline.Split('\n')) {
                            var l = line.Trim();
                            if (l.StartsWith(label, StringComparison.Ordinal)) {
                                var parts = l.Split(':');
                                if (parts.Length >= 2) {
                                    var rest = parts[1].Trim();
                                    var token = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
                                    return token;
                                }
                            }
                        }
                        return null;
                    }

                    // Locate file of the type
                    var loader = new MsBuildWorkspaceLoader();
                    var loaded = await loader.LoadAsync(slnPath);
                    INamedTypeSymbol? target = null;
                    foreach (var p in loaded.Projects) {
                        var comp = await p.GetCompilationAsync();
                        if (comp == null) { continue; }
                        foreach (var t in new RoslynTypeEnumerator().Enumerate(comp)) {
                            var tfqn = t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                            if (tfqn == (fqn.StartsWith("global::") ? fqn : "global::" + fqn)) {
                                target = t;
                                break;
                            }
                        }
                        if (target != null) { break; }
                    }
                    if (target == null) {
                        Console.Error.WriteLine("[e2e] Type not found.");
                        Environment.ExitCode = 3;
                        return;
                    }
                    filePath = target.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.FilePath;
                    if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) {
                        Console.Error.WriteLine("[e2e] Cannot resolve source file.");
                        Environment.ExitCode = 3;
                        return;
                    }
                    Console.WriteLine($"[e2e] Editing file: {filePath}");

                    var svc = new OnDemandOutlineService();
                    var outlineBefore = await svc.GetOutlineByFqnAsync(slnPath, fqn);
                    var pubHashBefore = outlineBefore != null ? ParseHash(outlineBefore, "PublicImplHash:") : null;

                    original = await File.ReadAllTextAsync(filePath);
                    var mutated = original;
                    var ts = DateTime.UtcNow.Ticks;
                    if (action == "add-method") {
                        var method = $"\n    public void E2eAdded_{ts}() {{ System.Console.WriteLine(\"e2e\"); }}\n";
                        const string marker = "// E2E_INSERT_HERE";
                        if (mutated.Contains(marker)) {
                            mutated = mutated.Replace(marker, method + "    " + marker);
                        }
                        else {
                            // fallback: insert before last '}'
                            var idx = mutated.LastIndexOf('}');
                            if (idx > 0) {
                                mutated = mutated.Insert(idx, method);
                            }
                        }
                    }
                    else if (action == "xml-doc") {
                        // naive: toggle a trailing xml doc line
                        mutated = mutated.Replace("/// <summary>", "/// <summary>\n/// (e2e xml-doc change)");
                    }

                    if (mutated == original) {
                        Console.Error.WriteLine("[e2e] Mutation produced no change.");
                        Environment.ExitCode = 4;
                        return;
                    }
                    await File.WriteAllTextAsync(filePath, mutated);
                    await Task.Delay(Math.Max(0, waitMs));

                    // Verify via ServiceV2 (no cache path)
                    var outline = await svc.GetOutlineByFqnAsync(slnPath, fqn);
                    if (outline == null) {
                        Console.Error.WriteLine("[e2e] Outline is null after mutation.");
                        Environment.ExitCode = 5;
                        return;
                    }
                    var pubHashAfter = ParseHash(outline, "PublicImplHash:");
                    if (assertPublicChanged && outlineBefore != null) {
                        if (pubHashBefore == pubHashAfter) {
                            Console.Error.WriteLine($"[e2e] ASSERT FAIL: PublicImplHash unchanged ({pubHashAfter}).");
                            Environment.ExitCode = 6;
                            return;
                        }
                        Console.WriteLine($"[e2e] ASSERT OK: PublicImplHash changed {pubHashBefore} -> {pubHashAfter}");
                    }

                    Console.WriteLine("[e2e] Outline length: " + outline.Length);
                    Console.WriteLine("[e2e] Preview:\n" + outline.Split('\n').Take(12).Aggregate(string.Empty, (a, b) => a + b + "\n"));

                    if (revert) {
                        await File.WriteAllTextAsync(filePath, original);
                        Console.WriteLine("[e2e] Reverted mutation.");
                    }
                    Console.WriteLine("[e2e] Done.");
                }
                catch (Exception ex) {
                    Console.Error.WriteLine("[e2e] Error: " + ex.Message);
                    if (revert && filePath != null && original != null) {
                        try {
                            await File.WriteAllTextAsync(filePath, original);
                            Console.WriteLine("[e2e] Reverted due to error.");
                        }
                        catch { }
                    }
                    Environment.ExitCode = 2;
                }
            }, pathArg, fqnArg, actionOpt, revertOpt, waitMsOpt, assertPubOpt
        );
        return cmd;
    }
}

