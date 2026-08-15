using Atelia.StateJournal;
using Atelia.TextAdv2.DevSupport;
using Atelia.TextAdv2.Observation;
using Atelia.TextAdv2.Routing;
using Atelia.TextAdv2.Spatial;
using Atelia.TextAdv2.WorldTruth;
using Xunit;

namespace Atelia.TextAdv2.Tests;

public class NavigationAndCliWorkflowTests {
    [Fact]
    public void ProjectLocationNavigationGraph_ExposesCanonicalGraphSeam() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = TestWorldBuilder.Create(revision);

            var spatial = WorldSpatialSnapshotBuilder.Build(world);
            var graph = LocationNavigationGraph.FromAdjacency(
                spatial.Locations[TestWorldBuilder.LocationIds.Square]
            );

            Assert.Equal(TestWorldBuilder.LocationIds.Square, graph.LocationId);
            Assert.Collection(
                graph.Edges,
                edge => {
                    Assert.Equal(TestWorldBuilder.PassageIds.SquareRidgeTrail, edge.PassageId);
                    Assert.Equal(TestWorldBuilder.LocationIds.Ridge, edge.TargetLocationId);
                    Assert.Equal(TravelMode.Land, edge.TravelMode);
                    Assert.Equal(5, edge.TravelCost);
                },
                edge => {
                    Assert.Equal(TestWorldBuilder.PassageIds.SquareShrineGate, edge.PassageId);
                    Assert.Equal(TestWorldBuilder.LocationIds.Shrine, edge.TargetLocationId);
                    Assert.Equal(TravelMode.Portal, edge.TravelMode);
                    Assert.Equal(1, edge.TravelCost);
                },
                edge => {
                    Assert.Equal(TestWorldBuilder.PassageIds.VillageSquareRoad, edge.PassageId);
                    Assert.Equal(TestWorldBuilder.LocationIds.Village, edge.TargetLocationId);
                    Assert.Equal(TravelMode.Land, edge.TravelMode);
                    Assert.Equal(1, edge.TravelCost);
                }
            );
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

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

    [Fact]
    public void DevTextRenderer_RendersCompactMovementText() {
        using var session = SampleWorldBootstrap.CreateTemporarySession();

        var movement = session.MoveActor(
            TestWorldBuilder.ActorIds.Scout,
            TestWorldBuilder.PassageIds.SquareRidgeTrail
        );
        string text = DevTextRenderer.RenderCompactMovement(movement);

        Assert.Equal(
            "scout: square --north gate/square-ridge-trail--> ridge | land | cost=5",
            text
        );
    }

    [Fact]
    public void DevTextRenderer_RendersRouteTraceText() {
        using var session = SampleWorldBootstrap.CreateTemporarySession();

        _ = session.MoveActor(
            TestWorldBuilder.ActorIds.Scout,
            TestWorldBuilder.PassageIds.SquareRidgeTrail
        );
        _ = session.MoveActor(
            TestWorldBuilder.ActorIds.Scout,
            TestWorldBuilder.PassageIds.RidgeAerieWinch
        );

        var trace = session.TraceActorRuntimeRoute(TestWorldBuilder.ActorIds.Scout);
        string text = DevTextRenderer.RenderRuntimeRouteTrace(trace);

        Assert.Equal(
            Normalize(ExpectedScoutRuntimeRouteTrace(trace.RuntimeEpochId)),
            Normalize(text)
        );
    }

    [Fact]
    public void DevTextRenderer_RendersIdleRouteTraceText() {
        using var session = SampleWorldBootstrap.CreateTemporarySession();

        var trace = session.TraceActorRuntimeRoute(TestWorldBuilder.ActorIds.Boatman);
        string text = DevTextRenderer.RenderRuntimeRouteTrace(trace);

        Assert.Equal(
            Normalize(ExpectedBoatmanIdleRuntimeRouteTrace(trace.RuntimeEpochId)),
            Normalize(text)
        );
    }

    [Fact]
    public void DevTextRenderer_RenderWorld_BridgesToCanonicalWorldDumpRenderer() {
        using var session = SampleWorldBootstrap.CreateTemporarySession();

        string text = DevTextRenderer.RenderWorld(session);

        Assert.Equal(CreateExpectedSampleWorldDump(), text);
    }

    [Fact]
    public void DevTextRenderer_RenderLocation_BridgesToCanonicalWorldDumpRenderer() {
        using var session = SampleWorldBootstrap.CreateTemporarySession();

        string text = DevTextRenderer.RenderLocation(session, TestWorldBuilder.LocationIds.Square);

        Assert.Equal(CreateExpectedSampleWorldLocationDump(TestWorldBuilder.LocationIds.Square), text);
    }

    private static string CreateTempRepoDir()
        => Path.Combine(Path.GetTempPath(), $"atelia-textadv2-navigation-tests-{Guid.NewGuid():N}");

    private static void DeleteDirectoryIfExists(string repoDir) {
        if (Directory.Exists(repoDir)) {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    private static string CreateExpectedSampleWorldDump() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = TestWorldBuilder.Create(revision);
            TestWorldBuilder.PopulateSampleActors(world);
            return WorldDumpRenderer.Render(world);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    private static string CreateExpectedSampleWorldLocationDump(string locationId) {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = TestWorldBuilder.Create(revision);
            TestWorldBuilder.PopulateSampleActors(world);
            return WorldDumpRenderer.RenderLocation(world, locationId);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    private static string ExpectedScoutRuntimeRouteTrace(string runtimeEpochId) => $$"""
RUNTIME ROUTE TRACE epoch={{runtimeEpochId}} actor=scout name=Scout
start=square (Square)
1. square --north gate/square-ridge-trail--> ridge | land | cost=5
2. ridge --cliff lift/ridge-aerie-winch--> aerie | air | cost=5
end=aerie (Aerie) | steps=2 | totalCost=10
""";

    private static string ExpectedBoatmanIdleRuntimeRouteTrace(string runtimeEpochId) => $$"""
RUNTIME ROUTE TRACE epoch={{runtimeEpochId}} actor=boatman name=Boatman
start=harbor (Harbor)
<no movement in this runtime>
end=harbor (Harbor) | steps=0 | totalCost=0
""";
}
