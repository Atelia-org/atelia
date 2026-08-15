namespace Atelia.TextAdv2.WorldTruth;

/// <summary>
/// 一次合法移动由 WorldTruth authoritative seam 产出的纯事实回执。
/// </summary>
internal sealed record ActorMoveReceipt(
    string ActorId,
    string PassageId,
    string ExitName,
    string FromLocationId,
    string ToLocationId,
    TravelMode TravelMode,
    int TravelCost
);
