namespace Atelia.TextAdv2.Runtime;

/// <summary>
/// 第一版逻辑时间快照。
///
/// 当前它是 world-backed authoritative logical clock。
/// 它与 `WorldTruth` 一起持久化在 repo 中，并在 reopen 同一 repoDir 时恢复。
/// </summary>
public sealed record LogicalTimeSnapshot(long CurrentTick);
