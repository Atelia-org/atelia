using Atelia.StateJournal;

namespace Atelia.TextAdv;

internal static class GameSimulation
{
    private const string WorldKey = "world";
    private const string LocationsKey = "locations";
    private const string InitialLocationKey = "initialLocation";
    private const string PlayerKey = "player";
    private const string PlayerLocationKey = "location";
    private const string NameKey = "name";
    private const string DescriptionKey = "description";
    private const string ExitsKey = "exits";

    private sealed record GameError(string ErrorCode, string Message)
        : AteliaError(ErrorCode, Message);

    internal sealed record LocationPerception(
        string LocationId,
        string Name,
        string Description,
        IReadOnlyList<LocationExitPerception> Exits);

    internal sealed record LocationExitPerception(
        string Direction,
        string TargetLocationId,
        string TargetName);

    internal static DurableDict<string> CreateNewWorld(Repository repo)
    {
        var revResult = repo.GetOrCreateBranch("main");
        var rev = revResult.Value!;

        var root = rev.CreateDict<string>();
        var world = rev.CreateDict<string>();
        var locations = rev.CreateDict<string>();
        var player = rev.CreateDict<string>();

        var beachId = "beach";
        var forestId = "forest";

        var beach = CreateLocation(rev, "沙滩",
            "一片开阔的沙滩，海浪轻拍着海岸线。"
            + "细白的沙子在阳光下闪闪发光。远处可以看到茂密的树林。");
        var forest = CreateLocation(rev, "密林",
            "茂密的树林遮天蔽日，空气中弥漫着泥土和树叶的气味。"
            + "树影间隐约能听到鸟鸣声。南边透过树缝可以看到沙滩的亮光。");

        AddExit(beach, "north", forestId);
        AddExit(forest, "south", beachId);

        locations.Upsert(beachId, beach);
        locations.Upsert(forestId, forest);

        world.Upsert(LocationsKey, locations);
        world.Upsert(InitialLocationKey, beachId);

        player.Upsert(PlayerLocationKey, beachId);

        root.Upsert(WorldKey, world);
        root.Upsert(PlayerKey, player);

        _ = repo.Commit(root).Value;
        return root;
    }

    internal static LocationPerception DescribeCurrentLocation(DurableDict<string> root)
        => DescribeLocation(root, GetPlayerLocationId(root));

    internal static AteliaResult<LocationPerception> MovePlayer(DurableDict<string> root, string direction)
    {
        var player = root.GetOrThrow<DurableDict<string>>(PlayerKey)!;
        var currentLocationId = GetPlayerLocationId(root);
        var currentLocation = GetLocation(root, currentLocationId);
        var exits = currentLocation.GetOrThrow<DurableDict<string>>(ExitsKey)!;

        if (!exits.TryGet(direction, out string? targetLocationId)
            || string.IsNullOrWhiteSpace(targetLocationId))
        {
            var available = string.Join(", ",
                EnumerateExits(root, currentLocationId)
                    .Select(exit => $"{exit.Direction} → {exit.TargetName}"));
            var currentName = currentLocation.GetOrThrow<string>(NameKey)!;
            return AteliaResult<LocationPerception>.Failure(
                new GameError("TextAdv.InvalidDirection",
                    $"「{currentName}」没有通往「{direction}」的出口。可用的出口: {available}"));
        }

        _ = GetLocation(root, targetLocationId);
        player.Upsert(PlayerLocationKey, targetLocationId);
        return DescribeLocation(root, targetLocationId);
    }

    private static string GetPlayerLocationId(DurableDict<string> root)
    {
        var player = root.GetOrThrow<DurableDict<string>>(PlayerKey)!;
        return player.GetOrThrow<string>(PlayerLocationKey)!;
    }

    private static LocationPerception DescribeLocation(DurableDict<string> root, string locationId)
    {
        var location = GetLocation(root, locationId);
        var name = location.GetOrThrow<string>(NameKey)!;
        var description = location.GetOrThrow<string>(DescriptionKey)!;
        var exits = EnumerateExits(root, locationId).ToArray();
        return new LocationPerception(locationId, name, description, exits);
    }

    private static IEnumerable<LocationExitPerception> EnumerateExits(
        DurableDict<string> root,
        string locationId)
    {
        var location = GetLocation(root, locationId);
        var exits = location.GetOrThrow<DurableDict<string>>(ExitsKey)!;

        foreach (var direction in exits.Keys)
        {
            var targetLocationId = exits.GetOrThrow<string>(direction)!;
            var targetLocation = GetLocation(root, targetLocationId);
            var targetName = targetLocation.GetOrThrow<string>(NameKey)!;
            yield return new LocationExitPerception(direction, targetLocationId, targetName);
        }
    }

    private static DurableDict<string> GetLocation(DurableDict<string> root, string locationId)
    {
        var world = root.GetOrThrow<DurableDict<string>>(WorldKey)!;
        var locations = world.GetOrThrow<DurableDict<string>>(LocationsKey)!;
        return locations.GetOrThrow<DurableDict<string>>(locationId)!;
    }

    private static DurableDict<string> CreateLocation(Revision rev, string name, string description)
    {
        var location = rev.CreateDict<string>();
        location.Upsert(NameKey, name);
        location.Upsert(DescriptionKey, description);
        location.Upsert(ExitsKey, rev.CreateDict<string>());
        return location;
    }

    private static void AddExit(DurableDict<string> from, string direction, string targetLocationId)
    {
        var exits = from.GetOrThrow<DurableDict<string>>(ExitsKey)!;
        exits.Upsert(direction, targetLocationId);
    }
}
