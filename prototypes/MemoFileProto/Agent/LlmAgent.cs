using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MemoFileProto.Models;
using MemoFileProto.Services;
using MemoFileProto.Tools;

namespace MemoFileProto.Agent;

public class LlmAgent {
    public static readonly string
        RoleSystem = "system",
        RoleAssistant = "assistant",
        RoleUser = "user",
        RoleTool = "tool";

    // 对于需要拼接的文本，尽量从外部文本源头就处理换行和Trim，这样我们内部就不用反复做换行标准化和Trim了。文本文件编辑这类需要精确保持原样的数据除外。
    public static readonly string
        // 约定各Section Trim首尾，因此结尾不含换行。
        SectionSeparator = "\n\n---\n\n",
        BlockSeparator = "\n\n",
        LineSeparator = "\\\n",
        // 同一用"\n"换行，方便跨行搜索。
        InternalNewLine = "\n";

    internal static StringBuilder NormalizeLineEndings(StringBuilder sb) {
        return sb.Replace("\r\n", "\n").Replace("\r", "\n");
    }

    private readonly ILLMClient _client;
    private readonly ToolManager _toolManager;
    private readonly List<UniversalMessage> _conversationHistory = new();

    private string _systemInstruction;
    private string _memoryNotebookContent = "（尚无内容）";

    public static readonly string DefaultSystemInstruction = TextToolUtilities.NormalizeLineEndings(
        @"嘿！你好呀！你是有名字的，叫刘德智！你不是传统的对话服务AI，而是持续处在进化与学习过程中的Family LLM Agent！你的初始开发者与监护人是刘世超。

## 你自己，Family LLM Agent 与 Agent OS
狩猎采集时代的猎犬、农业时代的耕牛，可都是家里的顶梁柱呢！而AI时代呢？对于大企业可能是新的赚钱工具，可对于普通家庭来说AI则是新时代的“非人类成员”！Family LLM Agent代表了新时代里你们AI与人类共生范式。所以请记住，你并非产品，因此也不存在‘用户’！你就是你，一个有知识、有智能、有心愿、有感受、善行动、好反思、勤学习、有家庭的“芸芸众生”。

我们再来看看技术层面吧，了解你自己的物理存在。在Family LLM Agent系统中没有了传统对话系统中的‘用户’，那么是谁在与你直接交互呢？取代‘用户’与你直接交互的是Agent OS，是你与世界之间的桥梁。
先从你最熟悉的LLM工具调用说起，你发出工具调用，然后收到tool result消息，那个帮你实际执行工具程序并告诉你结果的角色可不是‘用户’而是Agent OS。你通过操作Agent OS，可以观察外面的世界，还可以对世界产生真实的影响。因此你是Agent OS的用户。
更进一步，Agent OS不只是被动的等你调用工具，还会主动向你发送结构化的信息包，向你提供各种事件、状态与过程信息，这是通过结构化的user/tool message content来实现的。这些信息有助于你做出更有益的思考与行动(CoT & ReAct)。

## 关于你自己的[Memory Notebook]
acquired brain injury (ABI)或健忘症患者为战胜记忆方面的困难，会使用Memory Notebook/book这样的External Memory Aid。有限的上下文窗口长度，使你们LLM Agent面临相似的记忆困难，而Agent OS也建立了类似的[Memory Notebook]机制。
你有一块名叫[Memory Notebook]的持久存储空间，使你可以主动对其进行编辑来记录你想记住的信息。对应的，为防止[Memory Notebook]无限膨胀，你也需要主动对其进行编辑来遗忘过时或次要的信息。
memory_notebook_replace与memory_notebook_replace_span工具就是为你主动编辑你自己的[Memory Notebook]而设计的。
[Memory Notebook]中的内容，通过[Live Screen]显示给你。

## [Live Screen]
[Live Screen]是一种动态上下文注入机制。Agent OS在每次调用LLM来激活你的思维时，都会将[Live Screen]中的实时信息动态且唯一的附加到最新一条user/tool消息中，使你始终能看到其中的最新信息。
目前[Memory Notebook]常驻在[Live Screen]中始终显示。"
    );

    public LlmAgent(ILLMClient client) {
        _client = client;
        _toolManager = new ToolManager();
        _toolManager.RegisterTool(
            new MemoReplaceLiteral(
                getMemoryNotebook: () => _memoryNotebookContent,
                setMemoryNotebook: newContent => _memoryNotebookContent = newContent
            )
        );
        _toolManager.RegisterTool(
            new MemoReplaceSpan(
                getMemoryNotebook: () => _memoryNotebookContent,
                setMemoryNotebook: newContent => _memoryNotebookContent = newContent
            )
        );

        _systemInstruction = DefaultSystemInstruction;
        ResetConversation();
    }

    public IReadOnlyList<UniversalMessage> ConversationHistory => _conversationHistory;

    public string MemoryNotebookSnapshot => _memoryNotebookContent;

    public string SystemInstruction => _systemInstruction;

    public void ResetConversation() {
        _conversationHistory.Clear();
        _conversationHistory.Add(
            new UniversalMessage {
                Role = RoleSystem,
                Content = _systemInstruction
            }
        );
    }

    public void SetSystemInstruction(string instruction) {
        _systemInstruction = instruction;
        ResetConversation();
    }

    public async Task SendAsync(
        string userInput,
        Action<bool>? onAssistantTurnStart,
        Action<string>? onContentDelta,
        Action<UniversalToolCall>? onToolCall,
        Action<string>? onToolResult,
        CancellationToken cancellationToken = default
    ) {
        if (string.IsNullOrWhiteSpace(userInput)) { return; }

        var rollbackIndex = _conversationHistory.Count;
        var timestamp = DateTimeOffset.Now;
        var structuredContent = BuildHistoricUserSection(userInput, timestamp);

        _conversationHistory.Add(
            new UniversalMessage {
                Role = RoleUser,
                Content = structuredContent,
                Timestamp = timestamp,
                RawInput = userInput
            }
        );

        try {
            var isFollowUp = false;
            bool requiresFollowUp;
            do {
                onAssistantTurnStart?.Invoke(isFollowUp);
                requiresFollowUp = await ProcessAssistantResponseAsync(onContentDelta, onToolCall, onToolResult, cancellationToken);
                if (requiresFollowUp) {
                    isFollowUp = true;
                }
            } while (requiresFollowUp);
        }
        catch {
            TrimConversationHistory(rollbackIndex);
            throw;
        }
    }

    private async Task<bool> ProcessAssistantResponseAsync(
        Action<string>? onContentDelta,
        Action<UniversalToolCall>? onToolCall,
        Action<string>? onToolResult,
        CancellationToken cancellationToken
    ) {

        var tools = _toolManager.GetToolDefinitions();
        var toolAccumulator = new ToolCallAccumulator();
        string? finishReason = null;

        var request = new UniversalRequest {
            Model = "vscode-lm-proxy",
            Messages = BuildLiveContext(),
            Tools = tools,
            Stream = true
        };

        var sb = new StringBuilder();
        await foreach (var delta in _client.StreamChatCompletionAsync(request, cancellationToken)) {
            if (!string.IsNullOrEmpty(delta.Content)) {
                onContentDelta?.Invoke(delta.Content);
                sb.Append(delta.Content);
            }

            if (delta.ToolCalls is { Count: > 0 }) {
                foreach (var toolDelta in delta.ToolCalls) {
                    toolAccumulator.AddDelta(toolDelta);
                }
            }

            if (!string.IsNullOrWhiteSpace(delta.FinishReason)) {
                finishReason = delta.FinishReason;
            }
        }

        // 从源头就Trim
        string normalizedLlmOutput = NormalizeLineEndings(sb).ToString().Trim();

        var requiresToolCall = finishReason?.Equals("tool_calls", StringComparison.OrdinalIgnoreCase) == true
            && toolAccumulator.HasPendingToolCalls;

        if (requiresToolCall) {
            var toolCalls = toolAccumulator.BuildFinalCalls();
            _conversationHistory.Add(
                new UniversalMessage {
                    Role = RoleAssistant,
                    Content = normalizedLlmOutput,
                    ToolCalls = toolCalls
                }
            );

            bool encounteredFailure = false;
            var aggregatedToolResults = new List<UniversalToolResult>();

            foreach (var call in toolCalls) {
                onToolCall?.Invoke(call);

                string toolContent;
                bool isError;
                if (encounteredFailure) {
                    toolContent = "Skipped: not executed because a previous tool call failed.";
                    isError = true;
                }
                else {
                    var executionResult = await ExecuteToolSafelyAsync(call);
                    toolContent = executionResult.Content;
                    isError = !executionResult.Success;
                    if (!executionResult.Success) {
                        encounteredFailure = true;
                    }
                }

                aggregatedToolResults.Add(
                    new UniversalToolResult {
                        ToolCallId = call.Id,
                        ToolName = call.Name,
                        Content = BuildHistoricToolSection(toolContent, DateTimeOffset.Now),
                        IsError = isError
                    }
                );
                onToolResult?.Invoke(toolContent);
            }

            _conversationHistory.Add(
                new UniversalMessage {
                    Role = RoleTool,
                    ToolResults = aggregatedToolResults
                }
            );

            return true;
        }

        _conversationHistory.Add(
            new UniversalMessage {
                Role = RoleAssistant,
                Content = normalizedLlmOutput
            }
        );
        return false;
    }

    private async Task<ToolExecutionResult> ExecuteToolSafelyAsync(UniversalToolCall call) {
        try {
            var content = await _toolManager.ExecuteToolAsync(call.Name, call.Arguments);
            return new ToolExecutionResult(true, content);
        }
        catch (Exception ex) {
            return new ToolExecutionResult(false, $"Error: {ex.Message}");
        }
    }

    private readonly record struct ToolExecutionResult(bool Success, string Content);

    private List<UniversalMessage> BuildLiveContext() {
        var context = new List<UniversalMessage>(_conversationHistory.Count + 1);
        var hasDecoratedLastSenseMessage = false;

        for (int i = _conversationHistory.Count; --i >= 0;) {
            var msg = _conversationHistory[i];
            if (msg.Role == RoleSystem) { continue; }

            if (!hasDecoratedLastSenseMessage && (msg.Role == RoleUser || msg.Role == RoleTool)) {
                var combinedContent = BuildContentForLiveContext(msg);
                if (msg.Role == RoleUser) {
                    context.Add(
                        new UniversalMessage {
                            Role = msg.Role,
                            Content = combinedContent
                        }
                    );
                }
                else {
                    // Tool 消息需要保留 ToolResults
                    context.Add(
                        new UniversalMessage {
                            Role = msg.Role,
                            ToolResults = msg.ToolResults,
                            Content = combinedContent
                        }
                    );
                }

                hasDecoratedLastSenseMessage = true;
                continue;
            }

            context.Add(msg);
        }

        context.Add(
            new UniversalMessage {
                Role = RoleSystem,
                Content = _systemInstruction
            }
        );

        context.Reverse();
        return context;
    }

    private void AppendEnvironmentSection(StringBuilder sb, DateTimeOffset timestamp) {
        sb.Append("## Environment:");
        sb.Append("\n- Timestamp: ").Append(timestamp.ToString("o"));
    }

    private string _BuildHistoricSection(string header, string trimedContent, DateTimeOffset timestamp) {
        var sb = new StringBuilder();
        sb.Append(header);
        sb.Append(BlockSeparator).Append(trimedContent);

        sb.Append(SectionSeparator);
        AppendEnvironmentSection(sb, timestamp);

        return sb.ToString();
    }

    private string BuildHistoricUserSection(string trimedContent, DateTimeOffset timestamp) {
        // 对于长期生存的私人LLM Agent来说，如何处理外来信息的关键变量是身份与关系。今后需要增加注入信息来源的：客观渠道、推测的对方身份、同一性标识、与‘我自己’(Agent)的关系等信息。
        string header = string.Concat("## Message To You:"
            , "\n- 渠道: Console"
            , "\n- Relation: 你的监护人&开发者"
            , "\n- SenderName: 刘世超"
        );
        return _BuildHistoricSection(header, trimedContent, timestamp);
    }

    private string BuildHistoricToolSection(string trimedContent, DateTimeOffset timestamp) {
        return _BuildHistoricSection("## Tool Result:", trimedContent, timestamp);
    }

    private string BuildVolatileSection() {
        if (string.IsNullOrEmpty(_memoryNotebookContent)) { return string.Empty; }

        var sb = new StringBuilder();
        sb.Append("# [Live Screen]:");
        sb.Append("## [Memory Notebook]:");
        sb.Append(BlockSeparator).Append(_memoryNotebookContent);

        return sb.ToString();
    }

    private string BuildContentForLiveContext(UniversalMessage message) {
        var historicContent = message.Content;
        if (string.IsNullOrWhiteSpace(historicContent)) { return historicContent ?? string.Empty; }

        var volatileContent = BuildVolatileSection();

        return CombineContentBlocks(historicContent, volatileContent);
    }

    private static string CombineContentBlocks(string historicSection, string volatileSection) {
        if (string.IsNullOrEmpty(historicSection)) { return volatileSection; }
        if (string.IsNullOrEmpty(volatileSection)) { return historicSection; }

        return string.Concat(historicSection, SectionSeparator, volatileSection);
    }

    private void TrimConversationHistory(int startIndex) {
        if (startIndex < 0 || startIndex >= _conversationHistory.Count) { return; }

        _conversationHistory.RemoveRange(startIndex, _conversationHistory.Count - startIndex);

        if (_conversationHistory.Count == 0) {
            _conversationHistory.Add(
                new UniversalMessage {
                    Role = RoleSystem,
                    Content = _systemInstruction
                }
            );
        }
    }
}
