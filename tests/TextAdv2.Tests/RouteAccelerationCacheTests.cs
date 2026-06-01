using Atelia.StateJournal;
using Atelia.TextAdv2.ReadOnlyView;
using Atelia.TextAdv2.Routing;
using Atelia.TextAdv2.Runtime;
using Atelia.TextAdv2.WorldTruth;
using Xunit;

namespace Atelia.TextAdv2.Tests;

public class RouteAccelerationCacheTests {
    [Fact]
    public void Observe_AfterNavigationGraphMutation_ReportsStaleAndFallsBackToZeroHeuristic() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = TestWorldBuilder.Create(revision);
            var routeAcceleration = new RouteAccelerationCache();

            var rebuilt = routeAcceleration.Rebuild(
                world,
                [TestWorldBuilder.LocationIds.Aerie, TestWorldBuilder.LocationIds.Shrine]
            );
            world.SetPassageBaseTravelCost(TestWorldBuilder.PassageIds.SquareRidgeTrail, 9);
            var observedAfterMutation = routeAcceleration.Observe(world);
            var planningOptionsAfterMutation = routeAcceleration.GetPlanningOptions(world);

            Assert.Equal("landmark", rebuilt.PlannerMode);
            Assert.Equal("active", rebuilt.SnapshotStatus);
            Assert.Equal("zero", observedAfterMutation.PlannerMode);
            Assert.Equal("stale", observedAfterMutation.SnapshotStatus);
            Assert.Equal("landmark", observedAfterMutation.SnapshotKind);
            Assert.Equal(2, observedAfterMutation.LandmarkCount);
            Assert.Null(planningOptionsAfterMutation);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void Observe_AfterActorMovement_RemainsActiveBecauseNavigationGraphDidNotChange() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = TestWorldBuilder.Create(revision);
            TestWorldBuilder.PopulateSampleActors(world);
            var routeAcceleration = new RouteAccelerationCache();

            routeAcceleration.Rebuild(
                world,
                [TestWorldBuilder.LocationIds.Aerie, TestWorldBuilder.LocationIds.Shrine]
            );

            world.MoveActorAlongPassage(TestWorldBuilder.ActorIds.Scout, TestWorldBuilder.PassageIds.SquareRidgeTrail);
            var observedAfterMovement = routeAcceleration.Observe(world);
            var planningOptionsAfterMovement = routeAcceleration.GetPlanningOptions(world);

            Assert.Equal("landmark", observedAfterMovement.PlannerMode);
            Assert.Equal("active", observedAfterMovement.SnapshotStatus);
            Assert.NotNull(planningOptionsAfterMovement);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void Observe_AfterDisplayNoteChange_RemainsActiveBecauseGraphSignatureIgnoresDisplayFields() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = TestWorldBuilder.Create(revision);
            var routeAcceleration = new RouteAccelerationCache();

            routeAcceleration.Rebuild(
                world,
                [TestWorldBuilder.LocationIds.Aerie, TestWorldBuilder.LocationIds.Shrine]
            );

            world.SetPassageEndpointLocalViewNote(
                TestWorldBuilder.PassageIds.SquareRidgeTrail,
                TestWorldBuilder.LocationIds.Square,
                "Renamed display hint only."
            );

            var observedAfterNoteChange = routeAcceleration.Observe(world);
            var planningOptionsAfterNoteChange = routeAcceleration.GetPlanningOptions(world);

            Assert.Equal("landmark", observedAfterNoteChange.PlannerMode);
            Assert.Equal("active", observedAfterNoteChange.SnapshotStatus);
            Assert.NotNull(planningOptionsAfterNoteChange);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void Observe_AfterPassageTravelModeChange_ReportsStaleAndDropsPlanningOptions() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = TestWorldBuilder.Create(revision);
            var routeAcceleration = new RouteAccelerationCache();

            routeAcceleration.Rebuild(
                world,
                [TestWorldBuilder.LocationIds.Aerie, TestWorldBuilder.LocationIds.Shrine]
            );

            world.SetPassageTravelMode(TestWorldBuilder.PassageIds.SquareRidgeTrail, TravelMode.Water);
            var observedAfterMutation = routeAcceleration.Observe(world);
            var planningOptionsAfterMutation = routeAcceleration.GetPlanningOptions(world);

            Assert.Equal("stale", observedAfterMutation.SnapshotStatus);
            Assert.Equal("zero", observedAfterMutation.PlannerMode);
            Assert.Null(planningOptionsAfterMutation);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void DisablePassageDirection_ChangesNavigationAndRoutePlanWhileMarkingAccelerationStale() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = TestWorldBuilder.Create(revision);
            var routeAcceleration = new RouteAccelerationCache();

            routeAcceleration.Rebuild(
                world,
                [TestWorldBuilder.LocationIds.Aerie, TestWorldBuilder.LocationIds.Shrine]
            );

            var planBeforeMutation = LocationRoutePlanner.PlanShortestRoute(
                world,
                TestWorldBuilder.LocationIds.Village,
                TestWorldBuilder.LocationIds.Aerie
            );
            world.SetPassageDirectionEnabledFrom(
                TestWorldBuilder.PassageIds.SquareRidgeTrail,
                TestWorldBuilder.LocationIds.Square,
                false
            );

            var navigationAfterMutation = NavigationObservationProjector.ObserveLocationNavigation(
                world,
                TestWorldBuilder.LocationIds.Square
            );
            var planAfterMutation = LocationRoutePlanner.PlanShortestRoute(
                world,
                TestWorldBuilder.LocationIds.Village,
                TestWorldBuilder.LocationIds.Aerie
            );
            var accelerationAfterMutation = routeAcceleration.Observe(world);

            Assert.Equal(RoutePlanStatus.Found, planBeforeMutation.Status);
            Assert.Equal(11, planBeforeMutation.TotalTravelCost);
            Assert.DoesNotContain(
                navigationAfterMutation.Edges,
                static edge => edge.PassageId == TestWorldBuilder.PassageIds.SquareRidgeTrail
            );
            Assert.Equal(RoutePlanStatus.Unreachable, planAfterMutation.Status);
            Assert.Equal("stale", accelerationAfterMutation.SnapshotStatus);
            Assert.Equal("zero", accelerationAfterMutation.PlannerMode);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    private static string CreateTempRepoDir()
        => Path.Combine(Path.GetTempPath(), $"atelia-textadv2-route-acceleration-tests-{Guid.NewGuid():N}");

    private static void DeleteDirectoryIfExists(string repoDir) {
        if (Directory.Exists(repoDir)) {
            Directory.Delete(repoDir, recursive: true);
        }
    }
}
