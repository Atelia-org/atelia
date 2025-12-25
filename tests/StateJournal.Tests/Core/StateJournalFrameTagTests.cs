using Atelia.Rbf;
using Atelia.StateJournal;
using FluentAssertions;
using Xunit;

namespace Atelia.StateJournal.Tests.Core;

/// <summary>
/// StateJournalFrameTag 位段编码测试。
/// </summary>
/// <remarks>
/// 对应条款：
/// <list type="bullet">
///   <item><c>[F-FRAMETAG-STATEJOURNAL-BITLAYOUT]</c></item>
///   <item><c>[F-FRAMETAG-SUBTYPE-ZERO-WHEN-NOT-OBJVER]</c></item>
/// </list>
/// </remarks>
public class StateJournalFrameTagTests
{
    #region 预定义常量测试

    /// <summary>
    /// [FRAMETAG-OK-DICTVERSION] DictVersion 常量值正确
    /// </summary>
    [Fact]
    public void DictVersion_HasCorrectValue()
    {
        StateJournalFrameTag.DictVersion.Value.Should().Be(0x00010001);
    }

    /// <summary>
    /// [FRAMETAG-OK-METACOMMIT] MetaCommit 常量值正确
    /// </summary>
    [Fact]
    public void MetaCommit_HasCorrectValue()
    {
        StateJournalFrameTag.MetaCommit.Value.Should().Be(0x00000002);
    }

    /// <summary>
    /// DictVersion 字节序列（LE）验证
    /// </summary>
    [Fact]
    public void DictVersion_ByteSequence_IsCorrect()
    {
        var bytes = BitConverter.GetBytes(StateJournalFrameTag.DictVersion.Value);
        
        // LE: 01 00 01 00
        bytes.Should().Equal(0x01, 0x00, 0x01, 0x00);
    }

    /// <summary>
    /// MetaCommit 字节序列（LE）验证
    /// </summary>
    [Fact]
    public void MetaCommit_ByteSequence_IsCorrect()
    {
        var bytes = BitConverter.GetBytes(StateJournalFrameTag.MetaCommit.Value);
        
        // LE: 02 00 00 00
        bytes.Should().Equal(0x02, 0x00, 0x00, 0x00);
    }

    #endregion

    #region 位段提取测试

    /// <summary>
    /// [FRAMETAG-OK-GETRECORDTYPE] GetRecordType 从 DictVersion 提取 ObjectVersion
    /// </summary>
    [Fact]
    public void GetRecordType_DictVersion_ReturnsObjectVersion()
    {
        var tag = new FrameTag(0x00010001);
        
        tag.GetRecordType().Should().Be(RecordType.ObjectVersion);
    }

    /// <summary>
    /// GetRecordType 从 MetaCommit 提取 MetaCommit
    /// </summary>
    [Fact]
    public void GetRecordType_MetaCommit_ReturnsMetaCommit()
    {
        var tag = new FrameTag(0x00000002);
        
        tag.GetRecordType().Should().Be(RecordType.MetaCommit);
    }

    /// <summary>
    /// GetRecordType 从 Reserved 提取 Reserved
    /// </summary>
    [Fact]
    public void GetRecordType_Reserved_ReturnsReserved()
    {
        var tag = new FrameTag(0x00000000);
        
        tag.GetRecordType().Should().Be(RecordType.Reserved);
    }

    /// <summary>
    /// [FRAMETAG-OK-GETSUBTYPE] GetSubType 从 DictVersion 提取 0x0001
    /// </summary>
    [Fact]
    public void GetSubType_DictVersion_Returns0x0001()
    {
        var tag = new FrameTag(0x00010001);
        
        tag.GetSubType().Should().Be(0x0001);
    }

    /// <summary>
    /// GetSubType 从 MetaCommit 提取 0x0000
    /// </summary>
    [Fact]
    public void GetSubType_MetaCommit_Returns0x0000()
    {
        var tag = new FrameTag(0x00000002);
        
        tag.GetSubType().Should().Be(0x0000);
    }

    /// <summary>
    /// GetSubType 边界测试：高 16 位全为 1
    /// </summary>
    [Fact]
    public void GetSubType_MaxValue_Returns0xFFFF()
    {
        var tag = new FrameTag(0xFFFF0001);
        
        tag.GetSubType().Should().Be(0xFFFF);
    }

    /// <summary>
    /// [FRAMETAG-OK-GETOBJECTKIND] GetObjectKind 从 DictVersion 提取 Dict
    /// </summary>
    [Fact]
    public void GetObjectKind_DictVersion_ReturnsDict()
    {
        var tag = new FrameTag(0x00010001);
        
        tag.GetObjectKind().Should().Be(ObjectKind.Dict);
    }

    /// <summary>
    /// GetObjectKind 从 MetaCommit 提取 Reserved（SubType=0）
    /// </summary>
    [Fact]
    public void GetObjectKind_MetaCommit_ReturnsReserved()
    {
        var tag = new FrameTag(0x00000002);
        
        tag.GetObjectKind().Should().Be(ObjectKind.Reserved);
    }

    #endregion

    #region 构造测试

    /// <summary>
    /// Create 构造 ObjectVersion + Dict
    /// </summary>
    [Fact]
    public void Create_ObjectVersion_Dict_MatchesDictVersion()
    {
        var tag = StateJournalFrameTag.Create(RecordType.ObjectVersion, (ushort)ObjectKind.Dict);
        
        tag.Should().Be(StateJournalFrameTag.DictVersion);
        tag.Value.Should().Be(0x00010001);
    }

    /// <summary>
    /// Create 构造 MetaCommit
    /// </summary>
    [Fact]
    public void Create_MetaCommit_MatchesMetaCommit()
    {
        var tag = StateJournalFrameTag.Create(RecordType.MetaCommit);
        
        tag.Should().Be(StateJournalFrameTag.MetaCommit);
        tag.Value.Should().Be(0x00000002);
    }

    /// <summary>
    /// Create 默认 SubType 为 0
    /// </summary>
    [Fact]
    public void Create_DefaultSubType_IsZero()
    {
        var tag = StateJournalFrameTag.Create(RecordType.MetaCommit);
        
        tag.GetSubType().Should().Be(0);
    }

    /// <summary>
    /// CreateObjectVersion 构造 Dict
    /// </summary>
    [Fact]
    public void CreateObjectVersion_Dict_MatchesDictVersion()
    {
        var tag = StateJournalFrameTag.CreateObjectVersion(ObjectKind.Dict);
        
        tag.Should().Be(StateJournalFrameTag.DictVersion);
    }

    /// <summary>
    /// Create 位计算公式验证：(SubType << 16) | RecordType
    /// </summary>
    [Theory]
    [InlineData(RecordType.ObjectVersion, 0x0001, 0x00010001)]
    [InlineData(RecordType.MetaCommit, 0x0000, 0x00000002)]
    [InlineData(RecordType.ObjectVersion, 0x007F, 0x007F0001)]
    [InlineData(RecordType.ObjectVersion, 0xFFFF, 0xFFFF0001)]
    public void Create_ComputesCorrectValue(RecordType recordType, ushort subType, uint expectedValue)
    {
        var tag = StateJournalFrameTag.Create(recordType, subType);
        
        tag.Value.Should().Be(expectedValue);
    }

    #endregion

    #region TryParse 成功测试

    /// <summary>
    /// TryParse 解析 DictVersion 成功
    /// </summary>
    [Fact]
    public void TryParse_DictVersion_Succeeds()
    {
        var result = StateJournalFrameTag.TryParse(StateJournalFrameTag.DictVersion);
        
        result.IsSuccess.Should().BeTrue();
        result.Value.RecordType.Should().Be(RecordType.ObjectVersion);
        result.Value.ObjectKind.Should().Be(ObjectKind.Dict);
    }

    /// <summary>
    /// TryParse 解析 MetaCommit 成功
    /// </summary>
    [Fact]
    public void TryParse_MetaCommit_Succeeds()
    {
        var result = StateJournalFrameTag.TryParse(StateJournalFrameTag.MetaCommit);
        
        result.IsSuccess.Should().BeTrue();
        result.Value.RecordType.Should().Be(RecordType.MetaCommit);
        result.Value.ObjectKind.Should().BeNull();
    }

    #endregion

    #region TryParse 失败测试 - Reserved RecordType

    /// <summary>
    /// [FRAMETAG-OK-RESERVED] TryParse Reserved RecordType 返回失败
    /// </summary>
    [Fact]
    public void TryParse_Reserved_ReturnsFailure()
    {
        var tag = new FrameTag(0x00000000);
        
        var result = StateJournalFrameTag.TryParse(tag);
        
        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<UnknownRecordTypeError>();
        
        var error = (UnknownRecordTypeError)result.Error!;
        error.FrameTagValue.Should().Be(0x00000000);
        error.RecordType.Should().Be(0x0000);
    }

    /// <summary>
    /// TryParse Reserved RecordType 带 SubType 也返回失败
    /// </summary>
    [Fact]
    public void TryParse_ReservedWithSubType_ReturnsFailure()
    {
        var tag = new FrameTag(0x00010000); // SubType=1, RecordType=Reserved
        
        var result = StateJournalFrameTag.TryParse(tag);
        
        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<UnknownRecordTypeError>();
    }

    #endregion

    #region TryParse 失败测试 - Unknown RecordType

    /// <summary>
    /// TryParse 未知 RecordType 返回失败
    /// </summary>
    [Theory]
    [InlineData(0x0003)]  // 未来标准扩展范围
    [InlineData(0x00FF)]
    [InlineData(0x7FFF)]  // 标准扩展范围上限
    [InlineData(0x8000)]  // 实验/私有扩展范围
    [InlineData(0xFFFF)]
    public void TryParse_UnknownRecordType_ReturnsFailure(ushort recordType)
    {
        var tag = new FrameTag((uint)recordType);
        
        var result = StateJournalFrameTag.TryParse(tag);
        
        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<UnknownRecordTypeError>();
        
        var error = (UnknownRecordTypeError)result.Error!;
        error.RecordType.Should().Be(recordType);
    }

    #endregion

    #region TryParse 失败测试 - Unknown ObjectKind

    /// <summary>
    /// TryParse ObjectVersion + Reserved ObjectKind 返回失败
    /// </summary>
    [Fact]
    public void TryParse_ObjectVersion_ReservedObjectKind_ReturnsFailure()
    {
        var tag = new FrameTag(0x00000001); // RecordType=ObjectVersion, ObjectKind=Reserved
        
        var result = StateJournalFrameTag.TryParse(tag);
        
        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<UnknownObjectKindError>();
        
        var error = (UnknownObjectKindError)result.Error!;
        error.FrameTagValue.Should().Be(0x00000001);
        error.ObjectKind.Should().Be(0x0000);
    }

    /// <summary>
    /// TryParse ObjectVersion + Unknown ObjectKind 返回失败
    /// </summary>
    [Theory]
    [InlineData(0x0002)]  // 未来标准类型
    [InlineData(0x007F)]  // 标准类型上限
    [InlineData(0x0080)]  // 实验/私有类型
    [InlineData(0xFFFF)]
    public void TryParse_ObjectVersion_UnknownObjectKind_ReturnsFailure(ushort objectKind)
    {
        var tag = StateJournalFrameTag.Create(RecordType.ObjectVersion, objectKind);
        
        var result = StateJournalFrameTag.TryParse(tag);
        
        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<UnknownObjectKindError>();
        
        var error = (UnknownObjectKindError)result.Error!;
        error.ObjectKind.Should().Be(objectKind);
    }

    #endregion

    #region TryParse 失败测试 - Invalid SubType

    /// <summary>
    /// [F-FRAMETAG-SUBTYPE-ZERO-WHEN-NOT-OBJVER] MetaCommit + 非零 SubType 返回失败
    /// </summary>
    [Theory]
    [InlineData(0x0001)]
    [InlineData(0x00FF)]
    [InlineData(0xFFFF)]
    public void TryParse_MetaCommit_NonZeroSubType_ReturnsFailure(ushort subType)
    {
        var tag = StateJournalFrameTag.Create(RecordType.MetaCommit, subType);
        
        var result = StateJournalFrameTag.TryParse(tag);
        
        result.IsFailure.Should().BeTrue();
        result.Error.Should().BeOfType<InvalidSubTypeError>();
        
        var error = (InvalidSubTypeError)result.Error!;
        error.RecordType.Should().Be((ushort)RecordType.MetaCommit);
        error.SubType.Should().Be(subType);
    }

    #endregion

    #region 枚举值测试

    /// <summary>
    /// RecordType 枚举值正确
    /// </summary>
    [Fact]
    public void RecordType_Values_AreCorrect()
    {
        ((ushort)RecordType.Reserved).Should().Be(0x0000);
        ((ushort)RecordType.ObjectVersion).Should().Be(0x0001);
        ((ushort)RecordType.MetaCommit).Should().Be(0x0002);
    }

    /// <summary>
    /// ObjectKind 枚举值正确
    /// </summary>
    [Fact]
    public void ObjectKind_Values_AreCorrect()
    {
        ((ushort)ObjectKind.Reserved).Should().Be(0x0000);
        ((ushort)ObjectKind.Dict).Should().Be(0x0001);
    }

    #endregion

    #region 错误类型测试

    /// <summary>
    /// UnknownRecordTypeError 有正确的错误码
    /// </summary>
    [Fact]
    public void UnknownRecordTypeError_HasCorrectErrorCode()
    {
        var error = new UnknownRecordTypeError(0x00000000, 0x0000);
        
        error.ErrorCode.Should().Be("StateJournal.FrameTag.UnknownRecordType");
    }

    /// <summary>
    /// UnknownObjectKindError 有正确的错误码
    /// </summary>
    [Fact]
    public void UnknownObjectKindError_HasCorrectErrorCode()
    {
        var error = new UnknownObjectKindError(0x00000001, 0x0000);
        
        error.ErrorCode.Should().Be("StateJournal.FrameTag.UnknownObjectKind");
    }

    /// <summary>
    /// InvalidSubTypeError 有正确的错误码
    /// </summary>
    [Fact]
    public void InvalidSubTypeError_HasCorrectErrorCode()
    {
        var error = new InvalidSubTypeError(0x00010002, 0x0002, 0x0001);
        
        error.ErrorCode.Should().Be("StateJournal.FrameTag.InvalidSubType");
    }

    #endregion

    #region 往返测试

    /// <summary>
    /// Create → GetRecordType/GetSubType 往返
    /// </summary>
    [Theory]
    [InlineData(RecordType.ObjectVersion, 0x0001)]
    [InlineData(RecordType.MetaCommit, 0x0000)]
    public void Create_Roundtrip_ExtractCorrectValues(RecordType recordType, ushort subType)
    {
        var tag = StateJournalFrameTag.Create(recordType, subType);
        
        tag.GetRecordType().Should().Be(recordType);
        tag.GetSubType().Should().Be(subType);
    }

    /// <summary>
    /// CreateObjectVersion → GetRecordType/GetObjectKind 往返
    /// </summary>
    [Fact]
    public void CreateObjectVersion_Roundtrip_ExtractCorrectValues()
    {
        var tag = StateJournalFrameTag.CreateObjectVersion(ObjectKind.Dict);
        
        tag.GetRecordType().Should().Be(RecordType.ObjectVersion);
        tag.GetObjectKind().Should().Be(ObjectKind.Dict);
    }

    #endregion

    #region MVP 完整取值表测试

    /// <summary>
    /// MVP 完整取值表验证
    /// </summary>
    [Theory]
    [InlineData(0x00010001u, RecordType.ObjectVersion, ObjectKind.Dict, "DurableDict 版本记录")]
    [InlineData(0x00000002u, RecordType.MetaCommit, null, "提交元数据记录")]
    public void MVP_FrameTagValues_AreCorrect(uint frameTagValue, RecordType expectedRecordType, ObjectKind? expectedObjectKind, string description)
    {
        _ = description; // 仅用于测试可读性
        
        var tag = new FrameTag(frameTagValue);
        var result = StateJournalFrameTag.TryParse(tag);
        
        result.IsSuccess.Should().BeTrue($"FrameTag 0x{frameTagValue:X8} 应当能成功解析");
        result.Value.RecordType.Should().Be(expectedRecordType);
        result.Value.ObjectKind.Should().Be(expectedObjectKind);
    }

    #endregion
}
