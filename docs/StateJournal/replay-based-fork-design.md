# StateJournal Replay-Based Fork Design

> 目标：为 `StateJournal` 增加一条基于 version-chain replay/load 的通用 committed-clone 路线。  
> 关系：本设计不替代现有 `DurableObject.ForkCommittedAsMutable()` 的内存快路径；它提供的是 `Repository` 侧的统一重建能力。  
> 状态：设计草案，已按当前主线代码做过一轮贴实现 dry-run 修订。

---

## 1. 背景

当前 `StateJournal` 里已经同时存在两类能力：

- 读盘重建能力
  - `VersionChain.Load(...)`
  - `VersionChain.LoadFull(...)`
  - `DurableObject.ApplyDelta(...)`
  - `DurableObject.OnLoadCompleted(...)`
- 少数类型的 committed fork 快路径
  - `DurableDict<TKey, TValue>` / `DurableDict<TKey>`
  - `DurableHashSet<T>`
  - `DurableDeque<T>` / `DurableDeque`

现有实例方法 fork 的优点是快，但它依赖每个具体类型自己暴露 committed view，并实现一套内存级 clone 逻辑。
这对 `Dict` / `HashSet` / `Deque` 没问题，但对未来更复杂的类型会提高接入成本。

因此希望再补一条统一 fallback：

```text
source DurableObject
  -> 取 source.Revision
  -> 由 Repository 提供该 Revision 所在 segment 对应的 IRbfFile
  -> 以 source.HeadTicket 为起点做 version-chain replay/load
  -> 按指定 materialization mode 物化 committed state
  -> 回绑到同一个 Revision，分配新的 LocalId
```

这条路线的价值是：

- 不耦合具体类型的 committed-tracker clone 细节
- committed frozen source 也能统一 replay 成 mutable / frozen 物化结果
- 后续新类型只要 load 路是完整的，就天然更容易接入

---

## 2. 先澄清一个关键点

这次复核后，最重要的修正是：

> **本设计里没有第二个 “target revision”。**

Replay-based clone 的语义始终是：

- 从 `source` 身上拿到它所属的唯一 `Revision`
- 在这个 `Revision` 的对象图语境里 replay `source.HeadTicket`
- 把 replay 出来的对象作为一个新身份重新绑定回这个同一个 `Revision`

也就是说，这不是“把对象 replay 到另一个 revision”，而是：

> **在 source 所属 revision 内，基于磁盘上的 committed snapshot，重新物化出一个新的 shallow clone 身份。**

这和你指出的是一致的。之前把它描述成 “source.Revision == targetRevision” 容易制造不存在的第二个 revision 概念，应该删掉。

---

## 3. 现有代码事实

### 3.1 `Revision.Load(LocalId)` 不是 replay

当前 `Revision.Load(LocalId)` 的语义是从已加载 pool 里取对象，不会重新读盘。

所以 replay-based fork 不应该借 `Revision.Load(LocalId)` 扩义实现，而应该新增独立 helper。

### 3.2 真正的 replay 入口是 `VersionChain.Load` / `LoadFull`

当前读盘重建链路是：

```text
VersionChain.LoadFull(...)
  -> DurableFactory.TryCreate(...)
  -> 多帧 ApplyDelta(...)
  -> ValidateReconstructed(...)
  -> OnLoadCompleted(versionTicket)
```

其中“最终按 mutable 还是 frozen 形态物化 current view”发生在 `OnLoadCompleted(...)` 阶段，不在 `ApplyDelta(...)` 阶段。

### 3.3 `Repository` 才知道该用哪个 `IRbfFile`

当前 segment/file 打开能力在 `Repository`：

- 活跃 segment：`_segments.ActiveFile`
- 历史 segment：`_segments.OpenHistoricalFile(segmentNumber)`

`DurableObject` 自己拿不到 `IRbfFile`，所以 replay-based 能力不适合塞成 `DurableObject` 上的默认实现。

### 3.4 `Revision.Open(...)` 已经展示了正确的 replay 风格

`Revision.Open(CommitTicket id, IRbfFile file, uint segmentNumber)` 的现有做法就是一个很好的参考：

- `Repository` 负责拿到正确的 file
- `Revision` 负责在自己的语境中用 `VersionChain.Load(...)` / `LoadFull(...)` 重建对象
- 之后再做 bind、pool rebuild、引用校验等 revision 级收尾

Replay-based committed clone 只是把这个模式缩小到“加载单个 source object”，而不是全量打开整棵 revision。

---

## 4. 总体方案

### 4.1 明确保留两条不同能力

建议长期并存两条路线：

#### A. 对象实例上的快路径 fork

```csharp
source.ForkCommittedAsMutable()
```

语义：

- 同一已打开 `Revision` 内
- 基于内存中的 committed view
- 由具体类型自行实现高效 clone

特点：

- 快
- 依赖具体类型内存结构
- 是优化路径，不是统一 fallback

#### B. `Repository` 侧 replay-based committed clone

```csharp
repo.ReplayCommitted(source, LoadMaterializationMode.ForceMutable)
repo.ReplayCommitted(source, LoadMaterializationMode.ForceFrozen)
```

语义：

- 仍然只作用于 `source.Revision`
- `Repository` 仅负责提供该 revision 所在 segment 的 `IRbfFile`
- `Revision` 负责在自己的对象图语境里 replay `source.HeadTicket`
- 最终返回一个绑定到同一 `Revision`、但带有新 `LocalId` 的对象

特点：

- 慢于内存快路径
- 与具体类型 committed clone 逻辑解耦
- 适合作为统一 fallback

### 4.2 为什么这条能力应该挂在 `Repository`

不是因为它“操作另一个 revision”，而是因为：

- file/segment 的选择权在 `Repository`
- 活跃文件和历史文件的打开策略在 `Repository`
- `DurableObject` 自己不该知道 recent/archive 布局

所以比较自然的分层是：

```text
Repository
  -> 选择 file
  -> 调用 Revision 内部 replay helper
Revision
  -> 调 VersionChain.Load/LoadFull
  -> bind 新 LocalId
  -> 标记 pending object-map registration / mutability dirty
```

---

## 5. Dry-Run：按当前代码走一遍

这部分专门回答“从 source 拿 Revision，从 Revision 拿 repo 管理的 `IRbfFile`，再 `VersionChain.Load` 是否就差不多完事了”。

结论是：

> **是的，主路径基本就是这样。剩下的主要工作不在 IO，而在 load 结束后的 materialization mode 和 revision 绑定收尾。**

建议的运行路径如下。

### 5.1 `Repository.ReplayCommitted(source, mode)`

入口放在 `Repository`，因为它持有 `_segments`。

第一步做基本校验：

- `source` 已绑定到某个 `Revision`
- `source` 不是 detached
- `source` 是 tracked 对象，存在有效 `HeadTicket`
- `source.Revision` 这份工作态确实由当前 `Repository` 管理

最后一条的意思不是“另一个 revision”，而是 ownership check：

- `source.Revision.BranchName` 不应为 null
- 该 branch 在当前 `_branches` 中存在
- 更严格时可要求 `ReferenceEquals(branchState.LoadedRevision, source.Revision)`

### 5.2 选择 file

在当前实现下，`Revision` 只记录 `HeadSegmentNumber`，不为每个对象单独记录 segment。

因此第一版 replay 依赖当前已存在的现实约束：

- 活跃 `Revision` 的 live object head 通常与 `revision.HeadSegmentNumber` 对齐
- 当 branch head 不在 active segment，后续写入会走 `SaveAs(...)`
- segment rotation 也会走 `SaveAs(...)`

所以 file 选择可按现有模式：

```text
if revision.HeadSegmentNumber == repo._segments.ActiveSegmentNumber
    -> 用 _segments.ActiveFile
else
    -> using var file = _segments.OpenHistoricalFile(revision.HeadSegmentNumber)
```

这和 `Repository.CreateDetachedRevisionAtAddress(...)` 的思路是一致的。

### 5.3 调 `Revision` 内部 helper

随后进入例如：

```csharp
internal TDurable ReplayCommittedCore<TDurable>(
    TDurable source,
    IRbfFile sourceFile,
    LoadMaterializationMode mode)
    where TDurable : DurableObject;
```

这里做的事情应包括：

- 取 `source.HeadTicket`
- 调 `VersionChain.Load(...)` 或 `LoadFull(...)`
- 传入当前 `Revision` 的 symbol 上下文
- 断言加载结果类型与 `TDurable` 匹配
- 为新对象分配新的 `LocalId`
- `Bind(revision, newLocalId, DurableState.Clean)`
- `MarkPendingObjectMapRegistration()`
- 根据 current flags 与 version flags 的差异设置 mutability 状态

### 5.4 为什么需要传当前 revision 的 symbol 上下文

这和 `Revision.Open(...)` 的做法并不冲突。

`Open(...)` 在“从裸文件全量打开一个 revision”时，需要先读出持久化 SymbolTable，重建 `StringPool`。

而这里不是在打开一个陌生 revision，而是在：

- 对一个已经活着的 `source.Revision` 做单对象 replay
- 该 revision 已经拥有当前对象图的 symbol / ref / pool 基础设施

因此 replay 单个对象时，合理路径就是复用 `source.Revision` 当前持有的 symbol 上下文，而不是再额外重建一份新的。

### 5.5 真正的阻碍点在 materialization mode

如果只考虑“读出 committed state”，那主链路确实很短。

真正需要新增基础设施的是：

- `Normal`
- `ForceMutable`
- `ForceFrozen`

这三个模式如何影响 `OnLoadCompleted(...)`。

尤其是：

> committed frozen source replay 成 mutable fork 时，version truth 仍然是 frozen，但 current working view 必须是 mutable。

这意味着 replay 结果可能要落在下面这个状态：

```text
VersionObjectFlags == Frozen
CurrentObjectFlags == None
State == Clean
HasChanges == false
HasMutabilityChanges == true
```

也就是“内容干净，但 mutability 与 committed truth 不同”，这和现有 fork 语义是一致的。

---

## 6. 新增抽象：LoadMaterializationMode

不建议使用 `asMutable/asFrozen` 双 bool。  
建议引入 enum：

```csharp
internal enum LoadMaterializationMode {
    Normal,
    ForceMutable,
    ForceFrozen,
}
```

语义如下。

### 6.1 `Normal`

与当前 reopen/load 保持完全一致：

- committed mutable -> mutable current
- committed frozen -> frozen current

### 6.2 `ForceMutable`

目标：

- committed flags 仍保留磁盘真相
- current 视图按 mutable 物化
- 若 committed flags 原本是 frozen，则对象应呈现为 mutability-dirty

这正是 replay-based mutable fork 需要的模式。

### 6.3 `ForceFrozen`

目标：

- committed flags 与 current 视图都按 frozen 物化
- 作为未来 frozen clone 的统一入口

即使第一版外部只急需 `ForceMutable`，设计上也建议一起留出 `ForceFrozen`，避免后续再改签名。

---

## 7. 对对象层的改动建议

当前 `VersionChain.LoadFull(...)` 最后只调用：

```csharp
result.OnLoadCompleted(versionTicket);
```

如果要支持 replay-based fork，建议把 mode 正式传到对象层，例如：

```csharp
internal abstract void OnLoadCompleted(
    SizedPtr versionTicket,
    LoadMaterializationMode mode);
```

随后在 `VersionChain.LoadFull(...)` 增加对应参数：

```csharp
internal static AteliaResult<VersionChainLoadResult> LoadFull(
    IRbfFile file,
    SizedPtr versionTicket,
    FrameUsage? expectUsage = null,
    DurableObjectKind? expectObject = null,
    StringPool? symbolPool = null,
    LoadMaterializationMode materializationMode = LoadMaterializationMode.Normal)
```

### 7.1 为什么不只在 `VersionChain` 内部分支

因为 replay 结束后的“current 视图如何从 committed 数据物化”是类型相关的：

- `DurableDictBase`
- `DurableDequeBase`
- `DurableHashSet<T>`
- 未来的 `DurableText` / `DurableOrderedDict`

真正知道如何从 reconstructed committed state 生成 mutable / frozen current 的，是具体对象自己。

### 7.2 推荐的对象层分流方式

不建议把所有模式判断都直接塞进现有 `OnLoadCompleted(...)` 的一个大 if/else。

更清晰的方案是让 base class 明确区分几条物化 hook，例如：

```csharp
private protected abstract void SyncCurrentFromCommittedCore();
private protected abstract void SyncMutableCurrentFromCommittedCore();
private protected abstract void SyncFrozenCurrentFromCommittedCore();
```

再按 mode 分发：

```text
Normal + committed mutable -> SyncCurrentFromCommittedCore()
Normal + committed frozen  -> SyncFrozenCurrentFromCommittedCore()
ForceMutable               -> SyncMutableCurrentFromCommittedCore()
ForceFrozen                -> SyncFrozenCurrentFromCommittedCore()
```

这样 reopen 语义和 replay-fork 语义更不容易互相污染。

---

## 8. API 草案

### 8.1 Repository 入口

第一版建议先做成 internal：

```csharp
public sealed partial class Repository {
    internal TDurable ReplayCommitted<TDurable>(
        TDurable source,
        LoadMaterializationMode mode)
        where TDurable : DurableObject;
}
```

也可以包装出更直观的名字：

```csharp
internal TDurable ReplayCommittedAsMutable<TDurable>(TDurable source)
internal TDurable ReplayCommittedAsFrozen<TDurable>(TDurable source)
```

但从扩展性看，统一入口加 enum 更稳。

### 8.2 Revision 内部 helper

```csharp
internal TDurable ReplayCommittedCore<TDurable>(
    TDurable source,
    IRbfFile sourceFile,
    LoadMaterializationMode mode)
    where TDurable : DurableObject;
```

这里没有“target revision”参数，因为目标语境天然就是 `source.Revision` 本身。

---

## 9. 语义结果

对 `ReplayCommitted(source, ForceMutable)`，期望结果是：

```text
newObj.LocalId != source.LocalId
newObj.Revision == source.Revision
newObj.State == Clean
newObj.IsTracked == true
newObj.HeadTicket == source.HeadTicket
newObj.HasChanges == false
newObj.HasPendingObjectMapRegistration == true
```

并且：

- 若 source 的 committed flags 是 mutable
  - `newObj.IsFrozen == false`
  - `HasMutabilityChanges == false`
- 若 source 的 committed flags 是 frozen
  - `newObj.IsFrozen == false`
  - `HasMutabilityChanges == true`

这正好对应“内容是 source 的 committed snapshot，但身份是新对象，而且 working mutability 可以与 committed truth 不同”。

---

## 10. 前置条件与边界

第一版建议明确限制：

- `source` 必须已绑定到 `Revision`
- `source` 不能是 detached
- `source` 必须是 tracked committed 对象
- `source.Revision` 必须是当前 `Repository` 已管理的 loaded revision
- 只做 shallow clone
- 不支持跨 `Revision`

这里“不支持跨 `Revision`”的意思是：

- child `LocalId`
- `DurableRef`
- mixed 容器里的 durable child refs

这些都只在 source 所属 revision 的对象图语境里有意义。

所以 replay-based clone 的合同应该明确写成：

> **它永远把结果回绑到 source.Revision，而不是搬运到另一个 revision。**

---

## 11. 与现有实例快路径 fork 的关系

建议长期保留两者并存：

- `ForkCommittedAsMutable()` 是内存快路径
- `Repository.ReplayCommitted(...)` 是统一 fallback

原因很直接：

- `Dict` / `HashSet` / `Deque` 已经有不错的快路径
- replay 路线天然更慢
- 但 replay 路线的通用性更高，对未来类型更友好

因此不应把 replay 设计成“取代快路径”，而应把它定位成：

> **一条更统一、更诚实，但性能次优的 committed snapshot 重建能力。**

---

## 12. 推荐实施顺序

### PR A：先打通 materialization mode

- 增加 `LoadMaterializationMode`
- `VersionChain.Load(...)` / `LoadFull(...)` 透传 mode
- `DurableDictBase` / `DurableDequeBase` / `DurableHashSet<T>` 的 `OnLoadCompleted(...)` 支持 mode
- 补 committed frozen -> `ForceMutable` / `ForceFrozen` 物化测试

### PR B：再加 Repository/Revision replay primitive

- `Repository.ReplayCommitted(...)`
- `Revision.ReplayCommittedCore(...)`
- 绑定新 `LocalId`
- pending object-map registration
- mutability-dirty 语义

### PR C：最后接入真实 consumer

优先候选：

- `DurableOrderedDict`
- `DurableText`

理由：

- 它们目前更需要统一 fallback
- 又比 `Dict` / `Deque` 更能验证这条路线是否真的值得长期保留

---

## 13. 风险与权衡

### 13.1 优点

- 实现统一性更强
- 对新类型更友好
- 不强迫每个类型都暴露 committed 内存 clone
- `Repository` / `Revision` / `DurableObject` 的职责边界更清晰

### 13.2 成本

- 比内存快路径更慢
- 需要扩展 load materialization 合同
- 需要补 committed frozen replay 相关测试
- 需要小心保护现有 reopen 语义不被回归

### 13.3 最大风险点

最大风险不是多一次读盘，而是：

> **把“正常 reopen”与“replay fork”两种物化语义揉进同一段对象代码后，意外破坏正常 reopen。**

因此建议：

- 明确区分 `Normal` / `ForceMutable` / `ForceFrozen`
- 让对象层显式分流
- 用现有 roundtrip / frozen 测试模板覆盖这三种模式

---

## 14. 当前建议

当前建议收敛成下面四点：

1. 保留现有 `DurableObject.ForkCommittedAsMutable()`，并把它明确定位为内存快路径优化。
2. 把 replay-based committed clone 放在 `Repository`，因为 file/segment 的控制权本来就在这里。
3. 该能力的真实语义不是“source.Revision == targetRevision”，而是“根本不存在第二个 revision；结果总是回绑到 `source.Revision`”。
4. 先把 `LoadMaterializationMode` 打通，再接 `Repository.ReplayCommitted(...)`，最后拿 `DurableOrderedDict` / `DurableText` 做第一批真实消费者。

一句话概括：

> **Replay-based fork 最合适的定位，是 `Repository` 提供 file、`Revision` 在自身语境里 replay committed snapshot、再回绑成同 revision 新身份的一条统一 fallback。**
