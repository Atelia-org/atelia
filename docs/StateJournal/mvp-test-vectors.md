# StateJournal MVP 测试向量与检查清单

> **版本**：v2（2025-12-20 更新，根据畅谈会共识修订）
> **配套文档**：`mvp-design-v2.md`
>
> 覆盖范围：
> - **`_dirtyKeys` 不变式**（P0-1，监护人建议采纳）
> - **Magic-as-Separator**（P0-2，监护人建议采纳）
> - **首次 Commit 语义**（P0-7）
> - **Value 类型边界**（P0-4）
> - **CommitAll API 语义**（P0-6）
> - ELOG framing（data/meta；DHD3/DHM3；CRC32C）
> - varint（canonical 最短编码）
> - DurableDict diff（delta-of-prev key；tombstone）

---

## 0. 约定

- **Magic-as-Separator**：Magic 是 Record 分隔符，不属于任何 Record
- **文件结构**：`[Magic][Record1][Magic][Record2]...[Magic]`
- **Record 格式**：`[Len][Payload][Pad][Len][CRC32C]`（不含 Magic）
- **Ptr64**：指向 Record 的 `Len` 字段起始位置（第一个 Magic 之后）
- 字节序：除 varint 外，定长整数均为 little-endian
- varint：protobuf 风格 base-128，要求 canonical 最短编码
- CRC32C：覆盖 `Payload + Pad + TailLen`

---

## 条款编号映射

> 本节映射测试场景到 `mvp-design-v2.md` 中的规范条款（稳定语义锚点格式）。

| 条款 ID | 规范条款 | 对应测试用例 |
|---------|----------|--------------|
| `[F-RECORDKIND-DOMAIN-ISOLATION]` | RecordKind 域隔离（data/meta 各有独立枚举空间） | — |
| `[F-MAGIC-RECORD-SEPARATOR]` | Magic 是 Record Separator | ELOG-EMPTY-001, ELOG-SINGLE-001 |
| `[F-HEADLEN-TAILLEN-SYMMETRY]` | HeadLen == TailLen，否则视为损坏 | ELOG-LEN-001, ELOG-BAD-001 |
| `[F-RECORD-4B-ALIGNMENT]` | HeadLen % 4 == 0 且 Record 起点 4B 对齐 | ELOG-BAD-003 |
| `[F-PTR64-NULL-AND-ALIGNMENT]` | Ptr64 == 0 表示 null；否则 Ptr64 % 4 == 0 | PTR-OK-001, PTR-BAD-001/002 |
| `[F-CRC32C-PAYLOAD-COVERAGE]` | CRC32C 覆盖范围：Payload + Pad + TailLen | ELOG-OK-001, ELOG-BAD-002 |
| `[F-VARINT-CANONICAL-ENCODING]` | VarInt canonical 最短编码 | VARINT-OK-001, VARINT-BAD-001/002/003 |
| `[F-DECODE-ERROR-FAILFAST]` | VarInt 解码错误策略（EOF/溢出/非 canonical 失败） | VARINT-BAD-001/002/003 |
| `[F-KVPAIR-HIGHBITS-RESERVED]` | ValueType 高 4 bit 必须写 0；reader 见到非 0 视为格式错误 | DICT-BAD-005 |
| `[F-UNKNOWN-VALUETYPE-REJECT]` | 未知 ValueType MUST 视为格式错误 | DICT-BAD-006 |
| `[S-DIRTYSET-OBJECT-PINNING]` | Dirty Set MUST 持有对象强引用 | — |
| `[S-IDENTITY-MAP-KEY-COHERENCE]` | Identity Map 与 Dirty Set 的 key 必须等于 ObjectId | — |
| `[S-DIRTY-OBJECT-GC-PROHIBIT]` | Dirty 对象不得被 GC 回收 | — |
| `[S-NEW-OBJECT-AUTO-DIRTY]` | 新建对象 MUST 立即加入 Dirty Set | FIRST-COMMIT-002/003 |
| `[F-MAGIC-IS-FENCE]` | 术语约束：Magic 是栅栏，不属于 Record | — |
| `[S-WORKING-STATE-TOMBSTONE-FREE]` | Working State 纯净性（tombstone 不得作为值出现） | DICT-OK-003/004 |
| `[S-DELETE-API-CONSISTENCY]` | Delete 一致性（ContainsKey/TryGetValue/Enumerate 一致） | DICT-OK-003 |
| `[S-COMMIT-FAIL-MEMORY-INTACT]` | Commit 失败不改内存 | DIRTY-001/002/003 |
| `[S-COMMIT-SUCCESS-STATE-SYNC]` | Commit 成功后追平 | COMMIT-ALL-001 |
| `[S-POSTCOMMIT-WRITE-ISOLATION]` | 隔离性（Commit 后写入不影响 _committed） | — |
| `[S-DIFF-KEY-SORTED-UNIQUE]` | Key 唯一 + 升序 | DICT-BAD-002/003 |
| `[S-DIFF-CANONICAL-NO-NETZERO]` | Canonical Diff（规范化：不含 net-zero 变更） | DIRTY-001/002/003 |
| `[S-DIFF-REPLAY-DETERMINISM]` | 可重放性（Apply(S, D) == CurrentState） | DICT-OK-001/002 |
| `[S-DIRTYKEYS-TRACKING-EXACT]` | _dirtyKeys 精确性 | DIRTY-001/002/003/004/005 |
| `[S-HEAP-COMMIT-FAIL-INTACT]` | CommitAll 失败不改内存 | — |
| `[S-COMMIT-FAIL-RETRYABLE]` | CommitAll 可重试 | — |
| `[S-PAIRCOUNT-ZERO-LEGALITY]` | PairCount=0 仅在 Base Version (PrevVersionPtr=0) 时合法 | DICT-OK-EMPTY-BASE-001, DICT-BAD-001 |
| `[A-DISCARDCHANGES-REVERT-COMMITTED]` | DiscardChanges MUST | — |
| `[A-COMMITALL-FLUSH-DIRTYSET]` | CommitAll() 无参重载 MUST | COMMIT-ALL-001 |
| `[A-COMMITALL-SET-NEWROOT]` | CommitAll(IDurableObject) SHOULD | COMMIT-ALL-002 |
| `[A-DIRTYSET-OBSERVABILITY]` | Dirty Set 可见性 API SHOULD | — |
| `[R-RESYNC-DISTRUST-TAILLEN]` | Resync 不得信任损坏 TailLen 并做跳跃 | ELOG-TRUNCATE-001/002, ELOG-BAD-003/004 |
| `[R-META-AHEAD-BACKTRACK]` | Meta 领先 Data 按撕裂提交处理 | META-RECOVER-002, META-RECOVER-003 |
| `[R-DATATAIL-TRUNCATE-GARBAGE]` | 崩溃恢复截断后文件仍以 Magic 分隔符结尾 | — |
| `[R-META-RESYNC-SAME-AS-DATA]` | Meta 文件 resync 策略与 data 相同 | META-RECOVER-001 |

---

## 1. `_dirtyKeys` 不变式测试（P0-1 新增）

### 1.1 净零变更（Set 后 Delete 同一个新 key）

用例 DIRTY-001
- Given：committed 为空或不含 key=42
- When：`dict.Set(42, 100); dict.Remove(42);`
- Then：
  - `dict.HasChanges == false`（`_dirtyKeys.Count == 0`）
  - `dict.ComputeDiff().Count == 0`

### 1.2 改回原值

用例 DIRTY-002
- Given：committed = {42: 100}
- When：`dict.Set(42, 200); dict.Set(42, 100);`
- Then：`dict.HasChanges == false`

### 1.3 删除后恢复

用例 DIRTY-003
- Given：committed = {42: 100}
- When：`dict.Remove(42); dict.Set(42, 100);`
- Then：`dict.HasChanges == false`

### 1.4 多 key 独立性

用例 DIRTY-004
- Given：committed = {1: 10, 2: 20, 3: 30}
- When：`dict.Set(2, 200);`  // 只修改 key=2
- Then：
  - `dict.HasChanges == true`
  - `ComputeDiff()` 只含 1 个 entry（key=2）

### 1.5 diff 只覆盖 dirty keys（性能）

用例 DIRTY-005
- Given：committed 有 10000 keys
- When：仅修改 3 个 keys
- Then：`ComputeDiff()` 只返回 3 个 entries（升序、无重复）

---

## 2. 首次 Commit 语义（P0-7 新增）

### 2.1 空仓库初始状态

用例 FIRST-COMMIT-001
```csharp
var heap = StateJournal.Open(emptyPath);
Assert.Equal(0, heap.CurrentEpoch);      // 隐式空状态
Assert.Equal(16UL, heap.NextObjectId);   // 从 16 开始分配（0..15 保留）
Assert.Null(heap.RootObjectId);          // 无 root
```

### 2.2 首次 CommitAll

用例 FIRST-COMMIT-002
```csharp
var heap = StateJournal.Open(emptyPath);
var root = heap.CreateObject<DurableDict>();
heap.CommitAll(root.Id);
Assert.Equal(1, heap.CurrentEpoch);      // 首次 commit
Assert.Equal(root.Id, heap.RootObjectId);
```

### 2.3 新建对象首版本

用例 FIRST-COMMIT-003
```csharp
var dict = heap.CreateObject<DurableDict>();
dict.Set(1, 100);
heap.CommitAll(dict.Id);
var record = ReadObjectVersionRecord(dict.VersionPtr);
Assert.Equal(0UL, record.PrevVersionPtr);  // 首版本 PrevPtr = 0
```

---

## 3. Value 类型边界（P0-4 新增）

### 3.1 支持的类型

用例 VALUE-OK-001
```csharp
dict.Set(1, null);              // Val_Null ✓
dict.Set(2, 42L);               // Val_VarInt ✓
dict.Set(3, otherDurableObj);   // Val_ObjRef ✓
dict.Set(4, new Ptr64(0x1000)); // Val_Ptr64 ✓（仅 VersionIndex）
```

### 3.2 不支持的类型（MVP 必须拒绝）

用例 VALUE-BAD-001
```csharp
Assert.Throws<NotSupportedException>(() => dict.Set(5, 3.14f));   // float ✗
Assert.Throws<NotSupportedException>(() => dict.Set(6, 3.14));    // double ✗
Assert.Throws<NotSupportedException>(() => dict.Set(7, true));    // bool ✗
Assert.Throws<NotSupportedException>(() => dict.Set(8, "hello")); // string ✗
```

---

## 4. CommitAll API 语义（P0-6 修订）

### 4.1 CommitAll 提交所有 dirty 对象

用例 COMMIT-ALL-001
```csharp
var root = heap.LoadObject<DurableDict>(rootId);
var child = heap.CreateObject<DurableDict>();
root.Set(1, child);  // root 变 dirty
child.Set(1, 100);   // child 变 dirty

heap.CommitAll(root.Id);

Assert.False(root.HasChanges);   // 已提交
Assert.False(child.HasChanges);  // 也已提交
```

### 4.2 newRootId 仅决定 MetaCommitRecord.RootObjectId

用例 COMMIT-ALL-002
```csharp
var newRoot = heap.CreateObject<DurableDict>();
// newRoot 本身没有修改（不 dirty）

heap.CommitAll(newRoot.Id);  // 切换 root

var head = heap.GetHeadCommitRecord();
Assert.Equal(newRoot.Id, head.RootObjectId);
```

---

## 5. ELOG framing（Magic-as-Separator，P0-2 修订）

### 5.1 空文件

用例 ELOG-EMPTY-001
- Given：文件内容 = `[Magic]`（仅分隔符，4 bytes）
- Then：
  - `reverse_scan()` 返回 0 条 record
  - `Open()` 成功，`Epoch = 0`

### 5.2 单条 Record

用例 ELOG-SINGLE-001
- Given：文件结构 = `[Magic][Record][Magic]`
- Then：
  - `reverse_scan()` 返回 1 条 record
  - Record 起始位置 = 4（Ptr64 指向 Len 字段）

### 5.3 双条 Record

用例 ELOG-DOUBLE-001
- Given：文件结构 = `[Magic][Record1][Magic][Record2][Magic]`
- Then：`reverse_scan()` 按逆序返回 Record2, Record1

### 5.4 Len 计算（不含 Magic）

用例 ELOG-LEN-001
- Given：PayloadLen 分别为 `4k, 4k+1, 4k+2, 4k+3`
- Then：
  - PadLen = `(4 - PayloadLen % 4) % 4`
  - `HeadLen = 4 + PayloadLen + PadLen + 4 + 4`（不含 Magic）

### 5.5 Payload 含 Magic 字节

用例 ELOG-MAGIC-IN-PAYLOAD-001
- Given：payload 恰好包含 `44 48 44 33`（Magic_Data）
- Then：
  - CRC32C 校验通过时，正常解析
  - resync 时遇到 payload 中的 Magic，CRC 校验失败应继续向前扫描

### 5.6 截断测试

用例 ELOG-TRUNCATE-001
- Given：原文件 `[Magic][Record][Magic]`，截断为 `[Magic][Record]`
- Then：`Open()` 应回退到上一个有效 commit（或报告空仓库）

用例 ELOG-TRUNCATE-002
- Given：截断在 record 中间
- Then：`Open()` 识别出不完整 record，回退到上一个有效 commit

### 5.7 损坏记录（负例）

用例 ELOG-BAD-001（HeadLen != TailLen）
- Then：该 record 视为损坏

用例 ELOG-BAD-002（CRC32C 不匹配）
- Then：CRC32C 校验失败，不作为有效 head

用例 ELOG-BAD-003（Start 非 4B 对齐）
- Then：视为损坏

---

## 1. ELOG framing（data/meta）

### 1.1 有效记录（正例）

用例 ELOG-OK-001（最小可解析 record）
- Given：`Magic` 为 `DHD3` 或 `DHM3`；payload 至少 1 byte（kind）。
- When：写入 `Magic + HeadLen占位 + Payload + Pad + TailLen + CRC32C + 回填HeadLen`。
- Then：
  - `HeadLen == TailLen`。
  - `HeadLen % 4 == 0`。
  - `Start` 4B 对齐。
  - CRC32C 校验通过。
  - 反向扫尾能枚举到该条记录。

用例 ELOG-OK-002（PadLen 取值覆盖）
- Given：PayloadLen 分别为 `4k, 4k+1, 4k+2, 4k+3`。
- Then：PadLen 分别为 `0, 3, 2, 1`；下一条 Magic 起点 4B 对齐。

### 1.2 损坏记录（负例；必须拒绝/跳过）

用例 ELOG-BAD-001（HeadLen != TailLen）
- Given：尾部长度与头部长度不一致。
- Then：该 record 视为损坏；反向扫尾应停止在上一条有效 record（或跳过该条取决于实现策略，但不得误接受）。

用例 ELOG-BAD-002（CRC32C 不匹配）
- Given：只篡改 payload 任意 1 byte。
- Then：CRC32C 校验失败；反向扫尾不得将其作为有效 head。

用例 ELOG-BAD-003（Start 非 4B 对齐）
- Given：构造 TailLen 导致 `Start % 4 != 0`。
- Then：视为损坏。

用例 ELOG-BAD-004（TailLen 超界）
- Given：`TailLen > fileSize` 或 `TailLen < 16`。
- Then：视为损坏。

---

## 2. Meta 恢复与“撕裂提交”

用例 META-RECOVER-001（正常 head）
- Given：meta 尾部有多条 commit record，最后一条有效。
- Then：Open 选择最后一条。

用例 META-RECOVER-002（meta 领先 data：DataTail 越界）
- Given：最后一条 meta record CRC 通过，但 `DataTail` > data 文件长度。
- Then：Open 必须回退到上一条 meta record。

用例 META-RECOVER-003（meta 指针不可解引用）
- Given：最后一条 meta record CRC 通过，但 `VersionIndexPtr` 指向不存在/损坏 record。
- Then：Open 必须回退到上一条 meta record。

---

## 3. Ptr64（4B unit pointer）

用例 PTR-OK-001（指针可解析）
- Given：`Ptr64` 指向的 `ByteOffset` 为 4B 对齐且落在文件内，且该处 `Magic` 匹配。
- Then：`ReadRecord(ptr)` 成功。

用例 PTR-BAD-001（ByteOffset 越界）
- Given：`ByteOffset >= fileSize`。
- Then：必须报错为“不可解引用”。

用例 PTR-BAD-002（ByteOffset 非 4B 对齐）
- Given：构造一个 ptr，使 `ByteOffset % 4 != 0`。
- Then：必须报错（格式错误）。

---

## 4. varint（canonical）

用例 VARINT-OK-001（canonical 最短编码）
- Given：对一组 `uint64` 值（0、1、127、128、16384、2^63、2^64-1）编码。
- Then：writer 输出 canonical；reader 可解。

用例 VARINT-BAD-001（非 canonical：多余 continuation 0）
- Given：`0` 被编码为 `0x80 0x00`。
- Then：reader 必须拒绝（格式错误）。

用例 VARINT-BAD-002（溢出/过长）
- Given：`uint64` varuint 用 11 bytes。
- Then：reader 必须拒绝。

用例 VARINT-BAD-003（EOF）
- Given：截断在 varint 中间。
- Then：reader 必须拒绝。

---

## 5. DurableDict diff payload（delta-of-prev）

### 5.1 正例（apply 行为）

用例 DICT-OK-001（单条 set）
- Given：`PairCount=1`，`FirstKey=K`，`ValueType=Val_VarInt`，value=42。
- When：apply 到空 dict。
- Then：dict[K]=42。

用例 DICT-OK-002（多条 set；delta-of-prev）
- Given：keys 严格升序：`K0 < K1 < K2`；编码为 `FirstKey=K0`，deltas：`K1-K0`，`K2-K1`。
- Then：apply 后存在 3 个 key。

用例 DICT-OK-003（tombstone 删除）
- Given：dict 初始有 key K；diff 对 K 写 `Val_Tombstone`。
- Then：apply 后 key K 不存在（内存态不允许保留 tombstone 值）。

用例 DICT-OK-004（用户 null 与 tombstone 区分）
- Given：对 key K 写 `Val_Null`。
- Then：apply 后 key K 存在且值为用户可观察的 null。

用例 DICT-OK-005（ObjRef）
- Given：对 key K 写 `Val_ObjRef`，ObjectId=OID。
- Then：apply 后 value 为对象引用（语义上存 ObjectId）。

用例 DICT-OK-006（VersionIndex：Ptr64 值）
- Given：对 key=ObjectId 写 `Val_Ptr64`，value=VersionPtr。
- Then：apply 后映射存在且值等于该 Ptr64。

### 5.2 负例（必须拒绝）

用例 DICT-BAD-001（Overlay diff 中 PairCount=0）
- Given：存在一条 dict diff record，`PrevVersionPtr != 0`（Overlay diff），但 `PairCount=0`。
- Then：reader 必须拒绝（格式错误）。
- Rationale：Overlay diff 若无变更，writer MUST NOT emit；空 Overlay 违反 `[S-PAIRCOUNT-ZERO-LEGALITY]`。

用例 DICT-OK-EMPTY-BASE-001（Base Version 中 PairCount=0 合法）
- Given：存在一条 dict diff record，`PrevVersionPtr == 0`（Base Version），`PairCount=0`。
- Then：reader MUST 接受（表示"空字典的完整 state"）。
- Rationale：Genesis Base 或 Checkpoint Base 可以表示空字典。

用例 DICT-BAD-002（key 未严格升序 / delta 为 0）
- Given：`KeyDeltaFromPrev = 0` 或者还原后 `Key[i] <= Key[i-1]`。
- Then：必须拒绝。

用例 DICT-BAD-003（重复 key）
- Given：同一 diff 内出现相同 key（无论编码层如何表达）。
- Then：必须拒绝。

用例 DICT-BAD-004（key 溢出）
- Given：`Key[i-1] + KeyDeltaFromPrev` 溢出 `ulong`。
- Then：必须拒绝。

用例 DICT-BAD-005（ValueType 高 4 bit 非 0）
- Given：`KeyValuePairType` 高 4 bit != 0。
- Then：必须拒绝。

用例 DICT-BAD-006（未知 ValueType）
- Given：ValueType 不在 `{Null,Tombstone,ObjRef,VarInt,Ptr64}`。
- Then：必须拒绝。

---

## 6. DurableDict snapshot/base（链长封顶）

用例 SNAP-OK-001（链长触发 snapshot）
- Given：`DictSnapshotEveryNVersions = N`。
- When：同一 dict 连续写入 N+1 次版本（每次都有变更）。
- Then：第 N+1 次写入必须为 snapshot：`PrevVersionPtr = 0` 且 payload 为全量表。

用例 SNAP-OK-002（深度缓存不引入额外随机读）
- Given：dict materialize/replay 过程中计数得到 `VersionChainDepth` 并缓存。
- When：commit 写入新版本。
- Then：snapshot 判断只使用缓存深度（不得为了计算链长额外沿链扫描）。

用例 SNAP-OK-003（snapshot 后深度归零）
- When：写入 snapshot。
- Then：该 dict 的 `VersionChainDepth = 0`；下一次普通 overlay 写入后 `depth = 1`。

---

## 7. 推荐的“黄金文件”组织方式（可选）

- `test-data/format/elog/`：手工构造的 data/meta 二进制片段（包含 OK 与 BAD）。
- `test-data/format/dictdiff/`：若干 dict diff 的二进制 payload，配套 `expected.json` 描述 apply 后的 state。
- `test-data/format/varint/`：canonical 与非 canonical 的字节序列。

每个黄金文件建议配一个小的 `README.md`，写明：
- 编码输入（keys/values）
- 期望输出（state）
- 期望错误类型（FormatError/EOF/Overflow 等）
