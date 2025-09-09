using System.Text;
using System.Text.Json;
using CodeCortexV2.Abstractions;
using CodeCortexV2.Index;
using CodeCortexV2.Providers;
using CodeCortexV2.Workspace;
using CodeCortexV2.Formatting;
using Microsoft.CodeAnalysis;

namespace CodeCortexV2;

/// <summary>
/// Central service for CodeCortex V2 that owns the Roslyn workspace and the SymbolIndex,
/// and provides high-level operations: find and outline (Markdown only for now).
/// </summary>
public sealed class CodeCortexService {
    private readonly RoslynWorkspaceHost _host;
    private readonly SymbolIndex _index;

    private CodeCortexService(RoslynWorkspaceHost host, SymbolIndex index) {
        _host = host;
        _index = index;
    }

    public static async Task<CodeCortexService> CreateAsync(string slnOrProjectPath, CancellationToken ct) {
        var host = await RoslynWorkspaceHost.LoadAsync(slnOrProjectPath, ct).ConfigureAwait(false);
        var index = await SymbolIndex.BuildAsync(host.Workspace.CurrentSolution, ct).ConfigureAwait(false);
        return new CodeCortexService(host, index);
    }

    public async Task<SearchResults> FindAsync(string query, CodeCortexV2.Abstractions.SymbolKind? kind, int limit, int offset, CancellationToken ct)
        => await _index.SearchAsync(query, kind, limit, offset, ct).ConfigureAwait(false);

    /// <summary>
    /// Render outline (Markdown). Behavior:
    /// - If query resolves to exactly 1 type: render type outline (with members)
    /// - If no type matches: try namespace (N:... or raw FQN), render namespace overview as pseudo type outline
    /// - If multiple type matches: return a Markdown listing (similar to CLI fallback)
    /// </summary>
    public async Task<string> GetOutlineMarkdownAsync(string queryOrId, int limit, int offset, int baseHeadingLevel, int maxAtxLevel, CancellationToken ct) {
        // 1) Try find types first (paged)
        var page = await _index.SearchAsync(queryOrId, CodeCortexV2.Abstractions.SymbolKind.Type, limit, offset, ct).ConfigureAwait(false);
        if (page.Total == 0) {
            // 2) Try namespace by id or FQN
            var nsId = await _index.ResolveAsync(queryOrId, ct).ConfigureAwait(false);
            if (nsId is null && !queryOrId.StartsWith("N:", StringComparison.Ordinal)) {
                nsId = await _index.ResolveAsync("N:" + queryOrId, ct).ConfigureAwait(false);
            }
            if (nsId is null) {
                return "未找到匹配。支持：类型 (T:...) 与命名空间 (N:... 或 FQN)。";
            }

            var nsProvider = new NamespaceOutlineProvider(id => ResolveSymbolByDocId(_host.Workspace.CurrentSolution, id.Value));
            var nsOutline = await nsProvider.GetNamespaceAsTypeOutlineAsync(nsId.Value, new OutlineOptions(Markdown: true), ct).ConfigureAwait(false);
            var md = MarkdownLayout.RenderTypeOutline(nsOutline);
            return md;
        }
        if (page.Total > 1) {
            // Fallback listing as Markdown text
            var sb = new StringBuilder();
            sb.AppendLine($"Results {page.Items.Count}/{page.Total}, offset={page.Offset}, limit={page.Limit}{(page.NextOffset is int no ? ", nextOffset=" + no : string.Empty)}:");
            foreach (var h in page.Items) {
                var id = h.SymbolId.Value ?? string.Empty;
                var amb = h.IsAmbiguous ? " !ambiguous" : string.Empty;
                sb.AppendLine($"- [{h.Kind}/{h.MatchKind}] {id}{amb} {(string.IsNullOrEmpty(h.Assembly) ? string.Empty : "(asm: " + h.Assembly + ")")}");
            }
            return sb.ToString().TrimEnd();
        }

        // 3) Unique match → render outline
        var unique = page.Items[0];
        var typeSym = ResolveTypeByDocId(_host.Workspace.CurrentSolution, unique.SymbolId.Value);
        if (typeSym is null) {
            return "找到 1 个匹配，但未能解析类型符号（DocCommentId 可能不在当前编译环境）。";
        }
        var provider = new TypeOutlineProvider(id => id.Value == unique.SymbolId.Value ? typeSym : ResolveTypeByDocId(_host.Workspace.CurrentSolution, id.Value));
        var outline = await provider.GetTypeOutlineAsync(unique.SymbolId, new OutlineOptions(Markdown: true), ct).ConfigureAwait(false);
        var mdOutline = MarkdownLayout.RenderTypeOutline(outline);
        return mdOutline;
    }

    private static INamedTypeSymbol? ResolveTypeByDocId(Solution solution, string docId) {
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

    private static ISymbol? ResolveSymbolByDocId(Solution solution, string id) {
        if (string.IsNullOrWhiteSpace(id)) {
            return null;
        }

        if (id.StartsWith("T:", StringComparison.Ordinal)) {
            return ResolveTypeByDocId(solution, id);
        }

        if (id.StartsWith("N:", StringComparison.Ordinal) || !id.Contains(':')) {
            return ResolveNamespace(solution, id);
        }

        return null;
    }

    private static INamespaceSymbol? ResolveNamespace(Solution solution, string query) {
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

            INamespaceSymbol current = comp.Assembly.GlobalNamespace;
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
}

