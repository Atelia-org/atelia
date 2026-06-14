using System;
using System.Collections.Generic;
using Atelia.Agent.Core;
using Atelia.Completion.Tools;

namespace Atelia.MemoTree;

/// <summary>
/// 为 Agent.Core 提供 MemoTree Window 的投影源。
/// </summary>
public interface IMemoTreeWindowSource {
    MemoTreeWindowProjection ProjectWindow(AppRenderContext context);
}

/// <summary>
/// `IApp` 适配层的配置项。
/// </summary>
public sealed record MemoTreeAppOptions(
    string Name = "MemoTree",
    string Description = "面向 LLM Agent 的长期外置记忆树窗口。"
);

/// <summary>
/// 供 `IApp` 消费的 MemoTree Window 投影。
/// </summary>
public sealed record MemoTreeWindowProjection(
    string? Window,
    IReadOnlyList<string>? HiddenToolNames = null
);

/// <summary>
/// 把 MemoTree Window 投影包装成 `IApp`。
/// </summary>
public sealed class MemoTreeApp : IApp {
    private readonly IMemoTreeWindowSource _windowSource;

    public MemoTreeApp(
        IMemoTreeWindowSource windowSource,
        IReadOnlyList<ITool>? tools = null,
        MemoTreeAppOptions? options = null
    ) {
        _windowSource = windowSource ?? throw new ArgumentNullException(nameof(windowSource));

        var resolvedOptions = options ?? new MemoTreeAppOptions();
        Name = resolvedOptions.Name;
        Description = resolvedOptions.Description;
        Tools = tools ?? Array.Empty<ITool>();
    }

    public string Name { get; }

    public string Description { get; }

    public IReadOnlyList<ITool> Tools { get; }

    public AppProjection Render(AppRenderContext context) {
        var projection = _windowSource.ProjectWindow(context);
        return new AppProjection(projection.Window, projection.HiddenToolNames);
    }
}
