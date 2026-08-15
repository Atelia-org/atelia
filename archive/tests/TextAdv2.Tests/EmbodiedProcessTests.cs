using Atelia.StateJournal;
using Atelia.TextAdv2.Gym;
using Atelia.TextAdv2.Observation;
using Atelia.TextAdv2.Runtime;
using Atelia.TextAdv2.WorldTruth;
using Xunit;

namespace Atelia.TextAdv2.Tests;

public class EmbodiedProcessTests {
    [Fact]
    public void AdvanceLogicalTimeWithReport_RouteFollowingAutoMovesAcrossTicks() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = WorldState.Create(revision);
            AuthorThreeNodeWorld(world);

            _ = world.StartActorRouteFollowing("runner", "goal", ["start-mid", "mid-goal"]);

            var firstTick = world.AdvanceLogicalTimeWithReport(1);
            var afterFirstTick = world.GetActor("runner");
            var routeAfterFirstTick = Assert.IsType<RouteFollowingActorProcessState>(afterFirstTick.EmbodiedState);

            Assert.Equal(1, firstTick.CurrentTick);
            Assert.Single(firstTick.MovementReceipts);
            Assert.Equal("mid", firstTick.MovementReceipts[0].ToLocationId);
            Assert.Equal("mid", afterFirstTick.CurrentLocationId);
            Assert.Equal(["mid-goal"], routeAfterFirstTick.RemainingPassageIds);
            Assert.Equal(1, routeAfterFirstTick.RemainingTravelTicksOnCurrentLeg);

            var secondTick = world.AdvanceLogicalTimeWithReport(1);
            var afterSecondTick = world.GetActor("runner");

            Assert.Equal(2, secondTick.CurrentTick);
            Assert.Single(secondTick.MovementReceipts);
            Assert.Equal("goal", secondTick.MovementReceipts[0].ToLocationId);
            Assert.Equal("goal", afterSecondTick.CurrentLocationId);
            Assert.IsType<IdleActorEmbodiedState>(afterSecondTick.EmbodiedState);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void AdvanceLogicalTimeWithReport_MiningProducesCarriedResources() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = WorldState.Create(revision);

            _ = world.CreateLocation("mine", "Mine", "Ore seam.");
            _ = world.ConfigureLocationMiningWorksite("mine", ticksPerYield: 2, yieldItemId: "iron-ore", yieldAmount: 3);
            _ = world.CreateActor("miner", "Miner", "mine");
            _ = world.StartActorMining("miner", "mine");

            var firstTick = world.AdvanceLogicalTimeWithReport(1);
            var actorAfterFirstTick = world.GetActor("miner");
            var miningAfterFirstTick = Assert.IsType<MiningActorProcessState>(actorAfterFirstTick.EmbodiedState);

            Assert.Empty(firstTick.MovementReceipts);
            Assert.Equal(0, actorAfterFirstTick.GetCarriedResourceCount("iron-ore"));
            Assert.Equal(1, miningAfterFirstTick.ProgressTicksInCurrentCycle);
            Assert.Equal(0, miningAfterFirstTick.ProducedYieldCount);

            _ = world.AdvanceLogicalTimeWithReport(1);
            var actorAfterSecondTick = world.GetActor("miner");
            var miningAfterSecondTick = Assert.IsType<MiningActorProcessState>(actorAfterSecondTick.EmbodiedState);

            Assert.Equal(3, actorAfterSecondTick.GetCarriedResourceCount("iron-ore"));
            Assert.Equal(0, miningAfterSecondTick.ProgressTicksInCurrentCycle);
            Assert.Equal(3, miningAfterSecondTick.ProducedYieldCount);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void StartActorRouteFollowingThenAdvanceTime_UpdatesObservationsAndRuntimeRouteTrace() {
        string repoDir = CreateTempRepoDir();

        try {
            using var session = SerialWorldRuntime.CreateEmpty(repoDir);
            AuthorThreeNodeWorld(session);

            _ = session.StartActorRouteFollowing("runner", "goal");
            var contextBeforeAdvance = session.ObserveActorContext("runner");
            var time = session.AdvanceTime(2);
            var observedActor = session.ObserveActor("runner");
            var observedGoal = session.ObserveLocation("goal");
            var contextAfterAdvance = session.ObserveActorContext("runner");
            var trace = session.TraceActorRuntimeRoute("runner");

            Assert.Equal("route-following", contextBeforeAdvance.CurrentActivity.Kind);
            Assert.True(contextBeforeAdvance.CurrentActivity.IsInterruptible);
            Assert.NotNull(contextBeforeAdvance.CurrentActivity.RouteFollowing);
            Assert.Equal("goal", contextBeforeAdvance.CurrentActivity.RouteFollowing!.DestinationLocationId);
            Assert.Equal("Goal", contextBeforeAdvance.CurrentActivity.RouteFollowing.DestinationLocationName);
            Assert.Equal(["start-mid", "mid-goal"], contextBeforeAdvance.CurrentActivity.RouteFollowing.RemainingPassageIds);
            Assert.Equal(1, contextBeforeAdvance.CurrentActivity.RouteFollowing.RemainingTravelTicksOnCurrentLeg);
            Assert.Empty(contextBeforeAdvance.CarriedResources);

            Assert.Equal(2, time.CurrentTick);
            Assert.Equal("goal", observedActor.Location.LocationId);
            Assert.Equal(["runner"], ReadActorIds(observedGoal.PresentActors));
            Assert.Equal("idle", contextAfterAdvance.CurrentActivity.Kind);
            Assert.Empty(contextAfterAdvance.CarriedResources);
            Assert.Equal("start", trace.StartLocationId);
            Assert.Equal("goal", trace.EndLocationId);
            Assert.Equal(2, trace.StepCount);
            Assert.Equal(["start-mid", "mid-goal"], trace.Steps.Select(static step => step.PassageId).ToArray());
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void MoveActor_DuringActiveRouteFollowing_CancelsEmbodiedStateAndStopsFurtherAutoProgress() {
        string repoDir = CreateTempRepoDir();

        try {
            using var session = SerialWorldRuntime.CreateEmpty(repoDir);
            AuthorThreeNodeWorld(session);

            _ = session.StartActorRouteFollowing("runner", "goal");
            var move = session.MoveActor("runner", "start-mid");
            var stateAfterMove = session.DurableWorld.GetActor("runner").EmbodiedState;
            var time = session.AdvanceTime(1);
            var observedActor = session.ObserveActor("runner");

            Assert.Equal("mid", move.ToLocationId);
            Assert.IsType<IdleActorEmbodiedState>(stateAfterMove);
            Assert.Equal(1, time.CurrentTick);
            Assert.Equal("mid", observedActor.Location.LocationId);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void MoveActor_DuringNonInterruptibleRouteFollowing_RejectsManualOverride() {
        string repoDir = CreateTempRepoDir();

        try {
            using var session = SerialWorldRuntime.CreateEmpty(repoDir);
            AuthorThreeNodeWorld(session);

            _ = session.StartActorRouteFollowing("runner", "goal", isInterruptible: false);

            var exception = Assert.Throws<InvalidOperationException>(
                () => session.MoveActor("runner", "start-mid")
            );
            var context = session.ObserveActorContext("runner");

            Assert.Contains("non-interruptible", exception.Message, StringComparison.Ordinal);
            Assert.Equal("start", context.CurrentLocation.LocationId);
            Assert.Equal("route-following", context.CurrentActivity.Kind);
            Assert.False(context.CurrentActivity.IsInterruptible);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void StartActorMiningThenAdvanceTime_ProducesDurableCarriedResources() {
        string repoDir = CreateTempRepoDir();

        try {
            using var session = SerialWorldRuntime.CreateEmpty(repoDir);

            _ = session.CreateLocation("mine", "Mine", "Ore seam.");
            _ = session.ConfigureLocationMiningWorksite("mine", ticksPerYield: 3, yieldItemId: "iron-ore", yieldAmount: 2);
            _ = session.CreateActor("miner", "Miner", "mine");
            _ = session.StartActorMining("miner", "mine");

            var contextBeforeAdvance = session.ObserveActorContext("miner");
            var time = session.AdvanceTime(3);
            var actor = session.DurableWorld.GetActor("miner");
            var state = Assert.IsType<MiningActorProcessState>(actor.EmbodiedState);
            var contextAfterAdvance = session.ObserveActorContext("miner");

            Assert.Equal("mining", contextBeforeAdvance.CurrentActivity.Kind);
            Assert.True(contextBeforeAdvance.CurrentActivity.IsInterruptible);
            Assert.NotNull(contextBeforeAdvance.CurrentActivity.Mining);
            Assert.Equal("mine", contextBeforeAdvance.CurrentActivity.Mining!.WorksiteId);
            Assert.Equal("Mine", contextBeforeAdvance.CurrentActivity.Mining.WorksiteName);
            Assert.Equal("iron-ore", contextBeforeAdvance.CurrentActivity.Mining.YieldItemId);
            Assert.Equal(2, contextBeforeAdvance.CurrentActivity.Mining.YieldAmount);
            Assert.Empty(contextBeforeAdvance.CarriedResources);

            Assert.Equal(3, time.CurrentTick);
            Assert.Equal(2, actor.GetCarriedResourceCount("iron-ore"));
            Assert.Equal(2, state.ProducedYieldCount);
            Assert.Equal(0, state.ProgressTicksInCurrentCycle);
            Assert.Equal("mining", contextAfterAdvance.CurrentActivity.Kind);
            Assert.Equal(2, contextAfterAdvance.CurrentActivity.Mining!.ProducedYieldCount);
            Assert.Equal(0, contextAfterAdvance.CurrentActivity.Mining.ProgressTicksInCurrentCycle);
            Assert.Equal(["iron-ore"], contextAfterAdvance.CarriedResources.Select(static x => x.ItemId).ToArray());
            Assert.Equal([2L], contextAfterAdvance.CarriedResources.Select(static x => x.Quantity).ToArray());
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void CancelActorEmbodiedState_DuringNonInterruptibleMining_RejectsCancellation() {
        string repoDir = CreateTempRepoDir();

        try {
            using var session = SerialWorldRuntime.CreateEmpty(repoDir);

            _ = session.CreateLocation("mine", "Mine", "Ore seam.");
            _ = session.ConfigureLocationMiningWorksite("mine", ticksPerYield: 2, yieldItemId: "iron-ore", yieldAmount: 1);
            _ = session.CreateActor("miner", "Miner", "mine");
            _ = session.StartActorMining("miner", "mine", isInterruptible: false);

            var exception = Assert.Throws<InvalidOperationException>(
                () => session.CancelActorEmbodiedState("miner")
            );
            var context = session.ObserveActorContext("miner");

            Assert.Contains("non-interruptible", exception.Message, StringComparison.Ordinal);
            Assert.Equal("mining", context.CurrentActivity.Kind);
            Assert.False(context.CurrentActivity.IsInterruptible);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void StartActorMining_DuringNonInterruptibleMining_RejectsReplacement() {
        string repoDir = CreateTempRepoDir();

        try {
            using var session = SerialWorldRuntime.CreateEmpty(repoDir);

            _ = session.CreateLocation("mine", "Mine", "Ore seam.");
            _ = session.ConfigureLocationMiningWorksite("mine", ticksPerYield: 3, yieldItemId: "iron-ore", yieldAmount: 1);
            _ = session.CreateActor("miner", "Miner", "mine");
            _ = session.StartActorMining("miner", "mine", isInterruptible: false);

            var exception = Assert.Throws<InvalidOperationException>(
                () => session.StartActorMining("miner", "mine")
            );
            var state = Assert.IsType<MiningActorProcessState>(session.DurableWorld.GetActor("miner").EmbodiedState);

            Assert.Contains("non-interruptible", exception.Message, StringComparison.Ordinal);
            Assert.Equal(3, state.TicksPerYield);
            Assert.Equal(1, state.YieldAmount);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public async Task AgentTurnHost_KeepActionStillAllowsActiveRouteFollowingToProgressAsync() {
        string repoDir = CreateTempRepoDir();

        try {
            using var runtime = SerialWorldRuntime.CreateEmpty(repoDir);
            _ = runtime.CreateLocation("start", "Start", "Shared-world start.");
            _ = runtime.CreateLocation("goal", "Goal", "Shared-world goal.");
            _ = runtime.CreateActor("alpha", "Alpha", "start");
            _ = runtime.CreateActor("beta", "Beta", "goal");
            _ = runtime.CreatePassage("start-goal", "start", "advance", "goal", "return", TravelMode.Land, 1);
            _ = runtime.StartActorRouteFollowing("alpha", "goal");

            var host = new AgentTurnHost(
                runtime,
                [
                    new HostedAgent("alpha", new FixedDecisionPolicy(
                        new AgentTurnDecision(KeepAgentActionIntent.Instance)
                    )),
                    new HostedAgent("beta", new FixedDecisionPolicy(
                        new AgentTurnDecision(KeepAgentActionIntent.Instance)
                    )),
                ]
            );

            var result = await host.RunTurnAsync();
            var actorsById = result.Actors.ToDictionary(static actor => actor.ActorId, StringComparer.Ordinal);

            Assert.Equal(1, result.Time.CurrentTick);

            var alpha = actorsById["alpha"];
            Assert.IsType<KeepAgentActionIntent>(alpha.Decision.Action);
            Assert.Equal("start", alpha.InitialObservation.CurrentLocation.LocationId);
            Assert.Equal("goal", alpha.FinalObservation.CurrentLocation.LocationId);
            Assert.Null(alpha.ActionResult);

            var beta = actorsById["beta"];
            Assert.Equal(["alpha", "beta"], ReadActorIds(beta.FinalObservation.CurrentLocation.PresentActors));
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void StartActorMining_WithoutConfiguredWorksite_RejectsStart() {
        string repoDir = CreateTempRepoDir();

        try {
            using var session = SerialWorldRuntime.CreateEmpty(repoDir);

            _ = session.CreateLocation("mine", "Mine", "Ore seam.");
            _ = session.CreateActor("miner", "Miner", "mine");

            var exception = Assert.Throws<InvalidOperationException>(
                () => session.StartActorMining("miner", "mine")
            );

            Assert.Equal(
                "Location 'mine' is not configured as a mining worksite.",
                exception.Message
            );
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    private static void AuthorThreeNodeWorld(WorldState world) {
        ArgumentNullException.ThrowIfNull(world);

        _ = world.CreateLocation("start", "Start", "Start.");
        _ = world.CreateLocation("mid", "Mid", "Mid.");
        _ = world.CreateLocation("goal", "Goal", "Goal.");
        _ = world.CreateActor("runner", "Runner", "start");
        _ = world.CreatePassage("start-mid", "start", "forward", "mid", "back", TravelMode.Land, 1);
        _ = world.CreatePassage("mid-goal", "mid", "forward", "goal", "back", TravelMode.Land, 1);
    }

    private static void AuthorThreeNodeWorld(SerialWorldRuntime session) {
        ArgumentNullException.ThrowIfNull(session);

        _ = session.CreateLocation("start", "Start", "Start.");
        _ = session.CreateLocation("mid", "Mid", "Mid.");
        _ = session.CreateLocation("goal", "Goal", "Goal.");
        _ = session.CreateActor("runner", "Runner", "start");
        _ = session.CreatePassage("start-mid", "start", "forward", "mid", "back", TravelMode.Land, 1);
        _ = session.CreatePassage("mid-goal", "mid", "forward", "goal", "back", TravelMode.Land, 1);
    }

    private static string[] ReadActorIds(IEnumerable<ActorPresenceObservation> presentActors)
        => presentActors
            .Select(static actor => actor.ActorId)
            .OrderBy(static actorId => actorId, StringComparer.Ordinal)
            .ToArray();

    private static string CreateTempRepoDir()
        => Path.Combine(Path.GetTempPath(), $"atelia-textadv2-embodied-tests-{Guid.NewGuid():N}");

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
}
