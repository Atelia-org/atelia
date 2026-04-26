# `TextSequenceCore` size-estimate 现状说明

## 1. 当前结论

当前主线里，`TextSequenceCore` 的 `rebase vs deltify` 决策已经不是 count-based。

真实决策链路是：

- `DurableText.WritePendingDiff(...)` 读取 `_core.EstimatedRebaseBytes()` / `_core.EstimatedDeltifyBytes()`，见 `src/StateJournal/DurableText.cs:112-130`
- `VersionChainStatus.ShouldRebase(uint rebaseSize, uint deltifySize)` 基于字节估算做选择，见 `src/StateJournal/Internal/VersionChainStatus.cs:47-52`

当前 `TextSequenceCore` 的估算实现也已经不是早期那种固定常数近似：

- `EstimatedRebaseBytes()` 按真实 rebase wire shape 逐项累计，见 `src/StateJournal/NodeContainers/TextSequenceCore.cs:270-289`
- `EstimatedDeltifyBytes()` 按 `dirty link / dirty value / appended` 三个 section 分别累计，见 `src/StateJournal/NodeContainers/TextSequenceCore.cs:291-320`

因此这里要明确：

1. 当前核心决策输入是 `Estimated*Bytes`
2. `DirtyLinkCount` / `DirtyValueCount` / `AppendedCount` 等 count 只是协议头和 section 结构的一部分
3. 它们不是旧的 count-based 核心启发式，也不应再被文档表述成当前对外合同

## 2. 历史语义与当前实现的边界

早期讨论里，`TextSequenceCore` 曾被描述成：

- `EstimatedRebaseBytes() ~= _count * 常数`
- `EstimatedDeltifyBytes() ~= 三个 count × 常数`

这已经不是当前主线实现。

当前代码已经显式计入：

- 文本头的 `head.Sequence`
- 文本头的 `count`
- arena rebase/delta 的 section count
- 各 section 内节点的 `seq` / `nextSeq`
- dummy key
- value 的 helper 估算字节数

对应实现：

- `src/StateJournal/NodeContainers/TextSequenceCore.cs:270-320`

## 3. 当前真实协议形状

### 3.1 文本层头部

`TextSequenceCore` 在两条路径上都会先写：

- `_head.Sequence`
- `_count`

对应位置：

- `src/StateJournal/NodeContainers/TextSequenceCore.cs:231-240`

### 3.2 `LeafChainStore` 的三段式协议

之后进入 `LeafChainStore`，当前真实协议是：

1. `linkMutationCount`，每条 `{ seq, newNextSeq }`
2. `valueMutationCount`，每条 `{ seq, value }`
3. `appendedCount`，每条 `{ seq, nextSeq, key, value }`

对应源码：

- `src/StateJournal/NodeContainers/LeafChainStore.cs:357-421`

对 `TextSequenceCore` 来说：

- key 是 dummy `byte`，恒为 `0`
- value 是 `string`

因此这些 count 的角色是：

- 它们是实际序列化协议的一部分
- 它们必须体现在 `Estimated*Bytes()` 中
- 它们不是“拿来替代字节估算的旧启发式”

## 4. 当前估算公式

### 4.1 `EstimatedRebaseBytes()`

当前实现等价于：

```text
rebaseBytes =
    size(headSeq)
  + size(count)
  + size(0)
  + size(0)
  + size(liveCount)
  + Σ live node [
        size(seq)
      + size(nextSeq)
      + 1(dummy key)
      + estimate(value)
    ]
```

对应代码：

- `src/StateJournal/NodeContainers/TextSequenceCore.cs:270-289`

这里是沿 live chain 估算，所以 dead node 不会被误计入 rebase。

### 4.2 `EstimatedDeltifyBytes()`

当前实现等价于：

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
      + 1(dummy key)
      + estimate(value)
    ]
```

对应代码：

- `src/StateJournal/NodeContainers/TextSequenceCore.cs:291-320`

这意味着当前实现已经能正确区分：

- 纯 relink
- 纯 content mutation
- appended node

不再是“三个 count 各乘一个拍脑袋常数”。

## 5. 当前复杂度

当前复杂度应按真实实现理解：

- `EstimatedRebaseBytes()` = `O(live node count)`
- `EstimatedDeltifyBytes()` = `O(dirtyLinkCount + dirtyValueCount + appendedPhysicalCount)`

因此，过去“估算几乎是 O(1)”的描述已经不再适用当前主线。

## 6. 仍然存在的近似与误差来源

当前实现已经把协议骨架建模到了字节级，但仍然保留 helper 层近似。

最典型的是 `string`：

- `TextSequenceCore` 的 value 现在走 inline payload 路径写盘
- `StringHelper.EstimateBareSize(...)` 已按 `BareStringPayload` 的真实 header + payload 长度估算
- 对应位置：`src/StateJournal/Internal/ITypeHelper.cs` 中的 `StringHelper.EstimateBareSize(...)`

因此，对 `TextSequenceCore` 来说，`string` helper 本身不再是主要误差来源；剩余误差如果存在，更多来自共享层 envelope 估算，而不是字符串值大小本身。

- 不再是“协议字段漏算”

## 7. live 视角与 appended 物理窗口视角

这里仍然有一个容易混淆、但和 count-based 语义不同的重要点：

- rebase 站在 live chain 视角
- deltify 的 appended section 站在物理 appended window 视角

也就是说：

- `EstimatedRebaseBytes()` 只关心当前逻辑可达的 live blocks
- `EstimatedDeltifyBytes()` 的 appended 部分要忠实反映 `_committedCount.._currentCount` 这段物理窗口

这属于当前协议事实，不是遗留启发式。

## 8. 测试侧已对齐到字节语义

当前测试已经直接围绕字节估算语义，而不是 count-based 语义：

- `tests/StateJournal.Tests/NodeContainers/TextSequenceCoreTests.cs:13-80`

覆盖点包括：

- rebase header 与 live node scaffold 的字节数
- dirty link / dirty value / appended 三段 delta 的字节数
- appended window 包含 deleted draft node 的场景
- 与真实序列化形状的一致性

这说明当前主线心智模型已经是“协议感知的字节估算”。

## 9. 后续文档表述建议

当前最需要避免的误解是：

- “`TextSequenceCore` 现在还靠几个 count 的固定常数近似做决策”

更准确的表述应当是：

- `TextSequenceCore` 当前靠 `EstimatedRebaseBytes()` / `EstimatedDeltifyBytes()` 提供核心决策输入
- `DirtyLinkCount` / `DirtyValueCount` / `AppendedCount` 等 count 是协议 section 的结构事实
- 它们参与字节估算，但不替代字节估算本身
- 若文档仍提到这些 count，应把它们理解为协议结构或诊断视图，而不是上层决策接口

## 10. 一句话总结

`TextSequenceCore` 当前已经完成从“常数级 count 近似”到“协议感知的字节估算”的迁移；保留的 counts 现在表达的是 delta/rebase wire shape，而不是 `ShouldRebase(...)` 的核心启发式。
