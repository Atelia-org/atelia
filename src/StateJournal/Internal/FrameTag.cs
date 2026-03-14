namespace Atelia.StateJournal.Internal;

internal enum VersionKind : uint {
    Blank = 0,
    Rebase = 1 << FrameTag.VersionKindShift,
    Delta = 2 << FrameTag.VersionKindShift,
}

internal enum UsageKind : uint {
    Blank = 0,
    UserPayload = 1 << FrameTag.UsageKindShift,

    /// <summary>LocalId -&gt; SizedPtr, DurableDict&lt;uint, ulong&gt;。
    /// 是一个Epoch最重要的内容之一。</summary>
    VersionTable = 2 << FrameTag.UsageKindShift,
}

internal readonly struct FrameTag(uint bits) {
    #region Constant
    public const int VersionKindShift = 0, VersionKindBits = 2;
    internal const uint VersionKindMask = ((1U << VersionKindBits) - 1) << VersionKindShift;

    public const int ObjectKindShift = VersionKindShift + VersionKindBits, ObjectKindBits = ValueBox.DurRefKindBitCount;

    public const int UsageKindShift = ObjectKindShift + ObjectKindBits, UsageKindBits = 4;
    internal const uint UsageKindMask = ((1U << UsageKindBits) - 1) << UsageKindShift;
    #endregion

    internal uint Bits => bits;

    internal VersionKind VersionKind => (VersionKind)(bits & VersionKindMask);
    internal DurableObjectKind ObjectKind => (DurableObjectKind)(bits >> ObjectKindShift) & DurableObjectKind.Mask;
    internal UsageKind UsageKind => (UsageKind)(bits & UsageKindMask);

    internal FrameTag(UsageKind usageKind, DurableObjectKind objectKind, VersionKind versionKind)
        : this((uint)usageKind | ((uint)objectKind << ObjectKindShift) | (uint)versionKind) { }

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
        if (UsageKind is not UsageKind.Blank and not UsageKind.UserPayload and not UsageKind.VersionTable) {
            return new SjCorruptionError(
                $"Invalid UsageKind in frame tag at offset {offset}: {UsageKind}.",
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
