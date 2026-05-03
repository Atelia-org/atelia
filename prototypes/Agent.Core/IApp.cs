using System.Collections.Generic;
using System.Collections.Immutable;
using Atelia.Agent.Core.Tool;
using Atelia.Completion.Abstractions;

namespace Atelia.Agent.Core;

/// <summary>
/// 面向 App Window 的"若此刻执行上下文压缩，预计会压缩哪一段"的轻量预告。
/// </summary>
/// <remarks>
/// 此信息用于帮助 Agent 理解：若此刻执行上下文压缩，预计会压缩哪一段历史。
/// 若当前模型输出直接发出 <c>ctx_compress</c> 调用，引擎会复用这里给出的 <see cref="SplitIndex"/>，
/// 避免实际压缩边界与该次决策参考的边界发生漂移。
/// 其他路径（如 auto/manual deferred compaction）仍按各自执行时的历史快照决定切分点。
/// </remarks>
public readonly record struct CompactionPreview(
    int SplitIndex,
    int PrefixEntryCount,
    ulong PrefixTokenEstimate,
    ulong TotalHistoryTokenEstimate,
    string PrefixEndPreview,
    string SuffixStartPreview
) {
    public double PrefixHistoryRatio => TotalHistoryTokenEstimate == 0
        ? 0.0
        : (double)PrefixTokenEstimate / TotalHistoryTokenEstimate;
}

public readonly record struct AppRenderContext(
    LlmProfile? CurrentProfile,
    ulong EstimatedContextTokens,
    CompactionPreview? EstimatedCompactionPreview
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
