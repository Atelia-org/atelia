# StateJournal Frozen DurableObject PR Design

> 目标 PR：为 DurableObject 增加持久化 readonly/frozen 语义，并释放可变 tracking 资源。
> 依赖建议：先完成 [`fork-as-mutable-design.md`](fork-as-mutable-design.md) 中的 `ForkCommittedAsMutable`，使 frozen 对象的可变派生有明确路径。
> 状态：设计草案。当前建议先收敛为“VersionChain object flags + DurableDict full support”的第一阶段实施文档。

---

## 1. 背景

Agent History 中的 Entry / Message / ToolCall / ToolResult 等对象，语义上通常是：

```text
append once
read many
never mutate
```

当前只能用 `DurableDict<string>` 等可变容器表达。这能工作，但有两个问题：

- 语义上留有误用空间：旧历史 entry 仍可被随手 `Upsert/Remove`。
- 内存上保留 committed/current/dirty tracking metadata，对大量 readonly leaf 不经济。

Frozen DurableObject 的目标是把“不可再修改”变成对象级语义：

```csharp
var msg = rev.CreateDict<string>();
msg.Upsert("role", "user");
msg.Upsert("content", "hello");
msg.Freeze();

history.PushBack(msg);
repo.Commit(root).Value;
```

---

## 2. 目标与非目标

### 2.1 目标

- 为 DurableObject 增加持久化的 `IsFrozen` 语义。
- frozen 对象所有 mutating API 抛异常。
- frozen 对象 reopen 后仍然 frozen。
- tracker 进入 frozen 形态后释放支持变更所需的资源。
- frozen 对象仍可被 commit mark phase 遍历 child refs / string refs。
- frozen 对象可通过 `ForkCommittedAsMutable()` 生成新的 mutable 对象。
- 第一阶段支持 typed/mixed dict；第二阶段推广到 typed/mixed deque。
- ordered dict / `DurableText` 首版可只做 API 防误用，资源释放另行设计。

### 2.2 非目标

- 不支持原地 unfreeze/thaw。
- 不支持修改 frozen 对象。
- 不实现复杂 persistent data structure。
- 不把 frozen 对象自动压缩成 JSON/blob。
- 不在第一版中支持 dirty working state fork；这由 Fork PR 后续扩展决定。

### 2.3 推荐实施切片

为了尽快把设计落到代码，同时避免被 deque / ordered dict / text 的内部结构牵着走，建议明确切成两个阶段：

#### PR1：只把 DurableDict 做完整

- 为 VersionChain metadata 引入 resultant object flags。
- 为 `DurableObject` 增加 `IsFrozen` / `Freeze()` / mutability dirty / `HasPersistenceChanges`。
- 为 `DurableDict<TKey, TValue>` / `DurableDict<TKey>` 打通 freeze 的完整链路：
  - mutating API 防护
  - 持久化 roundtrip
  - `DictChangeTracker` frozen shape
  - frozen source 的 `ForkCommittedAsMutable()`
- `MixedDictImpl` 保持 child-ref / symbol-ref walk 正确，必要时在 freeze/load/fork 后 `RecountRefs()`。

#### PR2：推广到其他容器

- `DurableDeque<T>` / `DurableDeque`
- `DurableOrderedDict<TKey, TValue>` / `DurableOrderedDict<TKey>`
- `DurableText`

第一阶段不要一边推进 frozen，一边同时追求所有容器覆盖。对当前需求而言，`DurableDict` 才是核心受益者。

---

## 3. 命名

使用：

```text
Frozen
IsFrozen
Freeze()
```

不使用 `Freezed`。原因是 .NET 生态已有 `FrozenDictionary` / `FrozenSet`，`Frozen` 更自然。

---

## 4. API 草案

### 4.1 DurableObject

```csharp
public abstract class DurableObject {
    public bool IsFrozen { get; }

    public void Freeze();

    protected void ThrowIfFrozen();
    protected void ThrowIfDetachedOrFrozen();
}
```

也可以将 `Freeze()` 设计成 virtual/abstract：

```csharp
public abstract void Freeze();
```

**推荐取舍**：`IsFrozen` 统一放在 `DurableObject` 基类，`Freeze()` 采用 base template method，
再委托给具体类型的 internal hook。示意：

```csharp
public abstract class DurableObject {
    public bool IsFrozen { get; }

    public void Freeze();

    internal virtual void FreezeCore() =>
        throw new NotSupportedException($"{GetType().Name} does not support Freeze() yet.");
}
```

理由：

- `IsFrozen` / mutability dirty / `HasPersistenceChanges` 是对象级持久化语义，不该散落到各容器重复维护。
- 第一阶段只做 `DurableDict` 时，其他类型可由 `FreezeCore()` 显式抛 `NotSupportedException`，避免出现“flags 已持久化，但 mutating API 还没全部防住”的半完成状态。
- 未来推广到 deque / ordered dict / text 时，不需要再改 public API。

### 4.1.1 第一阶段的支持矩阵

PR1 建议采用下面的行为：

| 类型 | `Freeze()` | 资源释放 | 备注 |
|---|---|---:|---|
| `DurableDict<TKey, TValue>` | 支持 | 是 | 第一阶段完整实现 |
| `DurableDict<TKey>` | 支持 | 是 | 第一阶段完整实现 |
| `DurableDeque*` | `NotSupportedException` | 否 | 第二阶段 |
| `DurableOrderedDict*` | `NotSupportedException` | 否 | 第二阶段 |
| `DurableText` | `NotSupportedException` | 否 | 后续 |

### 4.2 异常

新增：

```csharp
public sealed class ObjectFrozenException : InvalidOperationException {
    public LocalId LocalId { get; }
}
```

所有 mutating API 在 frozen 状态下抛 `ObjectFrozenException`。

---

## 5. 状态模型

`Frozen` 不建议塞进 `DurableState` enum。理由：

- `DurableState` 描述持久化生命周期：Clean / Dirty / Detached。
- `Frozen` 描述可变性维度。
- 两者是正交状态。

推荐模型：

```text
DurableState:
  Clean
  PersistentDirty
  TransientDirty
  Detached

Mutability:
  Mutable
  Frozen
```

组合示例：

| DurableState | IsFrozen | 含义 |
|---|---:|---|
| `TransientDirty` | false | 新建可变对象 |
| `TransientDirty` | true | 新建后已冻结，尚未首次保存 |
| `Clean` | false | 已提交可变对象 |
| `Clean` | true | 已提交 readonly 对象 |
| `PersistentDirty` | true | 已提交对象发生变更后被冻结，需以 rebase 保存 frozen working state |
| `Detached` | 任意 | 终态，语义访问抛异常 |

---

## 6. 持久化语义

### 6.1 IsFrozen 必须持久化

这是 Freeze PR 的关键点。

如果 clean/tracked 对象调用 `Freeze()` 后不写新 frame，那么 reopen 会从旧 frame 加载出 mutable 对象，语义丢失。因此 `IsFrozen` 必须进入对象版本 payload 或版本元数据。

可选方案：

#### 方案 A：VersionChain metadata 增加 object flags

在 `VersionChainStatus.WriteRebase/WriteDeltify` 写入的 metadata 中增加 flags：

```text
parentTicket
cumulativeCost
objectFlags
```

其中：

```text
bit 0 = IsFrozen
```

`objectFlags` 定义为**该帧应用后的完整 resultant flags**，不是“本帧 flag diff”。Load 顺序 replay rebase -> delta -> head 时，每一帧都覆盖当前 flags。

写入顺序固定为：

```text
parentTicket
cumulativeCost
objectFlags
```

unknown bits 第一版必须 fail-fast 为 corruption/unsupported format，不默默忽略。这样后续新增 flags 时不会让旧代码误读对象语义。

Load 时 `VersionChainStatus.ApplyDelta` 读出 flags，并更新 status 内的 committed/current-version flags。`VersionChainStatus` 是“已提交版本 flags”的真源；`DurableObject` 基类持有 working flags（例如未提交的 `Freeze()`），并用 `_mutabilityDirty` 表达 working flags 是否不同于已提交 flags。容器类型不各自解析 flags。

优点：

- 所有对象类型共享。
- frozen 是对象版本元数据，不污染每个容器 payload。
- delta/rebase 都可表达 mutability 变化。
- delta 可把 flags 从 frozen 改回 mutable，支持 frozen source 的 mutable fork 新链。

缺点：

- 需要调整版本链 wire format。
- 当前早期项目无下游用户，可接受破坏性改动。

#### 方案 B：TypeCode / payload 内编码 frozen

每个对象 rebase payload 自己写 frozen bit。

缺点明显：

- deltify frame 没有 typeCode。
- 每个对象类型重复实现。
- mutability 变化难以独立表达。

不推荐。

推荐采用方案 A。

### 6.2 Freeze 后是否必须写 frame

规则：

```text
Freeze() 如果对象已经 tracked 且原本 clean：
  内容未变，但 IsFrozen 变了
  -> HasPersistenceChanges=true，下一次 commit 写 flags-only delta 或 rebase

Freeze() 如果对象 transient/untracked：
  首次 commit 写 rebase frame，flags 中 IsFrozen=true

Freeze() 如果对象 dirty：
  释放 diff metadata 后无法写 delta
  -> 下一次 commit 强制 rebase，flags 中 IsFrozen=true
```

因此对象需要一个 internal 标记：

```csharp
private bool _mutabilityDirty;
private bool _forceRebaseForFrozenSnapshot;
```

或等价状态。

dirty freeze 的事务边界：

- `_forceRebaseForFrozenSnapshot` 必须持续到 `PendingSave.Complete()`。
- 如果某个后续对象写入、ObjectMap 写入或 branch CAS 失败，标记不得清除，下一次 commit 应可重试 rebase。
- `DiscardChanges()` 对已经 frozen 且释放 diff metadata 的 dirty snapshot 第一版建议直接抛 `InvalidOperationException`，不要尝试恢复旧 mutable tracker。

建议把 `DiscardChanges()` 的规则说得更明确：

```text
clean tracked object:
  Freeze() 后若尚未 commit，可允许 DiscardChanges() 撤销这次 freeze
  （清掉 IsFrozen / mutabilityDirty，并恢复 mutable clean tracker）

dirty tracked object:
  Freeze() 后第一版不支持 DiscardChanges()
  （因为已经把当前内容固化为 frozen current，并可能释放旧 diff/tracker 元数据）

transient object:
  Freeze() 后若未 commit，第一版同样不支持 DiscardChanges()
  （理由同上：没有已提交快照可回退）
```

也就是说，`Freeze()` 不是“永远不可撤销”，但只有 **clean + tracked** 这一条路径值得为 UX 保留撤销能力。

### 6.3 VersionChain.Write 跳过条件

现有跳过逻辑：

```csharp
if (!obj.HasChanges && !context.ForceRebase
    && !context.ForceSave && obj.IsTracked) { return existing ticket; }
```

Freeze PR 后：

```text
HasChanges == content changed
HasPersistenceChanges == content changed || mutability changed || objectMapRegistrationPending
```

```csharp
internal bool HasPersistenceChanges { get; }
```

推荐保留 `HasChanges` 的现有语义：只表示内容 working state 是否不同。commit skip 改用 internal `HasPersistenceChanges`。理由：

- Fork 前置条件可以继续用 `HasChanges == false` 表示“没有 working state 内容变化”。
- UI/诊断不会把“只改 frozen flag”误报成内容变更。
- `DiscardChanges()` 语义更清楚。

`VersionChain.Write` 与 `PersistSnapshotCore` 的 clean skip 都应检查 `HasPersistenceChanges`，而不是只看 `HasChanges`。

### 6.4 对象级真源与推荐字段

第一阶段建议把“内容 dirty”和“持久化 dirty”明确拆开：

```csharp
public abstract class DurableObject {
    private bool _isFrozen;
    private bool _mutabilityDirty;
    private bool _forceRebaseForFrozenSnapshot;

    public bool IsFrozen => _isFrozen;
    public abstract bool HasChanges { get; } // 仅内容 working state

    internal bool HasPersistenceChanges =>
        HasChanges || _mutabilityDirty || HasPendingObjectMapRegistration;
}
```

规则：

- `HasChanges` 继续由 tracker 表达“内容 working state 是否不同”。
- `IsFrozen` 和 `_mutabilityDirty` 的唯一真源在 `DurableObject` 基类。
- tracker 不自行维护 `IsFrozen` 的持久化 dirty；它只负责 frozen 形态下的内容存储。

这样 `ForkCommittedAsMutable()`、commit skip、UI/诊断、以及 `DiscardChanges()` 的语义边界都会更清楚。

---

## 7. Tracker Frozen 形态

### 7.1 DictChangeTracker

当前：

```csharp
private readonly Dictionary<TKey, TValue?> _committed;
private readonly Dictionary<TKey, TValue?> _current;
private readonly BitDivision<TKey> _dirtyKeys;
```

Frozen 后目标：

```text
只保留 readonly current payload
释放 committed
释放 dirtyKeys
```

由于当前字段是 readonly 且构造后不可置空，实际实现需要先重构 tracker 存储形态，例如：

```csharp
internal struct DictChangeTracker<TKey, TValue> {
    private Dictionary<TKey, TValue?>? _committed;
    private Dictionary<TKey, TValue?> _current;
    private BitDivision<TKey>? _dirtyKeys;
    private bool _isFrozen;
}
```

或拆成状态对象：

```csharp
MutableDictState {
  committed
  current
  dirtyKeys
}

FrozenDictState {
  current
}
```

第一版建议采用 nullable fields，改动面较小。

为支持第一阶段 `DurableDict`，建议把需要的 API 也一并固定下来：

```csharp
internal struct DictChangeTracker<TKey, TValue> {
    public bool IsFrozen { get; }

    public void FreezeFromClean<VHelper>();
    public void FreezeFromCurrent<VHelper>();
    public void UnfreezeToMutableClean<VHelper>();
    public void MaterializeFrozenFromReconstructedCommitted<VHelper>();

    public DictChangeTracker<TKey, TValue> ForkMutableForNewOwner<KHelper, VHelper>();
}
```

语义建议：

- `FreezeFromClean<VHelper>()`
  - 前置条件：当前内容 clean。
  - 目标：保留 frozen `Current`，释放 `Committed` / `DirtyKeys`。
- `FreezeFromCurrent<VHelper>()`
  - 前置条件：允许 dirty / transient。
  - 目标：把当前内容整体 freeze 成新的 readonly current；之后不再保留“可增量回退”的 mutable tracker 状态。
- `UnfreezeToMutableClean<VHelper>()`
  - 仅供 `clean + tracked + freeze-but-not-committed-yet` 的 `DiscardChanges()` 使用。
  - 用 current 重建 committed，并清空 dirtyKeys。
- `MaterializeFrozenFromReconstructedCommitted<VHelper>()`
  - 仅供 load 路径使用。
  - 从 replay 后的 committed reconstructed state 直接收口成 frozen current，避免瞬时双份内存。
- `ForkMutableForNewOwner<KHelper, VHelper>()`
  - 若 tracker 仍是 mutable 形态，从 committed 拷贝。
  - 若 tracker 已是 frozen 形态，从 current 拷贝。
  - 对 `NeedRelease == true` 的值继续通过 `ForkFrozenForNewOwner` 复制所有权。

Frozen tracker 行为：

- `Current` 仍可供读取和 child-ref walk。
- `HasChanges` 仍只表示内容 dirty；frozen tracker 通常为 false。
- `HasPersistenceChanges` 来自 object-level mutability dirty / registration pending / content dirty。
- 当前主线的 `rebase vs deltify` 决策已由 `EstimatedRebaseBytes` / `EstimatedDeltifyBytes` 驱动；旧的聚合 `RebaseCount` / `DeltifyCount` 已从所有 tracker 删除。后续如有诊断需求需要重新加回，应明确标记为诊断视图，不进入 `WritePendingDiff` 决策路径。
- frozen tracker 在持久化层面的关键要求是：冻结快照应走 rebase。
- `WriteDeltify` 在 frozen dirty 状态下不应被调用；应强制 rebase。
- `Commit` 对 frozen tracker 是 no-op 或只清理 object-level dirty bit。
- ValidateReconstructed 需要能遍历 replayed committed state；不能在 freeze 后释放 committed 再校验。

第一阶段建议让 `Commit` 在 frozen tracker 上成为真正的 no-op；对象级状态清理由 `DurableObject.OnCommitSucceeded` 负责。这样 tracker 的职责会更纯粹。

### 7.1.1 Load 到 frozen dict 时避免瞬时双份内存

当前加载路径是：

```text
ApplyDelta -> committed reconstructed
OnLoadCompleted -> SyncCurrentFromCommitted()
```

如果 final flags 表示 frozen，第一阶段建议不要走“先复制出 mutable current，再立刻释放 committed”的路径，
否则会出现一次无意义的双份内存峰值。更好的做法是：

```text
ApplyDelta -> committed reconstructed
OnLoadCompleted detects IsFrozen
  -> 直接把 reconstructed committed 物化为 frozen current
  -> 不再保留 committed / dirtyKeys
```

也就是说，`DictChangeTracker` 需要提供一个“从 reconstructed committed 直接收口成 frozen current”的入口，
而不是只能先 `SyncCurrentFromCommitted()` 再 `FreezeFromClean()`。

### 7.2 DequeChangeTracker

当前保留：

```text
committed
current
committedDirtyMap
keep window metadata
```

Frozen 后只保留 current sequence。

需要重构点：

- `_committed` 目前 readonly，不可释放。
- `HasChanges` 必须识别 frozen 状态；旧的聚合 `RebaseCount` / `DeltifyCount` 已删除，无需再为 frozen 状态特殊处理。
- `WriteRebase` 可直接写 current。
- mutating API 入口先由容器 facade 拦截 frozen，tracker 也可 debug assert。
- ValidateReconstructed 同样需要 committed/current-agnostic reconstructed view。

### 7.3 OrderedDict / SkipListCore

`TypedOrderedDict` / `MixedOrderedDict` 基于 `SkipListCore` 和 `LeafChainStore`。Frozen 形态收益可能更大，但实现复杂。

第一版建议：

- 先实现 `IsFrozen` 语义和 mutating API 防护。
- 是否释放 `LeafChainStore` dirty tracking 元数据可作为第二阶段。
- 不要为了首版强行重写 skip-list 内部布局。
- `AcceptChildRefVisitor` 在 frozen 下必须仍可扫描 live current/committed view。

### 7.4 DurableText

`DurableText` 仍处草稿状态。可选策略：

- 若近期要用于 Agent History，优先实现 frozen 防误用。
- tracker 资源释放可延后。
- 文档中明确 `DurableText` frozen resource compaction 是后续任务。

### 7.5 Mixed refcount cache

Frozen shape 不能只保留 payload，还要保留或重建 mark phase 所需的引用信息。

`MixedDictImpl` / `MixedDequeImpl` 当前有：

```text
_durableRefCount
_symbolRefCount
```

用于 `AcceptChildRefVisitor` 短路。Frozen 后两种策略都可接受：

- 保留 refcount cache，并在 freeze/load/fork 后 `RecountRefs()`。
- frozen 下不走 refcount 短路，直接扫描 current payload。

第一版推荐保留 cache + debug recount；若实现 frozen shape 时复杂，则宁愿无条件扫描，也不能漏 child refs / symbol refs。

---

## 8. Mutating API 防护

所有写入口都必须先检查：

```csharp
ThrowIfDetachedOrFrozen();
```

Dict：

- `Upsert`
- `Remove`
- mixed typed view 的 `Upsert`

第一阶段只要求把 dict 系列的所有 public mutating API 防护完整补齐；其他类型因为 `Freeze()` 尚未开放，不作为 PR1 阻塞项。

Deque：

- `PushFront`
- `PushBack`
- `TrySetAt`
- `TrySetFront`
- `TrySetBack`
- `PopFront`
- `PopBack`

OrderedDict：

- `Upsert`
- `Remove`

Text：

- `Append`
- `Prepend`
- `InsertAfter`
- `InsertBefore`
- `SetContent`
- `Delete`
- `LoadBlocks`
- `LoadText`

注意：读取 API 不应抛 `ObjectFrozenException`。

---

## 9. 与 ForkCommittedAsMutable 的关系

Frozen 对象不支持原地解冻。修改路径是：

```csharp
var mutable = frozen.ForkCommittedAsMutable();
mutable.Upsert("field", "new value");
```

如果 Fork PR 已完成：

- frozen clean/tracked 对象可直接 fork mutable。
- forked mutable 对象继承 frozen source 的 version chain head。
- forked mutable 对象本身 `IsFrozen == false`。
- 如果 forked mutable 未修改内容，也必须写 flags-only delta/rebase；不能只登记 `ObjectMap` 指向 frozen source head，否则 reopen 后会变回 frozen。
- forked mutable 第一次内容修改 commit 写自己的 delta/rebase，flags 为 mutable。

如果 source 是 frozen transient/untracked，则第一版可拒绝 fork，要求先 commit。

### 9.1 Frozen DurableDict 的 fork 实现建议

这部分对 PR1 很关键，建议文档里显式钉住：

`ForkCommittedAsMutable()` 的 public 语义仍然叫“committed fork”，但对 frozen dict 而言，
其 frozen `Current` 就是“当前对象的持久化视图”。因此第一阶段可采用下列实现：

```text
source mutable tracker:
  从 committed 复制到新 mutable tracker

source frozen tracker:
  从 frozen current 复制到新 mutable tracker
```

这样做的理由：

- frozen dict 已经不再保留 mutable committed/current 双态；
- 其 current 本身就是上次持久化后希望 reopen 看到的内容；
- fork 的目标是得到一个新的 mutable owner，而不是恢复 source 的 mutable tracker。

因此，第一阶段不要把 frozen dict 的 fork 仍硬绑在“必须存在 committed 字典”这个前提上。

---

## 10. Commit / Load 生命周期

### 10.1 Freeze 调用

```text
Freeze()
  - ThrowIfDetached()
  - if already frozen: return
  - if HasChanges or !IsTracked: tracker.FreezeFromCurrent()
  - else tracker.FreezeFromClean()
  - IsFrozen = true
  - mark mutability dirty if working flags differ from committed flags
  - if content was dirty: force next save as rebase
```

### 10.2 WritePendingDiff

base class 需要考虑 object flags：

```text
rebaseSize = current payload size + TypeCode.Length
deltifySize = content delta size

if forceRebaseForFrozenSnapshot:
  write rebase
else:
  normal ShouldRebase

VersionChainStatus.Write*(..., objectFlags: IsFrozen)
```

这里应明确区分两层语义：

- 当前主线里，`normal ShouldRebase` 的输入是字节估算，而不是 `RebaseCount` / `DeltifyCount`
- object flags 带来的额外成本属于版本层共享开销，应体现在 `VersionChainStatus` 的 shared overhead 或 `deltifySize` 近似中，而不是退回到 count-based 启发式

flags-only delta 的成本不能按 0 处理。`objectFlags` 应纳入 per-frame overhead 或 deltifySize 估算，避免 rebase 策略失真。

### 10.3 OnCommitSucceeded

```text
update version status
tracker commit/frozen commit
clear mutability dirty
clear forceRebaseForFrozenSnapshot
SetState(Clean)
```

### 10.4 Load

```text
VersionChain.Load
  - replay frames
  - VersionChainStatus.ApplyDelta reads resultant objectFlags
  - ApplyDeltaCore replays content delta into committed state
  - ValidateReconstructed scans reconstructed committed state
  - OnLoadCompleted
  - if final flags IsFrozen:
      tracker.MaterializeFrozenFromReconstructedCommitted()
      object.IsFrozen = true
    else:
      normal mutable SyncCurrentFromCommitted()
```

注意：`ValidateReconstructed` 仍需在 `OnLoadCompleted` 前执行；frozen 不应破坏 typed string placeholder 和 mixed symbol 校验。当前若某些实现遍历 `_core.Current`，需要改成遍历 replayed committed state 或 tracker 暴露的 reconstructed view，否则 `_current` 为空时会漏校验。

### 10.5 SaveAs / ExportTo 对 frozen 的要求

第一阶段建议把 cross-file 语义也提前讲清楚：

- `SaveAs` 写出的 full snapshot 必须保留 `IsFrozen` flags。
- `ExportTo` 不应修改 source revision 中对象的 `_mutabilityDirty` / `_forceRebaseForFrozenSnapshot` / pending registration 状态。
- 对 frozen dict 而言，cross-file snapshot 仍应写成 rebase frame，但 flags 必须来自对象当前 `IsFrozen`，不能只继承旧 head。

---

## 11. 测试计划

### 11.1 Frozen 防写

- create dict。
- upsert 字段。
- freeze。
- 所有 mutating API 抛 `ObjectFrozenException`。
- read API 正常。

### 11.2 Frozen 首次保存

- transient object freeze 后 commit。
- reopen 后 `IsFrozen == true`。
- 内容正确。

### 11.3 Clean object freeze 也要持久化

- create/commit mutable object。
- reopen 或继续使用，调用 `Freeze()`。
- 不修改内容，commit。
- reopen 后 `IsFrozen == true`。

这是防止“freeze 语义没有写盘”的关键测试。

补一个撤销测试：

- create/commit mutable object。
- `Freeze()` 后但尚未 commit，调用 `DiscardChanges()`。
- 对象恢复 mutable clean。
- commit/reopen 后 `IsFrozen == false`。

### 11.4 Dirty object freeze 强制 rebase

- create/commit object。
- 修改字段。
- freeze。
- commit。
- reopen 后内容是修改后的内容，`IsFrozen == true`。

### 11.5 Frozen child refs

- parent frozen object 引用 child。
- commit root。
- child 不被 sweep。
- 移除其他引用后只靠 frozen parent 保留 child，仍可 reopen。

### 11.6 Fork frozen mutable

- frozen source commit。
- fork mutable。
- 修改 fork。
- commit。
- reopen 后 source 仍 frozen 且旧值，fork mutable 且新值。

补充 no-op fork 场景：

- frozen source commit。
- fork mutable。
- 不修改 fork，只挂到 root。
- commit/reopen。
- forked object 必须 `IsFrozen == false`。
- 该提交必须写 flags-only delta/rebase，不能只登记 ObjectMap 指向 frozen head。

### 11.7 Mixed ValueBox ownership

- frozen mixed dict 含 heap Bits64 值。
- fork mutable。
- 修改 fork 或 source 相关路径。
- 不发生 double free / 值错乱。

### 11.8 Wire format flags

- rebase frozen roundtrip。
- delta freeze roundtrip。
- flags-only delta roundtrip。
- mutable-from-frozen delta roundtrip。
- unknown flags fail-fast。

### 11.9 Load validation

- 构造 mixed symbol corruption / typed string placeholder 残留。
- 确认 `ValidateReconstructed` 在 sync/freeze 前能扫到 replayed committed state。

### 11.10 SaveAs / ExportTo

- full snapshot 保留 `IsFrozen`。
- cross-file reopen 后 flags 正确。
- ExportTo 不应改变 source revision 的 mutability dirty/pending 状态。

---

## 12. 实施顺序建议

1. 完成 ForkCommittedAsMutable PR。
2. 为版本链 metadata 加 resultant object flags，并更新 load/write/cost 估算。
3. 在 `DurableObject` 增加 `IsFrozen`、mutability dirty、`HasPersistenceChanges`、异常 helper。
4. 修改 commit skip 逻辑，使用 `HasPersistenceChanges`。
5. 修改 dict mutating API，添加 frozen 防护。
6. 重构 `DictChangeTracker` 支持 frozen shape、direct-load-to-frozen、以及 frozen-source fork。
7. 先实现 typed dict frozen 全链路测试。
8. 再推广到 mixed dict，并处理 refcount 与 mixed symbol walk。
9. deque / ordered dict / `DurableText` 暂不实现 `Freeze()`，保持显式 `NotSupportedException`。
10. 第二阶段再推广到 deque；ordered dict / `DurableText` 继续单独设计。

### 12.1 PR1 预计修改点

为方便后续直接开工，第一阶段大致会落到这些文件：

- `src/StateJournal/DurableObject.cs`
- `src/StateJournal/Internal/VersionChainStatus.cs`
- `src/StateJournal/Internal/VersionChain.cs`
- `src/StateJournal/Revision.Commit.cs`
- `src/StateJournal/DurableDict.Typed.cs`
- `src/StateJournal/DurableDict.Mixed.cs`
- `src/StateJournal/DurableDictBase.cs`
- `src/StateJournal/Internal/TypedDictImpl.cs`
- `src/StateJournal/Internal/MixedDictImpl.cs`
- `src/StateJournal/Internal/DurObjDictImpl.cs`
- `src/StateJournal/Internal/DictChangeTracker.cs`
- 以及相应测试文件

如果 PR1 期间发现必须修改 deque / ordered dict 的公共 facade，只应限于“显式拒绝 Freeze()”，不要把完整实现偷偷扩进去。

---

## 13. 未决问题

- public 是否暴露 `HasPersistenceChanges`；建议仅 internal。
- object flags 是否只包含 `IsFrozen`，或预留更多位。
- frozen dirty 对象是否总是强制 rebase；第一版建议是，并且标记持续到 `PendingSave.Complete()`。
- frozen tracker 是否要保留 committed payload 以便未来 debug/diff；第一版建议释放。
- `Freeze()` 是否允许在 object 未挂到 root 时调用；建议允许，commit 可达性仍由 root 决定。
- `DurableText` 是否在首版只做防写，不做资源释放。
- clean tracked object 在 freeze-but-not-yet-committed 状态下，`DiscardChanges()` 是否允许撤销 freeze；本文建议允许。

---

## 14. 和 JSON leaf 路线的关系

Frozen DurableObject 不排斥未来的 `DurableJson` / `DurableBlob`。

两者定位不同：

- Frozen：让现有 durable 容器具备 readonly 语义和较低内存形态。
- Json/Blob leaf：为大量 opaque immutable payload 提供更紧凑专用表示。

Agent History 可以先用 frozen mixed dict 表达结构化 entry；若后续发现大量 entry 只是 opaque JSON，再补专用 leaf。
