using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Immutable;
using Atelia.Completion.Abstractions;

namespace Atelia.Agent.Core.Tool;

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
