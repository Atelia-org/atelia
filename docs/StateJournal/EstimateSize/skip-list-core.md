# `SkipListCore<TKey, TValue, KHelper, VHelper>` size-estimate 现状说明

## 1. 当前结论

当前主线里，`SkipListCore` 已经不再使用旧的 count-based 启发式来驱动 `rebase vs deltify` 决策。

真实决策链路是：

- `DurableDictBase.WritePendingDiff(...)` 读取对象给出的 `EstimatedRebaseBytes` / `EstimatedDeltifyBytes`，见 `src/StateJournal/DurableDictBase.cs:56-76`
- `VersionChainStatus.ShouldRebase(uint rebaseSize, uint deltifySize)` 基于字节估算做决策，见 `src/StateJournal/Internal/VersionChainStatus.cs:47-52`

对 `SkipListCore<TKey, TValue, KHelper, VHelper>` 而言，当前实现已经是：

- `EstimatedRebaseBytes()` 按真实 rebase wire shape 做 live-scan，见 `src/StateJournal/NodeContainers/SkipListCore.cs:352-369`
- `EstimatedDeltifyBytes()` 按 `dirty links / dirty values / appended nodes` 三个 section 分别估算，见 `src/StateJournal/NodeContainers/SkipListCore.cs:371-400`

因此，这里需要明确两点：

1. 当前核心决策输入是 `Estimated*Bytes`，不是 `RebaseCount` / `DeltifyCount`
2. 代码里仍保留的 section counts 反映的是协议结构事实，不是旧的“按 count 近似成本”启发式

## 2. 历史语义与当前实现的边界

早期设计讨论里，`SkipListCore` 曾经把 delta 成本近似成“平均 live entry 大小 × 变更数”。那套思路已经不是当前主线实现。

本轮清理后，`SkipListCore` 已不再暴露聚合版 `RebaseCount` / `DeltifyCount` 视图，避免把旧启发式心智模型继续留在公开表面。当前真正参与决策的是下面两个字节估算：

- `EstimatedRebaseBytes()`
- `EstimatedDeltifyBytes()`

## 3. 当前真实协议形状

### 3.1 `SkipListCore` 自己写出的头部

`SkipListCore.WriteRebase(...)` / `WriteDeltify(...)` 会先写：

- `head sequence`
- 逻辑 `count`

对应位置：

- `src/StateJournal/NodeContainers/SkipListCore.cs:307-320`

### 3.2 `LeafChainStore` 的三段式协议

之后叶链部分由 `_arena` 负责。当前真实协议是三段式：

1. `linkMutationCount`，每条为 `{ seq, newNextSeq }`
2. `valueMutationCount`，每条为 `{ seq, value }`
3. `appendedCount`，每条为 `{ seq, nextSeq, key, value }`

对应源码：

- `src/StateJournal/NodeContainers/LeafChainStore.cs:357-421`

因此，section counts 在当前实现中的角色是：

- 它们是协议头的一部分
- 它们必须计入字节估算
- 它们不是“旧启发式输入”

## 4. 当前估算公式

### 4.1 `EstimatedRebaseBytes()`

当前实现直接对齐真实 rebase payload 结构：

```text
rebaseBytes =
    size(headSeq)
  + size(count)
  + size(0) + size(0) + size(liveCount)
  + Σ live node [
        size(seq)
      + size(nextSeq)
      + estimate(key)
      + estimate(value)
    ]
```

对应实现：

- `src/StateJournal/NodeContainers/SkipListCore.cs:352-369`

这里的 `liveCount` 来自逻辑 live 链，dead node 不会被误计进 rebase。

### 4.2 `EstimatedDeltifyBytes()`

当前实现按真实 delta 三段协议逐项估算：

```text
deltifyBytes =
    size(headSeq)
  + size(count)
  + size(dirtyLinkCount)
  + size(dirtyValueCount)
  + size(appendedCount)
  + Σ dirty link [
        size(seq)
      + size(newNextSeq)
    ]
  + Σ dirty value [
        size(seq)
      + estimate(value)
    ]
  + Σ appended node [
        size(seq)
      + size(nextSeq)
      + estimate(key)
      + estimate(value)
    ]
```

对应实现：

- `src/StateJournal/NodeContainers/SkipListCore.cs:371-400`

这意味着，当前 `SkipListCore` 已经明确区分：

- link-only mutation
- value-only mutation
- appended node

不再把它们压扁成一个聚合 `dirtyCount`。

## 5. 当前复杂度

当前复杂度可以直接按真实实现理解：

- `EstimatedRebaseBytes()`：`O(liveCount)`
- `EstimatedDeltifyBytes()`：`O(dirtyLinkCount + dirtyValueCount + appendedCount)`

这与早期设计稿里“`EstimatedDeltifyBytes()` 仍然会扫整条 live 链”的描述不同。当前主线已经完成了这一阶段演进。

## 6. 仍然存在的近似与误差来源

当前实现虽然已经不是 count-based 决策，但仍然是 estimate，不是 byte-perfect serialization replay。

主要误差来源在 helper 层：

- `KHelper.EstimateBareSize(...)`
- `VHelper.EstimateBareSize(...)`

例如：

- `StringHelper.EstimateBareSize(...)` 仍可能是保守近似，见 `src/StateJournal/Internal/ITypeHelper.cs:166`

这类误差是可接受的，因为它们属于“值本身大小的近似”，而不再是“容器协议骨架漏算”。

## 7. 诊断语义应该怎样理解

如果后续又考虑引入类似的聚合诊断统计，它们也应只服务于调试观察，不应再回到 `ShouldRebase(...)` 的核心输入位置，更不应替代 `EstimatedRebaseBytes()` / `EstimatedDeltifyBytes()`。

## 8. 测试侧已对齐到字节语义

当前测试已经直接围绕字节估算写断言，而不是围绕 count 启发式写断言：

- `tests/StateJournal.Tests/NodeContainers/SkipListCoreTests.cs:340-403`

覆盖点包括：

- rebase payload 长度与 `EstimatedRebaseBytes()` 对齐
- link-only delete 的 delta payload 长度与 `EstimatedDeltifyBytes()` 对齐
- 混合 section 场景的 delta payload 对齐
- VarInt 边界场景的对齐

这进一步说明主线心智模型已经从“count-based heuristic”切换到“byte-based estimate”。

## 9. 后续文档与命名建议

当前最需要避免的误解是：

- “`SkipListCore` 仍然靠 `DeltifyCount` 做核心决策”

正确表述应当是：

- `SkipListCore` 当前靠 `EstimatedRebaseBytes()` / `EstimatedDeltifyBytes()` 提供核心决策输入
- section counts 是协议结构与估算公式的一部分
- 若未来重新引入聚合 count 诊断，也只应视为诊断统计

## 10. 一句话总结

`SkipListCore` 当前已经完成从“count-based 近似”到“section-aware byte estimate”的迁移；保留的 counts 现在描述的是协议结构事实或诊断视图，而不是 `rebase vs deltify` 的核心决策输入。
