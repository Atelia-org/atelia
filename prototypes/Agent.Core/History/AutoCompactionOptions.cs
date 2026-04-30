namespace Atelia.Agent.Core.History;

/// <summary>
/// 用于自动上下文压缩的配置选项，包含压缩 LLM 调用所需的 prompt。
/// </summary>
/// <remarks>
/// 当 <see cref="AgentEngine"/> 在普通 LLM 调用前检测到上下文 token 量达到
/// <see cref="LlmProfile.SoftContextTokenCap"/> 时，会使用此配置触发半上下文压缩。
/// prompt 文本不由引擎内置，而是由调用者注入，便于在不同实验台项目中进行提示词工程。
/// </remarks>
/// <param name="SystemPrompt">摘要 LLM 的系统提示词。</param>
/// <param name="SummarizePrompt">追加在待摘要历史末尾的摘要请求消息。</param>
public sealed record AutoCompactionOptions(
    string SystemPrompt,
    string SummarizePrompt
);
