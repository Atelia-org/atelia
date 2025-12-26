# L1 符合性审阅报告：Objects 模块

> **审阅 ID**: L1-Objects-2025-12-26-001
> **审阅类型**: L1 符合性审阅
> **审阅员**: CodexReviewer
> **日期**: 2025-12-26
> **specRef**: `atelia/docs/StateJournal/mvp-design-v2.md` §3.4.2, §3.4.3

---

## 📋 审阅范围

| 文件 | 职责 |
|:-----|:-----|
| [Objects/ValueType.cs](../../../src/StateJournal/Objects/ValueType.cs) | ValueType 枚举及验证 |
| [Objects/DiffPayload.cs](../../../src/StateJournal/Objects/DiffPayload.cs) | DiffPayload 编解码 |
| [Objects/DurableDict.cs](../../../src/StateJournal/Objects/DurableDict.cs) | 持久化字典实现 |

---

## 📊 审阅摘要

| 统计项 | 数量 |
|:-------|-----:|
| 条款总数 | 16 |
| ✅ Conform (C) | 11 |
| 🔴 Violation (V) | 2 |
| ❓ Underspecified (U) | 3 |
| 💡 Improvement (I) | 0 |

---

## Group A: ValueType 条款

### ✅ C: [F-KVPAIR-HIGHBITS-RESERVED]

**规范**:
> 低 4 bit：`ValueType`（高 4 bit 预留，MVP 必须写 0；reader 见到非 0 视为格式错误）

**代码**: [ValueType.cs#L76-L82](../../../src/StateJournal/Objects/ValueType.cs#L76-L82)

```csharp
public static bool AreHighBitsZero(byte keyValuePairType) {
    return (keyValuePairType & HighBitsMask) == 0;
}
```

**代码**: [ValueType.cs#L92-L99](../../../src/StateJournal/Objects/ValueType.cs#L92-L99)

```csharp
if (!AreHighBitsZero(keyValuePairType)) {
    return AteliaResult<ValueType>.Failure(
        new DiffPayloadFormatError(
            $"KeyValuePairType high 4 bits must be 0, but got 0x{keyValuePairType:X2}.",
            "The file may be corrupted or from a newer version."
        )
    );
}
```

**复现**: existingTest `ValueTypeTests.ValidateKeyValuePairType_HighBitsNonZero_ReturnsFailure`

**判定**: C — 实现正确拒绝高 4 bit 非零的 KeyValuePairType。

---

### ✅ C: [F-UNKNOWN-VALUETYPE-REJECT]

**规范**:
> reader 遇到未知 ValueType（低 4 bit 不在 `{0,1,2,3,4}`）或高 4 bit 非 0，MUST 视为格式错误并失败（ErrorCode: `StateJournal.CorruptedRecord`）。

**代码**: [ValueType.cs#L56-L58](../../../src/StateJournal/Objects/ValueType.cs#L56-L58)

```csharp
public static bool IsKnown(this ValueType valueType) {
    return (byte)valueType <= MaxKnownValueType;
}
```

**代码**: [ValueType.cs#L101-L105](../../../src/StateJournal/Objects/ValueType.cs#L101-L105)

```csharp
var valueType = ExtractValueType(keyValuePairType);
if (!valueType.IsKnown()) {
    return AteliaResult<ValueType>.Failure(
        new UnknownValueTypeError(keyValuePairType)
    );
}
```

**复现**: existingTest `ValueTypeTests.ValidateKeyValuePairType_UnknownValueType_ReturnsFailure`

**判定**: C — 实现正确拒绝未知 ValueType（0x5~0xF）。

---

## Group B: DiffPayload 格式条款

### ✅ C: [S-DIFF-KEY-SORTED-UNIQUE]

**规范**:
> Key 唯一 + 升序：单个 diff 内 key 必须严格唯一，且按 key 升序排列（确定性输出）。

**Writer 验证** — [DiffPayload.cs#L134-L146](../../../src/StateJournal/Objects/DiffPayload.cs#L134-L146):

```csharp
private void ValidateKeyOrder(ulong key) {
    if (_firstPair) {
        _firstPair = false;
        _lastKey = key;
        return;
    }

    if (key <= _lastKey) {
        throw new ArgumentException(
            $"Keys must be in strictly ascending order. Got key {key} after {_lastKey}.",
            nameof(key)
        );
    }
    _lastKey = key;
}
```

**Reader 验证** — [DiffPayload.cs#L229-L235](../../../src/StateJournal/Objects/DiffPayload.cs#L229-L235):

```csharp
// 验证 key 唯一性：delta 必须 > 0（否则 key 会相等或回退）
if (delta == 0) {
    return AteliaResult<bool>.Failure(
        new DiffKeySortingError(_lastKey, _lastKey)
    );
}
```

**复现**: existingTest `DiffPayloadTests.Writer_NonAscendingKey_ThrowsArgumentException`, `DiffPayloadTests.Reader_DuplicateKey_ReturnsError`

**判定**: C — Writer 强制升序；Reader 检测 delta=0（重复 key）并返回错误。

---

### ✅ C: [S-PAIRCOUNT-ZERO-LEGALITY]

**规范**:
> `PairCount == 0` 仅在 `PrevVersionPtr == 0`（Base Version）时合法，表示"空字典的完整 state"。若 `PrevVersionPtr != 0`（Overlay diff）且 `PairCount == 0`，reader MUST 视为格式错误。

**代码分析**:

DiffPayload 编解码层本身**不感知 PrevVersionPtr**——该约束应在 ObjectVersionRecord 解析层验证。

**代码**: [DiffPayload.cs](../../../src/StateJournal/Objects/DiffPayload.cs) — Reader 不检查 PrevVersionPtr。

**复现**: manual — 在 DiffPayloadReader 中，PairCount=0 被静默接受，不验证 PrevVersionPtr。

**判定**: C — **条款约束的执行点不在 DiffPayload 层**，而是在 ObjectVersionRecord 解析层（尚未实现）。DiffPayload 编解码本身正确处理 PairCount=0 的情况（空 payload）。**当 ObjectVersionRecord 解析层实现时，需验证此约束。**

---

### ✅ C: [S-OVERLAY-DIFF-NONEMPTY]

**规范**:
> writer MUST NOT 为"无任何变更"的对象写入 `ObjectVersionRecord`。若对象无变更（`HasChanges == false`），不应生成新版本。

**代码**: [DurableDict.cs#L200-L204](../../../src/StateJournal/Objects/DurableDict.cs#L200-L204)

```csharp
public void WritePendingDiff(IBufferWriter<byte> writer) {
    ThrowIfDetached();

    // 1. 收集所有变更的 key，按升序排列
    var sortedDirtyKeys = _dirtyKeys.OrderBy(k => k).ToList();
```

当 `_dirtyKeys` 为空时，`WritePendingDiff` 会输出 `PairCount=0`。

**分析**: 此条款的执行点在 Workspace/Commit 层——应检查 `HasChanges` 并跳过无变更对象。`DurableDict.WritePendingDiff` 不负责此决策。

**复现**: existingTest `DurableDictTests.WritePendingDiff_NoChanges_WritesEmptyPayload`

**判定**: C — **条款约束的执行点在 Commit 层**（尚未实现）。DurableDict 正确暴露 `HasChanges` 属性供上层判断。

---

## Group C: DurableDict 不变式条款

### ✅ C: [S-WORKING-STATE-TOMBSTONE-FREE]

**规范**:
> Working State 纯净性：在任何对外可读/可枚举的状态视图中，tombstone 不得作为值出现；Delete 的语义是"key 不存在"。

**代码**: [DurableDict.cs#L157-L170](../../../src/StateJournal/Objects/DurableDict.cs#L157-L170)

```csharp
public bool Remove(ulong key) {
    ThrowIfDetached();

    var hadInWorking = _working.Remove(key);
    var hasInCommitted = _committed.ContainsKey(key);

    // 标记 _committed 中的 key 为已删除
    if (hasInCommitted) {
        _removedFromCommitted.Add(key);
    }
    // ...
}
```

**分析**: 实现使用 `_removedFromCommitted` HashSet 追踪删除，而非存储 tombstone 值。读取 API（TryGetValue, ContainsKey, Entries）正确排除已删除的 key。

**复现**: existingTest `DurableDictTests.Remove_KeyNotInEnumeration`

**判定**: C — Working State 不存储 tombstone；删除通过 `_removedFromCommitted` 追踪。

---

### ✅ C: [S-DELETE-API-CONSISTENCY]

**规范**:
> Delete 一致性：对任意 key，`ContainsKey(k)`、`TryGetValue(k).Success` 与 `Enumerate()` 返回结果必须一致。

**代码**: [DurableDict.cs#L55-L67](../../../src/StateJournal/Objects/DurableDict.cs#L55-L67)

```csharp
public bool TryGetValue(ulong key, out TValue? value) {
    ThrowIfDetached();
    // 先检查是否已从 _committed 删除
    if (_removedFromCommitted.Contains(key) && !_working.ContainsKey(key)) {
        value = default;
        return false;
    }
    if (_working.TryGetValue(key, out value)) { return true; }
    return _committed.TryGetValue(key, out value);
}

public bool ContainsKey(ulong key) {
    ThrowIfDetached();
    if (_working.ContainsKey(key)) { return true; }
    if (_removedFromCommitted.Contains(key)) { return false; }
    return _committed.ContainsKey(key);
}
```

**复现**: existingTest `DurableDictTests.Remove_ThenContainsKey_ReturnsFalse`

**判定**: C — 三个 API 使用一致的逻辑判断 key 存在性。

---

### ✅ C: [S-COMMIT-FAIL-MEMORY-INTACT]

**规范**:
> Commit 失败不改内存：若 Commit 失败，`_committed` 与 `_current` 必须保持调用前语义不变。

**代码**: [DurableDict.cs#L200-L220](../../../src/StateJournal/Objects/DurableDict.cs#L200-L220)

`WritePendingDiff` 只序列化数据，不修改 `_committed`、`_working` 或 `_dirtyKeys`。

**复现**: existingTest `DurableDictTests.WritePendingDiff_DoesNotUpdateState`

**判定**: C — 二阶段提交设计保证 `WritePendingDiff` 不修改内存状态。

---

### ✅ C: [S-COMMIT-SUCCESS-STATE-SYNC]

**规范**:
> Commit 成功后追平：Commit 成功返回后，必须满足 `CommittedState == CurrentState`，并清除 `HasChanges`。

**代码**: [DurableDict.cs#L228-L247](../../../src/StateJournal/Objects/DurableDict.cs#L228-L247)

```csharp
public void OnCommitSucceeded() {
    ThrowIfDetached();

    // 1. 合并 _working 到 _committed
    foreach (var key in _dirtyKeys) {
        if (_working.TryGetValue(key, out var value)) {
            _committed[key] = value;
        }
        else if (_removedFromCommitted.Contains(key)) {
            _committed.Remove(key);
        }
    }

    // 2. 清空变更追踪
    _dirtyKeys.Clear();
    _removedFromCommitted.Clear();
    _working.Clear();

    // 4. 状态转为 Clean
    _state = DurableObjectState.Clean;
}
```

**复现**: existingTest `DurableDictTests.OnCommitSucceeded_ClearsHasChanges`, `DurableDictTests.OnCommitSucceeded_MergesToCommitted`

**判定**: C — `OnCommitSucceeded` 正确合并状态并清除 `HasChanges`。

---

### ✅ C: [S-POSTCOMMIT-WRITE-ISOLATION]

**规范**:
> 隔离性：Commit 成功后，对 `_current` 的后续写入不得影响 `_committed`。

**代码分析**: 实现使用独立的 `_committed` 和 `_working` 字典。`OnCommitSucceeded` 后 `_working` 被清空，后续写入进入新的空 `_working`，不影响 `_committed`。

**复现**: existingTest `DurableDictTests.OnCommitSucceeded_ThenModify_BecomesPersistentDirty`

**判定**: C — 双字典设计天然保证写入隔离。

---

### ✅ C: [S-DIFF-CANONICAL-NO-NETZERO]

**规范**:
> Canonical Diff（规范化）：diff 不得包含 net-zero 变更的 key。

**代码**: [DurableDict.cs#L337-L362](../../../src/StateJournal/Objects/DurableDict.cs#L337-L362)

```csharp
private void UpdateDirtyKeyForSet(ulong key, TValue? newValue) {
    bool hasCommitted = _committed.TryGetValue(key, out var committedValue);
    bool isEqual = hasCommitted
        ? EqualityComparer<TValue>.Default.Equals(newValue, committedValue)
        : false;

    if (isEqual) {
        _dirtyKeys.Remove(key);
    }
    else {
        _dirtyKeys.Add(key);
    }
}
```

**复现**: existingTest `DurableDictTests.DirtyKeys_SetBackToOriginalValue_HasChangesBecomeFalse`

**判定**: C — `_dirtyKeys` 精确追踪，回到原值时移除 dirty 标记，保证 diff 不含 net-zero。

---

### ✅ C: [S-DIFF-REPLAY-DETERMINISM]

**规范**:
> 可重放性：对任意 Committed State S，写出的 diff D 必须满足 `Apply(S, D) == CurrentState`。

**代码分析**: `WritePendingDiff` 遍历 `_dirtyKeys`，为每个变更的 key：
- 若在 `_working` 中：写入当前值
- 若在 `_removedFromCommitted` 中：写入 Tombstone

这保证了 diff 可以将任意 CommittedState 转换为 CurrentState。

**复现**: existingTest `DurableDictTests.TwoPhaseCommit_RoundTrip`

**判定**: C — diff 生成逻辑保证可重放性。

---

### ✅ C: [S-DIRTYKEYS-TRACKING-EXACT]

**规范**:
> _dirtyKeys 精确性：`_dirtyKeys` MUST 精确追踪变更。

**代码**: [DurableDict.cs#L327-L393](../../../src/StateJournal/Objects/DurableDict.cs#L327-L393)

`UpdateDirtyKeyForSet` 和 `UpdateDirtyKeyForRemove` 实现精确追踪逻辑。

**复现**: existingTest 系列 `DurableDictTests.DirtyKeys_*`

**判定**: C — 实现完整覆盖所有追踪场景。

---

### 🔴 V: [A-DISCARDCHANGES-REVERT-COMMITTED]

---
id: "F-DISCARDCHANGES-DETACHED-01"
verdictType: "V"
severity: "Major"
clauseId: "[A-DISCARDCHANGES-REVERT-COMMITTED]"
dedupeKey: "[A-DISCARDCHANGES-REVERT-COMMITTED]|DurableDict.cs:L274|V|detached-throws"
---

**规范**:
> `[A-DURABLEDICT-API-SIGNATURES]` 规定：
> - `void DiscardChanges();` — **Detached 时 no-op（幂等）**

**代码**: [DurableDict.cs#L274-L276](../../../src/StateJournal/Objects/DurableDict.cs#L274-L276)

```csharp
case DurableObjectState.Detached:
    throw new ObjectDetachedException(ObjectId);
```

**复现**: existingTest `DurableDictTests.DiscardChanges_Detached_ThrowsException` — **测试验证了实现抛异常，但这与规范要求不符**。

## ⚖️ Verdict

**判定**: V (Major) — 规范明确要求 `DiscardChanges()` 在 Detached 时为 **no-op（幂等）**，但实现抛出 `ObjectDetachedException`。

## 🛠️ Action

将 `case DurableObjectState.Detached:` 的实现改为 `return;`（no-op）：

```csharp
case DurableObjectState.Detached:
    return;  // no-op, 幂等
```

同时更新测试 `DiscardChanges_Detached_ThrowsException` 为 `DiscardChanges_Detached_IsNoop`。

---

### ❓ U: [S-DURABLEDICT-KEY-ULONG-ONLY]

---
id: "F-DURABLEDICT-KEY-ULONG-01"
verdictType: "U"
severity: "—"
clauseId: "[S-DURABLEDICT-KEY-ULONG-ONLY]"
dedupeKey: "[S-DURABLEDICT-KEY-ULONG-ONLY]|DurableDict.cs:L1|U|key-type-conform"
---

**规范**:
> `[S-DURABLEDICT-KEY-ULONG-ONLY]` `DurableDict` 的 key：`ulong`，采用 `varuint`。

**代码**: [DurableDict.cs#L27](../../../src/StateJournal/Objects/DurableDict.cs#L27)

```csharp
public class DurableDict<TValue> : IDurableObject {
```

**分析**: 实现的 key 类型固定为 `ulong`（非泛型），符合规范。但类本身是泛型 `DurableDict<TValue>`，而规范 §3.4.2 附近有表述：

> **命名约定**：正文中禁止使用 `DurableDict<K, V>` 泛型语法；应使用描述性语句说明 key/value 类型。

## ⚖️ Verdict

**判定**: U — 规范未明确禁止 `DurableDict<TValue>`（只禁止 `DurableDict<K, V>`）。实现的 key 固定为 `ulong` 符合 `[S-DURABLEDICT-KEY-ULONG-ONLY]` 的约束，但泛型形式可能与"不使用泛型"的意图有分歧。

## ❓ Clarifying Questions

1. 规范是否允许 `DurableDict<TValue>` 形式（key 固定 ulong，value 泛型）？
2. 还是要求完全非泛型的 `DurableDict`（value 为 `object`）？

## 📝 Spec Change Proposal

建议在规范 §3.1.5 或 §3.4.2 中明确：

> DurableDict MUST 使用 `ulong` 作为 key 类型。Value 类型**可以**使用泛型 `DurableDict<TValue>` 或非泛型 `DurableDict`（value 为 `object`），实现者自选。

---

## Group D: DurableDict API 条款

### 🔴 V: [A-DURABLEDICT-API-SIGNATURES] — TryGetValue 返回类型

---
id: "F-TRYGETVALUE-SIGNATURE-01"
verdictType: "V"
severity: "Major"
clauseId: "[A-DURABLEDICT-API-SIGNATURES]"
dedupeKey: "[A-DURABLEDICT-API-SIGNATURES]|DurableDict.cs:L55|V|trygetvalue-return"
---

**规范**:
> `[A-DURABLEDICT-API-SIGNATURES]` DurableDict API 签名：
> - `AteliaResult<object> TryGetValue(ulong key);` — Success/NotFound/Detached

**代码**: [DurableDict.cs#L55-L64](../../../src/StateJournal/Objects/DurableDict.cs#L55-L64)

```csharp
public bool TryGetValue(ulong key, out TValue? value) {
    ThrowIfDetached();
    // ...
}
```

## 📝 Evidence

**规范**:
> TryGetValue 返回 Result：使用 `AteliaResult<object>` 而非 `bool TryGetValue(out value)`，保证与整体错误协议一致

**代码**: 实现使用 C# 经典的 `bool TryGetValue(out TValue? value)` 模式，而非 `AteliaResult<TValue>`。

**复现**: manual — 签名不符

## ⚖️ Verdict

**判定**: V (Major) — 实现的 API 签名与规范不一致：

| 规范 | 实现 |
|------|------|
| `AteliaResult<object> TryGetValue(ulong key)` | `bool TryGetValue(ulong key, out TValue? value)` |

规范明确要求使用 `AteliaResult<object>` 返回类型，以支持：
- NotFound 错误码
- Detached 错误码（而非抛异常）
- 与整体错误协议一致

## 🛠️ Action

方案 A（推荐）：修改实现以符合规范：

```csharp
public AteliaResult<TValue?> TryGetValue(ulong key) {
    if (_state == DurableObjectState.Detached) {
        return AteliaResult<TValue?>.Failure(new ObjectDetachedError(ObjectId));
    }
    // ... 查找逻辑 ...
    if (!found) {
        return AteliaResult<TValue?>.Failure(new KeyNotFoundError(key));
    }
    return AteliaResult<TValue?>.Success(value);
}
```

方案 B：如果团队决定保留 C# 惯例签名，需修订规范。

---

### ❓ U: [A-DURABLEDICT-API-SIGNATURES] — Enumerate 命名

---
id: "F-ENUMERATE-NAMING-01"
verdictType: "U"
severity: "—"
clauseId: "[A-DURABLEDICT-API-SIGNATURES]"
dedupeKey: "[A-DURABLEDICT-API-SIGNATURES]|DurableDict.cs:L120|U|enumerate-vs-entries"
---

**规范**:
> `IEnumerable<KeyValuePair<ulong, object>> Enumerate();` — Detached 时 MUST throw

**代码**: [DurableDict.cs#L120-L131](../../../src/StateJournal/Objects/DurableDict.cs#L120-L131)

```csharp
public IEnumerable<KeyValuePair<ulong, TValue?>> Entries {
    get {
        ThrowIfDetached();
        return GetEntriesCore();
    }
}
```

## ⚖️ Verdict

**判定**: U — 规范使用 `Enumerate()` 方法，实现使用 `Entries` 属性。两者语义相同，但命名不一致。

## ❓ Clarifying Questions

1. 规范是否强制要求 `Enumerate()` 方法名？
2. 还是 `Entries` 属性也被接受（更符合 C# 惯例）？

## 📝 Spec Change Proposal

建议在规范中澄清：

> `Enumerate()` 或等价的 `Entries` 属性 — Detached 时 MUST throw

---

### ❓ U: [A-DURABLEDICT-API-SIGNATURES] — Detached 时 HasChanges 行为

---
id: "F-HASCHANGES-DETACHED-01"
verdictType: "U"
severity: "—"
clauseId: "[S-DETACHED-ACCESS-TIERING]"
dedupeKey: "[S-DETACHED-ACCESS-TIERING]|DurableDict.cs:L46|U|haschanges-detached"
---

**规范**:
> `[S-DETACHED-ACCESS-TIERING]` Detached 对象的访问分层：
> | 访问类型 | 示例 API | Detached 行为 |
> |----------|----------|---------------|
> | **元信息访问** | `State`, `Id`, `ObjectId` | MUST NOT throw（O(1) 复杂度） |
> | **语义数据访问** | `TryGetValue`, `Set`, `Remove`, `Count`, `Enumerate`, `HasChanges` | MUST throw `ObjectDetachedException` |

**代码**: [DurableDict.cs#L46-L47](../../../src/StateJournal/Objects/DurableDict.cs#L46-L47)

```csharp
/// <remarks>
/// 复杂度 O(1)：直接检查 <c>_dirtyKeys.Count</c>。
/// </remarks>
public bool HasChanges => _dirtyKeys.Count > 0;
```

**分析**: `HasChanges` 属性**不检查 Detached 状态**，在 Detached 后会返回 `false`（因为 `DiscardChanges` 清空了 `_dirtyKeys`）而不是抛异常。

## ⚖️ Verdict

**判定**: U — 规范将 `HasChanges` 归类为"语义数据访问"，Detached 时 MUST throw。但当前实现不抛异常。

然而，这可能是规范分类问题：
- `HasChanges` 语义上是"是否有未提交变更"
- Detached 对象显然没有"未提交变更"的概念（因为它不会被提交）
- 返回 `false` 或抛异常都有一定道理

## ❓ Clarifying Questions

1. `HasChanges` 是否应该归类为"元信息访问"（不抛异常，返回 false）？
2. 还是规范分类正确，实现需要修复？

## 📝 Spec Change Proposal

建议之一：
- 方案 A：将 `HasChanges` 移至"元信息访问"类别
- 方案 B：保持规范，要求实现在 Detached 时抛异常

---

## 📋 测试覆盖分析

| 条款 | 测试文件 | 覆盖状态 |
|:-----|:---------|:---------|
| [F-KVPAIR-HIGHBITS-RESERVED] | ValueTypeTests.cs | ✅ 完整 |
| [F-UNKNOWN-VALUETYPE-REJECT] | ValueTypeTests.cs | ✅ 完整 |
| [S-DIFF-KEY-SORTED-UNIQUE] | DiffPayloadTests.cs | ✅ 完整 |
| [S-PAIRCOUNT-ZERO-LEGALITY] | DiffPayloadTests.cs | ⚠️ 部分（未测 ObjectVersionRecord 层） |
| [S-OVERLAY-DIFF-NONEMPTY] | — | ⚠️ 缺失（Commit 层未实现） |
| [S-WORKING-STATE-TOMBSTONE-FREE] | DurableDictTests.cs | ✅ 完整 |
| [S-DELETE-API-CONSISTENCY] | DurableDictTests.cs | ✅ 完整 |
| [S-COMMIT-FAIL-MEMORY-INTACT] | DurableDictTests.cs | ✅ 完整 |
| [S-COMMIT-SUCCESS-STATE-SYNC] | DurableDictTests.cs | ✅ 完整 |
| [S-POSTCOMMIT-WRITE-ISOLATION] | DurableDictTests.cs | ✅ 完整 |
| [S-DIFF-CANONICAL-NO-NETZERO] | DurableDictTests.cs | ✅ 完整 |
| [S-DIFF-REPLAY-DETERMINISM] | DurableDictTests.cs | ✅ 完整 |
| [S-DIRTYKEYS-TRACKING-EXACT] | DurableDictTests.cs | ✅ 完整 |
| [A-DISCARDCHANGES-REVERT-COMMITTED] | DurableDictTests.cs | ❌ 测试验证了错误行为 |
| [A-DURABLEDICT-API-SIGNATURES] | DurableDictTests.cs | ⚠️ 部分（签名差异未测） |

---

## 🔍 遗留问题

1. **ObjectVersionRecord 解析层**尚未实现，`[S-PAIRCOUNT-ZERO-LEGALITY]` 的验证无法在 DiffPayload 层执行。
2. **Commit 层**尚未实现，`[S-OVERLAY-DIFF-NONEMPTY]` 的 writer 端约束无法验证。
3. **VersionIndex** 复用 DurableDict 的实现尚未验证。

---

## ✅ 审阅结论

Objects 模块整体实现质量良好，大部分条款符合规范。发现的问题：

| 问题 | 严重度 | 建议 |
|:-----|:-------|:-----|
| DiscardChanges Detached 时抛异常 | Major | 改为 no-op |
| TryGetValue 返回类型不符 | Major | 改为 AteliaResult 或修订规范 |
| Enumerate vs Entries 命名 | Minor | 澄清规范 |
| HasChanges Detached 行为 | Minor | 澄清规范分类 |
| DurableDict 泛型形式 | Minor | 澄清规范意图 |

---

*审阅完成时间*: 2025-12-26
*审阅员*: CodexReviewer
