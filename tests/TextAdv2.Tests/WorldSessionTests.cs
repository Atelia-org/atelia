using Atelia.TextAdv2.Session;
using Atelia.TextAdv2.ReadOnlyView;
using Atelia.TextAdv2.WorldTruth;
using Atelia.TextAdv2.DevSupport;
using Xunit;

namespace Atelia.TextAdv2.Tests;

public class WorldSessionTests {
    [Fact]
    public void ObserveLocation_ReturnsReadOnlyViewObservation() {
        using var session = SampleWorldBootstrap.CreateTemporarySession();

        var result = session.ObserveLocation(TestWorldBuilder.LocationIds.Square);

        Assert.Equal(TestWorldBuilder.LocationIds.Square, result.LocationId);
        Assert.Equal("Square", result.LocationName);
        Assert.Contains(
            result.Exits,
            static exit => exit.PassageId == TestWorldBuilder.PassageIds.SquareShrineGate && exit.TravelMode == TravelMode.Portal
        );
        Assert.Contains(result.PresentActors, static actor => actor.ActorId == TestWorldBuilder.ActorIds.Scout);
    }

    [Fact]
    public void ObserveActor_ReturnsReadOnlyViewObservation() {
        using var session = SampleWorldBootstrap.CreateTemporarySession();

        var result = session.ObserveActor(TestWorldBuilder.ActorIds.Scout);

        Assert.Equal(TestWorldBuilder.ActorIds.Scout, result.ActorId);
        Assert.Equal("Scout", result.ActorName);
        Assert.Equal(TestWorldBuilder.LocationIds.Square, result.Location.LocationId);
        Assert.Contains(
            result.Location.Exits,
            static exit => exit.PassageId == TestWorldBuilder.PassageIds.SquareRidgeTrail && exit.TravelMode == TravelMode.Land
        );
    }

    [Fact]
    public void ObserveNavigation_ReturnsReadOnlyViewNavigationObservation() {
        using var session = SampleWorldBootstrap.CreateTemporarySession();

        var result = session.ObserveNavigation(TestWorldBuilder.LocationIds.Square);

        Assert.Equal(TestWorldBuilder.LocationIds.Square, result.LocationId);
        Assert.Equal("Square", result.LocationName);
        Assert.Collection(
            result.Edges,
            edge => {
                Assert.Equal(TestWorldBuilder.PassageIds.SquareRidgeTrail, edge.PassageId);
                Assert.Equal("north gate", edge.ExitName);
                Assert.Equal(TestWorldBuilder.LocationIds.Ridge, edge.TargetLocationId);
                Assert.Equal(TravelMode.Land, edge.TravelMode);
                Assert.Equal(5, edge.TravelCost);
            },
            edge => {
                Assert.Equal(TestWorldBuilder.PassageIds.SquareShrineGate, edge.PassageId);
                Assert.Equal("old arch", edge.ExitName);
                Assert.Equal(TestWorldBuilder.LocationIds.Shrine, edge.TargetLocationId);
                Assert.Equal(TravelMode.Portal, edge.TravelMode);
                Assert.Equal(1, edge.TravelCost);
            },
            edge => {
                Assert.Equal(TestWorldBuilder.PassageIds.VillageSquareRoad, edge.PassageId);
                Assert.Equal("west", edge.ExitName);
                Assert.Equal(TestWorldBuilder.LocationIds.Village, edge.TargetLocationId);
                Assert.Equal(TravelMode.Land, edge.TravelMode);
                Assert.Equal(1, edge.TravelCost);
            }
        );
    }

    [Fact]
    public void ObserveActorNavigation_ReturnsTypedNavigationAtActorsCurrentLocation() {
        using var session = SampleWorldBootstrap.CreateTemporarySession();
        _ = session.MoveActor(
            TestWorldBuilder.ActorIds.Scout,
            TestWorldBuilder.PassageIds.SquareRidgeTrail
        );

        var result = session.ObserveActorNavigation(TestWorldBuilder.ActorIds.Scout);

        Assert.Equal(TestWorldBuilder.ActorIds.Scout, result.ActorId);
        Assert.Equal("Scout", result.ActorName);
        Assert.Equal(TestWorldBuilder.LocationIds.Ridge, result.Navigation.LocationId);
        Assert.Collection(
            result.Navigation.Edges,
            edge => {
                Assert.Equal(TestWorldBuilder.PassageIds.RidgeAerieWinch, edge.PassageId);
                Assert.Equal("cliff lift", edge.ExitName);
                Assert.Equal(TestWorldBuilder.LocationIds.Aerie, edge.TargetLocationId);
                Assert.Equal(TravelMode.Air, edge.TravelMode);
                Assert.Equal(5, edge.TravelCost);
            },
            edge => {
                Assert.Equal(TestWorldBuilder.PassageIds.SquareRidgeTrail, edge.PassageId);
                Assert.Equal("downhill trail", edge.ExitName);
                Assert.Equal(TestWorldBuilder.LocationIds.Square, edge.TargetLocationId);
                Assert.Equal(TravelMode.Land, edge.TravelMode);
                Assert.Equal(2, edge.TravelCost);
            }
        );
    }

    [Fact]
    public void PlanRoute_ReturnsReadOnlyViewRoutePlan() {
        using var session = SampleWorldBootstrap.CreateTemporarySession();

        var result = session.PlanRoute(
            TestWorldBuilder.LocationIds.Village,
            TestWorldBuilder.LocationIds.Aerie
        );

        Assert.Equal(TestWorldBuilder.LocationIds.Village, result.FromLocationId);
        Assert.Equal("Village", result.FromLocationName);
        Assert.Equal(TestWorldBuilder.LocationIds.Aerie, result.ToLocationId);
        Assert.Equal("Aerie", result.ToLocationName);
        Assert.Equal(RoutePlanStatus.Found, result.Status);
        Assert.Equal(3, result.StepCount);
        Assert.Equal(11, result.TotalTravelCost);
        Assert.Collection(
            result.Steps,
            step => {
                Assert.Equal(1, step.StepNumber);
                Assert.Equal(TestWorldBuilder.PassageIds.VillageSquareRoad, step.PassageId);
                Assert.Equal("east", step.ExitName);
                Assert.Equal(TestWorldBuilder.LocationIds.Village, step.FromLocationId);
                Assert.Equal(TestWorldBuilder.LocationIds.Square, step.ToLocationId);
                Assert.Equal(TravelMode.Land, step.TravelMode);
                Assert.Equal(1, step.TravelCost);
                Assert.Equal(1, step.CumulativeTravelCost);
            },
            step => {
                Assert.Equal(2, step.StepNumber);
                Assert.Equal(TestWorldBuilder.PassageIds.SquareRidgeTrail, step.PassageId);
                Assert.Equal("north gate", step.ExitName);
                Assert.Equal(TestWorldBuilder.LocationIds.Square, step.FromLocationId);
                Assert.Equal(TestWorldBuilder.LocationIds.Ridge, step.ToLocationId);
                Assert.Equal(TravelMode.Land, step.TravelMode);
                Assert.Equal(5, step.TravelCost);
                Assert.Equal(6, step.CumulativeTravelCost);
            },
            step => {
                Assert.Equal(3, step.StepNumber);
                Assert.Equal(TestWorldBuilder.PassageIds.RidgeAerieWinch, step.PassageId);
                Assert.Equal("cliff lift", step.ExitName);
                Assert.Equal(TestWorldBuilder.LocationIds.Ridge, step.FromLocationId);
                Assert.Equal(TestWorldBuilder.LocationIds.Aerie, step.ToLocationId);
                Assert.Equal(TravelMode.Air, step.TravelMode);
                Assert.Equal(5, step.TravelCost);
                Assert.Equal(11, step.CumulativeTravelCost);
            }
        );
        Assert.Equal("zero", result.SearchStats.HeuristicName);
        Assert.Equal(0, result.SearchStats.LandmarkCount);
        Assert.True(result.SearchStats.ExpandedNodeCount > 0);
    }

    [Fact]
    public void PlanActorRoute_StartsFromActorsCurrentLocation() {
        using var session = SampleWorldBootstrap.CreateTemporarySession();
        _ = session.MoveActor(
            TestWorldBuilder.ActorIds.Scout,
            TestWorldBuilder.PassageIds.SquareRidgeTrail
        );

        var result = session.PlanActorRoute(
            TestWorldBuilder.ActorIds.Scout,
            TestWorldBuilder.LocationIds.Aerie
        );

        Assert.Equal(TestWorldBuilder.LocationIds.Ridge, result.FromLocationId);
        Assert.Equal(TestWorldBuilder.LocationIds.Aerie, result.ToLocationId);
        Assert.Equal(RoutePlanStatus.Found, result.Status);
        Assert.Single(result.Steps);
        Assert.Equal(TestWorldBuilder.PassageIds.RidgeAerieWinch, result.Steps[0].PassageId);
        Assert.Equal(TravelMode.Air, result.Steps[0].TravelMode);
        Assert.Equal(5, result.TotalTravelCost);
    }

    [Fact]
    public void PlanActorRoute_ProjectsAlreadyThereAndUnreachableStatesThroughTypedSeam() {
        using var session = SampleWorldBootstrap.CreateTemporarySession();

        var alreadyThere = session.PlanActorRoute(
            TestWorldBuilder.ActorIds.Scout,
            TestWorldBuilder.LocationIds.Square
        );
        var unreachable = session.PlanActorRoute(
            TestWorldBuilder.ActorIds.Boatman,
            TestWorldBuilder.LocationIds.Village
        );

        Assert.Equal(RoutePlanStatus.AlreadyThere, alreadyThere.Status);
        Assert.Equal(0, alreadyThere.StepCount);
        Assert.Equal(0, alreadyThere.TotalTravelCost);
        Assert.Empty(alreadyThere.Steps);

        Assert.Equal(RoutePlanStatus.Unreachable, unreachable.Status);
        Assert.Equal(0, unreachable.StepCount);
        Assert.Null(unreachable.TotalTravelCost);
        Assert.Empty(unreachable.Steps);
    }

    [Fact]
    public void PlanRoute_ProjectsAlreadyThereAndUnreachableStatesThroughTypedSeam() {
        using var session = SampleWorldBootstrap.CreateTemporarySession();

        var alreadyThere = session.PlanRoute(
            TestWorldBuilder.LocationIds.Shrine,
            TestWorldBuilder.LocationIds.Shrine
        );
        var unreachable = session.PlanRoute(
            TestWorldBuilder.LocationIds.Delta,
            TestWorldBuilder.LocationIds.Harbor
        );

        Assert.Equal(RoutePlanStatus.AlreadyThere, alreadyThere.Status);
        Assert.Equal(0, alreadyThere.StepCount);
        Assert.Equal(0, alreadyThere.TotalTravelCost);
        Assert.Empty(alreadyThere.Steps);
        Assert.Equal("zero", alreadyThere.SearchStats.HeuristicName);

        Assert.Equal(RoutePlanStatus.Unreachable, unreachable.Status);
        Assert.Equal(0, unreachable.StepCount);
        Assert.Null(unreachable.TotalTravelCost);
        Assert.Empty(unreachable.Steps);
        Assert.Equal("zero", unreachable.SearchStats.HeuristicName);
    }

    [Fact]
    public void MoveActorThenTraceActorRoute_UsesTypedSessionMovementHistory() {
        string repoDir = CreateTempRepoDir();

        try {
            using var session = SampleWorldBootstrap.OpenOrCreateSession(repoDir);

            var move = session.MoveActor(
                TestWorldBuilder.ActorIds.Scout,
                TestWorldBuilder.PassageIds.SquareRidgeTrail
            );
            var trace = session.TraceActorRoute(TestWorldBuilder.ActorIds.Scout);

            Assert.Equal(TestWorldBuilder.ActorIds.Scout, move.ActorId);
            Assert.Equal(TestWorldBuilder.LocationIds.Square, move.FromLocationId);
            Assert.Equal(TestWorldBuilder.LocationIds.Ridge, move.ToLocationId);
            Assert.Equal(TravelMode.Land, move.TravelMode);

            Assert.Equal(TestWorldBuilder.ActorIds.Scout, trace.ActorId);
            Assert.Equal("Scout", trace.ActorName);
            Assert.Equal(TestWorldBuilder.LocationIds.Square, trace.StartLocationId);
            Assert.Equal("Square", trace.StartLocationName);
            Assert.Equal(TestWorldBuilder.LocationIds.Ridge, trace.EndLocationId);
            Assert.Equal("Ridge", trace.EndLocationName);
            Assert.Equal(1, trace.StepCount);
            Assert.Equal(5, trace.TotalTravelCost);
            Assert.Collection(
                trace.Steps,
                step => {
                    Assert.Equal(1, step.StepNumber);
                    Assert.Equal(TestWorldBuilder.PassageIds.SquareRidgeTrail, step.PassageId);
                    Assert.Equal("north gate", step.ExitName);
                    Assert.Equal(TestWorldBuilder.LocationIds.Square, step.FromLocationId);
                    Assert.Equal("Square", step.FromLocationName);
                    Assert.Equal(TestWorldBuilder.LocationIds.Ridge, step.ToLocationId);
                    Assert.Equal("Ridge", step.ToLocationName);
                    Assert.Equal("land", step.TravelMode);
                    Assert.Equal(5, step.TravelCost);
                }
            );
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void OpenOrCreateSampleWorld_ReopensCommittedActorLocation() {
        string repoDir = CreateTempRepoDir();

        try {
            using (var session = SampleWorldBootstrap.OpenOrCreateSession(repoDir)) {
                var move = session.MoveActor(
                    TestWorldBuilder.ActorIds.Scout,
                    TestWorldBuilder.PassageIds.SquareRidgeTrail
                );

                Assert.Equal(TestWorldBuilder.ActorIds.Scout, move.ActorId);
                Assert.Equal(TestWorldBuilder.PassageIds.SquareRidgeTrail, move.PassageId);
                Assert.Equal(TestWorldBuilder.LocationIds.Square, move.FromLocationId);
                Assert.Equal(TestWorldBuilder.LocationIds.Ridge, move.ToLocationId);
                Assert.Equal(TravelMode.Land, move.TravelMode);
                Assert.Equal(TestWorldBuilder.LocationIds.Ridge, move.CurrentLocation.LocationId);
            }

            using var reopened = SampleWorldBootstrap.OpenOrCreateSession(repoDir);
            var observed = reopened.ObserveActor(TestWorldBuilder.ActorIds.Scout);

            Assert.Equal(TestWorldBuilder.ActorIds.Scout, observed.ActorId);
            Assert.Equal(TestWorldBuilder.LocationIds.Ridge, observed.Location.LocationId);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void MoveActor_ReturnsTypedMoveResultWithReadOnlyViewLocationShape() {
        using var session = SampleWorldBootstrap.CreateTemporarySession();

        var move = session.MoveActor(
            TestWorldBuilder.ActorIds.Scout,
            TestWorldBuilder.PassageIds.SquareRidgeTrail
        );

        Assert.Equal(TestWorldBuilder.ActorIds.Scout, move.ActorId);
        Assert.Equal("Scout", move.ActorName);
        Assert.Equal(TestWorldBuilder.PassageIds.SquareRidgeTrail, move.PassageId);
        Assert.Equal("north gate", move.ExitName);
        Assert.Equal(TestWorldBuilder.LocationIds.Square, move.FromLocationId);
        Assert.Equal("Square", move.FromLocationName);
        Assert.Equal(TestWorldBuilder.LocationIds.Ridge, move.ToLocationId);
        Assert.Equal("Ridge", move.ToLocationName);
        Assert.Equal(TravelMode.Land, move.TravelMode);
        Assert.Equal(5, move.TravelCost);
        Assert.Equal(TestWorldBuilder.LocationIds.Ridge, move.CurrentLocation.LocationId);
        Assert.Contains(
            move.CurrentLocation.Exits,
            static exit => exit.PassageId == TestWorldBuilder.PassageIds.RidgeAerieWinch && exit.TravelMode == TravelMode.Air
        );
    }

    [Fact]
    public void OpenOrCreateSession_WithOnlyLegacySessionSidecar_CreatesFreshSampleWorld() {
        string repoDir = CreateTempRepoDir();

        try {
            Directory.CreateDirectory(repoDir);
            File.WriteAllText(
                Path.Combine(repoDir, ".textadv2-runtime-state.json"),
                """
                {
                  "SchemaVersion": 1,
                  "CurrentTick": 99,
                  "MovementHistoryByActor": {}
                }
                """
            );

            using var session = SampleWorldBootstrap.OpenOrCreateSession(repoDir);
            var observedActor = session.ObserveActor(TestWorldBuilder.ActorIds.Scout);
            var observedTime = session.ObserveTime();

            Assert.Equal(TestWorldBuilder.ActorIds.Scout, observedActor.ActorId);
            Assert.Equal(TestWorldBuilder.LocationIds.Square, observedActor.Location.LocationId);
            Assert.Equal(0, observedTime.CurrentTick);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void AdvanceTimeThenObserveTime_TracksLogicalTickWithinSession() {
        using var session = SampleWorldBootstrap.CreateTemporarySession();

        var advanced = session.AdvanceTime(7);
        var observed = session.ObserveTime();

        Assert.Equal(7, advanced.CurrentTick);
        Assert.Equal(7, observed.CurrentTick);
    }

    [Fact]
    public void AdvanceTime_RejectsNegativeTickDeltaForTypedSeam() {
        using var session = SampleWorldBootstrap.CreateTemporarySession();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => session.AdvanceTime(-1));
        var observed = session.ObserveTime();

        Assert.Equal("ticks", exception.ParamName);
        Assert.Equal(0, observed.CurrentTick);
    }

    [Fact]
    public void OpenOrCreateSampleWorld_ReopensWorldTruthAndDurableTimeButResetsOtherSessionOwnedState() {
        string repoDir = CreateTempRepoDir();

        try {
            using (var session = SampleWorldBootstrap.OpenOrCreateSession(repoDir)) {
                var time = session.AdvanceTime(11);
                var move = session.MoveActor(
                    TestWorldBuilder.ActorIds.Scout,
                    TestWorldBuilder.PassageIds.SquareRidgeTrail
                );

                Assert.Equal(11, time.CurrentTick);
                Assert.Equal(TestWorldBuilder.LocationIds.Square, move.FromLocationId);
                Assert.Equal(TestWorldBuilder.LocationIds.Ridge, move.ToLocationId);
                Assert.Equal(TravelMode.Land, move.TravelMode);
            }

            using var reopened = SampleWorldBootstrap.OpenOrCreateSession(repoDir);
            var timeAfterReopen = reopened.ObserveTime();
            var traceAfterReopen = reopened.TraceActorRoute(TestWorldBuilder.ActorIds.Scout);
            var observedAfterReopen = reopened.ObserveActor(TestWorldBuilder.ActorIds.Scout);

            Assert.Equal(11, timeAfterReopen.CurrentTick);
            Assert.Equal(TestWorldBuilder.LocationIds.Ridge, traceAfterReopen.StartLocationId);
            Assert.Equal("Ridge", traceAfterReopen.StartLocationName);
            Assert.Equal(TestWorldBuilder.LocationIds.Ridge, traceAfterReopen.EndLocationId);
            Assert.Equal("Ridge", traceAfterReopen.EndLocationName);
            Assert.Equal(0, traceAfterReopen.StepCount);
            Assert.Equal(0, traceAfterReopen.TotalTravelCost);
            Assert.Empty(traceAfterReopen.Steps);
            Assert.Equal(TestWorldBuilder.LocationIds.Ridge, observedAfterReopen.Location.LocationId);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void RebuildRouteAcceleration_ActivatesLandmarkModeUntilSessionReopen() {
        string repoDir = CreateTempRepoDir();

        try {
            using (var session = SampleWorldBootstrap.OpenOrCreateSession(repoDir)) {
                var initial = session.ObserveRouteAcceleration();
                var rebuilt = session.RebuildRouteAcceleration(
                    $"{TestWorldBuilder.LocationIds.Aerie},{TestWorldBuilder.LocationIds.Shrine}"
                );
                var planned = session.PlanRoute(
                    TestWorldBuilder.LocationIds.Village,
                    TestWorldBuilder.LocationIds.Aerie
                );

                Assert.Equal("zero", initial.PlannerMode);
                Assert.Equal("inactive", initial.SnapshotStatus);
                Assert.Equal("none", initial.SnapshotKind);
                Assert.Equal("none", initial.LandmarkProfileName);
                Assert.False(initial.IsPersistent);
                Assert.Empty(initial.LandmarkLocationIds);

                Assert.Equal("landmark", rebuilt.PlannerMode);
                Assert.Equal("active", rebuilt.SnapshotStatus);
                Assert.Equal("landmark", rebuilt.SnapshotKind);
                Assert.Equal("custom", rebuilt.LandmarkProfileName);
                Assert.False(rebuilt.IsPersistent);
                Assert.Equal(2, rebuilt.LandmarkCount);
                Assert.Equal(
                    [TestWorldBuilder.LocationIds.Aerie, TestWorldBuilder.LocationIds.Shrine],
                    rebuilt.LandmarkLocationIds
                );
                Assert.Equal(RoutePlanStatus.Found, planned.Status);
                Assert.Equal(11, planned.TotalTravelCost);
                Assert.Equal("landmark", planned.SearchStats.HeuristicName);
                Assert.Equal(2, planned.SearchStats.LandmarkCount);
            }

            using var reopened = SampleWorldBootstrap.OpenOrCreateSession(repoDir);
            var observedAfterReopen = reopened.ObserveRouteAcceleration();

            Assert.Equal("zero", observedAfterReopen.PlannerMode);
            Assert.Equal("inactive", observedAfterReopen.SnapshotStatus);
            Assert.Equal("none", observedAfterReopen.SnapshotKind);
            Assert.Equal("none", observedAfterReopen.LandmarkProfileName);
            Assert.False(observedAfterReopen.IsPersistent);
            Assert.Empty(observedAfterReopen.LandmarkLocationIds);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void RebuildRouteAcceleration_WithoutExplicitLandmarks_UsesRecommendedSampleWorldProfile() {
        string repoDir = CreateTempRepoDir();

        try {
            using var session = SampleWorldBootstrap.OpenOrCreateSession(repoDir);

            var rebuilt = SampleWorldBootstrap.RebuildRouteAcceleration(session);

            Assert.Equal("landmark", rebuilt.PlannerMode);
            Assert.Equal("active", rebuilt.SnapshotStatus);
            Assert.Equal("landmark", rebuilt.SnapshotKind);
            Assert.Equal("sample-world-default", rebuilt.LandmarkProfileName);
            Assert.False(rebuilt.IsPersistent);
            Assert.Equal(3, rebuilt.LandmarkCount);
            Assert.Equal(
                [TestWorldBuilder.LocationIds.Aerie, TestWorldBuilder.LocationIds.Harbor, TestWorldBuilder.LocationIds.Shrine],
                rebuilt.LandmarkLocationIds
            );
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void OpenExisting_CanStillUseRecommendedSampleWorldLandmarksWithoutSessionPolicyInjection() {
        string repoDir = CreateTempRepoDir();

        try {
            using (SampleWorldBootstrap.CreateFreshSession(repoDir)) {
            }

            using var session = WorldSession.OpenExisting(repoDir);
            var rebuilt = SampleWorldBootstrap.RebuildRouteAcceleration(session);

            Assert.Equal("landmark", rebuilt.PlannerMode);
            Assert.Equal("sample-world-default", rebuilt.LandmarkProfileName);
            Assert.Equal(
                [TestWorldBuilder.LocationIds.Aerie, TestWorldBuilder.LocationIds.Harbor, TestWorldBuilder.LocationIds.Shrine],
                rebuilt.LandmarkLocationIds
            );
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    private static string CreateTempRepoDir()
        => Path.Combine(Path.GetTempPath(), $"atelia-textadv2-session-tests-{Guid.NewGuid():N}");

    private static void DeleteDirectoryIfExists(string repoDir) {
        if (Directory.Exists(repoDir)) {
            Directory.Delete(repoDir, recursive: true);
        }
    }
}
