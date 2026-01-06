// Source: Atelia.StateJournal - DataRecordWriter
// Spec: atelia/docs/StateJournal/mvp-design-v2.md §3.2.5

using System.Buffers;
using Atelia.Rbf;

namespace Atelia.StateJournal;

/// <summary>
/// Data 文件记录写入器。封装 IRbfFramer，提供语义化的写入 API。
/// </summary>
/// <remarks>
/// <para>用于向 Data RBF 文件写入 ObjectVersionRecord。</para>
/// <para><b>设计</b>：</para>
/// <list type="bullet">
///   <item>封装 RBF 层 framing 细节（FrameTag、Frame layout）</item>
///   <item>提供语义化 API：AppendDictVersion 写入 DurableDict 版本记录</item>
///   <item>返回 <deleted-place-holder> 作为版本指针（可用于后续 prevVersionPtr）</item>
/// </list>
/// </remarks>
public sealed class DataRecordWriter {
    private readonly IRbfFramer _framer;

    /// <summary>
    /// 创建 DataRecordWriter。
    /// </summary>
    /// <param name="framer">RBF 帧写入器。</param>
    public DataRecordWriter(IRbfFramer framer) {
        ArgumentNullException.ThrowIfNull(framer);
        _framer = framer;
    }

    /// <summary>
    /// 追加一条 DurableDict 版本记录。
    /// </summary>
    /// <param name="prevVersionPtr">指向上一个版本的指针（0 表示 Base Version）。</param>
    /// <param name="diffPayload">DiffPayload 数据。</param>
    /// <returns>写入的帧起始地址（可作为下一个版本的 prevVersionPtr）。</returns>
    /// <remarks>
    /// <para><b>FrameTag</b>: <see cref="FrameTags.DictVersion"/> (0x00010001)</para>
    /// <para><b>Payload 布局</b>: PrevVersionPtr(u64 LE) + DiffPayload(bytes)</para>
    /// </remarks>
    public <deleted-place-holder> AppendDictVersion(ulong prevVersionPtr, ReadOnlySpan<byte> diffPayload) {
        // 使用 BeginFrame 流式写入
        using var builder = _framer.BeginFrame(FrameTags.DictVersion);

        // 写入 ObjectVersionRecord payload
        ObjectVersionRecord.WriteTo(builder.Payload, prevVersionPtr, diffPayload);

        // 提交帧
        return builder.Commit();
    }

    /// <summary>
    /// 追加一条 DurableDict 版本记录（使用 ReadOnlyMemory）。
    /// </summary>
    /// <param name="prevVersionPtr">指向上一个版本的指针（0 表示 Base Version）。</param>
    /// <param name="diffPayload">DiffPayload 数据。</param>
    /// <returns>写入的帧起始地址。</returns>
    public <deleted-place-holder> AppendDictVersion(ulong prevVersionPtr, ReadOnlyMemory<byte> diffPayload) {
        return AppendDictVersion(prevVersionPtr, diffPayload.Span);
    }

    /// <summary>
    /// 将 RBF 缓冲数据推送到底层 Writer。
    /// </summary>
    public void Flush() {
        _framer.Flush();
    }
}
