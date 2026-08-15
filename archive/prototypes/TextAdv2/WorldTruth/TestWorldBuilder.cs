using Atelia.StateJournal;

namespace Atelia.TextAdv2.WorldTruth;

/// <summary>
/// 固定测试地图 builder。
///
/// 这张图不是为了文学体验，而是为了用尽可能少的地点与路径，覆盖当前 Passage 模型已支持的语义：
/// 普通双向路、双向非对称代价、单向禁行、水路、传送、空路、共享说明、本地出口视角与方向说明。
/// </summary>
internal static class TestWorldBuilder {
    public const string RecommendedLandmarkProfileName = "sample-world-default";

    internal static class ActorIds {
        public const string Boatman = "boatman";
        public const string Scout = "scout";
    }

    internal static class LocationIds {
        public const string Aerie = "aerie";
        public const string Delta = "delta";
        public const string Harbor = "harbor";
        public const string Ridge = "ridge";
        public const string Shrine = "shrine";
        public const string Square = "square";
        public const string Village = "village";
    }

    internal static class PassageIds {
        public const string HarborDeltaCurrent = "harbor-delta-current";
        public const string RidgeAerieWinch = "ridge-aerie-winch";
        public const string SquareRidgeTrail = "square-ridge-trail";
        public const string SquareShrineGate = "square-shrine-gate";
        public const string VillageSquareRoad = "village-square-road";
    }

    public static WorldState Create(Revision revision) {
        ArgumentNullException.ThrowIfNull(revision);

        var world = WorldState.Create(revision);
        Populate(world);
        return world;
    }

    /// <summary>
    /// 可选的示例 actor 集合。
    /// 它们不属于空间测试地图本体，因此与 <see cref="Populate"/> 分开，便于按需启用 actor 相关演示或测试。
    /// </summary>
    public static void PopulateSampleActors(WorldState world) {
        ArgumentNullException.ThrowIfNull(world);

        world.CreateActor(ActorIds.Scout, "Scout", LocationIds.Square);
        world.CreateActor(ActorIds.Boatman, "Boatman", LocationIds.Harbor);
    }

    public static void Populate(WorldState world) {
        ArgumentNullException.ThrowIfNull(world);

        var village = world.CreateLocation(
            LocationIds.Village,
            "Village",
            "Flat starting settlement with a paved road to the square."
        );
        var square = world.CreateLocation(
            LocationIds.Square,
            "Square",
            "Central junction used to verify multiple outgoing passages."
        );
        var ridge = world.CreateLocation(
            LocationIds.Ridge,
            "Ridge",
            "High ground connected by a steep trail and an aerial winch."
        );
        var harbor = world.CreateLocation(
            LocationIds.Harbor,
            "Harbor",
            "Working dock with direct access to the downstream channel."
        );
        var delta = world.CreateLocation(
            LocationIds.Delta,
            "Delta",
            "Low-water landing used to test one-way river travel."
        );
        var shrine = world.CreateLocation(
            LocationIds.Shrine,
            "Shrine",
            "Portal room used to verify instantaneous travel."
        );
        var aerie = world.CreateLocation(
            LocationIds.Aerie,
            "Aerie",
            "Cliff platform reached by aerial lift."
        );

        var villageSquareRoad = world.CreatePassage(
            PassageIds.VillageSquareRoad,
            village.Id,
            "east",
            square.Id,
            "west",
            TravelMode.Land,
            baseTravelCost: 1
        );
        world.SetPassageEndpointLocalViewNote(
            villageSquareRoad.Id,
            village.Id,
            "Main street leaves the village between the bakery and the well."
        );
        world.SetPassageEndpointLocalViewNote(
            villageSquareRoad.Id,
            square.Id,
            "The road back to the village starts beside the market fountain."
        );

        var squareRidgeTrail = world.CreatePassage(
            PassageIds.SquareRidgeTrail,
            square.Id,
            "north gate",
            ridge.Id,
            "downhill trail",
            TravelMode.Land,
            baseTravelCost: 3
        );
        world.SetPassageEndpointLocalViewNote(
            squareRidgeTrail.Id,
            square.Id,
            "The trail begins at the square's north retaining wall."
        );
        world.SetPassageEndpointLocalViewNote(squareRidgeTrail.Id, ridge.Id, "This path drops from the ridge toward town.");
        world.SetPassageSharedConditionNote(squareRidgeTrail.Id, "Wet stone steps make the whole trail slippery.");
        world.SetPassageDirectionTravelCostModifierFrom(squareRidgeTrail.Id, square.Id, 2);
        world.SetPassageDirectionConditionNoteFrom(
            squareRidgeTrail.Id,
            square.Id,
            "Climbing to the ridge is slow and tiring."
        );
        world.SetPassageDirectionTravelCostModifierFrom(squareRidgeTrail.Id, ridge.Id, -1);
        world.SetPassageDirectionConditionNoteFrom(
            squareRidgeTrail.Id,
            ridge.Id,
            "Descending is faster, but the sharp turns still matter."
        );

        var harborDeltaCurrent = world.CreatePassage(
            PassageIds.HarborDeltaCurrent,
            harbor.Id,
            "downstream slip",
            delta.Id,
            "towline bank",
            TravelMode.Water,
            baseTravelCost: 2
        );
        world.SetPassageEndpointLocalViewNote(
            harborDeltaCurrent.Id,
            harbor.Id,
            "A wooden launch ramp points directly into the current."
        );
        world.SetPassageEndpointLocalViewNote(
            harborDeltaCurrent.Id,
            delta.Id,
            "Old tow posts mark the only upstream landing."
        );
        world.SetPassageSharedConditionNote(harborDeltaCurrent.Id, "The channel is narrow and cluttered along its full length.");
        world.SetPassageDirectionTravelCostModifierFrom(harborDeltaCurrent.Id, harbor.Id, -1);
        world.SetPassageDirectionConditionNoteFrom(
            harborDeltaCurrent.Id,
            harbor.Id,
            "The boat drifts with the current and needs almost no rowing."
        );
        world.SetPassageDirectionEnabledFrom(harborDeltaCurrent.Id, delta.Id, false);
        world.SetPassageDirectionConditionNoteFrom(
            harborDeltaCurrent.Id,
            delta.Id,
            "No upstream tow service is operating right now."
        );

        var squareShrineGate = world.CreatePassage(
            PassageIds.SquareShrineGate,
            square.Id,
            "old arch",
            shrine.Id,
            "return seal",
            TravelMode.Portal,
            baseTravelCost: 1
        );
        world.SetPassageEndpointLocalViewNote(
            squareShrineGate.Id,
            square.Id,
            "The old arch is inset into the square's northern colonnade."
        );
        world.SetPassageEndpointLocalViewNote(
            squareShrineGate.Id,
            shrine.Id,
            "The return seal glows on the shrine floor."
        );
        world.SetPassageSharedConditionNote(squareShrineGate.Id, "The gate is stable and open at both ends.");
        world.SetPassageDirectionConditionNoteFrom(
            squareShrineGate.Id,
            square.Id,
            "Transit is effectively instantaneous from the square side."
        );
        world.SetPassageDirectionConditionNoteFrom(
            squareShrineGate.Id,
            shrine.Id,
            "Transit is effectively instantaneous from the shrine side."
        );

        var ridgeAerieWinch = world.CreatePassage(
            PassageIds.RidgeAerieWinch,
            ridge.Id,
            "cliff lift",
            aerie.Id,
            "cargo cradle",
            TravelMode.Air,
            baseTravelCost: 4
        );
        world.SetPassageEndpointLocalViewNote(
            ridgeAerieWinch.Id,
            ridge.Id,
            "A suspended cargo cradle hangs below the ridge station."
        );
        world.SetPassageEndpointLocalViewNote(
            ridgeAerieWinch.Id,
            aerie.Id,
            "The lift berth is bolted directly to the aerie deck."
        );
        world.SetPassageSharedConditionNote(ridgeAerieWinch.Id, "The lift is exposed to crosswinds for the entire ascent.");
        world.SetPassageDirectionTravelCostModifierFrom(ridgeAerieWinch.Id, ridge.Id, 1);
        world.SetPassageDirectionConditionNoteFrom(
            ridgeAerieWinch.Id,
            ridge.Id,
            "Operators slow the ascent when the wind shifts."
        );
        world.SetPassageDirectionConditionNoteFrom(
            ridgeAerieWinch.Id,
            aerie.Id,
            "Descending is steady once the cradle is balanced."
        );
    }

    public static bool TryGetRecommendedLandmarkLocationIds(WorldState world, out string[] landmarkLocationIds) {
        ArgumentNullException.ThrowIfNull(world);

        var actualLocationIds = world.EnumerateLocations()
            .Select(location => location.Id)
            .ToHashSet(StringComparer.Ordinal);
        var actualPassageIds = world.EnumeratePassages()
            .Select(passage => passage.Id)
            .ToHashSet(StringComparer.Ordinal);

        if (!ContainsAll(actualLocationIds,
            [
                LocationIds.Aerie,
                LocationIds.Delta,
                LocationIds.Harbor,
                LocationIds.Ridge,
                LocationIds.Shrine,
                LocationIds.Square,
                LocationIds.Village,
            ]
        )
            || !ContainsAll(actualPassageIds,
                [
                PassageIds.HarborDeltaCurrent,
                PassageIds.RidgeAerieWinch,
                PassageIds.SquareRidgeTrail,
                PassageIds.SquareShrineGate,
                PassageIds.VillageSquareRoad,
            ]
            )) {
            landmarkLocationIds = [];
            return false;
        }

        landmarkLocationIds = [
            LocationIds.Aerie,
            LocationIds.Harbor,
            LocationIds.Shrine,
        ];
        return true;
    }

    private static bool ContainsAll(HashSet<string> actualIds, IEnumerable<string> expectedIds) {
        ArgumentNullException.ThrowIfNull(actualIds);
        ArgumentNullException.ThrowIfNull(expectedIds);

        foreach (string expectedId in expectedIds) {
            if (!actualIds.Contains(expectedId)) { return false; }
        }

        return true;
    }
}
