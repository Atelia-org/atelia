# StateJournal — Memory Notebook

> **用途**：供 AI Agent 在新会话中快速重建对 `src/StateJournal` 的整体认知。
> **原则**：只记当前主线设计、已落地决策与高风险边界，不复述代码细节。
> **最后更新**：2026-04-09

---

## 一句话定位

StateJournal 是一个**可持久化增量序列化对象图引擎**：

- 内存中维护类型化/异构 Durable 容器
- 通过 delta/rebase 版本链持久化到 RBF 文件
- 通过 `Revision` 维护一个可编辑对象图工作态
- 通过 `Repository` 管理 branch、segment 与文件布局

---

## 当前主线决策

### 1. StateJournal 主协议已去除 Compaction

当前 `Revision.Commit(...)` 只有 primary commit 主线：

- Walk and mark
- Persist user objects / `SymbolTable` / `ObjectMap`
- Complete + Sweep + 更新 `_head`

不再存在：

- `RevisionCompactionSession`
- compaction apply / rollback
- follow-up persist
- `CommitCompletion.Compacted` / `CompactionRolledBack`
- `DurableObject.Rebind`
- `AcceptChildRefRewrite`

注意：

- 底层 `SlotPool` / `GcPool` / `InternPool` 里仍保留 compaction 相关原语与测试
- 但它们已不是 StateJournal 当前提交协议的一部分
- 现阶段应把这些能力视为“底层实验/储备能力”，而不是上层系统语义

### 2. `SlotPool<T>` 已支持 value slab 稀疏化

这条是去 compaction 的关键支撑。

当前 `SlotPool<T>` 的 `_slabs` 形状是：

```csharp
private T[]?[] _slabs;
```

语义：

- `null slab` 等价于“该 slab 当前逻辑上全空”
- 需要写入时再重新分配 value slab
- generation 元数据与 free bitmap 不随之释放
- 尾部 metadata 仍可单独 shrink

因此当前系统更偏向：

- 接受一定程度的稀疏性
- 避免为“地址压紧”付出全图 rewrite 的系统复杂度

### 3. typed 容器的 string 已改为延迟 intern

当前 typed 路线的运行时模型已经改变：

- `TypedDict<TKey, string>` / `TypedDeque<string>` / `string key`
- 运行时直接保存普通 `string`
- commit / 序列化时才 intern 为 `SymbolId`
- load / 反序列化时再解回 `string`

这意味着：

- typed string 的运行时真源不再是 `SymbolId`
- `SymbolTable` 更像持久化编码层的一部分
- `TypedDeque<string>` 已正式开放
- `TypedDict<TKey, string>` 不再需要专用 `SymbolValDictImpl`

但 mixed 路线不变：

- `MixedDict` / `MixedDeque` 的 string 仍以 `ValueBox(SymbolId)` 存储
- 因为这是 `ValueBox` tagged-pointer 异构模型本身的一部分

### 4. typed string load 采用 placeholder 过渡方案

引入延迟 intern 后，版本链历史回放会遇到一个真实问题：

- 历史 delta 中可能出现旧的 `SymbolId`
- 这些字符串在目标 head commit 的 `SymbolTable` 中可能已不存在
- 如果 `ApplyDelta` 期间立即强制 `SymbolId -> string`，则历史回放会失败

当前正式过渡方案是：

- `BinaryDiffReader.BareSymbolId(...)` 在 load 时优先查 head `StringPool`
- 若缺失，则通过 `LoadPlaceholderTracker` 生成“每个缺失 `SymbolId` 对应唯一 placeholder string”
- 历史回放完成后，再对 typed 容器做 placeholder 残留检查
- 若最终 surviving 数据里仍残留 placeholder，则判定为数据损坏或内部 bug

长期更正统的储备方案仍是：

- `staging in ChangeTracker`
- 文档见 `docs/StateJournal/typed-string-load-staging-reserve.md`

### 5. typed / mixed 的字符串标记与重建后校验职责已分离

这是当前实现里很重要的一条边界。

提交期 `WalkAndMark`：

- typed string / string key 通过 `ITypeHelper<T>.VisitChildRefs(...)` 暴露 `string` facade
- `WalkMarkVisitor.Visit(string?)` 负责 `StringPool.Store(...)` + `MarkReachable`
- mixed 容器里直接存储的 `ValueBox(SymbolId)` 仍走 `Visit(SymbolId)` 直达标记

load / 历史回放完成后：

- typed 容器的 string facade 通过 `ValidateReconstructed(...)` 检查 placeholder 是否残留
- mixed 容器里 surviving 的 `ValueBox(SymbolId)` 也在 `ValidateReconstructed(...)` 里直接做 `symbolPool.Validate(...)`
- `Revision.ValidateAllReferences()` 现在只负责 DurableObject 引用完整性，不再承担 symbol 校验

当前分工应牢记：

- 提交期标记：`LocalId`、typed `string facade`、mixed `SymbolId`
- 重建后校验：typed placeholder 残留 + mixed surviving `SymbolId`
- Open 后全图引用校验：只校验 DurableObject 引用

### 6. ordered skip-list load 的 committed canonicalization 属于 `ApplyDelta`

这是 `TypedOrderedDict` / `SkipListCore` 当前的一条明确边界：

- `ApplyDelta` 负责先重建 committed 叶链，再按新的 `committedHead` 清理 committed 窗口里不可达的死节点
- `SyncCurrentFromCommitted` 只负责把 committed 暴露为 current，并重建纯内存塔索引
- 不应再把“load 后 dead committed node 的 GC”理解为 `SyncCurrentFromCommitted` 的副作用

这样做的原因是：

- dead-node 清理本质上属于 committed 状态收敛，而不是 current 视图建立
- 塔索引依赖压缩后的物理 index，因此 committed canonicalization 必须发生在 `RebuildIndex` 之前
- 这也让 `SyncCurrentFromCommitted` 与其他 typed/mixed 容器保持更一致的职责边界

### 7. `LeafChainStore` 里移动 committed 槽位的 GC 不能与活跃 dirty tracking 共存

`LeafChainStore` 当前有一条必须牢记的不变量：

- `CollectCommitted` / `CollectAll` 这类会移动 committed 槽位的压缩操作，不能在活跃 dirty tracking 存在时执行
- 原因是 `CapturedOriginal.Index` 与 dirty bit 都绑定物理槽位；一旦 committed 槽位被压缩，索引就会失效
- `CollectDraft` 只移动 draft 窗口，不会破坏 committed dirty tracking，因此不受这条约束

当前 `SkipListCore.Commit` 的时序已经按这条不变量调整为：

- 先 `arena.Commit()` 结束 dirty tracking
- 再 `CollectCommitted(head)` 做 committed canonicalization
- 最后 `SyncCurrentFromCommitted()` 与 `RebuildIndex()`

### 8. enum Mask 成员已提取为 helper class 常量

`DurableObjectKind.Mask`、`HeapValueKind.Mask`、`VersionKind.Mask`、`FrameUsage.Mask`、`FrameSource.Mask` 已全部从 enum 中移除，改为对应 helper 静态类中的 `const byte BitMask`（或 `FrameTag` 内部的私有 `const`）。

原因：

- 新增 `TypedOrderedDict` 后，`Mask` 不再是有意义的枚举成员
- 保留在 enum 中会干扰 exhaustive switch/pattern matching

---

## 对象模型层（Public API）

### 继承体系

```text
DurableObject
  ├─ DurableDictBase<TKey>
  │    ├─ DurableDict<TKey, TValue>     // TypedDict facade
  │    │    ├─ TypedDictImpl<...>       // 一般 typed value
  │    │    └─ DurObjDictImpl<...>      // TValue : DurableObject，内部存 LocalId
  │    ├─ DurableDict<TKey>             // MixedDict facade
  │    │    └─ MixedDictImpl<...>       // 内部值为 ValueBox
  │    └─ DurableOrderedDict<TKey, TValue>  // TypedOrderedDict facade
  │         └─ TypedOrderedDictImpl<...>    // 内部基于 SkipListCore
  ├─ DurableDeque<T>                    // TypedDeque facade
  │    ├─ TypedDequeImpl<...>           // 一般 typed value
  │    └─ DurObjDequeImpl<...>          // TValue : DurableObject，内部存 LocalId
  └─ DurableDeque                       // MixedDeque facade
       └─ MixedDequeImpl                // 内部值为 ValueBox
```

注意：

- `DurableOrderedDict` 继承自 `DurableDictBase<TKey>`，仅支持 Typed，不支持 Mixed 也不支持 DurableObject value
- `DurableDeque` 现在已实现，不再是“占位”
- typed / mixed 两条 deque 路线都已经打通

### `DurableState` 生命周期

```text
CreateObject() -> TransientDirty
LoadObject()   -> Clean
Modify(Clean)  -> PersistentDirty
Commit(Dirty)  -> Clean
Discard(PersistentDirty) -> Clean
Discard(TransientDirty)  -> Detached
GC Sweep(不可达) -> Detached
```

关键文件：

- `src/StateJournal/DurableObject.cs`
- `src/StateJournal/DurableState.cs`

### 工厂入口

公开创建入口仍是 `Revision` 实例方法：

```csharp
var rev = new Revision(boundSegmentNumber: 1);

rev.CreateDict<string, int>();        // TypedDict
rev.CreateDict<string>();             // MixedDict
rev.CreateOrderedDict<string, int>(); // TypedOrderedDict
rev.CreateDeque<int>();               // TypedDeque
rev.CreateDeque();                    // MixedDeque
```

创建时自动：

- 分配 `LocalId`
- 绑定到当前 `Revision`
- 设为 `TransientDirty`

---

## 值存储层（ValueBox & Pools）

### `ValueBox`：mixed 容器的异构值载体

`ValueBox` 是一个 8 字节 tagged-pointer：

- inline double / integer / bool / null
- heap slot
- durable ref
- symbol-backed string

当前关键边界：

- mixed string 继续保存 `SymbolId`
- typed string 不再保存 `SymbolId`

这两条路线不能混淆。

关键文件：

- `src/StateJournal/Internal/ValueBox.cs`
- `src/StateJournal/Internal/ValueBox.String.cs`

### Pools 体系

```text
SlotPool<T>     // slab allocator，已支持 value slab 稀疏释放
GcPool<T>       // SlotPool + mark-sweep
InternPool<T>   // 去重 + mark-sweep
StringPool      // InternPool<string> + identity cache
```

当前对新会话最重要的记忆点：

- `SlotPool<T>` 已支持 `null slab`
- generation 元数据不随 value slab 释放
- `StringPool` 是 `Revision` 运行时 symbol 真源
- `SymbolTable` 是 durable mirror，不是 typed string 的运行时真源

### `SymbolTable` / `StringPool` 的角色分工

`Revision` 同时持有：

- `_symbolMirror : DurableDict<uint, InlineString>`
- `_symbolPool : StringPool`

当前语义：

- `_symbolPool` 是运行时 symbol 真源
- `_symbolMirror` 是 durable mirror，不是运行时真源
- 用户对象写出时，typed / mixed string 会通过 writer 的 write-through 路径把 mirror 补齐
- 提交期随后只需 prune 不可达旧 symbol，并可选做 full validation

相关 API：

- `Revision.InternSymbol(string?)`
- `Revision.InternReachableSymbol(string?)`
- `Revision.GetSymbol(SymbolId)`
- `Revision.TryGetSymbol(SymbolId, out string?)`

---

## Change Tracking 层

### `DictChangeTracker<TKey, TValue>`

核心模型仍是：

```text
_committed : Dictionary<TKey, TValue?>
_current   : Dictionary<TKey, TValue?>
_dirtyKeys : BitDivision<TKey>
```

语义：

- `_committed` 表示上次 commit 的快照
- `_current` 表示当前工作态
- dirty 集跟踪 upsert / remove
- `Commit()` 把 dirty 收敛进 `_committed`
- `Revert()` 把 `_current` 恢复到 `_committed`

### `DequeChangeTracker<T>`

deque 路线已正式落地，承担：

- typed deque 的 `_committed / _current` 双快照
- push / pop / set / revert / commit
- rebase / deltify 写出

对后续 Agent 来说，最重要的不是内部细节，而是：

- deque 已经不是设计草案
- API / diff / round-trip / durable child ref 都已有测试覆盖

### `DurableOrderedDict` 的 Change Tracking

`DurableOrderedDict` **不使用** `DictChangeTracker`。它的 commit / revert / delta 由 `SkipListCore` + `LeafChainStore` 内部管理——叶链自身维护 dirty tracking（dirty value bits + dirty link bits + captured originals），整体架构与基于 `Dictionary<TKey, TValue>` 快照的 `DictChangeTracker` 完全不同。

---

## NodeContainers 层

`src/StateJournal/NodeContainers/` 是本次新增的基于共享节点的有序数据结构子系统。

### 核心组件

| 类型 | 职责 |
|:-----|:-----|
| `LeafChainStore<TKey, TValue, KHelper, VHelper>` | 叶链 KV 仓库：连续数组存储 + Next 指针链 + 独立 dirty tracking（value bit / link bit / captured originals）+ mark-sweep GC |
| `LeafHandle` | 节点句柄：`uint Sequence`（持久身份）+ 缓存的物理 index |
| `ListCore<T>` | 内部动态数组，用于 `CapturedOriginal` 列表等 |
| `SkipListCore<TKey, TValue, KHelper, VHelper>` | 跳表有序字典核心：叶链参与序列化，索引塔纯内存态从叶链确定性重建 |

### 关键设计决策

- **叶链分离**：Key/Value 与 Next 指针分离存储，分别追踪脏状态；序列化时可独立表达"仅链接变更"与"仅 value 变更"
- **Key 不可变契约**：Key 仅在 `AllocNode` 和 `ApplyDelta`（新增段）时设定，后续只允许改 Value 和 Next
- **索引塔纯内存态**：`SkipListCore` 的塔索引不参与序列化，从叶链确定性重建（基于 key hash 确定塔高度）
- **sequence 单调递增、不复用**：`LeafChainStore` 的 `_nextAllocSequence` 只增不减，GC 后回收的 sequence 不会被新节点占用
- **增量帧三段式**：link mutations → value mutations → appended nodes（详见 `LeafChainStore` 注释）

### `SkipListCore` 的 commit 时序

```text
1. arena.Commit()              → 结束 dirty tracking
2. CollectAll(head)            → 全量 GC，移除不可达死节点
3. _committedHead = _head      → 记录 committed 起点
4. SyncCurrentFromCommitted()  → current = committed + RebuildIndex
```

---

## 类型系统层

### `TypeCodec`

泛型类型仍通过栈式 `byte[]` type code 编码：

```text
PushInt32, PushString -> MakeTypedDict
PushString            -> MakeMixedDict
PushInt32             -> MakeTypedDeque
PushInt32, PushString -> MakeTypedOrderedDict
```

关键文件：

- `src/StateJournal/Internal/TypeCodec.cs`

### `HelperRegistry` + `ITypeHelper<T>`

`HelperRegistry` 仍负责：

- 验证泛型参数是否支持
- 解析对应 helper 类型
- 生成持久化 `TypeCode`

`ITypeHelper<T>` 现在除 `Equals/Compare/Write/Read/UpdateOrInit/Freeze/ReleaseSlot` 外，
还新增了一组提交期/加载期静态钩子：

- `NeedVisitChildRefs`
- `VisitChildRefs(...)`
- `NeedValidateLoadPlaceholders`
- `ValidateReconstructed(...)`

当前这组钩子的主要用途是：

- 把 typed string 的 child-ref visit 与重建后校验下沉到 helper 多态
- 避免容器实现里继续散落 `typeof(T) == typeof(string)` 分支
- 让容器保留对自身枚举结构的掌控，不需要为了 helper 形状物化临时集合

`StringHelper` 当前同时承担：

- typed string 的序列化桥接
- typed string 的重建后 placeholder 残留检查

`HelperRegistry` 内部已做拆分重构：

- 基元标量类型的解析提取为 `ResolveScalarValueLeafHelper`（与 `ResolveKeyHelper` 共享逻辑）
- Tuple 元素解析 `ResolveTupleElementHelper` 不再递归调用完整的 `ResolveValueHelper`，而是先尝试 scalar leaf，再尝试嵌套 tuple
- 这使得 tuple 内部不会意外匹配到 `DurableDict`/`DurableDeque`/`DurableOrderedDict` 等容器类型作为 tuple 元素

`Compare` 方法：

- 用于 `SkipListCore` 等有序容器的键比较
- 默认委托 `Comparer<T>.Default`（对数值类型已跨平台稳定）
- 对 `string` / `InlineString` 覆盖为 `StringComparison.Ordinal` 语义
- 所有 `ValueTupleHelper` 也实现了字典序 `Compare`

关键文件：

- `src/StateJournal/Internal/ITypeHelper.cs`
- `src/StateJournal/Internal/HelperRegistry.cs`

---

## 序列化层

### `BinaryDiffWriter`

`BinaryDiffWriter` 现在对 typed string 的关键语义是：

- `BareSymbolId(string?)`
- 通过 `_stringRevision.InternReachableSymbol(value)` 把 facade `string` 编码成 `SymbolId`
- 同时标记该 symbol 为本轮 commit 可达
- 并通过 `Revision.ObservePersistedSymbol(...)` 把 durable mirror 补齐

注意：

- 提交期 mark 与写出期编码当前仍会各自触发一次 `StringPool` 查找
- 这是一种当前可接受的简化实现，不是最终极致形态

### `BinaryDiffReader`

`BinaryDiffReader.BareSymbolId(...)` 现在有三段行为：

1. 读出裸 `SymbolId`
2. 优先从当前 head `StringPool` 解码
3. 若缺失且提供了 `LoadPlaceholderTracker`，则返回本次 load 私有 placeholder string

关键文件：

- `src/StateJournal/Serialization/BinaryDiffWriter.cs`
- `src/StateJournal/Serialization/BinaryDiffReader.cs`
- `src/StateJournal/Internal/LoadPlaceholderTracker.cs`

---

## 版本链层

### `VersionChain.Write / Save`

核心流程没有变：

- `Write(...)` 写帧但不改对象内存态，返回 `PendingSave`
- `PendingSave.Complete()` 应用内存态变更
- `Save(...)` = `Write(...) + Complete()`

### `VersionChain.Load`

当前 load 流程里新增了一个很关键的概念：

- 当提供 `symbolPool` 时，会同时创建 `LoadPlaceholderTracker`
- head 之前的历史 delta 回放允许短暂出现无法从 head symbol table 解出的旧 `SymbolId`
- 回放完成后调用 `DurableObject.ValidateReconstructed(...)`

当前 `ValidateReconstructed(...)` 的职责边界：

- typed string key/value/element：检查 placeholder 是否残留
- mixed string key：如经过 typed helper 路径，也检查 placeholder
- mixed value / mixed deque element 中的 `ValueBox(SymbolId)`：在这里直接验证 surviving `SymbolId` 是否仍在 `symbolPool` 中有效
- 这一步是“对象局部的 reconstruction 收尾校验”，不是全图对象引用校验

关键文件：

- `src/StateJournal/Internal/VersionChain.cs`
- `src/StateJournal/DurableObject.cs`

---

## Revision 层

### `Revision` 的角色

`Revision` 是一个已打开的对象图工作会话：

- 持有当前工作态对象图
- 持有最近一次成功 commit 的 `_head`
- 负责 commit / open / export / save-as 协议
- 自身不长期持有 `IRbfFile`

### 对象管理：`GcPool<DurableObject>`

`GcPool<DurableObject>` 仍承担三件事：

- 分配 `LocalId`
- 作为 identity map
- 进行 mark-sweep 回收

固定 slot：

- slot 0 = `ObjectMap`
- slot 1 = `SymbolTable`

### 引用安全

`Revision.EnsureCanReference(obj)` 统一校验：

- 属于当前 `Revision`
- 未 detached
- handle 仍有效

`Open(...)` 后还会执行一次全图引用校验：

- `ValidateAllReferences()`
- 通过 `AcceptChildRefVisitor(...)` 遍历 child refs
- 当前只校验 DurableObject 引用（`LocalId`）
- string / symbol 的 reconstruction 合法性已提前由 `ValidateReconstructed(...)` 处理

相关文件：

- `src/StateJournal/Revision.cs`
- `src/StateJournal/Revision.Symbol.cs`

### Commit 协议

当前 `Commit(graphRoot, targetFile)` 只有 primary commit：

```text
Phase 1: PrepareLiveObjects / DFS Mark
Phase 2: Persist user objects + SymbolTable + ObjectMap
Phase 3: Complete pending saves + Sweep + 更新 _head
```

`CommitOutcome` 当前只有：

- `PrimaryOnly`

### `ExportTo` / `SaveAs`

这两条仍然存在，且都写 full snapshot：

- `ExportTo(...)`：写到目标文件，但不修改当前 `Revision` 的 head / 对象内存态
- `SaveAs(...)`：写到目标文件，并把当前 `Revision` 内存态切到该新快照

两者的帧来源都是：

- `FrameSource.CrossFileSnapshot`

### `GraphRoot` / `SymbolTable` TailMeta

`ObjectMap` 头帧的 TailMeta 当前固定 8 字节：

- `[0..3] GraphRoot.LocalId`
- `[4..7] SymbolTable.LocalId`

当前 open 路径只接受这种新格式。

### `Open(...)`

当前 `Revision.Open(...)` 的高层流程：

```text
Load ObjectMap frame chain
-> 读取 TailMeta
-> 单独加载 SymbolTable
-> 从 SymbolTable 重建 StringPool
-> 遍历 ObjectMap 加载所有用户对象
-> Rebuild GcPool
-> Bind 所有用户对象
-> 恢复 GraphRoot
-> ValidateAllReferences()
```

当前仍采用 MVP 全量加载策略：

- open 时一次性把整个对象图载入内存

---

## Repository 层

`Repository` 与 `Revision` 的职责边界：

- `Repository`：branch 元数据、segment 布局、repo 锁、segment 文件生命周期
- `Revision`：内存对象图、head 视角、持久化协议

当前记忆点：

- commit 地址 = `CommitAddress(segmentNumber, commitTicket)`
- `Revision` 只记录自己当前绑定的 `segmentNumber`
- `Repository` 在 branch CAS 成功后，再调用 `revision.AcceptPersistedSegment(...)`

---

## 进度地图

| 模块 | 状态 | 备注 |
|:-----|:-----|:-----|
| ValueBox（Tagged-Pointer + Faces） | ✅ 已完成 | mixed string 继续 symbol-backed |
| SlotPool / GcPool / InternPool / StringPool | ✅ 已完成 | `SlotPool` 已支持 sparse value slab |
| DictChangeTracker / DequeChangeTracker | ✅ 已完成 | dict + deque 两条都已工作 |
| DurableDict（Typed / Mixed / DurObj） | ✅ 已完成 | typed string 延迟 intern 已打通 |
| DurableDeque（Typed / Mixed / DurObj） | ✅ 已完成 | 不再是占位 |
| BinaryDiffWriter / Reader | ✅ 已完成 | typed string late intern + placeholder load |
| TypeCodec / HelperRegistry / DurableFactory | ✅ 已完成 | helper 多态已扩展到 reconstruction 校验 |
| VersionChain（Write / Save / Load） | ✅ 已完成 | reconstruction 校验钩子已接入 |
| Revision Commit 主协议 | ✅ 已完成 | 已去除 compaction 主线 |
| Repository | ✅ 已完成首版 | branch / segment / CAS / SaveAs 路由 |
| NodeContainers（LeafChainStore / SkipListCore） | ✅ 已完成 | 叶链共享节点仓库 + 跳表核心 |
| DurableOrderedDict（TypedOrderedDict） | ✅ 已完成 | 基于 SkipListCore，typed only |
| typed string staging-in-ChangeTracker | 🟡 储备方案 | 尚未落地，见专项文档 |

---

## 仍需牢记的高风险边界

### 1. typed string placeholder 方案是“正式过渡层”，不是最终形态

它已经正确、可测试、可工作，但不是最终最干净的建模。

长期更正统的方向仍然是：

- `ApplyDelta` 先写 staging store 形态
- `OnLoadCompleted` 前再 materialize 成 facade `string`

### 2. Mixed 与 typed 的 string 语义绝不能混淆

typed：

- 运行时是 `string`
- commit / load bridge 才碰 `SymbolId`

mixed：

- 运行时存的就是 `ValueBox(SymbolId)`

### 3. 底层仍有 compaction 原语，不等于上层仍有 compaction 语义

新会话容易被代码残留误导：

- pool 层 compaction API 还在
- `FrameSource.Compaction` 还在
- 旧测试/旧文档中也可能还有 compaction 历史痕迹

但当前 StateJournal 主协议认知应以“**commit 已去 compaction 化**”为准。

### 4. `memory-notebook.md` 才是当前主文档

下面这些文档仍有价值，但它们主要是：

- 背景设计
- 分阶段重构时的局部方案
- 未来候选演进路径

不应覆盖本文件作为“当前主线事实地图”的角色。

---

## 文件路径速查

```text
src/StateJournal/
├── DurableObject.cs
├── DurableState.cs
├── DurableDict.Typed.cs
├── DurableDict.Mixed.cs
├── DurableDeque.Typed.cs
├── DurableDeque.Mixed.cs
├── DurableOrderedDict.cs
├── Revision.cs
├── Revision.Commit.cs
├── Revision.Symbol.cs
├── Repository.cs
├── CommitOutcome.cs
├── Internal/
│   ├── ValueBox.cs + ValueBox.*.cs
│   ├── DictChangeTracker.cs
│   ├── DequeChangeTracker.cs
│   ├── VersionChain.cs
│   ├── DurableFactory.cs
│   ├── HelperRegistry.cs
│   ├── ITypeHelper.cs
│   ├── LoadPlaceholderTracker.cs
│   ├── TypedDictImpl.cs / MixedDictImpl.cs / DurObjDictImpl.cs
│   ├── TypedDequeImpl.cs / MixedDequeImpl.cs / DurObjDequeImpl.cs
│   ├── TypedOrderedDictImpl.cs
│   ├── TypedOrderedDictFactory.cs
│   └── ...
├── Serialization/
│   ├── BinaryDiffWriter.cs
│   ├── BinaryDiffReader.cs
│   ├── TaggedValueDispatcher.cs
│   └── VarInt.cs
├── NodeContainers/
│   ├── LeafChainStore.cs
│   ├── LeafHandle.cs
│   ├── ListCore.cs
│   └── SkipListCore.cs
└── Pools/
    ├── SlotPool.cs
    ├── GcPool.cs
    ├── InternPool.cs
    ├── StringPool.cs
    ├── SlabBitmap.cs
    └── SlotHandle.cs
```

---

## 设计文档索引

| 文档 | 路径 | 说明 |
|:-----|:-----|:-----|
| 去 compaction 化与 typed string 延迟 intern | `docs/StateJournal/remove-compaction-and-late-typed-string-intern.md` | 本轮主线设计背景 |
| typed string load staging 预备方案 | `docs/StateJournal/typed-string-load-staging-reserve.md` | placeholder 方案的后续正统演进 |
| 异构存储设计决策 | `docs/StateJournal/v4/异构存储设计决策.md` | ValueBox 核心决策 |
| Tagged-Pointer 布局 | `docs/StateJournal/v4/tagged-pointer.md` | `ValueBox` 编码表 |
| 浮点相等语义 | `docs/StateJournal/v4/floating-point-equality-semantics.md` | BitExact vs NumericEquiv |
| CBOR-inspired 编码 | `docs/StateJournal/v4/tagged-scalar-cbor-inspired-encoding.md` | Tagged 标量编码 |
| archived 旧设计 | `docs/StateJournal/archived/` | 多数已过时，仅作历史参考 |
