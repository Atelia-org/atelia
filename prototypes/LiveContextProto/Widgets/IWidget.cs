using System.Collections.Generic;
using System.Collections.Immutable;
using Atelia.LiveContextProto.State;
using Atelia.LiveContextProto.Tools;

namespace Atelia.LiveContextProto.Widgets;

internal sealed record WidgetRenderContext(
    AgentState AgentState,
    ImmutableDictionary<string, object?> Environment
);

internal interface IWidget {
    string Name { get; }
    string Description { get; }
    IReadOnlyList<ITool> Tools { get; }
    string? RenderLiveScreen(WidgetRenderContext context);
}
