using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Atelia.Completion.Abstractions;

namespace Atelia.Agent.Core.History;

/// <summary>
/// 上下文摘要工具：使用 LLM 将已投影的消息序列压缩为单条摘要文本。
/// </summary>
/// <remarks>
/// 此类不内置任何 prompt 文本，也不做 <see cref="HistoryEntry"/> → <see cref="IHistoryMessage"/> 投影——
/// 投影是调用者的职责（如 <see cref="AgentEngine"/> 或 batch runner）。
/// system prompt 由调用者注入，便于在不同实验台项目中进行提示词工程。
/// <para>
/// 典型用法（AgentEngine 内）：
/// 先通过 <see cref="ContextSplitter.FindHalfContextSplitPoint"/> 找到切分点，
/// 将 prefix 投影为 <see cref="IHistoryMessage"/> 列表（末尾追加 summarize prompt），
/// 再调用 <see cref="SummarizeAsync"/>。
/// </para>
/// <para>
/// 典型用法（batch runner / 测试）：
/// 直接从 JSON fixture 反序列化为 <see cref="IHistoryMessage"/> 列表，直接传入——
/// 因为 <see cref="IHistoryMessage"/> 是接口，测试环境可杜撰实现，无需构造真实 <see cref="HistoryEntry"/>。
/// </para>
/// </remarks>
public static class ContextSummarizer {

    /// <summary>
    /// 使用指定 LLM 将消息序列摘要为文本。
    /// </summary>
    /// <param name="profile">用于摘要调用的 LLM 配置。</param>
    /// <param name="messages">已投影的消息序列（按时间升序），末尾应包含摘要请求消息（role=Observation）。</param>
    /// <param name="systemPrompt">摘要 LLM 的系统提示词（由调用方注入）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>摘要文本；若输入为空则返回 <see cref="string.Empty"/>。</returns>
    public static async Task<string> SummarizeAsync(
        LlmProfile profile,
        IReadOnlyList<IHistoryMessage> messages,
        string systemPrompt,
        CancellationToken cancellationToken = default
    ) {
        if (messages.Count == 0) { return string.Empty; }

        var request = new CompletionRequest(
            ModelId: profile.ModelId,
            SystemPrompt: systemPrompt,
            Context: messages,
            Tools: ImmutableArray<ToolDefinition>.Empty
        );

        var aggregated = await profile.Client.StreamCompletionAsync(request, null, cancellationToken).ConfigureAwait(false);

        return aggregated.GetFlattenedText() ?? string.Empty;
    }
}
