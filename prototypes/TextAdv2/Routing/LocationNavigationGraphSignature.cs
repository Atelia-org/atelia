using System.Text;
using Atelia.TextAdv2.Spatial;
using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.Routing;

/// <summary>
/// 为 route acceleration 等缓存提供稳定的 routing graph signature。
/// 只编码 location ID 与 canonical routing edges，故意忽略展示字段。
///
/// 重构后从 <see cref="WorldSpatialSnapshot"/> 派生，不再依赖 LocationNavigationGraphProjector。
/// </summary>
internal static class LocationNavigationGraphSignature {
    /// <summary>
    /// 从 spatial seam 构建 graph signature。
    /// 这是新的 canonical 路径：输入是 derived spatial snapshot，不是 raw world state。
    /// </summary>
    public static string Build(WorldSpatialSnapshot spatial) {
        ArgumentNullException.ThrowIfNull(spatial);

        var builder = new StringBuilder();
        foreach (var locationId in spatial.Locations.Keys.Order(StringComparer.Ordinal)) {
            var adjacency = spatial.Locations[locationId];
            builder.Append("L|").Append(locationId).AppendLine();

            var graph = LocationNavigationGraph.FromAdjacency(adjacency);
            foreach (var edge in graph.Edges) {
                builder.Append("E|")
                    .Append(locationId)
                    .Append('|')
                    .Append(edge.PassageId)
                    .Append('|')
                    .Append(edge.TargetLocationId)
                    .Append('|')
                    .Append(edge.TravelMode.ToStorageValue())
                    .Append('|')
                    .Append(edge.TravelCost)
                    .AppendLine();
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// [convenience wrapper] 从 WorldState 构建 graph signature。
    /// </summary>
    public static string Build(WorldState world) {
        ArgumentNullException.ThrowIfNull(world);
        var spatial = WorldSpatialSnapshotBuilder.Build(world);
        return Build(spatial);
    }
}
