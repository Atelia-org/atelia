namespace Atelia.TextAdv2.Gym;

/// <summary>
/// Gym 对 agent 的最小 canonical seam。
///
/// 这层只关心：
/// - 给 agent 一次 turn 输入；
/// - 收回一个 primary action decision。
///
/// 更丰富的 goal / memory / budget / dynamic context，
/// 应在 agent 自己的 assembly 内部继续叠加，而不是直接膨胀这个最小 contract。
/// </summary>
public interface IAgentTurnPolicy {
    ValueTask<AgentTurnDecision> DecideAsync(AgentTurnInput input, CancellationToken ct = default);
}
