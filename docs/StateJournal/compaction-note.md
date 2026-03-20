# Revision Compaction 专项笔记

> 日期：2026-03-16
> 状态：已落地当前方案（Primary Commit + Compaction Follow-up）

> 2026-03-18 补记：`ExportTo` / `SaveAs` 已落地为跨文件完整快照路径。
> 这两条路径写出的帧统一标记为 `FrameSource.CrossFileSnapshot`，用于和常规 `PrimaryCommit` / `Compaction` 区分；
> 同时保留逻辑祖先 `parentTicket` 作为跨文件组织信息。该语义不影响当前读取，因为头帧本身是 rebase。

## 0. 当前进展（2026-03-16）

已落地：

- `Revision.Commit` 已升级为“两段提交”：
  - A) Primary Commit（三阶段）；
  - B) Compaction Apply（条件触发）+ Follow-up Persist。
- Compaction apply 已接入事务框架：
  - 通过 `GcPool.CompactWithUndo(...)` 获取 `UndoToken`；
  - apply 后执行引用一致性校验；
  - apply / rollback 的内部错误按 bug 处理，直接 fail-fast。
- 错误语义已细分：
  - `SJ.Compaction.FollowupPersistFailed`

已补齐：

- `GcPool.RollbackCompaction(...)` 已实现：
  - 通过 `CompactionUndoToken(Records)` 按 LIFO 顺序执行 `UndoMoveSlot(...)`，精确恢复 slot 布局与 generation。
  - 尾部容量收缩不再在 `CompactWithUndo` 内立即发生，而是在 follow-up persist 成功后才最终提交。
- follow-up persist 失败时会执行内存回滚：
  - 恢复 pool slot 布局；
  - 恢复对象 `LocalId`；
  - 丢弃 `ObjectMap` 与引用重写产生的工作态变更。
- 回滚时不仅恢复 pool，还会：
  - 将被移动对象 `Rebind` 回原 `LocalId`；
  - `DiscardChanges()` 回滚 `ObjectMap` 与引用重写造成的脏状态。

## 1. 问题陈述

### 1.1 什么是 LocalId 碎片化？

`GcPool<DurableObject>` 的 `Store()` 从 `SlotPool` 分配 slot，返回 `SlotHandle`（24-bit index + 8-bit generation）。`LocalId.Value == SlotHandle.Packed`，作为对象在 Revision 内的持久标识。

对象被 GC Sweep 回收后，其 slot 被 Free，留下**空洞（hole）**。后续 `Store()` 会优先复用空洞（SlotPool 的 freeBitmap FindFirstOne），但 generation 会递增（ABA 防护）。结果是：

- 长期运行后 **max index 单调递增**（即使活跃对象数稳定），浪费 pool 内存
- ObjectMap 的 key 空间稀疏化
- SlotPool 内部 slab 无法 shrink（尾部 slab 不全空）

### 1.2 为什么关心这个问题？

StateJournal 的目标用户是能**连续存活几十年**（中间可重启）的 LLM Agent 实例。即使单次 Commit 只创建/回收少量对象，几十年的累积也会让 index 空间碎片化严重。Compaction 能把所有活跃对象"压紧"到低位连续 slot 中，允许尾部空 slab 被回收。

### 1.3 现有基础设施

`SlabBitmap.CompactionEnumerator` 已就绪——Two-Finger 算法，正序找 set bit（空洞，freeBitmap 中 bit=1）与逆序找 clear bit（占用），输出 `(holeIndex, dataIndex)` 移动对，直到两游标相遇。不修改 bitmap，零分配。

```
SlotPool.FreeBitmap → CompactionEnumerator
  foreach (hole, data) in moves:
    move slot[data] → slot[hole]
```

## 2. 核心挑战：LocalId 重映射

Compaction 的本质是**移动 slot**——把高位占用的 slot 值移到低位空洞中。移动后 `SlotHandle` 改变（index 变了、新位置的 generation 也不同），进而 `LocalId.Value` 改变。所有**引用**该对象的地方都需要同步更新：

| 引用持有者 | 存储形式 | 更新方式 |
|:-----------|:---------|:---------|
| `DurableObject.LocalId` | `LocalId` (property, private set) | 需要新增 internal `Rebind` 方法 |
| `ObjectMap` | `Dictionary<uint, ulong>` key | 清空重建，或逐条 Remove+Reinsert |
| `DurObjDictImpl._core.Current` | `Dictionary<TKey, LocalId>` value | 遍历 + 更新 LocalId |
| `MixedDictImpl._core.Current` | `Dictionary<TKey, ValueBox>` value | 遍历 + 识别 DurableRef + 更新 ValueBox |
| `TypedDictImpl` / Deque 占位实现 | 无子引用 | 无需更新 |

### 2.1 引用重写的代价

引用重写是 O(所有存活对象的总引用数)——需要遍历每个 DurObjDict/MixedDict 的所有 value，检查是否指向被移动的对象。

关键问题：**被移动的对象可能很少，但需要扫描所有对象的所有引用来找到它们**。除非维护反向索引，否则无法避免全图扫描。

### 2.2 对落盘 delta 的影响

VersionChain 采用增量序列化（BinaryDiff）：只写变更部分。如果 Compaction 改写 LocalId：

- 每个包含被移动子对象引用的 DurObjDict/MixedDict → key 不变但 value 变了 → 产生 diff
- 被移动对象自身的 LocalId 变了 → 不直接影响 diff（LocalId 不序列化到 payload），但其**父对象**的 diff 会增大

即使只移动 1 个对象，如果它被 10 个 dict 引用，就产生 10 个额外 diff 帧。

**缓解因素**：`DictChangeTracker` 的变更追踪基于内容等价比较（非 touched 标记），即使 Remove 再 Upsert 回相同值也不产生 diff。因此 ObjectMap 的逐条更新不会导致未变化的 entry 被误标脏。渐进压缩（每次仅移动少量对象）可进一步控制 diff 增量。

## 3. 设计空间

### 3.1 方案 A：Full Compaction（每次 Sweep 后）

```
FinalizeCommit 尾声：
  Sweep → 得到空洞分布
  CompactionEnumerator → 移动计划
  执行移动 + 全图引用重写
  对象标脏 → 下次 Commit 自然写盘
```

- 优点：实现简单，index 空间始终紧凑
- 缺点：每次 Commit 都可能触发大量引用重写和额外 diff

### 3.2 方案 B：Deferred Compaction（独立操作）

Compaction 不在 Commit 内执行，而是作为独立操作（如 `Revision.Compact()`）：

```
Compact():
  计算移动计划
  执行移动 + 引用重写
  标记受影响对象为脏
  // 不自动 Commit——由调用者决定何时 Commit
```

- 调用者可选择在低负载时执行，或每 N 次 Commit 后执行一次
- 缺点：增加了 API 表面，调用者需要感知碎片化

### 3.3 方案 C：Incremental Compaction（渐进压缩）✅ 倾向方案

每次 Commit 仅在碎片率超过阈值时移动有限比例的对象，跨多次 Commit 逐步消除碎片：

```
FinalizeCommit 尾声（Sweep 之后）：
  if liveCount ≥ minThreshold && fragmentationRatio > triggerRatio:
    CompactionEnumerator → 最多 ceil(liveCount × compactRatio) 对移动
    执行移动 + 遍历 liveObjects 重写引用
```

当前实现的碎片率度量基于 pool 容量：`fragmentationRatio = (capacity - liveCount) / capacity`

- "遇强则强"：压缩量随存活对象数和碎片率自适应
- 每次 diff 增量可控
- 碎片在多次 Commit 中逐步收敛
- 小阈值（如 minThreshold=64）确保单元测试也能覆盖

### 3.4 方案 D：Epoch-Based Compaction（纪元压缩）

维护一个"compact 版本号"。Compaction 时不修改任何现有对象的引用，而是建立从旧 LocalId 到新 LocalId 的重映射表（可持久化）。后续读取时自动翻译。

- 类似 LSM-tree 的 compaction 思路
- 优点：零引用重写、diff 不增大
- 缺点：引入全局重映射表，读路径性能下降，实现复杂度高

## 4. 基础设施落实情况（方案 C）

### 4.1 `DurableObject.Rebind(LocalId newId)`

```csharp
// internal 方法，仅允许 Revision 的 Compaction 流程调用
internal void Rebind(LocalId newId) {
    Debug.Assert(!IsDetached);
    LocalId = newId;
}
```

状态：已实现。`Revision` compaction apply 会在 slot 移动后调用 `Rebind` 修正对象自身 `LocalId`。

### 4.2 `IChildRefRewriter` 接口

与只读的 `IChildRefVisitor` 对称，但能**修改**子引用：

```csharp
internal interface IChildRefRewriter {
    LocalId Rewrite(LocalId oldId);
}
```

各 `DurableObject` 实现类需要新增：

```csharp
internal abstract void AcceptChildRefRewrite<TRewriter>(ref TRewriter rewriter)
    where TRewriter : IChildRefRewriter;
```

状态：已实现。
实现概况：

- `DurObjDictImpl`：遍历 `_core.Current` dictionary values，对每个非空 LocalId 调 `Rewrite`
- `MixedDictImpl`：遍历 `_core.Current` dictionary values，识别 `BoxLzc.DurableRef` 的 ValueBox，提取 LocalId → Rewrite → 构建新 ValueBox 写回
- `TypedDictImpl` / Deque 占位：空实现

### 4.2.1 MixedDictImpl 的 DurableRef 计数优化

状态：已实现。

当前 `MixedDictImpl` 内部维护 `_durableRefCount`：

- `Upsert` / `Remove` 对 DurableRef 数量做差量维护
- `DiscardChanges` / load 完成后的 bulk 状态同步会重算计数
- `AcceptChildRefVisitor` / `AcceptChildRefRewrite` 在 `_durableRefCount == 0` 时直接 O(1) 返回

这让“绝大多数只是标量值的 MixedDict”可以在 compaction 重写与校验阶段被快速跳过。

### 4.3 `SlotPool/GcPool` 层移动与回滚原语

SlotPool 已补齐可逆 move 原语：

```csharp
internal readonly record struct MoveRecord(
    int FromIndex, int ToIndex,
    byte FromGenBefore, byte ToGenBefore
);

internal MoveRecord MoveSlotRecorded(int fromIndex, int toIndex);
internal void UndoMoveSlot(MoveRecord record);
internal void EnsureCapacity(int minCapacity);
```

`GcPool` 层当前新增的核心接口：

```csharp
internal CompactionUndoToken CompactWithUndo(int maxMoves);
internal void RollbackCompaction(CompactionUndoToken undoToken);
```

状态：

- `MoveSlotRecorded` / `UndoMoveSlot`：已实现并用于 apply / rollback。
- `RollbackCompaction`：已实现，在尾部容量裁剪前逆序撤销每条 move。
- `TrimExcessCapacity`：已实现，可独立回收尾部空容量；当前由 follow-up persist 成功路径调用。
- `CompactWithUndo`：已以**强异常安全**为目标实现。若在返回 `CompactionUndoToken` 前内部失败，会先恢复 pool 状态，再重新抛出异常（fail-fast）。

### 4.4 ObjectMap 更新

Compaction 改变了 key（LocalId.Value）。对每个移动的 (oldKey → newKey)，执行 Remove(oldKey) + Upsert(newKey, sameTicket)。

**注**：DictChangeTracker 基于内容等价追踪变更，即使极端情况下对 ObjectMap 做全删再逐条 Upsert，也只有真正变化的 entry 产生 diff。因此无需担心 ObjectMap 重建导致全量写帧。

## 5. generation 的处理

`SlotHandle` 编码了 8-bit generation。`MoveSlot` 在目标位置执行 `++generation`，语义等价于"释放旧占用者 + 在此位置重新分配"：

- 确保所有指向目标位置旧占用者的 stale handle 全部失效
- Compact 统一 Rebind 所有被移动对象即可
- 源位置 Free 时 generation 也自增（标准 Free 行为），使指向源的旧 handle 同样失效

## 6. 执行时机与流程草案

采用**渐进压缩**：Primary Commit 成功并更新 `_head` 之后，条件触发一次内存 compaction，再追加一次 follow-up 持久化提交；若 follow-up persist 因外部因素失败，则回滚到 primary commit 对应的内存态。若 compaction apply / rollback 自身出错，则按 bug 直接 fail-fast。

### 6.0 与跨文件快照的边界

- `ExportTo` / `SaveAs` 不走 compaction follow-up 协议，它们复用的是 primary persist 骨架 + full rebase 写入
- 这两条路径写出的帧统一使用 `FrameSource.CrossFileSnapshot`
- `SaveAs` 成功后当前 `Revision` 会切换到新文件；后续普通 `Commit` 重新写回 `FrameSource.PrimaryCommit`
- `CrossFileSnapshot` 的目的不是改变读取协议，而是把“这是一次跨文件完整快照”明确写进帧元数据，供未来更高层的文件组织/管理逻辑使用

### 6.1 已落地流程：Primary Commit + Compaction Follow-up

```
Commit(graphRoot):
  A) Primary Commit
    Phase 1: WalkAndMark → liveObjects
    Phase 2: Persist (写盘)
    Phase 3: Finalize
      3a. PendingSave.Complete()
      3b. Sweep (不可达对象 DetachByGc + Free)
      3c. 更新 _head（primary commit 已成功）
  B) Compaction Apply（条件触发）
    b1. RevisionCompactionSession.TryApply(...)
        前置条件: liveCount ≥ minThreshold && fragmentationRatio > triggerRatio
        ├─ GcPool.CompactWithUndo(...) → UndoToken
        ├─ SlotPool.MoveSlotRecorded (generation+1) × K（由 GcPool 内部执行）
        ├─ Rebind 被移动对象
        ├─ ObjectMap 逐条更新被移动的 key（只标脏真正变化的 entry）
        ├─ 基于 primary 的 liveObjects 做引用重写
        └─ ValidateCompactionApply(...)
            - Strict：全量校验 liveObjects
            - HotPath：只校验 touchedObjects + moved keys + ObjectMap/pool 对齐
  C) Follow-up Persist（仅在确实 compact 后）
    ├─ 复用 primary 的 liveObjects 持久化 compaction 脏变更
    └─ 若因外部因素失败：RollbackCompactionChanges 恢复内存态，`Head` 保持 primary commit
```

**当前错误语义**：

- Primary Commit 中对象图状态、文件句柄、RBF I/O、宿主环境等外部因素，返回 `AteliaError`
- Compaction follow-up persist 的外部失败，返回 `CommitOutcome.PrimaryCommittedCompactionRolledBack`
- Compaction apply / rollback 中违反内部不变量的错误，直接 fail-fast，不折叠为 `AteliaError`

Compact 在 Sweep 之后执行的好处：
- 只有真正可达的尾部对象会被移动，不浪费
- FreeBitmap 已反映 Sweep 后的空洞分布，移动计划最准确
- 被改动的引用自然变脏 → 同一次外层 Commit 的 follow-up persist 写盘

## 7. 性能估算

对于几百到几千个活跃对象的 Agent 场景：

| 操作 | 复杂度 | 预期耗时 |
|:-----|:-------|:---------|
| CompactionEnumerator（K 步） | O(K) + bitmap TZCNT | 亚微秒 |
| SlotPool.MoveSlotRecorded × K | O(K) 数组操作 | 亚微秒 |
| Rebind × K | O(K) | 亚微秒 |
| AcceptChildRefRewrite（全图） | O(总引用数) | 微秒级（几千引用） |
| ObjectMap 逐条更新 × K | O(K) dict 操作 | 亚微秒 |

瓶颈在全图引用重写的 O(总引用数) 遍历。但对于千级对象图，总引用数通常也在千级，遍历成本微不足道。

## 8. 开放问题

1. **压缩参数调优**：`minThreshold`、`triggerRatio`、`compactRatio` 的初始值需要实验。建议：
   - `minThreshold = 64`（足够小，单元测试可覆盖）
   - `triggerRatio = 0.25`（25% 以上碎片率才触发）
   - `compactRatio = 0.05`（每次最多移动 5% 的存活对象）

2. **首次 Commit 特例**：首次 Commit 时无碎片，Compact 无意义。碎片率计算自然短路。

3. **与未来 Lazy Loading 的交互**：当前 MVP 全量加载整个对象图。如果未来引入 lazy loading，Compact 的引用重写需要触发 lazy load——是否能接受？

4. **回滚覆盖面继续扩展**：当前 dict 体系路径已有充分测试覆盖；若后续补上 `DurableDeque` 持久化/变更追踪，需要同步验证 compaction 回滚与 `DiscardChanges()` 语义。

5. **方案 D（Epoch 重映射表）的长期价值**：如果未来对象规模大到全图扫描不可接受，可能需要方案 D。但那是遥远的未来问题。

## 9. 已实施的关键收敛

### 9.1 单份 `liveObjects` 贯穿三阶段

当前 `Commit` 已显式拆成：

1. `RunPrimaryCommit(...)`
2. `RevisionCompactionSession.TryApply(...)`
3. `PersistCompactionFollowup(...)`

`liveObjects` 由 primary 阶段产出，并被 compaction apply 与 follow-up persist 复用，不再重复走第二次 `WalkAndMark/Sweep`。这也把协议边界压实为：

- Primary Commit 负责确定可达集并 durable 化 primary snapshot
- Compaction Apply 只在同一可达集内做纯内存重排
- Follow-up Persist 只 durable 化 compaction 造成的脏变更

### 9.2 `MixedDictImpl._durableRefCount` 快路径

当前 `MixedDictImpl` 已通过 `_durableRefCount` 支持快速跳过：

- 纯标量 mixed dict 的 visitor / rewrite 入口 O(1) 返回
- bulk 状态同步后会重算计数，避免长期漂移

这属于低复杂度、明确收益的热路径优化。

### 9.3 校验模式分层

当前 `Revision.ValidateCompactionApply(...)` 已分层：

- `Strict`：全量校验 `liveObjects`
- `HotPath`：只校验 `touchedObjects`、moved old keys 已移除，以及 touched objects 的 pool/ObjectMap/子引用一致性

默认策略：

- `DEBUG` / 测试环境：默认 `Strict`
- 发布默认：`HotPath`
- 环境变量可覆盖：`ATELIA_SJ_COMPACTION_VALIDATE=STRICT|HOT|HOTPATH`

### 9.4 单次 no-op 判定

当前 `Revision.Commit` 已直接调用 `RevisionCompactionSession.TryApply(...)`，把旧的 `TryCreate + Apply(NotCompacted)` 双重 no-op 路径合并为单次判定。协议表达更自然，`Commit` 侧只需回答一次“这次是否真的 compact 了”。

### 9.5 Benchmark 三层结构

当前 benchmark 已建成三层：

- 端到端：`CompactionCommitBenchmarks`
  - 当前已改为通过 `IterationSetup` 预先构造 pending-compaction scenario，benchmark 方法本体只测目标那次 `Revision.Commit(...)`
- Primary Commit 分阶段：`PrimaryCommitStageBenchmarks`
  - `PrimaryCommit_Only`
  - `WalkAndMark_Only`
  - `Persist_Only`
  - `Finalize_Only`
- Compaction 分阶段：`CompactionStageBenchmarks`
  - `CompactWithUndo_Only`
  - `ReferenceRewrite_Only`
  - `Validate_Only`
  - `FollowupPersist_Only`

当前已覆盖的主要场景：

- `TypedLeafObjects_NoChildRefs`
- `DurObjDict_SparseRefs`
- `MixedDict_SparseRefs`
- `MixedDict_DenseRefs`

当前已覆盖的主要参数：

- `RemovedChildren = 40 / 70`
- `ValidationMode = HotPath / Strict`

## 10. 后续优化边界

在拿到一轮干净 benchmark 数据之前，当前更适合保持克制，不急着引入：

- 反向引用索引
- Epoch 重映射表
- 更激进的默认运行时局部校验削减

这些方案不是没价值，而是复杂度明显更高，应当建立在现有 benchmark 已证明“当前瓶颈确实不够好”的前提上。

## 11. 推荐运行命令

先用 Release 跑端到端，再跑 primary / compaction 两层阶段 benchmark：

```bash
dotnet run -c Release --project benchmarks/RevisionCommit.Bench/RevisionCommit.Bench.csproj --filter "*CompactionCommitBenchmarks*"
```

```bash
dotnet run -c Release --project benchmarks/RevisionCommit.Bench/RevisionCommit.Bench.csproj --filter "*PrimaryCommitStageBenchmarks*"
```

```bash
dotnet run -c Release --project benchmarks/RevisionCommit.Bench/RevisionCommit.Bench.csproj --filter "*CompactionStageBenchmarks*"
```

报告目录提示：

- BenchmarkDotNet 默认把报告输出到“当前工作目录”下的 `BenchmarkDotNet.Artifacts/results/`
- 如果在仓库根目录执行上述命令，报告通常出现在 `BenchmarkDotNet.Artifacts/results/`
- 如果在 `benchmarks/RevisionCommit.Bench/` 目录内执行，则报告通常出现在 `benchmarks/RevisionCommit.Bench/BenchmarkDotNet.Artifacts/results/`

如果首轮只想快速确认 primary commit 的大头，可以先聚焦 `Persist_Only`：

```bash
dotnet run -c Release --project benchmarks/RevisionCommit.Bench/RevisionCommit.Bench.csproj --filter "*PrimaryCommitStageBenchmarks.Persist_Only*"
```

如果首轮只想快速聚焦 compaction 校验收益，可以先缩到阶段 benchmark 的 `Validate_Only`：

```bash
dotnet run -c Release --project benchmarks/RevisionCommit.Bench/RevisionCommit.Bench.csproj --filter "*CompactionStageBenchmarks.Validate_Only*"
```

如果想先看单一场景，可以先聚焦 dense refs：

```bash
dotnet run -c Release --project benchmarks/RevisionCommit.Bench/RevisionCommit.Bench.csproj --filter "*MixedDict_DenseRefs*"
```

## 12. 首轮观测重点

第一轮建议优先回答这几件事：

1. `HotPath` 相比 `Strict` 到底省了多少。
2. `PrimaryCommit_Only` 中，主成本是否由 `Persist_Only` 主导，还是 `WalkAndMark_Only` 已经不可忽略。
3. 在不同场景下，compaction 侧主成本是 `CompactWithUndo`、引用重写、校验，还是 `FollowupPersist_Only`。
4. `SparseRefs` 与 `DenseRefs` 的差距是否明显到足以决定后续优化方向。
5. `RemovedChildren = 40` 与 `70` 的差异，是否说明 hole ratio 已明显影响收益或成本。

若首轮结果显示：

- `Persist_Only` 明显压倒 `WalkAndMark_Only` 与 `Finalize_Only`：优先回看对象 diff 序列化、`VersionChain.Write(...)` 与 RBF append 路径。
- `WalkAndMark_Only` 已占到显著比例：优先回看对象图遍历、child ref visitor 与 `liveObjects` 收集分配。
- `Validate_Only` 明显主导 compaction 成本：优先继续打磨校验路径。
- `ReferenceRewrite_Only` 在 dense refs 下明显放大：再考虑是否值得研究引用索引类方案。
- `FollowupPersist_Only` 占比很高：优先回看脏对象数量、diff 写出与 ObjectMap 持久化路径。

## 13. 关联文件速查

```
src/StateJournal/
├── Revision.cs                           # Commit 流程 + Compaction Apply/Rollback + Follow-up Persist
├── DurableObject.cs                      # Bind()/Rebind()
├── Pools/GcPool.cs                       # CompactWithUndo/RollbackCompaction
├── Pools/SlotPool.cs                     # Store/Free/FreeBitmap/MoveSlot/MoveSlotRecorded/UndoMoveSlot
├── Pools/SlabBitmap.cs                   # CompactionEnumerator 入口
├── Pools/SlabBitmap.Enumerator.cs        # OnesForward/ZerosReverse/CompactionEnumerator 实现
├── Pools/SlotHandle.cs                   # 24-bit index + 8-bit generation 编码
├── Internal/IChildRefVisitor.cs          # 只读子引用遍历
├── Internal/IChildRefRewriter.cs         # 可写子引用重写接口
├── Internal/DurObjDictImpl.cs            # DurableObject 引用 dict（已实现重写）
├── Internal/MixedDictImpl.cs             # 混合值 dict（已实现重写）
├── Internal/ValueBox.DurableRef.cs       # DurableRef 编解码（ValueBox.From / GetDurRefId）
└── Internal/DictChangeTracker.cs         # CommittedKeys / Current / Commit 流程
```
