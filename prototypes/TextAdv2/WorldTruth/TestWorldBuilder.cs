using Atelia.StateJournal;

namespace Atelia.TextAdv2.WorldTruth;

/// <summary>
/// 固定测试地图 builder。
///
/// 这张图不是为了文学体验，而是为了用尽可能少的地点与路径，覆盖当前 Passage 模型已支持的语义：
/// 普通双向路、双向非对称代价、单向禁行、水路、传送、空路、共享说明、本地出口视角与方向说明。
/// </summary>
internal static class TestWorldBuilder {
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
        villageSquareRoad.EndpointA.LocalViewNote = "Main street leaves the village between the bakery and the well.";
        villageSquareRoad.EndpointB.LocalViewNote = "The road back to the village starts beside the market fountain.";

        var squareRidgeTrail = world.CreatePassage(
            PassageIds.SquareRidgeTrail,
            square.Id,
            "north gate",
            ridge.Id,
            "downhill trail",
            TravelMode.Land,
            baseTravelCost: 3
        );
        squareRidgeTrail.EndpointA.LocalViewNote = "The trail begins at the square's north retaining wall.";
        squareRidgeTrail.EndpointB.LocalViewNote = "This path drops from the ridge toward town.";
        squareRidgeTrail.SharedConditionNote = "Wet stone steps make the whole trail slippery.";
        squareRidgeTrail.FromAToB.TravelCostModifier = 2;
        squareRidgeTrail.FromAToB.DirectionConditionNote = "Climbing to the ridge is slow and tiring.";
        squareRidgeTrail.FromBToA.TravelCostModifier = -1;
        squareRidgeTrail.FromBToA.DirectionConditionNote = "Descending is faster, but the sharp turns still matter.";

        var harborDeltaCurrent = world.CreatePassage(
            PassageIds.HarborDeltaCurrent,
            harbor.Id,
            "downstream slip",
            delta.Id,
            "towline bank",
            TravelMode.Water,
            baseTravelCost: 2
        );
        harborDeltaCurrent.EndpointA.LocalViewNote = "A wooden launch ramp points directly into the current.";
        harborDeltaCurrent.EndpointB.LocalViewNote = "Old tow posts mark the only upstream landing.";
        harborDeltaCurrent.SharedConditionNote = "The channel is narrow and cluttered along its full length.";
        harborDeltaCurrent.FromAToB.TravelCostModifier = -1;
        harborDeltaCurrent.FromAToB.DirectionConditionNote = "The boat drifts with the current and needs almost no rowing.";
        harborDeltaCurrent.FromBToA.IsEnabled = false;
        harborDeltaCurrent.FromBToA.DirectionConditionNote = "No upstream tow service is operating right now.";

        var squareShrineGate = world.CreatePassage(
            PassageIds.SquareShrineGate,
            square.Id,
            "old arch",
            shrine.Id,
            "return seal",
            TravelMode.Portal,
            baseTravelCost: 1
        );
        squareShrineGate.EndpointA.LocalViewNote = "The old arch is inset into the square's northern colonnade.";
        squareShrineGate.EndpointB.LocalViewNote = "The return seal glows on the shrine floor.";
        squareShrineGate.SharedConditionNote = "The gate is stable and open at both ends.";
        squareShrineGate.FromAToB.DirectionConditionNote = "Transit is effectively instantaneous from the square side.";
        squareShrineGate.FromBToA.DirectionConditionNote = "Transit is effectively instantaneous from the shrine side.";

        var ridgeAerieWinch = world.CreatePassage(
            PassageIds.RidgeAerieWinch,
            ridge.Id,
            "cliff lift",
            aerie.Id,
            "cargo cradle",
            TravelMode.Air,
            baseTravelCost: 4
        );
        ridgeAerieWinch.EndpointA.LocalViewNote = "A suspended cargo cradle hangs below the ridge station.";
        ridgeAerieWinch.EndpointB.LocalViewNote = "The lift berth is bolted directly to the aerie deck.";
        ridgeAerieWinch.SharedConditionNote = "The lift is exposed to crosswinds for the entire ascent.";
        ridgeAerieWinch.FromAToB.TravelCostModifier = 1;
        ridgeAerieWinch.FromAToB.DirectionConditionNote = "Operators slow the ascent when the wind shifts.";
        ridgeAerieWinch.FromBToA.DirectionConditionNote = "Descending is steady once the cradle is balanced.";
    }
}
