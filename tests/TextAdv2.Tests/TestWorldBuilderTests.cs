using Atelia.StateJournal;
using Atelia.TextAdv2.WorldTruth;
using Xunit;

namespace Atelia.TextAdv2.Tests;

public class TestWorldBuilderTests {
    [Fact]
    public void TestWorldBuilder_CoversCurrentPassageSemantics() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = TestWorldBuilder.Create(revision);

            Assert.Equal(7, world.EnumerateLocations().Count());
            Assert.Equal(5, world.EnumeratePassages().Count());

            var squareRidgeTrail = world.GetPassage(TestWorldBuilder.PassageIds.SquareRidgeTrail);
            Assert.Equal(TravelMode.Land, squareRidgeTrail.TravelMode);
            Assert.Equal("north gate", squareRidgeTrail.EndpointA.ExitName);
            Assert.Equal("downhill trail", squareRidgeTrail.EndpointB.ExitName);
            Assert.Equal("Wet stone steps make the whole trail slippery.", squareRidgeTrail.SharedConditionNote);
            Assert.Equal(2, squareRidgeTrail.FromAToB.TravelCostModifier);
            Assert.Equal(-1, squareRidgeTrail.FromBToA.TravelCostModifier);

            var harborDeltaCurrent = world.GetPassage(TestWorldBuilder.PassageIds.HarborDeltaCurrent);
            Assert.Equal(TravelMode.Water, harborDeltaCurrent.TravelMode);
            Assert.True(harborDeltaCurrent.FromAToB.IsEnabled);
            Assert.False(harborDeltaCurrent.FromBToA.IsEnabled);
            Assert.Equal(-1, harborDeltaCurrent.FromAToB.TravelCostModifier);
            Assert.Equal(
                "No upstream tow service is operating right now.",
                harborDeltaCurrent.FromBToA.DirectionConditionNote
            );

            Assert.Equal(TravelMode.Portal, world.GetPassage(TestWorldBuilder.PassageIds.SquareShrineGate).TravelMode);
            Assert.Equal(TravelMode.Air, world.GetPassage(TestWorldBuilder.PassageIds.RidgeAerieWinch).TravelMode);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void PassageMutation_RejectsNegativeTotalTravelCost() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = WorldState.Create(revision);
            var start = world.CreateLocation("start", "Start", "invariant start");
            var goal = world.CreateLocation("goal", "Goal", "invariant goal");
            var passage = world.CreatePassage("start-goal", start.Id, "go", goal.Id, "back", TravelMode.Land, 1);

            var exception = Assert.Throws<InvalidOperationException>(
                () => world.SetPassageDirectionTravelCostModifierFrom(passage.Id, start.Id, -2)
            );

            Assert.Contains("Negative travel cost", exception.Message, StringComparison.Ordinal);
            Assert.Equal(0, passage.FromAToB.TravelCostModifier);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void WorldState_SetPassageTravelMode_UpdatesSharedTravelMode() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = WorldState.Create(revision);
            var start = world.CreateLocation("start", "Start", "travel mode start");
            var goal = world.CreateLocation("goal", "Goal", "travel mode goal");
            var passage = world.CreatePassage("start-goal", start.Id, "go", goal.Id, "back", TravelMode.Land, 1);

            world.SetPassageTravelMode(passage.Id, TravelMode.Water);

            Assert.Equal(TravelMode.Water, world.GetPassage(passage.Id).TravelMode);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void WorldState_SetPassageEndpointLocalViewNote_RejectsLocationOutsidePassage() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = WorldState.Create(revision);
            var start = world.CreateLocation("start", "Start", "unrelated-location start");
            var goal = world.CreateLocation("goal", "Goal", "unrelated-location goal");
            var unrelated = world.CreateLocation("unrelated", "Unrelated", "not connected to passage");
            var passage = world.CreatePassage("start-goal", start.Id, "go", goal.Id, "back", TravelMode.Land, 1);

            var exception = Assert.Throws<InvalidOperationException>(
                () => world.SetPassageEndpointLocalViewNote(passage.Id, unrelated.Id, "should fail")
            );

            Assert.Equal(
                $"Passage '{passage.Id}' does not connect location '{unrelated.Id}'.",
                exception.Message
            );
            Assert.Equal(string.Empty, world.GetPassage(passage.Id).EndpointA.LocalViewNote);
            Assert.Equal(string.Empty, world.GetPassage(passage.Id).EndpointB.LocalViewNote);
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void WorldDumpRenderer_Render_IsDeterministic() {
        string repoDir = CreateTempRepoDir();

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var world = TestWorldBuilder.Create(revision);

            string dump = WorldDumpRenderer.Render(world);

            Assert.Equal(Normalize(ExpectedWorldDump), Normalize(dump));
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void TestWorldBuilder_Reopen_PreservesWorldDump() {
        string repoDir = CreateTempRepoDir();

        try {
            string beforeDump;

            using (var repo = Repository.Create(repoDir).Unwrap()) {
                var revision = repo.CreateBranch("main").Unwrap();
                var world = TestWorldBuilder.Create(revision);
                beforeDump = WorldDumpRenderer.Render(world);
                repo.Commit(world.Root).Unwrap();
            }

            using (var repo = Repository.Open(repoDir).Unwrap()) {
                var revision = repo.CheckoutBranch("main").Unwrap();
                var world = WorldState.FromRoot(revision.GetGraphRoot<DurableDict<string>>().Unwrap());
                string afterDump = WorldDumpRenderer.Render(world);

                Assert.Equal(Normalize(beforeDump), Normalize(afterDump));
            }
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void WorldState_FromRoot_FailsFastWhenSchemaVersionIsMissing() {
        string repoDir = CreateTempRepoDir();
        int supportedVersion;

        try {
            using (var repo = Repository.Create(repoDir).Unwrap()) {
                var revision = repo.CreateBranch("main").Unwrap();
                var world = WorldState.Create(revision);
                supportedVersion = world.Root.GetOrThrow<int>("schemaVersion");
                Assert.True(world.Root.Remove("schemaVersion"));
                repo.Commit(world.Root).Unwrap();
            }

            using (var repo = Repository.Open(repoDir).Unwrap()) {
                var revision = repo.CheckoutBranch("main").Unwrap();
                var exception = Assert.Throws<InvalidOperationException>(
                    () => WorldState.FromRoot(revision.GetGraphRoot<DurableDict<string>>().Unwrap())
                );

                Assert.Equal(
                    $"Expected world-state schemaVersion '{supportedVersion}', but found '<missing>'.",
                    exception.Message
                );
            }
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void WorldState_FromRoot_FailsFastWhenSchemaVersionIsUnsupported() {
        string repoDir = CreateTempRepoDir();
        int supportedVersion;
        int unsupportedVersion;

        try {
            using (var repo = Repository.Create(repoDir).Unwrap()) {
                var revision = repo.CreateBranch("main").Unwrap();
                var world = WorldState.Create(revision);
                supportedVersion = world.Root.GetOrThrow<int>("schemaVersion");
                unsupportedVersion = supportedVersion + 98;
                world.Root.Upsert("schemaVersion", unsupportedVersion);
                repo.Commit(world.Root).Unwrap();
            }

            using (var repo = Repository.Open(repoDir).Unwrap()) {
                var revision = repo.CheckoutBranch("main").Unwrap();
                var exception = Assert.Throws<InvalidOperationException>(
                    () => WorldState.FromRoot(revision.GetGraphRoot<DurableDict<string>>().Unwrap())
                );

                Assert.Equal(
                    $"Expected world-state schemaVersion '{supportedVersion}', but found '{unsupportedVersion}'.",
                    exception.Message
                );
            }
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void WorldState_FromRoot_FailsFastWhenSchemaVersionHasInvalidType() {
        string repoDir = CreateTempRepoDir();
        int supportedVersion;

        try {
            using (var repo = Repository.Create(repoDir).Unwrap()) {
                var revision = repo.CreateBranch("main").Unwrap();
                var world = WorldState.Create(revision);
                supportedVersion = world.Root.GetOrThrow<int>("schemaVersion");
                world.Root.Upsert("schemaVersion", "not-an-int");
                repo.Commit(world.Root).Unwrap();
            }

            using (var repo = Repository.Open(repoDir).Unwrap()) {
                var revision = repo.CheckoutBranch("main").Unwrap();
                var exception = Assert.Throws<InvalidOperationException>(
                    () => WorldState.FromRoot(revision.GetGraphRoot<DurableDict<string>>().Unwrap())
                );

                Assert.Equal(
                    $"Expected world-state schemaVersion '{supportedVersion}', but found '<invalid:TypeMismatch>'.",
                    exception.Message
                );
            }
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void WorldState_FromRoot_StillChecksKindBeforeSchemaGate() {
        string repoDir = CreateTempRepoDir();

        try {
            using (var repo = Repository.Create(repoDir).Unwrap()) {
                var revision = repo.CreateBranch("main").Unwrap();
                var world = WorldState.Create(revision);
                world.Root.Upsert(WorldState.KindKey, "not-world-state");
                repo.Commit(world.Root).Unwrap();
            }

            using (var repo = Repository.Open(repoDir).Unwrap()) {
                var revision = repo.CheckoutBranch("main").Unwrap();
                var exception = Assert.Throws<InvalidOperationException>(
                    () => WorldState.FromRoot(revision.GetGraphRoot<DurableDict<string>>().Unwrap())
                );

                Assert.Equal(
                    "Expected durable object kind 'world-state', but found 'not-world-state'.",
                    exception.Message
                );
            }
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    [Fact]
    public void WorldState_FromRoot_AllowsCurrentSchemaVersionHappyPath() {
        string repoDir = CreateTempRepoDir();

        try {
            using (var repo = Repository.Create(repoDir).Unwrap()) {
                var revision = repo.CreateBranch("main").Unwrap();
                var world = WorldState.Create(revision);
                world.CreateLocation("start", "Start", "schema gate happy path");
                repo.Commit(world.Root).Unwrap();
            }

            using (var repo = Repository.Open(repoDir).Unwrap()) {
                var revision = repo.CheckoutBranch("main").Unwrap();
                var reopened = WorldState.FromRoot(revision.GetGraphRoot<DurableDict<string>>().Unwrap());

                Assert.Equal("Start", reopened.GetLocation("start").Name);
                Assert.Single(reopened.EnumerateLocations());
                Assert.Empty(reopened.EnumeratePassages());
            }
        }
        finally {
            DeleteDirectoryIfExists(repoDir);
        }
    }

    private static string CreateTempRepoDir()
        => Path.Combine(Path.GetTempPath(), $"atelia-textadv2-tests-{Guid.NewGuid():N}");

    private static void DeleteDirectoryIfExists(string repoDir) {
        if (Directory.Exists(repoDir)) {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    private const string ExpectedWorldDump = """
WORLD
actors=0
locations=7
passages=5

LOCATIONS
- aerie | Aerie
  description: Cliff platform reached by aerial lift.
  exits:
    - cargo cradle -> ridge (Ridge) | passage=ridge-aerie-winch | mode=air | base=4 | modifier=0 | total=4 | enabled=true
      local: The lift berth is bolted directly to the aerie deck.
      shared: The lift is exposed to crosswinds for the entire ascent.
      directional: Descending is steady once the cradle is balanced.

- delta | Delta
  description: Low-water landing used to test one-way river travel.
  exits:
    - towline bank -> harbor (Harbor) | passage=harbor-delta-current | mode=water | base=2 | modifier=0 | total=2 | enabled=false
      local: Old tow posts mark the only upstream landing.
      shared: The channel is narrow and cluttered along its full length.
      directional: No upstream tow service is operating right now.

- harbor | Harbor
  description: Working dock with direct access to the downstream channel.
  exits:
    - downstream slip -> delta (Delta) | passage=harbor-delta-current | mode=water | base=2 | modifier=-1 | total=1 | enabled=true
      local: A wooden launch ramp points directly into the current.
      shared: The channel is narrow and cluttered along its full length.
      directional: The boat drifts with the current and needs almost no rowing.

- ridge | Ridge
  description: High ground connected by a steep trail and an aerial winch.
  exits:
    - cliff lift -> aerie (Aerie) | passage=ridge-aerie-winch | mode=air | base=4 | modifier=1 | total=5 | enabled=true
      local: A suspended cargo cradle hangs below the ridge station.
      shared: The lift is exposed to crosswinds for the entire ascent.
      directional: Operators slow the ascent when the wind shifts.
    - downhill trail -> square (Square) | passage=square-ridge-trail | mode=land | base=3 | modifier=-1 | total=2 | enabled=true
      local: This path drops from the ridge toward town.
      shared: Wet stone steps make the whole trail slippery.
      directional: Descending is faster, but the sharp turns still matter.

- shrine | Shrine
  description: Portal room used to verify instantaneous travel.
  exits:
    - return seal -> square (Square) | passage=square-shrine-gate | mode=portal | base=1 | modifier=0 | total=1 | enabled=true
      local: The return seal glows on the shrine floor.
      shared: The gate is stable and open at both ends.
      directional: Transit is effectively instantaneous from the shrine side.

- square | Square
  description: Central junction used to verify multiple outgoing passages.
  exits:
    - north gate -> ridge (Ridge) | passage=square-ridge-trail | mode=land | base=3 | modifier=2 | total=5 | enabled=true
      local: The trail begins at the square's north retaining wall.
      shared: Wet stone steps make the whole trail slippery.
      directional: Climbing to the ridge is slow and tiring.
    - old arch -> shrine (Shrine) | passage=square-shrine-gate | mode=portal | base=1 | modifier=0 | total=1 | enabled=true
      local: The old arch is inset into the square's northern colonnade.
      shared: The gate is stable and open at both ends.
      directional: Transit is effectively instantaneous from the square side.
    - west -> village (Village) | passage=village-square-road | mode=land | base=1 | modifier=0 | total=1 | enabled=true
      local: The road back to the village starts beside the market fountain.

- village | Village
  description: Flat starting settlement with a paved road to the square.
  exits:
    - east -> square (Square) | passage=village-square-road | mode=land | base=1 | modifier=0 | total=1 | enabled=true
      local: Main street leaves the village between the bakery and the well.

ACTORS
- <none>

PASSAGES
- harbor-delta-current
  shared: mode=water | base=2 | note=The channel is narrow and cluttered along its full length.
  endpointA: harbor | exit=downstream slip
    local: A wooden launch ramp points directly into the current.
  endpointB: delta | exit=towline bank
    local: Old tow posts mark the only upstream landing.
  harbor -> delta: enabled=true | modifier=-1 | total=1
    directional: The boat drifts with the current and needs almost no rowing.
  delta -> harbor: enabled=false | modifier=0 | total=2
    directional: No upstream tow service is operating right now.

- ridge-aerie-winch
  shared: mode=air | base=4 | note=The lift is exposed to crosswinds for the entire ascent.
  endpointA: ridge | exit=cliff lift
    local: A suspended cargo cradle hangs below the ridge station.
  endpointB: aerie | exit=cargo cradle
    local: The lift berth is bolted directly to the aerie deck.
  ridge -> aerie: enabled=true | modifier=1 | total=5
    directional: Operators slow the ascent when the wind shifts.
  aerie -> ridge: enabled=true | modifier=0 | total=4
    directional: Descending is steady once the cradle is balanced.

- square-ridge-trail
  shared: mode=land | base=3 | note=Wet stone steps make the whole trail slippery.
  endpointA: square | exit=north gate
    local: The trail begins at the square's north retaining wall.
  endpointB: ridge | exit=downhill trail
    local: This path drops from the ridge toward town.
  square -> ridge: enabled=true | modifier=2 | total=5
    directional: Climbing to the ridge is slow and tiring.
  ridge -> square: enabled=true | modifier=-1 | total=2
    directional: Descending is faster, but the sharp turns still matter.

- square-shrine-gate
  shared: mode=portal | base=1 | note=The gate is stable and open at both ends.
  endpointA: square | exit=old arch
    local: The old arch is inset into the square's northern colonnade.
  endpointB: shrine | exit=return seal
    local: The return seal glows on the shrine floor.
  square -> shrine: enabled=true | modifier=0 | total=1
    directional: Transit is effectively instantaneous from the square side.
  shrine -> square: enabled=true | modifier=0 | total=1
    directional: Transit is effectively instantaneous from the shrine side.

- village-square-road
  shared: mode=land | base=1 | note=<none>
  endpointA: village | exit=east
    local: Main street leaves the village between the bakery and the well.
  endpointB: square | exit=west
    local: The road back to the village starts beside the market fountain.
  village -> square: enabled=true | modifier=0 | total=1
  square -> village: enabled=true | modifier=0 | total=1
""";
}
