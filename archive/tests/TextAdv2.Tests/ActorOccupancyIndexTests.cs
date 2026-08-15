using Atelia.StateJournal;
using Atelia.TextAdv2.ReadModel;
using Atelia.TextAdv2.WorldTruth;
using Xunit;

namespace Atelia.TextAdv2.Tests;

public class ActorOccupancyIndexTests {
    [Fact]
    public void Build_WithCoLocatedActors_EnumeratesStableActorIdsByLocation() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = WorldState.Create(revision);

            _ = world.CreateLocation("start", "Start", "start");
            _ = world.CreateActor("beta", "Beta", "start");
            _ = world.CreateActor("alpha", "Alpha", "start");

            var occupancy = ActorOccupancyIndex.Build(world);

            Assert.Equal(
                ["alpha", "beta"],
                occupancy.EnumerateActorIdsAtLocation("start").ToArray()
            );
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void AddActor_AfterInitialBuild_MakesActorVisibleWithoutRebuild() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = WorldState.Create(revision);

            _ = world.CreateLocation("start", "Start", "start");
            _ = world.CreateActor("alpha", "Alpha", "start");

            var occupancy = ActorOccupancyIndex.Build(world);
            var beta = world.CreateActor("beta", "Beta", "start");
            occupancy.AddActor(beta.Id, beta.CurrentLocationId);

            Assert.Equal(
                ["alpha", "beta"],
                occupancy.EnumerateActorIdsAtLocation("start").ToArray()
            );
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void MoveActor_AfterInitialBuild_UpdatesSourceAndTargetBucketsIncrementally() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = WorldState.Create(revision);

            _ = world.CreateLocation("start", "Start", "start");
            _ = world.CreateLocation("goal", "Goal", "goal");
            _ = world.CreatePassage("start-goal", "start", "go", "goal", "back");
            _ = world.CreateActor("alpha", "Alpha", "start");
            _ = world.CreateActor("beta", "Beta", "goal");

            var occupancy = ActorOccupancyIndex.Build(world);
            var receipt = world.MoveActorAlongPassage("alpha", "start-goal");
            occupancy.MoveActor(receipt.ActorId, receipt.FromLocationId, receipt.ToLocationId);

            Assert.Empty(occupancy.EnumerateActorIdsAtLocation("start"));
            Assert.Equal(
                ["alpha", "beta"],
                occupancy.EnumerateActorIdsAtLocation("goal").ToArray()
            );
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    private static string CreateTempRepoDir()
        => Path.Combine(Path.GetTempPath(), $"atelia-textadv2-occupancy-tests-{Guid.NewGuid():N}");

    private static void DeleteDirectoryIfExists(string repoDir) {
        if (Directory.Exists(repoDir)) {
            Directory.Delete(repoDir, recursive: true);
        }
    }
}
