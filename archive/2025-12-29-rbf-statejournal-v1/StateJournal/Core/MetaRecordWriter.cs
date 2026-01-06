// Source: Atelia.StateJournal - MetaRecordWriter
// Spec: atelia/docs/StateJournal/mvp-design-v2.md §3.2.2

using Atelia.Rbf;

namespace Atelia.StateJournal;

/// <summary>
/// Meta 文件记录写入器。封装 IRbfFramer，提供语义化的 Meta 写入 API。
/// </summary>
/// <remarks>
/// <para>用于向 Meta RBF 文件写入 MetaCommitRecord。</para>
/// <para><b>设计</b>：</para>
/// <list type="bullet">
///   <item>封装 RBF 层 framing 细节（FrameTag、Frame layout）</item>
///   <item>提供语义化 API：AppendCommit 写入提交元数据记录</item>
///   <item>返回 <deleted-place-holder> 作为 commit 记录地址</item>
/// </list>
/// </remarks>
public sealed class MetaRecordWriter {
    private readonly IRbfFramer _framer;

    /// <summary>
    /// 创建 MetaRecordWriter。
    /// </summary>
    /// <param name="framer">RBF 帧写入器。</param>
    public MetaRecordWriter(IRbfFramer framer) {
        ArgumentNullException.ThrowIfNull(framer);
        _framer = framer;
    }

    /// <summary>
    /// 追加一条 MetaCommitRecord。
    /// </summary>
    /// <param name="record">要写入的 MetaCommitRecord。</param>
    /// <returns>写入的帧起始地址。</returns>
    /// <remarks>
    /// <para><b>FrameTag</b>: <see cref="FrameTags.MetaCommit"/> (0x00000002)</para>
    /// <para><b>Payload</b>: MetaCommitRecord 序列化数据</para>
    /// </remarks>
    public <deleted-place-holder> AppendCommit(in MetaCommitRecord record) {
        // 使用 BeginFrame 流式写入
        using var builder = _framer.BeginFrame(FrameTags.MetaCommit);

        // 写入 MetaCommitRecord payload
        MetaCommitRecordSerializer.Write(builder.Payload, record);

        // 提交帧
        return builder.Commit();
    }

    /// <summary>
    /// 追加一条 MetaCommitRecord（使用独立参数）。
    /// </summary>
    /// <param name="epochSeq">Epoch 序号。</param>
    /// <param name="rootObjectId">根对象 ID。</param>
    /// <param name="versionIndexPtr">VersionIndex 版本指针（在 Data file 中的地址）。</param>
    /// <param name="dataTail">Data file 有效尾部地址。</param>
    /// <param name="nextObjectId">下一个可分配的 ObjectId。</param>
    /// <returns>写入的帧起始地址。</returns>
    public <deleted-place-holder> AppendCommit(
        ulong epochSeq,
        ulong rootObjectId,
        ulong versionIndexPtr,
        ulong dataTail,
        ulong nextObjectId
    ) {
        var record = new MetaCommitRecord {
            EpochSeq = epochSeq,
            RootObjectId = rootObjectId,
            VersionIndexPtr = versionIndexPtr,
            DataTail = dataTail,
            NextObjectId = nextObjectId,
        };
        return AppendCommit(record);
    }

    /// <summary>
    /// 将 RBF 缓冲数据推送到底层 Writer。
    /// </summary>
    public void Flush() {
        _framer.Flush();
    }
}
