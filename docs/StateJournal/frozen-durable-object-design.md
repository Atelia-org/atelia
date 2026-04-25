# StateJournal Frozen DurableObject PR Design

> 目标 PR：为 DurableObject 增加持久化 readonly/frozen 语义，并释放可变 tracking 资源。
> 依赖建议：先完成 [`fork-as-mutable-design.md`](fork-as-mutable-design.md) 中的 `ForkCommittedAsMutable`，使 frozen 对象的可变派生有明确路径。
> 状态：设计草案，用于后续打磨和实施。

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
    public abstract bool IsFrozen { get; }

    public void Freeze();

    protected void ThrowIfFrozen();
    protected void ThrowIfDetachedOrFrozen();
}
```

也可以将 `Freeze()` 设计成 virtual/abstract：

```csharp
public abstract void Freeze();
```

具体取舍取决于 frozen state 是否统一放在 `DurableObject` 基类。

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

Load 时 `VersionChainStatus.ApplyDelta` 读出 flags，并更新 status 内的当前 flags。`VersionChainStatus` 应成为 object flags 的唯一真源；容器类型不各自解析 flags。

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

Frozen tracker 行为：

- `Current` 仍可供读取和 child-ref walk。
- `HasChanges` 仍只表示内容 dirty；frozen tracker 通常为 false。
- `HasPersistenceChanges` 来自 object-level mutability dirty / registration pending / content dirty。
- `RebaseCount` 仍等于 current count。
- `DeltifyCount` 为 0。
- `WriteDeltify` 在 frozen dirty 状态下不应被调用；应强制 rebase。
- `Commit` 对 frozen tracker 是 no-op 或只清理 object-level dirty bit。
- ValidateReconstructed 需要能遍历 replayed committed state；不能在 freeze 后释放 committed 再校验。

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
- `HasChanges` / `RebaseCount` / `DeltifyCount` 需识别 frozen 状态。
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
  - mark mutability dirty
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
      tracker.SyncCurrentFromCommitted()
      tracker.FreezeFromClean()
      object.IsFrozen = true
    else:
      normal mutable SyncCurrentFromCommitted()
```

注意：`ValidateReconstructed` 仍需在 `OnLoadCompleted` 前执行；frozen 不应破坏 typed string placeholder 和 mixed symbol 校验。当前若某些实现遍历 `_core.Current`，需要改成遍历 replayed committed state 或 tracker 暴露的 reconstructed view，否则 `_current` 为空时会漏校验。

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
6. 重构 `DictChangeTracker` 支持 frozen shape 和 reconstructed-state validation。
7. 实现 typed dict frozen 全链路测试。
8. 推广到 mixed dict，并处理 refcount。
9. 推广到 deque。
10. ordered dict / `DurableText` 先做防误用，资源释放另行设计。

---

## 13. 未决问题

- public 是否暴露 `HasPersistenceChanges`；建议仅 internal。
- object flags 是否只包含 `IsFrozen`，或预留更多位。
- frozen dirty 对象是否总是强制 rebase；第一版建议是，并且标记持续到 `PendingSave.Complete()`。
- frozen tracker 是否要保留 committed payload 以便未来 debug/diff；第一版建议释放。
- `Freeze()` 是否允许在 object 未挂到 root 时调用；建议允许，commit 可达性仍由 root 决定。
- `DurableText` 是否在首版只做防写，不做资源释放。

---

## 14. 和 JSON leaf 路线的关系

Frozen DurableObject 不排斥未来的 `DurableJson` / `DurableBlob`。

两者定位不同：

- Frozen：让现有 durable 容器具备 readonly 语义和较低内存形态。
- Json/Blob leaf：为大量 opaque immutable payload 提供更紧凑专用表示。

Agent History 可以先用 frozen mixed dict 表达结构化 entry；若后续发现大量 entry 只是 opaque JSON，再补专用 leaf。
