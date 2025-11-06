using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;

namespace Atelia.Agent.Core.History;

// TODO: 评估要不要升级为反射API的核心类型，支持更全面的编辑操作。
/// <summary>
/// Recent History 的轻量级只读快照，供 RecapMaintainer 等组件在本地编辑 Recap 文本并标记
/// 已消化的 Action/Observation 对。该类型不直接依赖 <see cref="AgentState"/>，提交逻辑由后者托管。
/// </summary>
/// <remarks>
/// 典型工作流：
/// <list type="number">
///   <item><description>AgentState 提供 <see cref="RecapBuilder"/> 快照。</description></item>
///   <item><description>上层组件在本地修改 <see cref="RecapText"/>、调用 <see cref="TryDequeueNextPair"/> 消化旧条目。</description></item>
///   <item><description>修改完成后将实例提交回 AgentState，由后者完成最终裁剪和持久化。</description></item>
/// </list>
/// </remarks>
internal sealed class RecapBuilder {
    private readonly ImmutableArray<ActionObservationPair> _pairs;
    private readonly PendingPairList _pendingPairsView;
    private readonly string _originalRecapText;
    private uint _recapTokenEstimate;

    private int _dequeuedCount;

    private RecapBuilder(
        string recapText,
        ImmutableArray<ActionObservationPair> pairs
    ) {
        RecapText = recapText ?? string.Empty;
        _originalRecapText = RecapText;
        _pairs = pairs;
        _pendingPairsView = new PendingPairList(this);
        _recapTokenEstimate = NormalizeEstimate(TokenEstimateHelper.GetDefault().EstimateString(RecapText));
    }

    /// <summary>
    /// 创建 RecapBuilder 快照，通常由 <see cref="AgentState"/> 调用。
    /// </summary>
    /// <remarks>
    /// 该方法假定 <paramref name="entries"/> 中的每个条目都已调用
    /// <see cref="HistoryEntry.AssignTokenEstimate(uint)"/> 完成 token 估算；
    /// 确保这一前提是调用方的责任。
    /// </remarks>
    internal static RecapBuilder CreateSnapshot(IReadOnlyList<HistoryEntry> entries) {
        if (entries is null) { throw new ArgumentNullException(nameof(entries)); }
        if (entries.Count == 0) { throw new ArgumentException("History entry snapshot cannot be empty.", nameof(entries)); }

        var recapText = ExtractRecapText(entries[0]);
        var pairs = BuildActionObservationPairs(entries);

        return new RecapBuilder(recapText, pairs);
    }

    /// <summary>
    /// 当前的 Recap 文本，可在本地编辑。
    /// </summary>
    public string RecapText { get; private set; }

    /// <summary>
    /// 快照中记录的 Action/Observation 对总数。
    /// </summary>
    public int TotalPairCount => _pairs.IsDefault ? 0 : _pairs.Length;

    /// <summary>
    /// 是否仍存在待处理的 Action/Observation 对。
    /// </summary>
    public bool HasPendingPairs => _dequeuedCount < TotalPairCount;

    /// <summary>
    /// 返回当前尚未消化的 Action/Observation 对视图。
    /// </summary>
    public IReadOnlyList<ActionObservationPair> PendingPairs => _pendingPairsView;

    /// <summary>
    /// 当前 Recap 文本的 token 估算值，最小值为 <c>1</c>。
    /// </summary>
    public uint RecapTokenEstimate => _recapTokenEstimate;

    /// <summary>
    /// 当前剩余内容的 token 估算（Recap 文本 + 未消化条目的估算值）。
    /// </summary>
    public uint CurrentTokenEstimate => GetCurrentTokenEstimate();

    /// <summary>
    /// 计算当前 Recap 文本与未消化条目的 token 估算总和。
    /// </summary>
    public uint GetCurrentTokenEstimate() {
        var total = _recapTokenEstimate;

        if (_pairs.IsDefaultOrEmpty || _dequeuedCount >= _pairs.Length) { return total; }

        for (var index = _dequeuedCount; index < _pairs.Length; index++) {
            total = checked(total + _pairs[index].TokenEstimate);
        }

        return total;
    }

    /// <summary>
    /// 更新 Recap 文本，并重新计算规范化后的 token 估算值（至少为 <c>1</c>）。
    /// </summary>
    /// <param name="recapText">新的 Recap 内容。</param>
    public void UpdateRecap(string recapText) {
        RecapText = recapText ?? string.Empty;
        _recapTokenEstimate = NormalizeEstimate(TokenEstimateHelper.GetDefault().EstimateString(RecapText));
    }

    /// <summary>
    /// 预览下一条将被消费的 Action/Observation 对。
    /// </summary>
    /// <param name="pair">返回的条目；若为空则表示队列耗尽。</param>
    /// <returns>存在未消化条目时返回 <c>true</c>。</returns>
    public bool TryPeekNextPair(out ActionObservationPair? pair) {
        if (!HasPendingPairs) {
            pair = null;
            return false;
        }

        pair = _pairs[_dequeuedCount];
        return true;
    }

    /// <summary>
    /// 从待处理队列中取出最旧的 Action/Observation 对并更新统计信息。
    /// </summary>
    /// <param name="pair">返回被取出的条目；若队列为空则为 <c>null</c>。</param>
    /// <returns>当成功取出条目时返回 <c>true</c>。</returns>
    public bool TryDequeueNextPair(out ActionObservationPair? pair) {
        if (!HasPendingPairs || (_pairs.Length > 0 && PendingPairCount <= 1)) {
            pair = null;
            return false;
        }

        var next = _pairs[_dequeuedCount];
        _dequeuedCount++;
        pair = next;
        return true;
    }

    /// <summary>
    /// 返回尚未消化的条目数量。
    /// </summary>
    public int PendingPairCount => TotalPairCount - _dequeuedCount;

    /// <summary>
    /// 当前未触碰区域（待保留区间）的起始Entry序列号。序列号由 AgentState.AppendEntryCore 统一递增分配，且 RecapBuilder 快照只读，因此首尾 pair 的 Serial 即可准确反映期望保留的历史范围。
    /// </summary>
    public ulong? FirstPendingSerial => HasPendingPairs ? _pairs[_dequeuedCount].Action.Serial : null;

    /// <summary>
    /// 期望保留区间的末尾Entry序列号。序列号由 AgentState.AppendEntryCore 统一递增分配，且 RecapBuilder 快照只读，因此首尾 pair 的 Serial 即可准确反映期望保留的历史范围。
    /// </summary>
    public ulong? LastPendingSerial => HasPendingPairs ? _pairs[^1].Observation.Serial : null;

    /// <summary>
    /// 当前快照是否存在任何改动（Recap 文本被修改或条目被消费）。
    /// </summary>
    public bool HasChanges
        => !string.Equals(RecapText, _originalRecapText, StringComparison.Ordinal)
           || _dequeuedCount > 0;

    /// <summary>
    /// 返回快照中的原始条目集合。
    /// </summary>
    public ImmutableArray<ActionObservationPair> SourcePairs => _pairs;

    // 为编辑空Recap提供一个编辑锚点
    const string EmptyRecap = "(Empty)";
    private static string ExtractRecapText(HistoryEntry firstEntry)
        => firstEntry switch {
            ObservationEntry observation => observation?.Notifications?.Detail ?? EmptyRecap,
            RecapEntry recapEntry => recapEntry.Content ?? EmptyRecap,
            _ => throw new ArgumentException("The first history entry must be an ObservationEntry or RecapEntry to provide recap text.")
        };

    private static ImmutableArray<ActionObservationPair> BuildActionObservationPairs(IReadOnlyList<HistoryEntry> entries) {
        if (entries.Count <= 1) { return ImmutableArray<ActionObservationPair>.Empty; }

        var capacity = Math.Max((entries.Count - 1) / 2, 0);
        var builder = ImmutableArray.CreateBuilder<ActionObservationPair>(capacity);

        // AgentState.ValidateAppendOrder 保证历史在追加时严格按照 Observation ↔ Action 交替，
        // 因此这里可以安全地按固定步长读取成对条目；若未来校验调整，应同步更新该逻辑。

        for (var index = 1; index + 1 < entries.Count; index += 2) {
            var actionEntry = entries[index];
            Debug.Assert(actionEntry is ActionEntry, $"Expected ActionEntry at index {index}, actual={actionEntry?.GetType().Name ?? "null"}.");
            if (actionEntry is not ActionEntry action) { throw new InvalidOperationException($"History sequence is corrupted: expected ActionEntry at index {index} but found {DescribeEntry(actionEntry)}."); }

            var observationEntry = entries[index + 1];
            Debug.Assert(observationEntry is ObservationEntry, $"Expected ObservationEntry at index {index + 1}, actual={observationEntry?.GetType().Name ?? "null"}.");
            if (observationEntry is not ObservationEntry observation) { throw new InvalidOperationException($"History sequence is corrupted: expected ObservationEntry at index {index + 1} but found {DescribeEntry(observationEntry)}."); }

            if (action.Serial >= observation.Serial) { throw new InvalidOperationException($"History entry serial order is invalid: action serial {action.Serial} must be less than observation serial {observation.Serial}."); }

            if (action.TokenEstimate == 0) { throw new InvalidOperationException($"ActionEntry serial={action.Serial} does not have a token estimate assigned."); }
            if (observation.TokenEstimate == 0) { throw new InvalidOperationException($"ObservationEntry serial={observation.Serial} does not have a token estimate assigned."); }

            builder.Add(CreatePair(action, observation));
        }

        if (builder.Count == 0) { return ImmutableArray<ActionObservationPair>.Empty; }

        return builder.MoveToImmutable();
    }

    private static ActionObservationPair CreatePair(ActionEntry action, ObservationEntry observation) {
        return new ActionObservationPair(
            Action: action,
            Observation: observation
        );
    }

    /// <summary>
    /// 根据 <see cref="HistoryEntry.TokenEstimate"/> 的约定，将估算值规范化到最小 <c>1</c>。
    /// </summary>
    private static uint NormalizeEstimate(uint rawEstimate)
        => Math.Max(1u, rawEstimate);

    private static string DescribeEntry(HistoryEntry? entry) {
        if (entry is null) { return "null"; }

        string serialPart;
        try {
            serialPart = entry.Serial.ToString();
        }
        catch (InvalidOperationException) {
            serialPart = "unassigned";
        }

        return $"{entry.GetType().Name}(Kind={entry.Kind}, Serial={serialPart})";
    }

    private sealed class PendingPairList : IReadOnlyList<ActionObservationPair> {
        private readonly RecapBuilder _owner;

        internal PendingPairList(RecapBuilder owner) {
            _owner = owner;
        }

        public ActionObservationPair this[int index] {
            get {
                if (index < 0 || index >= Count) { throw new ArgumentOutOfRangeException(nameof(index)); }
                return _owner._pairs[_owner._dequeuedCount + index];
            }
        }

        public int Count => _owner.TotalPairCount - _owner._dequeuedCount;

        public IEnumerator<ActionObservationPair> GetEnumerator() {
            for (int i = _owner._dequeuedCount; i < _owner.TotalPairCount; i++) {
                yield return _owner._pairs[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// 描述一个 Action/Observation 对及其原始序列号的轻量结构。
    /// </summary>
    /// <param name="Action">ActionEntry 的只读引用。</param>
    /// <param name="Observation">ObservationEntry 的只读引用。</param>
    public readonly record struct ActionObservationPair(
        ActionEntry Action,
        ObservationEntry Observation
    ) : ITokenEstimateSource {
        public uint TokenEstimate => checked(Action.TokenEstimate + Observation.TokenEstimate);
    }
}
