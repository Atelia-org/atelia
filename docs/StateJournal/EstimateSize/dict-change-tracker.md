# `DictChangeTracker<TKey, TValue>` size-estimate 现状说明

## 1. 当前结论

当前主线里，`DictChangeTracker` 的 `rebase vs deltify` 决策已经不是 count-based。

真实决策链路是：

- `DurableDictBase<TKey>.WritePendingDiff(...)` 读取子类暴露的 `EstimatedRebaseBytes` / `EstimatedDeltifyBytes`，加上 `WriteBytes(TypeCode)` 与 `WriteBytes(null)` 的长度前缀后，交给 `VersionChainStatus.ShouldRebase(...)` 决策，见 `src/StateJournal/DurableDictBase.cs:56-78`
- `VersionChainStatus.ShouldRebase(uint rebaseSize, uint deltifySize)` 基于字节估算做选择，见 `src/StateJournal/Internal/VersionChainStatus.cs:47-58`

`DictChangeTracker` 自身的字节估算入口是：

- `EstimatedRebaseBytes<KHelper, VHelper>()`，见 `src/StateJournal/Internal/DictChangeTracker.cs:44-54`
- `EstimatedDeltifyBytes<KHelper, VHelper>()`，见 `src/StateJournal/Internal/DictChangeTracker.cs:56-66`

两者都是 `O(1)`：通过内置的 `EstimateSummary` 聚合字段直接读出，不再扫描 `_current`。

## 2. 历史语义与当前实现的边界

早期设计稿曾把 dict 的 `rebase vs deltify` 决策建模成：

- “`EstimatedRebaseBytes ~= count × 常数`”
- “`EstimatedDeltifyBytes ~= (remove + upsert) × 常数`”

并且 `EstimatedRebaseBytes` 是 `O(n)` 全表扫描。

那套思路已经不是当前主线实现：

- 当前 dict body 已经精确建模 `WriteCount(...)` 头 + bare key/value payload；
- `WriteBytes(TypeCode)` / `WriteBytes(null)` 的长度前缀也由 `DurableDictBase` 显式补上；
- entry payload 的字节数由 `ITypeHelper.EstimateBareSize(...)` 提供，不再是常数近似；
- `_estimate` 在 mutation 热路径上增量维护，`EstimatedRebaseBytes` / `EstimatedDeltifyBytes` 都退化到 `O(1)`。

## 3. 当前真实协议形状

dict body 的实际序列化结构是 remove / upsert 两段：

`deltify`

```text
WriteCount(removeCount)
+ Σ remove key   [ KHelper.Write(key, asKey: true) ]
+ WriteCount(upsertCount)
+ Σ upsert pair  [ KHelper.Write(key, asKey: true) + VHelper.Write(value, asKey: false) ]
```

对应实现：`src/StateJournal/Internal/DictChangeTracker.cs:309-336`

`rebase`

```text
WriteCount(0)
+ WriteCount(currentCount)
+ Σ current pair [ KHelper.Write(key, asKey: true) + VHelper.Write(value, asKey: false) ]
```

对应实现：`src/StateJournal/Internal/DictChangeTracker.cs:338-348`

外层 frame 由 `DurableDictBase.WritePendingDiff(...)` 负责：

- rebase frame：`WriteBytes(TypeCode)` 标识容器类型
- deltify frame：`WriteBytes(null)` 留空 typeCode

两者都包含 `VarUInt` 长度前缀，已通过 `CostEstimateUtil.WriteBytesSize(...)` 计入决策字节数，见 `src/StateJournal/DurableDictBase.cs:60-62`。

## 4. 当前估算实现

### 4.1 `EstimateSummary` 字段

`DictChangeTracker` 内嵌一个轻量聚合结构，见 `src/StateJournal/Internal/DictChangeTracker.cs:10-15`：

```csharp
private struct EstimateSummary {
    public uint CommittedPayloadBareBytes;
    public uint CurrentPayloadBareBytes;
    public uint DirtyRemovedKeyBareBytes;
    public uint DirtyUpsertPayloadBareBytes;
}
```

字段语义：

- `CommittedPayloadBareBytes`：上次 commit 后的全量 bare payload，用于 `Revert` / `Unfreeze` 时 `O(1)` 对齐 `Current`
- `CurrentPayloadBareBytes`：当前 `_current` 全量 bare payload，是 `EstimatedRebaseBytes` 的核心输入
- `DirtyRemovedKeyBareBytes`：dirty remove 段对应 committed 旧 key 的 bare 字节和
- `DirtyUpsertPayloadBareBytes`：dirty upsert 段对应当前 (key + new value) 的 bare 字节和

### 4.2 估算公式

`EstimatedRebaseBytes` 按 rebase wire shape 累计：

```text
VarIntSize(0)
+ VarIntSize(currentCount)
+ CurrentPayloadBareBytes
```

`EstimatedDeltifyBytes` 按 delta 两段协议累计：

```text
VarIntSize(removeCount)
+ DirtyRemovedKeyBareBytes
+ VarIntSize(upsertCount)
+ DirtyUpsertPayloadBareBytes
```

这两个公式与 `WriteRebase` / `WriteDeltify` 一一对应。

### 4.3 增量维护点

`EstimateSummary` 的更新挂接在所有会改 `_current` / `_dirtyKeys` 的入口：

- `Upsert` / `Remove` 热路径：在覆盖 `_current` 前拿到旧值字节数，按 dirty 状态转移更新 summary
- `Commit<VHelper>()`：完成后令 `CommittedPayloadBareBytes = CurrentPayloadBareBytes`，dirty 两项清零，见 `src/StateJournal/Internal/DictChangeTracker.cs:237-264`
- `Revert<VHelper>()`：完成后令 `CurrentPayloadBareBytes = CommittedPayloadBareBytes`，dirty 两项清零，见 `src/StateJournal/Internal/DictChangeTracker.cs:208-235`
- `FreezeFromClean` / `FreezeFromCurrent` / `UnfreezeToMutableClean`：保留 `CurrentPayloadBareBytes` 并对齐 `CommittedPayloadBareBytes`，见 `src/StateJournal/Internal/DictChangeTracker.cs:266-307`
- `ApplyDelta` / `SyncCurrentFromCommitted`：冷路径走一次 `RecomputeEstimateSummarySlow` 全量重建，见 `src/StateJournal/Internal/DictChangeTracker.cs:350-380`

### 4.4 DEBUG 自检

为了防止 summary drift，`EstimatedRebaseBytes` / `EstimatedDeltifyBytes` 在 DEBUG 构建下都会调用 `AssertEstimateSummaryConsistent`，全量重算与 `_estimate` 比对，见 `src/StateJournal/Internal/DictChangeTracker.cs:68-108`。Release 构建下 `[Conditional("DEBUG")]` 直接消除。

## 5. 仍然存在的近似与误差来源

dict body 自己的协议骨架已经字节级建模，剩余误差主要来自下层：

- `KHelper.EstimateBareSize(...)` / `VHelper.EstimateBareSize(...)`：例如 `StringHelper` 仍可能是保守近似，见 `src/StateJournal/Internal/ITypeHelper.cs:166`
- `ValueBox.EstimateBareSize(...)` 对部分堆态值走上界，见 `src/StateJournal/Internal/ValueBox.cs:34-46`
- `VersionChainStatus.PerFrameOverhead` 仍然是共享层的粗略 frame envelope 近似，见 `src/StateJournal/Internal/VersionChainStatus.cs:17-18`

这些误差的性质是“值大小近似”或“共享层 envelope 近似”，不再是 dict 容器层的协议字段漏算。

## 6. 一句话总结

`DictChangeTracker` 当前已经完成从“count-based 近似 + `O(n)` rebase 扫描”到“协议感知的 `O(1)` 字节估算”的迁移；保留的 `RemoveCount` / `UpsertCount` 现在表达的是 delta wire shape，而不是 `ShouldRebase(...)` 的核心启发式。
