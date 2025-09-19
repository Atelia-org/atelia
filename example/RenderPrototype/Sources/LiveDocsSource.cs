using RenderPrototype.Rendering;

namespace RenderPrototype.Sources;

public sealed class LiveDocsSource : IRenderSource
{
    private readonly Dictionary<string, (string Title, string Body)> _headlines = new()
    {
        ["hd1"] = ("设计概览", "这是设计概览的正文……"),
        ["hd2"] = ("关键接口", "这里包含关键接口与理由……"),
        ["hd3"] = ("下一步计划", "这里是下一步计划与风险……"),
    };

    public Task<RenderSection> RenderAsync(RenderContext context, CancellationToken cancellationToken = default)
    {
        var lines = new List<string>();
        foreach (var (id, v) in _headlines)
        {
            lines.Add($"- {v.Title} [{id}]");
        }

        var tokens = _headlines.Keys
            .Select(id => new ActionToken(
                TokenText: $"copy://livedocs/{id}->memotree/inbox",
                Kind: "copy",
                Target: new NodeRef(id, _headlines[id].Title)))
            .ToList();

        var section = new RenderSection(
            SectionId: "livedocs:list",
            Markdown: string.Join("\n", lines),
            Title: "LiveDocs",
            Priority: 10,
            ActionTokens: tokens,
            NodeRefs: _headlines.Select(kv => new NodeRef(kv.Key, kv.Value.Title)).ToList(),
            Diagnostics: Array.Empty<string>()
        );
        return Task.FromResult(section);
    }

    public (string Title, string Body)? GetHeadline(string id)
    {
        return _headlines.TryGetValue(id, out var v) ? v : null;
    }
}
