# StateJournal ForkCommittedAsMutable PR Design

> 目标 PR：为现有 `DurableObject` 类型增加对象级 mutable fork 能力。
> 关系：本 PR 不实现 Freeze/Frozen；Freeze 作为后续独立 PR 设计，见 [`frozen-durable-object-design.md`](frozen-durable-object-design.md)。
> 状态：设计草案，用于后续打磨和实施。

---

## 1. 背景

StateJournal 当前的 `DurableObject` 都围绕“可变工作态 + 增量 diff payload”设计。实际应用中常见两类需求：

- 复用一个已持久化对象作为模板，生成一个新 `LocalId` 的可编辑副本。
- 后续 Freeze 功能中，从 readonly/frozen 对象派生一个 mutable 对象，而不是原地解冻。

如果直接复制 payload，会浪费 I/O；如果直接复用同一 `LocalId`，则破坏对象身份。更合适的做法是：

```text
source LocalId -> version ticket A
forked LocalId -> version ticket A
```

fork 后新对象拥有新的 `LocalId`，但初始内容继承 source 的版本链 head。新对象第一次被修改并 commit 时，再写入以 `A` 为 parent 的 delta/rebase。

---

## 2. 目标与非目标

### 2.1 目标

- 第一阶段为 typed/mixed dict 打通完整链路；第二阶段推广到 typed/mixed deque。
- fork 后产生同一 `Revision` 内的新对象和新 `LocalId`。
- fork 后对象初始内容与 source 的 committed state 相同。
- fork 后对象可正常修改、提交、重开。
- fork 后对象初次 commit 若没有发生内容修改，也必须登记到 `ObjectMap`，保证重开后可通过新 `LocalId` 读取。
- 第一版只要求 source 为 tracked 对象；允许 source 有未提交修改，但 fork 只复制 committed state，拒绝 working state fork。

### 2.2 非目标

- 不实现 `Freeze()` / `IsFrozen`。
- 不实现 `ForkAsImmutable()`。
- 不支持 dirty 对象的 working state clone。
- 不支持跨 `Revision` fork。
- 不把任意 C# object clone 成 DurableObject。
- 不改变现有 RBF frame payload 格式。
- 不在本 PR 实现 ordered dict / `DurableText` fork；它们依赖 `LeafChainStore`，应单独设计。

---

## 3. API 草案

### 3.1 命名：直接采用 `ForkCommittedAsMutable`

本 PR 的实际语义只复制 source 的 **committed state**，不复制 working state（详见 §4.1）。
为避免未来加入 `ForkCurrentAsMutable()` 时再做破坏性 rename，**MVP 直接采用诚实名字**
`ForkCommittedAsMutable()`，把 `ForkAsMutable` 这个简短名字预留给将来的语义扩展（例如根据
source 状态自动选择 committed/current 的高层 helper）。

> **注意：此操作受限于 StateJournal 的对象模型，属于“浅分叉 (Shallow Fork)”**。
> 如果 source 包含指向其他 `DurableObject` 的子引用（typed DurableObject value 或 mixed
> `DurableRef` / `LocalId`），fork 后的新对象将与 source 共享该子引用。若用户期望深度拷贝整个
> 对象图，必须显式设计业务级 deep fork 策略，并处理共享子图、自环和拓扑复用。API 注释中需显式
> 强调这一点，防止用户无意中通过 forked parent 修改了 template 的 child。

### 3.2 Public API

MVP 不引入 `IDurableForkable<TSelf>` 抽象接口——它在 MVP 没有现实消费者，徒增表面积。每个
facade 直接暴露具体方法即可，跨 facade 的共享路径走 `DurableObject` 上的 internal hook：

```csharp
public abstract class DurableObject {
    // 返回未绑定的同类型 clone；调用方负责后续 Bind / 状态复制。
    // 默认抛出，MVP 只让已支持的具体类型 override。
    internal virtual DurableObject ForkAsMutableCore() =>
        throw new NotSupportedException($"{GetType().Name} does not support committed fork yet.");
}

public abstract class DurableDict<TKey, TValue> : DurableDictBase<TKey>
    where TKey : notnull
    where TValue : notnull {
    public DurableDict<TKey, TValue> ForkCommittedAsMutable();
}

public abstract class DurableDict<TKey> : DurableDictBase<TKey>
    where TKey : notnull {
    public DurableDict<TKey> ForkCommittedAsMutable();
}
```

各 facade 的 public 方法负责 typed cast + 委托 `Revision.ForkCommittedAsMutable<TFork>(source)`
完成校验、绑定、pending registration 登记。

后续可覆盖：

- `DurableDeque<T>` / `DurableDeque`
- `DurableOrderedDict<TKey, TValue>` / `DurableOrderedDict<TKey>`
- `DurableText`

MVP 范围：

```text
PR1a: DurableDict<TKey, TValue> / DurableDict<TKey>
PR1b: DurableDeque<T> / DurableDeque
Future: ordered dict / DurableText
```

如未来确实出现“对任意 forkable 走泛型 API”的需求，再补 `IDurableForkable<TSelf>` 接口；届时
facade 的具体方法已经存在，加接口是非破坏性变更。

### 3.3 调用示例

```csharp
var template = (DurableDict<string>)rev.GraphRoot!;
var draft = template.ForkCommittedAsMutable();

draft.Upsert("title", "new draft");
root.Upsert("draft", draft);

repo.Commit(root).Value;
```

---

## 4. 语义规则

### 4.1 Source 前置条件

第一版只允许：

```text
source.BoundRevision != null
source.State != Detached
source.IsTracked == true
Revision.EnsureCanReference(source) succeeds
```

否则抛 `InvalidOperationException`。

原因：

- `IsTracked == false` 没有可继承的 version chain head。
- detached 对象不再属于有效对象图。

与最初草案不同，**此版本允许 `HasChanges == true` 的 source**。
原因：如果我们直接复制其 `_committed` 快照，丢弃其现有的脏修改，语义非常明确且完全安全。用户在持续编辑某个对象时如果想以它的最后保存状态为模板随时 spawn 实例，就不必强迫其先执行 `Commit()` 或 `Revert()`。

而“不支持 dirty 对象的 working state clone”依然成立（对应未来的 `ForkCurrentAsMutable()`），本 PR 并不跨界。
该放宽仅适用于能明确暴露 committed view 的实现路径；本 PR 的 dict/deque MVP 满足这一条件。ordered dict / `DurableText` 后续单独设计时也必须先证明其 committed view fork 语义成立。

**连续 fork 是允许的**：在 source 已是 fork（`ObjectMapRegistrationPending == true`）但仍
tracked 的状态下再次 fork，不应被拒绝。语义上等价于多个 LocalId 共同指向同一 inherited
`HeadTicket`，pending registration 列表里多一个条目；下一次 commit 会把所有 pending 一并登记。

### 4.2 Fork 结果

fork 结果：

```text
newObj.LocalId != source.LocalId
newObj.Revision == source.Revision
newObj.State == Clean
newObj.IsTracked == true
newObj.HeadTicket == source.HeadTicket
newObj.HasChanges == false
newObj.ObjectMapRegistrationPending == true
```

`ObjectMapRegistrationPending` 是设计概念，不一定是 public API。

`VersionChainStatus` 必须完整继承 source 的 tracked 状态：

```text
newObj.HeadTicket == source.HeadTicket
newObj.cumulativeCost == source.cumulativeCost
```

不能把 `_cumulativeCost` 重置为 0；否则 forked object 第一次修改会被当成首次保存，无法以 inherited head 为 parent 写 delta。

### 4.3 Commit 行为

现有 `PersistSnapshotCore` 中有关键跳过逻辑：

```csharp
if (!forceAll && obj.IsTracked && !obj.HasChanges) { continue; }
```

forked object 初始也是 clean/tracked，因此必须扩展提交逻辑：

```text
clean + tracked + no changes + no pending registration
  -> skip

clean + tracked + no changes + pending registration
  -> 不写 payload
  -> _objectMap.Upsert(new LocalId, inherited HeadTicket)
  -> 收集到 pendingRegistrations 列表，由 finalize 清 flag

tracked + has changes + pending registration
  -> 正常走 VersionChain.Write 写新 frame
  -> _objectMap.Upsert(new LocalId, new ticket)
  -> PendingSave.Complete() 应用版本链与状态
  -> 同时清除 pending registration flag
```

否则新 `LocalId` 不会进入 `ObjectMap`，reopen 后对象丢失。

#### 4.3.1 不能复用 `VersionChain.Write` 的 no-op 早返回路径

`VersionChain.Write` 里存在一段早返回：

```csharp
if (!obj.HasChanges && !context.ForceRebase && !context.ForceSave && obj.IsTracked) {
    return new PendingSave(obj, obj.HeadTicket, context);
}
```

这条路径的 `PendingSave.Complete()` 会调用 `OnCommitSucceeded(headTicket, context)`，而 context
此时 `WasRebase=false / EffectiveDeltifySize=0`，最终会走到
`_versionStatus.UpdateDeltified(headTicket, 0)`——**没有写任何帧，却把 `_cumulativeCost` 增加
了一个 `PerFrameOverhead`**。如果让 clean+pending 路径复用这条早返回，就会污染版本链统计，
使后续 `ShouldRebase` 决策出现偏差。

**正确做法**：clean+pending 路径根本不构造 `PendingSave`，也不调用 `VersionChain.Write`：

```csharp
foreach (var obj in liveObjects) {
    bool hasPending = obj.HasPendingObjectMapRegistration;
    if (!forceAll && obj.IsTracked && !obj.HasChanges) {
        if (hasPending) {
            _objectMap.Upsert(obj.LocalId.Value, obj.HeadTicket.Serialize());
            pendingRegistrations.Add(obj);
        }
        continue;
    }
    var writeResult = VersionChain.Write(obj, targetFile, userContext);
    if (writeResult.IsFailure) { return writeResult.Error!; }
    pendingSaves.Add(writeResult.Value);
    _objectMap.Upsert(obj.LocalId.Value, writeResult.Value.Ticket.Serialize());
    if (hasPending) { pendingRegistrations.Add(obj); }
}
```

`FinalizePrimaryCommit` 在所有写入和 ObjectMap commit 成功后：

```text
success:
  foreach PendingSave in pendingSaves: pending.Complete()           // 仅 dirty/forceAll 路径
  foreach DurableObject in pendingRegistrations:                    // clean+pending 与 dirty+pending 共用
    obj.ClearPendingObjectMapRegistration()
  Sweep()
  update _head

failure before finalize:
  pending registration remains true
  retry commit can register again
```

这样 clean+pending 路径完全绕开 `OnCommitSucceeded`，`_versionStatus` 保持原状（仍然继承 source
的 `_head` 与 `_cumulativeCost`）。

> **顺带**：`VersionChain.Write` 的这条早返回目前对 `_symbolMirror`（在 primary commit 且无
> symbol 变更时）也会触发，可能本身就是个潜在的 `_cumulativeCost` 漂移 bug。请在另一份 issue
> 中跟踪，本 PR 不做修复。

### 4.4 后续修改行为

forked object 第一次修改后：

```text
HasChanges == true
HeadTicket == inherited ticket A
```

commit 时写入新 frame：

```text
ticket B parent = A
ObjectMap[new LocalId] = B
```

source object 仍指向 `A` 或自己的后续版本，不受影响。

#### 4.4.1 帧共享的安全性

两个 LocalId 在一段时期内指向同一物理帧 A 是安全的，因为：

- 本项目当前的 RBF 文件是 **append-only**：物理帧不会被 GC 或重写；
- `Sweep()` 只回收 in-memory `GcPool<DurableObject>` 和 `StringPool` 的 slot，不会动文件；
- `VersionChain.Load` 是基于 `SizedPtr` 的内容寻址，对同一帧多次 Load 互不干扰，每次都构造
  独立的 facade 与 tracker。

#### 4.4.2 cumulativeCost 继承的影响

forked 继承 source 的 `_cumulativeCost`。如果 source 的链已经较长，forked 的首次 commit
很可能因 `ShouldRebase` 直接选择 rebase（其 parentTicket 仍写入 A 作为逻辑祖先元数据）。
这是预期行为：rebase 帧自带完整 typeCode，Load 看到 typeCode 即停止回溯，不会真去访问 A。
这条路径本质上等价于“以 source 历史的统计为基准，及时 rebase 切断与 source 的物理链耦合”。

### 4.5 与后续 Freeze PR 的交叉语义

本 PR 不实现 `IsFrozen`，但需要为后续保留语义槽位。

当 Freeze PR 引入版本 flags 后，存在一个关键场景：

```text
source: frozen, clean, tracked, head=A(flags: frozen)
fork: mutable, clean, tracked, inherited head=A, desired flags: mutable
```

如果 fork 后不修改内容、只登记 `ObjectMap[new LocalId] = A`，reopen 会按 `A` 的 flags 把 fork 加载为 frozen。后续 Freeze PR 必须扩展本设计：

```text
forked mutable desired flags != inherited head flags
  -> HasPersistenceChanges == true
  -> 首次 commit 必须写 flags-only delta/rebase
  -> 不能只做 ObjectMap registration
```

因此本 PR 的 pending registration 机制不要假设“所有 clean fork 都永远可以只登记 ObjectMap”；它只是当前无 flags 版本下的优化路径。

---

## 5. 实现方案

### 5.1 Revision 入口

新增 internal API：

```csharp
internal TFork ForkCommittedAsMutable<TFork>(TFork source)
    where TFork : DurableObject;
```

职责：

- 校验 source 前置条件（§4.1）。
- 调用 `source.ForkAsMutableCore()` 生成未绑定的同类型 clone（其内部 tracker / version status
  已按 §5.2-§5.4 复制完成）。
- 分配新 `LocalId` 并 `Bind(rev, newId, DurableState.Clean)`。
- 设置 `obj.MarkPendingObjectMapRegistration()`。

pending registration 标志放在 `DurableObject`（状态随对象走）：

```csharp
public abstract class DurableObject {
    private bool _pendingObjectMapRegistration;
    internal bool HasPendingObjectMapRegistration => _pendingObjectMapRegistration;
    internal void MarkPendingObjectMapRegistration() => _pendingObjectMapRegistration = true;
    internal void ClearPendingObjectMapRegistration() => _pendingObjectMapRegistration = false;
}
```

清理只在 `FinalizePrimaryCommit` 中显式发生（§4.3.1），对象自身不主动猜测 commit 成功。

需要覆盖两条路径：

- **clean pending**：commit 时直接 `_objectMap.Upsert(LocalId, HeadTicket)`，**不**经过
  `VersionChain.Write` 或 `OnCommitSucceeded`（避免污染 `_versionStatus`，详见 §4.3.1）。
- **dirty pending**：正常走 `VersionChain.Write` + `PendingSave.Complete()`；finalize 阶段
  额外清 pending flag。

如果 forked object 未挂入 graph root，本轮 commit 的 sweep 会通过 `DetachByGc` 把它标记
Detached。对象 detached 后不会出现在下一轮 `liveObjects` 里，pending flag 随对象一起被回收。
但测试需要显式覆盖此场景，确保不抛异常、不泄漏 ObjectMap 条目。

### 5.2 VersionChainStatus

`VersionChainStatus` 是 `struct`，赋值即值复制。无需新增工厂方法，但建议保留一个带不变量
断言的入口便于阅读：

```csharp
internal readonly VersionChainStatus ForkForNewObject() {
    Debug.Assert(IsTracked, "Caller must enforce IsTracked precondition before fork.");
    return this; // struct copy
}
```

源对象层面的前置校验由 `Revision.ForkCommittedAsMutable<TFork>` 统一负责，`VersionChainStatus`
本身只是值复制原语，不参与策略判断。

由于 `DurableDictBase` / `DurableDequeBase` 已经集中持有 `_versionStatus`，可以在这两个 base
class 加一个 `private protected void InheritVersionStatusFrom(DurableDictBase<TKey> source)`
类型的辅助方法，由各 fork core 调用。

复制语义：

- 只允许复制 tracked source。
- `_head` 和 `_cumulativeCost` 都完整继承。
- forked object 首次 delta 的 parent 是 inherited `_head`。
- forked object 的 rebase/delta 选择继续使用 inherited `_cumulativeCost` 参与 `ShouldRebase`
  （详见 §4.4.2）。

### 5.3 ChangeTracker fork

每类 tracker 增加“从 committed state 构造 mutable clean tracker”的方法。

Dict:

```csharp
public DictChangeTracker<TKey, TValue> ForkMutableFromCommitted<KHelper, VHelper>()
    where KHelper : unmanaged, ITypeHelper<TKey>
    where VHelper : unmanaged, ITypeHelper<TValue>;
```

Deque:

```csharp
public DequeChangeTracker<TValue> ForkMutableFromCommitted<VHelper>()
    where VHelper : unmanaged, ITypeHelper<TValue>;
```

语义：

- 新 tracker 拥有新的 collection 实例，不能共享 source 的 dictionary/deque 对象。
- 新 tracker 的 committed/current 内容始终等于 source committed state，忽略 source 的 current 脏状态。
- 新 tracker 的 dirty metadata 为空。
- 新 tracker 可正常后续修改和 commit。

Dict fork 应同时 fork key/value：

```csharp
var forkedKey = KHelper.ForkFrozenForNewOwner(key);
var forkedValue = VHelper.ForkFrozenForNewOwner(value);
newCommitted[forkedKey] = forkedValue;
newCurrent[forkedKey] = forkedValue;
```

即使当前 key helper 基本不持有 releasable 资源，也不要把该前提写死；tuple key 或未来 heap-backed key 会复用同一契约。

### 5.4 值所有权：不能盲目共享 NeedRelease 值

这是实现中的最高风险点。

`ITypeHelper<T>` 当前的 COW 契约只保证同一 tracker 内 committed/current 可共享 frozen 值。fork 会让两个对象同时持有同一批值；如果这些值需要手动释放，盲目共享会导致 double free 或 use-after-free。

因此需要给 helper 增加“为新 owner 克隆 frozen 值”的能力：

```csharp
internal interface ITypeHelper<T> where T : notnull {
    static virtual T? ForkFrozenForNewOwner(T? value) => value;
}
```

规则：

- `NeedRelease == false`：默认 identity。
- `ValueBoxHelper`：如果是 heap Bits64 slot，则分配新 slot 并复制数值；inline / durable ref / symbol ref 可 identity。
- tuple helper：递归 fork 每个 item。

`ValueBoxHelper` 需要真实内部 API：

```csharp
internal static ValueBox CloneFrozenForNewOwner(ValueBox value);
```

规则：

- inline scalar：identity。
- durable ref / symbol ref：identity，因为它们不拥有 heap Bits64 slot。
- heap Bits64：读取 raw bits，分配新 slot，返回 frozen box。

不能共享 frozen Bits64 slot；`ReleaseBits64Slot` 当前无条件 free slot，共享会导致 double free 或 use-after-free。

Dict fork 时：

```csharp
var forkedKey = KHelper.ForkFrozenForNewOwner(key);
var forkedValue = VHelper.ForkFrozenForNewOwner(value);
newCommitted[forkedKey] = forkedValue;
newCurrent[forkedKey] = forkedValue;
```

Deque / ordered / text 的内部 node store 也必须遵守同一原则。

### 5.5 Mixed 容器 refcount

`MixedDictImpl` / `MixedDequeImpl` 持有的 `_durableRefCount` / `_symbolRefCount` 是
**容器局部优化字段**，仅用于 `AcceptChildRefVisitor` 的快速短路。它们与池级（`StringPool` /
`GcPool`）的 refcount **不是同一回事**：

- 池级回收走的是提交期 mark-sweep，不依赖即时 refcount，因此 fork 时 **不需要**触碰池层。
- 但 forked 容器的局部计数必须正确反映自己持有的 ValueBox 内容，否则后续
  `AcceptChildRefVisitor` 短路判断会出错。

fork 后必须：

- 要么 fork 时同步复制 `(_durableRefCount, _symbolRefCount)`（与 source 相同，因为
  `ForkFrozenForNewOwner` 只换 heap slot，不改 ref tag）；
- 要么 fork 后直接调用 `RecountRefs()`。

推荐第一版直接 `RecountRefs()`，避免与 future ValueBox 细节耦合。

### 5.6 OrderedDict / DurableText 延期

`DurableOrderedDict` 和 `DurableText` 不走 `DictChangeTracker` / `DequeChangeTracker` 简单路径。

它们依赖：

- `SkipListCore`
- `TextSequenceCore`
- `LeafChainStore`

fork 时必须复制 committed live nodes、重建 current/index、清空 captured/dirty tracking，并保持 `_nextAllocSequence` 大于所有 copied sequence，否则后续插入会重号。

建议单独写 LeafChainStore fork 设计后再实现。当前 PR 中如果 public facade 已暴露 `ForkCommittedAsMutable()`，这些类型应明确抛 `NotSupportedException`；更保守的做法是第一阶段不暴露该方法。

---

## 6. 测试计划

### 6.1 基础 fork

- create/commit source typed dict。
- `ForkCommittedAsMutable()`。
- forked object 有新 `LocalId`，同 `Revision`。
- forked object 初始读到与 source 相同数据。
- source/forked 都 `HasChanges == false`。

### 6.2 ObjectMap registration

- source commit 后 fork。
- 不修改 forked object，只把 forked object 挂到 root。
- commit root。
- reopen。
- 通过 root 取回 forked object，验证内容存在。

### 6.3 修改 fork 不影响 source

- source commit。
- fork mutable。
- 修改 forked。
- commit root。
- reopen 后 source 仍为旧值，forked 为新值。

### 6.4 接受 dirty source (仅读取 committed)

- source 修改后未 commit。
- `ForkCommittedAsMutable()` 成功。
- forked object 读取的数据应为 source 修改前（即上次 commit 后）的状态。
- commit root，验证两者的不同修改互不影响。

### 6.5 拒绝 transient source

- 新建但未 commit 的 source。
- `ForkCommittedAsMutable()` 抛 `InvalidOperationException`。

### 6.6 Mixed ValueBox ownership

- mixed dict 存入需要 heap Bits64 的大整数或 exact double。
- commit 后 fork。
- 修改 source 或 forked 中对应 key。
- commit/revert 多轮，不出现 double free、值错乱。

### 6.7 DurableObject references

- source 中引用 child。
- fork 后 mark/commit/reopen。
- child 可达性正确；移除 source 引用但保留 fork 引用时 child 不应被 sweep。

### 6.8 Pending registration lifecycle

- clean pending：fork 后不修改，仅挂到 root；commit 后 reopen；断言没有写新 user payload，`ObjectMap` 指向 inherited `HeadTicket`。
- dirty pending：fork 后立即修改；commit 后 pending 清除；第二次无修改 commit 不应重复登记。
- forked object 未挂入 root：commit 其他 root 后 forked object 被 sweep/detach，不留下 stale pending registration。

### 6.9 Source/fork 分离

- source commit 到 A。
- fork 继承 A。
- source 修改并 commit 到 B。
- fork 修改并 commit 到 C(parent A)。
- reopen 后 source 内容来自 B，fork 内容来自 C。

### 6.10 ExportTo / SaveAs

- `ExportTo`：fork 处于 pending 时调用 ExportTo，应正确写出 forked 的 current state 到目标
  文件（`forceAll=true` 路径）；ExportTo 不调用 `FinalizePrimaryCommit`，因此当前 revision 的
  pending registration 标志保持不变（无需特殊清理逻辑）。
- `SaveAs`：fork 处于 pending 时调用 SaveAs，由于走 `forceAll=true` 全量快照路径，forked 对象
  会被当作普通对象写一份新 rebase 帧，pending 标志通过正常 `FinalizePrimaryCommit` 路径被
  清理（与 dirty+pending 路径相同）。SaveAs 不需要“特殊清 pending”逻辑，只需确保 finalize
  阶段统一处理 `pendingRegistrations` 列表。

---

## 7. 迁移与兼容

- 不改变已有 payload 格式。
- 不改变已有 commit/open 语义。
- 新增 ObjectMap registration pending 状态只影响 forked object。
- 旧数据无需迁移。

---

## 8. 未决问题

- 是否要新增专门异常类型，例如 `ObjectForkException`（MVP 倾向于直接用 `InvalidOperationException`，与现有风格一致）。
- pending fork 上 `DiscardChanges()` 的语义。建议第一版 internal 调用直接抛 `InvalidOperationException`，避免变成 clean/tracked 但未进入 ObjectMap 的半对象。
- ordered dict / `DurableText` 的 LeafChainStore fork 能力另行设计。
- `VersionChain.Write` 的 no-op 早返回路径（§4.3.1 末尾的“顺带”提醒）是否对 `_symbolMirror` 在无 symbol 变更场景下也存在 `_cumulativeCost` 漂移，需要单独立 issue 验证（与本 PR 解耦）。

---

## 9. 推荐实施顺序

1. 为 `VersionChainStatus` 增加 `ForkForNewObject()`（带断言的值复制 helper）。
2. 为 `DurableObject` 增加 `_pendingObjectMapRegistration` 字段及 `MarkPendingObjectMapRegistration` / `ClearPendingObjectMapRegistration` / `HasPendingObjectMapRegistration`。
3. 修改 `PersistSnapshotCore`：在原 `continue` 分支里识别 clean+pending，直接 `_objectMap.Upsert` 并收集到 `pendingRegistrations` 列表；dirty+pending 走原路径并附加到同一列表。修改 `FinalizePrimaryCommit`：完成所有 `PendingSave` 后统一清 pending flag。**严禁**为 clean+pending 构造伪 `PendingSave`（§4.3.1）。
4. 为 `ITypeHelper<T>` 增加 `ForkFrozenForNewOwner`（默认 identity）。
5. 实现 `ValueBox.CloneFrozenForNewOwner` 和 `ValueBoxHelper.ForkFrozenForNewOwner`。
6. 实现 `DictChangeTracker.ForkMutableFromCommitted<KHelper, VHelper>()`。
7. 在 `DurableObject` 加默认抛 `NotSupportedException` 的 `internal virtual DurableObject ForkAsMutableCore()`；在 `Revision` 加 `internal TFork ForkCommittedAsMutable<TFork>(TFork source)`；在 typed dict 实现类 override hook，并在 facade 暴露 `ForkCommittedAsMutable()`，补测试。
8. 实现 mixed dict fork（含 `RecountRefs()`），补 ValueBox ownership 测试。
9. 推广到 typed/mixed deque。
10. 为 ordered dict / `DurableText` 单独设计 LeafChainStore fork。
