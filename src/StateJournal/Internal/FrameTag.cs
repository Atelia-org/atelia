using System.Diagnostics;

namespace Atelia.StateJournal.Internal;

internal enum VersionKind : byte {
    Blank = 0,
    Rebase = 1,
    Delta = 2,
    Mask = (1 << FrameTag.VersionKindBits) - 1,
}

internal enum FrameUsage : byte {
    Blank = 0,
    UserPayload = 1,

    /// <summary><see cref="LocalId"/> → <see cref="SizedPtr"/>, <see cref="DurableDict{uint, ulong}"/>。
    /// 是一个<see cref="Revision"/>最重要的内容之一。</summary>
    ObjectMap = 2,
    Mask = (1 << FrameTag.UsageKindBits) - 1,
}

internal enum FrameSource : byte {
    Blank = 0,
    PrimaryCommit = 1,
    Compaction = 2,
    /// <summary>
    /// 写入到其他文件的完整快照（如 ExportTo / SaveAs）。
    /// 允许保留跨文件的逻辑祖先信息。
    /// </summary>
    CrossFileSnapshot = 3,
    Mask = (1 << FrameTag.FrameSourceBits) - 1,
}

internal readonly struct FrameTag(uint bits) {
    #region Constant
    public const int VersionKindShift = 0, VersionKindBits = 4;

    public const int ObjectKindShift = VersionKindShift + VersionKindBits, ObjectKindBits = ValueBox.DurRefKindBitCount;

    public const int UsageKindShift = ObjectKindShift + ObjectKindBits, UsageKindBits = 4;

    public const int FrameSourceShift = UsageKindShift + UsageKindBits, FrameSourceBits = 4;
    #endregion

    #region Static Helper
    internal static bool IsValidVersionKind(VersionKind value) => value is VersionKind.Rebase or VersionKind.Delta;
    internal static bool IsValidObjectKind(DurableObjectKind value) => value is not DurableObjectKind.Blank and not DurableObjectKind.Mask;
    internal static bool IsValidUsage(FrameUsage value) => value is FrameUsage.UserPayload or FrameUsage.ObjectMap;
    internal static bool IsValidSource(FrameSource value) => value is FrameSource.PrimaryCommit or FrameSource.Compaction or FrameSource.CrossFileSnapshot;
    #endregion

    internal uint Bits => bits;

    internal VersionKind VersionKind => (VersionKind)(bits >> VersionKindShift) & VersionKind.Mask;
    internal DurableObjectKind ObjectKind => (DurableObjectKind)(bits >> ObjectKindShift) & DurableObjectKind.Mask;
    internal FrameUsage Usage => (FrameUsage)(bits >> UsageKindShift) & FrameUsage.Mask;
    internal FrameSource Source => (FrameSource)(bits >> FrameSourceShift) & FrameSource.Mask;

    internal FrameTag(VersionKind versionKind, DurableObjectKind objectKind, FrameUsage usageKind, FrameSource frameSource)
        : this(((uint)versionKind << VersionKindShift) | ((uint)objectKind << ObjectKindShift) | ((uint)usageKind << UsageKindShift) | ((uint)frameSource << FrameSourceShift)) {
        Debug.Assert((byte)versionKind <= (byte)VersionKind.Mask, $"VersionKind {versionKind} exceeds {VersionKindBits}-bit field");
        Debug.Assert((byte)objectKind <= (byte)DurableObjectKind.Mask, $"ObjectKind {objectKind} exceeds {ObjectKindBits}-bit field");
        Debug.Assert((byte)usageKind <= (byte)FrameUsage.Mask, $"FrameUsage {usageKind} exceeds {UsageKindBits}-bit field");
        Debug.Assert((byte)frameSource <= (byte)FrameSource.Mask, $"FrameSource {frameSource} exceeds {FrameSourceBits}-bit field");
    }

    /// <summary>写入路径守门：确认所有字段均为已知的非 Blank 值。</summary>
    internal SjCorruptionError? ValidateComplete() {
        if (!IsValidVersionKind(VersionKind)) {
            return new SjCorruptionError(
                $"FrameTag has invalid VersionKind before write: {VersionKind}.",
                RecoveryHint: "WritePendingDiff must produce a Rebase or Delta VersionKind."
            );
        }
        if (!IsValidObjectKind(ObjectKind)) {
            return new SjCorruptionError(
                $"FrameTag has invalid ObjectKind before write: {ObjectKind}.",
                RecoveryHint: "WritePendingDiff must set a valid ObjectKind."
            );
        }
        if (!IsValidUsage(Usage)) {
            return new SjCorruptionError(
                $"FrameTag has invalid Usage before write: {Usage}.",
                RecoveryHint: "DiffWriteContext.FrameUsage must be UserPayload or ObjectMap."
            );
        }
        if (!IsValidSource(Source)) {
            return new SjCorruptionError(
                $"FrameTag has invalid Source before write: {Source}.",
                RecoveryHint: "DiffWriteContext.FrameSource must be PrimaryCommit, Compaction, or CrossFileSnapshot."
            );
        }
        return null;
    }

    /// <summary>读取路径校验：确认所有字段有效，可选匹配期望值。</summary>
    internal SjCorruptionError? Validate(long offset, FrameUsage? expectUsage = null, DurableObjectKind? expectObject = null) {
        if (!IsValidVersionKind(VersionKind)) {
            return new SjCorruptionError(
                $"Invalid VersionKind in frame tag at offset {offset}: {VersionKind}.",
                RecoveryHint: "The frame metadata is malformed."
            );
        }
        if (!IsValidObjectKind(ObjectKind)) {
            return new SjCorruptionError(
                $"Invalid ObjectKind in frame tag at offset {offset}: {ObjectKind}.",
                RecoveryHint: "The frame metadata is malformed."
            );
        }
        if (!IsValidUsage(Usage)) {
            return new SjCorruptionError(
                $"Invalid UsageKind in frame tag at offset {offset}: {Usage}.",
                RecoveryHint: "The frame metadata is malformed."
            );
        }
        if (!IsValidSource(Source)) {
            return new SjCorruptionError(
                $"Invalid FrameSource in frame tag at offset {offset}: {Source}.",
                RecoveryHint: "The frame metadata is malformed."
            );
        }
        if (expectUsage.HasValue && Usage != expectUsage.Value) {
            return new SjCorruptionError(
                $"Unexpected UsageKind at offset {offset}: expected {expectUsage.Value}, actual {Usage}.",
                RecoveryHint: "The provided ticket points to a different usage domain."
            );
        }
        if (expectObject.HasValue && ObjectKind != expectObject.Value) {
            return new SjCorruptionError(
                $"Unexpected ObjectKind at offset {offset}: expected {expectObject.Value}, actual {ObjectKind}.",
                RecoveryHint: "The provided ticket points to a different object type."
            );
        }
        return null;
    }
}
