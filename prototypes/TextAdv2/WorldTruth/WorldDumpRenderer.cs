using System.Text;

namespace Atelia.TextAdv2.WorldTruth;

/// <summary>
/// 以稳定、非文学化的文本格式打印当前世界真相，供调试、测试与人工检视使用。
///
/// 这里的输出故意保持低推断：
/// - 只显示 durable 世界里已经存在的事实；
/// - 不尝试做面向玩家的观察裁剪；
/// - 顺序固定，便于 snapshot test 与日志比对。
/// </summary>
internal static class WorldDumpRenderer {
    public static string Render(WorldState world) {
        ArgumentNullException.ThrowIfNull(world);

        var locations = world.EnumerateLocations()
            .OrderBy(location => location.Id, StringComparer.Ordinal)
            .ToArray();
        var passages = world.EnumeratePassages()
            .OrderBy(passage => passage.Id, StringComparer.Ordinal)
            .ToArray();

        var builder = new StringBuilder();
        builder.AppendLine("WORLD");
        builder.AppendLine($"locations={locations.Length}");
        builder.AppendLine($"passages={passages.Length}");
        builder.AppendLine();
        builder.AppendLine("LOCATIONS");

        for (int i = 0; i < locations.Length; i++) {
            if (i > 0) {
                builder.AppendLine();
            }

            AppendLocation(builder, world, locations[i]);
        }

        builder.AppendLine();
        builder.AppendLine("PASSAGES");

        for (int i = 0; i < passages.Length; i++) {
            if (i > 0) {
                builder.AppendLine();
            }

            AppendPassage(builder, passages[i]);
        }

        return builder.ToString().TrimEnd();
    }

    public static string RenderLocation(WorldState world, string locationId) {
        ArgumentNullException.ThrowIfNull(world);
        WorldState.ValidateEntityId(locationId, nameof(locationId));

        var builder = new StringBuilder();
        AppendLocation(builder, world, world.GetLocation(locationId));
        return builder.ToString().TrimEnd();
    }

    private static void AppendLocation(StringBuilder builder, WorldState world, Location location) {
        builder.AppendLine($"- {location.Id} | {location.Name}");
        builder.AppendLine($"  description: {location.Description}");
        builder.AppendLine("  exits:");

        var exits = world.EnumeratePassagesTouching(location.Id)
            .Select(passage => new {
                Passage = passage,
                Endpoint = passage.GetEndpointFor(location.Id),
                Direction = passage.GetDirectionFrom(location.Id),
                Destination = world.GetLocation(passage.GetOtherLocationId(location.Id)),
            })
            .OrderBy(entry => entry.Endpoint.ExitName, StringComparer.Ordinal)
            .ThenBy(entry => entry.Passage.Id, StringComparer.Ordinal)
            .ToArray();

        if (exits.Length == 0) {
            builder.AppendLine("    - <none>");
            return;
        }

        foreach (var entry in exits) {
            builder.AppendLine(
                $"    - {entry.Endpoint.ExitName} -> {entry.Destination.Id} ({entry.Destination.Name}) | "
                + $"passage={entry.Passage.Id} | mode={entry.Passage.TravelMode.ToStorageValue()} | "
                + $"base={entry.Passage.BaseTravelCost} | modifier={entry.Direction.TravelCostModifier} | "
                + $"total={entry.Passage.BaseTravelCost + entry.Direction.TravelCostModifier} | "
                + $"enabled={entry.Direction.IsEnabled.ToString().ToLowerInvariant()}"
            );

            AppendOptionalLine(builder, "      local", entry.Endpoint.LocalViewNote);
            AppendOptionalLine(builder, "      shared", entry.Passage.SharedConditionNote);
            AppendOptionalLine(builder, "      directional", entry.Direction.DirectionConditionNote);
        }
    }

    private static void AppendPassage(StringBuilder builder, Passage passage) {
        builder.AppendLine($"- {passage.Id}");
        builder.AppendLine(
            $"  shared: mode={passage.TravelMode.ToStorageValue()} | base={passage.BaseTravelCost} | note={FormatOptional(passage.SharedConditionNote)}"
        );
        builder.AppendLine($"  endpointA: {passage.EndpointA.LocationId} | exit={passage.EndpointA.ExitName}");
        AppendOptionalLine(builder, "    local", passage.EndpointA.LocalViewNote);
        builder.AppendLine($"  endpointB: {passage.EndpointB.LocationId} | exit={passage.EndpointB.ExitName}");
        AppendOptionalLine(builder, "    local", passage.EndpointB.LocalViewNote);

        AppendDirectionLine(
            builder,
            passage.EndpointA.LocationId,
            passage.EndpointB.LocationId,
            passage.FromAToB,
            passage.BaseTravelCost
        );
        AppendDirectionLine(
            builder,
            passage.EndpointB.LocationId,
            passage.EndpointA.LocationId,
            passage.FromBToA,
            passage.BaseTravelCost
        );
    }

    private static void AppendDirectionLine(
        StringBuilder builder,
        string fromLocationId,
        string toLocationId,
        PassageDirectionRule direction,
        int baseTravelCost
    ) {
        builder.AppendLine(
            $"  {fromLocationId} -> {toLocationId}: enabled={direction.IsEnabled.ToString().ToLowerInvariant()} | "
            + $"modifier={direction.TravelCostModifier} | total={baseTravelCost + direction.TravelCostModifier}"
        );
        AppendOptionalLine(builder, "    directional", direction.DirectionConditionNote);
    }

    private static void AppendOptionalLine(StringBuilder builder, string label, string value) {
        if (string.IsNullOrEmpty(value)) {
            return;
        }

        builder.AppendLine($"{label}: {value}");
    }

    private static string FormatOptional(string value) => string.IsNullOrEmpty(value) ? "<none>" : value;
}
