# DurableDict DiffPayload 二进制布局

> **来源**：从 `mvp-design-v2.md` §3.4.2 提取。
> 本文档是 DurableDict diff payload 线格式的 SSOT（唯一权威来源）。
>
> **依赖**：ValueType 枚举值定义见 [primitive-serialization.md](primitive-serialization.md#valuetype低-4-bit)。

---

## 三层语义与表示法约束（MVP 固定）

- **Working State（`_current`）**：不存储 tombstone。Remove 的效果必须体现为"移除 key"；枚举/ContainsKey 等读 API 只体现"存在/不存在"。
- **ChangeSet（Commit 时的 diff 算法）**：通过比较 `_committed` 与 `_current` 自动生成，允许识别 Remove 操作（当 key 存在于 `_committed` 但不存在于 `_current` 时）。
- **DiffPayload（序列化差分）**：用 `Val_Tombstone` 表示 Remove；Apply 时必须转化为"移除 key"。

关键约束（保证语义正确）：

- tombstone 必须是 **与用户可写入的 `null` 不同的值编码**。
- 因此 value 在逻辑上至少需要三个状态：
  - `NoChange`：不出现在 diff 里（**通过 diff 中缺失该 key 表达，不在 payload 中编码**）
  - `Set(value)`：显式设置为某个值（包含用户的 `null`）
  - `Delete`：tombstone

编码建议：

- **Working State（`_current`/`_committed`）**：使用标准 `Dictionary<K, V>`，Delete 直接移除 key。
- **DiffPayload**：tombstone 用元数据 tag 表达（`KeyValuePairType.Val_Tombstone`），而不是用"特殊值"。

---

## DurableDict diff payload（二进制布局，MVP 固定）

本节将 `DurableDict` 的 diff payload 规格化，满足：

- 顺序读即可 apply（不要求 mmap 随机定位）
- key 采用"有序 pairs + delta-of-prev"压缩：写 `FirstKey`，后续写 `KeyDeltaFromPrev`（key 为 `ulong`；delta 固定为非负）
- value 通过元数据 tag 区分 `Null / ObjRef(ObjectId) / VarInt / Tombstone / Ptr64`

符号约定：

- `varuint`：无符号 varint
- `varint`：有符号 varint（ZigZag+varint，仅用于 value，不用于 Dict key）

### payload

- `PairCount`: `varuint`
	- 语义：本 payload 中包含的 pair 数量。
	- 允许为 0（用于 Checkpoint Base/full-state 表示"空字典"）。
- 若 `PairCount == 0`：payload 到此结束。
- 否则（`PairCount > 0`）：
	- `FirstKey`: `varuint`
	- `FirstPair`：
	- `KeyValuePairType`: `byte`
		- **[F-KVPAIR-HIGHBITS-RESERVED]** 低 4 bit：`ValueType`（高 4 bit 预留，MVP 必须写 0；reader 见到非 0 视为格式错误）
	- `Value`：由 `ValueType` 决定（取值定义见 [primitive-serialization.md](primitive-serialization.md#valuetype低-4-bit)）
		- `Val_Null`：无 payload
		- `Val_Tombstone`：无 payload（表示删除）
		- `Val_ObjRef`：`ObjectId`（varuint）
		- `Val_VarInt`：`varint`
		- `Val_Ptr64`：`u64 LE`（用于 `VersionIndex` 的 `ObjectId -> ObjectVersionPtr`）
	- `RemainingPairs`：重复 `PairCount-1` 次
	- `KeyValuePairType`: `byte`（同上，见 [F-KVPAIR-HIGHBITS-RESERVED]）
	- `KeyDeltaFromPrev`: `varuint`
	- `Value`：由 `ValueType` 决定（同上）

### Key 还原规则（MVP 固定）

- 若 `PairCount == 0`：无 key。
- 否则：
	- `Key[0] = FirstKey`
	- `Key[i] = Key[i-1] + KeyDeltaFromPrev(i)`（对 `i >= 1`）

### ValueType

ValueType（低 4 bit，MVP 固定）：取值定义详见 [primitive-serialization.md](primitive-serialization.md#valuetype低-4-bit) 枚举值速查表。

> **MVP 范围说明**：MVP 仅实现 5 种 ValueType（Val_Null / Val_Tombstone / Val_ObjRef / Val_VarInt / Val_Ptr64）。`float`/`double`/`bool` 等类型将在后续版本添加对应的 ValueType 枚举值。

### 约束

- 同一个 diff 内不允许出现重复 key（MVP 直接视为编码错误）。
- 为保证 `KeyDeltaFromPrev` 为非负且压缩稳定：MVP writer 必须按 key 严格升序写出 pairs（reader 可顺序 apply；编码约束要求有序）。
- 对于空变更（overlay diff）：writer 不应为"无任何 upsert/delete"的对象写入 `ObjectVersionRecord`（因此 overlay diff 应满足 `PairCount > 0`）。
- 对于 Checkpoint Base/full-state：允许 `PairCount == 0`，表示"空字典的完整 state"。

### PairCount=0 合法性条款（MUST）

- **[S-PAIRCOUNT-ZERO-LEGALITY]**：`PairCount == 0` 仅在 `PrevVersionPtr == 0`（Base Version）时合法，表示"空字典的完整 state"。若 `PrevVersionPtr != 0`（Overlay diff）且 `PairCount == 0`，reader MUST 视为格式错误（ErrorCode: `StateJournal.InvalidFraming`）。
- **[S-OVERLAY-DIFF-NONEMPTY]**：writer MUST NOT 为"无任何变更"的对象写入 `ObjectVersionRecord`。若对象无变更（`HasChanges == false`），不应生成新版本。

### 未知 ValueType 处理条款（MUST）

- **[F-UNKNOWN-VALUETYPE-REJECT]**：reader 遇到未知 ValueType（低 4 bit 不在 `{0,1,2,3,4}`）或高 4 bit 非 0，MUST 视为格式错误并失败（ErrorCode: `StateJournal.CorruptedRecord`）。

### 实现提示

- writer：写 diff 前先对 ChangeSet keys 排序；写 `FirstKey = keys[0]`；后续写 `KeyDeltaFromPrev = keys[i] - keys[i-1]`。
- reader：顺序读取 pairs；对第 1 个 pair 使用 `FirstKey`，对后续 pair 通过累加 `KeyDeltaFromPrev` 还原 key，然后对内存 dict 执行 set/remove。

### VersionIndex 与 Dict 的关系（落地说明）

- MVP 中 `DurableDict` 统一为**无泛型**的底层原语：key 编码固定为 `FirstKey + KeyDeltaFromPrev`（delta-of-prev）。
- `VersionIndex` 复用 `DurableDict`（key 为 `ObjectId` as `ulong`，value 使用 `Val_Ptr64` 编码 `ObjectVersionPtr`）。

> **命名约定**：正文中禁止使用 `DurableDict<K, V>` 泛型语法；应使用描述性语句说明 key/value 类型。
