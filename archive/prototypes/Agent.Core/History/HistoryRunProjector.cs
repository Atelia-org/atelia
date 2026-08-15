using Atelia.Completion.Abstractions;

namespace Atelia.Agent.Core.History;

/// <summary>
/// RecentHistory 事件账本 → provider-facing 消息序列的统一遍历骨架。
/// </summary>
/// <remarks>
/// 分组不变量（actor-run）：observation-like 条目各自投影为一条消息；
/// 连续的 actor-side 条目（<see cref="ActionEntry"/> / <see cref="InjectionEntry"/>）
/// 合并投影为单条 <see cref="ActionMessage"/>。
/// 主调用投影（<see cref="AgentState.ProjectInvocationContext"/>）与摘要投影
/// （<see cref="AgentEngine"/> 的 compaction 流程）共享本骨架，仅在传入的投影委托上有差异，
/// 从而让 actor-run 的边界扫描与索引推进只有一个权威定义点。
/// </remarks>
internal static class HistoryRunProjector {
    /// <summary>
    /// 按 actor-run 分组规则遍历 <paramref name="entries"/> 的 <c>[startIndex, endExclusive)</c> 区间。
    /// </summary>
    /// <param name="projectObservationLike">把指定下标处的 observation-like 条目投影为一条消息。</param>
    /// <param name="projectActorRun">
    /// 把 <c>[runStart, runEnd)</c> 区间的连续 actor-side 条目投影为单条消息；
    /// 返回 <c>null</c> 表示该 run 投影后为空（例如 thinking 被裁剪殆尽），应整体跳过。
    /// </param>
    public static List<IHistoryMessage> Project(
        IReadOnlyList<HistoryEntry> entries,
        int startIndex,
        int endExclusive,
        Func<int, IHistoryMessage> projectObservationLike,
        Func<int, int, IHistoryMessage?> projectActorRun
    ) {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(projectObservationLike);
        ArgumentNullException.ThrowIfNull(projectActorRun);

        var messages = new List<IHistoryMessage>(Math.Max(0, endExclusive - startIndex));
        for (var index = startIndex; index < endExclusive; index++) {
            if (entries[index].IsObservationLike) {
                messages.Add(projectObservationLike(index));
                continue;
            }

            var runEnd = index + 1;
            while (runEnd < endExclusive && entries[runEnd].IsActorLike) {
                runEnd++;
            }

            var actorMessage = projectActorRun(index, runEnd);
            if (actorMessage is not null) {
                messages.Add(actorMessage);
            }

            index = runEnd - 1;
        }

        return messages;
    }
}
