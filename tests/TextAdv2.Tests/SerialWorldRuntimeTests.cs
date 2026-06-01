using Atelia.TextAdv2.Runtime;
using Atelia.TextAdv2.Observation;
using Atelia.TextAdv2.WorldTruth;
using Atelia.TextAdv2.DevSupport;
using Xunit;

namespace Atelia.TextAdv2.Tests;

public class SerialWorldRuntimeTests {
    [Fact]
    public void ObserveLocation_ReturnsObservationShape() {
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
    public void ObserveActor_ReturnsObservationShape() {
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
    public void ObserveActorContext_UsesNarrowLocationPayloadAndAvailableMovesAsCanonicalActionSurface() {
        using var session = SampleWorldBootstrap.CreateTemporarySession();

        var result = session.ObserveActorContext(TestWorldBuilder.ActorIds.Scout);

        Assert.Equal(TestWorldBuilder.ActorIds.Scout, result.ActorId);
        Assert.Equal("Scout", result.ActorName);
        Assert.Equal(0, result.CurrentTick);
        Assert.Equal(TestWorldBuilder.LocationIds.Square, result.CurrentLocation.LocationId);
        Assert.Equal("Square", result.CurrentLocation.LocationName);
        Assert.Equal("Central junction used to verify multiple outgoing passages.", result.CurrentLocation.LocationDescription);
        Assert.Contains(
            result.CurrentLocation.PresentActors,
            static actor => actor.ActorId == TestWorldBuilder.ActorIds.Scout
        );
        Assert.Equal(
            ["square-ridge-trail", "square-shrine-gate", "village-square-road"],
            result.AvailableMoves.Select(static edge => edge.PassageId).ToArray()
        );
        Assert.Null(typeof(ActorContextLocationObservation).GetProperty(nameof(LocationObservation.Exits)));
    }

    [Fact]
    public void ObserveNavigation_ReturnsObservationNavigationShape() {
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
    public void PlanRoute_ReturnsObservationRoutePlan() {
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
    public void MoveActor_ReturnsTypedMoveResultWithObservationLocationShape() {
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
    public void SessionAuthoringSeam_CanCreateLocationsActorAndPassageWithoutLeakingWorldTruthTypes() {
        string repoDir = CreateTempRepoDir();

        try {
            using var session = CreateEmptySession(repoDir);

            var village = session.CreateLocation("village", "Village", "A quiet riverside village.");
            var square = session.CreateLocation("square", "Square", "A stone square for shared activity.");
            var actor = session.CreateActor("runner", "Runner", village.LocationId);
            var passage = session.CreatePassage(
                "village-square-road",
                village.LocationId,
                "east",
                square.LocationId,
                "west",
                TravelMode.Land,
                2
            );
            var observedVillage = session.ObserveLocation(village.LocationId);
            var observedActor = session.ObserveActor(actor.ActorId);

            Assert.Equal("village", village.LocationId);
            Assert.Equal("Village", village.LocationName);
            Assert.Equal("A quiet riverside village.", village.LocationDescription);

            Assert.Equal("runner", actor.ActorId);
            Assert.Equal("Runner", actor.ActorName);
            Assert.Equal(village.LocationId, actor.CurrentLocationId);

            Assert.Equal("village-square-road", passage.PassageId);
            Assert.Equal(village.LocationId, passage.EndpointA.LocationId);
            Assert.Equal("east", passage.EndpointA.ExitName);
            Assert.Equal(square.LocationId, passage.EndpointB.LocationId);
            Assert.Equal("west", passage.EndpointB.ExitName);
            Assert.Equal(TravelMode.Land, passage.TravelMode);
            Assert.Equal(2, passage.BaseTravelCost);
            Assert.True(passage.FromAToB.IsEnabled);
            Assert.True(passage.FromBToA.IsEnabled);

            Assert.Equal(village.LocationId, observedVillage.LocationId);
            Assert.Contains(observedVillage.PresentActors, static presence => presence.ActorId == "runner");
            Assert.Contains(
                observedVillage.Exits,
                static exit => exit.PassageId == "village-square-road"
                    && exit.TargetLocationId == "square"
                    && exit.TravelMode == TravelMode.Land
                    && exit.TotalTravelCost == 2
            );

            Assert.Equal(actor.ActorId, observedActor.ActorId);
            Assert.Equal(village.LocationId, observedActor.Location.LocationId);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void SessionAuthoringSeam_ObserveLocation_IncludesBothActorsWhenTheyShareTheSameLocation() {
        string repoDir = CreateTempRepoDir();

        try {
            using var session = CreateEmptySession(repoDir);
            AuthorMiniWorld(session, alphaStartLocationId: "start", betaStartLocationId: "start");

            var observation = session.ObserveLocation("start");

            Assert.Equal("start", observation.LocationId);
            Assert.Equal(["alpha", "beta"], ReadActorIds(observation.PresentActors));
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void SessionAuthoringSeam_MovingActorIntoOccupiedLocation_UpdatesLocationAndActorObservations() {
        string repoDir = CreateTempRepoDir();

        try {
            using var session = CreateEmptySession(repoDir);
            AuthorMiniWorld(session, alphaStartLocationId: "start", betaStartLocationId: "goal");

            var move = session.MoveActor("alpha", "start-goal");
            var sourceObservation = session.ObserveLocation("start");
            var goalObservation = session.ObserveLocation("goal");
            var betaObservation = session.ObserveActor("beta");

            Assert.Equal("alpha", move.ActorId);
            Assert.Equal("goal", move.ToLocationId);
            Assert.Equal("goal", move.CurrentLocation.LocationId);
            Assert.Equal(["alpha", "beta"], ReadActorIds(move.CurrentLocation.PresentActors));
            Assert.Empty(sourceObservation.PresentActors);
            Assert.Equal(["alpha", "beta"], ReadActorIds(goalObservation.PresentActors));
            Assert.Equal("goal", betaObservation.Location.LocationId);
            Assert.Equal(["alpha", "beta"], ReadActorIds(betaObservation.Location.PresentActors));
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void CreateEmpty_CreatesDurableEmptyWorldWithZeroLogicalTime() {
        string repoDir = CreateTempRepoDir();

        try {
            using (var session = SerialWorldRuntime.CreateEmpty(repoDir)) {
                var time = session.ObserveTime();
                var acceleration = session.ObserveRouteAcceleration();

                Assert.Equal(0, time.CurrentTick);
                Assert.Equal(0, acceleration.LocationCount);
                Assert.Equal(0, acceleration.PassageCount);
                Assert.Equal("inactive", acceleration.SnapshotStatus);
                Assert.Equal("zero", acceleration.PlannerMode);
            }

            using var reopened = SerialWorldRuntime.OpenExisting(repoDir);
            var reopenedTime = reopened.ObserveTime();
            var reopenedAcceleration = reopened.ObserveRouteAcceleration();

            Assert.Equal(0, reopenedTime.CurrentTick);
            Assert.Equal(0, reopenedAcceleration.LocationCount);
            Assert.Equal(0, reopenedAcceleration.PassageCount);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void SessionAuthoringSeam_SetPassageMutationsReturnUpdatedTypedSnapshotAndAffectObservation() {
        string repoDir = CreateTempRepoDir();

        try {
            using var session = CreateEmptySession(repoDir);

            var start = session.CreateLocation("start", "Start", "Start of the route.");
            var goal = session.CreateLocation("goal", "Goal", "Goal of the route.");
            _ = session.CreatePassage("start-goal", start.LocationId, "go", goal.LocationId, "back", TravelMode.Land, 2);

            _ = session.SetPassageTravelMode("start-goal", TravelMode.Water);
            _ = session.SetPassageBaseTravelCost("start-goal", 4);
            _ = session.SetPassageSharedConditionNote("start-goal", "Water level is high.");
            _ = session.SetPassageEndpointLocalViewNote("start-goal", start.LocationId, "A rope ferry waits here.");
            _ = session.SetPassageDirectionTravelCostModifierFrom("start-goal", start.LocationId, 3);
            _ = session.SetPassageDirectionConditionNoteFrom("start-goal", start.LocationId, "Current flows against the boat.");
            var updated = session.SetPassageDirectionEnabledFrom("start-goal", start.LocationId, false);

            var observedStart = session.ObserveLocation(start.LocationId);
            var observedGoal = session.ObserveLocation(goal.LocationId);

            Assert.Equal(TravelMode.Water, updated.TravelMode);
            Assert.Equal(4, updated.BaseTravelCost);
            Assert.Equal("Water level is high.", updated.SharedConditionNote);
            Assert.Equal("A rope ferry waits here.", updated.EndpointA.LocalViewNote);
            Assert.False(updated.FromAToB.IsEnabled);
            Assert.Equal(3, updated.FromAToB.TravelCostModifier);
            Assert.Equal("Current flows against the boat.", updated.FromAToB.DirectionConditionNote);
            Assert.True(updated.FromBToA.IsEnabled);
            Assert.Equal(0, updated.FromBToA.TravelCostModifier);

            var outboundExit = Assert.Single(observedStart.Exits);
            Assert.Equal("start-goal", outboundExit.PassageId);
            Assert.Equal(TravelMode.Water, outboundExit.TravelMode);
            Assert.Equal(4, outboundExit.BaseTravelCost);
            Assert.Equal(3, outboundExit.TravelCostModifier);
            Assert.Equal(7, outboundExit.TotalTravelCost);
            Assert.Equal("Water level is high.", outboundExit.SharedConditionNote);
            Assert.Equal("Current flows against the boat.", outboundExit.DirectionalConditionNote);
            Assert.Equal("A rope ferry waits here.", outboundExit.LocalViewNote);
            Assert.False(outboundExit.IsEnabled);

            var returnExit = Assert.Single(observedGoal.Exits);
            Assert.True(returnExit.IsEnabled);
            Assert.Equal(4, returnExit.TotalTravelCost);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void SessionAuthoringSeam_PersistsCreatedWorldAcrossReopen() {
        string repoDir = CreateTempRepoDir();

        try {
            using (var session = CreateEmptySession(repoDir)) {
                var start = session.CreateLocation("start", "Start", "Start.");
                var goal = session.CreateLocation("goal", "Goal", "Goal.");
                _ = session.CreateActor("runner", "Runner", start.LocationId);
                _ = session.CreatePassage("start-goal", start.LocationId, "go", goal.LocationId, "back", TravelMode.Land, 1);
                _ = session.AdvanceTime(5);
            }

            using var reopened = SerialWorldRuntime.OpenExisting(repoDir);
            var observedActor = reopened.ObserveActor("runner");
            var observedStart = reopened.ObserveLocation("start");
            var observedTime = reopened.ObserveTime();

            Assert.Equal("runner", observedActor.ActorId);
            Assert.Equal("start", observedActor.Location.LocationId);
            Assert.Contains(observedStart.Exits, static exit => exit.PassageId == "start-goal" && exit.TargetLocationId == "goal");
            Assert.Equal(5, observedTime.CurrentTick);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void SessionAuthoringSeam_MarksRouteAccelerationSnapshotStaleAfterTopologyChange() {
        string repoDir = CreateTempRepoDir();

        try {
            using var session = SampleWorldBootstrap.OpenOrCreateSession(repoDir);
            var rebuilt = session.RebuildRouteAcceleration(
                $"{TestWorldBuilder.LocationIds.Aerie},{TestWorldBuilder.LocationIds.Shrine}"
            );

            _ = session.CreateLocation("detour", "Detour", "A newly authored detour.");
            var updated = session.CreatePassage(
                "square-detour",
                TestWorldBuilder.LocationIds.Square,
                "side trail",
                "detour",
                "back to square",
                TravelMode.Land,
                2
            );
            var observed = session.ObserveRouteAcceleration();

            Assert.Equal("active", rebuilt.SnapshotStatus);
            Assert.Equal("square-detour", updated.PassageId);
            Assert.Equal("stale", observed.SnapshotStatus);
            Assert.Equal("zero", observed.PlannerMode);
            Assert.Equal("landmark", observed.SnapshotKind);
            Assert.Equal("custom", observed.LandmarkProfileName);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void SessionAuthoringSeam_FailedMutation_DoesNotCommitPartialWorldState() {
        string repoDir = CreateTempRepoDir();

        try {
            using (var session = CreateEmptySession(repoDir)) {
                var start = session.CreateLocation("start", "Start", "Start.");
                var goal = session.CreateLocation("goal", "Goal", "Goal.");
                var other = session.CreateLocation("other", "Other", "Other.");
                _ = session.CreatePassage("start-goal", start.LocationId, "go", goal.LocationId, "back", TravelMode.Land, 1);

                var exception = Assert.Throws<InvalidOperationException>(
                    () => session.CreatePassage("start-other", start.LocationId, "go", other.LocationId, "other way", TravelMode.Land, 1)
                );

                Assert.Contains("Location 'start' already uses exit name 'go'", exception.Message, StringComparison.Ordinal);
            }

            using var reopened = SerialWorldRuntime.OpenExisting(repoDir);
            var observedStart = reopened.ObserveLocation("start");

            var exit = Assert.Single(observedStart.Exits);
            Assert.Equal("start-goal", exit.PassageId);
            Assert.Equal("goal", exit.TargetLocationId);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void OpenOrCreateSession_WithOnlyLegacySessionSidecar_ThrowsInsteadOfSilentlyRecreatingSampleWorld() {
        string repoDir = CreateTempRepoDir();
        string legacySidecarPath = Path.Combine(repoDir, ".textadv2-runtime-state.json");

        try {
            Directory.CreateDirectory(repoDir);
            File.WriteAllText(
                legacySidecarPath,
                """
                {
                  "SchemaVersion": 1,
                  "CurrentTick": 99,
                  "MovementHistoryByActor": {}
                }
                """
            );

            var exception = Assert.Throws<InvalidOperationException>(() => SampleWorldBootstrap.OpenOrCreateSession(repoDir));

            Assert.Contains("StateJournal repository", exception.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(legacySidecarPath));
            Assert.Equal([legacySidecarPath], Directory.EnumerateFileSystemEntries(repoDir).ToArray());
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

            using var session = SerialWorldRuntime.OpenExisting(repoDir);
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

    private static SerialWorldRuntime CreateEmptySession(string repoDir)
        => SerialWorldRuntime.CreateEmpty(repoDir);

    private static void AuthorMiniWorld(
        SerialWorldRuntime session,
        string alphaStartLocationId,
        string betaStartLocationId
    ) {
        ArgumentNullException.ThrowIfNull(session);

        _ = session.CreateLocation("start", "Start", "Shared-world start.");
        _ = session.CreateLocation("goal", "Goal", "Shared-world goal.");
        _ = session.CreateActor("alpha", "Alpha", alphaStartLocationId);
        _ = session.CreateActor("beta", "Beta", betaStartLocationId);
        _ = session.CreatePassage("start-goal", "start", "advance", "goal", "return", TravelMode.Land, 1);
    }

    private static string[] ReadActorIds(IEnumerable<ActorPresenceObservation> presentActors)
        => presentActors
            .Select(static actor => actor.ActorId)
            .OrderBy(static actorId => actorId, StringComparer.Ordinal)
            .ToArray();

    private static void DeleteDirectoryIfExists(string repoDir) {
        if (Directory.Exists(repoDir)) {
            Directory.Delete(repoDir, recursive: true);
        }
    }
}
