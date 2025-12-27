// Source: Atelia.StateJournal - RBF 文件 FrameTag 分配
// Spec: atelia/docs/StateJournal/mvp-design-v2.md §3.2.1, §3.2.2

using Atelia.Rbf;

namespace Atelia.StateJournal;

/// <summary>
/// RBF 文件用途标识（Meta 或 Data）。
/// </summary>
/// <remarks>
/// <para>StateJournal 使用两个 RBF 文件：</para>
/// <list type="bullet">
///   <item><term>Meta File</term><description>存储 MetaCommitRecord（commit log）</description></item>
///   <item><term>Data File</term><description>存储 ObjectVersionRecord（对象版本记录）</description></item>
/// </list>
/// </remarks>
public enum RbfFileKind : byte {
    /// <summary>
    /// Meta 文件：存储 MetaCommitRecord。
    /// </summary>
    Meta = 0,

    /// <summary>
    /// Data 文件：存储 ObjectVersionRecord。
    /// </summary>
    Data = 1,
}

/// <summary>
/// StateJournal RBF 文件的 FrameTag 分配表。
/// </summary>
/// <remarks>
/// <para><b>FrameTag 位段编码</b>：参见 <see cref="StateJournalFrameTag"/>。</para>
/// <para>此类提供 RBF 层与 StateJournal 层之间的 FrameTag 映射。</para>
/// 
/// <para><b>Meta File FrameTags</b>:</para>
/// <list type="table">
///   <item><term><c>0x00000002</c></term><description>MetaCommitRecord（提交元数据记录）</description></item>
/// </list>
/// 
/// <para><b>Data File FrameTags</b>:</para>
/// <list type="table">
///   <item><term><c>0x00010001</c></term><description>ObjectVersionRecord(Dict)（DurableDict 版本记录）</description></item>
///   <item><term><c>0x00020001</c></term><description>ObjectVersionRecord(Array)（DurableArray 版本记录，未来扩展）</description></item>
/// </list>
/// </remarks>
public static class FrameTags {
    // =========================================================================
    // Meta File FrameTags
    // =========================================================================

    /// <summary>
    /// MetaCommitRecord 的 FrameTag（RecordType=MetaCommit, SubType=0）。
    /// </summary>
    /// <remarks>
    /// <para>值：<c>0x00000002</c></para>
    /// <para>字节序列（LE）：<c>02 00 00 00</c></para>
    /// </remarks>
    public static readonly FrameTag MetaCommit = StateJournalFrameTag.MetaCommit;

    // =========================================================================
    // Data File FrameTags
    // =========================================================================

    /// <summary>
    /// DurableDict 版本记录的 FrameTag（RecordType=ObjectVersion, ObjectKind=Dict）。
    /// </summary>
    /// <remarks>
    /// <para>值：<c>0x00010001</c></para>
    /// <para>字节序列（LE）：<c>01 00 01 00</c></para>
    /// </remarks>
    public static readonly FrameTag DictVersion = StateJournalFrameTag.DictVersion;

    /// <summary>
    /// DurableArray 版本记录的 FrameTag（RecordType=ObjectVersion, ObjectKind=Array）。
    /// </summary>
    /// <remarks>
    /// <para>值：<c>0x00020001</c></para>
    /// <para>字节序列（LE）：<c>01 00 02 00</c></para>
    /// <para>状态：Reserved（未来扩展）</para>
    /// </remarks>
    public static readonly FrameTag ArrayVersion = new(0x00020001);

    // =========================================================================
    // Validation
    // =========================================================================

    /// <summary>
    /// 检查 FrameTag 是否为有效的 Meta File FrameTag。
    /// </summary>
    /// <param name="tag">要检查的 FrameTag。</param>
    /// <returns>如果是有效的 Meta File FrameTag，返回 true。</returns>
    public static bool IsMetaFrameTag(FrameTag tag) {
        return tag.Value == MetaCommit.Value;
    }

    /// <summary>
    /// 检查 FrameTag 是否为有效的 Data File FrameTag。
    /// </summary>
    /// <param name="tag">要检查的 FrameTag。</param>
    /// <returns>如果是有效的 Data File FrameTag，返回 true。</returns>
    /// <remarks>
    /// <para>当前 MVP 只支持 Dict；Array 等保留给未来扩展。</para>
    /// </remarks>
    public static bool IsDataFrameTag(FrameTag tag) {
        var recordType = tag.GetRecordType();
        return recordType == RecordType.ObjectVersion;
    }

    /// <summary>
    /// 获取指定文件类型的有效 FrameTag 集合（用于调试/诊断）。
    /// </summary>
    /// <param name="fileKind">文件类型。</param>
    /// <returns>有效 FrameTag 值的集合。</returns>
    public static IReadOnlyList<FrameTag> GetValidFrameTags(RbfFileKind fileKind) {
        return fileKind switch {
            RbfFileKind.Meta => [MetaCommit],
            RbfFileKind.Data => [DictVersion],  // MVP: 只有 Dict
            _ => [],
        };
    }
}
