using System.Collections.Generic;
using Atelia.LiveContextProto.State.History;

namespace Atelia.LiveContextProto.Provider;

internal sealed record ProviderInvocationOptions(
    string StrategyId
);

internal sealed record ProviderInvocationPlan(
    string StrategyId,
    IProviderClient Client,
    ModelInvocationDescriptor Invocation
);

internal sealed record ProviderRequest(
    string StrategyId,
    ModelInvocationDescriptor Invocation,
    IReadOnlyList<IContextMessage> Context
);
