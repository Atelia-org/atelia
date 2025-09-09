using System.Text.Json;
using CodeCortexV2;


if (args.Length == 0 || args[0] is "-h" or "--help") {
    Console.WriteLine(
        "CodeCortexV2.DevCli - dev-time one-shot CLI\n" +
                      "Usage:\n  ccv2 <sln|csproj> find <query> [--limit N] [--offset M] [--kind namespace|type|method|property|field|event] [--json]\n  ccv2 <sln|csproj> outline <query-or-id> [--limit N] [--offset M]\n  ccv2 <sln|csproj> source <typeId>\n"
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
var service = await CodeCortexService.CreateAsync(sln, cts.Token);

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
        CodeCortexV2.Abstractions.SymbolKind? kind = null;
        for (int i = 3; i < args.Length; i++) {
            if (args[i] == "--json") {
                json = true;
            } else if (args[i] == "--limit" && i + 1 < args.Length && int.TryParse(args[i + 1], out var n)) {
                limit = n;
                i++;
            } else if (args[i] == "--offset" && i + 1 < args.Length && int.TryParse(args[i + 1], out var m)) {
                offset = m;
                i++;
            } else if (args[i] == "--kind" && i + 1 < args.Length) {
                var v = args[i + 1];
                i++;
                if (Enum.TryParse<CodeCortexV2.Abstractions.SymbolKind>(v, true, out var parsed)) {
                    kind = parsed;
                } else {
                    Console.WriteLine($"Unknown --kind '{v}', ignored. Use: namespace|type|method|property|field|event");
                }
            }
        }
        var page = await service.FindAsync(query, kind, limit, offset, cts.Token);
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
            Console.WriteLine("Missing query-or-id. Usage: ccv2 <sln> outline <query-or-id> [--limit N] [--offset M]");
            return 1;
        }
        var query = args[2];
        int limit = 30;
        int offset = 0;
        for (int i = 3; i < args.Length; i++) {
            if (args[i] == "--limit" && i + 1 < args.Length && int.TryParse(args[i + 1], out var n)) {
                limit = n;
                i++;
            } else if (args[i] == "--offset" && i + 1 < args.Length && int.TryParse(args[i + 1], out var m)) {
                offset = m;
                i++;
            }
        }
        var md = await service.GetOutlineMarkdownAsync(query, limit, offset, baseHeadingLevel: 3, maxAtxLevel: 4, cts.Token);
        Console.WriteLine(md);
        return 0;
    }
    default:
        Console.WriteLine($"Unknown command '{cmd}'. Try --help");
        return 1;
}

