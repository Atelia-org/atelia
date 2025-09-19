using RenderPrototype.Rendering;

namespace RenderPrototype.Sources;

public sealed class MemoViewMetaSource : IRenderSource
{
    public Task<RenderSection> RenderAsync(RenderContext context, CancellationToken cancellationToken = default)
    {
    var markdown = $"""
    - 当前视图: `{context.ViewName}`
    - 提示: 使用 view create / view set-description 等命令管理视图
    """;
        var section = new RenderSection(
            SectionId: "meta:view",
            Markdown: markdown.Trim(),
            Title: "视图面板",
            Priority: -100,
            ActionTokens: Array.Empty<ActionToken>(),
            NodeRefs: Array.Empty<NodeRef>(),
            Diagnostics: Array.Empty<string>()
        );
        return Task.FromResult(section);
    }
}
