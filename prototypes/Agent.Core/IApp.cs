using System.Collections.Generic;
using System.Collections.Immutable;
using Atelia.Agent.Core.Tool;

namespace Atelia.Agent.Core;

public interface IApp {
    string Name { get; }
    string Description { get; }
    IReadOnlyList<ITool> Tools { get; }
    string? RenderWindow();
}

internal interface IAppHost {
    ImmutableArray<IApp> Apps { get; }
    ImmutableArray<ITool> Tools { get; }

    void RegisterApp(IApp app);
    bool RemoveApp(string name);

    string? RenderWindows();
}
