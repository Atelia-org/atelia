using System.Diagnostics;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal;

/// <summary>VersionChain.Load 的返回结果。</summary>
internal readonly struct VersionChainLoadResult {
    internal readonly DurableObject Object;
    /// <summary>头帧（versionTicket 所指帧）内储存的 parentTicket 字段。
    /// 对于 ObjectMap frame chain，此值通常即为父 commit 的 ticket（= 父 CommitTicket）。
    /// 在 ExportTo/SaveAs 的跨文件 full rebase 场景下，它也可能只是“逻辑祖先”元数据，
    /// 指向另一个文件中的 commit。</summary>
    internal readonly SizedPtr HeadParentTicket;
    /// <summary>最新帧（version chain 头帧）的 tailMeta 数据。无 tailMeta 时为空数组。</summary>
    internal readonly byte[] HeadTailMeta;

    internal VersionChainLoadResult(DurableObject obj, SizedPtr headPreviousVersion, byte[] headTailMeta) {
        Object = obj;
        HeadParentTicket = headPreviousVersion;
        HeadTailMeta = headTailMeta;
    }
}

/// <summary>
/// <see cref="VersionChain.Write"/> 的返回结果，持有已落盘但尚未应用到内存的写入结果。
/// 调用 <see cref="Complete"/> 将落盘结果应用到对象内存状态。
/// </summary>
internal readonly struct PendingSave {
    private readonly DurableObject _obj;
    private readonly DiffWriteContext _context;

    public SizedPtr Ticket { get; }

    internal PendingSave(DurableObject obj, SizedPtr ticket, DiffWriteContext context) {
        _obj = obj;
        Ticket = ticket;
        _context = context;
    }

    /// <summary>将落盘结果应用到对象内存状态（更新版本链、提交 DictChangeTracker、设置 Clean）。</summary>
    public void Complete() => _obj.OnCommitSucceeded(Ticket, _context);
}

internal static class VersionChain {
    private static bool TryInferObjectKind(Type type, out DurableObjectKind kind) {
        if (type == typeof(DurableList)) {
            kind = DurableObjectKind.MixedList;
            return true;
        }

        if (!type.IsGenericType) {
            kind = default;
            return false;
        }

        var def = type.GetGenericTypeDefinition();
        if (def == typeof(DurableDict<,>)) {
            kind = DurableObjectKind.TypedDict;
            return true;
        }
        if (def == typeof(DurableDict<>)) {
            kind = DurableObjectKind.MixedDict;
            return true;
        }
        if (def == typeof(DurableList<>)) {
            kind = DurableObjectKind.TypedList;
            return true;
        }

        kind = default;
        return false;
    }

    /// <summary>将 DurableObject 的待写入 diff 序列化并追加到 RBF 文件中，同时立即应用内存状态变更。</summary>
    /// <remarks>
    /// 写入路径的失败来源：
    /// - EndAppend 返回 AteliaResult 失败（RBF 层验证/容量问题）→ 包装为 SjCorruptionError 透传。
    /// - WritePendingDiff 抛异常 → 编程错误，不捕获，直接传播。
    /// </remarks>
    internal static AteliaResult<SizedPtr> Save(
        DurableObject obj,
        IRbfFile file,
        DiffWriteContext context,
        ReadOnlySpan<byte> tailMeta = default
    ) {
        var writeResult = Write(obj, file, context, tailMeta);
        if (writeResult.IsFailure) { return writeResult.Error!; }
        writeResult.Value.Complete();
        return writeResult.Value.Ticket;
    }

    /// <summary>
    /// 二阶段提交 Phase A：将 DurableObject 的待写入 diff 序列化并追加到 RBF 文件中，
    /// 但不修改对象内存状态。调用方需在所有写入成功后调用 <see cref="PendingSave.Finalize"/> 完成 Phase B。
    /// </summary>
    internal static AteliaResult<PendingSave> Write(
        DurableObject obj,
        IRbfFile file,
        DiffWriteContext context,
        ReadOnlySpan<byte> tailMeta = default
    ) {
        context.AssertValid();
        if (!obj.HasChanges && !context.ForceRebase
            && !context.ForceSave && obj.IsTracked
        ) { return new PendingSave(obj, obj.HeadTicket, context); }
        using RbfFrameBuilder builder = file.BeginAppend();
        RbfPayloadWriter rbfWriter = builder.PayloadAndMeta;
        BinaryDiffWriter diffWriter = new(rbfWriter);
        FrameTag frameTag = obj.WritePendingDiff(diffWriter, ref context);
        if (frameTag.ValidateComplete() is { } tagError) { return tagError; }
        int tailMetaLength = tailMeta.Length;
        if (tailMetaLength > 0) {
            tailMeta.CopyTo(rbfWriter.GetSpan(tailMetaLength));
            rbfWriter.Advance(tailMetaLength);
        }
        AteliaResult<SizedPtr> appendResult = builder.EndAppend(tag: frameTag.Bits, tailMetaLength: tailMetaLength);
        if (appendResult.IsFailure) {
            return new SjCorruptionError(
                $"Failed to append version frame: {appendResult.Error!.Message}",
                Cause: appendResult.Error
            );
        }
        return new PendingSave(obj, appendResult.Value, context);
    }

    /// <summary>从 RBF 文件加载版本链，重建 DurableObject。</summary>
    internal static AteliaResult<DurableObject> Load(
        IRbfFile file,
        SizedPtr versionTicket,
        FrameUsage? expectUsage = null,
        DurableObjectKind? expectObject = null
    ) {
        var result = LoadFull(file, versionTicket, expectUsage, expectObject);
        if (result.IsFailure) { return result.Error!; }
        return result.Value.Object;
    }

    /// <summary>从 RBF 文件加载版本链，重建 DurableObject，同时返回头帧的 tailMeta。</summary>
    /// <remarks>
    /// 读取路径的失败来源：
    /// - ReadPooledFrame 返回 AteliaResult 失败 → 包装为 SjCorruptionError 透传。
    /// - BinaryDiffReader / ApplyDelta 抛 InvalidDataException → 由边界 catch 转换为 SjCorruptionError。
    /// - TypeCodec.TryDecode / DurableFactory.TryCreate 返回 false → SjTypeResolutionError。
    /// </remarks>
    internal static AteliaResult<VersionChainLoadResult> LoadFull(
        IRbfFile file,
        SizedPtr versionTicket,
        FrameUsage? expectUsage = null,
        DurableObjectKind? expectObject = null
    ) {
        Stack<(RbfPooledFrame Frame, int ConsumedCount, int TailMetaLength, SizedPtr ParentTicket)> deltaChain = new(256);
        HashSet<SizedPtr> visitedTickets = new();
        try {
            ReadOnlySpan<byte> typeCode;
            SizedPtr readTarget = versionTicket;
            DurableObjectKind? chainObjectKind = null;
            do {
                if (!visitedTickets.Add(readTarget)) {
                    return new SjCorruptionError(
                        $"Detected cycle in version chain at offset {readTarget.Offset}.",
                        RecoveryHint: "The version chain references itself. Re-save from a known-good snapshot."
                    );
                }
                AteliaResult<RbfPooledFrame> frameResult = file.ReadPooledFrame(readTarget);
                if (frameResult.IsFailure) {
                    return new SjCorruptionError(
                        $"Failed to read version frame at offset {readTarget.Offset}: {frameResult.Error!.Message}",
                        RecoveryHint: "The RBF file may be corrupted or the SizedPtr reference is stale.",
                        Cause: frameResult.Error
                    );
                }
                RbfPooledFrame frame = frameResult.Value!;
                FrameTag frameTag = new(frame.Tag);
                if (frameTag.Validate(readTarget.Offset, expectUsage, expectObject) is { } tagError) { return tagError; }
                if (chainObjectKind.HasValue && chainObjectKind.Value != frameTag.ObjectKind) {
                    return new SjCorruptionError(
                        $"ObjectKind mismatch in version chain at offset {readTarget.Offset}: expected {chainObjectKind.Value}, actual {frameTag.ObjectKind}.",
                        RecoveryHint: "The version chain mixes different object kinds."
                    );
                }
                chainObjectKind ??= frameTag.ObjectKind;

                // 切分 payload 和 tailMeta：PayloadAndMeta 的末尾 TailMetaLength 字节是 tailMeta
                ReadOnlySpan<byte> payloadAndMeta = frame.PayloadAndMeta;
                int tailMetaLen = frame.TailMetaLength;
                ReadOnlySpan<byte> payload = tailMetaLen > 0 ? payloadAndMeta[..^tailMetaLen] : payloadAndMeta;
                BinaryDiffReader reader = new(payload);
                typeCode = reader.ReadBytes();
                if (frameTag.VersionKind == VersionKind.Rebase && typeCode.IsEmpty) {
                    return new SjCorruptionError(
                        $"VersionKind mismatch at offset {readTarget.Offset}: Rebase frame cannot have empty type code.",
                        RecoveryHint: "The frame metadata and payload are inconsistent."
                    );
                }
                if (frameTag.VersionKind == VersionKind.Delta && !typeCode.IsEmpty) {
                    return new SjCorruptionError(
                        $"VersionKind mismatch at offset {readTarget.Offset}: Delta frame must have empty type code.",
                        RecoveryHint: "The frame metadata and payload are inconsistent."
                    );
                }
                SizedPtr parentTicket = SizedPtr.Deserialize(reader.BareUInt64(false));
                deltaChain.Push((frame, reader.ConsumedCount, tailMetaLen, parentTicket));
                readTarget = parentTicket;
                // 只有当前帧是 delta（typeCode 为空）时，才会在下一轮继续追 parentTicket。
                // 若头帧本身已是 rebase，则循环在本轮结束后停止；
                // 因此 rebase 头帧中记录的 parentTicket 可以安全地仅作为逻辑祖先信息存在，
                // 不要求当前 file 内一定存在对应的物理前驱帧。
            } while (typeCode.IsEmpty);
            Debug.Assert(!typeCode.IsEmpty);
            Debug.Assert(deltaChain.Count > 0);

            if (!TypeCodec.TryDecode(typeCode, out Type? targetType)) {
                return new SjTypeResolutionError(
                    "Failed to decode type code from version chain head frame.",
                    RecoveryHint: "The stored type code may be corrupted or from an incompatible version."
                );
            }

            if (!DurableFactory.TryCreate(targetType, out DurableObject? result)) {
                return new SjTypeResolutionError(
                    $"Cannot create DurableObject instance for resolved type '{targetType}'.",
                    RecoveryHint: "Ensure the type is registered with DurableFactory."
                );
            }
            Debug.Assert(
                !chainObjectKind.HasValue
                || !TryInferObjectKind(targetType, out DurableObjectKind decodedObjectKind)
                || chainObjectKind.Value == decodedObjectKind,
                $"ObjectKind mismatch between frame tag and decoded type: tag={chainObjectKind}, type={targetType}."
            );

            // deltaChain 是 Stack，底部是第一个被 Push 的（即 versionTicket 指向的头帧）。
            // 头帧的 ParentTicket 即为版本链的前驱 ticket（ObjectMap 场景下 = 父 CommitTicket）。
            byte[] headTailMeta = Array.Empty<byte>();
            SizedPtr headParentTicket = default;

            // 从栈顶（rebase 帧）开始，依次向更新的 delta 帧应用变更。
            // 使用 Peek+Pop 而非 TryPop：若 ApplyDelta 抛异常，
            // 当前帧仍在栈中，finally 会负责释放。
            while (deltaChain.Count > 0) {
                var version = deltaChain.Peek();
                ReadOnlySpan<byte> payloadAndMeta = version.Frame.PayloadAndMeta;
                int tmLen = version.TailMetaLength;
                ReadOnlySpan<byte> diffPayload = tmLen > 0 ? payloadAndMeta[version.ConsumedCount..^tmLen] : payloadAndMeta[version.ConsumedCount..];
                BinaryDiffReader reader = new(diffPayload);
                result.ApplyDelta(ref reader, version.ParentTicket);
                reader.EnsureFullyConsumed();
                // 最后弹出的帧是头帧（deltaChain 中最先 Push 的）：提取其 parentTicket 和 tailMeta
                if (deltaChain.Count == 1) {
                    headParentTicket = version.ParentTicket;
                    if (tmLen > 0) { headTailMeta = payloadAndMeta[^tmLen..].ToArray(); }
                }
                version.Frame.Dispose();
                deltaChain.Pop();
            }
            result.OnLoadCompleted(versionTicket);
            return new VersionChainLoadResult(result, headParentTicket, headTailMeta);
        }
        catch (InvalidDataException ex) {
            return new SjCorruptionError(
                $"Data corruption detected during version chain load: {ex.Message}",
                RecoveryHint: "The stored diff data may be damaged or truncated."
            );
        }
        finally {
            // 清理残留的未释放帧。
            // 成功路径：deltaChain 已在 while 循环中逐个 Pop+Dispose 清空。
            // 失败路径（early return 或异常）：可能有未释放的帧需要在此清理。
            while (deltaChain.TryPop(out var remaining)) {
                remaining.Frame.Dispose();
            }
        }
    }
}
