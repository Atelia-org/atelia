using System.Collections.Immutable;

namespace Atelia.LiveContextProto.Context;

internal interface IContextMessage {
    ContextMessageRole Role { get; }
    DateTimeOffset Timestamp { get; }
}

internal interface IModelOutputMessage : IContextMessage {
    string Contents { get; }
    IReadOnlyList<ToolCallRequest> ToolCalls { get; }
}

internal record class ModelInputMessage(
    DateTimeOffset Timestamp,
    /// <summary>
    /// 描述变化，来自于AgentState.History。
    /// 先用拼接好的string作为类型简化首版实现，以后再按需扩展为支持多模态和可保持element边界。
    /// </summary>
    string? Notifications,

    /// <summary>
    /// 描述状态，来自于对Apps的渲染。
    /// </summary>
    string? Windows

) : IContextMessage {
    public virtual ContextMessageRole Role => ContextMessageRole.ModelInput;
}

internal record ToolResultsMessage(
    DateTimeOffset Timestamp,
    string? Notifications,
    string? Windows,
    IReadOnlyList<ToolCallResult> Results,
    string? ExecuteError
) : ModelInputMessage(Timestamp, Notifications, Windows) {
    public override ContextMessageRole Role => ContextMessageRole.ToolResults;
}

internal enum ContextMessageRole {
    ModelInput,
    ModelOutput,
    ToolResults
}

internal record TokenUsage(int PromptTokens, int CompletionTokens, int? CachedPromptTokens = null);
// Gemini的ThinkingSignature问题，可能GTP-5也有，加密后的Thinking文本，跨模型不通用。源于公共安全审查，模型思考时的输出难以复合公共安全标准，所以加密了。
