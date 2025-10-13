namespace MemoFileProto.Models;

/// <summary>
/// Provider 无关的通用请求模型
/// </summary>
public class UniversalRequest {
    /// <summary>
    /// 模型标识符（可能是 vscode-lm-proxy 或具体模型名）
    /// </summary>
    public required string Model { get; init; }

    /// <summary>
    /// 消息列表
    /// </summary>
    public required List<UniversalMessage> Messages { get; init; }

    /// <summary>
    /// 工具列表（可选）
    /// </summary>
    public List<UniversalTool>? Tools { get; init; }

    /// <summary>
    /// 是否流式响应（默认 true）
    /// </summary>
    public bool Stream { get; init; } = true;

    /// <summary>
    /// 最大 Token 数（可选）
    /// </summary>
    public int? MaxTokens { get; init; }

    /// <summary>
    /// 温度参数（可选）
    /// </summary>
    public double? Temperature { get; init; }
}
