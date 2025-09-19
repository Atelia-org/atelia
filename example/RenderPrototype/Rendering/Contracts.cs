namespace RenderPrototype.Rendering;

public interface IRenderSource
{
    Task<RenderSection> RenderAsync(RenderContext context, CancellationToken cancellationToken = default);
}

public interface IExpandableRenderSource
{
    Task ExpandAsync(NodeRef target, CancellationToken cancellationToken = default);
    Task CollapseAsync(NodeRef target, CancellationToken cancellationToken = default);
}

public sealed record RenderContext(
    string ViewName,
    int? BudgetTokens = null,
    IReadOnlyDictionary<string, object>? Hints = null
);

public sealed record RenderSection(
    string SectionId,
    string Markdown,
    string? Title = null,
    int Priority = 0,
    IReadOnlyList<ActionToken>? ActionTokens = null,
    IReadOnlyList<NodeRef>? NodeRefs = null,
    IReadOnlyList<string>? Diagnostics = null
);

public sealed record ActionToken(
    string TokenText,
    string Kind,
    NodeRef? Target = null,
    Range? Position = null
);

public sealed record NodeRef(
    string NodeId,
    string? Title = null
);

public interface IRenderComposer
{
    Task<IReadOnlyList<RenderSection>> ComposeAsync(RenderContext context, IEnumerable<IRenderSource> sources, CancellationToken cancellationToken = default);
    string ToMarkdown(IEnumerable<RenderSection> sections);
}
