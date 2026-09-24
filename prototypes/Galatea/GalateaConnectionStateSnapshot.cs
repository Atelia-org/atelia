namespace Atelia.Galatea.Server;

/// <summary>Connection selection frozen when a new Observation is accepted.</summary>
internal sealed record GalateaConnectionStateSnapshot(
    string? RuntimeOverrideConnectionId,
    string EffectiveConnectionId,
    string TurnConnectionId,
    GalateaConnectionStateChange? LastChange = null);

/// <summary>The latest effective connection change, retained only in runtime memory.</summary>
internal sealed record GalateaConnectionStateChange(
    string SourceActionAddress,
    string PreviousConnectionId,
    string ConnectionId,
    string Name,
    string Evidence);
