using Atelia.TextAdv2.Gym;

namespace Atelia.TextAdv2.DefaultAgent;

/// <summary>
/// 一个最小参考 policy：如果当前位置有 available move，就选择排序后的第一个；否则 keep。
/// 它主要用于验证 assembly 边界与 host contract，而不是提供强行为。
/// </summary>
public sealed class FirstAvailableMoveDefaultAgentPolicy : IDefaultAgentTurnPolicy {
    public ValueTask<AgentTurnDecision> DecideAsync(DefaultAgentTurnContext context, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(context);

        var firstMove = context.TurnInput.SelfObservation.AvailableMoves.FirstOrDefault();
        if (firstMove is null) {
            return ValueTask.FromResult(
                AgentTurnDecision.Keep(reasoning: "no-available-move")
            );
        }

        return ValueTask.FromResult(
            AgentTurnDecision.Move(firstMove.PassageId, reasoning: "first-available-move")
        );
    }
}
