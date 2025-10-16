using System.Collections.Generic;
using System.Collections.Immutable;
using Atelia.LiveContextProto.State.History;
using Atelia.LiveContextProto.Provider;

namespace Atelia.LiveContextProto.Context;

internal sealed record LlmInvocationOptions(
    string StrategyId
);

internal sealed record LlmInvocationPlan(
    string StrategyId,
    IProviderClient Client,
    ModelInvocationDescriptor Invocation
);

internal sealed record LlmRequest(
    string StrategyId,
    ModelInvocationDescriptor Invocation,
    IReadOnlyList<IContextMessage> Context,
    ImmutableArray<ToolDefinition> Tools
);
