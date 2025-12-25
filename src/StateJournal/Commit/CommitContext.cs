// Source: Atelia.StateJournal - 提交上下文
// Spec: atelia/docs/StateJournal/mvp-design-v2.md §Two-Phase Commit

namespace Atelia.StateJournal;

/// <summary>
/// 提交上下文，用于收集 commit 过程中的数据。
/// </summary>
/// <remarks>
/// <para>
/// MVP 阶段不含实际存储层，CommitContext 用于测试验证。
/// </para>
/// <para>
/// 对应条款：
/// <list type="bullet">
///   <item><c>[A-COMMITALL-DIRTY-ITERATION]</c>: 遍历所有脏对象</item>
///   <item><c>[A-COMMITALL-WRITE-DIFF]</c>: 调用 WritePendingDiff 序列化</item>
///   <item><c>[A-COMMITALL-UPDATE-VERSIONINDEX]</c>: 更新 VersionIndex 映射</item>
/// </list>
/// </para>
/// </remarks>
public class CommitContext {
    private readonly List<(ulong ObjectId, byte[] DiffPayload, uint FrameTag)> _writtenRecords = new();

    /// <summary>
    /// 当前 Epoch 序号。
    /// </summary>
    public ulong EpochSeq { get; init; }

    /// <summary>
    /// 模拟的 DataTail（下一个写入位置）。
    /// </summary>
    public ulong DataTail { get; set; }

    /// <summary>
    /// VersionIndex 的位置指针。
    /// </summary>
    public ulong VersionIndexPtr { get; set; }

    /// <summary>
    /// 根对象 ID（MVP: 可选）。
    /// </summary>
    public ulong RootObjectId { get; set; }

    /// <summary>
    /// 写入一个对象版本记录（模拟 RbfFramer.Write）。
    /// </summary>
    /// <param name="objectId">对象 ID。</param>
    /// <param name="diffPayload">diff 负载数据。</param>
    /// <param name="frameTag">帧标签。</param>
    /// <returns>写入位置（用于更新 VersionIndex）。</returns>
    public ulong WriteObjectVersion(ulong objectId, ReadOnlySpan<byte> diffPayload, uint frameTag) {
        var position = DataTail;
        var payload = diffPayload.ToArray();
        _writtenRecords.Add((objectId, payload, frameTag));

        // 模拟帧大小：8 (FrameHeader) + payload + 4 (CRC)
        // 注意：PrevVersionPtr 已经包含在 diffPayload 中
        DataTail += (ulong)(8 + payload.Length + 4);

        return position;
    }

    /// <summary>
    /// 获取所有写入的记录（用于测试验证）。
    /// </summary>
    public IReadOnlyList<(ulong ObjectId, byte[] DiffPayload, uint FrameTag)> WrittenRecords => _writtenRecords;

    /// <summary>
    /// 构造 MetaCommitRecord。
    /// </summary>
    /// <param name="nextObjectId">下一个可分配的 ObjectId。</param>
    /// <returns>包含当前提交元数据的 MetaCommitRecord。</returns>
    public MetaCommitRecord BuildMetaCommitRecord(ulong nextObjectId) {
        return new MetaCommitRecord {
            EpochSeq = EpochSeq,
            RootObjectId = RootObjectId,
            VersionIndexPtr = VersionIndexPtr,
            DataTail = DataTail,
            NextObjectId = nextObjectId,
        };
    }
}
