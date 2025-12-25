// Source: Atelia.StateJournal - FrameTag 位段编码
// Spec: atelia/docs/StateJournal/mvp-design-v2.md §F-FRAMETAG-STATEJOURNAL-BITLAYOUT

using Atelia.Rbf;

namespace Atelia.StateJournal;

/// <summary>
/// Record 顶层类型（FrameTag 低 16 位）。
/// </summary>
/// <remarks>
/// <para>对应条款：<c>[F-FRAMETAG-STATEJOURNAL-BITLAYOUT]</c></para>
/// <para>计算：<c>RecordType = (ushort)(FrameTag.Value &amp; 0xFFFF)</c></para>
/// </remarks>
public enum RecordType : ushort
{
    /// <summary>
    /// 保留值，MUST NOT 使用。
    /// </summary>
    Reserved = 0x0000,

    /// <summary>
    /// 对象版本记录。
    /// </summary>
    ObjectVersion = 0x0001,

    /// <summary>
    /// 提交元数据记录。
    /// </summary>
    MetaCommit = 0x0002,
}

/// <summary>
/// 对象类型（当 RecordType=ObjectVersion 时，FrameTag 高 16 位的语义）。
/// </summary>
/// <remarks>
/// <para>对应条款：<c>[F-FRAMETAG-STATEJOURNAL-BITLAYOUT]</c></para>
/// <para>计算：<c>ObjectKind = (ushort)(FrameTag.Value &gt;&gt; 16)</c></para>
/// </remarks>
public enum ObjectKind : ushort
{
    /// <summary>
    /// 保留值，MUST NOT 使用。
    /// </summary>
    Reserved = 0x0000,

    /// <summary>
    /// DurableDict（MVP 唯一实现）。
    /// </summary>
    Dict = 0x0001,
}

/// <summary>
/// StateJournal 层对 <see cref="FrameTag"/> 的位段解释。
/// </summary>
/// <remarks>
/// <para><b>[F-FRAMETAG-STATEJOURNAL-BITLAYOUT]</b>: StateJournal 按以下位段解释 <c>FrameTag.Value</c>:</para>
/// <list type="table">
///   <item>
///     <term>位 31..16</term>
///     <description>SubType（当 RecordType=ObjectVersion 时解释为 <see cref="ObjectKind"/>）</description>
///   </item>
///   <item>
///     <term>位 15..0</term>
///     <description>RecordType（Record 顶层类型）</description>
///   </item>
/// </list>
/// <para><b>端序</b>: Little-Endian (LE)，即低位字节在前（字节 0-1 = RecordType，字节 2-3 = SubType）。</para>
/// <para><b>计算公式</b>: <c>FrameTag = (SubType &lt;&lt; 16) | RecordType</c></para>
/// </remarks>
public static class StateJournalFrameTag
{
    // =========================================================================
    // 预定义常量
    // =========================================================================

    /// <summary>
    /// DurableDict 版本记录的 FrameTag。
    /// </summary>
    /// <remarks>
    /// <para>值：<c>0x00010001</c>（RecordType=ObjectVersion, ObjectKind=Dict）</para>
    /// <para>字节序列（LE）：<c>01 00 01 00</c></para>
    /// </remarks>
    public static readonly FrameTag DictVersion = new(0x00010001);

    /// <summary>
    /// 提交元数据记录的 FrameTag。
    /// </summary>
    /// <remarks>
    /// <para>值：<c>0x00000002</c>（RecordType=MetaCommit, SubType=0）</para>
    /// <para>字节序列（LE）：<c>02 00 00 00</c></para>
    /// </remarks>
    public static readonly FrameTag MetaCommit = new(0x00000002);

    // =========================================================================
    // 位段提取
    // =========================================================================

    /// <summary>
    /// 提取 RecordType（低 16 位）。
    /// </summary>
    /// <param name="tag">FrameTag。</param>
    /// <returns>RecordType 枚举值（可能是未定义的值）。</returns>
    public static RecordType GetRecordType(this FrameTag tag)
    {
        return (RecordType)(tag.Value & 0xFFFF);
    }

    /// <summary>
    /// 提取 SubType（高 16 位）。
    /// </summary>
    /// <param name="tag">FrameTag。</param>
    /// <returns>SubType 的原始值。</returns>
    public static ushort GetSubType(this FrameTag tag)
    {
        return (ushort)(tag.Value >> 16);
    }

    /// <summary>
    /// 提取 ObjectKind（高 16 位解释为 ObjectKind）。
    /// </summary>
    /// <param name="tag">FrameTag。</param>
    /// <returns>ObjectKind 枚举值（可能是未定义的值）。</returns>
    /// <remarks>
    /// <para>仅当 <c>GetRecordType(tag) == RecordType.ObjectVersion</c> 时才有意义。</para>
    /// <para>其他情况下 SubType 应为 0。</para>
    /// </remarks>
    public static ObjectKind GetObjectKind(this FrameTag tag)
    {
        return (ObjectKind)(tag.Value >> 16);
    }

    // =========================================================================
    // 构造
    // =========================================================================

    /// <summary>
    /// 创建 FrameTag。
    /// </summary>
    /// <param name="recordType">Record 顶层类型。</param>
    /// <param name="subType">SubType（默认为 0）。</param>
    /// <returns>FrameTag。</returns>
    /// <remarks>
    /// <para>计算公式：<c>FrameTag = (subType &lt;&lt; 16) | recordType</c></para>
    /// </remarks>
    public static FrameTag Create(RecordType recordType, ushort subType = 0)
    {
        uint value = ((uint)subType << 16) | (ushort)recordType;
        return new FrameTag(value);
    }

    /// <summary>
    /// 创建 ObjectVersion 类型的 FrameTag。
    /// </summary>
    /// <param name="kind">ObjectKind。</param>
    /// <returns>FrameTag。</returns>
    /// <remarks>
    /// <para>等价于 <c>Create(RecordType.ObjectVersion, (ushort)kind)</c></para>
    /// </remarks>
    public static FrameTag CreateObjectVersion(ObjectKind kind)
    {
        return Create(RecordType.ObjectVersion, (ushort)kind);
    }

    // =========================================================================
    // 验证与解析
    // =========================================================================

    /// <summary>
    /// 尝试解析并验证 FrameTag。
    /// </summary>
    /// <param name="tag">要解析的 FrameTag。</param>
    /// <returns>
    /// 成功时返回 (RecordType, ObjectKind?) 元组；
    /// 失败时返回相应的错误类型。
    /// </returns>
    /// <remarks>
    /// <para>验证规则：</para>
    /// <list type="bullet">
    ///   <item>RecordType == Reserved (0x0000) → <see cref="UnknownRecordTypeError"/></item>
    ///   <item>RecordType 未知（非 ObjectVersion/MetaCommit）→ <see cref="UnknownRecordTypeError"/></item>
    ///   <item>RecordType == ObjectVersion 且 ObjectKind == Reserved → <see cref="UnknownObjectKindError"/></item>
    ///   <item>RecordType != ObjectVersion 且 SubType != 0 → <see cref="InvalidSubTypeError"/></item>
    /// </list>
    /// </remarks>
    public static AteliaResult<(RecordType RecordType, ObjectKind? ObjectKind)> TryParse(FrameTag tag)
    {
        var recordType = tag.GetRecordType();
        var subType = tag.GetSubType();

        // 规则 1: RecordType == Reserved → UnknownRecordTypeError
        if (recordType == RecordType.Reserved)
        {
            return AteliaResult<(RecordType, ObjectKind?)>.Failure(
                new UnknownRecordTypeError(tag.Value, (ushort)recordType));
        }

        // 规则 2: RecordType 未知 → UnknownRecordTypeError
        if (recordType != RecordType.ObjectVersion && recordType != RecordType.MetaCommit)
        {
            return AteliaResult<(RecordType, ObjectKind?)>.Failure(
                new UnknownRecordTypeError(tag.Value, (ushort)recordType));
        }

        // RecordType == ObjectVersion 时
        if (recordType == RecordType.ObjectVersion)
        {
            var objectKind = (ObjectKind)subType;

            // 规则 3: ObjectKind == Reserved → UnknownObjectKindError
            if (objectKind == ObjectKind.Reserved)
            {
                return AteliaResult<(RecordType, ObjectKind?)>.Failure(
                    new UnknownObjectKindError(tag.Value, subType));
            }

            // 规则：ObjectKind 未知（非 Dict）→ UnknownObjectKindError
            // MVP 阶段只有 Dict
            if (objectKind != ObjectKind.Dict)
            {
                return AteliaResult<(RecordType, ObjectKind?)>.Failure(
                    new UnknownObjectKindError(tag.Value, subType));
            }

            return AteliaResult<(RecordType, ObjectKind?)>.Success((recordType, objectKind));
        }

        // RecordType != ObjectVersion 时（当前只有 MetaCommit）
        // 规则 4: SubType 必须为 0
        if (subType != 0)
        {
            return AteliaResult<(RecordType, ObjectKind?)>.Failure(
                new InvalidSubTypeError(tag.Value, (ushort)recordType, subType));
        }

        return AteliaResult<(RecordType, ObjectKind?)>.Success((recordType, null));
    }
}
