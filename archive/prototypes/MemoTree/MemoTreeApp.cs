using System;
using System.Collections.Generic;
using Atelia.Agent.Core;
using Atelia.Completion.Tools;

namespace Atelia.MemoTree;

/// <summary>
/// `IApp` 适配层的配置项。
/// </summary>
public sealed record MemoTreeAppOptions(
    string Name = "MemoTree",
    string Description = "面向 LLM Agent 的长期外置记忆树窗口。"
);

public sealed class MemoTreeApp : IApp {
    private readonly Func<AppRenderContext, AppProjection> _render;

    public MemoTreeApp(
        Func<AppRenderContext, AppProjection> render,
        IReadOnlyList<ITool>? tools = null,
        MemoTreeAppOptions? options = null
    ) {
        _render = render ?? throw new ArgumentNullException(nameof(render));

        var resolvedOptions = options ?? new MemoTreeAppOptions();
        Name = resolvedOptions.Name;
        Description = resolvedOptions.Description;
        Tools = tools ?? Array.Empty<ITool>();
    }

    public string Name { get; }

    public string Description { get; }

    public IReadOnlyList<ITool> Tools { get; }

    public AppProjection Render(AppRenderContext context) => _render(context);
}
