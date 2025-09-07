using System.Text.Json;
using CodeCortexV2.Abstractions;
using CodeCortexV2.Index;
using CodeCortexV2.Workspace;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("CodeCortexV2.DevCli - dev-time one-shot CLI\n" +
                      "Usage:\n  ccv2 <sln|csproj> find <query> [--limit N] [--json]\n  ccv2 <sln|csproj> outline <symbolId> [--level type|member|namespace|assembly] [--md]\n  ccv2 <sln|csproj> source <typeId>\n");
    return 0;
}

var sln = args[0];
if (args.Length < 2)
{
    Console.WriteLine("Missing command. Try --help");
    return 1;
}
var cmd = args[1];
using var cts = new CancellationTokenSource();
var host = await RoslynWorkspaceHost.LoadAsync(sln, cts.Token);

switch (cmd)
{
    case "find":
    {
        if (args.Length < 3)
        {
            Console.WriteLine("Missing query. Usage: ccv2 <sln> find <query> [--limit N] [--json]");
            return 1;
        }
        var query = args[2];
        int limit = 30;
        bool json = false;
        for (int i = 3; i < args.Length; i++)
        {
            if (args[i] == "--json") json = true;
            else if (args[i] == "--limit" && i + 1 < args.Length && int.TryParse(args[i + 1], out var n)) { limit = n; i++; }
        }
        var index = await SymbolIndex.BuildAsync(host.Workspace.CurrentSolution, cts.Token);
        var hits = await index.SearchAsync(query, null, limit, cts.Token);
        if (json)
        {
            var jsonStr = JsonSerializer.Serialize(hits, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(jsonStr);
        }
        else
        {
            Console.WriteLine($"Results ({hits.Count}):");
            foreach (var h in hits)
            {
                var idShort = h.SymbolId.Value?.Length > 24 ? h.SymbolId.Value.Substring(0, 24) + "…" : h.SymbolId.Value;
                Console.WriteLine($"- [{h.Kind}/{h.MatchKind}] {h.Name} {(string.IsNullOrEmpty(h.Assembly) ? string.Empty : "(asm: " + h.Assembly + ")")} — id: {idShort}");
            }
        }
        return 0;
    }
    default:
        Console.WriteLine($"Unknown command '{cmd}'. Try --help");
        return 1;
}

