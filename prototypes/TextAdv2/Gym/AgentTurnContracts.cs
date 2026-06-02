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

public sealed record ResolvedAgentAction {
    public ResolvedAgentAction(
        string actorId,
        AgentTurnDecision decision,
        AgentTurnResolutionStatus status,
        string? resolutionMessage,
        BatchStepCommand? Step
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(decision);

        ActorId = actorId;
        Decision = decision;
        Status = status;
        ResolutionMessage = resolutionMessage;
        this.Step = Step;
    }

    public string ActorId { get; }

    public AgentTurnDecision Decision { get; }

    public AgentTurnResolutionStatus Status { get; }

    public string? ResolutionMessage { get; }

    public BatchStepCommand? Step { get; }
}

public sealed record AgentTurnResolutionPlan {
    public AgentTurnResolutionPlan(ResolvedAgentAction[] Actions) {
        ArgumentNullException.ThrowIfNull(Actions);
        this.Actions = Actions;
    }

    public ResolvedAgentAction[] Actions { get; }
}

public sealed record AgentTurnActorResult {
    public AgentTurnActorResult(
        string actorId,
        ActorContextObservation InitialObservation,
        AgentTurnDecision Decision,
        AgentTurnResolutionStatus ResolutionStatus,
        string? ResolutionMessage,
        ActorContextObservation FinalObservation,
        ActorMoveResult? MoveResult,
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
        this.FinalObservation = FinalObservation;
        this.MoveResult = MoveResult;
        this.Fault = Fault;
    }

    public string ActorId { get; }

    public ActorContextObservation InitialObservation { get; }

    public AgentTurnDecision Decision { get; }

    public AgentTurnResolutionStatus ResolutionStatus { get; }

    public string? ResolutionMessage { get; }

    public ActorContextObservation FinalObservation { get; }

    public ActorMoveResult? MoveResult { get; }

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
