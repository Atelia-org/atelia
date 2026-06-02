using Atelia.TextAdv2.Runtime;

namespace Atelia.TextAdv2.Gym;

/// <summary>
/// 同进程 Agent Gym 的 turn orchestrator。
///
/// 当前版本刻意保持简单：
/// - 所有注册 agent 都视为 awake；
/// - turn 前批量观察，turn 后统一结算并推进 1 tick；
/// - conflict resolution 通过可替换 resolver 完成。
/// </summary>
public sealed class AgentTurnHost {
    private const long LogicalTicksPerTurn = 1;

    private readonly SerialWorldRuntime _runtime;
    private readonly HostedAgent[] _agents;
    private readonly IAgentTurnConflictResolver _resolver;
    private long _completedTurnCount;

    public AgentTurnHost(
        SerialWorldRuntime runtime,
        IEnumerable<HostedAgent> agents,
        IAgentTurnConflictResolver? resolver = null
    ) {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(agents);

        _runtime = runtime;
        _agents = [.. agents];
        _resolver = resolver ?? new DefaultSequentialAgentTurnConflictResolver();

        ValidateAgents(_agents);
    }

    public long CompletedTurnCount => _completedTurnCount;

    public async ValueTask<AgentTurnResult> RunTurnAsync(CancellationToken ct = default) {
        HostedAgent[] activeAgents = _agents;
        if (activeAgents.Length == 0) {
            return new AgentTurnResult(
                _completedTurnCount,
                _runtime.ObserveTime(),
                []
            );
        }

        long turnNumber = _completedTurnCount + 1;
        var initialObservations = ObserveActorContexts(activeAgents);
        var declarations = await CollectDeclarationsAsync(turnNumber, activeAgents, initialObservations, ct);
        var resolutionPlan = _resolver.Resolve(new AgentTurnResolutionRequest(turnNumber, declarations));
        var executionOutcomes = ExecuteResolvedOperations(resolutionPlan.Operations);
        var time = _runtime.AdvanceTime(LogicalTicksPerTurn);
        var finalObservations = ObserveActorContexts(activeAgents);
        var declarationsByActorId = declarations.ToDictionary(static x => x.ActorId, StringComparer.Ordinal);

        var actorResults = activeAgents
            .Select(agent => BuildActorResult(
                agent.ActorId,
                initialObservations[agent.ActorId],
                finalObservations[agent.ActorId],
                declarationsByActorId[agent.ActorId],
                executionOutcomes[agent.ActorId]
            ))
            .ToArray();

        _completedTurnCount = turnNumber;
        return new AgentTurnResult(turnNumber, time, actorResults);
    }

    private static void ValidateAgents(HostedAgent[] agents) {
        var duplicateActorIds = agents
            .GroupBy(static agent => agent.ActorId, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToArray();

        if (duplicateActorIds.Length > 0) {
            throw new InvalidOperationException(
                $"AgentTurnHost requires unique actor bindings, but duplicates were found: {string.Join(", ", duplicateActorIds)}."
            );
        }
    }

    private Dictionary<string, ActorContextObservation> ObserveActorContexts(HostedAgent[] agents) {
        var request = new BatchObserveRequest {
            Items = BuildActorContextObserveItems(agents),
        };
        var result = _runtime.ObserveBatch(request);
        return MapActorContextObservations(result, agents);
    }

    private static BatchObserveItem[] BuildActorContextObserveItems(HostedAgent[] agents)
        => agents
            .Select(agent => new BatchObserveItem {
                RequestId = agent.ActorId,
                Kind = "actor-context",
                ActorId = agent.ActorId,
            })
            .ToArray();

    private static Dictionary<string, ActorContextObservation> MapActorContextObservations(
        BatchObserveResult? result,
        HostedAgent[] agents
    ) {
        if (result is null) {
            throw new InvalidOperationException("AgentTurnHost expected post-observations, but runtime returned null.");
        }

        var observationsByActorId = new Dictionary<string, ActorContextObservation>(StringComparer.Ordinal);
        foreach (var item in result.Items) {
            if (item.Error is not null) {
                throw new InvalidOperationException(
                    $"AgentTurnHost failed to observe actor '{item.RequestId}': {item.Error.Message}"
                );
            }

            if (item.ActorContext is null) {
                throw new InvalidOperationException(
                    $"AgentTurnHost expected actor-context observation for '{item.RequestId}', but runtime returned no payload."
                );
            }

            observationsByActorId.Add(item.RequestId, item.ActorContext);
        }

        foreach (var agent in agents) {
            if (!observationsByActorId.ContainsKey(agent.ActorId)) {
                throw new InvalidOperationException(
                    $"AgentTurnHost did not receive actor-context observation for '{agent.ActorId}'."
                );
            }
        }

        return observationsByActorId;
    }

    private static async Task<AgentIntentDeclaration[]> CollectDeclarationsAsync(
        long turnNumber,
        HostedAgent[] agents,
        Dictionary<string, ActorContextObservation> observationsByActorId,
        CancellationToken ct
    ) {
        var tasks = agents
            .Select(agent => CollectDeclarationAsync(
                turnNumber,
                agent,
                observationsByActorId[agent.ActorId],
                ct
            ))
            .ToArray();

        return await Task.WhenAll(tasks);
    }

    private static async Task<AgentIntentDeclaration> CollectDeclarationAsync(
        long turnNumber,
        HostedAgent agent,
        ActorContextObservation observation,
        CancellationToken ct
    ) {
        try {
            var decision = await agent.Policy.DecideAsync(
                new AgentTurnInput(turnNumber, observation),
                ct
            );
            if (decision is null) {
                return CreateFaultedKeepDeclaration(
                    agent.ActorId,
                    "Agent policy returned null decision."
                );
            }

            return new AgentIntentDeclaration(agent.ActorId, decision);
        }
        catch (OperationCanceledException) {
            throw;
        }
        catch (Exception ex) {
            return CreateFaultedKeepDeclaration(agent.ActorId, ex.Message);
        }
    }

    private static AgentIntentDeclaration CreateFaultedKeepDeclaration(string actorId, string message)
        => new(
            actorId,
            AgentTurnDecision.Keep(reasoning: "policy-fault"),
            new AgentTurnFault(message)
        );

    private Dictionary<string, AgentActionExecutionOutcome> ExecuteResolvedOperations(ResolvedAgentOperation[] operations) {
        ArgumentNullException.ThrowIfNull(operations);

        var outcomes = new Dictionary<string, AgentActionExecutionOutcome>(StringComparer.Ordinal);
        foreach (var operation in operations) {
            AgentActionExecutionResult? actionResult = null;
            AgentTurnExecutionStatus executionStatus = AgentTurnExecutionStatus.NotExecuted;
            string? executionMessage = null;

            if (operation.ResolutionStatus == AgentTurnResolutionStatus.Accepted) {
                try {
                    actionResult = operation.ExecutableAction!.Execute(_runtime, operation.ActorId);
                    executionStatus = AgentTurnExecutionStatus.Succeeded;
                }
                catch (ArgumentException ex) {
                    executionStatus = AgentTurnExecutionStatus.Failed;
                    executionMessage = ex.Message;
                }
                catch (InvalidOperationException ex) {
                    executionStatus = AgentTurnExecutionStatus.Failed;
                    executionMessage = ex.Message;
                }
            }

            outcomes.Add(operation.ActorId, new AgentActionExecutionOutcome(
                operation.ResolutionStatus,
                operation.ResolutionMessage,
                executionStatus,
                executionMessage,
                actionResult
            ));
        }

        return outcomes;
    }

    private static AgentTurnActorResult BuildActorResult(
        string actorId,
        ActorContextObservation initialObservation,
        ActorContextObservation finalObservation,
        AgentIntentDeclaration declaration,
        AgentActionExecutionOutcome executionOutcome
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(initialObservation);
        ArgumentNullException.ThrowIfNull(finalObservation);
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(executionOutcome);

        return new AgentTurnActorResult(
            actorId,
            initialObservation,
            declaration.Decision,
            executionOutcome.ResolutionStatus,
            executionOutcome.ResolutionMessage,
            executionOutcome.ExecutionStatus,
            executionOutcome.ExecutionMessage,
            finalObservation,
            executionOutcome.ActionResult,
            declaration.Fault
        );
    }
}
