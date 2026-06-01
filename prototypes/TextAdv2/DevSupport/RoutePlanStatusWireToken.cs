using Atelia.TextAdv2.Observation;

namespace Atelia.TextAdv2.DevSupport;

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
