using RenderPrototype.Rendering;

namespace RenderPrototype.Sources;

public sealed class FakeHierarchySource : IRenderSource, IExpandableRenderSource
{
    // 内存假数据
    private readonly Dictionary<string, (string Title, bool Expanded, string[] Children)> _nodes = new()
    {
        ["root"] = ("根", true, new []{"a","b"}),
        ["a"] = ("A 节点", false, new []{"a1"}),
        ["a1"] = ("A-1 子节点", false, Array.Empty<string>()),
        ["b"] = ("B 节点", true, Array.Empty<string>())
    };

    public Task<RenderSection> RenderAsync(RenderContext context, CancellationToken cancellationToken = default)
    {
        var lines = new List<string>();
        void Emit(string id, int level)
        {
            var (title, expanded, children) = _nodes[id];
            var indent = new string(' ', level * 2);
            var marker = expanded ? "-" : "+";
            lines.Add($"{indent}{marker} {title} [{id}]");
            if (expanded)
            {
                foreach (var c in children)
                    Emit(c, level + 1);
            }
        }
        Emit("root", 0);

        var tokens = new List<ActionToken>();
        foreach (var (id, v) in _nodes)
        {
            var kind = v.Expanded ? "collapse" : "expand";
            tokens.Add(new ActionToken(TokenText: kind.ToUpperInvariant(), Kind: kind, Target: new NodeRef(id, v.Title)));
        }

        return Task.FromResult(new RenderSection(
            SectionId: "tree:fake",
            Markdown: string.Join("\n", lines),
            Title: "节点树",
            Priority: 0,
            ActionTokens: tokens,
            NodeRefs: _nodes.Select(kv => new NodeRef(kv.Key, kv.Value.Title)).ToList(),
            Diagnostics: Array.Empty<string>()
        ));
    }

    public Task ExpandAsync(NodeRef target, CancellationToken cancellationToken = default)
    {
        if (_nodes.TryGetValue(target.NodeId, out var v))
            _nodes[target.NodeId] = (v.Title, true, v.Children);
        return Task.CompletedTask;
    }

    public Task CollapseAsync(NodeRef target, CancellationToken cancellationToken = default)
    {
        if (_nodes.TryGetValue(target.NodeId, out var v))
            _nodes[target.NodeId] = (v.Title, false, v.Children);
        return Task.CompletedTask;
    }
}
