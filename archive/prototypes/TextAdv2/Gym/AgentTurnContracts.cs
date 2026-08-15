using System.Text.Json.Serialization;
using Atelia.TextAdv2.Runtime;

namespace Atelia.TextAdv2.Gym;

public sealed record HostedAgent {
    public HostedAgent(string actorId, IAgentTurnPolicy policy) {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(policy);

        ActorId = actorId;
        Policy = policy;
    }

    public string ActorId { get; }

    public IAgentTurnPolicy Policy { get; }
}

public sealed record AgentTurnInput {
    public AgentTurnInput(long turnNumber, ActorContextObservation selfObservation) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(turnNumber);
        ArgumentNullException.ThrowIfNull(selfObservation);

        TurnNumber = turnNumber;
        SelfObservation = selfObservation;
    }

    public long TurnNumber { get; }

    public ActorContextObservation SelfObservation { get; }
}

public sealed record AgentTurnDecision {
    public AgentTurnDecision(AgentActionIntent action, string? reasoning = null) {
        ArgumentNullException.ThrowIfNull(action);

        Action = action;
        Reasoning = reasoning;
    }

    public static AgentTurnDecision Keep(string? reasoning = null)
        => new(KeepAgentActionIntent.Instance, reasoning);

    public static AgentTurnDecision Move(string passageId, string? reasoning = null)
        => new(new MoveAgentActionIntent(passageId), reasoning);

    public static AgentTurnDecision StartRouteFollowing(
        string destinationLocationId,
        bool isInterruptible = true,
        string? reasoning = null
    )
        => new(new StartRouteFollowingAgentActionIntent(destinationLocationId, isInterruptible), reasoning);

    public static AgentTurnDecision StartMining(
        string worksiteId,
        bool isInterruptible = true,
        string? reasoning = null
    )
        => new(new StartMiningAgentActionIntent(worksiteId, isInterruptible), reasoning);

    public static AgentTurnDecision CancelCurrentProcess(string? reasoning = null)
        => new(CancelCurrentProcessAgentActionIntent.Instance, reasoning);

    public AgentActionIntent Action { get; }

    public string? Reasoning { get; }
}

public sealed record AgentTurnFault(string Message);

public sealed record AgentIntentDeclaration {
    public AgentIntentDeclaration(string actorId, AgentTurnDecision decision, AgentTurnFault? fault = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(decision);

        ActorId = actorId;
        Decision = decision;
        Fault = fault;
    }

    public string ActorId { get; }

    public AgentTurnDecision Decision { get; }

    public AgentTurnFault? Fault { get; }
}

public sealed record AgentTurnResolutionRequest {
    public AgentTurnResolutionRequest(long turnNumber, AgentIntentDeclaration[] declarations) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(turnNumber);
        ArgumentNullException.ThrowIfNull(declarations);

        TurnNumber = turnNumber;
        Declarations = declarations;
    }

    public long TurnNumber { get; }

    public AgentIntentDeclaration[] Declarations { get; }
}

public sealed record ResolvedAgentOperation {
    public ResolvedAgentOperation(
        string actorId,
        AgentTurnDecision decision,
        AgentTurnResolutionStatus resolutionStatus,
        string? resolutionMessage,
        ExecutableAgentAction? executableAction
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(decision);
        if (resolutionStatus == AgentTurnResolutionStatus.Accepted && executableAction is null) {
            throw new InvalidOperationException("Accepted resolved operation requires an executable action.");
        }
        if (resolutionStatus == AgentTurnResolutionStatus.Rejected && executableAction is not null) {
            throw new InvalidOperationException("Rejected resolved operation cannot keep an executable action.");
        }

        ActorId = actorId;
        Decision = decision;
        ResolutionStatus = resolutionStatus;
        ResolutionMessage = resolutionMessage;
        ExecutableAction = executableAction;
    }

    public string ActorId { get; }

    public AgentTurnDecision Decision { get; }

    public AgentTurnResolutionStatus ResolutionStatus { get; }

    public string? ResolutionMessage { get; }

    public ExecutableAgentAction? ExecutableAction { get; }
}

public sealed record AgentTurnResolutionPlan {
    public AgentTurnResolutionPlan(ResolvedAgentOperation[] Operations) {
        ArgumentNullException.ThrowIfNull(Operations);
        this.Operations = Operations;
    }

    public ResolvedAgentOperation[] Operations { get; }
}

/// <summary>
/// resolver 把原始 intent 收口成“host 可以直接执行”的 operation，
/// 从而避免 host 再次解释 agent-facing intent surface。
/// </summary>
public abstract record ExecutableAgentAction {
    public abstract AgentActionExecutionResult? Execute(SerialWorldRuntime runtime, string actorId);

    protected static ActorActivityObservation ObserveActivityAfterMutation(
        SerialWorldRuntime runtime,
        string actorId,
        Action mutation
    ) {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(mutation);

        mutation();
        return runtime.ObserveActorRuntimeState(actorId).CurrentActivity;
    }
}

public sealed record KeepExecutableAgentAction : ExecutableAgentAction {
    public static KeepExecutableAgentAction Instance { get; } = new();

    public override AgentActionExecutionResult? Execute(SerialWorldRuntime runtime, string actorId) {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        return null;
    }
}

public sealed record CancelCurrentProcessExecutableAgentAction : ExecutableAgentAction {
    public static CancelCurrentProcessExecutableAgentAction Instance { get; } = new();

    public override AgentActionExecutionResult Execute(SerialWorldRuntime runtime, string actorId) {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        return new AgentActionExecutionResult(
            "cancel-current-process",
            Move: null,
            ActivityAfterAction: ObserveActivityAfterMutation(runtime, actorId, () => {
                _ = runtime.CancelActorEmbodiedState(actorId);
            })
        );
    }
}

public sealed record MoveExecutableAgentAction(string PassageId) : ExecutableAgentAction {
    public override AgentActionExecutionResult Execute(SerialWorldRuntime runtime, string actorId) {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        return new AgentActionExecutionResult(
            "move",
            runtime.MoveActor(actorId, PassageId),
            runtime.ObserveActorRuntimeState(actorId).CurrentActivity
        );
    }
}

public sealed record StartRouteFollowingExecutableAgentAction(
    string DestinationLocationId,
    bool IsInterruptible
) : ExecutableAgentAction {
    public override AgentActionExecutionResult Execute(SerialWorldRuntime runtime, string actorId) {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        return new AgentActionExecutionResult(
            "start-follow-route",
            Move: null,
            ActivityAfterAction: ObserveActivityAfterMutation(runtime, actorId, () => {
                _ = runtime.StartActorRouteFollowing(actorId, DestinationLocationId, IsInterruptible);
            })
        );
    }
}

public sealed record StartMiningExecutableAgentAction(
    string WorksiteId,
    bool IsInterruptible
) : ExecutableAgentAction {
    public override AgentActionExecutionResult Execute(SerialWorldRuntime runtime, string actorId) {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        return new AgentActionExecutionResult(
            "start-mining",
            Move: null,
            ActivityAfterAction: ObserveActivityAfterMutation(runtime, actorId, () => {
                _ = runtime.StartActorMining(actorId, WorksiteId, IsInterruptible);
            })
        );
    }
}

public sealed record AgentActionExecutionResult(
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ActorMoveResult? Move,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ActorActivityObservation? ActivityAfterAction
);

internal sealed record AgentActionExecutionOutcome(
    AgentTurnResolutionStatus ResolutionStatus,
    string? ResolutionMessage,
    AgentTurnExecutionStatus ExecutionStatus,
    string? ExecutionMessage,
    AgentActionExecutionResult? ActionResult
);

public sealed record AgentTurnActorResult {
    public AgentTurnActorResult(
        string actorId,
        ActorContextObservation InitialObservation,
        AgentTurnDecision Decision,
        AgentTurnResolutionStatus ResolutionStatus,
        string? ResolutionMessage,
        AgentTurnExecutionStatus ExecutionStatus,
        string? ExecutionMessage,
        ActorContextObservation FinalObservation,
        AgentActionExecutionResult? ActionResult,
        AgentTurnFault? Fault
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(InitialObservation);
        ArgumentNullException.ThrowIfNull(Decision);
        ArgumentNullException.ThrowIfNull(FinalObservation);

        ActorId = actorId;
        this.InitialObservation = InitialObservation;
        this.Decision = Decision;
        this.ResolutionStatus = ResolutionStatus;
        this.ResolutionMessage = ResolutionMessage;
        this.ExecutionStatus = ExecutionStatus;
        this.ExecutionMessage = ExecutionMessage;
        this.FinalObservation = FinalObservation;
        this.ActionResult = ActionResult;
        this.Fault = Fault;
    }

    public string ActorId { get; }

    public ActorContextObservation InitialObservation { get; }

    public AgentTurnDecision Decision { get; }

    public AgentTurnResolutionStatus ResolutionStatus { get; }

    public string? ResolutionMessage { get; }

    public AgentTurnExecutionStatus ExecutionStatus { get; }

    public string? ExecutionMessage { get; }

    public ActorContextObservation FinalObservation { get; }

    public AgentActionExecutionResult? ActionResult { get; }

    public AgentTurnFault? Fault { get; }
}

public sealed record AgentTurnResult {
    public AgentTurnResult(long turnNumber, LogicalTimeSnapshot Time, AgentTurnActorResult[] Actors) {
        ArgumentOutOfRangeException.ThrowIfNegative(turnNumber);
        ArgumentNullException.ThrowIfNull(Time);
        ArgumentNullException.ThrowIfNull(Actors);

        TurnNumber = turnNumber;
        this.Time = Time;
        this.Actors = Actors;
    }

    public long TurnNumber { get; }

    public LogicalTimeSnapshot Time { get; }

    public AgentTurnActorResult[] Actors { get; }
}

public enum AgentTurnResolutionStatus {
    Accepted = 0,
    Rejected = 1,
}

public enum AgentTurnExecutionStatus {
    NotExecuted = 0,
    Succeeded = 1,
    Failed = 2,
}
