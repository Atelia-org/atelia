using Atelia.Agent.Core.Tool;
using Atelia.Completion.Abstractions;

namespace Atelia.Agent.Core;

/// <summary>
/// 表示可供代理执行的工具定义。
/// <para>
/// <see cref="ToolExecutor"/> 会统一捕获并转换执行期间抛出的异常；
/// 因此实现通常无需在 <see cref="ExecuteAsync"/> 中自行捕获异常，除非需要补充结构化上下文后再抛出。
/// </para>
/// </summary>
public interface ITool {
    string Name { get; }
    string Description { get; }
    IReadOnlyList<ToolParamSpec> Parameters { get; }
    /// <summary>
    /// 执行工具逻辑。实现可以直接抛出异常，由 <see cref="ToolExecutor"/> 统一捕获并转换为失败结果。
    /// </summary>
    ValueTask<LodToolExecuteResult> ExecuteAsync(IReadOnlyDictionary<string, object?>? arguments, CancellationToken cancellationToken);
}
