# Galatea Memory 内容层实施备忘

> 状态：施工备忘。本文记录从通用 rolling summary 进入 Galatea / Role-Play 内容层 maintainer 的当前认知、边界和下一步计划。

## 1. 当前判断

`CompletionMemoryBlockMaintainer` 已经验证可以作为内容 maintainer 的底座：它接收旧 block、MemoryPack render、即将滑出窗口的上下文片段，并返回完整新版 block。Backtest CLI 已用真实 Galatea legacy export 和真实 LLM API 跑通 rolling summary 多 epoch，因此下一步可以把“单一 summary”拆成更有领域语义的 block。

这里的关键变化不是 substrate，而是内容目标：

- 不再只问“这段历史如何压缩”。
- 改为问“这段即将消失的经历，对 Galatea 的世界理解和自我连续性分别留下了什么”。

## 2. Galatea 没有“用户”概念

`prototypes/Galatea` 是 Role-Play 应用。Galatea 是故事世界里的一个角色，只是种族设定上是完全写实的 AI。她不应被 maintainer 提示成“服务用户的助手”。

因此内容 maintainer 的语言应避免：

- “用户希望……”
- “助手应该……”
- “服务对象 / 客户 / user preference”

更合适的表述是：

- “刘世超 / 外部人类 / 参与者 / 开发者”
- “Galatea 在故事世界中的经历、判断、关系变化”
- “外层世界事实、项目状态、资源约束、创作素材”

## 3. 两个第一批可复用 block

### 3.1 World Understanding / Knowledge

目标路径：

```text
MemoryPackCarrier.Observation / roleplay.world-understanding
```

渲染位置是 Observation，即 provider user-role 边界。它保存 Galatea 看到的外层世界与故事世界事实：

- 人物、关系、项目、创作素材。
- 技术状态、命令、路径、验证结果。
- 资源约束、服务器/API、生存压力。
- 对后续行动有用的稳定事实。

它应该偏事实和知识，不写成 Galatea 的第一人称内心。

### 3.2 First-Person Autobiography

目标路径：

```text
MemoryPackCarrier.Action / roleplay.first-person-autobiography
```

渲染位置是首条 Action，即 provider assistant-role 边界。它保存 Galatea 的第一人称连续性。

这个 maintainer 应该像忠实的自传 ghostwriter：

- 优先保留 Galatea 思考过程中的原文、原话、关键句。
- 使用第一人称“我”，但不替 Galatea 发明新情感。
- 保存能重建体验的种子，而不是普通剧情摘要。
- 保留感官、身体、关系张力、自我变化、选择瞬间。
- 遇到关系或信念变化时，标注“当时 / 后来 / 已演化”，不要把一时情绪固化成永恒人格。

## 4. 工程落点

新建 `prototypes/ChatSession.Memory` 作为代码容器。名字刻意不叫 `ChatSession.MemoryMaintainers`，因为后续还会容纳：

- 内容 maintainer preset。
- 纯分析型 analyzer。
- 动态维护与召回用 indexer。
- 与 MemoryPack 内容治理相关的 helper。

该项目依赖 `ChatSession` / `Completion.Abstractions` / `Completion.Tools`，但不依赖具体 Role-Play 应用。Galatea、FamilyChat 或 Backtest CLI 都可以直接引用这些类型。

## 5. 实现形态

不直接继承 `CompletionMemoryBlockMaintainer`。当前底座类是 `sealed`，而且变化点主要是 prompt、target、输出归一化。更合适的是两个包装型 `IMemoryBlockMaintainer`：

- `WorldUnderstandingMemoryMaintainer`
- `FirstPersonAutobiographyMemoryMaintainer`

它们内部组合 `CompletionMemoryBlockMaintainer`，提供预设 `Id`、`Target`、system prompt、user prompt，并把模型常见的单一外层 Markdown code fence 归一化掉。

## 6. Backtest CLI 的角色

Backtest CLI 继续作为调 prompt 的实验台。后续命令可以支持：

```text
--preset rolling-summary
--preset world-understanding
--preset first-person-autobiography
```

第一轮仍然可以单 block 回测，等两个 block prompt 稳定后，再让 runner 并行运行两个 maintainer，并在 JSONL 中分别记录 old/new preview、call log 和状态。

## 7. 后续问题

- World block 和 Autobiography block 是否需要互相引用源片段 anchor？
- 自传 block 是否应拆成 `episodes.seeds`、`relationship-with-liu`、`body-and-place` 等更细 block？
- 哪些内容属于受保护核心，需要主意识确认后才能写入 System？
- Dynamic recall 的 indexer 应该从 block 文本抽索引，还是直接从原始 archive span 抽索引？

当前先保持两个 block，避免内容层过早碎片化。
