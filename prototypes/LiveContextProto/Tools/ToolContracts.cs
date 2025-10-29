using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Immutable;
using Atelia.LiveContextProto.Context;

namespace Atelia.LiveContextProto.Tools;

/// <summary>
/// 表示可供代理执行的工具定义。
/// <para>
/// <see cref="ToolExecutor"/> 会统一捕获并转换执行期间抛出的异常；
/// 因此实现通常无需在 <see cref="ExecuteAsync"/> 中自行捕获异常，除非需要补充结构化上下文后再抛出。
/// </para>
/// </summary>
internal interface ITool {
    string Name { get; }
    string Description { get; }
    IReadOnlyList<ToolParamSpec> Parameters { get; }
    /// <summary>
    /// 执行工具逻辑。实现可以直接抛出异常，由 <see cref="ToolExecutor"/> 统一捕获并转换为失败结果。
    /// </summary>
    ValueTask<LodToolExecuteResult> ExecuteAsync(IReadOnlyDictionary<string, object?>? arguments, CancellationToken cancellationToken);
}

internal static class ToolDefinitionBuilder {
    public static ToolDefinition FromTool(ITool tool) {
        if (tool is null) { throw new ArgumentNullException(nameof(tool)); }

        var parameters = tool.Parameters is { Count: > 0 }
            ? ImmutableArray.CreateRange(tool.Parameters)
            : ImmutableArray<ToolParamSpec>.Empty;

        return new ToolDefinition(tool.Name, tool.Description, parameters);
    }
    public static ImmutableArray<ToolDefinition> FromTools(IEnumerable<ITool> tools) {
        if (tools is null) { throw new ArgumentNullException(nameof(tools)); }

        var builder = ImmutableArray.CreateBuilder<ToolDefinition>();
        foreach (var tool in tools) {
            if (tool is null) { continue; }
            builder.Add(FromTool(tool));
        }

        return builder.ToImmutable();
    }
}
