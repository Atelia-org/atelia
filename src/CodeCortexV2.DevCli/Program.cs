using System.Text.Json;
using CodeCortexV2;
using CodeCortexV2.Workspace;
using Microsoft.CodeAnalysis;


if (args.Length == 0 || args[0] is "-h" or "--help") {
    Console.WriteLine(
        "CodeCortexV2.DevCli - dev-time one-shot CLI\n" +
                      "Usage:\n  ccv2 <sln|csproj> find <query> [--limit N] [--offset M] [--json] [--engine symbolindex|symboltree|symboltreeb]\n" +
                      "  ccv2 <sln|csproj> outline <query-or-id> [--limit N] [--offset M] [--json] [--engine symbolindex|symboltree|symboltreeb]\n\n" +
                      "  # E2E test commands (no arguments; use defaults)\n" +
                      "  ccv2 e2e-add-method\n" +
                      "  ccv2 e2e-generic-base-search\n" +
                      "  ccv2 e2e-doc-removed\n" +
                      "  ccv2 e2e-partial-type-remove\n" +
                      "  ccv2 e2e-namespace-deep-cascade\n" +
                      "  ccv2 e2e-suffix-ambiguity\n" +
                      "  ccv2 e2e-case-insensitive-exact\n" +
                      "  ccv2 e2e-exact-without-global\n" +
                      "  ccv2 e2e-wildcard-search\n" +
                      "  ccv2 e2e-fuzzy-fallback\n" +
                      "  ccv2 e2e-debounce-batch\n" +
                      "  ccv2 e2e-with-delta-upsert-order\n" +
                      "  ccv2 e2e-project-removed\n" +
                      "  ccv2 e2e-all\n"
    );
    return 0;
}

// E2E commands (no-arg; defaulted paths)
if (args[0] == "e2e-add-method") {
    return await CodeCortexV2.DevCli.E2eAddMethodCommand.RunAsync();
}
if (args[0] == "e2e-wildcard-search") {
    return await CodeCortexV2.DevCli.E2eWildcardSearchCommand.RunAsync();
}
if (args[0] == "e2e-generic-base-search") {
    return await CodeCortexV2.DevCli.E2eGenericBaseSearchCommand.RunAsync();
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
if (args[0] == "e2e-case-insensitive-exact") {
    return await CodeCortexV2.DevCli.E2eCaseInsensitiveExactCommand.RunAsync();
}
if (args[0] == "e2e-exact-without-global") {
    return await CodeCortexV2.DevCli.E2eExactWithoutGlobalCommand.RunAsync();
}
if (args[0] == "e2e-fuzzy-fallback") {
    return await CodeCortexV2.DevCli.E2eFuzzyFallbackCommand.RunAsync();
}
if (args[0] == "e2e-debounce-batch") {
    return await CodeCortexV2.DevCli.E2eDebounceBatchCommand.RunAsync();
}
if (args[0] == "e2e-with-delta-upsert-order") {
    return await CodeCortexV2.DevCli.E2eWithDeltaUpsertOrderCommand.RunAsync();
}
if (args[0] == "e2e-project-removed") {
    return await CodeCortexV2.DevCli.E2eProjectRemovedCommand.RunAsync();
}
if (args[0] == "e2e-all") {
    return await CodeCortexV2.DevCli.E2eAllCommand.RunAsync();
}

var sln = args[0];
if (args.Length < 2) {
    Console.WriteLine("Missing command. Try --help");
    return 1;
}
var cmd = args[1];
using var cts = new CancellationTokenSource();

switch (cmd) {
    case "find": {
        if (args.Length < 3) {
            Console.WriteLine("Missing query. Usage: ccv2 <sln> find <query> [--limit N] [--offset M] [--json] [--engine symbolindex|symboltree]");
            return 1;
        }
        var query = args[2];
        int limit = 30;
        int offset = 0;
        bool json = false;
        var engineMode = SearchEngineMode.SymbolIndex;
        for (int i = 3; i < args.Length; i++) {
            if (args[i] == "--json") {
                json = true;
            } else if (args[i] == "--limit" && i + 1 < args.Length && int.TryParse(args[i + 1], out var n)) {
                limit = n;
                i++;
            } else if (args[i] == "--offset" && i + 1 < args.Length && int.TryParse(args[i + 1], out var m)) {
                offset = m;
                i++;
            } else if (args[i] == "--engine" && i + 1 < args.Length) {
                var v = args[i + 1].ToLowerInvariant();
                if (v is "symboltreeb" or "treeb" or "b") {
                    engineMode = SearchEngineMode.SymbolTreeB;
                } else if (v is "symboltree" or "tree") {
                    engineMode = SearchEngineMode.SymbolTree;
                } else {
                    engineMode = SearchEngineMode.SymbolIndex;
                }

                i++;
            }
        }
        var service = await WorkspaceTextInterface.CreateAsync(sln, engineMode, cts.Token);
        var output = await service.FindAsync(query, limit, offset, json, cts.Token);
        Console.WriteLine(output);
        return 0;
    }
    case "outline": {
        if (args.Length < 3) {
            Console.WriteLine("Missing query-or-id. Usage: ccv2 <sln> outline <query-or-id> [--limit N] [--offset M] [--json] [--engine symbolindex|symboltree]");
            return 1;
        }
        var query = args[2];
        int limit = 30;
        int offset = 0;
        bool json = false;
        var engineMode = SearchEngineMode.SymbolIndex;
        for (int i = 3; i < args.Length; i++) {
            if (args[i] == "--json") {
                json = true;
            } else if (args[i] == "--limit" && i + 1 < args.Length && int.TryParse(args[i + 1], out var n)) {
                limit = n;
                i++;
            } else if (args[i] == "--offset" && i + 1 < args.Length && int.TryParse(args[i + 1], out var m)) {
                offset = m;
                i++;
            } else if (args[i] == "--engine" && i + 1 < args.Length) {
                var v = args[i + 1].ToLowerInvariant();
                if (v is "symboltreeb" or "treeb" or "b") {
                    engineMode = SearchEngineMode.SymbolTreeB;
                } else if (v is "symboltree" or "tree") {
                    engineMode = SearchEngineMode.SymbolTree;
                } else {
                    engineMode = SearchEngineMode.SymbolIndex;
                }

                i++;
            }
        }
        var service = await WorkspaceTextInterface.CreateAsync(sln, engineMode, cts.Token);
        var output = await service.GetOutlineAsync(query, limit, offset, json, cts.Token);
        Console.WriteLine(output);
        return 0;
    }
    default:
        Console.WriteLine($"Unknown command '{cmd}'. Try --help");
        return 1;
}

// e2e-add-method is implemented in E2eAddMethodCommand

