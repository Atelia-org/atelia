# Galatea Memory Substrate 实现交接提示

> 用途：给全新 coding agent 会话快速加载背景、目标、关键文件和施工边界。本文面向“具体实现 `memory-substrate-engineering.md`”这件事，不展开 Galatea 心智理论和 Memory Pack 内容分类。

> **归档说明（2026-07-22）**：该交接任务已经完成，下面的提示词描述的是施工前状态，不应再次执行。现行 substrate 与 Rewrite-only 决策见 `prototypes/ChatSession/MemorySubstrate.cs` 和 `docs/Galatea/memory-maintainer-slimming-refactor.md`。

## 1. 可直接复制给新会话的用户提示词

```text
请在 /repos/focus/atelia 中实现 Galatea Memory Substrate 的第一阶段工程能力。

先阅读这些文件：

1. docs/Galatea/memory-substrate-engineering.md
   - 这是本次要实现的主设计文档。
   - 重点是内容无关的软件工程 substrate：RecentHistorySlice、IRecentHistoryAnalyzer、IMemoryBlockMaintainer、MemoryPack、MemoryPackDraft、渲染/投影。

2. docs/Galatea/memory-content-research-notes.md
   - 只作为背景，里面的心智理论、Belief modeling、Reabsorb、动态召回、mainline notice 都不要在本次实现里展开。

3. prototypes/ChatSession/ChatSessionContracts.cs
   - 查看当前 ContextHeader、ObservationMessage、ActionMessage、IMemoryMaintainerAgent 等现有合同。
   - 注意 ContextHeader 当前应使用领域术语：SystemPromptFragment、ObservationMessage、ActionMessage。

4. prototypes/ChatSession/ChatSessionEngine.Compaction.cs
   - 查看现有 RunMemoryMaintainersAsync(...)、FindHalfContextSplitPoint(...)、tool-loop 相关实现。
   - 本次可以复用其思路，但不要把 Galatea 内容分类写进 ChatSession。

5. tests/FamilyChat.Server.Tests/ChatSessionQuickStartSamplesTests.cs
   - 查看现有 ChatSession substrate 测试风格、ScriptedCompletionClient、demo tool、ContextHeader 测试。

目标：

- 添加内容无关的 Memory substrate 类型和最小编排能力。
- 这些工程 substrate 类型放在 `Atelia.ChatSession` / `prototypes/ChatSession`，因为它已经是 Galatea 与其他上层应用共用的会话基础。
- Galatea 后续只定义具体 Memory Pack key、维护策略、提示词和 UI 行为。
- 不要实现具体 Galatea Memory Pack 内容分类。
- 不要实现 Reabsorb、动态召回、自我一致性维护、mainline notice、主意识审议。
- 不要做 Revision 级 StateJournal branch fork。
- Maintainer 对调用方必须无副作用：输入旧 block + recent history，返回新版 block；调用方再决定是否写入 draft。
- 现有 `IMemoryMaintainerAgent` / `MemoryMaintenanceRequest` / `MemoryMaintenanceResult` 属于早期概念验证遗留，不应继续扩张成并列框架；实现时优先把已有 split、并行 maintainer、tool-loop 和 duplicate writer 检查融合到新 block-level substrate。
- 旧的 MaintainerAgent 与新文档里的 Maintainer 应合并成一个类型概念：一个可插拔、可单独测试、对调用方无副作用的 `IMemoryBlockMaintainer`。它输入 old block + recent history slice，输出 new block；编排器负责并行执行、汇总结果和可选 draft 应用。

建议第一阶段实现范围：

1. 在合适项目中定义纯工程类型：
   - ContextHeaderSnapshot
   - RecentHistorySlice
   - RecentHistoryAnalysisContext
   - IRecentHistoryAnalyzer
   - MemoryPack
   - MemoryPackBlock
   - MemoryPackBlockPath
   - RenderedMemoryPack
   - MemoryPackDraft
   - IMemoryBlockMaintainer
   - MemoryBlockMaintenanceRequest
   - MemoryBlockMaintenanceResult
   - MemoryMaintenanceNotice（若需要，可先极简）

   `IMemoryBlockMaintainer` 的统一接口保持最小：`Id`、`Target`、`MaintainAsync(...)`。旧概念中有价值的插件式结构保留在内置 LLM maintainer 实现里：`SystemPrompt` / `UserPrompt` / `ToolSession` 是该实现的配置，而不进入所有 maintainer 的共同接口。这样 fake、规则型 maintainer 和外部服务 maintainer 都能单独测试，不必依赖 tool abstraction。

2. MemoryPack 采用领域术语三载体：
   - System: OrderedDictionary<string, MemoryPackBlock>
   - Observation: OrderedDictionary<string, MemoryPackBlock>
   - Action: OrderedDictionary<string, MemoryPackBlock>

3. 不要引入 MemoryPackRole、MemoryPackSlot、MemoryPackChannel。
   - channel 由 MemoryPack.System / Observation / Action 字段位置表达。
   - 跨 channel 定位使用 MemoryPackBlockPath(MemoryPackCarrier Carrier, string BlockKey)。
   - Public API 用强类型 enum：MemoryPackCarrier.System / Observation / Action。
   - StateJournal / JSON 持久化边界显式转成稳定 string token，例如 `system` / `observation` / `action`；不要把 enum 直接塞进 StateJournal mixed value，也不要退回 provider API 的 user/assistant 命名。

4. 渲染规则：
   - 每个 block 渲染为：
     ## {key}

     {text}
   - 每个 channel 按 OrderedDictionary 顺序拼接。
   - RenderedMemoryPack 包含 SystemPromptFragment、ObservationMessage、ActionMessage。

5. 投影规则：
   - RenderedMemoryPack.SystemPromptFragment -> ContextHeader.SystemPromptFragment
   - RenderedMemoryPack.ObservationMessage -> ContextHeader.ObservationMessage
   - RenderedMemoryPack.ActionMessage -> ContextHeader.ActionMessage
   - MVP 阶段 `RenderedMemoryPack.ActionMessage` 先是 string：空字符串投影为 null；非空字符串投影为只包含一个 text block 的 ActionMessage。Completion 层多模态 Blocks/Parts 暂不进入 Memory substrate。

6. RecentHistorySlice 表示 analysis window：
   - ContextHeaderSnapshot PriorContext
   - IReadOnlyList<IHistoryMessage> Messages
   - string? SourceId
   - ulong? EstimatedTokens
   - PriorContext 使用 Empty object，不用 null 表示无前置上下文。

7. MemoryPackDraft 支持：
   - ReplaceBlock(MemoryPackBlockPath path, string newText)
   - UpsertBlock(MemoryPackBlockPath path, string text, int? order = null)
   - RemoveBlock(MemoryPackBlockPath path)
   - Build()

8. 添加聚焦测试：
   - MemoryPack 能按 System/Observation/Action 三 channel 保存和渲染 block。
   - 同一 channel key 不重复，顺序稳定。
   - MemoryPackDraft 替换/插入/删除不直接修改 base pack。
   - RecentHistorySlice 可携带 ContextHeaderSnapshot.Empty 或非空 prior context。
   - 一个 fake IMemoryBlockMaintainer 可输入 old block + recent history 并返回 new block。
   - 编排器若实现，必须拒绝同一 MemoryPackBlockPath 的多 writer。
   - maintainer target 对应 old block 不存在时，编排器传入空 MemoryPackBlock，并在应用结果时 upsert 创建 block。
   - 内置 LLM maintainer 若实现，应复用现有 tool-loop 行为：每个 maintainer 可有自己的 ToolSession，并在结果中保留 invocation、errors、ToolCallsExecuted。

验证：

- 优先跑最窄测试，例如相关 test class 或项目 filter。
- 然后跑受影响项目测试。
- 最后运行 git diff --check。

重要边界：

- 本次只做 substrate，不做 Galatea 内容分类。
- 不要把 core.beliefs、self.memoir、relation.laoliu 等具体 key 写入 substrate 类型或测试核心逻辑。
- 不要把 API role 的 user/assistant 作为领域模型字段名；内部使用 Observation/Action。
- 不要把现有 `MemoryMaintenanceRequest` / `MemoryMaintenanceResult` 原样当成最终设计继续加字段；它们需要和 `MemoryBlockMaintenanceRequest` / `MemoryBlockMaintenanceResult` 方向融合。
- 不要长期保留 `IMemoryMaintainerAgent` 与 `IMemoryBlockMaintainer` 两套并列概念；若短期为迁移保留旧类型，也应让它成为 adapter 或被新内置 LLM maintainer 吸收。
- 如果现有文件已经被用户或其他工具修改，先读当前内容，不要回滚。
```

## 2. 背景与动机

Galatea 的长期记忆系统原先被设计为完整的动态记忆维护系统，包含自我一致性、动态召回、Reabsorb、主意识审议、fork/snapshot 等概念。这个问题太大，第一阶段应切出纯软件工程 substrate：

- 能接收 recent history 做分析。
- 能维护一个旧版文本 block 并产出新版文本。
- 能用内容无关容器保存三载体 Memory Pack。
- 能把 Memory Pack 渲染/投影到现有 ChatSession `ContextHeader`。

内容理论问题已经移到 `docs/Galatea/memory-content-research-notes.md`，不阻塞第一阶段施工。

## 3. 当前命名决策

内部领域层使用 Atelia / Agent 术语：

| 领域名 | ChatSession 类型 | Provider API 边界 |
|---|---|---|
| System | `SystemPromptFragment` | system prompt |
| Observation | `ObservationMessage` | user role |
| Action | `ActionMessage` | assistant role |

不要在 substrate 内部使用 `User` / `Assistant` 作为模型字段名。它们只属于 provider 适配层或旧代码兼容层。

## 4. 关键文件

### 设计文档

- `docs/Galatea/memory-substrate-engineering.md`：本次主设计。
- `docs/Galatea/memory-content-research-notes.md`：内容理论研究备忘，只读背景。
- `docs/Galatea/mem-agents.md`：更大的 Galatea memory agents 设计草案，含历史讨论形成的长期方向。

### ChatSession 现有实现

- `prototypes/ChatSession/ChatSessionContracts.cs`
  - `ContextHeader`
  - `IMemoryMaintainerAgent`
  - `MemoryMaintenanceRequest`
  - `MemoryMaintainerResult`

- `prototypes/ChatSession/ChatSessionEngine.Compaction.cs`
  - `CompactAsync(...)`
  - `RunMemoryMaintainersAsync(...)`
  - `FindHalfContextSplitPoint(...)`
  - maintainer tool-loop 参考实现

- `prototypes/ChatSession/ChatSessionEngine.State.cs`
  - `SetContextHeader(...)`
  - session state / persistence 相关入口

- `prototypes/ChatSession/MessageRecord.cs`
  - ChatSession history message 持久化映射。

### 测试参考

- `tests/FamilyChat.Server.Tests/ChatSessionQuickStartSamplesTests.cs`
  - ContextHeader 投影测试。
  - RunMemoryMaintainersAsync 测试。
  - ScriptedCompletionClient / demo tool 参考。

- `tests/FamilyChat.Server.Tests/FamilyChatServerTests.cs`
  - ChatSession / FamilyChat 集成式测试样例。

## 5. 实现建议

第一阶段工程 substrate 直接放在 `prototypes/ChatSession` / `Atelia.ChatSession`，因为：

- 它已有 `ContextHeader`、`IHistoryMessage`、`ObservationMessage`、`ActionMessage`。
- 它已有 `RunMemoryMaintainersAsync(...)` 可作为编排参考。
- 它已经是 Galatea / FamilyChat 共享会话 substrate。

但要保持边界：ChatSession 层不应知道 Galatea 的具体 Memory Pack key 或心智内容分类。

### 5.1 已澄清但仍需实现时收口的点

- `MemoryMaintenanceRequest` / `MemoryMaintenanceResult` 是早期雏形；新实现应融合旧能力，而不是产生长期并列 API。融合方向是：`IMemoryBlockMaintainer` 成为唯一 maintainer 概念；编排器吸收旧 `RunMemoryMaintainersAsync(...)` 的 split、并行、tool-loop、duplicate writer 检查和审计字段。
- Action channel 的 Memory substrate MVP 使用 string；投影到当前 `ContextHeader.ActionMessage` 时再转为 text-only `ActionMessage`。
- `MemoryPackBlockPath` 使用 `MemoryPackCarrier` enum；持久化边界显式转 string token。StateJournal 当前不直接支持 enum mixed value，这不是阻碍，因为 ChatSession 现有 schema 也习惯用 string discriminator 落盘。
- maintainer target 对应 old block 不存在时，按空 block 处理并允许创建。这是新建 session 的正常路径。

## 6. 最小验收清单

- [ ] 类型能表达 `PriorContext + Messages` 的 analysis window。
- [ ] `ContextHeaderSnapshot.Empty` 可表示无前置上下文。
- [ ] `MemoryPack` 有 System / Observation / Action 三个有序字典。
- [ ] `MemoryPackBlock` 不携带 role/channel，自身只表达文本内容。
- [ ] `MemoryPackBlockPath` 只作为操作路径。
- [ ] `MemoryPackDraft` 不直接修改 base pack。
- [ ] `RenderedMemoryPack` 能投影到当前 `ContextHeader`。
- [ ] 测试不依赖 Galatea 具体内容分类。
- [ ] `git diff --check` 通过。

## 7. 暂缓事项

以下内容不要在第一阶段实现：

- 具体 Memory Pack 三层内容治理。
- `core.beliefs` / `self.memoir` / `relation.*` 等具体 key。
- Reabsorb。
- 动态记忆召回。
- 自我一致性维护。
- mainline notice。
- Conscious Review Job。
- StateJournal Revision 级 branch fork。
