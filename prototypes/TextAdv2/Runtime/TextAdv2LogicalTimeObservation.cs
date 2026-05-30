namespace Atelia.TextAdv2.Runtime;

/// <summary>
/// 第一版逻辑时间快照。
///
/// 当前它是 runtime / host 级的 server-owned logical clock。
/// 它由 runtime sidecar state 持久化，保证同一 repoDir 重开后可恢复；
/// 但当前仍不承诺与 world commit 的原子一致性。
/// </summary>
public sealed record TextAdv2LogicalTimeObservation(long CurrentTick);
