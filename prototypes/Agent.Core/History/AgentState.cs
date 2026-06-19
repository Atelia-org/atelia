using System;
using System.Diagnostics;
using Atelia.Completion.Abstractions;
using Atelia.Diagnostics;

namespace Atelia.Agent.Core.History;

/// <summary>
/// Agent 状态管理器，负责维护 RecentHistory 事件账本以及待注入的通知队列。
/// </summary>
/// <remarks>
/// 职能定位：Recent History 的工作集协调器。
/// 本类型持有一个进程内 <c>_workingSet</c> cache，表示 Agent 的短期工作记忆（Recent History + pending notifications），
/// 以“事件账本”而不是“provider message 日志”的粒度记录最近的若干条目，用于实时上下文投影与反射操作。
/// 在 non-live / snapshot 路径下，这个 working set 本身就是当前真相；在 repo-backed live durable workspace 路径下，
/// 真相锚定在 workspace durable object，本类型只把 working set 作为带明确 reload/backfill 边界的从属镜像。
/// 发给模型的历史消息序列不是直接复用本账本，而是由 <see cref="ProjectInvocationContext"/> 在调用前按需要投影与合并。
/// 与 durable history/workspace 的分工：
/// - working set（本类型）：维护 RecentHistory cache，支持快速追加、反射编辑（裁剪、降级）、上下文渲染。
/// - live workspace：在 repo-backed 路径下持有 authoritative recent history / pending notifications，并负责 stable-boundary commit。
/// - Recap 边界：当 RecapEntry 生成并写回 workspace 后，其覆盖的原始条目会从 RecentHistory cache 中移除；更早的 durable 历史不再由本类型直接承载。
/// 后续计划（重构路线图）：
/// - 新增 RecapEntry：作为 ObservationEntry 的派生类，携带"覆盖范围元数据"（如 CoveredUntilEntrySerial / 时间戳）。
/// - 引入 EntrySerial：为每个 HistoryEntry 分配唯一递增序列号，便于跨内存/持久层定位与追踪。
/// - 反射机制扩展：提供 IHistoryReflection 接口，支持 PeekRange / MarkAsRecapped / RemoveRecappedEntries / DowngradeDetailLevel 等操作，作用范围限定在 RecentHistory。
/// - HistoryEntry 部分可变化：允许对 RecentHistory 中的条目动态调整 DetailLevel（Detail → Basic），以实现渐进式压缩，但保持其他字段不可变。
/// - HistoryLimitOptions：配置 RecentHistory 的容量策略（条数 / Token 估算 / 时间窗口），触发自动 Recap 流程。
/// - 持久化协调：进一步明确 working set cache 的失效边界，确保 recap / tail rewrite / live append 后 durable truth 与 cache reload 一致。
/// 本设计遵循"短期工作集 cache + live durable workspace + Recap 边界"的分层记忆架构。
/// </remarks>
public sealed partial class AgentState {
    private readonly AgentStateWorkingSet _workingSet = new();

    /// <summary>
    /// 指示历史尾部是否存在一个等待下次 LLM 调用继续补完的 Injection prefix。
    /// 这是显式账本事实，而不是额外持久化的隐藏标志位。
    /// </summary>
    public bool HasPendingActionContinuation => RecentHistory.Count > 0 && RecentHistory[^1] is InjectionEntry;

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
        DebugUtil.Info("History", $"AgentState initialized with prompt length={systemPrompt.Length}");
    }

    /// <summary>
    /// 获取当前 Agent 的系统提示词。
    /// </summary>
    public string SystemPrompt { get; private set; }

    /// <summary>
    /// 获取内存中的 Recent History（只读视图）。
    /// </summary>
    /// <remarks>
    /// 此列表仅包含当前 working set 中的最近活跃条目，不包含已被 recap 吞并到更早 durable 历史的部分。
    /// 在 live workspace 路径下，它是 authoritative durable recent history 的本地 cache 视图，而不是独立真相。
    /// </remarks>
    public IReadOnlyList<HistoryEntry> RecentHistory => _workingSet.RecentHistory;

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
    public bool HasPendingNotification => _workingSet.HasPendingNotifications;

    /// <summary>
    /// 追加主机通知到待处理队列。
    /// </summary>
    /// <param name="item">通知内容。</param>
    /// <remarks>
    /// 通知会在下一条 ObservationEntry 或 ToolEntry 追加时自动附加。
    /// 未来计划增强为带 ID 的确认机制，确保模型成功消费后才移除。
    /// </remarks>
    public void AppendNotification(string item) {
        if (item is null) { throw new ArgumentNullException(nameof(item)); }
        EnsureWorkspaceSessionOpen();
        if (_workspaceSession is null) {
            _workingSet.EnqueuePendingNotification(item); // TODO: 更具体的消息类型，更多元数据。
            return;
        }

        try {
            ApplyPendingNotificationsState(_workspaceSession.AppendPendingNotification(item));
        }
        catch {
            ReloadPendingNotificationsFromWorkspaceSession();
            throw;
        }
    }

    /// <summary>
    /// 追加模型输出（ActionEntry）到 Recent History。
    /// </summary>
    /// <param name="entry">模型生成的动作条目。</param>
    /// <returns>追加后的条目实例（与输入相同）。</returns>
    public ActionEntry AppendAction(ActionEntry entry) {
        if (entry is null) { throw new ArgumentNullException(nameof(entry)); }
        EnsureWorkspaceSessionOpen();
        if (_workspaceSession is not null) {
            try {
                var mutation = _workspaceSession.AppendAction(entry);
                if (!TryApplyRecentHistoryDelta(
                    mutation.AuthoritativePreRecentHistory,
                    mutation.AppendedEntry,
                    mutation.LastSerial
                )) {
                    ReloadRecentHistoryFromWorkspaceSession();
                }
                return entry;
            }
            catch {
                ReloadRecentHistoryFromWorkspaceSession();
                throw;
            }
        }
        ValidateAppendOrder(entry);
        AppendEntryCoreLocal(entry);
        return entry;
    }

    /// <summary>
    /// 追加观测输入（ObservationEntry）到 Recent History，并自动附加待处理的通知。
    /// </summary>
    /// <param name="entry">观测条目。</param>
    /// <param name="inlineNotifications">当前轮内联追加的 recent events（可选）。</param>
    /// <returns>附加通知后的条目实例。</returns>
    public ObservationEntry AppendObservation(ObservationEntry entry, string? inlineNotifications = null) {
        if (entry is null) { throw new ArgumentNullException(nameof(entry)); }
        EnsureWorkspaceSessionOpen();
        if (_workspaceSession is not null) {
            try {
                var mutation = _workspaceSession.AppendObservation(entry, inlineNotifications);
                if (!TryApplyWorkingSetDelta(
                    mutation.AuthoritativePreRecentHistory,
                    mutation.AuthoritativePrePendingNotifications,
                    mutation.AppendedEntry,
                    mutation.LastSerial
                )) {
                    ReloadWorkingSetFromWorkspaceSession();
                }
                return entry;
            }
            catch {
                ReloadWorkingSetFromWorkspaceSession();
                throw;
            }
        }
        if (HasPendingActionContinuation) {
            throw new InvalidOperationException("Cannot append observation while a pending action continuation is open.");
        }
        ValidateAppendOrder(entry);
        AttachNotificationsToObservation(entry, inlineNotifications);
        AppendEntryCoreLocal(entry);
        return entry;
    }

    /// <summary>
    /// 追加工具执行结果（ToolEntry）到 Recent History，并自动附加待处理的通知。
    /// </summary>
    /// <param name="entry">工具结果条目，必须包含至少一条工具结果。</param>
    /// <returns>附加通知后的条目实例。</returns>
    /// <exception cref="ArgumentException">当条目不包含任何工具结果时抛出。</exception>
    public ToolResultsEntry AppendToolResults(ToolResultsEntry entry) {
        if (entry is null) { throw new ArgumentNullException(nameof(entry)); }
        EnsureWorkspaceSessionOpen();
        if (_workspaceSession is not null) {
            try {
                var mutation = _workspaceSession.AppendToolResults(entry);
                if (!TryApplyWorkingSetDelta(
                    mutation.AuthoritativePreRecentHistory,
                    mutation.AuthoritativePrePendingNotifications,
                    mutation.AppendedEntry,
                    mutation.LastSerial
                )) {
                    ReloadWorkingSetFromWorkspaceSession();
                }
                return entry;
            }
            catch {
                ReloadWorkingSetFromWorkspaceSession();
                throw;
            }
        }
        if (entry.Results is not { Count: > 0 }) { throw new ArgumentException("ToolResultsEntry must include at least one tool result.", nameof(entry)); }
        if (HasPendingActionContinuation) {
            throw new InvalidOperationException("Cannot append tool results while a pending action continuation is open.");
        }
        ValidateAppendOrder(entry);
        AttachNotificationsToObservation(entry);
        AppendEntryCoreLocal(entry);
        return entry;
    }

    /// <summary>
    /// 向最近的 assistant/action 历史中注入一段可续写内容。
    /// </summary>
    /// <remarks>
    /// 注入内容会作为独立 <see cref="InjectionEntry"/> 进入历史，
    /// 由投影层在构造 provider 上下文时与相邻的 actor-side entries 动态拼接成真正的 assistant/action message。
    /// </remarks>
    public ActionInjectionResult InjectActionContent(ActionInjectionRequest request) {
        ArgumentNullException.ThrowIfNull(request);
        EnsureWorkspaceSessionOpen();
        if (string.IsNullOrWhiteSpace(request.Content)) {
            throw new ArgumentException("Injected action content must not be null or whitespace.", nameof(request));
        }
        if (_workspaceSession is not null) {
            try {
                var mutation = _workspaceSession.InjectActionContent(request);
                if (!TryApplyRecentHistoryDelta(
                    mutation.AuthoritativePreRecentHistory,
                    mutation.AppendedEntry,
                    mutation.LastSerial
                )) {
                    ReloadRecentHistoryFromWorkspaceSession();
                }
                LogInjectedActionContent(request.Source.Kind, mutation.Result);
                return mutation.Result;
            }
            catch {
                ReloadRecentHistoryFromWorkspaceSession();
                throw;
            }
        }

        if (RecentHistory.Count == 0) {
            throw new InvalidOperationException("Cannot inject action content into empty history. At least one prior ActionEntry is required.");
        }

        var injectedBlockKind = RecentHistoryRules.ResolveInjectedBlockKind(RecentHistory, request);
        if (RecentHistory[^1] is ActionEntry tailAction) {
            RecentHistoryRules.EnsureActionAcceptsInjection(tailAction, context: "inject after trailing action");
        }

        var injectionEntry = new InjectionEntry(
            content: request.Content,
            blockKind: injectedBlockKind,
            source: request.Source
        );
        ValidateAppendOrder(injectionEntry);
        AppendEntryCoreLocal(injectionEntry);

        var result = new ActionInjectionResult(
            InjectedEntrySerial: injectionEntry.Serial,
            InjectedBlockKind: injectedBlockKind
        );
        LogInjectedActionContent(request.Source.Kind, result);
        return result;
    }

    /// <summary>
    /// 更新系统提示词。
    /// </summary>
    /// <param name="prompt">新的系统提示词内容。</param>
    public void SetSystemPrompt(string prompt) {
        EnsureWorkspaceSessionOpen();
        if (_workspaceSession is null) {
            SystemPrompt = prompt;
        }
        else {
            try {
                SystemPrompt = _workspaceSession.SetSystemPrompt(prompt);
            }
            catch {
                ReloadWorkingSetFromWorkspaceSession();
                throw;
            }
        }
        DebugUtil.Info("History", $"System prompt updated length={prompt.Length}");
    }

    /// <summary>
    /// 对应 Key-Notes 中的 Context Projection。
    /// 按当前 invocation 语义把 RecentHistory 事件账本投影为 StablePrefix + ActiveTurnTail 两段 provider-facing 上下文。
    /// </summary>
    /// <param name="options">
    /// 投影选项。<see cref="ContextProjectionOptions.TargetInvocation"/> 为 <c>null</c>
    /// 表示非真实调用场景（如 Recap / UI / debug / 测试）；这是投影的一等公民语义，而非兼容层。
    /// </param>
    /// <returns>按时间顺序排列的两段上下文视图。</returns>
    /// <remarks>
    /// 仅遍历内存中的 Recent History，不包含已归档的持久历史。
    /// StablePrefix / ActiveTurnTail 的切分点严格由 <see cref="TurnAnalyzer"/> 的 Turn 边界语义决定。
    /// 连续的 actor-side entries（<see cref="ActionEntry"/> / <see cref="InjectionEntry"/>）会在此阶段动态拼接成单条 <see cref="ActionMessage"/>。
    /// App Windows 只注入一次。
    /// </remarks>
    public ProjectedInvocationContext ProjectInvocationContext(ContextProjectionOptions options) {
        if (options is null) { throw new ArgumentNullException(nameof(options)); }

        if (RecentHistory.Count == 0) {
            return new ProjectedInvocationContext(
                StablePrefix: Array.Empty<IHistoryMessage>(),
                ActiveTurnTail: Array.Empty<IHistoryMessage>()
            );
        }

        var currentTurn = TurnAnalyzer.Analyze(RecentHistory);
        var activeTurnStartIndex = DetermineActiveTurnStartIndex(currentTurn);
        var windowsCarrierIndex = FindWindowsCarrierIndex();
        var stablePrefix = ProjectHistoryRange(
            startIndex: 0,
            endExclusive: Math.Min(activeTurnStartIndex, RecentHistory.Count),
            options,
            currentTurn,
            windowsCarrierIndex,
            isInActiveTurn: false
        );
        var activeTurnTail = ProjectHistoryRange(
            startIndex: Math.Min(activeTurnStartIndex, RecentHistory.Count),
            endExclusive: RecentHistory.Count,
            options,
            currentTurn,
            windowsCarrierIndex,
            isInActiveTurn: true
        );

        return new ProjectedInvocationContext(stablePrefix, activeTurnTail);
    }

    /// <summary>
    /// （内部方法）取出并聚合所有待处理的通知。
    /// </summary>
    /// <returns>聚合后的通知内容，若无待处理通知则返回 <c>null</c>。</returns>
    /// <remarks>
    /// TODO: 改为确认机制，只有在模型成功消费后才真正移除通知，以支持重试场景下的实时性更新。
    /// </remarks>
    internal string? TakeoutPendingNotifications() {
        EnsureWorkspaceSessionOpen();
        var drained = _workspaceSession is null
            ? _workingSet.DrainPendingNotifications()
            : _workspaceSession.DrainPendingNotifications();
        if (drained.Length == 0) { return null; }

        if (_workspaceSession is not null) {
            ReloadWorkingSetFromWorkspaceSession();
        }

        // 后续找机会改成真的被模型读到后再移除出队列，使得模型调用失败后有机会读取新出现的notification。这需要特殊对待History中最后一条Entry是ModelInputEntry的情况，使其可以被更新，这需要与序列化到文件的机制相协调。
        return string.Join("\n", drained);
    }

    /// <summary>
    /// （内部方法）追加条目到 Recent History，并分配序列号。
    /// </summary>
    /// <param name="entry">要追加的条目。</param>
    /// <remarks>
    /// TODO: 当实现 HistoryLimitOptions 后，此方法需检查容量阈值，必要时触发 Recap 流程。
    /// </remarks>
    private void AppendEntryCoreLocal(HistoryEntry entry) {
        // fail-fast
        uint tokenEstimate = TokenEstimateHelper.GetDefault().Estimate(entry);
        entry.AssignTokenEstimate(tokenEstimate);

        // non-live / snapshot path 仍由本地 working set 负责分配 serial 并维护 RecentHistory。
        entry.AssignSerial(_workingSet.AllocateNextSerial());
        _workingSet.AppendHistoryEntry(entry);

        DebugUtil.Trace(
            "History",
            $"Appended {entry.Kind} entry serial={entry.Serial} tokens={entry.TokenEstimate} (count={RecentHistory.Count})"
        );
    }

    private void ValidateAppendOrder(HistoryEntry entry) {
        RecentHistoryRules.ValidateAppendOrder(RecentHistory, entry);
    }

    private static void LogInjectedActionContent(
        InjectionSourceKind sourceKind,
        ActionInjectionResult result
    ) {
        DebugUtil.Info(
            "History",
            $"Injected action continuation serial={result.InjectedEntrySerial} source={sourceKind} blockKind={result.InjectedBlockKind}"
        );
    }

    /// <summary>
    /// 将 prefix（[0..<paramref name="splitIndex"/>）替换为一条 <see cref="RecapEntry"/>，
    /// 保留 suffix（<paramref name="splitIndex"/>..）不变。用于 LLM 一次性摘要后的历史压缩。
    /// </summary>
    /// <param name="splitIndex">suffix 起始索引（由 <see cref="ContextSplitter.FindHalfContextSplitPoint"/> 返回）。</param>
    /// <param name="summary">LLM 摘要文本。</param>
    /// <returns>新创建的 <see cref="RecapEntry"/>。</returns>
    /// <remarks>
    /// <b>前置条件</b>：<c>splitIndex &gt;= 1</c> 且 <c>splitIndex &lt; RecentHistory.Count</c>；
    /// <c>RecentHistory[splitIndex - 1]</c> 为 observation-like；
    /// <c>RecentHistory[splitIndex]</c> 为 actor-side entry（<see cref="ActionEntry"/> / <see cref="InjectionEntry"/>）。
    /// 替换后序列仍满足 history transition 的合法性约束。
    /// <para>
    /// <b>Serial 约定</b>：Recap 走正常递增 working-set serial，保持全局唯一；
    /// <see cref="RecapEntry.InsteadSerial"/> 记录被替代段的最后一条 serial，保证历史链不断。
    /// </para>
    /// </remarks>
    internal RecapEntry ReplacePrefixWithRecap(int splitIndex, string summary) {
        EnsureWorkspaceSessionOpen();
        Debug.Assert(splitIndex >= 1, "splitIndex must be >= 1 (need at least one entry in prefix).");
        Debug.Assert(splitIndex < RecentHistory.Count, "splitIndex must be within bounds.");
        Debug.Assert(RecentHistory[splitIndex - 1].IsObservationLike, "Prefix must end with observation-like entry.");
        Debug.Assert(RecentHistory[splitIndex].IsActorLike, "Suffix must start with an actor-like entry.");

        RecapEntry recap;
        if (_workspaceSession is null) {
            var insteadSerial = RecentHistory[splitIndex - 1].Serial;
            recap = new RecapEntry(summary, insteadSerial);
            recap.AssignTokenEstimate(TokenEstimateHelper.GetDefault().Estimate(recap));
            recap.AssignSerial(_workingSet.AllocateNextSerial());

            _workingSet.ReplacePrefixWithRecap(splitIndex, recap);
        }
        else {
            _workspaceSession.ReplacePrefixWithRecap(splitIndex, summary);
            ReloadWorkingSetFromWorkspaceSession();
            recap = RecentHistory[0] as RecapEntry
                ?? throw new InvalidOperationException("Workspace recap replacement did not reload a recap entry at history prefix.");
        }

        DebugUtil.Info(
            "History",
            $"Compacted prefix replaced={splitIndex} insteadSerial={recap.InsteadSerial} recapSerial={recap.Serial} remaining={RecentHistory.Count}"
        );

        return recap;
    }

    private int DetermineActiveTurnStartIndex(CurrentTurnInfo currentTurn) {
        if (RecentHistory.Count == 0) { return 0; }
        if (currentTurn.StartIndex >= 0) { return currentTurn.StartIndex; }

        for (var index = 0; index < RecentHistory.Count; index++) {
            if (RecentHistory[index].IsActorLike) { return index; }
        }

        return RecentHistory.Count;
    }

    private int FindWindowsCarrierIndex() {
        for (var index = RecentHistory.Count - 1; index >= 0; index--) {
            if (RecentHistory[index].IsObservationLike) {
                return index;
            }
        }

        return -1;
    }

    private List<IHistoryMessage> ProjectHistoryRange(
        int startIndex,
        int endExclusive,
        ContextProjectionOptions options,
        CurrentTurnInfo currentTurn,
        int windowsCarrierIndex,
        bool isInActiveTurn
    ) {
        return HistoryRunProjector.Project(
            RecentHistory,
            startIndex,
            endExclusive,
            projectObservationLike: index => ProjectObservationLikeEntry(
                RecentHistory[index],
                windows: index == windowsCarrierIndex ? options.Windows : null
            ),
            projectActorRun: (runStart, runEnd) => ProjectActorRun(
                startIndex: runStart,
                endExclusive: runEnd,
                options,
                currentTurn,
                isInActiveTurn
            )
        );
    }

    private static ObservationMessage ProjectObservationLikeEntry(
        HistoryEntry entry,
        string? windows
    ) {
        return entry switch {
            RecapEntry recapEntry => new ObservationMessage(ObservationEntry.MergeContent(recapEntry.Content, windows)),
            ObservationEntry observationEntry => observationEntry.GetMessage(windows),
            _ => throw new ArgumentOutOfRangeException(nameof(entry), entry.Kind, "Unsupported observation-like history entry kind.")
        };
    }

    private ActionMessage? ProjectActorRun(
        int startIndex,
        int endExclusive,
        ContextProjectionOptions options,
        CurrentTurnInfo currentTurn,
        bool isInActiveTurn
    ) {
        var blocks = new List<ActionBlock>();
        for (var index = startIndex; index < endExclusive; index++) {
            blocks.AddRange(
                ProjectActorBlocks(
                    RecentHistory[index],
                    options,
                    currentTurn,
                    isInActiveTurn
                )
            );
        }

        return blocks.Count == 0
            ? null
            : new ActionMessage(blocks);
    }

    private static IReadOnlyList<ActionBlock> ProjectActorBlocks(
        HistoryEntry entry,
        ContextProjectionOptions options,
        CurrentTurnInfo currentTurn,
        bool isInActiveTurn
    ) {
        if (entry is InjectionEntry injectionEntry) {
            return ProjectInjectionBlocks(
                injectionEntry,
                options,
                currentTurn,
                isInActiveTurn
            );
        }

        IReadOnlyList<ActionBlock> sourceBlocks = entry switch {
            ActionEntry actionEntry => actionEntry.Message.Blocks,
            _ => throw new ArgumentOutOfRangeException(nameof(entry), entry.Kind, "Unsupported actor-like history entry kind.")
        };

        var retainThinkingInActiveTurn = isInActiveTurn && ShouldRetainThinkingInActiveTurn(options, currentTurn);
        var projectedBlocks = new List<ActionBlock>(sourceBlocks.Count);

        foreach (var block in sourceBlocks) {
            if (block is not ActionBlock.ReasoningBlock reasoningBlock) {
                projectedBlocks.Add(block);
                continue;
            }

            if (!retainThinkingInActiveTurn) { continue; }
            if (reasoningBlock.Origin != options.TargetInvocation) { continue; }
            projectedBlocks.Add(reasoningBlock);
        }

        return projectedBlocks;
    }

    private static IReadOnlyList<ActionBlock> ProjectInjectionBlocks(
        InjectionEntry injectionEntry,
        ContextProjectionOptions options,
        CurrentTurnInfo currentTurn,
        bool isInActiveTurn
    ) {
        ArgumentNullException.ThrowIfNull(injectionEntry);

        return injectionEntry.BlockKind switch {
            ActionBlockKind.Text => [new ActionBlock.Text(injectionEntry.Content)],
            ActionBlockKind.Thinking => ProjectInjectedThinkingBlock(injectionEntry, options, currentTurn, isInActiveTurn),
            _ => throw new InvalidOperationException(
                $"Unsupported injection block kind '{injectionEntry.BlockKind}'."
            )
        };
    }

    private static IReadOnlyList<ActionBlock> ProjectInjectedThinkingBlock(
        InjectionEntry injectionEntry,
        ContextProjectionOptions options,
        CurrentTurnInfo currentTurn,
        bool isInActiveTurn
    ) {
        var retainThinkingInActiveTurn = isInActiveTurn && ShouldRetainThinkingInActiveTurn(options, currentTurn);
        if (!retainThinkingInActiveTurn || options.TargetInvocation is null) {
            return Array.Empty<ActionBlock>();
        }

        return [
            new ActionBlock.TextReasoningBlock(
                injectionEntry.Content,
                options.TargetInvocation
            )
        ];
    }

    private static bool ShouldRetainThinkingInActiveTurn(
        ContextProjectionOptions options,
        CurrentTurnInfo currentTurn
    ) {
        if (options.TargetInvocation is null) { return false; }
        if (!currentTurn.HasExplicitStartBoundary) { return false; }
        return options.ThinkingMode == ThinkingProjectionMode.CurrentTurnOnly;
    }

    /// <summary>
    /// （内部方法）为 observation-like 条目附加待处理通知，以及当前轮内联追加的 recent events。
    /// </summary>
    private void AttachNotificationsToObservation(ObservationEntry entry, string? inlineNotifications = null) {
        var queuedNotifications = TakeoutPendingNotifications();
        var notifications = MergeNotifications(queuedNotifications, inlineNotifications);
        if (notifications is null) { return; }
        entry.MergeNotifications(notifications);
    }

    private static string? MergeNotifications(string? first, string? second) {
        if (first is null) { return second; }
        if (second is null) { return first; }
        return string.Join("\n", first, second);
    }
}
