# `DequeChangeTracker<TValue>` size-estimate 现状说明

## 1. 当前结论

当前主线里，`DequeChangeTracker` 的 `rebase vs deltify` 决策已经不是 count-based。

真实决策链路是：

- `DurableDequeBase.WritePendingDiff(...)` 读取子类暴露的 `EstimatedRebaseBytes` / `EstimatedDeltifyBytes`，加上 `WriteBytes(TypeCode)` / `WriteBytes(null)` 的长度前缀后，交给 `VersionChainStatus.ShouldRebase(...)`，见 `src/StateJournal/DurableDequeBase.cs:53-72`
- `VersionChainStatus.ShouldRebase(uint rebaseSize, uint deltifySize)` 基于字节估算做选择，见 `src/StateJournal/Internal/VersionChainStatus.cs:47-58`

`DequeChangeTracker` 自身的字节估算入口是：

- `EstimatedRebaseBytes<VHelper>()`，见 `src/StateJournal/Internal/DequeChangeTracker.cs:55-65`
- `EstimatedDeltifyBytes<VHelper>()`，见 `src/StateJournal/Internal/DequeChangeTracker.cs:67-82`

`EstimatedRebaseBytes` 是 `O(1)`；`EstimatedDeltifyBytes` 在 keep-patch index cache 命中时是 `O(1)`，cache 失效时退化到 `O(KeepDirtyCount)` 重算。

## 2. 历史语义与当前实现的边界

早期设计稿里，`DequeChangeTracker` 曾被描述成：

- `EstimatedRebaseBytes` 为 `O(current.Count)` 全量扫描
- `EstimatedDeltifyBytes` 漏算 5 个 `WriteCount(...)` header
- `keep patch` 的 index 误用了 `currentIndex` 而非 `keepRelativeIndex`，在 `_newKeepLo > 0` 场景系统性高估
- `WriteBytes(TypeCode)` 只算了 payload 长度，没有算长度前缀

那套思路已经不是当前主线实现：

- `_currentValueBytes` 与 prefix / suffix / keep 三段 dirty value bytes 都在 mutation 热路径上增量维护；
- 5 个 `WriteCount(...)` header 已经被 `CountHeaderBytes(...)` 显式累加；
- keep patch index header 已切换到正确的 `keepRelativeIndex` 语义并以 `_dirtyKeepIndexBytesCache` 形式 lazy 维护；
- `WriteBytes(TypeCode)` / `WriteBytes(null)` 的长度前缀由 `CostEstimateUtil.WriteBytesSize(...)` 在 `DurableDequeBase` 层补齐，见 `src/StateJournal/DurableDequeBase.cs:56-58`。

## 3. 当前真实协议形状

deque body 的实际序列化结构是“5 个 count header + 三段 dirty bytes + keep patch index”：

`deltify`

```text
WriteCount(trimFrontCount)
+ WriteCount(trimBackCount)
+ WriteCount(keepDirtyCount)
+ Σ keep patch [ WriteCount(keepRelativeIndex) + VHelper.Write(value, asKey: false) ]
+ WriteCount(pushFrontCount)
+ Σ push-front [ VHelper.Write(value, asKey: false) ]   // 逆序写
+ WriteCount(pushBackCount)
+ Σ push-back  [ VHelper.Write(value, asKey: false) ]
```

对应实现：`src/StateJournal/Internal/DequeChangeTracker.cs:398-416`

`rebase`：复用同一形状，等价于在空 committed 上的 push-back

```text
WriteCount(0)
+ WriteCount(0)
+ WriteCount(0)
+ WriteCount(0)
+ WriteCount(currentCount)
+ Σ current value [ VHelper.Write(value, asKey: false) ]
```

对应实现：`src/StateJournal/Internal/DequeChangeTracker.cs:418-428`

注意 `keep patch` 的 index 是 `keepRelativeIndex = committedIndex - _oldKeepLo`，不是 `currentIndex`。这一点同时影响估算与写盘，必须一致。

外层 frame 由 `DurableDequeBase.WritePendingDiff(...)` 负责：

- rebase frame：`WriteBytes(TypeCode)`
- deltify frame：`WriteBytes(null)`

两者都包含 `VarUInt` 长度前缀，已计入决策字节数。

## 4. 当前估算实现

### 4.1 `EstimateSummary` 字段

`DequeChangeTracker` 不再用嵌套结构，而是直接持有以下增量字段，见 `src/StateJournal/Internal/DequeChangeTracker.cs:21-27`：

```csharp
private uint _currentValueBytes;
private uint _dirtyPrefixValueBytes;
private uint _dirtySuffixValueBytes;
private uint _dirtyKeepValueBytes;
private uint _dirtyKeepIndexBytesCache;
private bool _dirtyKeepIndexBytesCacheValid;
```

字段语义：

- `_currentValueBytes`：`_current` 全量 value 的估算字节和，是 `EstimatedRebaseBytes` 的核心输入
- `_dirtyPrefixValueBytes`：`_current[0 .. _newKeepLo)` 段的 value 字节和（对应 push-front 段）
- `_dirtySuffixValueBytes`：`_current[NewKeepHi .. _current.Count)` 段的 value 字节和（对应 push-back 段）
- `_dirtyKeepValueBytes`：keep window 内所有 dirty slot 当前值的字节和
- `_dirtyKeepIndexBytesCache` + `…Valid`：`Σ VarIntSize(keepRelativeIndex)` 的 lazy cache；只在 dirty 集合或 `_oldKeepLo` 发生变化时失效

### 4.2 估算公式

`EstimatedRebaseBytes`：

```text
5 × VarIntSize(0..0,currentCount)  // 实际是 4 个 0 + 1 个 currentCount
+ _currentValueBytes
```

`EstimatedDeltifyBytes`：

```text
VarIntSize(trimFrontCount)
+ VarIntSize(trimBackCount)
+ VarIntSize(keepDirtyCount)
+ VarIntSize(pushFrontCount)
+ VarIntSize(pushBackCount)
+ _dirtyPrefixValueBytes
+ _dirtySuffixValueBytes
+ _dirtyKeepValueBytes
+ GetDirtyKeepIndexBytes()
```

两个公式与 `WriteRebase` / `WriteDeltify` 一一对应，且不再误用 `currentIndex`。

### 4.3 增量维护点

value bytes 字段挂接在所有会改窗口或 dirty map 的入口：

- `PushFront` / `PushBack` / `PopFront` / `PopBack` 及对应的 absorb committed fast path，见 `src/StateJournal/Internal/DequeChangeTracker.cs:103-137`
- keep window 内的 `AfterSet`（脏标转移、新旧值差量），见 `src/StateJournal/Internal/DequeChangeTracker.cs:176-213`
- `TryAbsorbDirtyFrontIntoKeep` / `TryAbsorbDirtyBackIntoKeep`：把 dirty 段并入 keep 窗口时迁移 `_dirtyPrefix/Suffix` 到 `_dirtyKeep`，见 `src/StateJournal/Internal/DequeChangeTracker.cs:453-489`
- `Commit<VHelper>()` / `Revert<VHelper>()`：在 `ResetTrackedWindow` 处把 summary 收敛到 clean baseline，见 `src/StateJournal/Internal/DequeChangeTracker.cs:349-394, 487-499`
- `ApplyDelta` / `SyncCurrentFromCommitted`：冷路径在 `ResetTrackedWindow` 里一次性重建 `_currentValueBytes`，见 `src/StateJournal/Internal/DequeChangeTracker.cs:430-499`

keep patch index cache 的失效规则：

- `_oldKeepLo` 滑动 → 失效（所有 `keepRelativeIndex` 整体平移）
- `_committedDirtyMap` 集合变化 → 失效
- `_newKeepLo` 变化但 `_oldKeepLo` 未变 → 不失效（patch index 不依赖 `_newKeepLo`）

### 4.4 DEBUG 自检

`EstimatedRebaseBytes` / `EstimatedDeltifyBytes` 在 DEBUG 构建下都会调用 `AssertEstimateSummaryConsistent<VHelper>()`，全量重算并比对所有 summary 字段，见 `src/StateJournal/Internal/DequeChangeTracker.cs:652-...`。Release 构建下 `[Conditional("DEBUG")]` 直接消除。

## 5. 仍然存在的近似与误差来源

deque body 自己的协议骨架已经字节级建模，剩余误差主要来自下层：

- `VHelper.EstimateBareSize(...)`：例如 `StringHelper` 固定返回 `5`，见 `src/StateJournal/Internal/ITypeHelper.cs:166`
- `ValueBox.EstimateBareSize(...)` 对部分堆态值走上界，见 `src/StateJournal/Internal/ValueBox.cs:34-46`
- `VersionChainStatus.PerFrameOverhead` 仍是共享层的粗略 frame envelope 近似，见 `src/StateJournal/Internal/VersionChainStatus.cs:17-18`

这些误差的性质是“值大小近似”或“共享层 envelope 近似”，不再是 deque 容器层的协议字段漏算或基准错位。

## 6. 一句话总结

`DequeChangeTracker` 当前已经完成从“`O(n)` rebase 扫描 + 漏算 5 个 count header + keep-patch index 误用 `currentIndex`”到“协议感知的 `O(1)` 字节估算 + lazy keep-index cache”的迁移；保留的 `TrimFront/Back/PushFront/Back/KeepDirty` 等 count 现在表达的是 delta wire shape，而不是 `ShouldRebase(...)` 的核心启发式。
