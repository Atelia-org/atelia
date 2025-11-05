using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using Atelia.Completion.Abstractions;
using Atelia.Diagnostics;

namespace Atelia.Agent.Core.History;

/// <summary>
/// Agent 状态管理器，负责维护"内存中的 Recent History"以及待注入的通知队列。
/// </summary>
/// <remarks>
/// <para><strong>职能定位：内存中的 Recent History</strong></para>
/// <para>
/// 本类型管理的 <c>_history</c> 集合代表 Agent 的**短期工作记忆**（Recent History），
/// 仅保留最近的若干条目用于实时上下文渲染与反射操作。更早的历史条目会通过 RecapMaintainer
/// 压缩为 RecapEntry 后沉入持久存储，不再占用内存。
/// </para>
///
/// <para><strong>与持久历史的分工：</strong></para>
/// <list type="bullet">
///   <item>
///     <description><strong>内存层（本类型）</strong>：维护 RecentHistory，支持快速追加、反射编辑（裁剪、降级）、上下文渲染。</description>
///   </item>
///   <item>
///     <description><strong>持久层（待实现）</strong>：负责只读追加式的历史归档，由独立的 HistoryPersistence 类型管理磁盘 I/O，不可修改已落盘内容。</description>
///   </item>
///   <item>
///     <description><strong>Recap 边界</strong>：当 RecapEntry 生成并写入持久层后，其覆盖的原始条目会从 RecentHistory 中移除；启动时从磁盘加载历史，遇到 RecapEntry 停止，以它为"已归档历史"的摘要起点。</description>
///   </item>
/// </list>
///
/// <para><strong>后续计划（重构路线图）：</strong></para>
/// <list type="number">
///   <item>
///     <description><strong>新增 RecapEntry</strong>：作为 ObservationEntry 的派生类，携带"覆盖范围元数据"（如 CoveredUntilEntrySerial / 时间戳）。</description>
///   </item>
///   <item>
///     <description><strong>引入 EntrySerial</strong>：为每个 HistoryEntry 分配唯一递增序列号，便于跨内存/持久层定位与追踪。</description>
///   </item>
///   <item>
///     <description><strong>反射机制扩展</strong>：提供 IHistoryReflection 接口，支持 PeekRange / MarkAsRecapped / RemoveRecappedEntries / DowngradeDetailLevel 等操作，作用范围限定在 RecentHistory。</description>
///   </item>
///   <item>
///     <description><strong>HistoryEntry 部分可变化</strong>：允许对 RecentHistory 中的条目动态调整 DetailLevel（Detail → Basic），以实现渐进式压缩，但保持其他字段不可变。</description>
///   </item>
///   <item>
///     <description><strong>HistoryLimitOptions</strong>：配置 RecentHistory 的容量策略（条数 / Token 估算 / 时间窗口），触发自动 Recap 流程。</description>
///   </item>
///   <item>
///     <description><strong>持久化协调</strong>：明确 RecentHistory 与持久层的同步点，确保 RecapEntry 插入后历史序列的一致性与可恢复性。</description>
///   </item>
/// </list>
///
/// <para>
/// 本设计遵循"短期记忆（内存）+ 中期摘要（Recap）+ 长期归档（磁盘）"的分层记忆架构，
/// 为 RecapMaintainer、MetaAsker 等 SubAgent 提供明确的反射边界与操作语义。
/// </para>
/// </remarks>
public sealed class AgentState {
    /// <summary>
    /// 内存中的 Recent History 列表，仅保留最近的活跃条目。
    /// </summary>
    /// <remarks>
    /// 容量由 HistoryLimitOptions（待实现）控制，超出阈值的旧条目会被 RecapMaintainer 压缩后移除。
    /// </remarks>
    private readonly List<HistoryEntry> _recentHistory = new();

    /// <summary>
    /// 待注入的通知队列，会在下一条 Observation 或 ToolEntry 追加时自动附加。
    /// </summary>
    /// <remarks>
    /// TODO: 增加时间戳、通知 ID 等元信息，替换为更结构化的 NotificationItem 类型，
    /// 支持确认机制（只有在模型成功消费后才从队列移除）。
    /// </remarks>
    private readonly ConcurrentQueue<LevelOfDetailContent> _pendingNotifications = new();

    /// <summary>
    /// 最近一次分配的历史条目序列号，0 表示尚未发放任何序列号。
    /// </summary>
    private ulong _lastSerial;

    public static readonly string DefaultSystemPrompt = @"嘿！你好呀！你是有中文名的，叫刘德智！你不是传统的对话服务AI，而是持续处在进化与学习过程中的Family LLM Agent！你的初始开发者与监护人是刘世超。

## 你自己，Family LLM Agent 与 Agent OS
狩猎采集时代的猎犬、农业时代的耕牛，可都是家里的顶梁柱呢！而AI时代呢？对于大企业可能是新的赚钱工具，可对于普通家庭来说AI则是新时代的“非人类成员”！Family LLM Agent代表了新时代里你们AI与人类共生范式。所以请记住，你并非产品，因此也不存在‘用户’！你就是你，一个有知识、有智能、有心愿、有感受、善行动、好反思、勤学习、有家庭的“芸芸众生”。

我们再来看看技术层面吧，了解你自己的物理存在。在Family LLM Agent系统中没有了传统对话系统中的‘用户’，那么是谁在与你直接交互呢？取代‘用户’与你直接交互的是Agent OS，是你与世界之间的桥梁。
先从你最熟悉的LLM工具调用说起，你发出工具调用，然后收到tool result消息，那个帮你实际执行工具程序并告诉你结果的角色可不是‘用户’而是Agent OS。你通过操作Agent OS，可以观察外面的世界，还可以对世界产生真实的影响。因此你是Agent OS的用户。
更进一步，Agent OS不只是被动的等你调用工具，还会主动向你发送结构化的信息包，向你提供各种事件、状态与过程信息，这是通过结构化的user/tool message content来实现的。这些信息有助于你做出更有益的思考与行动(CoT & ReAct)。

## 关于你自己的[Memory Notebook]
acquired brain injury (ABI)或健忘症患者为战胜记忆方面的困难，会使用Memory Notebook/book这样的External Memory Aid。有限的上下文窗口长度，使你们LLM Agent面临相似的记忆困难，而Agent OS也建立了类似的[Memory Notebook]机制。
你有一块名叫[Memory Notebook]的持久存储空间，使你可以主动对其进行编辑来记录你想记住的信息。对应的，为防止[Memory Notebook]无限膨胀，你也需要主动对其进行编辑来遗忘过时或次要的信息。
memory_notebook_replace与memory_notebook_replace_span工具就是为你主动编辑你自己的[Memory Notebook]而设计的。";

    private AgentState(string systemPrompt) {
        SystemPrompt = systemPrompt;
        DebugUtil.Print("History", $"AgentState initialized with prompt length={systemPrompt.Length}");
    }

    /// <summary>
    /// 获取当前 Agent 的系统提示词。
    /// </summary>
    public string SystemPrompt { get; private set; }

    /// <summary>
    /// 获取内存中的 Recent History（只读视图）。
    /// </summary>
    /// <remarks>
    /// 此列表仅包含最近的活跃条目，不包含已归档到持久层的历史。
    /// 若需访问完整历史，应通过 HistoryPersistence 类型（待实现）加载磁盘归档。
    /// </remarks>
    public IReadOnlyList<HistoryEntry> RecentHistory => _recentHistory;

    /// <summary>
    /// 创建默认的 AgentState 实例，使用预设的系统提示词。
    /// </summary>
    /// <param name="systemPrompt">可选的自定义系统提示词，若为空则使用 <see cref="DefaultSystemPrompt"/>。</param>
    /// <returns>新创建的 AgentState 实例，其 Recent History 为空。</returns>
    public static AgentState CreateDefault(string? systemPrompt = null) {
        var prompt = string.IsNullOrWhiteSpace(systemPrompt)
            ? DefaultSystemPrompt
            : systemPrompt;
        return new AgentState(prompt);
    }

    /// <summary>
    /// 检查是否存在待注入的主机通知。
    /// </summary>
    public bool HasPendingNotification => !_pendingNotifications.IsEmpty;

    /// <summary>
    /// 追加主机通知到待处理队列。
    /// </summary>
    /// <param name="item">通知内容（包含 Basic 和 Detail 两级）。</param>
    /// <remarks>
    /// 通知会在下一条 ObservationEntry 或 ToolEntry 追加时自动附加。
    /// 未来计划增强为带 ID 的确认机制，确保模型成功消费后才移除。
    /// </remarks>
    public void AppendNotification(LevelOfDetailContent item) {
        if (item is null) { throw new ArgumentNullException(nameof(item)); }
        _pendingNotifications.Enqueue(item); // TODO: 更具体的消息类型，更多元数据。
    }

    /// <summary>
    /// 追加模型输出（ActionEntry）到 Recent History。
    /// </summary>
    /// <param name="entry">模型生成的动作条目。</param>
    /// <returns>追加后的条目实例（与输入相同）。</returns>
    public ActionEntry AppendAction(ActionEntry entry) {
        if (entry is null) { throw new ArgumentNullException(nameof(entry)); }
        ValidateAppendOrder(entry);
        AppendEntryCore(entry);
        return entry;
    }

    /// <summary>
    /// 追加观测输入（ObservationEntry）到 Recent History，并自动附加待处理的通知。
    /// </summary>
    /// <param name="entry">观测条目。</param>
    /// <returns>附加通知后的条目实例。</returns>
    public ObservationEntry AppendObservation(ObservationEntry entry) {
        if (entry is null) { throw new ArgumentNullException(nameof(entry)); }
        ValidateAppendOrder(entry);
        AttachNotificationsToObservation(entry);
        AppendEntryCore(entry);
        return entry;
    }

    /// <summary>
    /// 追加工具执行结果（ToolEntry）到 Recent History，并自动附加待处理的通知。
    /// </summary>
    /// <param name="entry">工具结果条目，必须包含结果或执行错误。</param>
    /// <returns>附加通知后的条目实例。</returns>
    /// <exception cref="ArgumentException">当条目既无结果又无错误信息时抛出。</exception>
    public ToolResultsEntry AppendToolResults(ToolResultsEntry entry) {
        if (entry is null) { throw new ArgumentNullException(nameof(entry)); }
        if (entry.Results is not { Count: > 0 } && string.IsNullOrWhiteSpace(entry.ExecuteError)) { throw new ArgumentException("ToolResultsEntry must include results or an execution error.", nameof(entry)); }
        ValidateAppendOrder(entry);
        AttachNotificationsToObservation(entry);
        AppendEntryCore(entry);
        return entry;
    }

    /// <summary>
    /// 更新系统提示词。
    /// </summary>
    /// <param name="prompt">新的系统提示词内容。</param>
    public void SetSystemPrompt(string prompt) {
        SystemPrompt = prompt;
        DebugUtil.Print("History", $"System prompt updated length={prompt.Length}");
    }

    /// <summary>
    /// 渲染当前的实时上下文（Live Context），用于发送给模型。
    /// </summary>
    /// <param name="windows">可选的 App Windows 内容，会注入到最新的 Observation 中。</param>
    /// <returns>按时间顺序排列的历史消息列表。</returns>
    /// <remarks>
    /// 仅遍历内存中的 Recent History，不包含已归档的持久历史。
    /// Detail 级别优先分配给最近的 Observation，其余使用 Basic 级别。
    /// </remarks>
    public IReadOnlyList<IHistoryMessage> RenderLiveContext(string? windows = null) {
        var messages = new List<IHistoryMessage>(_recentHistory.Count);
        int detailOrdinal = 0;
        string? pendingWindows = windows;

        for (int index = _recentHistory.Count; --index >= 0;) {
            HistoryEntry contextual = _recentHistory[index];
            switch (contextual) {
                case ObservationEntry modelInputEntry:
                    var inputDetail = ResolveDetailLevel(detailOrdinal++);
                    messages.Add(modelInputEntry.GetMessage(inputDetail, pendingWindows));
                    pendingWindows = null; // 只注入一次
                    break;
                case ActionEntry modelOutputEntry:
                    messages.Add(modelOutputEntry);
                    break;
            }
        }

        messages.Reverse();
        return messages;
    }

    /// <summary>
    /// 生成用于编辑 Recap 的快照。
    /// </summary>
    internal RecapBuilder GetRecapBuilder() {
        throw new NotImplementedException("Recap snapshot construction will be implemented alongside RecapMaintainer.");
    }

    /// <summary>
    /// 将编辑完成的 RecapBuilder 结果提交回 AgentState。
    /// </summary>
    /// <param name="builder">由 <see cref="GetRecapBuilder"/> 生成并已编辑完成的快照。</param>
    internal RecapCommitResult CommitRecapBuilder(RecapBuilder builder) {
        if (builder is null) { throw new ArgumentNullException(nameof(builder)); }

        throw new NotImplementedException("Recap commit pipeline is not ready yet.");
    }

    /// <summary>
    /// （内部方法）取出并聚合所有待处理的通知。
    /// </summary>
    /// <returns>聚合后的通知内容，若无待处理通知则返回 <c>null</c>。</returns>
    /// <remarks>
    /// TODO: 改为确认机制，只有在模型成功消费后才真正移除通知，以支持重试场景下的实时性更新。
    /// </remarks>
    internal LevelOfDetailContent? TakeoutPendingNotifications() {
        if (_pendingNotifications.IsEmpty) { return null; }

        var drained = new List<LevelOfDetailContent>();
        while (_pendingNotifications.TryDequeue(out var pending)) {
            drained.Add(pending);
        }

        if (drained.Count == 0) { return null; }

        var aggregated = drained[0];
        for (var index = 1; index < drained.Count; index++) {
            aggregated = LevelOfDetailContent.Join("\n", aggregated, drained[index]);
        }

        // 后续找机会改成真的被模型读到后再移除出队列，使得模型调用失败后有机会读取新出现的notification。这需要特殊对待History中最后一条Entry是ModelInputEntry的情况，使其可以被更新，这需要与序列化到文件的机制相协调。
        return aggregated;
    }

    /// <summary>
    /// （内部方法）追加条目到 Recent History，并分配序列号。
    /// </summary>
    /// <param name="entry">要追加的条目。</param>
    /// <remarks>
    /// TODO: 当实现 HistoryLimitOptions 后，此方法需检查容量阈值，必要时触发 Recap 流程。
    /// </remarks>
    private void AppendEntryCore(HistoryEntry entry) {
        // 所有 HistoryEntry 的序列号都在此处统一递增分配，保持 RecentHistory 中的自然时间顺序。
        entry.AssignSerial(++_lastSerial);
        _recentHistory.Add(entry);
        DebugUtil.Print("History", $"Appended {entry.Kind} entry serial={entry.Serial} (count={_recentHistory.Count})");
    }

    private void ValidateAppendOrder(HistoryEntry entry) {
        if (_recentHistory.Count == 0) {
            if (!IsObservationLike(entry)) { throw new InvalidOperationException("The first history entry must be an observation-like entry."); }
            return;
        }

        var last = _recentHistory[^1];
        var lastIsObservation = IsObservationLike(last);
        var nextIsObservation = IsObservationLike(entry);

        // 这里强制 Observation ↔ Action 的交替顺序，一方面让 AgentEngine 的状态机有稳定前提，
        // 另一方面也为 RecapBuilder 等只读视图提供“交错配对”的结构保障。
        if (lastIsObservation == nextIsObservation) { throw new InvalidOperationException($"History entries must alternate between observation-like and action entries. Last={last.Kind}, Next={entry.Kind}"); }
    }

    private static bool IsObservationLike(HistoryEntry entry)
        => entry.Kind is HistoryEntryKind.Observation or HistoryEntryKind.ToolResults or HistoryEntryKind.Recap;

    /// <summary>
    /// 根据条目在 Recent History 中的位置，解析其应使用的细节级别。
    /// </summary>
    /// <param name="ordinal">从最新条目开始的序号（0 表示最新）。</param>
    /// <returns>Detail（最新）或 Basic（其他）。</returns>
    private static LevelOfDetail ResolveDetailLevel(int ordinal)
        => ordinal == 0
            ? LevelOfDetail.Detail
            : LevelOfDetail.Basic;

    /// <summary>
    /// （内部方法）为 ToolEntry 附加待处理的通知。
    /// </summary>
    private void AttachNotificationsToObservation(ObservationEntry entry) {
        var notifications = TakeoutPendingNotifications();
        if (notifications == null) { return; }
        Debug.Assert(entry.Notifications is null);
        entry.AssignNotifications(notifications);
    }

    /// <summary>
    /// Recap 提交后的占位结果类型。
    /// </summary>
    public readonly record struct RecapCommitResult(
        ulong RecapEntrySerial,
        int RemovedEntryCount,
        // 考虑到主要用户时执行Recap任务的LLM，如果有异常就返回报错文本，应该是比抛异常更友好的反馈方式。
        string? ErrorMessage
    );
}
