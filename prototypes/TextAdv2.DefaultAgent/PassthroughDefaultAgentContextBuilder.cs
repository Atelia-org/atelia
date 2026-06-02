using Atelia.TextAdv2.Gym;

namespace Atelia.TextAdv2.DefaultAgent;

/// <summary>
/// 第一版默认 context builder。
/// 它只负责把 goal / memory / budget 三种来源收口成统一 turn context，
/// 不绑定具体的 LLM prompt 或 retrieval 策略。
/// </summary>
public sealed class PassthroughDefaultAgentContextBuilder : IDefaultAgentContextBuilder {
    private readonly IDefaultAgentGoalSource _goalSource;
    private readonly IDefaultAgentMemorySource _memorySource;
    private readonly IDefaultAgentBudgetSource _budgetSource;

    public PassthroughDefaultAgentContextBuilder(
        IDefaultAgentGoalSource? goalSource = null,
        IDefaultAgentMemorySource? memorySource = null,
        IDefaultAgentBudgetSource? budgetSource = null
    ) {
        _goalSource = goalSource ?? EmptyGoalSource.Instance;
        _memorySource = memorySource ?? EmptyMemorySource.Instance;
        _budgetSource = budgetSource ?? DefaultBudgetSource.Instance;
    }

    public async ValueTask<DefaultAgentTurnContext> BuildAsync(AgentTurnInput input, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(input);

        var goalTask = _goalSource.GetGoalAsync(input, ct);
        var memoryTask = _memorySource.GetMemoryAsync(input, ct);
        var budgetTask = _budgetSource.GetBudgetAsync(input, ct);

        var goal = await goalTask;
        var memory = await memoryTask;
        var budget = await budgetTask;

        return new DefaultAgentTurnContext(input, goal, memory, budget);
    }

    private sealed class EmptyGoalSource : IDefaultAgentGoalSource {
        public static EmptyGoalSource Instance { get; } = new();

        public ValueTask<DefaultAgentGoalState?> GetGoalAsync(AgentTurnInput input, CancellationToken ct = default) {
            ArgumentNullException.ThrowIfNull(input);
            return ValueTask.FromResult<DefaultAgentGoalState?>(null);
        }
    }

    private sealed class EmptyMemorySource : IDefaultAgentMemorySource {
        public static EmptyMemorySource Instance { get; } = new();

        public ValueTask<DefaultAgentMemorySlice> GetMemoryAsync(AgentTurnInput input, CancellationToken ct = default) {
            ArgumentNullException.ThrowIfNull(input);
            return ValueTask.FromResult(DefaultAgentMemorySlice.Empty);
        }
    }

    private sealed class DefaultBudgetSource : IDefaultAgentBudgetSource {
        public static DefaultBudgetSource Instance { get; } = new();

        public ValueTask<DefaultAgentTurnBudget> GetBudgetAsync(AgentTurnInput input, CancellationToken ct = default) {
            ArgumentNullException.ThrowIfNull(input);
            return ValueTask.FromResult(DefaultAgentTurnBudget.Default);
        }
    }
}
