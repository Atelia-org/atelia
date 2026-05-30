using Atelia.TextAdv2.Runtime;
using Atelia.TextAdv2.WorldTruth;
using Xunit;

namespace Atelia.TextAdv2.Tests;

public class TextAdv2RuntimeTests {
    [Fact]
    public void ObserveActor_ReturnsIndentedJsonPayload() {
        using var runtime = TextAdv2SampleWorldDevBootstrap.CreateTemporaryRuntime();

        var result = runtime.ObserveActor(TestWorldBuilder.ActorIds.Scout);

        Assert.Equal(TextAdv2RuntimeContentTypes.Json, result.ContentType);
        Assert.Contains("\"ActorId\": \"scout\"", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"LocationId\": \"square\"", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void MoveActorQuietThenTraceActorRoute_UsesRuntimeMovementHistory() {
        string repoDir = CreateTempRepoDir();

        try {
            using var runtime = TextAdv2SampleWorldDevBootstrap.OpenOrCreateRuntime(repoDir);

            var move = runtime.MoveActorQuiet(
                TestWorldBuilder.ActorIds.Scout,
                TestWorldBuilder.PassageIds.SquareRidgeTrail
            );
            var trace = runtime.TraceActorRoute(TestWorldBuilder.ActorIds.Scout);

            Assert.Equal(TextAdv2RuntimeContentTypes.PlainText, move.ContentType);
            Assert.Equal(
                "scout: square --north gate/square-ridge-trail--> ridge | land | cost=5",
                move.Output
            );
            Assert.Equal(TextAdv2RuntimeContentTypes.PlainText, trace.ContentType);
            Assert.Contains("start=square (Square)", trace.Output, StringComparison.Ordinal);
            Assert.Contains(
                "1. square --north gate/square-ridge-trail--> ridge | land | cost=5",
                trace.Output,
                StringComparison.Ordinal
            );
            Assert.Contains("end=ridge (Ridge) | steps=1 | totalCost=5", trace.Output, StringComparison.Ordinal);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void OpenOrCreateSampleWorld_ReopensCommittedActorLocation() {
        string repoDir = CreateTempRepoDir();

        try {
            using (var runtime = TextAdv2SampleWorldDevBootstrap.OpenOrCreateRuntime(repoDir)) {
                var move = runtime.MoveActor(
                    TestWorldBuilder.ActorIds.Scout,
                    TestWorldBuilder.PassageIds.SquareRidgeTrail
                );

                Assert.Equal(TextAdv2RuntimeContentTypes.Json, move.ContentType);
                Assert.Contains("\"ToLocationId\": \"ridge\"", move.Output, StringComparison.Ordinal);
            }

            using var reopened = TextAdv2SampleWorldDevBootstrap.OpenOrCreateRuntime(repoDir);
            var observed = reopened.ObserveActor(TestWorldBuilder.ActorIds.Scout);

            Assert.Contains("\"ActorId\": \"scout\"", observed.Output, StringComparison.Ordinal);
            Assert.Contains("\"LocationId\": \"ridge\"", observed.Output, StringComparison.Ordinal);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void OpenOrCreateRuntime_WithOnlyLegacyRuntimeSidecar_CreatesFreshSampleWorld() {
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

            using var runtime = TextAdv2SampleWorldDevBootstrap.OpenOrCreateRuntime(repoDir);
            var observedActor = runtime.ObserveActor(TestWorldBuilder.ActorIds.Scout);
            var observedTime = runtime.ObserveTime();

            Assert.Contains("\"ActorId\": \"scout\"", observedActor.Output, StringComparison.Ordinal);
            Assert.Contains("\"LocationId\": \"square\"", observedActor.Output, StringComparison.Ordinal);
            Assert.Equal(0, observedTime.CurrentTick);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void AdvanceTimeThenObserveTime_TracksLogicalTickWithinRuntime() {
        using var runtime = TextAdv2SampleWorldDevBootstrap.CreateTemporaryRuntime();

        var advanced = runtime.AdvanceTime(7);
        var observed = runtime.ObserveTime();

        Assert.Equal(7, advanced.CurrentTick);
        Assert.Equal(7, observed.CurrentTick);
    }

    [Fact]
    public void AdvanceTime_RejectsNegativeTickDeltaForTypedSeam() {
        using var runtime = TextAdv2SampleWorldDevBootstrap.CreateTemporaryRuntime();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => runtime.AdvanceTime(-1));
        var observed = runtime.ObserveTime();

        Assert.Equal("ticks", exception.ParamName);
        Assert.Equal(0, observed.CurrentTick);
    }

    [Fact]
    public void OpenOrCreateSampleWorld_ReopensWorldTruthButResetsRuntimeOwnedTimeAndHistory() {
        string repoDir = CreateTempRepoDir();

        try {
            using (var runtime = TextAdv2SampleWorldDevBootstrap.OpenOrCreateRuntime(repoDir)) {
                var time = runtime.AdvanceTime(11);
                var move = runtime.MoveActorQuiet(
                    TestWorldBuilder.ActorIds.Scout,
                    TestWorldBuilder.PassageIds.SquareRidgeTrail
                );

                Assert.Equal(11, time.CurrentTick);
                Assert.Equal(
                    "scout: square --north gate/square-ridge-trail--> ridge | land | cost=5",
                    move.Output
                );
            }

            using var reopened = TextAdv2SampleWorldDevBootstrap.OpenOrCreateRuntime(repoDir);
            var timeAfterReopen = reopened.ObserveTime();
            var traceAfterReopen = reopened.TraceActorRoute(TestWorldBuilder.ActorIds.Scout);
            var observedAfterReopen = reopened.ObserveActor(TestWorldBuilder.ActorIds.Scout);

            Assert.Equal(0, timeAfterReopen.CurrentTick);
            Assert.Contains("start=ridge (Ridge)", traceAfterReopen.Output, StringComparison.Ordinal);
            Assert.Contains("<no movement in this run>", traceAfterReopen.Output, StringComparison.Ordinal);
            Assert.Contains("end=ridge (Ridge) | steps=0 | totalCost=0", traceAfterReopen.Output, StringComparison.Ordinal);
            Assert.Contains("\"LocationId\": \"ridge\"", observedAfterReopen.Output, StringComparison.Ordinal);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void RebuildRouteAcceleration_ActivatesLandmarkModeUntilRuntimeReopen() {
        string repoDir = CreateTempRepoDir();

        try {
            using (var runtime = TextAdv2SampleWorldDevBootstrap.OpenOrCreateRuntime(repoDir)) {
                var initial = runtime.ObserveRouteAcceleration();
                var rebuilt = runtime.RebuildRouteAcceleration(
                    $"{TestWorldBuilder.LocationIds.Aerie},{TestWorldBuilder.LocationIds.Shrine}"
                );
                var planned = runtime.PlanRoute(
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
                Assert.Contains("ROUTE PLAN from=village (Village) to=aerie (Aerie) status=found", planned.Output, StringComparison.Ordinal);
                Assert.Contains("totalCost=11", planned.Output, StringComparison.Ordinal);
            }

            using var reopened = TextAdv2SampleWorldDevBootstrap.OpenOrCreateRuntime(repoDir);
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
            using var runtime = TextAdv2SampleWorldDevBootstrap.OpenOrCreateRuntime(repoDir);

            var rebuilt = runtime.RebuildRouteAcceleration();

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

    private static string CreateTempRepoDir()
        => Path.Combine(Path.GetTempPath(), $"atelia-textadv2-runtime-tests-{Guid.NewGuid():N}");

    private static void DeleteDirectoryIfExists(string repoDir) {
        if (Directory.Exists(repoDir)) {
            Directory.Delete(repoDir, recursive: true);
        }
    }
}
