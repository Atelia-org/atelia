namespace MemoFileProto.Models;

/// <summary>
/// Provider 无关的通用工具定义
/// </summary>
public class UniversalTool {
    /// <summary>
    /// 工具名称
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// 工具描述
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// 工具参数的 JSON Schema（object 类型，可序列化为 JSON）
    /// </summary>
    public required object Parameters { get; init; }
}
