# StateJournal MVP 测试向量（Layer 1）

> **版本**：v4（2025-12-24 更新，FrameTag 位段编码）
> **配套文档**：
> - `mvp-design-v2.md` — 设计规范
> - [rbf-test-vectors.md](rbf-test-vectors.md) — Layer 0（RBF）测试向量
>
> 本文档遵循 [Atelia 规范约定](../spec-conventions.md)。

## 覆盖范围

- **FrameTag 位段编码**（2025-12-24 新增）
- **`_dirtyKeys` 不变式**（P0-1，监护人建议采纳）
- **首次 Commit 语义**（P0-7）
- **Value 类型边界**（P0-4）
- **CommitAll API 语义**（P0-6）
- DurableDict diff（delta-of-prev key；tombstone）
- DurableDict snapshot/base（链长封顶）

---

## 条款编号映射

> 本节映射测试场景到 `mvp-design-v2.md` 中的规范条款（稳定语义锚点格式）。
> Layer 0 条款映射见 [rbf-test-vectors.md](rbf-test-vectors.md)。

| 条款 ID | 规范条款 | 对应测试用例 |
|---------|----------|--------------|
| `[F-FRAMETAG-STATEJOURNAL-BITLAYOUT]` | FrameTag 16/16 位段布局（RecordType/SubType） | FRAMETAG-OK-001/002/003 |
| `[F-FRAMETAG-SUBTYPE-ZERO-WHEN-NOT-OBJVER]` | 非 ObjectVersion 时 SubType MUST 为 0 | FRAMETAG-BAD-001 |
| `[F-OBJVER-OBJECTKIND-FROM-TAG]` | ObjectKind 从 FrameTag 高 16 位获取 | FRAMETAG-OK-001 |
| `[F-OBJVER-PAYLOAD-MINLEN]` | ObjectVersionRecord payload 至少 8 字节 | OBJVER-BAD-001 |
| `[F-UNKNOWN-FRAMETAG-REJECT]` | 未知 RecordType MUST fail-fast | FRAMETAG-BAD-002 |
| `[F-UNKNOWN-OBJECTKIND-REJECT]` | 未知 ObjectKind MUST fail-fast | FRAMETAG-BAD-003 |
| `[S-STATEJOURNAL-TOMBSTONE-SKIP]` | 先检查 FrameStatus，跳过 Tombstone 帧 | TOMBSTONE-SKIP-001 |
| `[F-KVPAIR-HIGHBITS-RESERVED]` | ValueType 高 4 bit 必须写 0；reader 见到非 0 视为格式错误 | DICT-BAD-005 |
| `[F-UNKNOWN-VALUETYPE-REJECT]` | 未知 ValueType MUST 视为格式错误 | DICT-BAD-006 |
| `[S-DIRTYSET-OBJECT-PINNING]` | Dirty Set MUST 持有对象强引用 | — |
| `[S-IDENTITY-MAP-KEY-COHERENCE]` | Identity Map 与 Dirty Set 的 key 必须等于 ObjectId | — |
| `[S-DIRTY-OBJECT-GC-PROHIBIT]` | Dirty 对象不得被 GC 回收 | — |
| `[S-NEW-OBJECT-AUTO-DIRTY]` | 新建对象 MUST 立即加入 Dirty Set | FIRST-COMMIT-002/003 |
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

---

## 0. FrameTag 位段编码测试（2025-12-24 新增）

> 测试 FrameTag 16/16 位段编码的正确解析。

### 0.1 正例

用例 FRAMETAG-OK-001（ObjectVersionRecord with Dict）
- Given：FrameTag bytes = `01 00 01 00`（u32 LE = `0x00010001`）
- When：解析 FrameTag
- Then：
  - `RecordType = 0x0001`（ObjectVersionRecord）
  - `SubType = 0x0001`（Dict）
  - Payload 不含 ObjectKind 字节

用例 FRAMETAG-OK-002（MetaCommitRecord）
- Given：FrameTag bytes = `02 00 00 00`（u32 LE = `0x00000002`）
- When：解析 FrameTag
- Then：
  - `RecordType = 0x0002`（MetaCommitRecord）
  - `SubType = 0x0000`

用例 FRAMETAG-OK-003（ObjectVersionRecord with future Array）
- Given：FrameTag bytes = `01 00 02 00`（u32 LE = `0x00020001`）
- When：解析 FrameTag
- Then：
  - `RecordType = 0x0001`（ObjectVersionRecord）
  - `SubType = 0x0002`（Array，若已实现；否则应 reject）

用例 TOMBSTONE-SKIP-001（先检查 FrameStatus）
- Given：Frame 的 FrameStatus = Tombstone，FrameTag = `0x00010001`
- When：StateJournal Reader 处理该帧
- Then：MUST 跳过，不解析 Payload

### 0.2 负例

用例 FRAMETAG-BAD-001（MetaCommitRecord 带非零 SubType）
- Given：FrameTag bytes = `02 00 01 00`（u32 LE = `0x00010002`）
- When：解析 FrameTag
- Then：MUST reject（SubType != 0 for MetaCommitRecord）

用例 FRAMETAG-BAD-002（未知 RecordType）
- Given：FrameTag bytes = `FF 00 00 00`（u32 LE = `0x000000FF`）
- When：解析 FrameTag
- Then：MUST reject（unknown RecordType）

用例 FRAMETAG-BAD-003（未知 ObjectKind）
- Given：FrameTag bytes = `01 00 FF 00`（u32 LE = `0x00FF0001`）
- When：解析 FrameTag（ObjectVersionRecord with ObjectKind=0x00FF）
- Then：MUST reject（unknown ObjectKind）

用例 OBJVER-BAD-001（Payload 不足 8 字节）
- Given：FrameTag = `0x00010001`（ObjectVersionRecord），Payload 长度 = 4 字节
- When：解析 ObjectVersionRecord
- Then：MUST reject（Payload MUST 至少 8 字节 for PrevVersionPtr）

---

## 1. `_dirtyKeys` 不变式测试（P0-1）

### 1.1 净零变更（Set 后 Delete 同一个新 key）

**用例 DIRTY-001**
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

## 2. 首次 Commit 语义（P0-7）

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

## 3. Value 类型边界（P0-4）

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

## 4. CommitAll API 语义（P0-6）

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

- `test-data/format/dictdiff/`：若干 dict diff 的二进制 payload，配套 `expected.json` 描述 apply 后的 state

> Layer 0 黄金文件（rbf、varint）见 [rbf-test-vectors.md](rbf-test-vectors.md)。

每个黄金文件建议配一个小的 `README.md`，写明：
- 编码输入（keys/values）
- 期望输出（state）
- 期望错误类型（FormatError/EOF/Overflow 等）

---

## 变更日志

| 日期 | 版本 | 变更 |
|------|------|------|
| 2025-12-24 | v4 | 新增 §0 FrameTag 位段编码测试；新增条款映射 `[F-FRAMETAG-*]`、`[F-OBJVER-*]`、`[S-STATEJOURNAL-TOMBSTONE-SKIP]` |
| 2025-12-22 | v3 | Layer 0 测试向量提取到 rbf-test-vectors.md |
| 2025-12-20 | v2 | 根据畅谈会共识修订 |
