namespace Atelia.StateJournal.Internal;

internal enum VersionKind : uint {
    Blank = 0,
    Rebase = 1 << FrameTag.VersionKindShift,
    Delta = 2 << FrameTag.VersionKindShift,
}

internal enum UsageKind : uint {
    Blank = 0,
    UserPayload = 1 << FrameTag.UsageKindShift,

    /// <summary><see cref="LocalId"/> → <see cref="SizedPtr"/>, <see cref="DurableDict{uint, ulong}"/>。
    /// 是一个<see cref="Revision"/>最重要的内容之一。</summary>
    ObjectMap = 2 << FrameTag.UsageKindShift,
}

internal enum FrameSource : uint {
    Blank = 0,
    PrimaryCommit = 1 << FrameTag.FrameSourceShift,
    Compaction = 2 << FrameTag.FrameSourceShift,
}

internal readonly struct FrameTag(uint bits) {
    #region Constant
    public const int VersionKindShift = 0, VersionKindBits = 2;
    internal const uint VersionKindMask = ((1U << VersionKindBits) - 1) << VersionKindShift;

    public const int ObjectKindShift = VersionKindShift + VersionKindBits, ObjectKindBits = ValueBox.DurRefKindBitCount;

    public const int UsageKindShift = ObjectKindShift + ObjectKindBits, UsageKindBits = 4;
    internal const uint UsageKindMask = ((1U << UsageKindBits) - 1) << UsageKindShift;

    public const int FrameSourceShift = UsageKindShift + UsageKindBits, FrameSourceBits = 2;
    internal const uint FrameSourceMask = ((1U << FrameSourceBits) - 1) << FrameSourceShift;
    #endregion

    internal uint Bits => bits;

    internal VersionKind VersionKind => (VersionKind)(bits & VersionKindMask);
    internal DurableObjectKind ObjectKind => (DurableObjectKind)(bits >> ObjectKindShift) & DurableObjectKind.Mask;
    internal UsageKind UsageKind => (UsageKind)(bits & UsageKindMask);
    internal FrameSource FrameSource => (FrameSource)(bits & FrameSourceMask);

    internal FrameTag(UsageKind usageKind, DurableObjectKind objectKind, VersionKind versionKind, FrameSource frameSource)
        : this((uint)usageKind | ((uint)objectKind << ObjectKindShift) | (uint)versionKind | (uint)frameSource) { }

    internal SjCorruptionError? Validate(long offset, UsageKind? expectUsage = null, DurableObjectKind? expectObject = null) {
        if (VersionKind is not VersionKind.Rebase and not VersionKind.Delta) {
            return new SjCorruptionError(
                $"Invalid VersionKind in frame tag at offset {offset}: {VersionKind}.",
                RecoveryHint: "The frame metadata is malformed."
            );
        }
        if (ObjectKind is DurableObjectKind.Blank) {
            return new SjCorruptionError(
                $"Invalid ObjectKind in frame tag at offset {offset}: {ObjectKind}.",
                RecoveryHint: "The frame metadata is malformed."
            );
        }
        if (UsageKind is not UsageKind.UserPayload and not UsageKind.ObjectMap) {
            return new SjCorruptionError(
                $"Invalid UsageKind in frame tag at offset {offset}: {UsageKind}.",
                RecoveryHint: "The frame metadata is malformed."
            );
        }
        if (FrameSource is not FrameSource.PrimaryCommit and not FrameSource.Compaction) {
            return new SjCorruptionError(
                $"Invalid FrameSource in frame tag at offset {offset}: {FrameSource}.",
                RecoveryHint: "The frame metadata is malformed."
            );
        }
        if (expectUsage.HasValue && UsageKind != expectUsage.Value) {
            return new SjCorruptionError(
                $"Unexpected UsageKind at offset {offset}: expected {expectUsage.Value}, actual {UsageKind}.",
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
