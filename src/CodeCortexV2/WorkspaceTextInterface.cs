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
/// Text-based UI for AI Coder to explore a .NET workspace/codebase.
/// - String-only outputs (Markdown/JSON/plain) for console/RPC/file sinks.
/// - Delegates symbol search/resolve to SymbolIndex; no redundant heuristics.
/// - No complex objects are exposed; no state mutations.
/// </summary>
/// <remarks>
/// Transport-friendly and future-proof for semantic/agent features: even when powered
/// by Roslyn and LLM/Agent, results must still surface as text (outlines, listings,
/// diffs/previews).
/// </remarks>
public interface IWorkspaceTextInterface {
    /// <summary>
    /// Search symbols via SymbolIndex and return formatted text.
    /// json=true returns pretty JSON; otherwise a human-friendly listing.
    /// </summary>
    /// <param name="query">Identifier/FQN/doc-id (T:/N:)/pattern.</param>
    /// <param name="limit">Max items on this page.</param>
    /// <param name="offset">Zero-based offset.</param>
    /// <param name="json">When true, returns JSON text; otherwise plain text.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Text block suitable for console/file/RPC.</returns>
    public Task<string> FindAsync(string query, int limit, int offset, bool json, CancellationToken ct);

    /// <summary>
    /// Outline-or-list: performs a search; if exactly one match (type or namespace),
    /// returns its outline (Markdown by default; JSON when json=true). Otherwise, returns
    /// the same search listing as FindAsync.
    /// </summary>
    /// <remarks>
    /// Resolution and heuristics (namespaces, fuzzy, etc.) are owned by SymbolIndex.
    /// This UI never prepends prefixes (e.g., "N:") nor re-implements resolution.
    /// </remarks>
    /// <param name="query">Query or id as interpreted by SymbolIndex.</param>
    /// <param name="limit">Search page size.</param>
    /// <param name="offset">Search offset.</param>
    /// <param name="json">When true, returns JSON outline/listing; otherwise Markdown/plain.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<string> GetOutlineAsync(string query, int limit, int offset, bool json, CancellationToken ct);
}

/// <summary>
/// Sealed implementation that owns the Roslyn workspace and the SymbolIndex and fulfills
/// the text-only UI contract. Produces transport-friendly strings for console/RPC/file.
/// </summary>
public sealed class WorkspaceTextInterface : IWorkspaceTextInterface {
    private readonly RoslynWorkspaceHost _host;
    private readonly SymbolIndex _index;

    private WorkspaceTextInterface(RoslynWorkspaceHost host, SymbolIndex index) {
        _host = host;
        _index = index;
    }

    public static async Task<WorkspaceTextInterface> CreateAsync(string slnOrProjectPath, CancellationToken ct) {
        var host = await RoslynWorkspaceHost.LoadAsync(slnOrProjectPath, ct).ConfigureAwait(false);
        var index = await SymbolIndex.BuildAsync(host.Workspace.CurrentSolution, ct).ConfigureAwait(false);
        return new WorkspaceTextInterface(host, index);
    }

    public async Task<string> FindAsync(string query, int limit, int offset, bool json, CancellationToken ct) {
        var page = await _index.SearchAsync(query, kinds: CodeCortexV2.Abstractions.SymbolKinds.All, limit, offset, ct).ConfigureAwait(false);
        if (json) {
            return JsonSerializer.Serialize(page, new JsonSerializerOptions { WriteIndented = true });
        }
        var sb = new StringBuilder();
        sb.AppendLine($"Results {page.Items.Count}/{page.Total}, offset={page.Offset}, limit={page.Limit}{(page.NextOffset is int no ? ", nextOffset=" + no : string.Empty)}:");
        foreach (var h in page.Items) {
            var id = h.SymbolId.Value ?? string.Empty;
            var amb = h.IsAmbiguous ? " !ambiguous" : string.Empty;
            sb.AppendLine($"- [{h.Kind}/{h.MatchKind}] {id}{amb} {(string.IsNullOrEmpty(h.Assembly) ? string.Empty : "(asm: " + h.Assembly + ")")}");
        }
        return sb.ToString().TrimEnd();
    }

    public async Task<string> GetOutlineAsync(string query, int limit, int offset, bool json, CancellationToken ct) {
        var page = await _index.SearchAsync(query, kinds: CodeCortexV2.Abstractions.SymbolKinds.All, limit, offset, ct).ConfigureAwait(false);
        if (page.Total != 1) {
            // Return search results when not unique (or none)
            return await FindAsync(query, limit, offset, json, ct).ConfigureAwait(false);
        }

        var hit = page.Items[0];
        // Inline doc-id resolver (no fuzzy, no guessing)
        ISymbol? DocIdResolver(SymbolId id) {
            var val = id.Value;
            if (string.IsNullOrEmpty(val)) {
                return null;
            }

            var solution = _host.Workspace.CurrentSolution;
            if (val.StartsWith("T:", StringComparison.Ordinal)) {
                var meta = val.Substring(2);
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
            if (val.StartsWith("N:", StringComparison.Ordinal)) {
                var name = val.Substring(2);
                if (name.StartsWith("global::", StringComparison.Ordinal)) {
                    name = name.Substring(8);
                }

                var parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
                foreach (var proj in solution.Projects) {
                    var comp = proj.GetCompilationAsync().GetAwaiter().GetResult();
                    if (comp is null) {
                        continue;
                    }

                    INamespaceSymbol cur = comp.Assembly.GlobalNamespace;
                    bool ok = true;
                    foreach (var seg in parts) {
                        var next = cur.GetNamespaceMembers().FirstOrDefault(n => n.Name == seg);
                        if (next is null) {
                            ok = false;
                            break;
                        }
                        cur = next;
                    }
                    if (ok && !cur.IsGlobalNamespace) {
                        return cur;
                    }
                }
                return null;
            }
            return null;
        }

        if (hit.Kind == Abstractions.SymbolKinds.Type) {
            var provider = new TypeOutlineProvider(DocIdResolver);
            var outline = await provider.GetTypeOutlineAsync(hit.SymbolId, new OutlineOptions(Markdown: !json), ct).ConfigureAwait(false);
            if (json) {
                var obj = JsonProjection.ToPlainObject(outline);
                return JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
            }
            return MarkdownLayout.RenderSymbolOutline(outline, baseHeadingLevel: 2, maxAtxLevel: 3);
        }
        if (hit.Kind == Abstractions.SymbolKinds.Namespace) {
            var provider = new NamespaceOutlineProvider(DocIdResolver);
            var outline = await provider.GetNamespaceOutlineAsync(hit.SymbolId, new OutlineOptions(Markdown: !json), ct).ConfigureAwait(false);
            if (json) {
                var obj = JsonProjection.ToPlainObject(outline);
                return JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
            }
            return MarkdownLayout.RenderSymbolOutline(outline, baseHeadingLevel: 2, maxAtxLevel: 3);
        }

        return $"不支持的符号类型用于大纲: {hit.Kind}";
    }
}

