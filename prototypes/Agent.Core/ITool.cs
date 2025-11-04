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
    /// <param name="arguments">
    /// 工具参数字典，键为参数名，值为参数值。
    /// <para>
    /// <strong>参数名匹配规则：</strong>字典键必须与 <see cref="ToolParamSpec.Name"/> 完全一致（区分大小写）。
    /// 不支持忽略大小写或别名匹配，以避免参数名碰撞检测的复杂性，并保持接口契约清晰。
    /// </para>
    /// <para>
    /// <strong>设计原因：</strong>当前团队内部调用路径可控，调用方拼写准确，强制大小写一致可更早暴露拼写错误；
    /// 若未来需要兼容外部协议的大小写差异，应在具体工具或适配层处理，而非在核心框架引入通用复杂度。
    /// </para>
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>工具执行结果。</returns>
    ValueTask<LodToolExecuteResult> ExecuteAsync(IReadOnlyDictionary<string, object?>? arguments, CancellationToken cancellationToken);
}
