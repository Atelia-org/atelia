using System.Diagnostics;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal;

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

    /// <summary>将 DurableObject 的待写入 diff 序列化并追加到 RBF 文件中。</summary>
    /// <remarks>
    /// 写入路径的失败来源：
    /// - EndAppend 返回 AteliaResult 失败（RBF 层验证/容量问题）→ 包装为 SjCorruptionError 透传。
    /// - WritePendingDiff 抛异常 → 编程错误，不捕获，直接传播。
    /// </remarks>
    internal static AteliaResult<SizedPtr> Save(DurableObject obj, IRbfFile file, bool forceRebase = false) {
        DiffWriteContext context = new() { ForceRebase = forceRebase };
        if (!obj.HasChanges && !forceRebase && obj.IsTracked) { return obj.HeadTicket; }
        using RbfFrameBuilder builder = file.BeginAppend();
        RbfPayloadWriter rbfWriter = builder.PayloadAndMeta;
        BinaryDiffWriter diffWriter = new(rbfWriter);
        FrameTag frameTag = obj.WritePendingDiff(diffWriter, context);
        AteliaResult<SizedPtr> appendResult = builder.EndAppend(tag: frameTag.Bits);
        if (appendResult.IsFailure) {
            return new SjCorruptionError(
                $"Failed to append version frame: {appendResult.Error!.Message}",
                Cause: appendResult.Error
            );
        }
        SizedPtr versionTicket = appendResult.Value;
        obj.OnCommitSucceeded(versionTicket, context);
        return versionTicket;
    }

    /// <summary>从 RBF 文件加载版本链，重建 DurableObject。</summary>
    /// <remarks>
    /// 读取路径的失败来源：
    /// - ReadPooledFrame 返回 AteliaResult 失败 → 包装为 SjCorruptionError 透传。
    /// - BinaryDiffReader / ApplyDelta 抛 InvalidDataException → 由边界 catch 转换为 SjCorruptionError。
    /// - TypeCodec.TryDecode / DurableFactory.TryCreate 返回 false → SjTypeResolutionError。
    /// </remarks>
    internal static AteliaResult<DurableObject> Load(
        IRbfFile file,
        SizedPtr versionTicket,
        UsageKind? expectUsage = null,
        DurableObjectKind? expectObject = null
    ) {
        Stack<(RbfPooledFrame Frame, int ConsumedCount, SizedPtr ParentTicket)> deltaChain = new(256);
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

                ReadOnlySpan<byte> payload = frame.PayloadAndMeta;
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
                deltaChain.Push((frame, reader.ConsumedCount, parentTicket));
                readTarget = parentTicket;
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

            // 从栈顶（rebase 帧）开始，依次向更新的 delta 帧应用变更。
            // 使用 Peek+Pop 而非 TryPop：若 ApplyDelta 抛异常，
            // 当前帧仍在栈中，finally 会负责释放。
            while (deltaChain.Count > 0) {
                var version = deltaChain.Peek();
                BinaryDiffReader reader = new(version.Frame.PayloadAndMeta[version.ConsumedCount..]);
                result.ApplyDelta(ref reader, version.ParentTicket);
                reader.EnsureFullyConsumed();
                version.Frame.Dispose();
                deltaChain.Pop();
            }
            result.OnLoadCompleted(versionTicket);
            return result;
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
