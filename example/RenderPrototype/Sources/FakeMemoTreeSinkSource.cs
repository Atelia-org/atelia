using RenderPrototype.Rendering;

namespace RenderPrototype.Sources;

public sealed class FakeMemoTreeSinkSource : IRenderSource
{
    private int _seq = 1;
    private readonly List<(string Id, string Title, string Body)> _nodes = new();

    public Task<RenderSection> RenderAsync(RenderContext context, CancellationToken cancellationToken = default)
    {
        if (_nodes.Count == 0)
        {
            return Task.FromResult(new RenderSection(
                SectionId: "memotree:inbox",
                Markdown: "(收件箱为空)",
                Title: "MemoTree 收件箱",
                Priority: 20,
                ActionTokens: Array.Empty<ActionToken>(),
                NodeRefs: Array.Empty<NodeRef>(),
                Diagnostics: Array.Empty<string>()
            ));
        }

        var lines = _nodes.Select(n => $"- {n.Title} [{n.Id}]");
        return Task.FromResult(new RenderSection(
            SectionId: "memotree:inbox",
            Markdown: string.Join("\n", lines),
            Title: "MemoTree 收件箱",
            Priority: 20,
            ActionTokens: Array.Empty<ActionToken>(),
            NodeRefs: _nodes.Select(n => new NodeRef(n.Id, n.Title)).ToList(),
            Diagnostics: Array.Empty<string>()
        ));
    }

    public void AddNode(string title, string body)
    {
        var id = $"mt{_seq++}";
        _nodes.Add((id, title, body));
    }
}
