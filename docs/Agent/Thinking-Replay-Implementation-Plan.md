# Thinking Replay 实施计划稿

**状态**：Draft v0.1
**前置设计**：[Thinking-Replay-Design.md](./Thinking-Replay-Design.md)
**实施原则**：

- 小 PR、强验收、可回滚
- 单 PR 单主刀，另一方专职审阅
- 先做结构重构，再做 thinking 端到端
- 不在同一批核心类型上并行编码

---

## 0. 总体策略

### 0.1 目标拆分

按风险和语义复杂度，把实施拆成 5 个 PR：

1. `ActionEntry.Blocks` 存储重构，不含 Thinking
2. invocation-specific projection 接口升级
3. `CompletionChunk.Thinking` + Anthropic StreamParser 接入
4. thinking 投影过滤 + Anthropic replay 回灌
5. 文档收口 + Recap/兼容性补强

### 0.2 协作模式

建议采用“轮流主刀 + 对方审阅”的模式：

- PR 1：Claude 主刀，GPT-5 审阅
- PR 2：GPT-5 主刀，Claude 审阅
- PR 3：视 PR 1/2 落地质量决定；默认 Claude 主刀，GPT-5 审阅
- PR 4：建议 GPT-5 主刀，Claude 审阅
- PR 5：任一方主刀均可，另一方快速审阅

### 0.3 为什么不并行分头施工

- `ActionEntry` / `ProjectInvocationContext` / converter 路径是高耦合核心面
- 并行修改会显著增加 merge 风险与“没人做整体 sanity check”的概率
- 当前阶段最大的风险不是编码速度，而是边界走样与隐性回归

---

## 1. PR 1：Blocks 存储重构（不含 Thinking）

### 1.1 负责人

- **主刀**：Claude
- **审阅**：GPT-5

### 1.2 目标

把 `ActionEntry` 的内部真相表示从：

- `Content + ToolCalls + Invocation`

升级为：

- `Blocks + Invocation`

同时保持现有语义与测试行为不变，不引入 thinking 相关逻辑。

### 1.3 改动范围

核心文件预估：

- `prototypes/Agent.Core/History/HistoryEntry.cs`
- `prototypes/Agent.Core/History/CompletionAccumulator.cs`
- 相关测试文件：
  - `tests/Atelia.LiveContextProto.Tests/AgentStateMachineToolExecutionTests.cs`
  - `tests/Atelia.LiveContextProto.Tests/AgentStateRecapTests.cs`
  - `tests/Atelia.LiveContextProto.Tests/RecapBuilderTests.cs`
  - message converter 测试里手写 `ActionEntry(...)` 的部分

### 1.4 具体任务

1. 新增 `ActionBlock` sum type，仅含：
   - `Text`
   - `ToolCall`
2. `ActionEntry` 主构造切换为：
   - `IReadOnlyList<ActionBlock> Blocks`
   - `CompletionDescriptor Invocation`
3. 保留旧构造重载：
   - `string content`
   - `IReadOnlyList<ParsedToolCall> toolCalls`
   - `CompletionDescriptor invocation`
4. `Content` / `ToolCalls` 改为派生视图
5. `CompletionAccumulator` 内部改为按 `ActionBlock` 累积
6. 所有现有测试调通，不引入 Thinking 相关抽象

### 1.5 明确不做

- 不引入 `ThinkingBlock`
- 不改 `ProjectContext` / `ProjectInvocationContext`
- 不改 converter 的 rich path
- 不改 `CompletionChunk`

### 1.6 主要风险

- `Content` compatibility view 语义悄悄变化
- 手写 `ActionEntry` 的测试构造器批量失配
- `CompletionAccumulator` 文本 flush 行为变化，导致 tool call 前后的内容拼接回归

### 1.7 审阅重点

GPT-5 审阅时重点检查：

1. `Content` 是否严格是 `string.Concat(text blocks)`，没有额外注入换行
2. `ToolCalls` 是否严格来自 `ToolCall` blocks，顺序不变
3. 旧构造重载是否只作为 compatibility convenience，不引入第二真相源
4. `CompletionAccumulator` 在 text/toolcall 交错时是否保持旧行为
5. 所有 `ActionEntry` 测试构造是否已迁移到稳定形态

### 1.8 验收命令

```bash
dotnet test tests/Atelia.LiveContextProto.Tests/Atelia.LiveContextProto.Tests.csproj
```

如涉及更广项目引用，再补：

```bash
dotnet build Atelia.sln
```

### 1.9 合并条件

- 全量测试绿
- 无 thinking 相关半成品接口残留
- 设计文档中的 PR 1 范围未被越界扩大

---

## 2. PR 2：富接口与 invocation-specific projection

### 2.1 负责人

- **主刀**：GPT-5
- **审阅**：Claude

### 2.2 目标

把上下文投影从单一扁平列表升级为：

- `ProjectedInvocationContext { StablePrefix, ActiveTurnTail }`

并统一入口为：

- `AgentState.ProjectInvocationContext(ContextProjectionOptions options)`

此阶段仍**不含 Thinking 行为变化**，只是铺接口与切分结构。

### 2.3 改动范围

核心文件预估：

- `prototypes/Agent.Core/History/AgentState.cs`
- `prototypes/Completion.Abstractions/IHistoryMessage.cs`
- 新增 projection 相关类型（建议放 `prototypes/Agent.Core/History/`）
- `prototypes/Agent.Core/AgentEngine.cs`
- 使用旧 `ProjectContext` 的调用点与测试

### 2.4 具体任务

1. 新增 `IRichActionMessage`，放在 `Completion.Abstractions`
2. 新增 `ProjectedActionMessage`
3. 新增：
   - `ContextProjectionOptions`
   - `ThinkingProjectionMode`
   - `ProjectedInvocationContext`
4. 实现 `AgentState.ProjectInvocationContext(options)`
5. 删除旧：
   - `AgentState.ProjectContext(string? windows = null)`
   - `AgentEngine.ProjectContext()`
6. 所有调用站点改用：
   - `ProjectInvocationContext(...).ToFlat()`
7. 此阶段 `StablePrefix` / `ActiveTurnTail` 使用相同过滤规则，不引入 Thinking

### 2.5 明确不做

- 不引入 `CompletionChunkKind.Thinking`
- 不改 Anthropic parser
- 不实现 thinking 条件保留

### 2.6 主要风险

- 删除旧 API 时漏改调用点
- `TargetInvocation = null` 场景语义没有在实现里真正统一
- `StablePrefix / ActiveTurnTail` 切分点与 `TurnAnalyzer` 语义不一致

### 2.7 审阅重点

Claude 审阅时重点检查：

1. 依赖方向是否仍然健康
2. `TargetInvocation = null` 是否确实是一等公民语义，而非兼容层残影
3. `StablePrefix / ActiveTurnTail` 的分割是否完全由 Turn 起点决定
4. `ProjectedActionMessage` 是否统一用于两段投影，而不是让输出类型分裂
5. 所有旧 `ProjectContext` 路径是否已清理干净

### 2.8 建议新增测试

- `StablePrefix / ActiveTurnTail` 切分边界正确
- `TargetInvocation = null` 时 `.ToFlat()` 结果不含 thinking 且与旧行为一致
- 空 history 时两段都为空

### 2.9 验收命令

```bash
dotnet test tests/Atelia.LiveContextProto.Tests/Atelia.LiveContextProto.Tests.csproj
```

如 converter 路径改动较大，再补：

```bash
dotnet build Atelia.sln
```

---

## 3. PR 3：Thinking chunk 接入 + Anthropic StreamParser

### 3.1 负责人

- **建议主刀**：Claude
- **建议审阅**：GPT-5

### 3.2 目标

让 Anthropic 的 thinking 内容能从流式响应进入：

- `CompletionChunkKind.Thinking`
- `ThinkingChunk`
- `ActionBlock.Thinking`

但此阶段还不做 replay 回灌。

### 3.3 改动范围

核心文件预估：

- `prototypes/Completion.Abstractions/CompletionChunk.cs`
- `prototypes/Agent.Core/History/CompletionAccumulator.cs`
- `prototypes/Completion/Anthropic/AnthropicStreamParser.cs`
- `tests/Atelia.LiveContextProto.Tests/AnthropicStreamParserTests.cs`
- 相关端到端测试或新增测试

### 3.4 具体任务

1. `CompletionChunkKind` 新增 `Thinking`
2. 新增 `ThinkingChunk`
3. `CompletionAccumulator` 处理 `Thinking` case，构造 `ActionBlock.Thinking`
4. `AnthropicStreamParser` 聚合：
   - `content_block_start`
   - `content_block_delta(thinking_delta)`
   - `content_block_stop`
5. 生成 provider-native `OpaquePayload`
6. 可选附带 `PlainTextForDebug`

### 3.5 明确不做

- 不改 `ProjectInvocationContext` 的 filtering 规则
- 不改 `AnthropicMessageConverter` 回灌

### 3.6 主要风险

- Anthropic thinking block 聚合不完整
- `OpaquePayload` 不是可稳定 round-trip 的 provider-native 形态
- `CompletionAccumulator` 的 ordering 保真出错

### 3.7 审阅重点

GPT-5 审阅时重点检查：

1. `OpaquePayload` 是否由 parser 直接构造，Accumulator 完全透明
2. block 完结时机是否正确，不会丢 `signature`
3. `text -> thinking -> text` 顺序是否保真
4. 若 parser 失败，错误是否足够可诊断

### 3.8 建议新增测试

- thinking SSE 事件聚合为完整 JSON payload
- `ActionEntry.Blocks` 中出现 `ThinkingBlock`
- `Content` compatibility view 不包含 thinking 文本

### 3.9 验收命令

```bash
dotnet test tests/Atelia.LiveContextProto.Tests/Atelia.LiveContextProto.Tests.csproj --filter Anthropic
dotnet test tests/Atelia.LiveContextProto.Tests/Atelia.LiveContextProto.Tests.csproj
```

---

## 4. PR 4：Thinking 投影过滤 + Anthropic replay 回灌

### 4.1 负责人

- **建议主刀**：GPT-5
- **建议审阅**：Claude

### 4.2 目标

闭环实现：

- 当前 Turn 内
- invocation 严格匹配
- 显式起点完整

时，Anthropic extended thinking 能被回灌。

### 4.3 改动范围

核心文件预估：

- `prototypes/Agent.Core/History/AgentState.cs`
- projection 相关新增类型
- `prototypes/Completion/Anthropic/AnthropicMessageConverter.cs`
- `tests/Atelia.LiveContextProto.Tests/AnthropicMessageConverterTests.cs`
- 新增投影层单测与端到端测试

### 4.4 具体任务

1. 在 `ProjectInvocationContext` 中落地 thinking 过滤规则表
2. `StablePrefix` 始终过滤 thinking
3. `ActiveTurnTail` 仅在 4 条件同时满足时保留 thinking
4. `AnthropicMessageConverter` 识别 `IRichActionMessage`
5. 反序列化 `ThinkingBlock.OpaquePayload` 回 native content block
6. 不支持/反序列化失败时 fail-fast

### 4.5 主要风险

- `TargetInvocation == null` 场景下错误保留 thinking
- `HasExplicitStartBoundary == false` 时仍误回灌
- converter 中 mixing rich path / fallback path 导致重复编码或丢块

### 4.6 审阅重点

Claude 审阅时重点检查：

1. §4 投影规则表是否逐项落到代码
2. `StablePrefix` 与 `ActiveTurnTail` 的 thinking 过滤是否完全一致于文档
3. Anthropic converter 的 native block 重建是否确实 round-trip
4. fail-fast 诊断是否足够明确

### 4.7 建议新增测试

- 旧 turn thinking 被剥离
- 当前 turn 但 origin 不匹配时被剥离
- `TargetInvocation == null` 时被剥离
- recapped 边界下被剥离
- 所有条件满足时被保留
- Anthropic round-trip：第一次 thinking 在第二次请求中出现

### 4.8 验收命令

```bash
dotnet test tests/Atelia.LiveContextProto.Tests/Atelia.LiveContextProto.Tests.csproj --filter "Anthropic|Turn|Projection"
dotnet test tests/Atelia.LiveContextProto.Tests/Atelia.LiveContextProto.Tests.csproj
```

---

## 5. PR 5：文档收口 + Recap/兼容性补强

### 5.1 负责人

- **主刀**：任一方均可
- **审阅**：另一方快速审阅

### 5.2 目标

完成正式文档落地，并确保 Recap 路径与 Blocks/Thinking 不冲突。

### 5.3 改动范围

- `docs/Agent/memory-notebook.md`
- `docs/Completion/memory-notebook.md`
- `docs/Agent/Thinking-Replay-Design.md`
- 若需：
  - `RecapBuilder`
  - `AgentState` Recap 相关测试

### 5.4 具体任务

1. 在 memory notebook 文档中增加 cross reference
2. 明确 `Recap` 当前通过 `Content` compatibility view 工作
3. 记录 v1 边界与已知开放点
4. 如有必要，补充 Recap 兼容测试

### 5.5 验收命令

```bash
dotnet test tests/Atelia.LiveContextProto.Tests/Atelia.LiveContextProto.Tests.csproj
```

### 5.6 落地结果（PR 5 完成）

- ✅ `docs/Agent/memory-notebook.md`：核心数据流改写为 `ProjectInvocationContext`，新增 ActionEntry.Blocks 源 + Thinking 投影 4 条件描述；Turn 锁定小节"后续依赖此约束的 PR"标记为已落地（Anthropic 端到端、ProjectInvocationContext 过滤），并指明 OpenAI reasoning_content 仍未实现。
- ✅ `docs/Completion/memory-notebook.md`：第 1/2/7 节扩展（IRichActionMessage/ActionBlock sum type、CompletionChunkKind.Thinking、Anthropic SSE 表新增 thinking/signature 处理），最后边界小节明确 `ActionBlock.Thinking.Origin` 与 Turn lock 同构 + ProjectInvocationContext 过滤策略。
- ✅ `docs/Agent/Thinking-Replay-Design.md`：D9 已在 PR 2.5 修订；本 PR 不需进一步改动。
- ✅ `prototypes/Agent.Core/History/RecapBuilder.cs:49`：cref 由 `AgentState.ProjectContext` 修正为 `AgentState.ProjectInvocationContext`。
- ✅ Recap 路径兼容性已确认：`RecapBuilder.ExtractRecapText` 只读 `RecapEntry.Content` / `ObservationEntry.Notifications.Detail`，不触碰 `ActionEntry` 内容；`BuildActionObservationPairs` 仅按交替顺序成对消费 entry 引用，对内部 Blocks 透明。**结论：Recap 无需新增兼容代码或测试。**
- ⚠️ **已知 v1 边界（暂不修，记录在此）**：`TokenEstimateHelper.EstimateAction` 使用 lossy compat view `action.Content`，**不计入 Thinking blocks 的 token 成本**。对当前 turn 注入 thinking 时实际 prompt 体积会显著高于估算。后续校准 token 估算时需同步增加 `Blocks` 中 `Thinking.PlainTextForDebug` 长度（或更精确的 OpaquePayload 体积估算）。


---

## 6. 统一协作规则

每个 PR 开工前，主刀方应先写一个非常短的“施工意图”：

- 本 PR 的目标
- 明确不做什么
- 预计修改的 3 到 8 个文件

每个 PR 提交审阅前，主刀方应自检：

1. 是否越过了本 PR 的边界
2. 是否引入了半成品 API
3. 是否已有最小测试覆盖新增语义
4. 是否所有设计文档里的关键术语仍与实现一致

审阅方应优先抓：

1. 依赖方向
2. API 对称性
3. 隐性 compatibility drift
4. 文档规则是否真的落地为代码

---

## 7. 建议给 Claude 的开工消息

建议转发给 Claude 的简版消息如下：

> 设计已收束，准备按实施计划推进。
> 先从 PR 1 开始：`ActionEntry.Blocks` 存储重构，不含 Thinking。
> 你主刀，我这边由 GPT-5 做专职审阅。
> 请严格控制边界：只做 `ActionBlock(Text/ToolCall)`、`ActionEntry` 主构造切换、旧构造重载保留、`Content/ToolCalls` 派生视图、`CompletionAccumulator` 内部迁移，以及必要测试迁移；不要提前引入 Thinking / projection / rich message。
> 交付标准是全量测试零回归。

---

## 8. 当前建议

如果用户拍板，直接开工：

- **先做 PR 1**
- PR 1 合并或稳定后再开 PR 2
- 不建议 PR 1 和 PR 2 并行

这是当前风险最低、信息增量最高的推进顺序。
