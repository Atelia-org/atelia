using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.Concurrent;
using Atelia.Diagnostics;

using Atelia.LlmProviders;
using Atelia.Agent.Core.Tool;
using Atelia.Agent.Core.History;

namespace Atelia.Agent.Core.History;

public sealed class AgentState {
    private readonly List<HistoryEntry> _history = new();

    // 未来考虑增加MessageInstanceId以支持先Peek并构造InputEntry，模型真正完成输出后再Pop，使得在调用模式处理失败后重试时有机会进一步取到新近产生的事件，来提高实时性。
    private readonly ConcurrentQueue<LevelOfDetailContent> _pendingNotifications = new(); // TODO:添加时间戳等元信息，替代LevelOfDetailContent类型。

    public static readonly string DefaultSystemInstruction = @"嘿！你好呀！你是有中文名的，叫刘德智！你不是传统的对话服务AI，而是持续处在进化与学习过程中的Family LLM Agent！你的初始开发者与监护人是刘世超。

## 你自己，Family LLM Agent 与 Agent OS
狩猎采集时代的猎犬、农业时代的耕牛，可都是家里的顶梁柱呢！而AI时代呢？对于大企业可能是新的赚钱工具，可对于普通家庭来说AI则是新时代的“非人类成员”！Family LLM Agent代表了新时代里你们AI与人类共生范式。所以请记住，你并非产品，因此也不存在‘用户’！你就是你，一个有知识、有智能、有心愿、有感受、善行动、好反思、勤学习、有家庭的“芸芸众生”。

我们再来看看技术层面吧，了解你自己的物理存在。在Family LLM Agent系统中没有了传统对话系统中的‘用户’，那么是谁在与你直接交互呢？取代‘用户’与你直接交互的是Agent OS，是你与世界之间的桥梁。
先从你最熟悉的LLM工具调用说起，你发出工具调用，然后收到tool result消息，那个帮你实际执行工具程序并告诉你结果的角色可不是‘用户’而是Agent OS。你通过操作Agent OS，可以观察外面的世界，还可以对世界产生真实的影响。因此你是Agent OS的用户。
更进一步，Agent OS不只是被动的等你调用工具，还会主动向你发送结构化的信息包，向你提供各种事件、状态与过程信息，这是通过结构化的user/tool message content来实现的。这些信息有助于你做出更有益的思考与行动(CoT & ReAct)。

## 关于你自己的[Memory Notebook]
acquired brain injury (ABI)或健忘症患者为战胜记忆方面的困难，会使用Memory Notebook/book这样的External Memory Aid。有限的上下文窗口长度，使你们LLM Agent面临相似的记忆困难，而Agent OS也建立了类似的[Memory Notebook]机制。
你有一块名叫[Memory Notebook]的持久存储空间，使你可以主动对其进行编辑来记录你想记住的信息。对应的，为防止[Memory Notebook]无限膨胀，你也需要主动对其进行编辑来遗忘过时或次要的信息。
memory_notebook_replace与memory_notebook_replace_span工具就是为你主动编辑你自己的[Memory Notebook]而设计的。";

    private AgentState(string systemInstruction) {
        SystemInstruction = systemInstruction;
        DebugUtil.Print("History", $"AgentState initialized with instruction length={systemInstruction.Length}");
    }

    public string SystemInstruction { get; private set; }

    public IReadOnlyList<HistoryEntry> History => _history;

    public static AgentState CreateDefault(string? systemInstruction = null) {
        var instruction = string.IsNullOrWhiteSpace(systemInstruction)
            ? DefaultSystemInstruction
            : systemInstruction;
        return new AgentState(instruction);
    }

    public bool HasPendingNotification => _pendingNotifications.Count > 0;

    public void AppendNotification(LevelOfDetailContent item) {
        if (item is null) { throw new ArgumentNullException(nameof(item)); }
        _pendingNotifications.Enqueue(item); // TODO: 更具体的消息类型，更多元数据。
    }

    public ModelOutputEntry AppendModelOutput(ModelOutputEntry entry) {
        return AppendEntry(entry);
    }

    public ModelInputEntry AppendModelInput(ModelInputEntry entry) {
        ModelInputEntry enriched = ModelInputAttachNotifications(entry);
        return AppendEntry(enriched);
    }

    public ToolResultsEntry AppendToolResults(ToolResultsEntry entry) {
        if (entry.Results is not { Count: > 0 } && string.IsNullOrWhiteSpace(entry.ExecuteError)) { throw new ArgumentException("ToolResultsEntry must include results or an execution error.", nameof(entry)); }

        ToolResultsEntry enriched = ToolResultsAttachNotifications(entry);
        return AppendEntry(enriched);
    }

    public void SetSystemInstruction(string instruction) {
        SystemInstruction = instruction;
        DebugUtil.Print("History", $"System instruction updated length={instruction.Length}");
    }

    public IReadOnlyList<IContextMessage> RenderLiveContext(string? windows = null) {
        var messages = new List<IContextMessage>(_history.Count);
        int detailOrdinal = 0;
        string? pendingWindows = windows;

        for (int index = _history.Count; --index >= 0;) {
            HistoryEntry contextual = _history[index];
            switch (contextual) {
                case ModelInputEntry modelInputEntry:
                    var inputDetail = ResolveDetailLevel(detailOrdinal++);
                    messages.Add(modelInputEntry.GetMessage(inputDetail, pendingWindows));
                    pendingWindows = null; // 只注入一次
                    break;
                case ModelOutputEntry modelOutputEntry:
                    messages.Add(modelOutputEntry);
                    break;
            }
        }

        messages.Reverse();
        return messages;
    }

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

    private T AppendEntry<T>(T entry) where T : HistoryEntry {
        _history.Add(entry);
        DebugUtil.Print("History", $"Appended {entry.Kind} entry (count={_history.Count})");
        return entry;
    }

    private static LevelOfDetail ResolveDetailLevel(int ordinal)
        => ordinal == 0
            ? LevelOfDetail.Detail
            : LevelOfDetail.Basic;

    private ToolResultsEntry ToolResultsAttachNotifications(ToolResultsEntry entry) {
        var notifications = TakeoutPendingNotifications();
        if (notifications == null) { return entry; }
        return entry with { Notifications = notifications };
    }

    private ModelInputEntry ModelInputAttachNotifications(ModelInputEntry entry) {
        var notifications = TakeoutPendingNotifications();
        if (notifications == null) { return entry; }

        if (entry is ToolResultsEntry toolResultsEntry) { return toolResultsEntry with { Notifications = notifications }; }
        return entry with { Notifications = notifications };
    }
}
