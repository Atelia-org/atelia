namespace Atelia.TextAdv2.Runtime;

/// <summary>
/// 第一版逻辑时间快照。
///
/// 当前它是 runtime / host 级的 server-owned logical clock。
/// 当前仅作为进程内易失调试态存在；
/// reopen 同一 repoDir 时不恢复，也不承诺与 world commit 的原子一致性。
/// </summary>
public sealed record TextAdv2LogicalTimeObservation(long CurrentTick);
