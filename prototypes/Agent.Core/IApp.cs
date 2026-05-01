using System.Collections.Generic;
using System.Collections.Immutable;
using Atelia.Agent.Core.Tool;
using Atelia.Completion.Abstractions;

namespace Atelia.Agent.Core;

public readonly record struct AppRenderContext(
    LlmProfile? CurrentProfile,
    ulong EstimatedContextTokens,
    bool HasPendingCompaction
);

public interface IApp {
    string Name { get; }
    string Description { get; }
    IReadOnlyList<ITool> Tools { get; }
    string? RenderWindow(AppRenderContext context);
}

internal interface IAppHost {
    ImmutableArray<IApp> Apps { get; }
    ImmutableArray<ITool> Tools { get; }

    void RegisterApp(IApp app);
    bool RemoveApp(string name);

    string? RenderWindows(AppRenderContext context);
}
