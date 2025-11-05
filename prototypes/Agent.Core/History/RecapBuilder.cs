using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using Atelia.Agent.Core;
using Atelia.Completion.Abstractions;

namespace Atelia.Agent.Core.History;

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

    private int _dequeuedCount;

    private RecapBuilder(
        string recapText,
        ImmutableArray<ActionObservationPair> pairs,
        ulong firstSerial,
        ulong lastSerial
    ) {
        RecapText = recapText ?? string.Empty;
        _originalRecapText = RecapText;
        _pairs = pairs;
        FirstSerial = firstSerial;
        LastSerial = lastSerial;
        _pendingPairsView = new PendingPairList(this);

        if (TotalPairCount > 0 && firstSerial == 0) { throw new ArgumentOutOfRangeException(nameof(firstSerial), "First serial must be greater than zero when pairs are present."); }

        if (TotalPairCount > 0 && lastSerial < firstSerial) { throw new ArgumentOutOfRangeException(nameof(lastSerial), "Last serial must be greater than or equal to first serial."); }
    }

    /// <summary>
    /// 创建 RecapBuilder 快照，通常由 <see cref="AgentState"/> 调用。
    /// </summary>
    internal static RecapBuilder CreateSnapshot(IReadOnlyList<HistoryEntry> entries) {
        if (entries is null) { throw new ArgumentNullException(nameof(entries)); }
        if (entries.Count == 0) { throw new ArgumentException("History entry snapshot cannot be empty.", nameof(entries)); }

        var recapText = ExtractRecapText(entries[0]);
        var (pairs, firstSerial, lastSerial) = BuildActionObservationPairs(entries);

        return new RecapBuilder(recapText, pairs, firstSerial, lastSerial);
    }

    /// <summary>
    /// 当前的 Recap 文本，可在本地编辑。
    /// </summary>
    public string RecapText { get; private set; }

    /// <summary>
    /// 快照创建时的原始 Recap 文本。
    /// </summary>
    public string OriginalRecapText => _originalRecapText;

    /// <summary>
    /// 快照覆盖的历史起始序列号。
    /// </summary>
    public ulong FirstSerial { get; }

    /// <summary>
    /// 快照覆盖的历史结束序列号。
    /// </summary>
    public ulong LastSerial { get; }

    /// <summary>
    /// 快照中记录的 Action/Observation 对总数。
    /// </summary>
    public int TotalPairCount => _pairs.IsDefault ? 0 : _pairs.Length;

    /// <summary>
    /// 已经被消费（Dequeued）的 Action/Observation 对数量。
    /// </summary>
    public int DequeuedPairCount => _dequeuedCount;

    /// <summary>
    /// 是否仍存在待处理的 Action/Observation 对。
    /// </summary>
    public bool HasPendingPairs => _dequeuedCount < TotalPairCount;

    /// <summary>
    /// 返回当前尚未消化的 Action/Observation 对视图。
    /// </summary>
    public IReadOnlyList<ActionObservationPair> PendingPairs => _pendingPairsView;

    /// <summary>
    /// 当前剩余内容的字符数估算（Recap 文本长度 + 未消化条目的长度）。
    /// </summary>
    public int CurrentCharCount => GetCurrentCharCount();

    /// <summary>
    /// 当前的 Recap 文本与未消化条目共同占用的字符数。
    /// </summary>
    public int GetCurrentCharCount() {
        // TODO: Replace this heuristic once HistoryEntry exposes precise character metrics.
        var total = RecapText?.Length ?? 0;

        if (_pairs.IsDefaultOrEmpty || _dequeuedCount >= _pairs.Length) { return total; }

        for (var index = _dequeuedCount; index < _pairs.Length; index++) {
            total += EstimatePairCharCount(_pairs[index]);
        }

        return total;
    }

    /// <summary>
    /// 更新 Recap 文本。
    /// </summary>
    /// <param name="recapText">新的 Recap 内容。</param>
    public void UpdateRecap(string recapText) {
        RecapText = recapText ?? string.Empty;
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
        if (!HasPendingPairs) {
            pair = null;
            return false;
        }

        var next = _pairs[_dequeuedCount];
        _dequeuedCount++;
        pair = next;
        return true;
    }

    /// <summary>
    /// 一次性取出所有剩余条目，常用于强制清空待处理队列。
    /// </summary>
    public IReadOnlyList<ActionObservationPair> DequeueAllRemaining() {
        if (!HasPendingPairs) { return ImmutableArray<ActionObservationPair>.Empty; }

        var builder = ImmutableArray.CreateBuilder<ActionObservationPair>(PendingPairCount);

        while (TryDequeueNextPair(out var pair) && pair.HasValue) {
            builder.Add(pair.Value);
        }

        return builder.MoveToImmutable();
    }

    /// <summary>
    /// 返回尚未消化的条目数量。
    /// </summary>
    public int PendingPairCount => TotalPairCount - _dequeuedCount;

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
            RecapEntry recapEntry => recapEntry.Contents ?? EmptyRecap,
            _ => throw new ArgumentException("The first history entry must be an ObservationEntry or RecapEntry to provide recap text.")
        };

    private static (ImmutableArray<ActionObservationPair> Pairs, ulong FirstSerial, ulong LastSerial) BuildActionObservationPairs(IReadOnlyList<HistoryEntry> entries) {
        if (entries.Count <= 1) { return (ImmutableArray<ActionObservationPair>.Empty, 0, 0); }

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

            builder.Add(CreatePair(action, observation));
        }

        if (builder.Count == 0) { return (ImmutableArray<ActionObservationPair>.Empty, 0, 0); }

        // 序列号由 AgentState.AppendEntryCore 统一递增分配，且 RecapBuilder 快照只读，
        // 因此首尾 pair 的 Serial 即可准确反映快照覆盖的历史范围。
        ulong firstSerial = builder[0].Action.Serial;
        ulong lastSerial = builder[^1].Observation.Serial;

        return (builder.MoveToImmutable(), firstSerial, lastSerial);
    }

    private static ActionObservationPair CreatePair(ActionEntry action, ObservationEntry observation) {
        return new ActionObservationPair(
            Action: action,
            Observation: observation
        );
    }

    private static int EstimatePairCharCount(ActionObservationPair pair)
        => EstimateActionCharCount(pair.Action) + EstimateObservationCharCount(pair.Observation);

    private static int EstimateActionCharCount(ActionEntry action) {
        var total = action.Contents?.Length ?? 0;

        if (action.ToolCalls is { Count: > 0 }) {
            foreach (var call in action.ToolCalls) {
                if (!string.IsNullOrEmpty(call.ToolName)) {
                    total += call.ToolName.Length;
                }

                if (!string.IsNullOrEmpty(call.ToolCallId)) {
                    total += call.ToolCallId.Length;
                }

                if (call.RawArguments is { Count: > 0 }) {
                    foreach (var argument in call.RawArguments) {
                        total += argument.Key.Length;
                        total += argument.Value?.Length ?? 0;
                    }
                }

                if (!string.IsNullOrEmpty(call.ParseError)) {
                    total += call.ParseError.Length;
                }

                if (!string.IsNullOrEmpty(call.ParseWarning)) {
                    total += call.ParseWarning.Length;
                }
            }
        }

        return total;
    }

    private static int EstimateObservationCharCount(ObservationEntry observation) {
        var message = observation.GetMessage(LevelOfDetail.Detail, windows: null);
        var total = message.Contents?.Length ?? 0;

        if (message is ToolResultsMessage toolResults) {
            if (toolResults.Results is { Count: > 0 }) {
                foreach (var result in toolResults.Results) {
                    if (!string.IsNullOrEmpty(result.ToolName)) {
                        total += result.ToolName.Length;
                    }

                    if (!string.IsNullOrEmpty(result.ToolCallId)) {
                        total += result.ToolCallId.Length;
                    }

                    total += result.Result?.Length ?? 0;
                }
            }

            if (!string.IsNullOrEmpty(toolResults.ExecuteError)) {
                total += toolResults.ExecuteError.Length;
            }
        }

        return total;
    }

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
    );
}
