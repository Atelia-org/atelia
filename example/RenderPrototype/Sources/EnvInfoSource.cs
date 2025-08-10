using RenderPrototype.Rendering;

namespace RenderPrototype.Sources;

public sealed class EnvInfoSource : IRenderSource, IExpandableRenderSource
{
    private bool _expanded;

    // 假数据
    private readonly int _contextUsagePercent = 84; // 已使用百分比
    private readonly int _tokenBalance = 123456;    // 余额（示例）

    public Task<RenderSection> RenderAsync(RenderContext context, CancellationToken cancellationToken = default)
    {
        string markdown;
        var tokens = new List<ActionToken>();
        if (_expanded)
        {
            markdown = $"- 上下文使用: {_contextUsagePercent}%\n- Token 余额: {_tokenBalance}";
            tokens.Add(new ActionToken("COLLAPSE", "collapse", new NodeRef("env", "环境")));
        }
        else
        {
            var remaining = 100 - _contextUsagePercent;
            var warn = remaining < 20 ? $"上下文空间剩余不足20% (剩余 {remaining}%)" : "上下文良好";
            markdown = $"- {warn}";
            tokens.Add(new ActionToken("EXPAND", "expand", new NodeRef("env", "环境")));
        }

        return Task.FromResult(new RenderSection(
            SectionId: "env:info",
            Markdown: markdown,
            Title: "环境信息",
            Priority: -50,
            ActionTokens: tokens,
            NodeRefs: new []{ new NodeRef("env", "环境") },
            Diagnostics: Array.Empty<string>()
        ));
    }

    public Task ExpandAsync(NodeRef target, CancellationToken cancellationToken = default)
    { _expanded = true; return Task.CompletedTask; }

    public Task CollapseAsync(NodeRef target, CancellationToken cancellationToken = default)
    { _expanded = false; return Task.CompletedTask; }
}
