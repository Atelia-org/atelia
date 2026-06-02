using Atelia.TextAdv2.Runtime;

namespace Atelia.TextAdv2.Gym;

/// <summary>
/// 当前默认 resolver 只把“每个 agent 声明一个 primary intent”归一化为可执行 plan。
/// 它暂不承担 richer simultaneous conflict semantics；后续更复杂的裁决规则应替换这一层。
/// </summary>
public sealed class DefaultSequentialAgentTurnConflictResolver : IAgentTurnConflictResolver {
    public AgentTurnResolutionPlan Resolve(AgentTurnResolutionRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        var actions = new ResolvedAgentAction[request.Declarations.Length];
        for (int i = 0; i < request.Declarations.Length; i++) {
            actions[i] = ResolveDeclaration(request.TurnNumber, request.Declarations[i]);
        }

        return new AgentTurnResolutionPlan(actions);
    }

    private static ResolvedAgentAction ResolveDeclaration(long turnNumber, AgentIntentDeclaration declaration) {
        ArgumentNullException.ThrowIfNull(declaration);

        return declaration.Decision.Action switch {
            KeepAgentActionIntent => new ResolvedAgentAction(
                declaration.ActorId,
                declaration.Decision,
                AgentTurnResolutionStatus.Accepted,
                resolutionMessage: null,
                Step: null
            ),
            MoveAgentActionIntent move => new ResolvedAgentAction(
                declaration.ActorId,
                declaration.Decision,
                AgentTurnResolutionStatus.Accepted,
                resolutionMessage: null,
                Step: new BatchStepCommand {
                    RequestId = BuildStepRequestId(turnNumber, declaration.ActorId),
                    ActorId = declaration.ActorId,
                    PassageId = move.PassageId,
                }
            ),
            _ => new ResolvedAgentAction(
                declaration.ActorId,
                declaration.Decision,
                AgentTurnResolutionStatus.Rejected,
                resolutionMessage: $"Unsupported agent action intent '{declaration.Decision.Action.GetType().Name}'.",
                Step: null
            ),
        };
    }

    internal static string BuildStepRequestId(long turnNumber, string actorId)
        => $"turn-{turnNumber}:{actorId}";
}
