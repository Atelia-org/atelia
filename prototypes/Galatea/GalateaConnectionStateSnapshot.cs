namespace Atelia.Galatea.Server;

/// <summary>Connection selection frozen when a new Observation is accepted.</summary>
internal sealed record GalateaConnectionStateSnapshot(
    string? RuntimeOverrideConnectionId,
    string EffectiveConnectionId,
    string TurnConnectionId,
    GalateaConnectionStateChange? LastChange = null,
    string EffectiveName = "",
    string TurnName = "");

/// <summary>A pending effective connection change, consumed after its exact Observation is appended.</summary>
internal sealed record GalateaConnectionStateChange(
    string SourceActionAddress,
    string PreviousConnectionId,
    string ConnectionId,
    string Name,
    string Evidence,
    string PreviousName = "");
