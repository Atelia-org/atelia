# StateJournal 中旧的 count-based 残留语义调研

更新日期：2026-04-26

状态注记：

- 本文是 2026-04-26 的调研快照，用于定位"旧 count-based 估算的残留"。
- **同日已完成全部清理动作**：
  - `DictChangeTracker` / `DequeChangeTracker` / `SkipListCore` / `TextSequenceCore` 中的聚合 `RebaseCount` / `DeltifyCount` 已删除，相应测试与注释同步调整。
  - `VersionChainStatus.PerFrameOverhead` 已从魔数 `7` 重写为叶常量求和（RBF 协议层 + VersionChain metadata 层），数值与协议事实对齐。
  - 过时设计文档 `EstimateSize/skip-list-core.md` / `text-sequence-core.md` / `dict-change-tracker.md` / `deque-change-tracker.md` 均已化简为"现状说明"。
- 因此，下文"建议的后续清理顺序"与"最终判断"等段落是**调研当时的提案**，不再是待办事项；保留它们是为了让后续读者能看到清理动机与决策痕迹。

## 结论摘要

当前主线里，`rebase vs deltify` 的核心决策已经不再读取 `RebaseCount` / `DeltifyCount`。真实决策入口是字节估算：

- `src/StateJournal/Internal/VersionChainStatus.cs:47-52` 的 `ShouldRebase(uint rebaseSize, uint deltifySize)`
- `src/StateJournal/DurableDictBase.cs:56-76`
- `src/StateJournal/DurableDequeBase.cs:53-72`
- `src/StateJournal/DurableText.cs:112-130`

也就是说，旧的 count-based 语义现在主要残留在三类地方：

1. 诊断/测试辅助属性：还保留 `RebaseCount` / `DeltifyCount` 命名，但已不是决策输入。
2. 过时注释与设计文档：仍把当前实现描述成“按 count 近似”。
3. 少量“按 count 命名但其实仍有真实用途”的字段：它们代表协议里的 section count，不属于应删除的遗留。

## 分类结果

### 一、真实逻辑依赖，应保留

这些 count 命名虽然还在，但它们表达的是当前协议结构，已经不是“旧的 count-based 决策残留”，不建议直接删。

#### 1. `DictChangeTracker` 的 `RemoveCount` / `UpsertCount`

位置：

- `src/StateJournal/Internal/DictChangeTracker.cs:37-38`
- `src/StateJournal/Internal/DictChangeTracker.cs:57-66`
- `src/StateJournal/Internal/DictChangeTracker.cs:340-349`

用途：

- `EstimatedDeltifyBytes()` 直接把 `RemoveCount` / `UpsertCount` 作为 delta wire shape 的 header 字段计入字节估算。
- `WriteRebase()` / `WriteDeltify()` 真实序列化时也要写 count header。

判断：

- 这是“协议层 count”，不是旧的“用 count 近似成本”。
- 应保留。

#### 2. `DequeChangeTracker` 的分段 count

位置：

- `src/StateJournal/Internal/DequeChangeTracker.cs:40-45`
- `src/StateJournal/Internal/DequeChangeTracker.cs:69-83`
- `src/StateJournal/Internal/DequeChangeTracker.cs:413-430`

涉及命名：

- `TrimFrontCount`
- `TrimBackCount`
- `PushFrontCount`
- `PushBackCount`
- `KeepCount`
- `KeepDirtyCount`

用途：

- 这些值直接对应 deque delta/rebase 协议的 5 个 count header。
- `EstimatedDeltifyBytes()` 和真实写盘都依赖它们。

判断：

- 这些 count 是“当前协议事实”，不是历史残留。
- 应保留。

#### 3. `VersionChainStatus.PerFrameOverhead`

位置：

- `src/StateJournal/Internal/VersionChainStatus.cs:17-18`

用途：

- 注释仍写成“固定额外开销相当于多少条变更”，带有明显 count-based 口吻。
- 但这个常量仍参与当前 `ShouldRebase(...)` 的累计成本模型。

判断：

- 逻辑上仍需保留。
- 但注释建议改成“共享 frame 开销近似值”，避免继续把它理解成 payload count 语义。

> **后续动作**：已落地，并进一步重写为叶常量求和形式（RBF 协议层 + VersionChain metadata 层），数值随协议事实自然得出，不再是手调魔数。

### 二、diagnostics / test-only 残留，可降级或删除

这部分最符合"旧的 count-based 残留语义"。

> **后续动作**：本节列出的 4 处聚合属性已全部删除，唯一直接引用 `RebaseCount` 的活跃测试也已迁移到 `Current.Count`。下文细节作为决策档案保留。

#### 1. `DictChangeTracker.RebaseCount` / `DeltifyCount`

位置：

- `src/StateJournal/Internal/DictChangeTracker.cs:34-43`

现状：

- 明确放在 `#region 可用于单元测试` 中。
- 生产估算与决策已经改走 `EstimatedRebaseBytes()` / `EstimatedDeltifyBytes()`，见同文件 `:45-68`。
- 本次调研未发现主线生产代码对这两个属性的消费点。

判断：

- `test-only`。
- 可以降级为 `DEBUG` 下的诊断接口，或直接删除后按需在测试里改看更具体的状态。

#### 2. `DequeChangeTracker.RebaseCount` / `DeltifyCount`

位置：

- `src/StateJournal/Internal/DequeChangeTracker.cs:39-48`

现状：

- 同样属于测试辅助区域。
- `DeltifyCount` 的上方注释写着“用于估算落盘大小”，但真实估算代码已经不用这个聚合值，而是直接读 5 个 section count 与各类 byte summary，见 `src/StateJournal/Internal/DequeChangeTracker.cs:69-83`。
- 活跃测试里只发现一个直接断言 `RebaseCount` 的位置：`tests/StateJournal.Tests/Internal/DequeChangeTrackerTests.cs:108`。

判断：

- `RebaseCount` / 聚合版 `DeltifyCount` 都可视为 legacy diagnostics。
- `DeltifyCount` 上方注释已过时。
- 适合改名为 `Diagnostic*`，或直接删掉并让测试改用 `Current.Count` / `PushFrontCount` / `PushBackCount` / `KeepDirtyCount` 等更具体指标。

#### 3. `SkipListCore.RebaseCount` / `DeltifyCount`

位置：

- `src/StateJournal/NodeContainers/SkipListCore.cs:344-347`

现状：

- 这两个属性是 `internal`，但不在测试辅助区，也没有当前主线消费者。
- 注释仍明确写着“估算开销输入”“与 DeltifyCount 单位不严格对齐，ShouldRebase 的启发式已容忍此差异”，这描述的是旧模型，不再符合当前代码。
- 当前真实估算已是按真实 wire shape 分 section 统计字节，见 `src/StateJournal/NodeContainers/SkipListCore.cs:352-400`。
- 对应测试也已经迁移到直接校验 `EstimatedRebaseBytes()` / `EstimatedDeltifyBytes()`，见 `tests/StateJournal.Tests/NodeContainers/SkipListCoreTests.cs:340-403`。

判断：

- 这是当前主线里最典型、最容易误导后续阅读者的 legacy residue。
- 若没有计划中的诊断面板依赖，建议优先删除。
- 若担心调试价值，可以先改成 `DiagnosticLiveCount` / `DiagnosticDirtyNodeCount` 之类更诚实的命名。

#### 4. `TextSequenceCore.RebaseCount` / `DeltifyCount`

位置：

- `src/StateJournal/NodeContainers/TextSequenceCore.cs:263-265`

现状：

- 与 `SkipListCore` 类似，未发现主线消费者。
- 当前真实估算已按 header / dirty-link / dirty-value / appended section 逐项计字节，见 `src/StateJournal/NodeContainers/TextSequenceCore.cs:270-320`。
- 新测试也已经围绕 `Estimated*Bytes()` 写，见 `tests/StateJournal.Tests/NodeContainers/TextSequenceCoreTests.cs:13-80`。

判断：

- 可直接删，或降级为 debug-only。
- 保留价值低于 `DequeChangeTracker` 的细粒度 count。

### 三、过时注释与文档，应尽快修正

#### 1. `SkipListCore` 源码注释仍在描述旧 count-based 估算

位置：

- `src/StateJournal/NodeContainers/SkipListCore.cs:344-347`

问题：

- 注释把 `RebaseCount` / `DeltifyCount` 描述成“估算开销输入”。
- 这会误导读者以为 `ShouldRebase` 还接受 count，而不是 byte estimate。

建议：

- 若属性保留，注释改成“诊断统计，不参与当前 rebase/deltify 决策”。
- 若属性删除，则注释一并清理。

#### 2. `docs/StateJournal/EstimateSize/skip-list-core.md` 明显落后于代码

位置：

- `docs/StateJournal/EstimateSize/skip-list-core.md:10-15`
- `docs/StateJournal/EstimateSize/skip-list-core.md:90-99`

问题：

- 文档仍写 `EstimatedDeltifyBytes()` 是“平均 live entry bytes × DeltifyCount”。
- 但当前代码已经不是这个实现，真实实现见 `src/StateJournal/NodeContainers/SkipListCore.cs:371-400`。

判断：

- 这是过时文档，不应再作为现状说明。
- 清理优先级高，因为它和当前源码直接冲突。

#### 3. `docs/StateJournal/EstimateSize/text-sequence-core.md` 明显落后于代码

位置：

- `docs/StateJournal/EstimateSize/text-sequence-core.md:7-12`

问题：

- 文档仍写：
  - `EstimatedRebaseBytes()` 直接返回 `_count * 5`
  - `EstimatedDeltifyBytes()` 只看三个 count 做固定常数近似
- 但当前代码已经改成按真实 wire shape 逐项累计，见 `src/StateJournal/NodeContainers/TextSequenceCore.cs:270-320`。

判断：

- 过时文档。
- 优先级同样很高。

#### 4. `docs/StateJournal/frozen-durable-object-design.md` 仍把 `RebaseCount` / `DeltifyCount` 当作 tracker 合同的一部分

位置：

- `docs/StateJournal/frozen-durable-object-design.md:458-459`
- `docs/StateJournal/frozen-durable-object-design.md:504`

问题：

- 文档讨论 frozen tracker 时仍把 `RebaseCount` / `DeltifyCount` 当作需要维持的显式行为面。
- 这会强化“count 仍是核心接口”的印象，但当前主线的核心接口已经是 `Estimated*Bytes`。

判断：

- 属于设计文档滞后，不是运行时问题。
- 可在前两类源码/文档清理之后再统一修。

### 四、历史材料，可保留为背景，不建议优先清理

#### 1. `archive/StateJournal` 中的旧实现

位置：

- `archive/StateJournal/DurableTreeCore.cs:35-42`
- `archive/StateJournal/DruableTreeCoreTests.cs:59-65`
- `archive/StateJournal/DruableTreeCoreTests.cs:121-123`

现状：

- 这里确实保留了“count 直接参与语义”的旧实现和测试。
- 但它已经在 `archive/`，不属于主线行为。

判断：

- 除非要做全仓库术语净化，否则不建议作为近期清理目标。

#### 2. `prototypes/PersistentAgentProto/README.md` 的历史说明

位置：

- `prototypes/PersistentAgentProto/README.md:43-65`

现状：

- 这里明确把问题描述成“把条目数误当字节成本”的历史修复说明。
- 这段文字与当前主线并不冲突，反而能解释为何要做这轮改造。

判断：

- 建议保留，属于有效历史背景，而不是误导性残留。

## 测试侧现状

活跃测试整体上已经明显迁移到“字节估算”语义：

- `tests/StateJournal.Tests/NodeContainers/SkipListCoreTests.cs:340-403` 直接校验 `EstimatedRebaseBytes()` / `EstimatedDeltifyBytes()` 是否与真实 payload 长度一致。
- `tests/StateJournal.Tests/NodeContainers/TextSequenceCoreTests.cs:13-80` 也围绕 `Estimated*Bytes()` 写测试。

仍残留的 count-based 测试依赖很少，当前只看到：

- `tests/StateJournal.Tests/Internal/DequeChangeTrackerTests.cs:108` 直接断言 `tracker.RebaseCount`。

这说明测试层已经基本完成语义迁移，清理 `RebaseCount` / `DeltifyCount` 的阻力不大。

## 建议的后续清理顺序（按“高收益低风险”排序）

### 1. 先清 `SkipListCore` / `TextSequenceCore` 的聚合 count 残留

建议动作：

- 删除或 debug-only 化：
  - `src/StateJournal/NodeContainers/SkipListCore.cs:344-347`
  - `src/StateJournal/NodeContainers/TextSequenceCore.cs:263-265`
- 同步修正 `SkipListCore` 上方过时注释。

原因：

- 无主线消费者。
- 最容易误导读者。
- 删除后对真实协议和估算逻辑没有影响。

### 2. 再清 tracker 里的聚合遗留名，保留协议级细粒度 count

建议动作：

- 处理 `DictChangeTracker.RebaseCount` / `DeltifyCount`
- 处理 `DequeChangeTracker.RebaseCount` / `DeltifyCount`
- 保留 `RemoveCount` / `UpsertCount` / `TrimFrontCount` / `PushBackCount` / `KeepDirtyCount` 等协议级字段
- 顺手把 `DequeChangeTracker` 的“用于估算落盘大小”注释改掉

原因：

- 风险仍低。
- 但需要一起处理少量测试和调用方阅读习惯。
- 这样能避免把“聚合 count”与“协议 count”混为一谈。

### 3. 更新三份最误导的文档

建议优先级：

1. `docs/StateJournal/EstimateSize/skip-list-core.md`
2. `docs/StateJournal/EstimateSize/text-sequence-core.md`
3. `docs/StateJournal/frozen-durable-object-design.md`

原因：

- 这些文档目前最容易让后续开发者误判当前实现状态。
- 尤其前两份与代码直接冲突，收益非常高。

## 最终判断

可以直接删/改名/降级为 debug-only 的：

- `SkipListCore.RebaseCount` / `DeltifyCount`
- `TextSequenceCore.RebaseCount` / `DeltifyCount`
- `DictChangeTracker.RebaseCount` / `DeltifyCount`
- `DequeChangeTracker.RebaseCount` / `DeltifyCount`

需要保留的：

- `DictChangeTracker.RemoveCount` / `UpsertCount`
- `DequeChangeTracker.Trim* / Push* / KeepDirtyCount`
- `VersionChainStatus.PerFrameOverhead` 本体（但建议改注释）

应视为过时说明并尽快修正的：

- `src/StateJournal/NodeContainers/SkipListCore.cs:344-347` 的注释
- `docs/StateJournal/EstimateSize/skip-list-core.md`
- `docs/StateJournal/EstimateSize/text-sequence-core.md`
- `docs/StateJournal/frozen-durable-object-design.md` 中相关段落
