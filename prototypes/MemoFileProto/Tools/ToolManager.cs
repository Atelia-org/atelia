namespace MemoFileProto.Tools;

/// <summary>
/// 工具管理器，用于注册和管理所有可用的工具
/// </summary>
public class ToolManager {
    private readonly Dictionary<string, ITool> _tools = new();

    public ToolManager() {
        // 工具由外部注册，不再在构造函数中硬编码
    }

    /// <summary>
    /// 注册一个工具
    /// </summary>
    public void RegisterTool(ITool tool) {
        _tools[tool.Name] = tool;
    }

    /// <summary>
    /// 获取所有工具定义
    /// </summary>
    public List<Models.UniversalTool> GetToolDefinitions() {
        return _tools.Values.Select(t => t.GetToolDefinition()).ToList();
    }

    /// <summary>
    /// 执行工具
    /// </summary>
    public async Task<string> ExecuteToolAsync(string toolName, string arguments) {
        if (!_tools.TryGetValue(toolName, out var tool)) { return $"Error: Tool '{toolName}' not found"; }

        return await tool.ExecuteAsync(arguments);
    }

    /// <summary>
    /// 检查工具是否存在
    /// </summary>
    public bool HasTool(string toolName) {
        return _tools.ContainsKey(toolName);
    }
}
