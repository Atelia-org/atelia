using Atelia.TextAdv2.Gym;

namespace Atelia.TextAdv2.DefaultAgent;

/// <summary>
/// DefaultAgent 内部 richer turn context 的构建边界。
/// Gym 自身不感知 goal / memory / budget，这些增强信息在这里叠加。
/// </summary>
public interface IDefaultAgentContextBuilder {
    ValueTask<DefaultAgentTurnContext> BuildAsync(AgentTurnInput input, CancellationToken ct = default);
}

/// <summary>
/// DefaultAgent 内部 policy seam。
/// 这层消费已构建好的 richer context，再经 adapter 对接到 Gym 的最小 turn contract。
/// </summary>
public interface IDefaultAgentTurnPolicy {
    ValueTask<AgentTurnDecision> DecideAsync(DefaultAgentTurnContext context, CancellationToken ct = default);
}

public interface IDefaultAgentGoalSource {
    ValueTask<DefaultAgentGoalState?> GetGoalAsync(AgentTurnInput input, CancellationToken ct = default);
}

public interface IDefaultAgentMemorySource {
    ValueTask<DefaultAgentMemorySlice> GetMemoryAsync(AgentTurnInput input, CancellationToken ct = default);
}

public interface IDefaultAgentBudgetSource {
    ValueTask<DefaultAgentTurnBudget> GetBudgetAsync(AgentTurnInput input, CancellationToken ct = default);
}
