using Atelia.TextAdv2.Observation;

namespace Atelia.TextAdv2.DevSupport;

/// <summary>
/// `RoutePlanStatus` 的 canonical token helper。
/// machine-facing contract 仍定义在 observation layer；这里负责 host JSON 与 dev text helper 复用的 token 映射。
/// </summary>
internal static class RoutePlanStatusWireToken {
    public static RoutePlanStatus Parse(string value)
        => value switch {
            "found" => RoutePlanStatus.Found,
            "already-there" => RoutePlanStatus.AlreadyThere,
            "unreachable" => RoutePlanStatus.Unreachable,
            _ => throw new InvalidOperationException($"Unknown route plan status '{value}'."),
        };

    public static string ToWireToken(this RoutePlanStatus value)
        => value switch {
            RoutePlanStatus.Found => "found",
            RoutePlanStatus.AlreadyThere => "already-there",
            RoutePlanStatus.Unreachable => "unreachable",
            _ => throw new InvalidOperationException($"Unsupported route plan status '{value}'."),
        };
}
