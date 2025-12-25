// Source: Atelia.StateJournal - Recovery Info
// Spec: atelia/docs/StateJournal/mvp-design-v2.md §Recovery

namespace Atelia.StateJournal;

/// <summary>
/// 恢复结果信息。
/// </summary>
/// <remarks>
/// <para>
/// 对应条款：
/// <list type="bullet">
///   <item><c>[R-META-AHEAD-BACKTRACK]</c>: 若 MetaCommitRecord 的 DataTail 大于 data file 实际长度，继续回扫上一条</item>
///   <item><c>[R-DATATAIL-TRUNCATE-SAFETY]</c>: data file 截断到 DataTail 是安全的</item>
/// </list>
/// </para>
/// </remarks>
public readonly struct RecoveryInfo {
    /// <summary>
    /// 恢复的 Epoch 序号。
    /// </summary>
    public ulong EpochSeq { get; init; }

    /// <summary>
    /// 恢复的 NextObjectId。
    /// </summary>
    public ulong NextObjectId { get; init; }

    /// <summary>
    /// VersionIndex 指针。
    /// </summary>
    public ulong VersionIndexPtr { get; init; }

    /// <summary>
    /// Data file 有效尾部。
    /// </summary>
    public ulong DataTail { get; init; }

    /// <summary>
    /// 是否发生了截断（data file 比 DataTail 长）。
    /// </summary>
    public bool WasTruncated { get; init; }

    /// <summary>
    /// 截断前的 data file 大小（如果 WasTruncated）。
    /// </summary>
    public ulong OriginalDataSize { get; init; }

    /// <summary>
    /// 是否是空仓库。
    /// </summary>
    public bool IsEmpty => EpochSeq == 0;

    /// <summary>
    /// 创建空仓库的 RecoveryInfo。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 空仓库边界（MVP 固定）：
    /// <list type="bullet">
    ///   <item><c>EpochSeq = 0</c></item>
    ///   <item><c>NextObjectId = 16</c>（保留区外的第一个 ID）</item>
    ///   <item><c>VersionIndexPtr = 0</c>（null）</item>
    /// </list>
    /// </para>
    /// </remarks>
    public static RecoveryInfo Empty => new() {
        EpochSeq = 0,
        NextObjectId = 16,
        VersionIndexPtr = 0,
        DataTail = 0,
        WasTruncated = false,
    };
}
