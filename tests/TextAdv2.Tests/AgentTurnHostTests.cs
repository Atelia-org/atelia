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
            Assert.NotNull(alpha.MoveResult);
            Assert.Equal("goal", alpha.MoveResult!.ToLocationId);
            Assert.Equal(1, alpha.FinalObservation.CurrentTick);

            var beta = actorsById["beta"];
            Assert.Equal("goal", beta.InitialObservation.CurrentLocation.LocationId);
            Assert.Equal("goal", beta.FinalObservation.CurrentLocation.LocationId);
            Assert.Equal(AgentTurnResolutionStatus.Accepted, beta.ResolutionStatus);
            Assert.Null(beta.MoveResult);
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
            Assert.Null(alpha.MoveResult);
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

    private static void AuthorMiniWorld(SerialWorldRuntime runtime) {
        ArgumentNullException.ThrowIfNull(runtime);

        _ = runtime.CreateLocation("start", "Start", "Shared-world start.");
        _ = runtime.CreateLocation("goal", "Goal", "Shared-world goal.");
        _ = runtime.CreateActor("alpha", "Alpha", "start");
        _ = runtime.CreateActor("beta", "Beta", "goal");
        _ = runtime.CreatePassage("start-goal", "start", "advance", "goal", "return", TravelMode.Land, 1);
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
}
