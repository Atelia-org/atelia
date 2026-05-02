# Agent.Core — Memory Notebook

> **用途**：供 AI Agent 在新会话中快速重建对 `prototypes/Agent.Core` 的整体认知。
> **原则**：只记当前主线设计、已落地决策与高风险边界，不复述代码细节。
> **最后更新**：2026-04-22

---

## 一句话定位

Agent.Core 是 Atelia 智能体的**推理循环编排器**：

- 维护一个内存中的工作记忆（`AgentState.RecentHistory`）
- 把 LLM 流式输出累积成 `ActionEntry`、把工具调用结果包装成 `ToolResultsEntry`
- 通过 `IApp` / `ITool` 把外部能力挂载到 LLM 可见的工具集
- 自身不持有持久化机制，定位是"内存级状态机 + 可被 host 驱动的单步推理引擎"

它跑在 `Completion.Abstractions` 之上，被 `LiveContextProto` 和 `DebugApps` 这类宿主消费。

---

## 当前主线决策

### 1. 推理循环是单步驱动的状态机

`AgentEngine.StepAsync()` 的核心是按当前 `AgentRunState` 决定下一步：

- `WaitingInput` → 触发事件，由宿主 push 一条 `ObservationEntry`
- `PendingInput` / `PendingToolResults` → 调 LLM，累积 `ActionEntry`
- `WaitingToolResults` → 逐个跑 ToolCall，攒进 `_pendingToolResults`
- `ToolResultsReady` → 把缓存合成一条 `ToolResultsEntry`，回到 PendingToolResults

不变量：**Observation/Action/ToolResults 必须严格交替**，由 `AgentState.ValidateAppendOrder()` 在追加时拦截。

### 2. AgentState 是纯内存模型，没有持久化

当前 `AgentState` 只有：

- `_recentHistory: List<HistoryEntry>` — 活跃工作记忆
- `_pendingNotifications: Queue<...>` — 待附加到下条 Observation 的通知
- `_pendingToolResults: Dictionary<...>` — 还没收齐的工具结果

`HistoryEntry` 派生类按 `Kind` 分（Observation / Action / ToolResults / Recap），每条带 `Serial`（递增编号）和 `EstimatedTokens`，但**没有任何 disk-backing**。重启即丢失。

> **集成 StateJournal 的关键挑战在这里**：`HistoryEntry` 是 sealed 派生类多态，需要先把它建模为可序列化的形态（如 enum kind + DurableDict<int, ValueBox>）才能落到 StateJournal。

### 3. RecapBuilder / RecapEntry 已搭骨架但流程未闭环

- `RecapEntry` 类型已定义（带 `InsteadSerial` 字段，标识它替代哪些旧条目）
- `RecapBuilder` 提供 `TryDequeueNextPair()` / `UpdateRecap()` 等 API
- 强制约束：**至少保留一对 Action/Observation**，防止把工作记忆压缩光
- 但谁来调度 RecapBuilder、何时触发摘要生成（`RecapMaintainer`）尚未实现

### 4. IApp 是工具的容器，不是消息源

- `IApp.Tools` 列出该 App 暴露的 `ITool` 实例
- `IApp.RenderWindow()` 返回一段 markdown，被 `DefaultAppHost.RenderWindows()` 拼到 `# [Window]` 区块塞进 LiveContext
- App 不能主动 push Observation，所有外部输入都走宿主 → `AppendObservation`
- `MethodToolWrapper.FromMethod/FromDelegate` 把标 `[Tool]` 的方法反射成 `ITool`，这是注册工具的主路径

### 5. 工具参数类型受限于 MethodToolWrapper

当前只支持 `bool/int/long/float/double/decimal/string` 及其 nullable 形式，外加末尾必须有 `CancellationToken`。

> 复杂类型（数组、嵌套对象）需要走自定义 `ITool` 实现，绕开 wrapper。

### 6. LevelOfDetailContent：Basic/Detail 全量替代

- 不是 diff/增量，而是两份**完全独立**的渲染版本
- 用于通知、工具结果、窗口等场景的"详略可切换"
- `ProjectInvocationContext()` 时按当前策略选 Basic 或 Detail

### 7. 事件系统允许宿主在关键节点插桩

`AgentEngine` 暴露一组更收敛的扩展点，供宿主参与输入推进、profile 决议、请求前准备与状态观察：

- `WaitingInput` — 当前状态为等待输入，宿主应决议本轮的 `IncomingObservation`
- `ResolveProfile` — 在构造模型请求前决议本次调用的最终 `LlmProfile`
- `PrepareInvocationAsync` — 在最终 profile 已确定后、请求构造前刷新本轮所需的外部状态
- `StateTransition` — `AgentRunState` 变更时触发

设计契约已按阶段收紧：profile 只能在 `ResolveProfile` 阶段决议；`PrepareInvocationAsync` 是唯一正式的 awaited freshness gate；其余模型调用与工具执行细节重新收回为引擎内部实现。

---

## 核心数据流（一次完整 turn）

```text
1. 宿主调 StepAsync()
   ├─ 状态 = WaitingInput → 触发 WaitingInput 事件
   └─ 宿主提交 `IncomingObservation`（显式 ObservationEntry 或 recent events 草案）

2. 状态 = PendingInput → 准备 LLM 调用
   ├─ 触发 ResolveProfile（宿主可决议最终 profile）
   ├─ 按最终 profile 做 soft cap 判定
   ├─ `await PrepareInvocationAsync(...)` 刷新本轮所需 snapshot / tool visibility
   ├─ ProjectInvocationContext(options) 把 RecentHistory 投影成 IHistoryMessage 序列
   │    · 拆分为 StablePrefix + ActiveTurnTail（以最近一条 Observation 为分界）
   │    · ActionEntry 的有序内容以 `Blocks: IReadOnlyList<ActionBlock>` 为唯一真相
   │    · `ActionBlock.Thinking` 仅在 ActiveTurnTail 内、`Origin == TargetInvocation`、
   │      且存在显式 Turn 起点与 `ThinkingMode == CurrentTurnOnly` 同时成立时保留
   ├─ 拼上 DefaultAppHost.RenderWindows() 生成的窗口块
   ├─ ToolExecutor.GetVisibleToolDefinitions() 收集可见工具
   └─ 直接构造 CompletionRequest 并发起模型调用

3. `await ICompletionClient.StreamCompletionAsync(request)`
   ├─ Completion 层内部消费 provider 流并聚合
   ├─ 产出 `CompletionResult`（Message.Blocks + Invocation + Errors）
   └─ AgentEngine 包装为 ActionEntry → AppendAction()

4. 若 ToolCalls 空 → 状态回 WaitingInput
   否则 → 状态 = WaitingToolResults
   ├─ 逐个 FindNextPendingToolCall()
   ├─ ToolExecutor.ExecuteAsync(call) → 结果存 _pendingToolResults
   └─ 等待所有结果就绪

5. 收齐后 → 状态 = ToolResultsReady
   └─ 合成 ToolResultsEntry → AppendToolResults()
   → 状态 = PendingToolResults，回到 2 开新一轮 LLM 调用
```

---

## 目录结构速览

```text
prototypes/Agent.Core/
├─ AgentEngine.cs              中央状态机（StepAsync 主循环）
├─ AgentPrimitives.cs          AgentRunState 等枚举
├─ IApp.cs / ITool.cs          应用与工具接口
├─ MethodToolWrapper.cs        反射工具包装（公开 API）
├─ MethodToolWrapper.Impl.cs   反射实现细节
├─ LevelOfDetailContent.cs     Basic/Detail 双层内容
├─ LlmProfile.cs               Client + ModelId 配置对象
│
├─ App/
│  └─ DefaultAppHost.cs        App 注册容器 + RenderWindows 拼装
│
├─ History/
│  ├─ AgentState.cs            ⭐ 工作记忆主体
│  ├─ HistoryEntry.cs          抽象基类 + 4 种派生（ObservationEntry 非 sealed，可被扩展；其余均为 sealed）
│  ├─ AgentEngine.cs / ICompletionClient.StreamCompletionAsync 聚合结果 → CompletionResult / ActionEntry
│  ├─ RecapBuilder.cs          摘要工作面（流程未闭环）
│  ├─ TokenEstimateHelper.cs   Token 估算（硬编码系数）
│  └─ ITokenEstimator.cs       Token 策略接口
│
├─ Tool/
│  ├─ ToolExecutor.cs          执行调度 + 可见性管理
│  ├─ ToolContracts.cs         ITool → ToolDefinition 转换
│  └─ LodToolCallResult.cs     工具结果 LOD 包装
│
└─ Text/
   ├─ IBlockizer.cs            文本分块策略接口
   ├─ DefaultBlockizer.cs      按 \n 分块
   ├─ MarkdownBlockizer.cs     基于 Markdig 的语义分块
   └─ TextRenderer.cs          (待考察)
```

---

## 已知边界与高风险点

### 持久化空白

- `AgentState` 全在 RAM，宿主重启即丢
- `RecapEntry` 的 `InsteadSerial` 字段为持久化预留，但还没有实际写入磁盘的代码路径
- 集成 StateJournal 时需先解决 `HistoryEntry` 多态序列化问题（动态语言风格的 type-tag + DurableDict 是当前推荐路径）

### 非线程安全

- `AgentEngine` / `AgentState` 假设单线程驱动
- 多 Agent 并行需要每个 Agent 独立实例

### Token 估算精度

- `TokenEstimateHelper` 用硬编码字符系数估算
- 与各 Provider 真实 token 计数有偏差
- RecapBuilder 的触发阈值最终依赖此估算，长期需要校准

### 工具参数类型受限

- `MethodToolWrapper` 仅支持基本标量 + string
- 复杂参数（数组、对象、union）需要自己实现 `ITool`，绕开 wrapper
- LLM JSON 没有 uint，所以 ID 类参数要用 `long` + `checked((uint)x)` 的模式

### App.RenderWindow 渲染契约

- 渲染叠加层 vs 正文的视觉区分非常关键（LLM 容易把 `[id]`/`│` 等元数据列误当成正文）
- 当前最佳实践：用 ` │ ` (U+2502) 做列分隔，并在 Window 顶部明确说明格式约定
- 详见 [memories/repo/durable-text-llm-experiment.md](../../memories/repo/durable-text-llm-experiment.md)

---

## 与邻居项目的关系

```text
LiveContextProto (Console 宿主)
    ↓ uses
DebugApps (pmux peer 等)
    ↓ uses
Agent (CharacterAgent 等高层封装)
    ↓ uses
Agent.Core (本项目) ← StateJournal (待集成)
    ↓ uses
Completion + Completion.Abstractions
```

- **Agent.Core 不直接依赖 StateJournal**——这是有意的隔离，便于先把推理循环跑稳
- **Agent**（不带 Core 后缀）项目封装 `CharacterAgent` 等更高层模式，以及 `DurableTextApp` 等示例 IApp

---

## 调研中发现的设计悬而未决处

1. **RecapMaintainer 由谁触发**：是独立后台任务、子 Agent，还是 Engine 内置？
2. **HistoryEntry 持久化的离散方案**：动态类型语言的"type tag + dict" vs StateJournal 的强类型 DurableDict——选择会影响整个 Agent.Core 的演进方向

> 以下几项在调研过程中发现**代码中已经落地**，列在此处供后续读者快速校准：
>
> - **多 LLM 切换策略**：`StepAsync(profile)` 入参允许每步传入 `LlmProfile`，但**仅允许在 Turn 起点切换**（见下节）
> - **App 生命周期**：`RemoveApp()` 已存在；移除后新发起的工具调用会被拒为 `ToolNotRegistered`，进行中的不受影响

---

## Turn 与 LlmProfile 锁定（硬约束）

**不变量**：`AgentState.RecentHistory` 中从最近一条 `ObservationEntry`（含）起到末尾的连续段构成一个 **Turn**。同一个 Turn 内的所有模型调用必须使用同一个 `LlmProfile`，按 `CompletionDescriptor` 三元组（`ProviderId` / `ApiSpecId` / `Model`）严格比对。

**判定**：
- **Turn 起点**：精确匹配 `HistoryEntryKind.Observation` 的条目。
  - 注意 `ToolResultsEntry` 虽继承自 `ObservationEntry`，但 `Kind == ToolResults`，**不**视为 Turn 起点（它是 Turn 中段的环境反馈）。
- **是否锁定**：从末尾向起点反扫，若沿途出现至少一条 `ActionEntry`，则该 Turn 已锁定到该 `ActionEntry.Invocation`。
- **校验时机**：在 `ResolveProfile` 阶段结束后，仅对最终决议出的 profile 执行一次校验。
- **违反后果**：抛 `InvalidOperationException`，错误消息包含锁定信息与传入信息，便于排查。

**为何如此设计**：
- 主流闭源模型（GPT-5、Gemini 2.5/3.0）的 thinking/reasoning 内容是**加密**的，跨模型不可解码。Turn 锁定保证后续在 Turn 范围内注入加密 thinking 时，下一次调用仍在同一 model，可以原样回灌。
- 切 profile 的语义被收敛到"开新 Turn"——也就是必须先有新的 `ObservationEntry`（用户输入或工具结果之外的环境观测）。
- **不**提供 escape hatch：远程故障时宁可让调用方显式开新 Turn 重启，也不引入"尽力而为解码 + 模型兼容性矩阵"的隐式复杂性（详见 `gitignore/` 或后续 PR 讨论）。

**接口形态保留**：`StepAsync(profile, ct)` 签名不变；profile 仍按每步传入。仅在违反不变量时报错——这样最小化改动现有调用点。

**实现位置**：`prototypes/Agent.Core/History/TurnAnalyzer.cs` 负责纯函数式 Turn 分析；`prototypes/Agent.Core/AgentEngine.cs` 中 `EnsureProfileMatchesCurrentTurnLock(profile)` 负责执行业务约束。

**测试**：`tests/Atelia.LiveContextProto.Tests/AgentEngineTurnLockTests.cs` 覆盖 6 个场景：首次调用、同 turn 工具往返同 profile、同 turn 切 ModelId、同 turn 切 ProviderId、跨 turn 切换、`ResolveProfile` handler 篡改 profile。

**后续依赖此约束的 PR**：
- ✅ `ActionBlock.Thinking` 端到端落地（Anthropic extended thinking）——`ActionBlock` 已上提到 `Completion.Abstractions`，`ActionEntry.Message.Blocks` 为唯一真相，`ActionMessage.GetFlattenedText()`/`ToolCalls` 作为 lossy derived view。
- ✅ MessageConverter 投影时按 turn 边界裁剪 thinking blocks：`AgentState.ProjectInvocationContext` 只在 ActiveTurnTail 中按 4 个条件（`isInActiveTurn` / `TargetInvocation != null` / `HasExplicitStartBoundary` / `ThinkingMode == CurrentTurnOnly`）且 `Origin == TargetInvocation` 保留，Stable Prefix 始终剩离。
- ✅ OpenAI reasoning_content 双轨并行：本轮仅实现 Anthropic；OpenAI Chat Completions 仍丢弃 reasoning，留待后续多-provider 阶段。

实现与设计详见 [docs/Agent/Thinking-Replay-Design.md](Thinking-Replay-Design.md) 与 [docs/Agent/Thinking-Replay-Implementation-Plan.md](Thinking-Replay-Implementation-Plan.md)。

---

## 相关文档

- [docs/StateJournal/memory-notebook.md](../StateJournal/memory-notebook.md) — 持久化层的对应概览
- [docs/LiveContextProto/](../LiveContextProto/) — 历史设计笔记，部分已被现行实现覆盖
- [docs/Agent/ResidentCodebaseAgents.md](../Agent/ResidentCodebaseAgents.md) — 长期驻留 Agent 的高层愿景
