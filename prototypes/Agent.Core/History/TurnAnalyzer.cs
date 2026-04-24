namespace Atelia.Agent.Core.History;

/// <summary>
/// 描述 <see cref="AgentState.RecentHistory"/> 末尾当前 Turn 的分析结果。
/// </summary>
/// <param name="StartIndex">
/// 当前 Turn 在 <see cref="AgentState.RecentHistory"/> 中的显式起点索引。
/// 仅当最近历史中仍保留 <see cref="HistoryEntryKind.Observation"/> 边界时大于等于 0；
/// 若边界已被 Recap 裁剪，则为 -1。
/// </param>
/// <param name="EndIndex">当前 Turn 末尾索引；空历史时为 -1。</param>
/// <param name="LockedInvocation">
/// 当前 Turn 已锁定到的模型身份；<c>null</c> 表示当前 Turn 尚未发生模型调用。
/// 即使显式 Turn 起点已被 Recap 裁剪，此值仍可能非空。
/// </param>
internal readonly record struct CurrentTurnInfo(
    int StartIndex,
    int EndIndex,
    CompletionDescriptor? LockedInvocation
) {
    public bool HasExplicitStartBoundary => StartIndex >= 0;
    public bool IsLocked => LockedInvocation is not null;
}

/// <summary>
/// 提供对当前 Turn 的纯函数式分析，不引入额外可变状态。
/// </summary>
internal static class TurnAnalyzer {
    /// <summary>
    /// 分析当前 RecentHistory 末尾所在的 Turn。
    /// </summary>
    /// <remarks>
    /// Turn 起点按 <see cref="HistoryEntryKind.Observation"/> 精确判定；
    /// <see cref="ToolResultsEntry"/> 与 <see cref="RecapEntry"/> 虽然都是 observation-like，
    /// 但不视为新的 Turn 起点。若显式起点已被 Recap 裁剪，仍会尽力从残留片段中推断锁定的模型身份。
    /// </remarks>
    public static CurrentTurnInfo Analyze(IReadOnlyList<HistoryEntry> history) {
        if (history is null) { throw new ArgumentNullException(nameof(history)); }
        if (history.Count == 0) { return new CurrentTurnInfo(-1, -1, null); }

        var endIndex = history.Count - 1;
        var startIndex = -1;
        CompletionDescriptor? lockedInvocation = null;

        for (var index = endIndex; index >= 0; index--) {
            var entry = history[index];

            if (entry is ActionEntry actionEntry) {
                // 反向扫描时持续覆写，最终保留下来的就是当前 Turn 内首次模型调用的 Invocation。
                lockedInvocation = actionEntry.Invocation;
                continue;
            }

            if (IsTurnStart(entry)) {
                startIndex = index;
                break;
            }
        }

        return new CurrentTurnInfo(startIndex, endIndex, lockedInvocation);
    }

    internal static bool IsTurnStart(HistoryEntry entry) {
        if (entry is null) { throw new ArgumentNullException(nameof(entry)); }
        return entry.Kind == HistoryEntryKind.Observation;
    }
}
