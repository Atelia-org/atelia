# StateJournal — Memory Notebook

> **用途**：供 AI Agent 在新会话中快速重建对 `src/StateJournal` 的整体认知。
> **原则**：只记脉络与决策，不复述代码细节；有疑问时指向源文件路径。
> **最后更新**：2026-03-25

---

## 一句话定位

StateJournal 是一个**可持久化增量序列化对象图引擎**——在内存中维护类型化/异构容器（Dict/Deque），通过 delta/rebase 版本链将变更持久化到 RBF 文件，支持按版本回放重建任意时刻的对象状态。

---

## 依赖关系

```
Atelia.Primitives   ← AteliaResult<T>, AteliaError
Atelia.Data         ← SizedPtr (38:26 bit 紧凑指针，用作 RBF 帧 ticket)
Atelia.Rbf          ← IRbfFile, RbfFrameBuilder, RbfPooledFrame (追加式二进制分帧文件)
```

StateJournal 的 **Revision / DurableObject / VersionChain 核心层** 不依赖网络或第三方库，文件 I/O 主要经由 RBF 抽象完成。
例外是 `Repository` 子层：它负责 repo 目录、branch JSON、segment 布局与锁文件，因此会直接使用文件系统 API。

---

## 对象模型层（Public API）

### 继承体系

```
DurableObject                           // 抽象基类：LocalId, DurableState, 生命周期钩子
  ├─ DurableDictBase<TKey>              // 抽象：版本链读写逻辑(WritePendingDiff/ApplyDelta/OnCommitSucceeded)
  │    ├─ DurableDict<TKey, TValue>     // TypedDict 外观（值类型同构）
  │    │    ├─ TypedDictImpl<…>         // Internal：自持 DictChangeTracker<TKey, TValue>
  │    │    └─ DurObjDictImpl<…>        // Internal：值为 DurableObject 子类，内部存 LocalId
  │    └─ DurableDict<TKey>             // MixedDict 外观（值异构，内部 ValueBox）
  │         └─ MixedDictImpl<…>         // Internal：自持 DictChangeTracker<TKey, ValueBox>
  ├─ DurableDeque<T>                     // TypedDeque 外观 ← ⚠️ 占位，尚未实现
  │    ├─ TypedDequeImpl<…>              // Internal 占位。基元类型同构值。
  │    └─ DurObjDequeImpl<…>             // Internal 占位。值为 DurableObject 子类，内部存 LocalId
  └─ DurableDeque                        // MixedDeque 外观 ← ⚠️ 占位，尚未实现
       └─ MixedDequeImpl                 // Internal 占位。值异构，内部 ValueBox
```

### DurableState 生命周期

```
CreateObject() → TransientDirty
LoadObject()   → Clean
Modify(Clean)  → PersistentDirty
Commit(Dirty)  → Clean
Discard(PersistentDirty) → Clean（回退到 committed 快照）
Discard(TransientDirty)  → Detached（终态，不可恢复）
GC Sweep(不可达) → Detached（由 DetachByGc() 设置，终态）
```

关键文件：`DurableState.cs`, `DurableObject.cs`

### 工厂入口

公开创建入口为 `Revision` 实例方法（`Durable` 静态类已降为 `internal`）：

```csharp
var rev = new Revision(boundSegmentNumber: 1);
rev.CreateDict<string, int>()   // TypedDict
rev.CreateDict<string>()         // MixedDict（异构值）
rev.CreateDeque<int>()            // TypedDeque（占位）
rev.CreateDeque()                 // MixedDeque（占位）
```

创建时自动分配 `LocalId`、绑定到 `Revision`、标记 `TransientDirty`。
`Revision` 现在只记录自己当前绑定的 `segmentNumber`，不再长期持有 `IRbfFile`。
底层仍通过 Static Generic Class Cache 编译工厂委托，首次访问后接近零开销。
详见 `Internal/DurableFactory.cs`, `Revision.cs`。

### 接口与访问模式

- `IDict<TKey>`: ContainsKey / Count / Remove / Keys
- `IDict<TKey, TValue>`: Upsert / Get（返回 `GetIssue` 枚举）/ 索引器
- MixedDict 额外提供：`Get<T>()`, `TryGet<T>()`, `Upsert<T>()`, `Of<T>()` 泛型访问
- `GetIssue` 枚举：None / PrecisionLost / Saturated / OverflowedToInfinity / TypeMismatch / NotFound / ...

关键文件：`IDict.cs`, `GetIssue.cs`, `DurableDict.Mixed.cs`

---

## 值存储层（ValueBox & Pools）

### ValueBox — Tagged-Pointer

`ValueBox` 是一个 `readonly struct`（8 字节 `ulong`），用 **LeadingZeroCount (LZC)** 作为类型标签：

| LZC | Payload bits | 用途 | 解码 |
|----:|:-----|:-----|:-----|
| 0 | 63 | Inline Double（牺牲 1bit 尾数） | `bits << 1` |
| 1 | 62 | Inline 非负整数 [0, 2^62) | `bits & mask` |
| 2 | 61 | Inline 负整数 [-2^61, -1]（补码自带 tag） | `bits \| signRestore` |
| 24 | 39 | Heap Slot（6bit HeapValueKind + 1bit Exclusive + 32bit SlotHandle）| 见 HeapSlot 解码 |
| 27 | 36 | DurableRef（4bit DurableObjectKind + 32bit LocalId）| — |
| 62 | 1 | Boolean | `bits & 1` |
| 63 | 0 | Null | — |
| 64 | 0 | Uninitialized（`default`） | — |

关键文件：`Internal/ValueBox.cs`, `Internal/BoxLzc.cs`, `Internal/LzcConstants` （在 BoxLzc.cs 中）

### 关键设计决策（值存储）

- **整数与浮点不互等**：`ValueBox(int 42) ≠ ValueBox(double 42.0)`
- **同类型同值同码**：`ValueBox(byte 42) == ValueBox(int 42)`，`ValueBox(float 0.5) == ValueBox(double 0.5)`
- **不归一化 -0.0 和 NaN**：为的是保护用户代码计算时产生 inf 时符号正确，以及不阻断用户将浮点数用于非数值计算场景（NaN-Boxing、哈希）。
- **浮点不追求最短内联编码**：ValueBox 建模运行时内存状态，不是序列化格式
- **数值 Slot 独占可变**：GcPool，可 inplace 改值、可立即释放

详见 `docs/StateJournal/v4/异构存储设计决策.md`

### Pools 体系

```
ValuePools（静态单例）
  ├─ OfBits64: GcPool<ulong>        // double bits / ulong / long 的堆存储
  └─ OfString: StringPool           // 字符串去重池（包装 InternPool + identity cache）
```

- **SlotPool\<T\>**（`Pools/SlotPool.cs`）：Slab + Bitmap 分配器，O(1) alloc/free/index，尾部空页自动回收，8-bit generation 防 ABA
- **GcPool\<T\>**（`Pools/GcPool.cs`）：包装 SlotPool + Mark-Sweep GC Bitmap
- **InternPool\<T\>**（`Pools/InternPool.cs`）：去重 + Mark-Sweep GC，哈希桶链 + SlotPool；若新分配的 handle 恰好为 packed=0，会额外推进一次 generation，保证不会向外暴露全 0 handle
- **SlabBitmap**（`Pools/SlabBitmap.cs`）：GcPool/InternPool 共用的 bitmap 基础设施

### Symbol / String 约定

- `Revision` 维护一份持久化 `SymbolTable`（`DurableDict<uint, InlineString>`）和一份运行时 `StringPool`
- `SymbolId.Null = 0`
- 非空 `SymbolId` 直接使用底层 `StringPool` 返回的 `SlotHandle.Packed`
- 因为 `SlotPool`和`InternPool` 已保证不会向外分配 packed=0，所以 `0` 可以稳定保留给 null 语义，不需要 `+1` 偏移编码
- `Revision.InternSymbol(string? value)`：`null -> SymbolId.Null`；非空字符串 intern 后同步写入 `_symbolTable`
- `Revision.GetSymbol(SymbolId id)`：`SymbolId.Null -> null`

### Exclusive / Frozen 所有权

对 Heap Slot（HeapSlot 编码的 ValueBox）：
- **Exclusive（bit32=1）**：独占可变，可 inplace 写、可立即 Free
- **Frozen（bit32=0）**：共享不可变，Commit 时 `_committed` 和 `_current` 共享同一 handle

`DictChangeTracker.Commit` 冻结 current 值再共享给 committed；后续修改触发 Copy-on-Write（`StoreOrReuseBits64`）。

关键文件：`Internal/ValueBox.cs`（ExclusiveBit, AsFrozen, Freeze, StoreOrReuseBits64）

---

## Change Tracking 层

### DictChangeTracker\<TKey, TValue\>

核心策略：**双字典 + BitDivision 脏标记**

```
_committed: Dictionary<TKey, TValue?> — 上次 commit 的快照
_current:   Dictionary<TKey, TValue?> — 当前工作状态
_dirtyKeys: BitDivision<TKey>         — false 子集 = 被删除的 key，true 子集 = 被 upsert 的 key
```

- **Upsert 后**：若新值与 committed 语义相等 → 释放新值的独占 slot，恢复 committed 冻结副本（"写回归零"优化）
- **Remove 后**：若 key 在 committed 中存在 → 标记为 remove (之前有，现在无)；不存在 → 标记为 same（新增后又删除 = 无变化）
- **Commit**：遍历 dirtyKeys，将变动增量应用到 `_committed`，冻结共享值
- **Revert**：遍历 dirtyKeys 反向恢复 `_current`，释放独占 slot

关键文件：`Internal/DictChangeTracker.cs`

### BoolDivision\<TKey\> & BitDivision\<TKey\>

两种"key → bool"哈希表实现，用于追踪脏 key 的子集归属：
- `BoolDivision`：基于双向链表的子集枚举
- `BitDivision`：基于 bitmap 的子集枚举（更 cache 友好）

DictChangeTracker 当前使用 `BitDivision`。

关键文件：`Internal/BoolDivision.cs`, `Internal/BitDivision.cs`

---

## 序列化层

### 二进制 Diff 读写

- `BinaryDiffWriter`（`Serialization/BinaryDiffWriter.cs`）：`ref struct`，包装 `IBufferWriter<byte>`，提供 Tagged（自描述）和 Bare（无类型头）两类写入方法
- `BinaryDiffReader`（`Serialization/BinaryDiffReader.cs`）：`ref struct`，基于 `ReadOnlySpan<byte>` 零分配读取

### Tagged 编码（CBOR-inspired）

借用 CBOR major type 0/1/7 的头字节空间，但 payload 采用 **little-endian**。
**这不是标准 CBOR，不追求兼容性。**

- 非负整数：0..23 内联，24+ 用 1/2/4/8 字节扩展
- 负整数：映射 `payload = -1 - value`，同样 0..23 内联
- 浮点：half(0xF9) / single(0xFA) / double(0xFB)，序列化时尽量选最短表示
- DurableRef：重写 CBOR major type 5 的头字节空间

关键文件：`Serialization/ScalarRules.cs`, `Serialization/TaggedFloat.cs`, `Serialization/TaggedInt.cs`

### TaggedValueDispatcher

MixedDict 反序列化的入口。读取 tag byte → 按 CBOR-inspired 规则 dispatch 到对应的 `ValueBox.*Face.UpdateOrInit`。

关键文件：`Serialization/TaggedValueDispatcher.cs`

### VarInt

整数的 Bare 编码采用变长编码（类似 LEB128）。

关键文件：`Serialization/VarInt.cs`

---

## 版本链层

### 核心流程

**Write**（`VersionChain.Write`）—— 二阶段写入，返回 `PendingSave`：

```
对象有变更？
  → BeginAppend() 获取 RbfFrameBuilder
  → WritePendingDiff() 序列化 diff（由 DurableDictBase 实现）
    → 内部决策 rebase vs deltify（VersionChainStatus.ShouldRebase）
    → rebase: 写入 TypeCode + 全量数据
    → deltify: 写入空 TypeCode + 增量数据
  → EndAppend(tag) 得到 SizedPtr
  → 返回 PendingSave（持有 obj + ticket + context，尚未更新内存状态）
```

**PendingSave.Complete()**：调用 `OnCommitSucceeded()` 将写盘结果应用到对象内存状态。

**Save**（`VersionChain.Save`）：一步到位的便捷方法，等价于 `Write()` + `Complete()`。

**Load**（`VersionChain.Load`）：

```
从 versionTicket 开始向前回溯：
  → ReadPooledFrame 读帧
  → 检测 TypeCode 是否为空
    → 空 = delta 帧，继续回溯（压入栈）
    → 非空 = rebase 帧（链终点）
  → TypeCodec.TryDecode 解码类型 → DurableFactory.TryCreate 创建空对象
  → 从栈顶（rebase）开始逐帧 ApplyDelta，重建状态
  → OnLoadCompleted()
```

关键文件：`Internal/VersionChain.cs`

### Rebase vs Deltify 决策

`VersionChainStatus.ShouldRebase` 基于成本模型：
- 首次保存必须 rebase（否则无 TypeCode，Load 无法终止回溯）
- 后续：若 `(rebaseSize - deltifySize) × MaxReadAmplificationRatio ≤ cumulativeCost`，则值得 deltify
- `MaxReadAmplificationRatio=3`（来自 Write:Read:Storage = 1:1:1 权重）
- `PerFrameOverhead=6`（约 34B 的固定开销折算为变更条数）

关键文件：`Internal/VersionChainStatus.cs`

### FrameTag

RBF 帧的 `uint tag` 字段被 StateJournal 结构化为四部分：
- VersionKind（4bit）：Rebase / Delta
- ObjectKind（4bit）：MixedDict / TypedDict / MixedDeque / TypedDeque
- UsageKind（4bit）：Blank / UserPayload / ObjectMap
- FrameSource（4bit）：PrimaryCommit / Compaction / CrossFileSnapshot

关键文件：`Internal/FrameTag.cs`

---

## 类型系统层

### TypeCodec — 栈式操作码

将泛型类型编码为 `byte[]`，用于 rebase 帧的类型标识。操作码基于栈：

```
PushInt32, PushString → MakeTypedDict  //= DurableDict<string, int>
PushString → MakeMixedDict              //= DurableDict<string>
PushInt32 → MakeTypedDeque               //= DurableDeque<int>
```

一经持久化不可变更。

关键文件：`Internal/TypeCodec.cs`

### HelperRegistry & ITypeHelper\<T\>

`HelperRegistry` 做两件事合一：验证泛型参数是否受支持 + 返回对应的 `ITypeHelper<T>` 实现类型。

`ITypeHelper<T>` 提供静态抽象方法：Equals / Write / Read / UpdateOrInit / Freeze / ReleaseSlot。
通过泛型约束 `where VHelper : unmanaged, ITypeHelper<T>` 实现编译期静态绑定，避免虚调用。

当前支持的基元类型：bool, string, double, float, Half, ulong, uint, ushort, byte, long, int, short, sbyte。

关键文件：`Internal/ITypeHelper.cs`, `Internal/HelperRegistry.cs`

---

## 与 RBF 的交互

StateJournal 将 RBF 视为一个 **append-only 分帧二进制文件**。交互模式：

| 操作 | RBF API | SJ 使用处 |
|:-----|:--------|:---------|
| 写帧 | `IRbfFile.BeginAppend()` → `RbfFrameBuilder.PayloadAndMeta` → `EndAppend(tag)` | `VersionChain.Save` |
| 读帧 | `IRbfFile.ReadPooledFrame(SizedPtr)` → `RbfPooledFrame.PayloadAndMeta` | `VersionChain.Load` |
| 帧地址 | `SizedPtr`（`Atelia.Data`，38:26 bit 紧凑指针）| 作为版本链条目间的链接 |

`RbfPayloadWriter` 实现 `IBufferWriter<byte>`，`BinaryDiffWriter` 直接包装它。
`RbfPooledFrame.PayloadAndMeta` 返回 `ReadOnlySpan<byte>`，`BinaryDiffReader` 直接消费。

关键文件（RBF 侧供参考）：
`src/Rbf/IRbfFile.cs`, `src/Rbf/RbfFrameBuilder.cs`, `src/Rbf/RbfPooledFrame.cs`,
`src/Data/SizedPtr.cs`

---

## 进度地图

| 模块 | 状态 | 备注 |
|:-----|:-----|:-----|
| ValueBox（Tagged-Pointer + 所有 Face） | ✅ 已完成 | 含 Inline/Heap 编解码、Equality、Write |
| Pools（SlotPool / GcPool / InternPool / SlabBitmap） | ✅ 已完成 | 含 Mark-Sweep GC、Compaction、`MoveSlotRecorded`/`UndoMoveSlot` 精确回滚 |
| DictChangeTracker + BitDivision | ✅ 已完成 | 双字典 + 脏标记 |
| DurableDict（Typed / Mixed / DurObj） | ✅ 已完成 | 含 Rebase/Deltify |
| BinaryDiffWriter / Reader | ✅ 已完成 | 含 Tagged + Bare 编码 |
| TypeCodec / HelperRegistry / DurableFactory | ✅ 已完成 | 泛型类型 → 工厂 |
| VersionChain（Write/Save/Load） | ✅ 已完成 | 含二阶段 Write + PendingSave |
| DurableDeque（Typed/Mixed） | ⬜ 占位 | 所有方法 throw；Diff 方案待定 |
| Revision | ✅ 已完成 | Primary Commit（三阶段）+ Compaction Apply/Rollback + Follow-up Persist + CrossFileSnapshot(`ExportTo`/`SaveAs`) |
| Repository | ✅ 已完成首版 | 单写者 repo + `refs/branches` + `CreateBranch` / `CheckoutBranch` + segment 轮换 + O(1) `CommitAddress(uint, CommitTicket)` 直接索引 |

---

## 未来方向思考笔记（2026-03-18 会话）

> 以下由 Claude Opus 4.6 会话留给后续 Agent 的思考笔记。背景：本会话完成了 ExportTo/SaveAs 的代码审阅和 commit 层的重构，在 Revision.Commit.cs 消除了 TryPersistSnapshot（10 参数→删除）、PrepareLiveObjects（5 参数→1 参数）、BuildStateError（4 参数→2 参数）。

### 方向 1：Repository — 从单文件到文件集

**现状更新（2026-03-20）**：`Repository.cs` 已从空壳演进为完整实现：管理 `refs/branches/*.json`、`recent/*.sj.rbf`、repo 锁、按需 `CheckoutBranch()`、以及 branch 级 commit 推进。`CommitSequence` 已彻底消除——每个 commit 由 `{SegmentNumber, CommitTicket}` 天然寻址，segment 路由 O(1)，`sequence.json` 不再存在，恢复流程大幅简化。

**核心命题**：Repository 管理一组 RBF 文件，构成逻辑上的"仓库"。

#### 我认为 Repository 需要解决的问题

1. **文件发现与目录结构**：启动时扫描目录，识别哪些 `.rbf` 文件属于此仓库、哪些 branch 当前可写。当前方案是 `refs/branches/*.json` + `recent/*.sj.rbf`；当前 `Open()` 已会从 segments 恢复 durable sequence 下界，而不只看 branch。
2. **文件生命周期**：何时创建新文件？可能的触发条件：
   - 文件尺寸超阈值（类似 git packfile 的理念）
   - 显式 SaveAs
   - compaction 产生新世代
3. **文件级 GC**：当某个 RBF 文件中所有帧都不再被任何可达 commit 引用时，可安全删除。需要某种引用计数或扫描机制。
3. **单写者锁**：Repository 描述自己是"进程独占"的。文件锁（`state-journal.lock`）。
4. **Open 时不需要 recovery**：当前 `Open()` 只做 `扫描 segments + 加载 branches`。segment 文件和 branch 文件就是全部真相，不需要 repair 逻辑。

#### 设计建议

- **先把 branch 语义站稳**：公开 API 统一用 `CreateBranch` / `CheckoutBranch`，磁盘目录统一用 `refs/branches`，把 `HEAD` 留给 `Revision` 工作会话语义。
- **manifest 继续推迟**：当前 `branches + sequence + recent segments` 已足够支撑单写 repo；等真的需要跨层索引时再引入 manifest。
- **`ExportTo` 是 Repository 的天然子操作**：未来可以是 `repo.CreateSnapshot()` 或类似 API，内部调 `Revision.ExportTo` 到仓库管理的新文件。

**当前记忆点（供后续 Agent 快速接手）**：

- `CommitSequence` 已彻底消除。commit 地址 = `CommitAddress(uint SegmentNumber, CommitTicket)`。
- `BranchHead` 类型已重命名为 `CommitAddress`，`Unborn` 语义移至调用侧（`CommitAddress?` = null）。
- `sequence.json` 不再存在。`Open()` 只做 `扫描 segments + 加载 branches`，没有 recovery/repair 逻辑。
- Segment 路由 O(1) 直接索引。文件名 `{segmentNumber:X8}.sj.rbf`（uint hex8）。
- TailMeta 已从 12 字节缩减为 4 字节（仅 GraphRoot LocalId）。
- Branch JSON 字段由 `commitSequence` → `segmentNumber`（`uint`）更名。
- poisoned commit 后 reopen 的 correctness 无需特殊 recovery——branch 和 segment 文件本身就是全部真相。

### 方向 2：新的 DurableObject 类型

#### DurableList — Diff vs Op-Record

**Diff 路线**（类似 Dict 的做法）：
- 优点：与 Dict 复用大量基础设施（DictChangeTracker 的双快照 + 脏标记模式）
- 缺点：List 的 insert/remove 会导致后续元素全部"脏"（index shift），diff 退化为全量重写
- 适用场景：短列表、主要做 append/replace、很少做中间插入

**Op-Record 路线**：
- 记录操作序列：`Insert(index, value)`, `Remove(index)`, `Append(value)`, `Clear()`
- rebase 帧写全量，delta 帧写操作日志
- 优点：精确描述变更意图，对 insert-heavy 场景友好
- 缺点：回放开销随 delta 链长度线性增长；合并/冲突解决复杂
- 适用场景：消息历史、事件日志等 append-mostly 序列

**我的倾向**：先走 Diff 路线做一个能用的版本。理由：
1. Agent 的 List 使用模式大概率是 append-mostly（对话历史、步骤记录），中间插入少
2. Diff 路线能直接复用 VersionChainStatus 的 rebase/deltify 成本模型
3. Op-Record 的"操作合并"问题（连续 Insert 后 Remove 的归约）是一个独立的设计黑洞，不值得现在踩
4. 如果未来 profiling 发现 List diff 效率有问题，可以后续引入 op-record 作为优化——反正 rebase 帧格式不变，只影响 delta 帧

#### DurableText — Rope vs PieceTable

**Rope**：
- 经典的平衡二叉绳结构，O(log n) insert/delete
- delta 表达：记录 splice 操作 `(offset, deleteCount, insertText)`
- 优点：随机位置编辑高效；天然支持大文本
- 缺点：实现复杂；小文本（<4KB）不如 flat string

**PieceTable**：
- 两个缓冲区（original + additions）+ 一组 pieces 描述当前文本的拼接
- delta 表达：新的 piece 就是 delta
- 优点：实现相对简单；append-only additions 缓冲区天然适配 RBF 的追加模型
- 缺点：读取需要跳跃多个 piece；频繁编辑后 pieces 会碎片化
- 缺点：序列化兼容未来的局部加载特性

**我的倾向**：如果文本主要服务于 Agent 的笔记/记忆场景，大多数编辑是 append 或 replace 整段。PieceTable 的简单性更有吸引力。但如果要支持精细的协作编辑（Agent 和用户共同编辑同一文档），Rope 更合适。

**务实建议**：先不做。Agent 的文本需求短期内可以用 `DurableDict<string, string>` 模拟（key=段落ID，value=文本内容）。等 Agent 真的跑起来并且产生了"我需要高效编辑一段长文本"的需求时再做。

#### NodeSet — 共享结构聚合帧

这个思路很有意思。如果我理解正确：

- 一个 NodeSet 是一组轻量 Node（每个 Node 本质是 K-V 映射）
- 整组 Node 打包在一帧里（而不是每个 Node 一帧）
- 读取时合并近期到 rebase 的若干帧，重建 BTree 等共享结构

**关键洞察**：当前每个 DurableObject 独立占一帧，帧间通过 LocalId 互引。这对"一棵 BTree 有 1000 个节点"的场景会产生 1000 个帧和 1000 条 ObjectMap 条目——开销过大。NodeSet 把粒度从"每节点一帧"提升到"每结构一帧"。

**我关心的设计问题**：
1. **Node 的 identity**：如果 Node 不是 DurableObject（没有 LocalId），怎么在 delta 帧里引用"哪个 Node 被修改了"？需要某种帧内 ID（可能就是 BTree 节点地址 / 页号）。
2. **部分重建**：如果 NodeSet 帧很大（一棵 BTree 的全部节点），delta 合并的成本可能高。需要某种分段/分页机制？
3. **GC**：NodeSet 内的 Node 引用外部 DurableObject 时（比如 BTree 叶子节点存 LocalId），GC walk 需要穿透 NodeSet 边界。`AcceptChildRefVisitor` 需要能遍历 NodeSet 内所有外部引用。

**总结**：NodeSet 是往"容器即模型"方向迈的关键一步——Dict 是 flat K-V，NodeSet + BTree 是 ordered K-V with range query。Dict 能满足 80% 场景，BTree 处理剩下的 20%（排序、范围查询、前缀匹配）。如果思路清晰就先做这个也合理。

### 方向 3：时空回溯 — CommitTicket 信标

**场景还原**：Agent 在 step 5 发现走错了，想回到 step 2 的状态，但带上"这条路不行"的结论。

**当前能力**：`Revision.Open(commitTicket, file, segmentNumber)` 可以回到任意已 commit 的快照。但这是"开一个新的 Revision 实例"，不是在当前 Revision 上回退。

**需要的新能力**：

1. **Revert to commit**：将当前 Revision 的内存对象图回退到指定 CommitTicket 的状态。类似 `git reset --hard <commit>`。
   - 实现思路：由调用方先解析目标 commit 所在的 `segmentNumber` 并打开对应 file，再用 `Open(targetCommitTicket, file, segmentNumber)` 加载目标快照到新 pool，然后替换当前 Revision 的 `_pool / _objectMap / _head / _boundSegmentNumber`。
   - 难点：当前 Revision 的对象引用（用户持有的 `DurableObject` 变量）全部失效。需要某种"引用重绑定"机制，或者接受"revert 后旧引用全部 Detached"。

2. **携带非持久信息穿越**：用户想带着 "这条路不行" 的结论回去。这不是 StateJournal 的持久状态，而是某种 **annotations / side-channel**。
   - 方案 A：在 commit 之前把结论写进持久状态（比如一个 `DurableDict<string, string>("_agent_notes")`），然后 revert 时指定"保留某些 key 的当前值"——这就变成了 selective revert / cherry-pick。
   - 方案 B：annotations 完全不走 StateJournal。回退后用户自己把结论塞进新状态。这更简单，但需要应用层自己管理 side-channel。
   - 方案 C：提供一种"穿越信封" API —— `revert(targetCommitTicket, envelope: object)` 返回时同时返回信封内容。信封不落盘、不参与 diff，纯内存传递。

3. **分支/Fork**：从某个历史 CommitTicket 创建一个新的 Revision 分支，两个分支独立演化。
   - 这比 revert 简单——就是 `var branch = Revision.Open(someOldCommitTicket, file, segmentNumber)` + 让它写到不同的 RBF 文件（或同一 repo 的 active segment）。
   - `ExportTo` 已经做了大部分工作：从当前状态完整快照到新文件。Fork = Open(旧 commit) + ExportTo(新文件)。

4. **轨迹本身作为可分析对象（"反射 API"）**：
   - 最简形式：`CommitTicket[] ListCommits()` + `CommitSnapshot Inspect(CommitTicket)` —— 列出所有 commit，读取任意一个的 ObjectMap 和 GraphRoot。
   - 进阶：diff 两个 commit 的状态（哪些 key 变了、哪些对象新增/删除）。这需要两次 Open + 比较 ObjectMap 的 key-set 差异，不需要新基础设施。
   - 更进阶：commit graph 持久化（当前 `CommitSnapshot.ParentId` 只记录了线性 parent，fork 后需要 DAG）。

**务实优先级建议**：
1. 先做 `Revision.ListCommits(file)` —— 从最新 commit 沿 parent 链回溯，收集所有 CommitTicket。这是其他所有功能的基础。
2. 再做 Fork（`Open` 旧 commit + `ExportTo` 新文件），利用已有能力组合。
3. Revert 和 selective-carry-forward 最复杂，放最后。

### 给后续 Agent 的优先级建议

如果要排一个"接下来做什么"的序，我会建议：

1. **Repository MVP**（最小骨架：single-file + lock + Open/Create）—— 打通完整链路
2. **让一个真实 Agent 原型跑在 StateJournal 上** —— 验证 API 人体工学
3. **NodeSet**（思路清晰，组合价值高）
4. **DurableList**（Diff 路线，快速出活）
5. **时空回溯基础设施**（ListCommits + Fork）
6. **DurableText**（需求不紧急，先用 Dict 模拟）

理由：2 的优先级应该非常高。在没有真实 Agent 使用反馈之前，所有基础设施设计都只是猜测。一旦有了真实使用者，才能知道 Dict/List/Text/NodeSet 的优先级到底是什么、API 哪里别扭、性能瓶颈在哪。

### 补充：DurableDeque 灵感（来自 Agent.Core/History 分析）

在分析了 `prototypes/Agent.Core/History/AgentState.cs` 的实际数据模式后，发现 Agent History 的操作模式是纯 Deque：后端 push + 前端 trim，无中间 insert/remove。这意味着在做通用 DurableList 之前，应该先做一个 **DurableDeque**：

**操作集**：`PushBack` / `PopFront` / `PushFront`（可选） / `PopBack`（可选） / 索引读取 / 枚举

**Delta 编码**（极其简单）：
```
[VarInt: trimFrontCount] [VarInt: trimBackCount]
[VarInt: pushFrontCount] [pushFrontCount 个元素...]
[VarInt: pushBackCount]  [pushBackCount 个元素...]
```

**为什么优先于 DurableList**：
- 不需要处理 index shift（insert/remove 导致后续元素全部脏）
- delta 帧天然紧凑：append-mostly 场景下只写新元素
- 完全复用 VersionChainStatus 的 rebase/deltify 成本模型
- 覆盖 Agent 最核心的数据结构：对话历史、步骤记录、事件日志

**值类型策略**：先做基元版本（`DurableDeque<T>` where T 是 int/string/double 等已支持类型），满足"存一列 string"的场景。异构多态元素（如 HistoryEntry 体系）等后续 NodeSet 或自定义序列化支持后再做。

**优先级修订**：
1. Repository MVP
2. **DurableDeque**（新增，插入到 NodeSet 之前）
3. Agent prototype 跑在 StateJournal 上（用 DurableDict + DurableDeque 建模 AgentState）
4. NodeSet
5. DurableList（通用版，含 insert/remove）
6. 时空回溯

---

## Revision 层

Revision 是**已打开的对象图工作会话**：它持有一个可编辑的内存对象图、最近一次已提交快照的视角，以及把当前工作态 durable 化为新快照的能力。
每次 `Commit(graphRoot)` 都会产生一个新的落盘快照；但 `Revision` 本身不是那个快照对象，更接近 checkout / workspace / session，而不是 git commit。

> **现状更新（2026-03-20）**：`Revision` 已不再长期持有 `_file`。
> 它只记录 `_boundSegmentNumber`，真正的 `IRbfFile` 由调用方在 `Open(...)` / `Commit(...)` / `SaveAs(...)` 时显式传入。
> 这样 `Revision` 的职责聚焦于"对象图工作态 + 持久化协议"，而文件句柄生命周期回收到 `Repository`。

### 对象管理：GcPool\<DurableObject\>

`GcPool<DurableObject>` 同时承担三个职责：

| 职责 | 机制 |
|:-----|:-----|
| **分配 LocalId** | `_pool.Store(obj)` 返回 `SlotHandle`，`LocalId.FromSlotHandle` 转换 |
| **Identity Map** | `_pool[handle]` / `_pool.TryGetValue` O(1) 数组索引查找 |
| **GC 回收** | BeginMark → MarkReachable → Sweep 三步 Mark-Sweep |

- **slot 0** 始终由 ObjectMap 占据（对应 `LocalId.Null`），不分配给用户对象。
- **slot 1** 始终由 SymbolTable 占据，不分配给用户对象。
- `LocalId.Value == SlotHandle.Packed`：二者一一对应，通过 `FromSlotHandle` / `ToSlotHandle` 互转。
- 对象一经 `Bind(revision, localId)` 终生属于该 Revision，不可改绑。

### 引用安全

`Revision.EnsureCanReference(obj)` 统一校验：
- 对象已绑定到当前 Revision（`IsBoundTo`）
- 对象未被 Detach（`!IsDetached`）
- 对象仍在 pool 中（`_pool.Validate(handle)`，防止使用已被 GC 回收的对象）

`DurObjDictImpl.Upsert` 和 MixedDict 的 `Upsert(DurableObject?)` 通过此方法拦截跨 Revision / 已回收的引用。

Open 时还执行 `ValidateAllReferences()`：遍历所有对象的子引用，检测悬空引用。

### Commit 两段协议（显式结果）

```
Commit(graphRoot, targetFile)
  ├─ A) Primary Commit（三阶段）
  │    ├─ Phase 1: WalkAndMark（DFS + Mark）
  │    ├─ Phase 2: Persist（写盘，不改对象内存；失败回滚 _objectMap）
  │    └─ Phase 3: Finalize（Complete + Sweep + 更新 _head）
  ├─ B) Compaction Apply（条件触发）
  │    ├─ RevisionCompactionSession.TryApply（ShouldCompact + CompactWithUndo + Rebind/引用重写）
  │    ├─ 校验模式分层：
  │    │    - Strict：全量校验 liveObjects 引用一致性
  │    │    - HotPath：只校验 touched objects + moved keys + ObjectMap/pool 对齐
  │    └─ 目标语义：apply / rollback 的内部 bug 直接 fail-fast，不建模为普通结果
  └─ C) Follow-up Persist（仅在确实 compact 后）
       └─ 复用 primary 的 liveObjects 持久化 compaction 造成的脏变更；若 follow-up persist 因外部因素失败，则回滚 compaction，并返回 rolled-back outcome
```

`Commit(graphRoot, targetFile)` 返回 `CommitOutcome`，显式区分：
- `PrimaryOnly`
- `Compacted`
- `CompactionRolledBack`

注意：`Commit(...)` 本身**不会**推进 `_boundSegmentNumber`。
当前实现把"写盘成功"与"branch CAS 成功"分离：

- `Revision.Commit/SaveAs` 只负责把快照 durable 化到调用方提供的 file
- `Repository` 在 branch CAS 成功后，再调用 `revision.AcceptPersistedSegment(targetSegmentNumber)`

因此 `_boundSegmentNumber` 始终表达"这个 Revision 已被 repo 接纳到哪个 segment"，而不是"最近一次写盘尝试的目标 segment"。

### ExportTo / SaveAs（跨文件完整快照）

- `ExportTo(graphRoot, targetFile)`：把当前可达对象图完整写到 `targetFile`，但不修改当前 `Revision` 的 `_boundSegmentNumber / _head / 对象 HeadTicket`
- `SaveAs(graphRoot, targetFile)`：同样做完整快照写入，但成功后只更新 `_head` 与对象内存状态；是否切换 `_boundSegmentNumber` 仍由调用方后续显式确认
- 两者都会把写出的帧标记为 `FrameSource.CrossFileSnapshot`
- 这类 full rebase 帧允许保留当前对象/commit 的逻辑祖先 `parentTicket`，即使该祖先位于其他文件
- 读取时不需要在目标文件继续追这条 parent 链：因为头帧自身是 rebase，`VersionChain.Load` 在读到非空 TypeCode 后就会停止回溯

**错误语义目标**：
- 可归因于对象图状态、文件句柄、RBF I/O、宿主环境等外部不可控因素的失败，返回 `AteliaError`。
- 纯内存 compaction / rollback 中的内部不变量破坏，视为实现 bug，直接 fail-fast。
- `CommitOutcome.CompactionIssue` 只承载“primary 已 durable，但 follow-up persist 因外部因素未完成”的诊断信息。
- rollback 成功后，`Head` 与当前内存图会重新对齐到 primary commit。

当前实现注意：

- `GcPool.RollbackCompaction` 已实现：在 shrink 最终提交前通过 move records 精确恢复 slot 布局与 generation。
- `GcPool.TrimExcessCapacity` 已实现：可独立回收尾部空容量；当前只在 follow-up persist 成功后调用。
- `GcPool.CompactWithUndo` 现在以强异常安全为目标：若在返回 undo token 前内部出错，会先恢复 pool 状态，再重新抛出异常。
- 引用重写现在返回“是否真的改动”，Rollback 只对 moved objects + 实际被重写的对象做 `DiscardChanges()`，不再粗暴扫描全体对象。
- Compaction 流程已收拢为独立的 `RevisionCompactionSession`，`Revision.Commit` 主要保留协议调度职责。
- `RevisionCompactionSession.TryApply(...)` 已把“ShouldCompact”与“是否真的发生移动”合并为单次判定，消除了 `TryCreate + Apply(NotCompacted)` 的双重 no-op 路径。
- compaction 校验现已支持 `Strict` / `HotPath` 两种模式：
  - `DEBUG` 默认 `Strict`
  - 发布默认 `HotPath`
  - 可用环境变量 `ATELIA_SJ_COMPACTION_VALIDATE=STRICT|HOT|HOTPATH` 覆盖
- 已接入轻量调试日志类别：`StateJournal.Commit` / `StateJournal.Compaction`。

当前观测入口：

- compaction 的设计取舍、benchmark 运行建议与首轮观测重点，集中记录在 `docs/StateJournal/compaction-note.md`
- 已落地的结构化收敛包括：
  - primary `liveObjects` 复用到 compaction apply 与 follow-up persist
  - `MixedDictImpl._durableRefCount` 快路径
  - 两层 benchmark：整次 commit 与分阶段 benchmark
- 更复杂的后续方案（如更激进的局部校验、反向引用索引、Epoch 重映射）应等待 benchmark 数据再决定

**关键不变量**：Sweep 在 Persist 之后执行，确保"先写盘再回收"——不会出现对象被 GC 但数据未保存的情况。

### GraphRoot / SymbolTable 持久化

ObjectMap 帧的 TailMeta 当前固定为 8 字节：
- `[0..3] GraphRoot.LocalId.Value`
- `[4..7] SymbolTable.LocalId.Value`（当前固定为 slot 1）

Open 时会同时恢复 GraphRoot 与 SymbolTable 布局约束。

> **历史变更**（2026-03-25）：TailMeta 现为 8 字节 `[rootLocalId:4B][symbolTableLocalId:4B]`。当前只接受带 SymbolTable 的新格式 commit；legacy open 路径已移除。

### Open 全量加载

```
Open(commitTicket, rbfFile, segmentNumber)
  → LoadFull(objectMap 帧) → 获取 ObjectMap + TailMeta
  → 校验 TailMeta 至少 8 字节，且 SymbolTable LocalId 固定为 slot 1
  → 遍历 ObjectMap entries → VersionChain.Load 每个对象（含 SymbolTable）
  → GcPool.Rebuild(entries) 批量重建 pool
  → 从 SymbolTable 重建运行时 StringPool
  → Bind 所有用户对象
  → 从 TailMeta 恢复 GraphRoot
  → 记录 boundSegmentNumber = segmentNumber
  → ValidateAllReferences() 校验引用完整性
```

当前采用 MVP 全量加载策略：Open 时一次性加载整个对象图到内存。

### CommitSnapshot

`_head: CommitSnapshot?` 记录最近一次成功 Commit 的不可变快照（Id / ParentId / ObjectMap / GraphRoot）。首次 Commit 前为 null。`Head` / `HeadParent` / `GraphRoot` 属性均从 `_head` 派生。

> **历史变更**（2026-03-20）：`CommitSnapshot.CommitSequence` 字段和 `Revision.CommitSequence` 属性已删除。

### IChildRefVisitor / IChildRefRewriter 与图遍历

`IChildRefVisitor` 是 GC / 校验用的子引用访问器接口：

```csharp
internal interface IChildRefVisitor {
    void Visit(LocalId childId);
}
```

`IChildRefRewriter` 是 compaction 用的子引用重写接口：

```csharp
internal interface IChildRefRewriter {
    LocalId Rewrite(LocalId oldId);
}
```

`DurableObject.AcceptChildRefVisitor<TVisitor>(ref TVisitor)` 是抽象方法，各实现类按需遍历子引用：
- `DurObjDictImpl`：遍历所有值中的非空 LocalId
- `MixedDictImpl`：遍历 ValueBox 值中的 DurableRef
- `TypedDictImpl` / Deque 占位实现：空实现（不含子引用）

`DurableObject.AcceptChildRefRewrite<TRewriter>(ref TRewriter)` 用于 compaction apply 阶段重写子引用。

---

## Repository 层补充

`Repository` 现在和 `Revision` 的职责边界已经更清晰：

- `Repository` 持有 branch 元数据、recent/archive 布局、active segment 句柄与 repo 锁
- `Revision` 持有内存对象图、`_head` 视角与 `_boundSegmentNumber`
- `Repository.Commit(graphRoot)` 通过 `graphRoot.Revision.BranchName` 找到 branch，再根据 `revision.HeadSegmentNumber` 与当前 `ActiveSegmentNumber` 是否相同，决定走原地 `Commit` 还是跨 segment `SaveAs`
- `Repository` 会先形成一个 `RevisionWritePlan`，执行写盘后再做 branch CAS；只有 CAS 成功后才确认 segment 切换

当前 segment/file 生命周期策略：

- active segment 的可写 `IRbfFile` 由 `Repository` 长期持有
- 历史 segment 在 `CheckoutBranch()` 时按次只读打开，`Revision.Open(...)` 完成后立即关闭
- `MaintainSegmentLayout()` 与 `Open()` 的 recent→archive 收敛不再需要顾虑已加载 `Revision` 长期占用旧文件句柄

关键文件：`Revision.cs`, `Internal/IChildRefVisitor.cs`, `Internal/IChildRefRewriter.cs`, `DurableObject.cs`

---

## 文件路径速查

```
src/StateJournal/
├── Durable.cs                    # internal 工厂基础设施（VersionChain.Load 使用）
├── DurableObject.cs              # 抽象基类（Bind, DetachByGc, IsBoundTo, AcceptChildRefVisitor）
├── DurableState.cs               # 生命周期枚举（Clean/PersistentDirty/TransientDirty/Detached）
├── DurableDictBase.cs            # Dict 共享版本链逻辑
├── DurableDict.Typed.cs          # TypedDict 外观
├── DurableDict.Mixed.cs          # MixedDict 外观（含 generic accessor）
├── DurableDeque.Typed.cs          # TypedDeque 外观（占位）
├── DurableDeque.Mixed.cs          # MixedDeque 外观（占位）
├── Revision.cs                   # 对象图工作会话（Primary Commit + Compaction Follow-up）
├── Repository.cs                 # Repo 骨架
├── IDict.cs                      # Dict 接口 + 扩展方法
├── ValueKind.cs                  # 值类型枚举
├── DurableObjectKind.cs          # 对象种类枚举
├── LocalId.cs / CommitTicket.cs      # 标识类型（LocalId ↔ SlotHandle 互转）
├── GetIssue.cs                   # Get 操作的结果枚举
├── ObjectDetachedException.cs    # Detached 对象操作异常
├── Internal/
│   ├── ValueBox.cs + ValueBox.*.cs    # Tagged-Pointer 值载体
│   ├── BoxLzc.cs                      # LZC 编码常量
│   ├── ValuePools.cs                  # 静态池单例
│   ├── DictChangeTracker.cs           # 双字典变更追踪
│   ├── BitDivision.cs / BoolDivision.cs
│   ├── VersionChain.cs                # Write/Save/Load 核心流程 + PendingSave
│   ├── VersionChainStatus.cs          # Rebase/Deltify 成本决策
│   ├── FrameTag.cs                    # RBF 帧元数据结构
│   ├── TypeCodec.cs                   # 栈式类型编码
│   ├── DurableFactory.cs              # 泛型工厂缓存
│   ├── HelperRegistry.cs              # 类型→Helper 映射
│   ├── ITypeHelper.cs                 # 静态抽象 Helper 接口
│   ├── DiffWriteContext.cs            # 写入上下文
│   ├── DictDiffApplier.cs             # Load 时的增量应用
│   ├── DurableRef.cs                  # (ObjectKind, LocalId) 轻量引用
│   ├── IChildRefVisitor.cs            # GC/校验用子引用访问器接口
│   ├── IChildRefRewriter.cs           # Compaction 子引用重写接口
│   ├── HeapValueKind.cs
│   ├── StateJournalErrors.cs          # SjCorruptionError / SjStateError / SjCompaction*Error
│   ├── TypedDictImpl.cs / MixedDictImpl.cs / DurObjDictImpl.cs
│   ├── TypedDequeImpl.cs / MixedDequeImpl.cs
│   └── ...
├── Serialization/
│   ├── BinaryDiffWriter.cs / BinaryDiffReader.cs
│   ├── ScalarRules.cs                 # CBOR-inspired 常量
│   ├── TaggedFloat.cs / TaggedInt.cs  # 序列化辅助
│   ├── TaggedValueDispatcher.cs       # MixedDict 反序列化 dispatch
│   └── VarInt.cs
└── Pools/
    ├── SlotPool.cs       # Slab + Bitmap 基础分配器
    ├── GcPool.cs         # Mark-Sweep GC 池（含 CompactWithUndo + RollbackCompaction）
    ├── InternPool.cs     # 去重 + Mark-Sweep 池
    ├── SlabBitmap.cs     # Bitmap 基础设施
    ├── SlotHandle.cs     # 32bit 胖指针（generation + index）
    └── IMarkSweepPool.cs / IValuePool.cs / ISweepCollectHandler.cs
```

---

## 设计文档索引

| 文档 | 路径 | 说明 |
|:-----|:-----|:-----|
| 异构存储设计决策 | `docs/StateJournal/v4/异构存储设计决策.md` | ValueBox 核心决策清单 |
| Tagged-Pointer 布局 | `docs/StateJournal/v4/tagged-pointer.md` | BoxLzc 全景分配表 |
| 浮点相等语义 | `docs/StateJournal/v4/floating-point-equality-semantics.md` | BitExact vs NumericEquiv |
| CBOR-inspired 编码 | `docs/StateJournal/v4/tagged-scalar-cbor-inspired-encoding.md` | 序列化规则详述 |
| 文本序列化决策 | `docs/StateJournal/v4/text-serialization-decision.md` | 阶段性不引入文本格式 |
| NaN-Boxing 讨论 | `docs/StateJournal/v4/nan-boxing.md` | 早期讨论记录 |
| archived/ | `docs/StateJournal/archived/` | v2/v3 旧版设计，大部分已过时 |
