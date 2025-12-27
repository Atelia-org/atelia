// Source: Atelia.StateJournal - ObjectVersionRecord Payload Layout
// Spec: atelia/docs/StateJournal/mvp-design-v2.md §3.2.5

using System.Buffers;
using System.Buffers.Binary;

namespace Atelia.StateJournal;

/// <summary>
/// ObjectVersionRecord 的 payload 布局。
/// </summary>
/// <remarks>
/// <para><b>[F-OBJVER-PAYLOAD-MINLEN]</b>: Payload 至少 8 字节（PrevVersionPtr）。</para>
/// 
/// <para><b>Payload 布局</b>：</para>
/// <list type="table">
///   <item><term>偏移 0</term><description>PrevVersionPtr（u64 LE, 8 bytes）</description></item>
///   <item><term>偏移 8</term><description>DiffPayload（剩余全部字节）</description></item>
/// </list>
/// 
/// <para><b>语义</b>：</para>
/// <list type="bullet">
///   <item><c>PrevVersionPtr=0</c>：Base Version（Genesis Base 或 Checkpoint Base）</item>
///   <item><c>PrevVersionPtr≠0</c>：指向同一 ObjectId 的上一个版本记录</item>
/// </list>
/// </remarks>
public static class ObjectVersionRecord {
    /// <summary>
    /// PrevVersionPtr 的字节长度（u64 LE，固定 8 字节）。
    /// </summary>
    public const int PrevVersionPtrSize = 8;

    /// <summary>
    /// ObjectVersionRecord payload 的最小长度（PrevVersionPtr）。
    /// </summary>
    /// <remarks>
    /// <para>对应条款：<c>[F-OBJVER-PAYLOAD-MINLEN]</c></para>
    /// </remarks>
    public const int MinPayloadLength = PrevVersionPtrSize;

    /// <summary>
    /// 表示 Base Version 的 PrevVersionPtr 值（Genesis Base 或 Checkpoint Base）。
    /// </summary>
    public const ulong NullPrevVersionPtr = 0;

    // =========================================================================
    // Encode API
    // =========================================================================

    /// <summary>
    /// 将 ObjectVersionRecord payload 写入 writer。
    /// </summary>
    /// <param name="writer">目标缓冲区写入器。</param>
    /// <param name="prevVersionPtr">指向上一个版本的指针（0 表示 Base Version）。</param>
    /// <param name="diffPayload">DiffPayload 数据。</param>
    /// <remarks>
    /// <para>输出格式：PrevVersionPtr(u64 LE) + DiffPayload(bytes)</para>
    /// </remarks>
    public static void WriteTo(IBufferWriter<byte> writer, ulong prevVersionPtr, ReadOnlySpan<byte> diffPayload) {
        ArgumentNullException.ThrowIfNull(writer);

        // 写入 PrevVersionPtr (8 bytes, little-endian)
        var ptrSpan = writer.GetSpan(PrevVersionPtrSize);
        BinaryPrimitives.WriteUInt64LittleEndian(ptrSpan, prevVersionPtr);
        writer.Advance(PrevVersionPtrSize);

        // 写入 DiffPayload
        if (diffPayload.Length > 0) {
            var payloadSpan = writer.GetSpan(diffPayload.Length);
            diffPayload.CopyTo(payloadSpan);
            writer.Advance(diffPayload.Length);
        }
    }

    /// <summary>
    /// 将 ObjectVersionRecord payload 写入 writer（使用 ReadOnlyMemory）。
    /// </summary>
    /// <param name="writer">目标缓冲区写入器。</param>
    /// <param name="prevVersionPtr">指向上一个版本的指针（0 表示 Base Version）。</param>
    /// <param name="diffPayload">DiffPayload 数据。</param>
    public static void WriteTo(IBufferWriter<byte> writer, ulong prevVersionPtr, ReadOnlyMemory<byte> diffPayload) {
        WriteTo(writer, prevVersionPtr, diffPayload.Span);
    }

    /// <summary>
    /// 计算 ObjectVersionRecord payload 的总长度。
    /// </summary>
    /// <param name="diffPayloadLength">DiffPayload 的长度。</param>
    /// <returns>总 payload 长度。</returns>
    public static int GetPayloadLength(int diffPayloadLength) {
        return PrevVersionPtrSize + diffPayloadLength;
    }

    // =========================================================================
    // Decode API
    // =========================================================================

    /// <summary>
    /// 尝试解析 ObjectVersionRecord payload。
    /// </summary>
    /// <param name="payload">原始 payload 数据。</param>
    /// <param name="prevVersionPtr">输出的 PrevVersionPtr。</param>
    /// <param name="diffPayload">输出的 DiffPayload 切片（不复制数据）。</param>
    /// <returns>成功时返回 true；失败时返回错误。</returns>
    /// <remarks>
    /// <para>对应条款：<c>[F-OBJVER-PAYLOAD-MINLEN]</c></para>
    /// </remarks>
    public static AteliaResult<bool> TryParse(
        ReadOnlySpan<byte> payload,
        out ulong prevVersionPtr,
        out ReadOnlySpan<byte> diffPayload
    ) {
        prevVersionPtr = 0;
        diffPayload = ReadOnlySpan<byte>.Empty;

        // 检查最小长度
        if (payload.Length < MinPayloadLength) {
            return AteliaResult<bool>.Failure(
                new ObjectVersionRecordTruncatedError(payload.Length, MinPayloadLength)
            );
        }

        // 读取 PrevVersionPtr (u64 LE)
        prevVersionPtr = BinaryPrimitives.ReadUInt64LittleEndian(payload);

        // 剩余部分为 DiffPayload
        diffPayload = payload[PrevVersionPtrSize..];

        return AteliaResult<bool>.Success(true);
    }

    /// <summary>
    /// 尝试解析 ObjectVersionRecord payload（返回结构化结果）。
    /// </summary>
    /// <param name="payload">原始 payload 数据。</param>
    /// <returns>成功时返回 ParsedRecord；失败时返回错误。</returns>
    public static AteliaResult<ParsedObjectVersionRecord> TryParse(ReadOnlyMemory<byte> payload) {
        var result = TryParse(payload.Span, out var prevVersionPtr, out var diffPayloadSpan);
        if (result.IsFailure) { return AteliaResult<ParsedObjectVersionRecord>.Failure(result.Error!); }

        // 计算 DiffPayload 在 payload 中的偏移量，返回 Memory 切片
        var diffPayload = payload[PrevVersionPtrSize..];

        return AteliaResult<ParsedObjectVersionRecord>.Success(
            new ParsedObjectVersionRecord(prevVersionPtr, diffPayload)
        );
    }

    /// <summary>
    /// 检查 PrevVersionPtr 是否表示 Base Version（Genesis Base 或 Checkpoint Base）。
    /// </summary>
    /// <param name="prevVersionPtr">PrevVersionPtr 值。</param>
    /// <returns>如果是 Base Version，返回 true。</returns>
    public static bool IsBaseVersion(ulong prevVersionPtr) {
        return prevVersionPtr == NullPrevVersionPtr;
    }
}

/// <summary>
/// 解析后的 ObjectVersionRecord 结构。
/// </summary>
/// <param name="PrevVersionPtr">指向上一个版本的指针（0 表示 Base Version）。</param>
/// <param name="DiffPayload">DiffPayload 数据（Memory 切片，不复制）。</param>
public readonly record struct ParsedObjectVersionRecord(
    ulong PrevVersionPtr,
    ReadOnlyMemory<byte> DiffPayload) {
    /// <summary>
    /// 是否为 Base Version（Genesis Base 或 Checkpoint Base）。
    /// </summary>
    public bool IsBaseVersion => ObjectVersionRecord.IsBaseVersion(PrevVersionPtr);
}

/// <summary>
/// ObjectVersionRecord payload 截断错误。
/// </summary>
/// <param name="ActualLength">实际 payload 长度。</param>
/// <param name="MinLength">最小要求长度。</param>
public record ObjectVersionRecordTruncatedError(int ActualLength, int MinLength)
    : AteliaError(
        ErrorCode: "StateJournal.ObjectVersionRecordTruncated",
        Message: $"ObjectVersionRecord payload truncated: got {ActualLength} bytes, need at least {MinLength}.",
        RecoveryHint: "The record may be corrupted or incomplete. Check the data file integrity."
    );
