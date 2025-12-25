// Source: Atelia.StateJournal - 持久化对象堆
// Spec: atelia/docs/StateJournal/mvp-design-v2.md

namespace Atelia.StateJournal;

/// <summary>
/// StateJournal 模块的错误基类。
/// </summary>
/// <remarks>
/// 继承自 <see cref="AteliaError"/>，提供 StateJournal 相关的错误码。
/// 错误码格式：<c>StateJournal.{ErrorName}</c>。
/// </remarks>
public abstract record StateJournalError(
    string ErrorCode,
    string Message,
    string? RecoveryHint = null,
    IReadOnlyDictionary<string, string>? Details = null,
    AteliaError? Cause = null
) : AteliaError(ErrorCode, Message, RecoveryHint, Details, Cause);

// ============================================================================
// Format Errors (编码/解码格式错误)
// ============================================================================

/// <summary>
/// VarInt 解码错误：EOF、溢出或非 canonical 编码。
/// </summary>
/// <remarks>
/// 对应条款：<c>[F-DECODE-ERROR-FAILFAST]</c>
/// </remarks>
public sealed record VarIntDecodeError(
    string Message,
    string? RecoveryHint = null,
    IReadOnlyDictionary<string, string>? Details = null
) : StateJournalError("StateJournal.VarInt.DecodeError", Message, RecoveryHint, Details);

/// <summary>
/// VarInt 非 canonical 编码（存在多余的 0 continuation 字节）。
/// </summary>
/// <remarks>
/// 对应条款：<c>[F-VARINT-CANONICAL-ENCODING]</c>
/// </remarks>
public sealed record VarIntNonCanonicalError(
    ulong Value,
    int ActualBytes,
    int ExpectedBytes
) : StateJournalError(
    "StateJournal.VarInt.NonCanonical",
    $"Non-canonical varint encoding: value {Value} used {ActualBytes} bytes but should use {ExpectedBytes} bytes.",
    "Ensure the encoder produces minimal (canonical) encoding.");

/// <summary>
/// 未知的 FrameTag RecordType。
/// </summary>
/// <remarks>
/// 对应条款：<c>[F-UNKNOWN-FRAMETAG-REJECT]</c>
/// </remarks>
public sealed record UnknownRecordTypeError(
    uint FrameTagValue,
    ushort RecordType
) : StateJournalError(
    "StateJournal.FrameTag.UnknownRecordType",
    $"Unknown RecordType 0x{RecordType:X4} in FrameTag 0x{FrameTagValue:X8}. This may indicate file corruption or version mismatch.",
    "Check file integrity or upgrade to a newer version that supports this record type.",
    new Dictionary<string, string> {
        ["FrameTag"] = $"0x{FrameTagValue:X8}",
        ["RecordType"] = $"0x{RecordType:X4}"
    });

/// <summary>
/// 未知的 ObjectKind（当 RecordType=ObjectVersion 时）。
/// </summary>
/// <remarks>
/// 对应条款：<c>[F-UNKNOWN-OBJECTKIND-REJECT]</c>
/// </remarks>
public sealed record UnknownObjectKindError(
    uint FrameTagValue,
    ushort ObjectKind
) : StateJournalError(
    "StateJournal.FrameTag.UnknownObjectKind",
    $"Unknown ObjectKind 0x{ObjectKind:X4} in FrameTag 0x{FrameTagValue:X8}. Cannot deserialize object version record.",
    "Check file integrity or upgrade to a newer version that supports this object type.",
    new Dictionary<string, string> {
        ["FrameTag"] = $"0x{FrameTagValue:X8}",
        ["ObjectKind"] = $"0x{ObjectKind:X4}"
    });

/// <summary>
/// FrameTag SubType 非零（当 RecordType 非 ObjectVersion 时）。
/// </summary>
/// <remarks>
/// 对应条款：<c>[F-FRAMETAG-SUBTYPE-ZERO-WHEN-NOT-OBJVER]</c>
/// </remarks>
public sealed record InvalidSubTypeError(
    uint FrameTagValue,
    ushort RecordType,
    ushort SubType
) : StateJournalError(
    "StateJournal.FrameTag.InvalidSubType",
    $"SubType must be 0x0000 when RecordType is not ObjectVersion, but got 0x{SubType:X4}.",
    "This is likely a file corruption issue.",
    new Dictionary<string, string> {
        ["FrameTag"] = $"0x{FrameTagValue:X8}",
        ["RecordType"] = $"0x{RecordType:X4}",
        ["SubType"] = $"0x{SubType:X4}"
    });

/// <summary>
/// 未知的 ValueType。
/// </summary>
/// <remarks>
/// 对应条款：<c>[F-UNKNOWN-VALUETYPE-REJECT]</c>
/// </remarks>
public sealed record UnknownValueTypeError(
    byte ValueTypeByte
) : StateJournalError(
    "StateJournal.ValueType.Unknown",
    $"Unknown ValueType 0x{ValueTypeByte:X2}. Cannot decode value.",
    "Check file integrity or upgrade to a newer version.");

// ============================================================================
// Address/Pointer Errors (地址/指针错误)
// ============================================================================

/// <summary>
/// 地址未 4 字节对齐。
/// </summary>
/// <remarks>
/// 对应条款：<c>[F-ADDRESS64-ALIGNMENT]</c>
/// </remarks>
public sealed record AddressAlignmentError(
    ulong Address
) : StateJournalError(
    "StateJournal.Address.Alignment",
    $"Address 0x{Address:X16} is not 4-byte aligned (offset % 4 = {Address % 4}).",
    "Ensure all frame addresses are 4-byte aligned.");

/// <summary>
/// 指针越界。
/// </summary>
public sealed record AddressOutOfBoundsError(
    ulong Address,
    long FileLength
) : StateJournalError(
    "StateJournal.Address.OutOfBounds",
    $"Address 0x{Address:X16} exceeds file length {FileLength}.",
    "The file may be truncated or the pointer is corrupted.");

// ============================================================================
// Object Lifecycle Errors (对象生命周期错误)
// ============================================================================

/// <summary>
/// 对象已分离（Detached），不可访问。
/// </summary>
/// <remarks>
/// 对应条款：<c>[S-TRANSIENT-DISCARD-DETACH]</c>
/// </remarks>
public sealed record ObjectDetachedError(
    ulong ObjectId
) : StateJournalError(
    "StateJournal.Object.Detached",
    $"Object {ObjectId} has been detached and cannot be accessed.",
    "The object was never committed and has been discarded. Call CreateObject() to create a new object.");

/// <summary>
/// 对象未找到。
/// </summary>
public sealed record ObjectNotFoundError(
    ulong ObjectId
) : StateJournalError(
    "StateJournal.Object.NotFound",
    $"Object {ObjectId} not found in the journal.",
    "Verify the ObjectId is correct. It may have been deleted or never created.");

// ============================================================================
// Diff/Payload Errors (差分/载荷错误)
// ============================================================================

/// <summary>
/// DiffPayload 中的 key 未排序或有重复。
/// </summary>
/// <remarks>
/// 对应条款：<c>[S-DIFF-KEY-SORTED-UNIQUE]</c>
/// </remarks>
public sealed record DiffKeySortingError(
    ulong PreviousKey,
    ulong CurrentKey
) : StateJournalError(
    "StateJournal.Diff.KeySorting",
    $"DiffPayload keys must be sorted and unique. Found key {CurrentKey} after {PreviousKey}.",
    "This indicates a corrupt or malformed diff payload.");
