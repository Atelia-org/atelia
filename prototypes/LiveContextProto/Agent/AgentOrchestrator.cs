using System.Threading;
using System.Threading.Tasks;
using Atelia.Diagnostics;
using Atelia.LiveContextProto.Provider;
using Atelia.LiveContextProto.State;
using Atelia.LiveContextProto.State.History;

namespace Atelia.LiveContextProto.Agent;

internal sealed record AgentInvocationResult(
    ModelOutputEntry Output,
    ToolResultsEntry? ToolResults
);

internal sealed class AgentOrchestrator {
    private const string DebugCategory = "Provider";

    private readonly AgentState _state;
    private readonly ProviderRouter _router;

    public AgentOrchestrator(AgentState state, ProviderRouter router) {
        _state = state;
        _router = router;
    }

    public async Task<AgentInvocationResult> InvokeAsync(
        ProviderInvocationOptions options,
        CancellationToken cancellationToken = default
    ) {
        var context = _state.RenderLiveContext();
        DebugUtil.Print(DebugCategory, $"[Orchestrator] Rendering context count={context.Count}");

        var plan = _router.Resolve(options);
        var request = new ProviderRequest(plan.StrategyId, plan.Invocation, context, plan.StubScriptName);

        var deltas = plan.Client.CallModelAsync(request, cancellationToken);
        var aggregate = await ModelOutputAccumulator.AggregateAsync(deltas, plan.Invocation, cancellationToken);

        var outputEntry = _state.AppendModelOutput(aggregate.OutputEntry);
        ToolResultsEntry? toolResultsEntry = null;

        if (aggregate.ToolResultsEntry is not null) {
            toolResultsEntry = _state.AppendToolResults(aggregate.ToolResultsEntry);
        }

        DebugUtil.Print(DebugCategory, "[Orchestrator] Invocation completed and appended to state");

        return new AgentInvocationResult(outputEntry, toolResultsEntry);
    }
}
