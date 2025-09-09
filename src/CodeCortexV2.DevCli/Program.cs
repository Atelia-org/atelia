using System.Text.Json;
using System.Linq;
using Microsoft.CodeAnalysis;
using CodeCortexV2.Abstractions;
using CodeCortexV2.Index;
using CodeCortexV2.Workspace;
using CodeCortexV2.Formatting;

if (args.Length == 0 || args[0] is "-h" or "--help") {
    Console.WriteLine(
        "CodeCortexV2.DevCli - dev-time one-shot CLI\n" +
                      "Usage:\n  ccv2 <sln|csproj> find <query> [--limit N] [--offset M] [--kind namespace|type|method|property|field|event] [--json]\n  ccv2 <sln|csproj> outline <query-or-id> [--limit N] [--offset M] [--json|--md]\n  ccv2 <sln|csproj> source <typeId>\n"
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
        var index = await SymbolIndex.BuildAsync(host.Workspace.CurrentSolution, cts.Token);
        var page = await index.SearchAsync(query, kind, limit, offset, cts.Token);
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
        var page = await index.SearchAsync(query, CodeCortexV2.Abstractions.SymbolKind.Type, limit, offset, cts.Token);
        if (page.Total == 0) {
            // 尝试按命名空间渲染（支持 "N:..." 或 FQN 名称）→ 使用 Provider 生成“伪 TypeOutline”
            var nsId = await index.ResolveAsync(query, cts.Token);
            if (nsId is null && !query.StartsWith("N:", StringComparison.Ordinal)) {
                nsId = await index.ResolveAsync("N:" + query, cts.Token);
            }
            if (nsId is null) {
                Console.WriteLine("未找到匹配。支持：类型 (T:...) 与命名空间 (N:... 或 FQN)。");
                return 0;
            }

            var nsProvider = new CodeCortexV2.Providers.NamespaceOutlineProvider(id => ResolveSymbolByDocId(host.Workspace.CurrentSolution, id.Value));
            var nsOutline = await nsProvider.GetNamespaceAsTypeOutlineAsync(nsId.Value, new OutlineOptions(Markdown: true), cts.Token);
            if (json) {
                var jsonStr = JsonSerializer.Serialize(nsOutline, new JsonSerializerOptions { WriteIndented = true });
                Console.WriteLine(jsonStr);
            } else {
                var md = CodeCortexV2.Formatting.MarkdownLayout.RenderTypeOutline(nsOutline);
                Console.WriteLine(md);
            }
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
            // New --json semantics: emit blocks-based structured doc for the type
            var name = typeSym.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            var fqn = typeSym.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty);
            var docId = DocumentationCommentId.CreateDeclarationId(typeSym) ?? fqn;
            var asm = typeSym.ContainingAssembly?.Name;
            var file = typeSym.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree?.FilePath;

            var members = typeSym
                .GetMembers()
                .Where(m => IsPublicApiMember(m) && !(m is IMethodSymbol ms && (ms.MethodKind == MethodKind.PropertyGet || ms.MethodKind == MethodKind.PropertySet || ms.MethodKind == MethodKind.EventAdd || ms.MethodKind == MethodKind.EventRemove)))
                .OrderBy(m => m.Name)
                .Select(
                m => new {
                    Kind = m.Kind.ToString(),
                    Name = m.Name,
                    Signature = SignatureFormatter.RenderSignature(m),
                    Blocks = ToJsonBlocks(MarkdownLayout.BuildMemberBlocks(m))
                }
            )
                .ToList();

            // Insert a synthetic 'type as member' entry at the beginning for unified signature view
            var typeMember = new {
                Kind = TypeKindKeyword(typeSym.TypeKind),
                Name = name,
                Signature = SignatureFormatter.RenderSignature(typeSym),
                Blocks = ToJsonBlocks(MarkdownLayout.BuildMemberBlocks(typeSym))
            };
            members.Insert(0, typeMember);

            var payload = new {
                Kind = TypeKindKeyword(typeSym.TypeKind),
                Name = name,
                DocId = docId,
                Assembly = asm,
                File = file,
                TypeBlocks = ToJsonBlocks(MarkdownLayout.BuildMemberBlocks(typeSym)),
                Members = members
            };
            var jsonStr = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(jsonStr);


        } else {
            // Centralized Markdown layout for outline
            var md = CodeCortexV2.Formatting.MarkdownLayout.RenderTypeOutline(outline);
            Console.WriteLine(md);
        }
        return 0;

        static bool IsPublicApiMember(ISymbol s) =>
            s.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal
            && !s.IsImplicitlyDeclared;

        static string TypeKindKeyword(TypeKind k) => k switch {
            TypeKind.Class => "class",
            TypeKind.Struct => "struct",
            TypeKind.Interface => "interface",
            TypeKind.Enum => "enum",
            TypeKind.Delegate => "delegate",
            _ => k.ToString().ToLowerInvariant()
        };








        static object ToJsonBlock(Block b) {
            return b switch {
                ParagraphBlock p => new { kind = "p", text = p.Text },
                CodeBlock c => new { kind = "code", text = c.Text, lang = c.Language },
                SequenceBlock seq => new { kind = "seq", children = seq.Children.Select(ToJsonBlock).ToList() },
                ListBlock l => new { kind = "list", ordered = l.Ordered, items = l.Items.Select(it => it.Children.Select(ToJsonBlock).ToList()).ToList() },
                TableBlock t => new { kind = "table", headers = t.Headers.Select(h => h.Text).ToList(), rows = t.Rows.Select(r => r.Select(c => c.Text).ToList()).ToList() },
                SectionBlock s => new { kind = "section", heading = s.Heading, body = ToJsonBlock(s.Body) },
                _ => new { kind = "unknown" }
            };
        }

        static System.Collections.Generic.List<object> ToJsonBlocks(System.Collections.Generic.List<Block> blocks)
            => blocks.Select(ToJsonBlock).ToList();

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



static Microsoft.CodeAnalysis.ISymbol? ResolveSymbolByDocId(Microsoft.CodeAnalysis.Solution solution, string id) {
    if (string.IsNullOrWhiteSpace(id)) {
        return null;
    }
    if (id.StartsWith("T:", StringComparison.Ordinal)) {
        return ResolveTypeByDocId(solution, id);
    }
    if (id.StartsWith("N:", StringComparison.Ordinal) || !id.Contains(':')) {
        // Accept either doc id (N:...) or raw FQN for namespace
        return ResolveNamespace(solution, id);
    }
    return null;
}


static Microsoft.CodeAnalysis.INamespaceSymbol? ResolveNamespace(Microsoft.CodeAnalysis.Solution solution, string query) {
    string name = query;
    if (!string.IsNullOrWhiteSpace(query) && query.StartsWith("N:", StringComparison.Ordinal)) {
        name = query.Substring(2);
    }
    if (name.StartsWith("global::", StringComparison.Ordinal)) {
        name = name.Substring(8);
    }
    var parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0) {
        return null;
    }
    foreach (var proj in solution.Projects) {
        var comp = proj.GetCompilationAsync().GetAwaiter().GetResult();
        if (comp is null) {
            continue;
        }

        Microsoft.CodeAnalysis.INamespaceSymbol current = comp.Assembly.GlobalNamespace;
        bool ok = true;
        foreach (var seg in parts) {
            var next = current.GetNamespaceMembers().FirstOrDefault(n => n.Name == seg);
            if (next is null) {
                ok = false;
                break;
            }
            current = next;
        }
        if (ok) {
            return current;
        }
    }
    return null;
}
