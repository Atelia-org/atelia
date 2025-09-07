using System.Text.Json;
using CodeCortexV2.Abstractions;
using CodeCortexV2.Index;
using CodeCortexV2.Workspace;

if (args.Length == 0 || args[0] is "-h" or "--help") {
    Console.WriteLine(
        "CodeCortexV2.DevCli - dev-time one-shot CLI\n" +
                      "Usage:\n  ccv2 <sln|csproj> find <query> [--limit N] [--offset M] [--json]\n  ccv2 <sln|csproj> outline <query-or-id> [--limit N] [--offset M] [--json|--md]\n  ccv2 <sln|csproj> source <typeId>\n"
    );
    return 0;
}

var sln = args[0];
if (args.Length < 2) {
    Console.WriteLine("Missing command. Try --help");
    return 1;
}
var cmd = args[1];
using var cts = new CancellationTokenSource();
var host = await RoslynWorkspaceHost.LoadAsync(sln, cts.Token);

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
        var index = await SymbolIndex.BuildAsync(host.Workspace.CurrentSolution, cts.Token);
        var page = await index.SearchAsync(query, null, limit, offset, cts.Token);
        if (json) {
            var jsonStr = JsonSerializer.Serialize(page, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(jsonStr);
        } else {
            Console.WriteLine($"Results {page.Items.Count}/{page.Total}, offset={page.Offset}, limit={page.Limit}{(page.NextOffset is int no ? ", nextOffset=" + no : string.Empty)}:");
            foreach (var h in page.Items) {
                var id = h.SymbolId.Value ?? string.Empty;
                var amb = h.IsAmbiguous ? " !ambiguous" : string.Empty;
                Console.WriteLine($"- [{h.Kind}/{h.MatchKind}] {id}{amb} {(string.IsNullOrEmpty(h.Assembly) ? string.Empty : "(asm: " + h.Assembly + ")")}");
            }
        }
        return 0;
    }
    case "outline": {
        if (args.Length < 3) {
            Console.WriteLine("Missing query-or-id. Usage: ccv2 <sln> outline <query-or-id> [--limit N] [--offset M] [--json|--md]");
            return 1;
        }
        var query = args[2];
        int limit = 30;
        int offset = 0;
        bool json = false; // default Markdown
        for (int i = 3; i < args.Length; i++) {
            if (args[i] == "--json") {
                json = true;
            } else if (args[i] == "--md") {
                json = false;
            } else if (args[i] == "--limit" && i + 1 < args.Length && int.TryParse(args[i + 1], out var n)) {
                limit = n;
                i++;
            } else if (args[i] == "--offset" && i + 1 < args.Length && int.TryParse(args[i + 1], out var m)) {
                offset = m;
                i++;
            }
        }
        var index = await SymbolIndex.BuildAsync(host.Workspace.CurrentSolution, cts.Token);
        var page = await index.SearchAsync(query, null, limit, offset, cts.Token);
        if (page.Total == 0) {
            Console.WriteLine("未找到匹配。注意：目前仅支持查询 workspace 中定义的类型（DocCommentId T:...）。");
            return 0;
        }
        if (page.Total > 1) {
            // Fallback to find listing
            if (json) {
                var jsonStr = JsonSerializer.Serialize(page, new JsonSerializerOptions { WriteIndented = true });
                Console.WriteLine(jsonStr);
            } else {
                Console.WriteLine($"Results {page.Items.Count}/{page.Total}, offset={page.Offset}, limit={page.Limit}{(page.NextOffset is int no ? ", nextOffset=" + no : string.Empty)}:");
                foreach (var h in page.Items) {
                    var id = h.SymbolId.Value ?? string.Empty;
                    var amb = h.IsAmbiguous ? " !ambiguous" : string.Empty;
                    Console.WriteLine($"- [{h.Kind}/{h.MatchKind}] {id}{amb} {(string.IsNullOrEmpty(h.Assembly) ? string.Empty : "(asm: " + h.Assembly + ")")}");
                }
            }
            return 0;
        }
        // Unique → render outline
        var unique = page.Items[0];
        // Resolve type symbol via DocCommentId
        var typeSym = ResolveTypeByDocId(host.Workspace.CurrentSolution, unique.SymbolId.Value);
        if (typeSym is null) {
            Console.WriteLine("找到 1 个匹配，但未能解析类型符号（DocCommentId 可能不在当前编译环境）。");
            return 1;
        }
        var provider = new CodeCortexV2.Providers.TypeOutlineProvider(id => id.Value == unique.SymbolId.Value ? typeSym : ResolveTypeByDocId(host.Workspace.CurrentSolution, id.Value));
        var outline = await provider.GetTypeOutlineAsync(unique.SymbolId, new OutlineOptions(Markdown: true), cts.Token);
        if (json) {
            var jsonStr = JsonSerializer.Serialize(outline, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(jsonStr);
        } else {
            // Minimal Markdown rendering
            Console.WriteLine($"# {outline.Name}");
            if (!string.IsNullOrWhiteSpace(outline.Summary)) {
                Console.WriteLine(outline.Summary);
            }

            foreach (var m in outline.Members) {
                Console.WriteLine($"- `{m.Signature}`");
                if (!string.IsNullOrWhiteSpace(m.Summary)) {
                    Console.WriteLine($"  - {m.Summary}");
                }
            }
        }
        return 0;
    }
    default:
        Console.WriteLine($"Unknown command '{cmd}'. Try --help");
        return 1;
}

static Microsoft.CodeAnalysis.INamedTypeSymbol? ResolveTypeByDocId(Microsoft.CodeAnalysis.Solution solution, string docId) {
    if (string.IsNullOrWhiteSpace(docId) || !docId.StartsWith("T:", StringComparison.Ordinal)) {
        return null;
    }

    var meta = docId.Substring(2);
    foreach (var proj in solution.Projects) {
        var comp = proj.GetCompilationAsync().GetAwaiter().GetResult();
        if (comp is null) {
            continue;
        }

        var t = comp.GetTypeByMetadataName(meta);
        if (t is not null) {
            return t;
        }
    }
    return null;
}

