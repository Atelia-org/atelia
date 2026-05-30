namespace Atelia.TextAdv2.Runtime;

public enum TextAdv2RuntimeCommandMode {
    World,
    Location,
    ObserveLocation,
    ObserveActor,
    ObserveNavigation,
    ObserveActorNavigation,
    ObserveRouteAcceleration,
    ObserveTime,
    AdvanceTime,
    PlanActorRoute,
    PlanRoute,
    RebuildRouteAcceleration,
    TraceActorRoute,
    MoveActorQuiet,
    MoveActor,
}

public sealed record TextAdv2RuntimeCommand(
    TextAdv2RuntimeCommandMode Mode,
    string? Arg1 = null,
    string? Arg2 = null
);

public sealed record TextAdv2RuntimeCommandResult(string Output, string ContentType);

public static class TextAdv2RuntimeContentTypes {
    public const string Json = "application/json";
    public const string PlainText = "text/plain; charset=utf-8";
}
