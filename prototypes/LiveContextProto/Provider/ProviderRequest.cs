using System.Collections.Generic;
using Atelia.LiveContextProto.State.History;

namespace Atelia.LiveContextProto.Provider;

internal sealed record ProviderInvocationOptions(
    string StrategyId,
    string? StubScriptName = null
);

internal sealed record ProviderInvocationPlan(
    string StrategyId,
    IProviderClient Client,
    ModelInvocationDescriptor Invocation,
    string? StubScriptName
);

internal sealed record ProviderRequest(
    string StrategyId,
    ModelInvocationDescriptor Invocation,
    IReadOnlyList<IContextMessage> Context,
    string? StubScriptName
);
