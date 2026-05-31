using Atelia.StateJournal;
using Atelia.TextAdv2.ReadOnlyView;
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

    private static string CreateTempRepoDir()
        => Path.Combine(Path.GetTempPath(), $"atelia-textadv2-actor-tests-{Guid.NewGuid():N}");

    private static void DeleteDirectoryIfExists(string repoDir) {
        if (Directory.Exists(repoDir)) {
            Directory.Delete(repoDir, recursive: true);
        }
    }
}
