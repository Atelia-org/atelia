using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.Spatial;

/// <summary>
/// 基于 canonical spatial seam 的空间一致性校验 helper。
///
/// 它不拥有世界真相，也不负责构建 snapshot；
/// 它只消费 <see cref="WorldSpatialSnapshot"/>，把“如何读取地点邻接关系做校验”的逻辑统一收口在 spatial 层。
/// </summary>
internal static class WorldSpatialValidation
{
    /// <summary>
    /// 校验某 location 上拟新增的 exit name 当前尚未被占用。
    /// 若已被占用，则报出已占用该出口名的现有 passage。
    /// </summary>
    public static void EnsureExitNameAvailable(
        WorldSpatialSnapshot spatial,
        string locationId,
        string exitName
    )
    {
        ArgumentNullException.ThrowIfNull(spatial);
        WorldState.ValidateEntityId(locationId, nameof(locationId));
        WorldState.ValidateExitName(exitName, nameof(exitName));

        if (!spatial.Locations.TryGetValue(locationId, out var adjacency))
        {
            throw new InvalidOperationException($"Location '{locationId}' does not exist.");
        }

        foreach (var edge in adjacency.Edges)
        {
            if (string.Equals(edge.ExitName, exitName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Location '{locationId}' already uses exit name '{exitName}' for passage '{edge.PassageId}'."
                );
            }
        }
    }

    /// <summary>
    /// 校验整个 spatial snapshot 中，每个 location 的 exit name 都唯一。
    /// 若检测到重复，则报出该重复组中 canonical 排序下的首个 passage 作为定位锚点。
    /// </summary>
    public static void EnsureUniqueExitNames(WorldSpatialSnapshot spatial)
    {
        ArgumentNullException.ThrowIfNull(spatial);

        foreach (var adjacency in spatial.Locations
                     .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                     .Select(entry => entry.Value))
        {
            string? previousExitName = null;
            string? firstPassageIdForCurrentExitName = null;

            foreach (var edge in adjacency.Edges
                         .OrderBy(edge => edge.ExitName, StringComparer.Ordinal)
                         .ThenBy(edge => edge.PassageId, StringComparer.Ordinal)
                         .ThenBy(edge => edge.ToLocationId, StringComparer.Ordinal))
            {
                if (!string.Equals(previousExitName, edge.ExitName, StringComparison.Ordinal))
                {
                    previousExitName = edge.ExitName;
                    firstPassageIdForCurrentExitName = edge.PassageId;
                    continue;
                }

                throw new InvalidOperationException(
                    $"Location '{adjacency.LocationId}' reuses exit name '{edge.ExitName}' during world load; duplicate detected at passage '{firstPassageIdForCurrentExitName}'."
                );
            }
        }
    }
}
