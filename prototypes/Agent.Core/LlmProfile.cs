using Atelia.Completion.Abstractions;
namespace Atelia.Agent.Core;

/// <summary>
/// 用于模型切换功能的内层对象。
/// 原型阶段回避次要复杂性，先不做配置的序列化和文件读写，仅在初始化阶段用代码构造。
/// </summary>
/// <param name="Client">完成（completion）协议客户端，决定 Provider 与 ApiSpec 归属。</param>
/// <param name="ModelId">具体模型标识，会与 <see cref="ICompletionClient.Name"/>、<see cref="ICompletionClient.ApiSpecId"/>
/// 一同写入 <see cref="CompletionDescriptor"/>。</param>
/// <param name="Name">用于在UI中显示，以及区分不同的LlmProfile实例。</param>
/// <param name="SoftContextTokenCap">此 Provider-Model 组合的有效上下文窗口软上限（token 估算值）。应传入大于 0 的合理预算值，用于在接近物理窗口前提前触发上下文管理。</param>
/// <remarks>
/// <b>切换时机约束</b>：profile 切换仅允许在 Turn 起点发生，即历史末尾不存在 <see cref="Atelia.Agent.Core.History.ActionEntry"/>
/// 跨越当前 <see cref="Atelia.Agent.Core.History.ObservationEntry"/> 之后的情况。
/// 在 Turn 中段（已发生模型调用、正在工具往返）传入与首次调用不一致的 profile，
/// <see cref="AgentEngine.StepAsync"/> 会抛出 <see cref="InvalidOperationException"/>。
/// 该约束的存在是为了支持加密 thinking/reasoning 内容的同 Turn 复用。
/// </remarks>
public sealed record LlmProfile(
    ICompletionClient Client,
    string ModelId,
    string Name,
    uint SoftContextTokenCap
);
