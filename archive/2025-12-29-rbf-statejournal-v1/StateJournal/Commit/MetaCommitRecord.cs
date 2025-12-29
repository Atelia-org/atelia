// Source: Atelia.StateJournal - Meta Commit Record
// Spec: atelia/docs/StateJournal/mvp-design-v2.md §META-COMMIT-RECORD

using System.Buffers;
using System.Buffers.Binary;

namespace Atelia.StateJournal;

/// <summary>
/// Meta Commit Record：提交点的元数据记录。
/// </summary>
/// <remarks>
/// <para>
/// 每次 Commit 都会在 meta file 追加一条 MetaCommitRecord。
/// </para>
/// <para>
/// 格式：EpochSeq(varuint) + RootObjectId(varuint) + VersionIndexPtr(u64 LE) + DataTail(u64 LE) + NextObjectId(varuint)
/// </para>
/// <para>
/// 对应条款：<c>[F-META-COMMIT-RECORD]</c>
/// </para>
/// </remarks>
public readonly struct MetaCommitRecord : IEquatable<MetaCommitRecord> {
    /// <summary>
    /// Epoch 序号（单调递增）。
    /// </summary>
    public ulong EpochSeq { get; init; }

    /// <summary>
    /// 根对象 ID。
    /// </summary>
    public ulong RootObjectId { get; init; }

    /// <summary>
    /// VersionIndex 的版本指针（在 data file 中的位置）。
    /// </summary>
    public ulong VersionIndexPtr { get; init; }

    /// <summary>
    /// Data file 的有效尾部（包含尾部分隔符）。
    /// </summary>
    public ulong DataTail { get; init; }

    /// <summary>
    /// 下一个可分配的 ObjectId。
    /// </summary>
    public ulong NextObjectId { get; init; }

    /// <inheritdoc/>
    public bool Equals(MetaCommitRecord other) =>
        EpochSeq == other.EpochSeq &&
        RootObjectId == other.RootObjectId &&
        VersionIndexPtr == other.VersionIndexPtr &&
        DataTail == other.DataTail &&
        NextObjectId == other.NextObjectId;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is MetaCommitRecord other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(EpochSeq, RootObjectId, VersionIndexPtr, DataTail, NextObjectId);

    /// <summary>
    /// 判断两个 MetaCommitRecord 是否相等。
    /// </summary>
    public static bool operator ==(MetaCommitRecord left, MetaCommitRecord right) => left.Equals(right);

    /// <summary>
    /// 判断两个 MetaCommitRecord 是否不相等。
    /// </summary>
    public static bool operator !=(MetaCommitRecord left, MetaCommitRecord right) => !left.Equals(right);
}

/// <summary>
/// MetaCommitRecord 的序列化/反序列化。
/// </summary>
/// <remarks>
/// <para>
/// 格式：EpochSeq(varuint) + RootObjectId(varuint) + VersionIndexPtr(u64 LE) + DataTail(u64 LE) + NextObjectId(varuint)
/// </para>
/// </remarks>
public static class MetaCommitRecordSerializer {
    /// <summary>
    /// 序列化 MetaCommitRecord 到 buffer。
    /// </summary>
    /// <param name="writer">目标缓冲区写入器。</param>
    /// <param name="record">要序列化的记录。</param>
    /// <remarks>
    /// 格式：EpochSeq(varuint) + RootObjectId(varuint) + VersionIndexPtr(u64 LE) + DataTail(u64 LE) + NextObjectId(varuint)
    /// </remarks>
    public static void Write(IBufferWriter<byte> writer, in MetaCommitRecord record) {
        // 写入所需的临时栈缓冲区
        Span<byte> varIntBuffer = stackalloc byte[VarInt.MaxVarUInt64Bytes];

        // EpochSeq (varuint)
        int epochLen = VarInt.WriteVarUInt(varIntBuffer, record.EpochSeq);
        var epochSpan = writer.GetSpan(epochLen);
        varIntBuffer[..epochLen].CopyTo(epochSpan);
        writer.Advance(epochLen);

        // RootObjectId (varuint)
        int rootLen = VarInt.WriteVarUInt(varIntBuffer, record.RootObjectId);
        var rootSpan = writer.GetSpan(rootLen);
        varIntBuffer[..rootLen].CopyTo(rootSpan);
        writer.Advance(rootLen);

        // VersionIndexPtr (u64 LE)
        var ptrSpan = writer.GetSpan(8);
        BinaryPrimitives.WriteUInt64LittleEndian(ptrSpan, record.VersionIndexPtr);
        writer.Advance(8);

        // DataTail (u64 LE)
        var tailSpan = writer.GetSpan(8);
        BinaryPrimitives.WriteUInt64LittleEndian(tailSpan, record.DataTail);
        writer.Advance(8);

        // NextObjectId (varuint)
        int nextIdLen = VarInt.WriteVarUInt(varIntBuffer, record.NextObjectId);
        var nextIdSpan = writer.GetSpan(nextIdLen);
        varIntBuffer[..nextIdLen].CopyTo(nextIdSpan);
        writer.Advance(nextIdLen);
    }

    /// <summary>
    /// 反序列化 MetaCommitRecord。
    /// </summary>
    /// <param name="payload">序列化的 payload 数据。</param>
    /// <returns>成功时返回 MetaCommitRecord；失败时返回错误。</returns>
    public static AteliaResult<MetaCommitRecord> TryRead(ReadOnlySpan<byte> payload) {
        var reader = payload;

        // EpochSeq
        var epochResult = VarInt.TryReadVarUInt(reader);
        if (epochResult.IsFailure) {
            return AteliaResult<MetaCommitRecord>.Failure(
                new MetaCommitRecordTruncatedError("EpochSeq", epochResult.Error!)
            );
        }
        reader = reader[epochResult.Value.BytesConsumed..];

        // RootObjectId
        var rootResult = VarInt.TryReadVarUInt(reader);
        if (rootResult.IsFailure) {
            return AteliaResult<MetaCommitRecord>.Failure(
                new MetaCommitRecordTruncatedError("RootObjectId", rootResult.Error!)
            );
        }
        reader = reader[rootResult.Value.BytesConsumed..];

        // VersionIndexPtr (8 bytes)
        if (reader.Length < 8) {
            return AteliaResult<MetaCommitRecord>.Failure(
                new MetaCommitRecordTruncatedError("VersionIndexPtr")
            );
        }
        var versionIndexPtr = BinaryPrimitives.ReadUInt64LittleEndian(reader);
        reader = reader[8..];

        // DataTail (8 bytes)
        if (reader.Length < 8) {
            return AteliaResult<MetaCommitRecord>.Failure(
                new MetaCommitRecordTruncatedError("DataTail")
            );
        }
        var dataTail = BinaryPrimitives.ReadUInt64LittleEndian(reader);
        reader = reader[8..];

        // NextObjectId
        var nextIdResult = VarInt.TryReadVarUInt(reader);
        if (nextIdResult.IsFailure) {
            return AteliaResult<MetaCommitRecord>.Failure(
                new MetaCommitRecordTruncatedError("NextObjectId", nextIdResult.Error!)
            );
        }

        return AteliaResult<MetaCommitRecord>.Success(
            new MetaCommitRecord {
                EpochSeq = epochResult.Value.Value,
                RootObjectId = rootResult.Value.Value,
                VersionIndexPtr = versionIndexPtr,
                DataTail = dataTail,
                NextObjectId = nextIdResult.Value.Value,
            }
        );
    }

    /// <summary>
    /// 计算序列化后的大小。
    /// </summary>
    /// <param name="record">要计算大小的记录。</param>
    /// <returns>序列化后的字节数。</returns>
    public static int GetSerializedSize(in MetaCommitRecord record) {
        return VarInt.GetVarUIntLength(record.EpochSeq)
            + VarInt.GetVarUIntLength(record.RootObjectId)
            + 8  // VersionIndexPtr
            + 8  // DataTail
            + VarInt.GetVarUIntLength(record.NextObjectId);
    }
}
