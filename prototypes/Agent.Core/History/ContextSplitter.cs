using System;
using System.Collections.Generic;

namespace Atelia.Agent.Core.History;

/// <summary>
/// 上下文切分工具：为 LLM 一次性摘要提供"大致一半"的合法切分点。
/// </summary>
/// <remarks>
/// <para><b>切分点合法性约束</b>：切分只能发生在 Observation→Action 边界上。</para>
/// <para>
/// 初始 Observation 可以作为 prefix 的最后一条被摘要，也可以作为 suffix 的第一条被保留——
/// 当前实现选择前者。切分后 prefix 以 Observation-like 结束，suffix 以 Action 开始，
/// 交替结构保持合法。
/// </para>
/// <para><b>返回值语义</b>：返回 suffix 的起始索引（即切分后第一条 <see cref="ActionEntry"/> 的位置）。
/// 调用方可使用 C# 右开区间进行切片：
/// <c>entries[..splitIndex]</c> 为待摘要的前半部分，
/// <c>entries[splitIndex..]</c> 为保留的后半部分。</para>
/// </remarks>
public static class ContextSplitter {

    /// <summary>
    /// 在给定的历史条目列表中，找到累计 token 估计值约为总量一半的合法切分点。
    /// </summary>
    /// <param name="entries">按时间顺序排列的历史条目列表（索引 0 最早）。</param>
    /// <returns>
    /// suffix 的起始索引（C# 右开区间语义），可直接用于 <c>entries[..returnValue]</c> / <c>entries[returnValue..]</c>；
    /// 若不存在合法切分点则返回 <c>-1</c>。
    /// </returns>
    /// <remarks>
    /// 算法采用双 Pass：
    /// <list type="number">
    /// <item>第一遍：遍历所有条目，累加 <see cref="HistoryEntry.TokenEstimate"/> 得到总量。</item>
    /// <item>第二遍：再次遍历，在每个 Observation→Action 边界检查累计 token 数是否达到 ceiling(总量/2)，
    /// 返回首个达标的边界点（若没有达标的，则返回最后一个合法边界点）。</item>
    /// </list>
    /// 由于 <see cref="HistoryEntry.TokenEstimate"/> 内部有字段缓存，重复调用不会重复计算。
    /// </remarks>
    public static int FindHalfContextSplitPoint(IReadOnlyList<HistoryEntry> entries) {
        // 至少需要 2 条才能形成 Observation→Action 边界
        if (entries.Count < 2) { return -1; }

        // ──── Pass 1：统计总 token 数 ────
        ulong totalTokens = 0;
        for (int i = 0; i < entries.Count; i++) {
            totalTokens += GetTokenEstimateOrThrow(entries[i], i);
        }

        if (totalTokens == 0) { return -1; }

        // ceiling half：保证累计 token 真正达到"约一半"
        ulong halfTokens = (totalTokens + 1) / 2;

        // ──── Pass 2：找约一半的合法切分点 ────
        ulong cumulativeTokens = 0;
        int lastValidSuffixStart = -1;

        for (int i = 0; i < entries.Count - 1; i++) {
            cumulativeTokens += GetTokenEstimateOrThrow(entries[i], i);

            if (entries[i].IsObservationLike && entries[i + 1].Kind == HistoryEntryKind.Action) {
                int suffixStart = i + 1;
                lastValidSuffixStart = suffixStart;

                if (cumulativeTokens >= halfTokens) {
                    return suffixStart;
                }
            }
        }

        // 没有达标的切分点，返回最后一个合法边界
        return lastValidSuffixStart;
    }

    /// <summary>
    /// 获取条目的 token 估计值。若未赋值（返回 0）则抛出，因为合法进入 <see cref="AgentState"/>
    /// 的条目必然已完成赋值。
    /// </summary>
    private static uint GetTokenEstimateOrThrow(HistoryEntry entry, int index) {
        uint est = entry.TokenEstimate;
        if (est == 0) {
            throw new InvalidOperationException(
                $"HistoryEntry at index {index} (Kind={entry.Kind}) has no token estimate assigned. "
                + "Entries must go through AgentState token estimation before context splitting."
            );
        }
        return est;
    }
}
