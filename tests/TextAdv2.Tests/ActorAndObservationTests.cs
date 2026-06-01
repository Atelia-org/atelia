using Atelia.StateJournal;
using Atelia.TextAdv2.Observation;
using Atelia.TextAdv2.Spatial;
using Atelia.TextAdv2.WorldTruth;
using Xunit;

namespace Atelia.TextAdv2.Tests;

public class ActorAndObservationTests {
    [Fact]
    public void ObserveLocation_ProjectsStableExitFieldsAndPresentActors() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = TestWorldBuilder.Create(revision);
            TestWorldBuilder.PopulateSampleActors(world);

            var observation = LocationObservationProjector.ObserveLocation(world, TestWorldBuilder.LocationIds.Square);

            Assert.Equal(TestWorldBuilder.LocationIds.Square, observation.LocationId);
            Assert.Equal("Square", observation.LocationName);
            Assert.Collection(
                observation.Exits,
                exit => {
                    Assert.Equal("north gate", exit.ExitName);
                    Assert.Equal(TestWorldBuilder.PassageIds.SquareRidgeTrail, exit.PassageId);
                    Assert.Equal(TestWorldBuilder.LocationIds.Ridge, exit.TargetLocationId);
                    Assert.Equal(TravelMode.Land, exit.TravelMode);
                    Assert.Equal(3, exit.BaseTravelCost);
                    Assert.Equal(2, exit.TravelCostModifier);
                    Assert.Equal(5, exit.TotalTravelCost);
                    Assert.True(exit.IsEnabled);
                },
                exit => {
                    Assert.Equal("old arch", exit.ExitName);
                    Assert.Equal(TestWorldBuilder.PassageIds.SquareShrineGate, exit.PassageId);
                },
                exit => {
                    Assert.Equal("west", exit.ExitName);
                    Assert.Equal(TestWorldBuilder.PassageIds.VillageSquareRoad, exit.PassageId);
                }
            );
            Assert.Collection(
                observation.PresentActors,
                actor => {
                    Assert.Equal(TestWorldBuilder.ActorIds.Scout, actor.ActorId);
                    Assert.Equal("Scout", actor.ActorName);
                }
            );
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void ObserveLocation_WithSharedSpatialSnapshot_MatchesConvenienceWrapper() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = TestWorldBuilder.Create(revision);
            TestWorldBuilder.PopulateSampleActors(world);
            var spatial = WorldSpatialSnapshotBuilder.Build(world);

            var observedViaWrapper = LocationObservationProjector.ObserveLocation(world, TestWorldBuilder.LocationIds.Square);
            var observedViaSharedSpatial = LocationObservationProjector.ObserveLocation(
                world,
                spatial,
                TestWorldBuilder.LocationIds.Square
            );

            Assert.Equal(observedViaWrapper.LocationId, observedViaSharedSpatial.LocationId);
            Assert.Equal(observedViaWrapper.LocationName, observedViaSharedSpatial.LocationName);
            Assert.Equal(observedViaWrapper.LocationDescription, observedViaSharedSpatial.LocationDescription);
            Assert.Equal(
                observedViaWrapper.Exits.Select(static exit => exit.PassageId),
                observedViaSharedSpatial.Exits.Select(static exit => exit.PassageId)
            );
            Assert.Equal(
                observedViaWrapper.Exits.Select(static exit => exit.ExitName),
                observedViaSharedSpatial.Exits.Select(static exit => exit.ExitName)
            );
            Assert.Equal(
                observedViaWrapper.PresentActors.Select(static actor => actor.ActorId),
                observedViaSharedSpatial.PresentActors.Select(static actor => actor.ActorId)
            );
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void MoveActorAlongPassage_ReturnsAuthoritativeReceiptAndUpdatesActorLocationAndObservation() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = TestWorldBuilder.Create(revision);
            TestWorldBuilder.PopulateSampleActors(world);

            var receipt = world.MoveActorAlongPassage(
                TestWorldBuilder.ActorIds.Scout,
                TestWorldBuilder.PassageIds.SquareRidgeTrail
            );
            var observation = LocationObservationProjector.ObserveActorLocation(world, TestWorldBuilder.ActorIds.Scout);

            Assert.Equal(TestWorldBuilder.ActorIds.Scout, receipt.ActorId);
            Assert.Equal(TestWorldBuilder.PassageIds.SquareRidgeTrail, receipt.PassageId);
            Assert.Equal("north gate", receipt.ExitName);
            Assert.Equal(TestWorldBuilder.LocationIds.Square, receipt.FromLocationId);
            Assert.Equal(TestWorldBuilder.LocationIds.Ridge, receipt.ToLocationId);
            Assert.Equal(TravelMode.Land, receipt.TravelMode);
            Assert.Equal(5, receipt.TravelCost);
            Assert.Equal(TestWorldBuilder.LocationIds.Ridge, world.GetActor(TestWorldBuilder.ActorIds.Scout).CurrentLocationId);
            Assert.Equal(TestWorldBuilder.LocationIds.Ridge, observation.Location.LocationId);
            Assert.Contains(
                observation.Location.Exits,
                exit => exit.PassageId == TestWorldBuilder.PassageIds.SquareRidgeTrail
                    && exit.TargetLocationId == TestWorldBuilder.LocationIds.Square
                    && exit.TravelCostModifier == -1
                    && exit.TotalTravelCost == 2
            );
            Assert.Collection(
                observation.Location.PresentActors,
                actor => Assert.Equal(TestWorldBuilder.ActorIds.Scout, actor.ActorId)
            );
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void AuthoritativeTotalTravelCost_StaysInSyncAcrossReceiptObservationNavigationAndDump() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = TestWorldBuilder.Create(revision);
            TestWorldBuilder.PopulateSampleActors(world);

            var passage = world.GetPassage(TestWorldBuilder.PassageIds.SquareRidgeTrail);
            int authoritativeTotal = passage.GetTotalTravelCostFrom(TestWorldBuilder.LocationIds.Square);

            var receipt = world.MoveActorAlongPassage(
                TestWorldBuilder.ActorIds.Scout,
                TestWorldBuilder.PassageIds.SquareRidgeTrail
            );
            var observation = LocationObservationProjector.ObserveLocation(world, TestWorldBuilder.LocationIds.Square);
            var navigation = NavigationObservationProjector.ObserveLocationNavigation(world, TestWorldBuilder.LocationIds.Square);
            string dump = WorldDumpRenderer.RenderLocation(world, TestWorldBuilder.LocationIds.Square);

            Assert.Equal(5, authoritativeTotal);
            Assert.Equal(authoritativeTotal, receipt.TravelCost);
            Assert.Contains(
                observation.Exits,
                exit => exit.PassageId == TestWorldBuilder.PassageIds.SquareRidgeTrail
                    && exit.TotalTravelCost == authoritativeTotal
            );
            Assert.Contains(
                navigation.Edges,
                edge => edge.PassageId == TestWorldBuilder.PassageIds.SquareRidgeTrail
                    && edge.TravelCost == authoritativeTotal
            );
            Assert.Contains(
                "passage=square-ridge-trail | mode=land | base=3 | modifier=2 | total=5 | enabled=true",
                dump,
                StringComparison.Ordinal
            );
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void MoveActorAlongPassage_RejectsDisabledDirection() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = TestWorldBuilder.Create(revision);
            var actor = world.CreateActor("delta-runner", "Delta Runner", TestWorldBuilder.LocationIds.Delta);

            var exception = Assert.Throws<InvalidOperationException>(
                () => world.MoveActorAlongPassage(actor.Id, TestWorldBuilder.PassageIds.HarborDeltaCurrent)
            );

            Assert.Contains("not traversable", exception.Message, StringComparison.Ordinal);
            Assert.Equal(TestWorldBuilder.LocationIds.Delta, world.GetActor(actor.Id).CurrentLocationId);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void MoveActorAlongPassage_RejectsPassageThatDoesNotTouchActorsCurrentLocation() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = TestWorldBuilder.Create(revision);
            TestWorldBuilder.PopulateSampleActors(world);

            var exception = Assert.Throws<InvalidOperationException>(
                () => world.MoveActorAlongPassage(
                    TestWorldBuilder.ActorIds.Scout,
                    TestWorldBuilder.PassageIds.HarborDeltaCurrent
                )
            );

            Assert.Contains("not connected by passage", exception.Message, StringComparison.Ordinal);
            Assert.Equal(TestWorldBuilder.LocationIds.Square, world.GetActor(TestWorldBuilder.ActorIds.Scout).CurrentLocationId);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void ObserveLocation_WithCoLocatedActors_ProjectsBothPresentActors() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = WorldState.Create(revision);
            _ = AuthorMiniWorld(world, alphaStartLocationId: "start", betaStartLocationId: "start");

            var observation = LocationObservationProjector.ObserveLocation(world, "start");

            Assert.Equal("start", observation.LocationId);
            Assert.Equal(["alpha", "beta"], ReadActorIds(observation.PresentActors));
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void ObserveActorAndLocation_AfterMove_ProjectsSharedPresenceAtDestinationAndRemovesActorFromSource() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = WorldState.Create(revision);
            _ = AuthorMiniWorld(world, alphaStartLocationId: "start", betaStartLocationId: "goal");

            _ = world.MoveActorAlongPassage("alpha", "start-goal");
            var sourceObservation = LocationObservationProjector.ObserveLocation(world, "start");
            var goalObservation = LocationObservationProjector.ObserveLocation(world, "goal");
            var betaObservation = LocationObservationProjector.ObserveActorLocation(world, "beta");

            Assert.Empty(sourceObservation.PresentActors);
            Assert.Equal(["alpha", "beta"], ReadActorIds(goalObservation.PresentActors));
            Assert.Equal("goal", betaObservation.Location.LocationId);
            Assert.Equal(["alpha", "beta"], ReadActorIds(betaObservation.Location.PresentActors));
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    private static (string StartId, string GoalId, string AlphaId, string BetaId, string PassageId) AuthorMiniWorld(
        WorldState world,
        string alphaStartLocationId,
        string betaStartLocationId
    ) {
        ArgumentNullException.ThrowIfNull(world);

        _ = world.CreateLocation("start", "Start", "Shared-world start.");
        _ = world.CreateLocation("goal", "Goal", "Shared-world goal.");
        _ = world.CreateActor("alpha", "Alpha", alphaStartLocationId);
        _ = world.CreateActor("beta", "Beta", betaStartLocationId);
        _ = world.CreatePassage("start-goal", "start", "advance", "goal", "return", TravelMode.Land, 1);

        return ("start", "goal", "alpha", "beta", "start-goal");
    }

    private static string[] ReadActorIds(IEnumerable<ActorPresenceObservation> presentActors)
        => presentActors
            .Select(static actor => actor.ActorId)
            .OrderBy(static actorId => actorId, StringComparer.Ordinal)
            .ToArray();

    private static string CreateTempRepoDir()
        => Path.Combine(Path.GetTempPath(), $"atelia-textadv2-actor-tests-{Guid.NewGuid():N}");

    private static void DeleteDirectoryIfExists(string repoDir) {
        if (Directory.Exists(repoDir)) {
            Directory.Delete(repoDir, recursive: true);
        }
    }
}
