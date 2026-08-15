using Atelia.TextAdv2.Gym;
using Atelia.TextAdv2.Observation;
using Atelia.TextAdv2.Runtime;
using Atelia.TextAdv2.WorldTruth;
using Xunit;

namespace Atelia.TextAdv2.Tests;

public class AgentTurnHostTests {
    [Fact]
    public async Task RunTurnAsync_CollectsDecisionsAppliesEffectsAndAdvancesTimeAsync() {
        string repoDir = CreateTempRepoDir();

        try {
            using var runtime = SerialWorldRuntime.CreateEmpty(repoDir);
            AuthorMiniWorld(runtime);

            var host = new AgentTurnHost(
                runtime,
                [
                    new HostedAgent("alpha", new FixedDecisionPolicy(
                        new AgentTurnDecision(new MoveAgentActionIntent("start-goal"))
                    )),
                    new HostedAgent("beta", new FixedDecisionPolicy(
                        new AgentTurnDecision(KeepAgentActionIntent.Instance)
                    )),
                ]
            );

            var result = await host.RunTurnAsync();
            var actorsById = result.Actors.ToDictionary(static actor => actor.ActorId, StringComparer.Ordinal);

            Assert.Equal(1, result.TurnNumber);
            Assert.Equal(1, result.Time.CurrentTick);
            Assert.Equal(1, host.CompletedTurnCount);

            var alpha = actorsById["alpha"];
            Assert.Equal("start", alpha.InitialObservation.CurrentLocation.LocationId);
            Assert.Equal("goal", alpha.FinalObservation.CurrentLocation.LocationId);
            Assert.Equal(AgentTurnResolutionStatus.Accepted, alpha.ResolutionStatus);
            Assert.Equal(AgentTurnExecutionStatus.Succeeded, alpha.ExecutionStatus);
            Assert.NotNull(alpha.ActionResult);
            Assert.Equal("move", alpha.ActionResult!.Kind);
            Assert.NotNull(alpha.ActionResult.Move);
            Assert.Equal("goal", alpha.ActionResult.Move!.ToLocationId);
            Assert.Equal("idle", alpha.ActionResult.ActivityAfterAction!.Kind);
            Assert.Equal(1, alpha.FinalObservation.CurrentTick);

            var beta = actorsById["beta"];
            Assert.Equal("goal", beta.InitialObservation.CurrentLocation.LocationId);
            Assert.Equal("goal", beta.FinalObservation.CurrentLocation.LocationId);
            Assert.Equal(AgentTurnResolutionStatus.Accepted, beta.ResolutionStatus);
            Assert.Equal(AgentTurnExecutionStatus.Succeeded, beta.ExecutionStatus);
            Assert.Null(beta.ActionResult);
            Assert.Equal(1, beta.FinalObservation.CurrentTick);
            Assert.Equal(["alpha", "beta"], ReadActorIds(beta.FinalObservation.CurrentLocation.PresentActors));
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public async Task RunTurnAsync_WhenPolicyThrows_FallsBackToKeepAndPreservesWorldAsync() {
        string repoDir = CreateTempRepoDir();

        try {
            using var runtime = SerialWorldRuntime.CreateEmpty(repoDir);
            AuthorMiniWorld(runtime);

            var host = new AgentTurnHost(
                runtime,
                [
                    new HostedAgent("alpha", new ThrowingPolicy("agent crashed")),
                ]
            );

            var result = await host.RunTurnAsync();
            var alpha = Assert.Single(result.Actors);

            Assert.Equal(1, result.TurnNumber);
            Assert.Equal(1, result.Time.CurrentTick);
            Assert.IsType<KeepAgentActionIntent>(alpha.Decision.Action);
            Assert.Equal(AgentTurnResolutionStatus.Accepted, alpha.ResolutionStatus);
            Assert.Equal(AgentTurnExecutionStatus.Succeeded, alpha.ExecutionStatus);
            Assert.Null(alpha.ActionResult);
            Assert.NotNull(alpha.Fault);
            Assert.Contains("agent crashed", alpha.Fault!.Message, StringComparison.Ordinal);
            Assert.Equal("start", alpha.InitialObservation.CurrentLocation.LocationId);
            Assert.Equal("start", alpha.FinalObservation.CurrentLocation.LocationId);

            var observedAlpha = runtime.ObserveActor("alpha");
            Assert.Equal("start", observedAlpha.Location.LocationId);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public async Task RunTurnAsync_WithNoRegisteredAgents_DoesNotAdvanceTimeAsync() {
        string repoDir = CreateTempRepoDir();

        try {
            using var runtime = SerialWorldRuntime.CreateEmpty(repoDir);
            var host = new AgentTurnHost(runtime, []);

            var result = await host.RunTurnAsync();

            Assert.Equal(0, result.TurnNumber);
            Assert.Equal(0, result.Time.CurrentTick);
            Assert.Empty(result.Actors);
            Assert.Equal(0, host.CompletedTurnCount);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public async Task RunTurnAsync_StartFollowRouteIntent_StartsProcessAndAdvancesItAsync() {
        string repoDir = CreateTempRepoDir();

        try {
            using var runtime = SerialWorldRuntime.CreateEmpty(repoDir);
            AuthorThreeNodeWorld(runtime);

            var host = new AgentTurnHost(
                runtime,
                [
                    new HostedAgent("runner", new FixedDecisionPolicy(
                        new AgentTurnDecision(new StartRouteFollowingAgentActionIntent("goal"))
                    )),
                ]
            );

            var result = await host.RunTurnAsync();
            var runner = Assert.Single(result.Actors);

            Assert.Equal(1, result.Time.CurrentTick);
            Assert.Equal("start", runner.InitialObservation.CurrentLocation.LocationId);
            Assert.Equal(AgentTurnResolutionStatus.Accepted, runner.ResolutionStatus);
            Assert.Equal(AgentTurnExecutionStatus.Succeeded, runner.ExecutionStatus);
            Assert.NotNull(runner.ActionResult);
            Assert.Equal("start-follow-route", runner.ActionResult!.Kind);
            Assert.Null(runner.ActionResult.Move);
            Assert.Equal("route-following", runner.ActionResult.ActivityAfterAction!.Kind);
            Assert.Equal("goal", runner.ActionResult.ActivityAfterAction.RouteFollowing!.DestinationLocationId);
            Assert.Equal("mid", runner.FinalObservation.CurrentLocation.LocationId);
            Assert.Equal("route-following", runner.FinalObservation.CurrentActivity.Kind);
            Assert.Equal(["mid-goal"], runner.FinalObservation.CurrentActivity.RouteFollowing!.RemainingPassageIds);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public async Task RunTurnAsync_StartMiningIntent_StartsProcessAndProducesYieldAsync() {
        string repoDir = CreateTempRepoDir();

        try {
            using var runtime = SerialWorldRuntime.CreateEmpty(repoDir);
            _ = runtime.CreateLocation("mine", "Mine", "Ore seam.");
            _ = runtime.ConfigureLocationMiningWorksite("mine", ticksPerYield: 1, yieldItemId: "iron-ore", yieldAmount: 2);
            _ = runtime.CreateActor("miner", "Miner", "mine");

            var host = new AgentTurnHost(
                runtime,
                [
                    new HostedAgent("miner", new FixedDecisionPolicy(
                        new AgentTurnDecision(new StartMiningAgentActionIntent("mine"))
                    )),
                ]
            );

            var result = await host.RunTurnAsync();
            var miner = Assert.Single(result.Actors);

            Assert.Equal(1, result.Time.CurrentTick);
            Assert.Equal(AgentTurnResolutionStatus.Accepted, miner.ResolutionStatus);
            Assert.Equal(AgentTurnExecutionStatus.Succeeded, miner.ExecutionStatus);
            Assert.NotNull(miner.ActionResult);
            Assert.Equal("start-mining", miner.ActionResult!.Kind);
            Assert.Equal("mining", miner.ActionResult.ActivityAfterAction!.Kind);
            Assert.Equal("mine", miner.ActionResult.ActivityAfterAction.Mining!.WorksiteId);
            Assert.Equal("mining", miner.FinalObservation.CurrentActivity.Kind);
            Assert.Equal(["iron-ore"], miner.FinalObservation.CarriedResources.Select(static x => x.ItemId).ToArray());
            Assert.Equal([2L], miner.FinalObservation.CarriedResources.Select(static x => x.Quantity).ToArray());
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public async Task RunTurnAsync_CancelCurrentProcessIntent_StopsExistingEmbodiedProcessAsync() {
        string repoDir = CreateTempRepoDir();

        try {
            using var runtime = SerialWorldRuntime.CreateEmpty(repoDir);
            AuthorThreeNodeWorld(runtime);
            _ = runtime.StartActorRouteFollowing("runner", "goal");

            var host = new AgentTurnHost(
                runtime,
                [
                    new HostedAgent("runner", new FixedDecisionPolicy(
                        new AgentTurnDecision(CancelCurrentProcessAgentActionIntent.Instance)
                    )),
                ]
            );

            var result = await host.RunTurnAsync();
            var runner = Assert.Single(result.Actors);

            Assert.Equal(1, result.Time.CurrentTick);
            Assert.Equal(AgentTurnResolutionStatus.Accepted, runner.ResolutionStatus);
            Assert.Equal(AgentTurnExecutionStatus.Succeeded, runner.ExecutionStatus);
            Assert.NotNull(runner.ActionResult);
            Assert.Equal("cancel-current-process", runner.ActionResult!.Kind);
            Assert.Equal("idle", runner.ActionResult.ActivityAfterAction!.Kind);
            Assert.Equal("start", runner.FinalObservation.CurrentLocation.LocationId);
            Assert.Equal("idle", runner.FinalObservation.CurrentActivity.Kind);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public async Task RunTurnAsync_WhenResolverRejectsUnsupportedIntent_ReportsRejectedWithoutExecutionAsync() {
        string repoDir = CreateTempRepoDir();

        try {
            using var runtime = SerialWorldRuntime.CreateEmpty(repoDir);
            AuthorMiniWorld(runtime);

            var host = new AgentTurnHost(
                runtime,
                [
                    new HostedAgent("alpha", new FixedDecisionPolicy(
                        new AgentTurnDecision(new UnsupportedAgentActionIntent())
                    )),
                ]
            );

            var result = await host.RunTurnAsync();
            var alpha = Assert.Single(result.Actors);

            Assert.Equal(1, result.Time.CurrentTick);
            Assert.Equal(AgentTurnResolutionStatus.Rejected, alpha.ResolutionStatus);
            Assert.Equal(AgentTurnExecutionStatus.NotExecuted, alpha.ExecutionStatus);
            Assert.Contains("Unsupported agent action intent", alpha.ResolutionMessage, StringComparison.Ordinal);
            Assert.Null(alpha.ExecutionMessage);
            Assert.Null(alpha.ActionResult);
            Assert.Equal("start", alpha.FinalObservation.CurrentLocation.LocationId);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public async Task RunTurnAsync_WhenAcceptedOperationFailsDuringExecution_PreservesAcceptanceAndReportsExecutionFailureAsync() {
        string repoDir = CreateTempRepoDir();

        try {
            using var runtime = SerialWorldRuntime.CreateEmpty(repoDir);
            AuthorMiniWorld(runtime);

            var host = new AgentTurnHost(
                runtime,
                [
                    new HostedAgent("alpha", new FixedDecisionPolicy(
                        new AgentTurnDecision(new MoveAgentActionIntent("missing-passage"))
                    )),
                ]
            );

            var result = await host.RunTurnAsync();
            var alpha = Assert.Single(result.Actors);

            Assert.Equal(1, result.Time.CurrentTick);
            Assert.Equal(AgentTurnResolutionStatus.Accepted, alpha.ResolutionStatus);
            Assert.Equal(AgentTurnExecutionStatus.Failed, alpha.ExecutionStatus);
            Assert.Null(alpha.ResolutionMessage);
            Assert.Contains("missing-passage", alpha.ExecutionMessage, StringComparison.Ordinal);
            Assert.Null(alpha.ActionResult);
            Assert.Equal("start", alpha.FinalObservation.CurrentLocation.LocationId);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    private static void AuthorMiniWorld(SerialWorldRuntime runtime) {
        ArgumentNullException.ThrowIfNull(runtime);

        _ = runtime.CreateLocation("start", "Start", "Shared-world start.");
        _ = runtime.CreateLocation("goal", "Goal", "Shared-world goal.");
        _ = runtime.CreateActor("alpha", "Alpha", "start");
        _ = runtime.CreateActor("beta", "Beta", "goal");
        _ = runtime.CreatePassage("start-goal", "start", "advance", "goal", "return", TravelMode.Land, 1);
    }

    private static void AuthorThreeNodeWorld(SerialWorldRuntime runtime) {
        ArgumentNullException.ThrowIfNull(runtime);

        _ = runtime.CreateLocation("start", "Start", "Start.");
        _ = runtime.CreateLocation("mid", "Mid", "Mid.");
        _ = runtime.CreateLocation("goal", "Goal", "Goal.");
        _ = runtime.CreateActor("runner", "Runner", "start");
        _ = runtime.CreatePassage("start-mid", "start", "advance", "mid", "return", TravelMode.Land, 1);
        _ = runtime.CreatePassage("mid-goal", "mid", "advance", "goal", "return", TravelMode.Land, 1);
    }

    private static string[] ReadActorIds(IEnumerable<ActorPresenceObservation> presentActors)
        => presentActors
            .Select(static actor => actor.ActorId)
            .OrderBy(static actorId => actorId, StringComparer.Ordinal)
            .ToArray();

    private static string CreateTempRepoDir()
        => Path.Combine(Path.GetTempPath(), $"atelia-textadv2-agent-turn-tests-{Guid.NewGuid():N}");

    private static void DeleteDirectoryIfExists(string repoDir) {
        if (Directory.Exists(repoDir)) {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    private sealed class FixedDecisionPolicy(AgentTurnDecision decision) : IAgentTurnPolicy {
        public ValueTask<AgentTurnDecision> DecideAsync(AgentTurnInput input, CancellationToken ct = default) {
            ArgumentNullException.ThrowIfNull(input);
            return ValueTask.FromResult(decision);
        }
    }

    private sealed class ThrowingPolicy(string message) : IAgentTurnPolicy {
        public ValueTask<AgentTurnDecision> DecideAsync(AgentTurnInput input, CancellationToken ct = default) {
            ArgumentNullException.ThrowIfNull(input);
            throw new InvalidOperationException(message);
        }
    }

    private sealed record UnsupportedAgentActionIntent : AgentActionIntent;
}
