using System.Collections.Generic;
using System.Collections.Immutable;
using Atelia.LiveContextProto.State;
using Atelia.LiveContextProto.Tools;

namespace Atelia.LiveContextProto.Apps;

internal sealed record AppRenderContext(
    AgentState AgentState,
    ImmutableDictionary<string, object?> Environment
);

internal interface IApp {
    string Name { get; }
    string Description { get; }
    IReadOnlyList<ITool> Tools { get; }
    string? RenderWindow(AppRenderContext context);
}
