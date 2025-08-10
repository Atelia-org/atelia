using System.Text.RegularExpressions;
using RenderPrototype.Sources;

namespace RenderPrototype.Rendering;

public sealed class DemoActionDispatcher
{
    private readonly LiveDocsSource _live;
    private readonly FakeMemoTreeSinkSource _sink;

    public DemoActionDispatcher(LiveDocsSource live, FakeMemoTreeSinkSource sink)
    { _live = live; _sink = sink; }

    // 支持格式：copy://livedocs/{id}->memotree/inbox
    private static readonly Regex CopyRe = new("^copy://livedocs/(?<id>[a-zA-Z0-9_-]+)->memotree/inbox$", RegexOptions.Compiled);

    public bool Dispatch(ActionToken token)
    {
        if (!string.Equals(token.Kind, "copy", StringComparison.OrdinalIgnoreCase))
            return false;
        var m = CopyRe.Match(token.TokenText);
        if (!m.Success) return false;
        var id = m.Groups["id"].Value;
        var hd = _live.GetHeadline(id);
        if (hd is null) return false;
        _sink.AddNode(hd.Value.Title, hd.Value.Body);
        return true;
    }
}
