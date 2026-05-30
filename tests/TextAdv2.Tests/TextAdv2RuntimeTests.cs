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
    public void Execute_ObserveActor_UsesThinTypedAdapter() {
        using var runtime = TextAdv2SampleWorldDevBootstrap.CreateTemporaryRuntime();

        var typed = runtime.ObserveActor(TestWorldBuilder.ActorIds.Scout);
        var viaCommand = runtime.Execute(
            new TextAdv2RuntimeCommand(TextAdv2RuntimeCommandMode.ObserveActor, TestWorldBuilder.ActorIds.Scout)
        );

        Assert.Equal(typed, viaCommand);
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
    public void AdvanceTimeThenObserveTime_TracksLogicalTickWithinRuntime() {
        using var runtime = TextAdv2SampleWorldDevBootstrap.CreateTemporaryRuntime();

        var advanced = runtime.AdvanceTime(7);
        var observed = runtime.ObserveTime();

        Assert.Equal(TextAdv2RuntimeContentTypes.Json, advanced.ContentType);
        Assert.Contains("\"CurrentTick\": 7", advanced.Output, StringComparison.Ordinal);
        Assert.Equal(TextAdv2RuntimeContentTypes.Json, observed.ContentType);
        Assert.Contains("\"CurrentTick\": 7", observed.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenOrCreateSampleWorld_ReopensLogicalTimeAndMovementHistory() {
        string repoDir = CreateTempRepoDir();

        try {
            using (var runtime = TextAdv2SampleWorldDevBootstrap.OpenOrCreateRuntime(repoDir)) {
                var time = runtime.AdvanceTime(11);
                var move = runtime.MoveActorQuiet(
                    TestWorldBuilder.ActorIds.Scout,
                    TestWorldBuilder.PassageIds.SquareRidgeTrail
                );

                Assert.Contains("\"CurrentTick\": 11", time.Output, StringComparison.Ordinal);
                Assert.Equal(
                    "scout: square --north gate/square-ridge-trail--> ridge | land | cost=5",
                    move.Output
                );
            }

            using var reopened = TextAdv2SampleWorldDevBootstrap.OpenOrCreateRuntime(repoDir);
            var timeAfterReopen = reopened.ObserveTime();
            var traceAfterReopen = reopened.TraceActorRoute(TestWorldBuilder.ActorIds.Scout);

            Assert.Contains("\"CurrentTick\": 11", timeAfterReopen.Output, StringComparison.Ordinal);
            Assert.Contains(
                "1. square --north gate/square-ridge-trail--> ridge | land | cost=5",
                traceAfterReopen.Output,
                StringComparison.Ordinal
            );
            Assert.Contains("end=ridge (Ridge) | steps=1 | totalCost=5", traceAfterReopen.Output, StringComparison.Ordinal);
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

                Assert.Contains("\"PlannerMode\": \"zero\"", initial.Output, StringComparison.Ordinal);
                Assert.Contains("\"SnapshotStatus\": \"inactive\"", initial.Output, StringComparison.Ordinal);
                Assert.Contains("\"PlannerMode\": \"landmark\"", rebuilt.Output, StringComparison.Ordinal);
                Assert.Contains("\"LandmarkCount\": 2", rebuilt.Output, StringComparison.Ordinal);
                Assert.Contains("ROUTE PLAN from=village (Village) to=aerie (Aerie) status=found", planned.Output, StringComparison.Ordinal);
                Assert.Contains("totalCost=11", planned.Output, StringComparison.Ordinal);
            }

            using var reopened = TextAdv2SampleWorldDevBootstrap.OpenOrCreateRuntime(repoDir);
            var observedAfterReopen = reopened.ObserveRouteAcceleration();

            Assert.Contains("\"PlannerMode\": \"zero\"", observedAfterReopen.Output, StringComparison.Ordinal);
            Assert.Contains("\"SnapshotStatus\": \"inactive\"", observedAfterReopen.Output, StringComparison.Ordinal);
            Assert.Contains("\"IsPersistent\": false", observedAfterReopen.Output, StringComparison.Ordinal);
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

            Assert.Contains("\"PlannerMode\": \"landmark\"", rebuilt.Output, StringComparison.Ordinal);
            Assert.Contains("\"LandmarkProfileName\": \"sample-world-default\"", rebuilt.Output, StringComparison.Ordinal);
            Assert.Contains("\"LandmarkCount\": 3", rebuilt.Output, StringComparison.Ordinal);
            Assert.Contains("\"aerie\"", rebuilt.Output, StringComparison.Ordinal);
            Assert.Contains("\"harbor\"", rebuilt.Output, StringComparison.Ordinal);
            Assert.Contains("\"shrine\"", rebuilt.Output, StringComparison.Ordinal);
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
