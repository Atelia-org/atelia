using System.Text;
using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.Routing;

/// <summary>
/// 为 route acceleration 等缓存提供稳定的 routing graph signature。
/// 只编码 location ID 与 canonical routing edges，故意忽略展示字段。
/// </summary>
internal static class LocationNavigationGraphSignature {
    public static string Build(WorldState world) {
        ArgumentNullException.ThrowIfNull(world);

        var builder = new StringBuilder();
        foreach (var location in world.EnumerateLocations().OrderBy(location => location.Id, StringComparer.Ordinal)) {
            builder.Append("L|").Append(location.Id).AppendLine();

            var graph = LocationNavigationGraphProjector.Project(world, location.Id);
            foreach (var edge in graph.Edges) {
                builder.Append("E|")
                    .Append(location.Id)
                    .Append('|')
                    .Append(edge.PassageId)
                    .Append('|')
                    .Append(edge.TargetLocationId)
                    .Append('|')
                    .Append(edge.TravelCost)
                    .AppendLine();
            }
        }

        return builder.ToString();
    }
}
