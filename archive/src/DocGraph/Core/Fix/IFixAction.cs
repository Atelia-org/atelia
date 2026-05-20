// DocGraph v0.1 - 修复动作接口
// 参考：implementation-plan.md §3.3

namespace Atelia.DocGraph.Core.Fix;

/// <summary>
/// 修复动作接口。
/// </summary>
public interface IFixAction {
    /// <summary>
    /// 检查是否可以执行此修复动作。
    /// </summary>
    /// <param name="context">修复上下文。</param>
    /// <returns>是否可以执行。</returns>
    bool CanExecute(FixContext context);

    /// <summary>
    /// 获取修复动作的描述（用于确认提示）。
    /// </summary>
    string Describe();

    /// <summary>
    /// 获取修复预览（用于 dry-run 模式）。
    /// </summary>
    string Preview();

    /// <summary>
    /// 执行修复动作。
    /// </summary>
    /// <param name="workspaceRoot">工作区根目录。</param>
    /// <returns>修复结果。</returns>
    FixResult Execute(string workspaceRoot);
}

/// <summary>
/// 修复上下文。
/// </summary>
public class FixContext {
    /// <summary>
    /// 文档图。
    /// </summary>
    public DocumentGraph Graph { get; }

    /// <summary>
    /// 修复选项。
    /// </summary>
    public FixOptions Options { get; }

    /// <summary>
    /// 工作区根目录。
    /// </summary>
    public string WorkspaceRoot { get; }

    /// <summary>
    /// 创建修复上下文。
    /// </summary>
    public FixContext(DocumentGraph graph, FixOptions options, string workspaceRoot) {
        Graph = graph;
        Options = options;
        WorkspaceRoot = workspaceRoot;
    }
}
