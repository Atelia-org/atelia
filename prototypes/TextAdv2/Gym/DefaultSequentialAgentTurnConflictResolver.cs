using Atelia.TextAdv2.Runtime;

namespace Atelia.TextAdv2.Gym;

/// <summary>
/// 当前默认 resolver 只把“每个 agent 声明一个 primary intent”归一化为可执行 plan。
/// 它暂不承担 richer simultaneous conflict semantics；后续更复杂的裁决规则应替换这一层。
/// </summary>
public sealed class DefaultSequentialAgentTurnConflictResolver : IAgentTurnConflictResolver {
    public AgentTurnResolutionPlan Resolve(AgentTurnResolutionRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        var operations = new ResolvedAgentOperation[request.Declarations.Length];
        for (int i = 0; i < request.Declarations.Length; i++) {
            operations[i] = ResolveDeclaration(request.Declarations[i]);
        }

        return new AgentTurnResolutionPlan(operations);
    }

    private static ResolvedAgentOperation ResolveDeclaration(AgentIntentDeclaration declaration) {
        ArgumentNullException.ThrowIfNull(declaration);

        return declaration.Decision.Action switch {
            KeepAgentActionIntent => new ResolvedAgentOperation(
                declaration.ActorId,
                declaration.Decision,
                AgentTurnResolutionStatus.Accepted,
                resolutionMessage: null,
                executableAction: KeepExecutableAgentAction.Instance
            ),
            CancelCurrentProcessAgentActionIntent => new ResolvedAgentOperation(
                declaration.ActorId,
                declaration.Decision,
                AgentTurnResolutionStatus.Accepted,
                resolutionMessage: null,
                executableAction: CancelCurrentProcessExecutableAgentAction.Instance
            ),
            MoveAgentActionIntent move => new ResolvedAgentOperation(
                declaration.ActorId,
                declaration.Decision,
                AgentTurnResolutionStatus.Accepted,
                resolutionMessage: null,
                executableAction: new MoveExecutableAgentAction(move.PassageId)
            ),
            StartRouteFollowingAgentActionIntent routeFollowing => new ResolvedAgentOperation(
                declaration.ActorId,
                declaration.Decision,
                AgentTurnResolutionStatus.Accepted,
                resolutionMessage: null,
                executableAction: new StartRouteFollowingExecutableAgentAction(
                    routeFollowing.DestinationLocationId,
                    routeFollowing.IsInterruptible
                )
            ),
            StartMiningAgentActionIntent mining => new ResolvedAgentOperation(
                declaration.ActorId,
                declaration.Decision,
                AgentTurnResolutionStatus.Accepted,
                resolutionMessage: null,
                executableAction: new StartMiningExecutableAgentAction(
                    mining.WorksiteId,
                    mining.IsInterruptible
                )
            ),
            _ => new ResolvedAgentOperation(
                declaration.ActorId,
                declaration.Decision,
                AgentTurnResolutionStatus.Rejected,
                resolutionMessage: $"Unsupported agent action intent '{declaration.Decision.Action.GetType().Name}'.",
                executableAction: null
            ),
        };
    }
}
