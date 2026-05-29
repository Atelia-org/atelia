using Atelia.StateJournal;
using Atelia.TextAdv2.ReadOnlyView;
using Atelia.TextAdv2.WorldTruth;
using Xunit;

namespace Atelia.TextAdv2.Tests;

public class NavigationAndCliWorkflowTests {
    [Fact]
    public void ObserveLocationNavigation_UsesOnlyEnabledEdges() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = TestWorldBuilder.Create(revision);

            var deltaNavigation = NavigationObservationProjector.ObserveLocationNavigation(
                world,
                TestWorldBuilder.LocationIds.Delta
            );
            var squareNavigation = NavigationObservationProjector.ObserveLocationNavigation(
                world,
                TestWorldBuilder.LocationIds.Square
            );

            Assert.Empty(deltaNavigation.Edges);
            Assert.Collection(
                squareNavigation.Edges,
                edge => {
                    Assert.Equal(TestWorldBuilder.PassageIds.SquareRidgeTrail, edge.PassageId);
                    Assert.Equal("north gate", edge.ExitName);
                    Assert.Equal(TestWorldBuilder.LocationIds.Ridge, edge.TargetLocationId);
                    Assert.Equal(5, edge.TravelCost);
                },
                edge => {
                    Assert.Equal(TestWorldBuilder.PassageIds.SquareShrineGate, edge.PassageId);
                    Assert.Equal("old arch", edge.ExitName);
                    Assert.Equal(TestWorldBuilder.LocationIds.Shrine, edge.TargetLocationId);
                    Assert.Equal(1, edge.TravelCost);
                },
                edge => {
                    Assert.Equal(TestWorldBuilder.PassageIds.VillageSquareRoad, edge.PassageId);
                    Assert.Equal("west", edge.ExitName);
                    Assert.Equal(TestWorldBuilder.LocationIds.Village, edge.TargetLocationId);
                    Assert.Equal(1, edge.TravelCost);
                }
            );
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void ObserveActorNavigation_TracksCurrentActorLocationAfterMovement() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = TestWorldBuilder.Create(revision);
            TestWorldBuilder.PopulateSampleActors(world);

            world.MoveActorAlongPassage(
                TestWorldBuilder.ActorIds.Scout,
                TestWorldBuilder.PassageIds.SquareRidgeTrail
            );

            var navigation = NavigationObservationProjector.ObserveActorNavigation(
                world,
                TestWorldBuilder.ActorIds.Scout
            );

            Assert.Equal(TestWorldBuilder.ActorIds.Scout, navigation.ActorId);
            Assert.Equal(TestWorldBuilder.LocationIds.Ridge, navigation.Navigation.LocationId);
            Assert.Collection(
                navigation.Navigation.Edges,
                edge => {
                    Assert.Equal(TestWorldBuilder.PassageIds.RidgeAerieWinch, edge.PassageId);
                    Assert.Equal("cliff lift", edge.ExitName);
                    Assert.Equal(TestWorldBuilder.LocationIds.Aerie, edge.TargetLocationId);
                    Assert.Equal(5, edge.TravelCost);
                },
                edge => {
                    Assert.Equal(TestWorldBuilder.PassageIds.SquareRidgeTrail, edge.PassageId);
                    Assert.Equal("downhill trail", edge.ExitName);
                    Assert.Equal(TestWorldBuilder.LocationIds.Square, edge.TargetLocationId);
                    Assert.Equal(2, edge.TravelCost);
                }
            );
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    private static string CreateTempRepoDir()
        => Path.Combine(Path.GetTempPath(), $"atelia-textadv2-navigation-tests-{Guid.NewGuid():N}");

    private static void DeleteDirectoryIfExists(string repoDir) {
        if (Directory.Exists(repoDir)) {
            Directory.Delete(repoDir, recursive: true);
        }
    }
}
