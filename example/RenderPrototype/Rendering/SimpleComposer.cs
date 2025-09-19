using System.Text;

namespace RenderPrototype.Rendering;

public sealed class SimpleComposer : IRenderComposer
{
    public async Task<IReadOnlyList<RenderSection>> ComposeAsync(RenderContext context, IEnumerable<IRenderSource> sources, CancellationToken cancellationToken = default)
    {
        var list = new List<RenderSection>();
        foreach (var src in sources.OrderBy(_ => 0))
        {
            var sec = await src.RenderAsync(context, cancellationToken);
            list.Add(sec);
        }
        return list.OrderBy(s => s.Priority).ToList();
    }

    public string ToMarkdown(IEnumerable<RenderSection> sections)
    {
        var sb = new StringBuilder();
        foreach (var s in sections.OrderBy(s => s.Priority))
        {
            if (!string.IsNullOrWhiteSpace(s.Title))
                sb.AppendLine($"## {s.Title}");
            sb.AppendLine(s.Markdown.TrimEnd());
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }
}
