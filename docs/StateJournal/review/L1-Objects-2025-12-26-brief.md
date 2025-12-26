# L1 审阅任务包：Objects 模块

> **briefId**: L1-Objects-2025-12-26-001
> **reviewType**: L1
> **createdBy**: Team Leader
> **createdAt**: 2025-12-26

---

## 🎯 焦点

**模块**：`atelia/src/StateJournal/Objects/`

**specRef**:
- commit: HEAD (main branch)
- files:
  - `atelia/docs/StateJournal/mvp-design-v2.md` — §3.4.2 (DiffPayload), §3.4.3 (DurableDict 不变式)

---

## 📋 条款清单

### Group A: ValueType

| ID | 标题 | 要点 |
|:---|:-----|:-----|
| `[F-KVPAIR-HIGHBITS-RESERVED]` | 高 4 位预留 | 低 4 bit = ValueType，高 4 bit MUST 写 0，reader 见非 0 格式错误 |
| `[F-UNKNOWN-VALUETYPE-REJECT]` | 未知 ValueType | 低 4 bit 不在 {0,1,2,3,4} MUST fail-fast |

**规范原文**：

> **[F-KVPAIR-HIGHBITS-RESERVED]** 低 4 bit：`ValueType`（高 4 bit 预留，MVP 必须写 0；reader 见到非 0 视为格式错误）

> **[F-UNKNOWN-VALUETYPE-REJECT]**：reader 遇到未知 ValueType（低 4 bit 不在 `{0,1,2,3,4}`）或高 4 bit 非 0，MUST 视为格式错误并失败（ErrorCode: `StateJournal.CorruptedRecord`）。

### Group B: DiffPayload 格式

| ID | 标题 | 要点 |
|:---|:-----|:-----|
| `[S-DIFF-KEY-SORTED-UNIQUE]` | Key 唯一升序 | 单个 diff 内 key MUST 严格唯一且升序 |
| `[S-PAIRCOUNT-ZERO-LEGALITY]` | PairCount=0 合法性 | 仅 Base Version (PrevVersionPtr=0) 允许 |
| `[S-OVERLAY-DIFF-NONEMPTY]` | Overlay diff 非空 | writer MUST NOT 为无变更对象写版本 |

**规范原文**：

> **[S-DIFF-KEY-SORTED-UNIQUE]** Key 唯一 + 升序：单个 diff 内 key 必须严格唯一，且按 key 升序排列（确定性输出）。

> **[S-PAIRCOUNT-ZERO-LEGALITY]**：`PairCount == 0` 仅在 `PrevVersionPtr == 0`（Base Version）时合法，表示"空字典的完整 state"。若 `PrevVersionPtr != 0`（Overlay diff）且 `PairCount == 0`，reader MUST 视为格式错误。

> **[S-OVERLAY-DIFF-NONEMPTY]**：writer MUST NOT 为"无任何变更"的对象写入 `ObjectVersionRecord`。若对象无变更（`HasChanges == false`），不应生成新版本。

### Group C: DurableDict 不变式

| ID | 标题 | 要点 |
|:---|:-----|:-----|
| `[S-WORKING-STATE-TOMBSTONE-FREE]` | Working State 纯净 | tombstone 不得出现在可枚举视图 |
| `[S-DELETE-API-CONSISTENCY]` | Delete 一致性 | ContainsKey/TryGetValue/Enumerate 必须一致 |
| `[S-COMMIT-FAIL-MEMORY-INTACT]` | Commit 失败不改内存 | 失败后内存状态保持不变 |
| `[S-COMMIT-SUCCESS-STATE-SYNC]` | Commit 成功追平 | CommittedState == CurrentState |
| `[S-POSTCOMMIT-WRITE-ISOLATION]` | 隔离性 | 后续写入不影响 _committed |
| `[S-DIFF-CANONICAL-NO-NETZERO]` | Canonical Diff | 不含 net-zero 变更 |
| `[S-DIFF-REPLAY-DETERMINISM]` | 可重放性 | Apply(S, D) == CurrentState |
| `[S-DIRTYKEYS-TRACKING-EXACT]` | _dirtyKeys 精确性 | 精确追踪变更 |
| `[A-DISCARDCHANGES-REVERT-COMMITTED]` | DiscardChanges | 重置为 _committed 副本 |
| `[S-DURABLEDICT-KEY-ULONG-ONLY]` | Key 类型 | key 固定为 ulong |

**规范原文摘要**：

> **[S-WORKING-STATE-TOMBSTONE-FREE]** Working State 纯净性：在任何对外可读/可枚举的状态视图中，tombstone 不得作为值出现；Delete 的语义是"key 不存在"。

> **[S-DELETE-API-CONSISTENCY]** Delete 一致性：对任意 key，`ContainsKey(k)`、`TryGetValue(k).Success` 与 `Enumerate()` 返回结果必须一致。

> **[S-COMMIT-FAIL-MEMORY-INTACT]** Commit 失败不改内存：若 Commit 失败，`_committed` 与 `_current` 必须保持调用前语义不变。

> **[S-COMMIT-SUCCESS-STATE-SYNC]** Commit 成功后追平：Commit 成功返回后，必须满足 `CommittedState == CurrentState`，并清除 `HasChanges`。

> **[S-POSTCOMMIT-WRITE-ISOLATION]** 隔离性：Commit 成功后，对 `_current` 的后续写入不得影响 `_committed`。

> **[S-DIFF-CANONICAL-NO-NETZERO]** Canonical Diff（规范化）：diff 不得包含 net-zero 变更的 key。

> **[S-DIFF-REPLAY-DETERMINISM]** 可重放性：对任意 Committed State S，写出的 diff D 必须满足 `Apply(S, D) == CurrentState`。

> **[S-DIRTYKEYS-TRACKING-EXACT]** _dirtyKeys 精确性：`_dirtyKeys` MUST 精确追踪变更。

> **[A-DISCARDCHANGES-REVERT-COMMITTED]** DiscardChanges：将 `_current` 重置为 `_committed` 的副本，并清空 `_dirtyKeys`。

> **[S-DURABLEDICT-KEY-ULONG-ONLY]** `DurableDict` 的 key：`ulong`，采用 `varuint`。

### Group D: DurableDict API

| ID | 标题 | 要点 |
|:---|:-----|:-----|
| `[A-DURABLEDICT-API-SIGNATURES]` | API 签名 | TryGetValue 返回 Result，Remove 返回 bool |

**规范原文**：

> **[A-DURABLEDICT-API-SIGNATURES]** DurableDict API 签名：
> - `AteliaResult<object> TryGetValue(ulong key);` — Success/NotFound/Detached
> - `bool ContainsKey(ulong key);` — Detached 时 MUST throw
> - `int Count { get; }` — Detached 时 MUST throw
> - `IEnumerable<KeyValuePair<ulong, object>> Enumerate();` — Detached 时 MUST throw
> - `void Set(ulong key, object value);` — Detached 时 MUST throw
> - `bool Remove(ulong key);` — Detached 时 MUST throw；返回是否存在
> - `void DiscardChanges();` — Detached 时 no-op（幂等）

---

## 🔍 代码入口

| 文件 | 职责 | 条款关联 |
|:-----|:-----|:---------|
| `Objects/ValueType.cs` | ValueType 枚举及验证 | F-KVPAIR-HIGHBITS-RESERVED, F-UNKNOWN-VALUETYPE-REJECT |
| `Objects/DiffPayload.cs` | DiffPayload 编解码 | S-DIFF-KEY-SORTED-UNIQUE, S-PAIRCOUNT-ZERO-LEGALITY |
| `Objects/DurableDict.cs` | 持久化字典实现 | S-WORKING-STATE-*, S-COMMIT-*, S-DIFF-*, A-DURABLEDICT-* |

**相关测试**：
- `Objects/ValueTypeTests.cs`
- `Objects/DiffPayloadTests.cs`
- `Objects/DurableDictTests.cs`

---

## 📚 依赖上下文

**前置条款**（来自 Core）：
- `[F-VARINT-CANONICAL-ENCODING]` — VarInt 编码
- `[F-DECODE-ERROR-FAILFAST]` — 解码错误处理

---

## 📋 审阅指令

**角色**：L1 符合性法官

### MUST DO

1. 逐条款检查代码是否满足规范语义
2. 每个 Finding 必须引用：条款原文 + 代码位置 + 复现方式
3. 遇到规范未覆盖的行为 → 标记为 `U`（Underspecified），不是 `V`
4. DurableDict 双字典模型的正确性是重点

### MUST NOT

1. 不评论代码风格（那是 L3）
2. 不假设规范未写的约束
3. 不产出无法复现的 Finding

### 特别关注

- **ValueType 验证**：检查 `ValidateKeyValuePairType` 是否正确拒绝高 4 位非 0 和未知类型
- **DiffPayload 写入顺序**：检查 `DiffPayloadWriter` 是否强制 key 升序
- **DiffPayload 读取验证**：检查 `DiffPayloadReader` 是否验证 key 唯一性（delta > 0）
- **DurableDict 状态转换**：检查状态机是否正确
- **_dirtyKeys 精确性**：检查 Set/Remove 后 _dirtyKeys 的维护是否正确
- **API 签名**：注意规范要求 `TryGetValue` 返回 `AteliaResult<object>`，但实现可能不同

---

## 📤 输出格式

**文件**：`atelia/docs/StateJournal/review/L1-Objects-2025-12-26-findings.md`

**格式**：EVA-v1（参见 Recipe）

---

## ⚠️ 注意事项

1. **TryGetValue 返回类型**：规范要求返回 `AteliaResult<object>`，但实现使用 `bool TryGetValue(out TValue?)`。这可能是 **U** 或 **V**——需要判断规范是否为 MUST 还是建议。

2. **泛型 vs 非泛型**：规范提到"DurableDict 不使用泛型"，但实现是 `DurableDict<TValue>`。需要检查是否违反规范或规范有歧义。

3. **DiscardChanges 在 Detached 时**：规范说"no-op（幂等）"，但实现 throw。需要核实规范要求。
