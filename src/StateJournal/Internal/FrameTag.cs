namespace Atelia.StateJournal.Internal;

internal struct FrameTagConstant {
    public const int VersionKindShift = 0, VersionKindBits = 2;
    internal const uint VersionKindMask = ((1U << VersionKindBits) - 1) << VersionKindShift;

    public const int ObjectKindShift = VersionKindShift + VersionKindBits, ObjectKindBits = 4;
    internal const uint ObjectKindMask = ((1U << ObjectKindBits) - 1) << ObjectKindShift;

    public const int UsageKindShift = ObjectKindShift + ObjectKindBits, UsageKindBits = 4;
    internal const uint UsageKindMask = ((1U << UsageKindBits) - 1) << UsageKindShift;
}

internal enum VersionKind : uint {
    Blank = 0,
    Rebase = 1 << FrameTagConstant.VersionKindShift,
    Delta = 2 << FrameTagConstant.VersionKindShift,
}

internal enum ObjectKind : uint {
    Blank = 0,
    MixedDict = 1 << FrameTagConstant.ObjectKindShift,
    TypedDict = 2 << FrameTagConstant.ObjectKindShift,
    MixedList = 3 << FrameTagConstant.ObjectKindShift,
    TypedList = 4 << FrameTagConstant.ObjectKindShift,
}

internal enum UsageKind : uint {
    Blank = 0,
    UserPayload = 1 << FrameTagConstant.UsageKindShift,

    /// <summary>LocalId -&gt; SizedPtr, DurableDict&lt;uint, ulong&gt;。
    /// 是一个Epoch最重要的内容之一。</summary>
    VersionTable = 2 << FrameTagConstant.UsageKindShift,
}

internal readonly struct FrameTag(uint bits) {
    internal uint Bits => bits;

    internal VersionKind VersionKind => (VersionKind)(bits & FrameTagConstant.VersionKindMask);
    internal ObjectKind ObjectKind => (ObjectKind)(bits & FrameTagConstant.ObjectKindMask);
    internal UsageKind UsageKind => (UsageKind)(bits & FrameTagConstant.UsageKindMask);

    internal FrameTag(UsageKind usageKind, ObjectKind objectKind, VersionKind versionKind)
        : this((uint)usageKind | (uint)objectKind | (uint)versionKind) { }

    internal SjCorruptionError? Validate(long offset, UsageKind? expectUsage = null, ObjectKind? expectObject = null) {
        if (VersionKind is not VersionKind.Rebase and not VersionKind.Delta) {
            return new SjCorruptionError(
                $"Invalid VersionKind in frame tag at offset {offset}: {VersionKind}.",
                RecoveryHint: "The frame metadata is malformed."
            );
        }
        if (ObjectKind is ObjectKind.Blank) {
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
