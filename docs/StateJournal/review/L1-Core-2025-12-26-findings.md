# L1 Core 模块符合性审阅 Findings

> **briefId**: L1-Core-2025-12-26-001
> **reviewDate**: 2025-12-26
> **reviewer**: CodexReviewer
> **status**: Complete

---

## 目录

1. [Group 1: VarInt 编解码](#group-1-varint-编解码)
2. [Group 2: Ptr64 / Address64](#group-2-ptr64--address64)
3. [Group 3: StateJournalError 类型](#group-3-statejournalerror-类型)
4. [Group 4: FrameTag 位段编码](#group-4-frametag-位段编码)
5. [Group 5: IDurableObject 接口](#group-5-idurableobject-接口)
6. [Group 6: DurableObjectState 枚举](#group-6-durableobjectstate-枚举)
7. [审阅摘要](#审阅摘要)

---

## Group 1: VarInt 编解码

### F-VARINT-CANONICAL-ENCODING-001

---
id: "F-VARINT-CANONICAL-ENCODING-001"
verdictType: "C"
clauseId: "[F-VARINT-CANONICAL-ENCODING]"
dedupeKey: "F-VARINT-CANONICAL-ENCODING|VarInt.cs|C|canonical-write"
---

# 🟢 C: [F-VARINT-CANONICAL-ENCODING] WriteVarUInt 产生 canonical 最短编码

## 📝 Evidence

**规范**:
> `varuint`：无符号 base-128，每个字节低 7 bit 为数据，高 1 bit 为 continuation（1 表示后续还有字节）。`uint64` 最多 10 字节。
> 
> **[F-VARINT-CANONICAL-ENCODING]** canonical 最短编码 (mvp-design-v2.md §3.2.0.1)

**代码**: [VarInt.cs#L44-L64](../../../src/StateJournal/Core/VarInt.cs#L44-L64)

```csharp
public static int WriteVarUInt(Span<byte> destination, ulong value)
{
    int length = GetVarUIntLength(value);
    if (destination.Length < length)
    {
        throw new ArgumentException(
            $"Destination buffer too small. Need {length} bytes but only {destination.Length} available.",
            nameof(destination));
    }

    int offset = 0;
    while (value >= 0x80)
    {
        // 低 7 bit + continuation flag
        destination[offset++] = (byte)(value | 0x80);
        value >>= 7;
    }
    // 最后一个字节没有 continuation flag
    destination[offset++] = (byte)value;

    return offset;
}
```

**复现**:
- 类型: existingTest
- 参考: `VarIntTests.WriteVarUInt_Zero_ProducesOneByte`, `WriteVarUInt_300_ProducesExpectedBytes`, `WriteAndRead_VarUInt_Roundtrip`
- 验证: 测试验证 0 编码为 1 字节 `[0x00]`，300 编码为 `[0xAC, 0x02]`

## ⚖️ Verdict

**判定**: C — 代码正确实现了 canonical 最短编码。算法先计算 `GetVarUIntLength(value)` 获取最短长度，然后按 base-128 编码写入。不会产生多余的 0 continuation 字节。

---

### F-VARINT-CANONICAL-ENCODING-002

---
id: "F-VARINT-CANONICAL-ENCODING-002"
verdictType: "C"
clauseId: "[F-VARINT-CANONICAL-ENCODING]"
dedupeKey: "F-VARINT-CANONICAL-ENCODING|VarInt.cs|C|canonical-read-reject"
---

# 🟢 C: [F-VARINT-CANONICAL-ENCODING] TryReadVarUInt 拒绝非 canonical 编码

## 📝 Evidence

**规范**:
> **[F-VARINT-CANONICAL-ENCODING]** canonical 最短编码
> 
> **[F-DECODE-ERROR-FAILFAST]** 解码错误策略：遇到 EOF、溢出、或非 canonical 一律视为格式错误并失败。 (mvp-design-v2.md §3.2.0.1)

**代码**: [VarInt.cs#L90-L96](../../../src/StateJournal/Core/VarInt.cs#L90-L96)

```csharp
// 检查 canonical 编码
int expectedLength = GetVarUIntLength(result);
if (bytesConsumed != expectedLength)
{
    return AteliaResult<(ulong, int)>.Failure(
        new VarIntNonCanonicalError(result, bytesConsumed, expectedLength));
}
```

**复现**:
- 类型: existingTest
- 参考: `VarIntTests.TryReadVarUInt_NonCanonical_ZeroWithTwoBytes_ReturnsFailure`, `TryReadVarUInt_NonCanonical_OneWithThreeBytes_ReturnsFailure`, `TryReadVarUInt_NonCanonical_127WithTwoBytes_ReturnsFailure`
- 验证: 测试验证 `0x80 0x00` (0 用 2 字节) 被拒绝，返回 `VarIntNonCanonicalError`

## ⚖️ Verdict

**判定**: C — 代码在解码完成后检查实际消费字节数与 canonical 长度是否一致，不一致则返回 `VarIntNonCanonicalError`。

---

### F-DECODE-ERROR-FAILFAST-001

---
id: "F-DECODE-ERROR-FAILFAST-001"
verdictType: "C"
clauseId: "[F-DECODE-ERROR-FAILFAST]"
dedupeKey: "F-DECODE-ERROR-FAILFAST|VarInt.cs|C|eof-handling"
---

# 🟢 C: [F-DECODE-ERROR-FAILFAST] TryReadVarUInt 处理 EOF

## 📝 Evidence

**规范**:
> **[F-DECODE-ERROR-FAILFAST]** 解码错误策略：遇到 EOF、溢出、或非 canonical 一律视为格式错误并失败。 (mvp-design-v2.md §3.2.0.1)

**代码**: [VarInt.cs#L68-L73](../../../src/StateJournal/Core/VarInt.cs#L68-L73) 和 [VarInt.cs#L101-L105](../../../src/StateJournal/Core/VarInt.cs#L101-L105)

```csharp
// 空缓冲区检查
if (source.IsEmpty)
{
    return AteliaResult<(ulong, int)>.Failure(
        new VarIntDecodeError("Unexpected EOF: empty buffer when reading varuint."));
}

// continuation flag 后无数据
return AteliaResult<(ulong, int)>.Failure(
    new VarIntDecodeError(
        $"Unexpected EOF: continuation flag set at byte {bytesConsumed} but no more data.",
        "The varuint encoding is truncated."));
```

**复现**:
- 类型: existingTest
- 参考: `VarIntTests.TryReadVarUInt_EmptyBuffer_ReturnsFailure`, `TryReadVarUInt_TruncatedContinuation_ReturnsFailure`, `TryReadVarUInt_MultiByteEof_ReturnsFailure`

## ⚖️ Verdict

**判定**: C — 代码正确检测并报告 EOF 错误：空缓冲区和 continuation flag 后无数据两种情况都返回 `VarIntDecodeError`。

---

### F-DECODE-ERROR-FAILFAST-002

---
id: "F-DECODE-ERROR-FAILFAST-002"
verdictType: "C"
clauseId: "[F-DECODE-ERROR-FAILFAST]"
dedupeKey: "F-DECODE-ERROR-FAILFAST|VarInt.cs|C|overflow-handling"
---

# 🟢 C: [F-DECODE-ERROR-FAILFAST] TryReadVarUInt 处理溢出

## 📝 Evidence

**规范**:
> **[F-DECODE-ERROR-FAILFAST]** 解码错误策略：遇到 EOF、溢出（超过允许的最大字节数或移位溢出）、或非 canonical 一律视为格式错误并失败。 (mvp-design-v2.md §3.2.0.1)

**代码**: [VarInt.cs#L77-L91](../../../src/StateJournal/Core/VarInt.cs#L77-L91)

```csharp
// 检查溢出：varuint64 最多 10 字节
if (bytesConsumed > MaxVarUInt64Bytes)
{
    return AteliaResult<(ulong, int)>.Failure(
        new VarIntDecodeError(
            $"VarUInt overflow: more than {MaxVarUInt64Bytes} bytes.",
            "The encoded value exceeds uint64 range."));
}

// 第 10 字节特殊处理：只能有低 1 bit 有效（0x00 或 0x01）
if (bytesConsumed == MaxVarUInt64Bytes && b > 0x01)
{
    return AteliaResult<(ulong, int)>.Failure(
        new VarIntDecodeError(
            $"VarUInt overflow: 10th byte value 0x{b:X2} exceeds allowed range.",
            "The encoded value exceeds uint64 range."));
}
```

**复现**:
- 类型: existingTest
- 参考: `VarIntTests.TryReadVarUInt_ElevenBytes_ReturnsOverflowError`, `TryReadVarUInt_TenthByteTooLarge_ReturnsOverflowError`, `TryReadVarUInt_MaxValue_Succeeds`

## ⚖️ Verdict

**判定**: C — 代码正确检测两种溢出情况：(1) 超过 10 字节，(2) 第 10 字节值大于 0x01。同时 `ulong.MaxValue` 可以正确编解码。

---

## Group 2: Ptr64 / Address64

### F-ADDRESS64-DEFINITION-001

---
id: "F-ADDRESS64-DEFINITION-001"
verdictType: "C"
clauseId: "[F-ADDRESS64-DEFINITION]"
dedupeKey: "F-ADDRESS64-DEFINITION|Ptr64.cs|C|type-alias"
---

# 🟢 C: [F-ADDRESS64-DEFINITION] Ptr64 是 Address64 的类型别名

## 📝 Evidence

**规范**:
> **Address64** 是 8 字节 LE 编码的文件偏移量，指向一个 Frame 的起始位置。 (rbf-interface.md §2.3)
>
> **Ptr64** / **Address64**：8 字节文件偏移量。详见 rbf-interface.md §2.2 (mvp-design-v2.md 术语表)

**代码**: [Ptr64.cs#L13](../../../src/StateJournal/Core/Ptr64.cs#L13)

```csharp
global using Ptr64 = Atelia.Rbf.Address64;
```

**复现**:
- 类型: existingTest
- 参考: `Address64Tests.Ptr64_IsAliasForAddress64`, `Ptr64Null_EqualsAddress64Null`

## ⚖️ Verdict

**判定**: C — `Ptr64` 正确定义为 `Atelia.Rbf.Address64` 的 global using 别名，与规范要求一致。

---

### F-ADDRESS64-ALIGNMENT-001

---
id: "F-ADDRESS64-ALIGNMENT-001"
verdictType: "C"
clauseId: "[F-ADDRESS64-ALIGNMENT]"
dedupeKey: "F-ADDRESS64-ALIGNMENT|Address64Extensions.cs|C|validation"
---

# 🟢 C: [F-ADDRESS64-ALIGNMENT] TryFromOffset 验证 4 字节对齐

## 📝 Evidence

**规范**:
> **[F-ADDRESS64-ALIGNMENT]**：有效 Address64 MUST 4 字节对齐（`Value % 4 == 0`） (rbf-interface.md §2.3)

**代码**: [Address64Extensions.cs#L29-L35](../../../src/StateJournal/Core/Address64Extensions.cs#L29-L35)

```csharp
// 检查 4 字节对齐
if (offset % 4 != 0)
{
    return AteliaResult<Address64>.Failure(new AddressAlignmentError(offset));
}

return AteliaResult<Address64>.Success(new Address64(offset));
```

**复现**:
- 类型: existingTest
- 参考: `Address64Tests.TryFromOffset_AlignedValue_ReturnsSuccess`, `TryFromOffset_UnalignedValue_ReturnsFailure`
- 验证: 4, 8, 1024 对齐值成功；1, 2, 3, 5, 7 非对齐值返回 `AddressAlignmentError`

## ⚖️ Verdict

**判定**: C — 代码正确实现了 4 字节对齐验证，非对齐值返回 `AddressAlignmentError`。

---

### F-ADDRESS64-NULL-001

---
id: "F-ADDRESS64-NULL-001"
verdictType: "C"
clauseId: "[F-ADDRESS64-NULL]"
dedupeKey: "F-ADDRESS64-NULL|Address64Extensions.cs|C|null-handling"
---

# 🟢 C: [F-ADDRESS64-NULL] TryFromOffset(0) 返回 Address64.Null

## 📝 Evidence

**规范**:
> **[F-ADDRESS64-NULL]**：`Value == 0` 表示 null（无效地址） (rbf-interface.md §2.3)

**代码**: [Address64Extensions.cs#L22-L26](../../../src/StateJournal/Core/Address64Extensions.cs#L22-L26)

```csharp
// Null 地址（offset=0）是合法值，直接返回
if (offset == 0)
{
    return AteliaResult<Address64>.Success(Address64.Null);
}
```

**复现**:
- 类型: existingTest
- 参考: `Address64Tests.TryFromOffset_Zero_ReturnsNullAddress`, `Null_HasValueZero`, `Null_IsNullReturnsTrue`

## ⚖️ Verdict

**判定**: C — `offset=0` 正确返回 `Address64.Null`（合法值，非错误），符合规范定义的 null 语义。

---

## Group 3: StateJournalError 类型

### F-DECODE-ERROR-FAILFAST-003

---
id: "F-DECODE-ERROR-FAILFAST-003"
verdictType: "C"
clauseId: "[F-DECODE-ERROR-FAILFAST]"
dedupeKey: "F-DECODE-ERROR-FAILFAST|StateJournalError.cs|C|error-types"
---

# 🟢 C: [F-DECODE-ERROR-FAILFAST] VarInt 解码错误类型定义

## 📝 Evidence

**规范**:
> **[F-DECODE-ERROR-FAILFAST]** 解码错误策略：遇到 EOF、溢出、或非 canonical 一律视为格式错误并失败。 (mvp-design-v2.md §3.2.0.1)

**代码**: [StateJournalError.cs#L24-L49](../../../src/StateJournal/Core/StateJournalError.cs#L24-L49)

```csharp
/// <summary>
/// VarInt 解码错误：EOF、溢出或非 canonical 编码。
/// </summary>
public sealed record VarIntDecodeError(
    string Message,
    string? RecoveryHint = null,
    IReadOnlyDictionary<string, string>? Details = null
) : StateJournalError("StateJournal.VarInt.DecodeError", Message, RecoveryHint, Details);

/// <summary>
/// VarInt 非 canonical 编码（存在多余的 0 continuation 字节）。
/// </summary>
public sealed record VarIntNonCanonicalError(
    ulong Value,
    int ActualBytes,
    int ExpectedBytes
) : StateJournalError(
    "StateJournal.VarInt.NonCanonical",
    $"Non-canonical varint encoding: value {Value} used {ActualBytes} bytes but should use {ExpectedBytes} bytes.",
    "Ensure the encoder produces minimal (canonical) encoding.");
```

**复现**:
- 类型: existingTest
- 参考: `StateJournalErrorTests.VarIntDecodeError_HasCorrectErrorCode`, `VarIntNonCanonicalError_FormatsMessage`

## ⚖️ Verdict

**判定**: C — 定义了 `VarIntDecodeError`（用于 EOF/溢出）和 `VarIntNonCanonicalError`（用于非 canonical 编码），ErrorCode 格式符合 `StateJournal.{ErrorName}` 规范。

---

### F-UNKNOWN-FRAMETAG-REJECT-001

---
id: "F-UNKNOWN-FRAMETAG-REJECT-001"
verdictType: "C"
clauseId: "[F-UNKNOWN-FRAMETAG-REJECT]"
dedupeKey: "F-UNKNOWN-FRAMETAG-REJECT|StateJournalError.cs|C|error-type"
---

# 🟢 C: [F-UNKNOWN-FRAMETAG-REJECT] 未知 RecordType 错误类型定义

## 📝 Evidence

**规范**:
> **[F-UNKNOWN-FRAMETAG-REJECT]** Reader 遇到未知 RecordType MUST fail-fast（不得静默跳过）。 (mvp-design-v2.md 枚举值速查表)

**代码**: [StateJournalError.cs#L51-L65](../../../src/StateJournal/Core/StateJournalError.cs#L51-L65)

```csharp
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
    ...);
```

**复现**:
- 类型: existingTest
- 参考: `StateJournalErrorTests.UnknownRecordTypeError_IncludesDetails`

## ⚖️ Verdict

**判定**: C — `UnknownRecordTypeError` 类型正确定义，包含 FrameTagValue 和 RecordType 详细信息，支持 fail-fast 语义。

---

### F-UNKNOWN-OBJECTKIND-REJECT-001

---
id: "F-UNKNOWN-OBJECTKIND-REJECT-001"
verdictType: "C"
clauseId: "[F-UNKNOWN-OBJECTKIND-REJECT]"
dedupeKey: "F-UNKNOWN-OBJECTKIND-REJECT|StateJournalError.cs|C|error-type"
---

# 🟢 C: [F-UNKNOWN-OBJECTKIND-REJECT] 未知 ObjectKind 错误类型定义

## 📝 Evidence

**规范**:
> **[F-UNKNOWN-OBJECTKIND-REJECT]** 当 `RecordType == ObjectVersionRecord` 时，Reader 遇到未知 `ObjectKind` MUST fail-fast（不得静默跳过）。 (mvp-design-v2.md 枚举值速查表)

**代码**: [StateJournalError.cs#L67-L81](../../../src/StateJournal/Core/StateJournalError.cs#L67-L81)

```csharp
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
    ...);
```

**复现**:
- 类型: existingTest
- 参考: `StateJournalErrorTests.UnknownObjectKindError_IncludesDetails`

## ⚖️ Verdict

**判定**: C — `UnknownObjectKindError` 类型正确定义，支持 fail-fast 语义。

---

### S-TRANSIENT-DISCARD-DETACH-001

---
id: "S-TRANSIENT-DISCARD-DETACH-001"
verdictType: "C"
clauseId: "[S-TRANSIENT-DISCARD-DETACH]"
dedupeKey: "S-TRANSIENT-DISCARD-DETACH|StateJournalError.cs|C|error-type"
---

# 🟢 C: [S-TRANSIENT-DISCARD-DETACH] 对象分离错误类型定义

## 📝 Evidence

**规范**:
> **[S-TRANSIENT-DISCARD-DETACH]** 后续**语义数据访问** MUST 抛出 `ObjectDetachedException`。
> 异常消息 SHOULD 提供恢复指引，例如："Object was never committed. Call CreateObject() to create a new object." (mvp-design-v2.md §3.1.0.1)

**代码**: [StateJournalError.cs#L119-L128](../../../src/StateJournal/Core/StateJournalError.cs#L119-L128)

```csharp
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
```

**复现**:
- 类型: existingTest
- 参考: `StateJournalErrorTests.ObjectDetachedError_HasRecoveryHint`
- 验证: RecoveryHint 包含 "CreateObject()"

## ⚖️ Verdict

**判定**: C — `ObjectDetachedError` 类型正确定义，RecoveryHint 符合规范建议的消息格式。

---

## Group 4: FrameTag 位段编码

### F-FRAMETAG-STATEJOURNAL-BITLAYOUT-001

---
id: "F-FRAMETAG-STATEJOURNAL-BITLAYOUT-001"
verdictType: "C"
clauseId: "[F-FRAMETAG-STATEJOURNAL-BITLAYOUT]"
dedupeKey: "F-FRAMETAG-STATEJOURNAL-BITLAYOUT|StateJournalFrameTag.cs|C|bit-extraction"
---

# 🟢 C: [F-FRAMETAG-STATEJOURNAL-BITLAYOUT] GetRecordType/GetSubType/GetObjectKind 位段提取

## 📝 Evidence

**规范**:
> **[F-FRAMETAG-STATEJOURNAL-BITLAYOUT]** StateJournal MUST 按以下位段解释 `FrameTag.Value`：
> 
> | 位范围 | 字段名 | 类型 | 语义 |
> |--------|--------|------|------|
> | 31..16 | SubType | `u16` | 当 RecordType=ObjectVersion 时解释为 ObjectKind |
> | 15..0 | RecordType | `u16` | Record 顶层类型 |
>
> **计算公式**：`FrameTag = (SubType << 16) | RecordType` (mvp-design-v2.md 枚举值速查表)

**代码**: [StateJournalFrameTag.cs#L79-L102](../../../src/StateJournal/Core/StateJournalFrameTag.cs#L79-L102)

```csharp
public static RecordType GetRecordType(this FrameTag tag)
{
    return (RecordType)(tag.Value & 0xFFFF);
}

public static ushort GetSubType(this FrameTag tag)
{
    return (ushort)(tag.Value >> 16);
}

public static ObjectKind GetObjectKind(this FrameTag tag)
{
    return (ObjectKind)(tag.Value >> 16);
}
```

**复现**:
- 类型: existingTest
- 参考: `StateJournalFrameTagTests.GetRecordType_DictVersion_ReturnsObjectVersion`, `GetSubType_DictVersion_Returns0x0001`, `GetObjectKind_DictVersion_ReturnsDict`, `Create_ComputesCorrectValue`

## ⚖️ Verdict

**判定**: C — 位段提取方法正确实现：`& 0xFFFF` 提取低 16 位，`>> 16` 提取高 16 位。测试验证了 `0x00010001` 解析为 RecordType=ObjectVersion, ObjectKind=Dict。

---

### F-FRAMETAG-STATEJOURNAL-BITLAYOUT-002

---
id: "F-FRAMETAG-STATEJOURNAL-BITLAYOUT-002"
verdictType: "C"
clauseId: "[F-FRAMETAG-STATEJOURNAL-BITLAYOUT]"
dedupeKey: "F-FRAMETAG-STATEJOURNAL-BITLAYOUT|StateJournalFrameTag.cs|C|constants"
---

# 🟢 C: [F-FRAMETAG-STATEJOURNAL-BITLAYOUT] 预定义常量值正确

## 📝 Evidence

**规范**:
> | FrameTag 值 | RecordType | ObjectKind | 说明 | 字节序列（LE）|
> |-------------|------------|------------|------|---------------|
> | `0x00010001` | ObjectVersion | Dict | DurableDict 版本记录 | `01 00 01 00` |
> | `0x00000002` | MetaCommit | — | 提交元数据记录 | `02 00 00 00` |
> (mvp-design-v2.md 枚举值速查表)

**代码**: [StateJournalFrameTag.cs#L47-L63](../../../src/StateJournal/Core/StateJournalFrameTag.cs#L47-L63)

```csharp
public static readonly FrameTag DictVersion = new(0x00010001);
public static readonly FrameTag MetaCommit = new(0x00000002);
```

**复现**:
- 类型: existingTest
- 参考: `StateJournalFrameTagTests.DictVersion_HasCorrectValue`, `MetaCommit_HasCorrectValue`, `DictVersion_ByteSequence_IsCorrect`, `MetaCommit_ByteSequence_IsCorrect`
- 验证: 字节序列 `01 00 01 00` 和 `02 00 00 00` 与规范一致（LE）

## ⚖️ Verdict

**判定**: C — 预定义常量 `DictVersion` 和 `MetaCommit` 值与规范完全一致。

---

### F-FRAMETAG-SUBTYPE-ZERO-WHEN-NOT-OBJVER-001

---
id: "F-FRAMETAG-SUBTYPE-ZERO-WHEN-NOT-OBJVER-001"
verdictType: "C"
clauseId: "[F-FRAMETAG-SUBTYPE-ZERO-WHEN-NOT-OBJVER]"
dedupeKey: "F-FRAMETAG-SUBTYPE-ZERO-WHEN-NOT-OBJVER|StateJournalFrameTag.cs|C|validation"
---

# 🟢 C: [F-FRAMETAG-SUBTYPE-ZERO-WHEN-NOT-OBJVER] TryParse 验证非 ObjectVersion 时 SubType=0

## 📝 Evidence

**规范**:
> **[F-FRAMETAG-SUBTYPE-ZERO-WHEN-NOT-OBJVER]** 当 `RecordType != ObjectVersionRecord` 时，`SubType` MUST 为 `0x0000`；Reader 遇到非零 SubType MUST 视为格式错误。 (mvp-design-v2.md 枚举值速查表)

**代码**: [StateJournalFrameTag.cs#L175-L181](../../../src/StateJournal/Core/StateJournalFrameTag.cs#L175-L181)

```csharp
// RecordType != ObjectVersion 时（当前只有 MetaCommit）
// 规则 4: SubType 必须为 0
if (subType != 0)
{
    return AteliaResult<(RecordType, ObjectKind?)>.Failure(
        new InvalidSubTypeError(tag.Value, (ushort)recordType, subType));
}
```

**复现**:
- 类型: existingTest
- 参考: `StateJournalFrameTagTests.TryParse_MetaCommit_NonZeroSubType_ReturnsFailure`
- 验证: MetaCommit + SubType=0x0001/0x00FF/0xFFFF 均返回 `InvalidSubTypeError`

## ⚖️ Verdict

**判定**: C — TryParse 在 RecordType 非 ObjectVersion 时正确检查 SubType 必须为 0，否则返回 `InvalidSubTypeError`。

---

### F-OBJVER-OBJECTKIND-FROM-TAG-001

---
id: "F-OBJVER-OBJECTKIND-FROM-TAG-001"
verdictType: "C"
clauseId: "[F-OBJVER-OBJECTKIND-FROM-TAG]"
dedupeKey: "F-OBJVER-OBJECTKIND-FROM-TAG|StateJournalFrameTag.cs|C|extraction"
---

# 🟢 C: [F-OBJVER-OBJECTKIND-FROM-TAG] ObjectKind 从 FrameTag 高 16 位提取

## 📝 Evidence

**规范**:
> **[F-OBJVER-OBJECTKIND-FROM-TAG]** 当 `RecordType == ObjectVersionRecord` 时，`SubType` MUST 解释为 `ObjectKind`，Payload 内 MUST NOT 再包含 ObjectKind 字节。 (mvp-design-v2.md 枚举值速查表)

**代码**: [StateJournalFrameTag.cs#L158-L170](../../../src/StateJournal/Core/StateJournalFrameTag.cs#L158-L170)

```csharp
// RecordType == ObjectVersion 时
if (recordType == RecordType.ObjectVersion)
{
    var objectKind = (ObjectKind)subType;

    // 规则 3: ObjectKind == Reserved → UnknownObjectKindError
    if (objectKind == ObjectKind.Reserved)
    {
        return AteliaResult<(RecordType, ObjectKind?)>.Failure(
            new UnknownObjectKindError(tag.Value, subType));
    }
    // ...
    return AteliaResult<(RecordType, ObjectKind?)>.Success((recordType, objectKind));
}
```

**复现**:
- 类型: existingTest
- 参考: `StateJournalFrameTagTests.TryParse_DictVersion_Succeeds`, `CreateObjectVersion_Roundtrip_ExtractCorrectValues`
- 验证: `0x00010001` 解析返回 ObjectKind=Dict

## ⚖️ Verdict

**判定**: C — TryParse 在 RecordType=ObjectVersion 时正确从 SubType（高 16 位）解释 ObjectKind。

---

### F-UNKNOWN-FRAMETAG-REJECT-002

---
id: "F-UNKNOWN-FRAMETAG-REJECT-002"
verdictType: "C"
clauseId: "[F-UNKNOWN-FRAMETAG-REJECT]"
dedupeKey: "F-UNKNOWN-FRAMETAG-REJECT|StateJournalFrameTag.cs|C|validation"
---

# 🟢 C: [F-UNKNOWN-FRAMETAG-REJECT] TryParse 拒绝未知 RecordType

## 📝 Evidence

**规范**:
> **[F-UNKNOWN-FRAMETAG-REJECT]** Reader 遇到未知 RecordType MUST fail-fast（不得静默跳过）。 (mvp-design-v2.md 枚举值速查表)

**代码**: [StateJournalFrameTag.cs#L140-L153](../../../src/StateJournal/Core/StateJournalFrameTag.cs#L140-L153)

```csharp
// 规则 1: RecordType == Reserved → UnknownRecordTypeError
if (recordType == RecordType.Reserved)
{
    return AteliaResult<(RecordType, ObjectKind?)>.Failure(
        new UnknownRecordTypeError(tag.Value, (ushort)recordType));
}

// 规则 2: RecordType 未知 → UnknownRecordTypeError
if (recordType != RecordType.ObjectVersion && recordType != RecordType.MetaCommit)
{
    return AteliaResult<(RecordType, ObjectKind?)>.Failure(
        new UnknownRecordTypeError(tag.Value, (ushort)recordType));
}
```

**复现**:
- 类型: existingTest
- 参考: `StateJournalFrameTagTests.TryParse_Reserved_ReturnsFailure`, `TryParse_UnknownRecordType_ReturnsFailure`
- 验证: 0x0000(Reserved), 0x0003, 0x00FF, 0x7FFF, 0x8000, 0xFFFF 均返回 `UnknownRecordTypeError`

## ⚖️ Verdict

**判定**: C — TryParse 正确拒绝 Reserved(0x0000) 和所有未知 RecordType 值，返回 `UnknownRecordTypeError`。

---

### F-UNKNOWN-OBJECTKIND-REJECT-002

---
id: "F-UNKNOWN-OBJECTKIND-REJECT-002"
verdictType: "C"
clauseId: "[F-UNKNOWN-OBJECTKIND-REJECT]"
dedupeKey: "F-UNKNOWN-OBJECTKIND-REJECT|StateJournalFrameTag.cs|C|validation"
---

# 🟢 C: [F-UNKNOWN-OBJECTKIND-REJECT] TryParse 拒绝未知 ObjectKind

## 📝 Evidence

**规范**:
> **[F-UNKNOWN-OBJECTKIND-REJECT]** 当 `RecordType == ObjectVersionRecord` 时，Reader 遇到未知 `ObjectKind` MUST fail-fast（不得静默跳过）。 (mvp-design-v2.md 枚举值速查表)

**代码**: [StateJournalFrameTag.cs#L163-L172](../../../src/StateJournal/Core/StateJournalFrameTag.cs#L163-L172)

```csharp
// 规则 3: ObjectKind == Reserved → UnknownObjectKindError
if (objectKind == ObjectKind.Reserved)
{
    return AteliaResult<(RecordType, ObjectKind?)>.Failure(
        new UnknownObjectKindError(tag.Value, subType));
}

// 规则：ObjectKind 未知（非 Dict）→ UnknownObjectKindError
// MVP 阶段只有 Dict
if (objectKind != ObjectKind.Dict)
{
    return AteliaResult<(RecordType, ObjectKind?)>.Failure(
        new UnknownObjectKindError(tag.Value, subType));
}
```

**复现**:
- 类型: existingTest
- 参考: `StateJournalFrameTagTests.TryParse_ObjectVersion_ReservedObjectKind_ReturnsFailure`, `TryParse_ObjectVersion_UnknownObjectKind_ReturnsFailure`
- 验证: ObjectKind=0x0000(Reserved), 0x0002, 0x007F, 0x0080, 0xFFFF 均返回 `UnknownObjectKindError`

## ⚖️ Verdict

**判定**: C — TryParse 正确拒绝 Reserved(0x0000) 和所有非 Dict 的 ObjectKind 值，返回 `UnknownObjectKindError`。MVP 阶段只支持 Dict。

---

## Group 5: IDurableObject 接口

### A-OBJECT-STATE-PROPERTY-001

---
id: "A-OBJECT-STATE-PROPERTY-001"
verdictType: "C"
clauseId: "[A-OBJECT-STATE-PROPERTY]"
dedupeKey: "A-OBJECT-STATE-PROPERTY|IDurableObject.cs|C|interface-def"
---

# 🟢 C: [A-OBJECT-STATE-PROPERTY] State 属性定义

## 📝 Evidence

**规范**:
> **[A-OBJECT-STATE-PROPERTY]**：`IDurableObject` MUST 暴露 `State` 属性，返回 `DurableObjectState` 枚举；读取 MUST NOT 抛异常（含 Detached 状态）；复杂度 MUST 为 O(1) (mvp-design-v2.md §3.1.0.1)

**代码**: [IDurableObject.cs#L35-L44](../../../src/StateJournal/Core/IDurableObject.cs#L35-L44)

```csharp
/// <summary>
/// 对象的生命周期状态。
/// </summary>
/// <remarks>
/// <para>
/// 对应条款：<c>[A-OBJECT-STATE-PROPERTY]</c>
/// </para>
/// <para>
/// 读取 MUST NOT 抛异常（含 <see cref="DurableObjectState.Detached"/> 状态），复杂度 O(1)。
/// </para>
/// </remarks>
DurableObjectState State { get; }
```

**复现**:
- 类型: existingTest
- 参考: `IDurableObjectTests.State_WhenClean_DoesNotThrow`, `State_WhenDetached_DoesNotThrow`
- 验证: FakeDurableObject 实现了正确的契约，Detached 状态下读取 State 不抛异常

## ⚖️ Verdict

**判定**: C — 接口正确定义了 `State` 属性，XML 文档明确了 O(1) 复杂度和不抛异常的要求。测试使用 FakeDurableObject 验证了契约。

---

### A-HASCHANGES-O1-COMPLEXITY-001

---
id: "A-HASCHANGES-O1-COMPLEXITY-001"
verdictType: "C"
clauseId: "[A-HASCHANGES-O1-COMPLEXITY]"
dedupeKey: "A-HASCHANGES-O1-COMPLEXITY|IDurableObject.cs|C|interface-def"
---

# 🟢 C: [A-HASCHANGES-O1-COMPLEXITY] HasChanges 属性定义

## 📝 Evidence

**规范**:
> **[A-HASCHANGES-O1-COMPLEXITY]**：`HasChanges` 属性 MUST 存在且复杂度为 O(1) (mvp-design-v2.md §3.1.0.1)

**代码**: [IDurableObject.cs#L46-L59](../../../src/StateJournal/Core/IDurableObject.cs#L46-L59)

```csharp
/// <summary>
/// 是否有未提交的变更。
/// </summary>
/// <remarks>
/// <para>
/// 对应条款：<c>[A-HASCHANGES-O1-COMPLEXITY]</c>
/// </para>
/// <para>
/// 复杂度 MUST 为 O(1)。
/// </para>
/// <para>
/// 语义：<c>HasChanges == true</c> 当且仅当 <see cref="State"/> 为
/// <see cref="DurableObjectState.PersistentDirty"/> 或 <see cref="DurableObjectState.TransientDirty"/>。
/// </para>
/// </remarks>
bool HasChanges { get; }
```

**复现**:
- 类型: existingTest
- 参考: `IDurableObjectTests.HasChanges_WhenClean_ReturnsFalse`, `HasChanges_WhenPersistentDirty_ReturnsTrue`, `HasChanges_IsConsistentWithState`
- 验证: FakeDurableObject 使用 `_state is DurableObjectState.PersistentDirty or DurableObjectState.TransientDirty` 实现 O(1)

## ⚖️ Verdict

**判定**: C — 接口正确定义了 `HasChanges` 属性，XML 文档明确了 O(1) 复杂度要求和语义定义。

---

## Group 6: DurableObjectState 枚举

### A-OBJECT-STATE-CLOSED-SET-001

---
id: "A-OBJECT-STATE-CLOSED-SET-001"
verdictType: "C"
clauseId: "[A-OBJECT-STATE-CLOSED-SET]"
dedupeKey: "A-OBJECT-STATE-CLOSED-SET|DurableObjectState.cs|C|enum-values"
---

# 🟢 C: [A-OBJECT-STATE-CLOSED-SET] DurableObjectState 封闭集

## 📝 Evidence

**规范**:
> **[A-OBJECT-STATE-CLOSED-SET]**：`DurableObjectState` MUST 仅包含 `Clean`, `PersistentDirty`, `TransientDirty`, `Detached` 四个值 (mvp-design-v2.md §3.1.0.1)

**代码**: [DurableObjectState.cs#L20-L62](../../../src/StateJournal/Core/DurableObjectState.cs#L20-L62)

```csharp
public enum DurableObjectState
{
    /// <summary>
    /// 干净状态：对象的 Working State 等于 Committed State。
    /// </summary>
    Clean = 0,

    /// <summary>
    /// 持久脏状态：对象已有 Committed 版本，但 Working State 有未提交的变更。
    /// </summary>
    PersistentDirty = 1,

    /// <summary>
    /// 瞬态脏状态：对象是新建的，尚无 Committed 版本。
    /// </summary>
    TransientDirty = 2,

    /// <summary>
    /// 已分离状态：对象已与 Workspace 断开连接（终态）。
    /// </summary>
    Detached = 3,
}
```

**复现**:
- 类型: existingTest
- 参考: `DurableObjectStateTests.DurableObjectState_HasExactlyFourValues`, `Clean_HasValue0`, `PersistentDirty_HasValue1`, `TransientDirty_HasValue2`, `Detached_HasValue3`
- 验证: `Enum.GetValues<DurableObjectState>().Length == 4`

## ⚖️ Verdict

**判定**: C — 枚举正好包含 4 个值：Clean(0), PersistentDirty(1), TransientDirty(2), Detached(3)，与规范完全一致。

---

## 审阅摘要

### 统计数据

| 类别 | 数量 |
|------|------|
| **Conform (C)** | 17 |
| **Violation (V)** | 0 |
| **Underspecified (U)** | 0 |
| **Improvement (I)** | 0 |
| **总计** | 17 |

### 按条款组统计

| Group | C | V | U | I |
|-------|---|---|---|---|
| Group 1: VarInt 编解码 | 4 | 0 | 0 | 0 |
| Group 2: Ptr64 / Address64 | 3 | 0 | 0 | 0 |
| Group 3: StateJournalError 类型 | 4 | 0 | 0 | 0 |
| Group 4: FrameTag 位段编码 | 4 | 0 | 0 | 0 |
| Group 5: IDurableObject 接口 | 2 | 0 | 0 | 0 |
| Group 6: DurableObjectState 枚举 | 1 | 0 | 0 | 0 |

### 结论

**Core 模块符合性审阅结果：✅ 全部通过**

所有 17 个审阅条款均判定为 **Conform (C)**。代码实现忠实地遵循了规范要求：

1. **VarInt 编解码**：正确实现 canonical 最短编码和 fail-fast 解码错误处理
2. **Address64/Ptr64**：正确实现 4 字节对齐验证和 null 语义
3. **错误类型**：完整定义了所有规范要求的错误类型，ErrorCode 格式规范
4. **FrameTag 位段**：位运算正确，TryParse 覆盖所有验证规则
5. **IDurableObject**：接口定义完整，XML 文档明确了复杂度要求
6. **DurableObjectState**：枚举值封闭集与规范一致

### 测试覆盖

所有 Findings 都有对应的 existingTest 验证，测试覆盖充分。

---

> **审阅完成时间**：2025-12-26
> **审阅者**：CodexReviewer
