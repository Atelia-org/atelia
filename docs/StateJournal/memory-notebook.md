# StateJournal — Memory Notebook

> **用途**：供 AI Agent 在新会话中快速重建对 `src/StateJournal` 的整体认知。
> **原则**：只记脉络与决策，不复述代码细节；有疑问时指向源文件路径。
> **最后更新**：2026-03-14

---

## 一句话定位

StateJournal 是一个**可持久化增量序列化对象图引擎**——在内存中维护类型化/异构容器（Dict/List），通过 delta/rebase 版本链将变更持久化到 RBF 文件，支持按版本回放重建任意时刻的对象状态。

---

## 依赖关系

```
Atelia.Primitives   ← AteliaResult<T>, AteliaError
Atelia.Data         ← SizedPtr (38:26 bit 紧凑指针，用作 RBF 帧 ticket)
Atelia.Rbf          ← IRbfFile, RbfFrameBuilder, RbfPooledFrame (追加式二进制分帧文件)
```

StateJournal 本身 **不依赖** 网络、文件系统 API 或第三方库。RBF 抽象了文件 I/O。

---

## 对象模型层（Public API）

### 继承体系

```
DurableObject                           // 抽象基类：LocalId, GlobalId, DurableState, 生命周期钩子
  ├─ DurableDictBase<TKey>              // 抽象：版本链读写逻辑(WritePendingDiff/ApplyDelta/OnCommitSucceeded)
  │    ├─ DurableDict<TKey, TValue>     // TypedDict 外观（值类型同构）
  │    │    ├─ TypedDictImpl<…>         // Internal：自持 DictChangeTracker<TKey, TValue>
  │    │    └─ DurObjDictImpl<…>        // Internal：值为 DurableObject 子类，内部存 LocalId
  │    └─ DurableDict<TKey>             // MixedDict 外观（值异构，内部 ValueBox）
  │         └─ MixedDictImpl<…>         // Internal：自持 DictChangeTracker<TKey, ValueBox>
  ├─ DurableList<T>                     // TypedList 外观 ← ⚠️ 占位，尚未实现
  │    ├─ TypedListImpl<…>              // Internal 占位。基元类型同构值。
  │    └─ DurObjListImpl<…>             // Internal 占位。值为 DurableObject 子类，内部存 LocalId
  └─ DurableList                        // MixedList 外观 ← ⚠️ 占位，尚未实现
       └─ MixedListImpl                 // Internal 占位。值异构，内部 ValueBox
```

### DurableState 生命周期

```
CreateObject() → TransientDirty
LoadObject()   → Clean
Modify(Clean)  → PersistentDirty
Commit(Dirty)  → Clean
Discard(PersistentDirty) → Clean（回退到 committed 快照）
Discard(TransientDirty)  → Detached（终态，不可恢复）
```

关键文件：`DurableState.cs`, `DurableObject.cs`

### 工厂入口

`Durable` 静态类（`Durable.cs`）是唯一的创建入口：

```csharp
Durable.Dict<string, int>()    // TypedDict
Durable.Dict<string>()          // MixedDict（异构值）
Durable.List<int>()             // TypedList（占位）
Durable.List()                  // MixedList（占位）
```

背后通过 Static Generic Class Cache 编译工厂委托，首次访问后接近零开销。
详见 `Internal/DurableFactory.cs`。

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
  └─ OfString: InternPool<string>   // 字符串去重池
```

- **SlotPool\<T\>**（`Pools/SlotPool.cs`）：Slab + Bitmap 分配器，O(1) alloc/free/index，尾部空页自动回收，8-bit generation 防 ABA
- **GcPool\<T\>**（`Pools/GcPool.cs`）：包装 SlotPool + Mark-Sweep GC Bitmap
- **InternPool\<T\>**（`Pools/InternPool.cs`）：去重 + Mark-Sweep GC，哈希桶链 + SlotPool
- **SlabBitmap**（`Pools/SlabBitmap.cs`）：GcPool/InternPool 共用的 bitmap 基础设施

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

**Save**（`VersionChain.Save`）：

```
对象有变更？
  → BeginAppend() 获取 RbfFrameBuilder
  → WritePendingDiff() 序列化 diff（由 DurableDictBase 实现）
    → 内部决策 rebase vs deltify（VersionChainStatus.ShouldRebase）
    → rebase: 写入 TypeCode + 全量数据
    → deltify: 写入空 TypeCode + 增量数据
  → EndAppend(tag) 得到 SizedPtr
  → OnCommitSucceeded() 更新内部状态
```

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

RBF 帧的 `uint tag` 字段被 StateJournal 结构化为三部分：
- VersionKind（2bit）：Rebase / Delta
- ObjectKind（4bit）：MixedDict / TypedDict / MixedList / TypedList
- UsageKind（4bit）：Blank / UserPayload / ObjectMap

关键文件：`Internal/FrameTag.cs`

---

## 类型系统层

### TypeCodec — 栈式操作码

将泛型类型编码为 `byte[]`，用于 rebase 帧的类型标识。操作码基于栈：

```
PushInt32, PushString → MakeTypedDict  //= DurableDict<string, int>
PushString → MakeMixedDict              //= DurableDict<string>
PushInt32 → MakeTypedList               //= DurableList<int>
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
| Pools（SlotPool / GcPool / InternPool / SlabBitmap） | ✅ 已完成 | 含 Mark-Sweep GC |
| DictChangeTracker + BitDivision | ✅ 已完成 | 双字典 + 脏标记 |
| DurableDict（Typed / Mixed / DurObj） | ✅ 已完成 | 含 Rebase/Deltify |
| BinaryDiffWriter / Reader | ✅ 已完成 | 含 Tagged + Bare 编码 |
| TypeCodec / HelperRegistry / DurableFactory | ✅ 已完成 | 泛型类型 → 工厂 |
| VersionChain（Save/Load） | ✅ 已完成 | RBF 集成 |
| DurableList（Typed/Mixed） | ⬜ 占位 | 所有方法 throw；Diff 方案待定（候选：最短编辑距离 / 操作记录合并 / BTree） |
| Revision | 🔧 进行中 | ObjectMap + Commit/Open/Load + CreateDict/CreateList 工厂 + Identity Map 已实现 |
| Revision.ObjectMap | ✅ 已完成 | `DurableDict<uint, ulong>`，LocalId → SizedPtr 映射，Save/Load 已通过测试 |
| Revision.LocalId 管理 | 🔧 进行中 | `LocalIdAllocator`：空洞回收 + 高水位分配。后续计划用 `GcPool<DurableObject>` 替代 |
| DurableObject 生存期绑定 | 🔧 进行中 | Bind(Revision, LocalId) 只读绑定 + 跨 Revision 引用拦截 |
| Repository | ⬜ 骨架 | 仅有 DirectoryPath 属性 |

---

## 对象生存期管理演进路线（2026-03-15 规划）

### 阶段一（已实施）：Revision-Owned Factory + Identity Map

**核心变更**：`Durable` 静态工厂降为 `internal`，公开创建入口移至 `Revision` 实例方法。

- `DurableObject.Bind(revision, localId)`：internal 一次性绑定，对象终生属于一个 Revision。
- `Revision.CreateDict<K,V>()` / `CreateDict<K>()` / `CreateList<T>()` / `CreateList()`：分配 LocalId、绑定、标记 TransientDirty、放入 Identity Map。
- **Identity Map**（`Dictionary<LocalId, DurableObject>`）：Load 命中缓存直接返回，保证实例唯一。采用 Strong Reference（dirty 安全 + 实例唯一保证）。
- **跨 Revision 引用拦截**：`DurObjDictImpl.Upsert` 和 `DurableDict<TKey>.Upsert(DurableObject?)` 检查 `value.Revision == this.Revision`。
- `GlobalId` 属性已删除（语义陷阱：返回的是 HEAD 版本上的 GlobalId，易误以为是 commit 后的）。

**设计约束**：
- 对象一经 Bind 不可更改 Revision 或 LocalId。
- VersionChain.Load 内部仍使用 `Durable`（now internal）工厂 + `DurableFactory.TryCreate` 创建空壳，Revision.Load 在外层 Bind。
- ObjectMap（`DurableDict<uint, ulong>`）本身不走 Bind 流程（它是 Revision 的内部基础设施）。

### 阶段二（已实施）：GcPool\<DurableObject\> 统一管理

**目标**：用一个 `GcPool<DurableObject>` 同时替代 `LocalIdAllocator` + `Dictionary<LocalId, DurableObject>` Identity Map。

| 现状 | 目标 |
|:-----|:-----|
| `LocalId` 内含裸 `uint` | `LocalId` 内含 `SlotHandle`（带 generation 防 ABA） |
| 分配 ID = `LocalIdAllocator.Allocate()` | 分配 ID = `GcPool.Alloc(obj)` 返回 SlotHandle |
| 查对象 = `_identityMap[localId]` (哈希查找) | 查对象 = `GcPool[slotHandle]` (O(1) 数组索引) |
| 空洞追踪 = HoleSpan + Queue | 空洞管理 = SlotPool 内建 bitmap free-list |
| GC = 无 | GC = Mark-Sweep：Commit 时从 GraphRoot 遍历标记可达对象 |

**Mark-Sweep GC 设计思路**：

1. **Commit 时触发**：在 `Revision.Commit()` 保存脏对象之前，先从 `GraphRoot` 开始 DFS/BFS 遍历对象图。
2. **Mark 阶段**：递归标记所有从 GraphRoot 可达的 DurableObject。DurObjDict/MixedDict 中的 DurableRef 是遍历边。
3. **Sweep 阶段**：未标记的对象从 GcPool 释放、从 ObjectMap 中移除。
4. **落盘保证**：Sweep 后 ObjectMap 只含可达对象 → 持久化状态始终健康。

**Lazy-Load 兼容（性能优化，可延后）**：

- **MVP 简单实现**：Open Revision 时直接加载整个对象图到内存。内存集中，运行时简单。
- **优化实现**：保持 Lazy-Load，Commit GC 时对未加载对象临时 Load 以遍历其 DurableRef 成员，遍历完即释放。
- **优化辅助**：序列化时给对象帧加标记"是否含有对其他 DurableObject 的引用"（`HasDurableRefs` flag in FrameTag），GC 遍历时跳过不含引用的叶对象，避免不必要的临时 Load。

---

## 文件路径速查

```
src/StateJournal/
├── Durable.cs                    # internal 工厂基础设施（VersionChain.Load 使用）
├── DurableObject.cs              # 抽象基类
├── DurableState.cs               # 生命周期枚举
├── DurableDictBase.cs            # Dict 共享版本链逻辑
├── DurableDict.Typed.cs          # TypedDict 外观
├── DurableDict.Mixed.cs          # MixedDict 外观（含 generic accessor）
├── DurableList.Typed.cs          # TypedList 外观（占位）
├── DurableList.Mixed.cs          # MixedList 外观（占位）
├── Revision.cs                   # Revision 骨架
├── Repository.cs                 # Repo 骨架
├── IDict.cs                      # Dict 接口 + 扩展方法
├── ValueKind.cs                  # 值类型枚举
├── DurableObjectKind.cs          # 对象种类枚举
├── LocalId.cs / CommitId.cs / GlobalId.cs
├── LocalIdExhaustedException.cs
├── GetIssue.cs                   # Get 操作的结果枚举
├── ObjectDetachedException.cs
├── Internal/
│   ├── ValueBox.cs + ValueBox.*.cs    # Tagged-Pointer 值载体
│   ├── BoxLzc.cs                      # LZC 编码常量
│   ├── ValuePools.cs                  # 静态池单例
│   ├── DictChangeTracker.cs           # 双字典变更追踪
│   ├── BitDivision.cs / BoolDivision.cs
│   ├── VersionChain.cs                # Save/Load 核心流程
│   ├── VersionChainStatus.cs          # Rebase/Deltify 成本决策
│   ├── FrameTag.cs                    # RBF 帧元数据结构
│   ├── TypeCodec.cs                   # 栈式类型编码
│   ├── DurableFactory.cs              # 泛型工厂缓存
│   ├── HelperRegistry.cs              # 类型→Helper 映射
│   ├── ITypeHelper.cs                 # 静态抽象 Helper 接口
│   ├── DiffWriteContext.cs            # 写入上下文
│   ├── DictDiffApplier.cs             # Load 时的增量应用
│   ├── DurableRef.cs                 # (ObjectKind, LocalId) 轻量引用
│   ├── HeapValueKind.cs
│   ├── StateJournalErrors.cs          # SjCorruptionError 等
│   ├── TypedDictImpl.cs / MixedDictImpl.cs / DurObjDictImpl.cs
│   ├── TypedListImpl.cs / MixedListImpl.cs
│   └── ...
├── Serialization/
│   ├── BinaryDiffWriter.cs / BinaryDiffReader.cs
│   ├── ScalarRules.cs                 # CBOR-inspired 常量
│   ├── TaggedFloat.cs / TaggedInt.cs  # 序列化辅助
│   ├── TaggedValueDispatcher.cs       # MixedDict 反序列化 dispatch
│   └── VarInt.cs
└── Pools/
    ├── SlotPool.cs       # Slab + Bitmap 基础分配器
    ├── GcPool.cs         # Mark-Sweep GC 池
    ├── InternPool.cs     # 去重 + Mark-Sweep 池
    ├── SlabBitmap.cs     # Bitmap 基础设施
    ├── SlotHandle.cs     # 32bit 胖指针（generation + index）
    └── IMarkSweepPool.cs / IValuePool.cs
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
