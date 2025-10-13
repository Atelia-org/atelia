using MemoFileProto.Models;

namespace MemoFileProto.Tools;

/// <summary>
/// 工具接口，所有工具都应实现此接口
/// </summary>
public interface ITool {
    /// <summary>
    /// 工具名称
    /// </summary>
    string Name { get; }

    /// <summary>
    /// 工具描述
    /// </summary>
    string Description { get; }

    /// <summary>
    /// 获取工具定义（Provider 无关格式）
    /// </summary>
    UniversalTool GetToolDefinition();

    /// <summary>
    /// 执行工具
    /// </summary>
    /// <param name="arguments">工具参数（JSON字符串）</param>
    /// <returns>执行结果</returns>
    Task<string> ExecuteAsync(string arguments);
}
