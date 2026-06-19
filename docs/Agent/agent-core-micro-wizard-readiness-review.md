# Agent.Core Micro-Wizard Readiness Review

状态：draft v1  
范围：审阅当前 `prototypes/Agent.Core` 是否适合作为 `Micro-Wizard Runtime` 的施工地基，并给出建议的自底向上施工顺序。

## 0. 一句话结论

当前的 `Agent.Core` 已经不只是“适合做 wizard runtime 的地基”，而是已经具备了第一批真正可开工的基础插槽。它原本更偏向：

- 主会话历史推进
- 普通 tool loop
- context compaction 这类单点引擎能力

它还没有天然长成一个完整的 wizard runtime。  
不过好消息是：它已经有足够清晰的切入缝，可以在**不推倒主循环**的前提下，逐步长出 Micro-Wizard 所需的外围基础设施。

基于当前代码而不是更早的设想，我现在的判断是：

- **可以以 `Agent.Core` 为施工地基**
- **而且“事件钩子 + RecentHistory actor-side injection + phase 级 tool gating”这三类基础插槽已经基本到位**
- **但仍不应第一步就把“durable wizard recipe + instance + DSL 解释器”硬塞进引擎核心**
- **现在更稳的路线是：先在宿主侧真正做出一个薄的 `WizardOrchestrator`，用真实场景压 shape**

补一条现在需要显式写清的前提：

- **Micro-Wizard 作为高级认知运行时，默认建立在 `Agent.Core` 当前 full-feature-only runtime 之上**
- **也就是说，当前 `Agent.Core` 只接受 `SupportsAgentCoreFullFeatures == true` 的 profile**

## 1. 当前已经适合承载 Micro-Wizard 的部分

### 1.1 单步状态机边界已经比较清楚

`AgentEngine.StepAsync()` 的主循环已经把这些边界收拢出来了：

- `WaitingInput`
- `PendingInput`
- `WaitingToolResults`
- `ToolResultsReady`
- `PendingToolResults`

这意味着 wizard runtime 不必自己重造一套 LLM/tool 循环，只需要在这些边界上接管“何时注入阶段目标、何时监听事件、何时切换局部策略”。

### 1.2 `ResolveProfile -> PrepareInvocationAsync -> actual invocation` 这一段非常宝贵

这条链路已经提供了一个自然的“请求前裁决门”：

- `ResolveProfile` 负责确定本轮真实使用哪个 `LlmProfile`
- `PrepareInvocationAsync` 负责在请求构造前刷新外部状态

这很适合以后挂 wizard runtime，因为很多 wizard 行为本质都是：

- 在真正请求模型前，决定本轮看到什么
- 决定本轮允许用哪些工具
- 决定本轮附带哪种阶段性提示

而且现在这条链路已经多了一层实际可用的准入门：

- 当前 turn 的实际 profile 会先在 `ResolveProfile` 阶段决议
- `AgentEngine` 会在入口与 `ResolveProfile` 结果上强校验 `SupportsAgentCoreFullFeatures`
- 因此 wizard runtime 已不必再为 non-full-feature profile 设计运行时分叉

这意味着 wizard runtime 已经不必自己额外发明一套 capability policy。

### 1.3 `ToolRegistry + ToolSession` 已经把工具定义和会话态分开了

这对 wizard 很关键。

因为 wizard 真正需要的往往不是“重新发明工具框架”，而是：

- 同一批已注册工具，在不同阶段有不同可见性
- 同一个 tool loop，会话内临时只露出某些工具

当前这套分层已经足够承接这种能力。

### 1.4 引擎状态已经可以 durable snapshot

`AgentEngineStateSnapshot` 与围绕 `AgentWorkspaceRoot` 做 materialize/project 的内部 snapshot helper 已经存在，这说明：

- 未来要加 wizard instance durable state，并不是从零开始
- 但也意味着我们现在应该先把 wizard runtime 的状态形状想清楚，再写入 snapshot schema

### 1.5 事件粒度与 phase sandbox 现在已经够第一版开工

和更早的判断相比，这里最关键的变化是：

- `ActionProduced` 已有
- `ToolExecutionCompleted` 已有
- `PrepareInvocationEventArgs.ToolAccessOverride` 已有
- `ToolAccessSnapshot.AllowOnly(...)` 已有

也就是说，第一版 wizard runtime 最需要的：

- 触发器事件源
- actor-side continuation primitive
- allow-only phase sandbox

现在都已经不再是“未来要先补”的缺口，而是“已经可以开始用”的现成插槽。

## 2. 当前最关键的地基缺口

### 2.1 现在最缺的不是 primitive，而是宿主侧 orchestration 壳

从当前代码看，最缺的已经不是：

- 事件源
- injection primitive
- allow-only tool gating

而是一个真正把这些插槽接成运行时流程的宿主侧对象，例如：

- trigger 注册
- phase 状态推进
- 当前 active wizard 的 scratchpad
- cleanup / cooldown / telemetry

这说明“可开工性”的瓶颈已经从引擎内核缺 primitive，转移到了宿主侧 runtime 组装。

### 2.2 `InjectActionContent(...)` 已可用，但 timing discipline 需要认真对待

Micro-Wizard 非常依赖这种能力，比如：

- “我刚刚看过完整文件了，现在请只决定保留什么”
- “现在只确认 split 边界，不要提前写 summary”
- “刚才失败了，因为 `after_text` 不唯一，请给更短更独特的锚点”

这里真正需要的，不是普通 notification，也不是单次请求级的临时窗口文本，而是：

- 在 RecentHistory 事件账本里出现一段可续写的 actor-side prefix
- 下一次 completion 继续补完它
- 若最后一条历史本身就是 Action，则投影后的 provider 上下文应把它视为同一段连续 actor stream
- 若最后一条历史不是 Action，则运行时仍应能显式建立一个新的 actor-side continuation 起点

当前代码已经提供了这个原语，而且由于 `Agent.Core` 运行时现在只接受 full-feature profile，host-side wizard 也不再需要围绕 non-full-feature 回退去设计主流程。

仍值得知道的一个实现细节是：

- 在新 turn 刚开始、实际 profile 尚未 resolve 前，`CurrentTurnFullFeatureEnabled` 仍可能是 `null`
- 但它现在更像内部时序状态，而不是宿主需要重点围绕其三值语义建模的公开能力系统

对当前施工而言，更重要的结论是：

- wizard orchestrator 可以直接假定：只要本次 `StepAsync(...)` 没被入口/`ResolveProfile` 拒绝，本轮就处在 full-feature 语义主链上

### 2.3 “临时新增工具”依然不适合现在就做成第一层基础设施

这是一个值得提前钉住的判断。

从需求上看，wizard 好像需要“进入局部流程时新增几个临时工具”。  
但从实现稳定性看，第一步更稳的路线其实是：

- 先把 wizard 相关工具常驻注册在 registry 中
- 默认对主会话隐藏
- 进入某个 phase 时通过 tool access gating 暴露出来

原因很简单：

- 真正动态新增工具实例会立刻碰到恢复语义和持久化问题
- “这个临时工具对象是谁创建的、重启后怎么恢复、调用中途 crash 怎么办”会迅速抬高复杂度

所以第一阶段推荐：

- **优先做“预注册 + phase gating”**
- **暂缓做“每次 invocation 动态发明新工具实例”**

### 2.4 还没有 built-in wizard lifecycle / durable state / cleanup 模型

当前内核并没有这些一等公民对象：

- `WizardRecipe`
- `WizardInstance`
- `WizardStatus`
- `WizardPhaseState`
- `WizardScratchpad`

这不阻止第一版开工，但意味着：

- 第一版更适合做宿主侧 orchestrator
- 不适合马上把 wizard state 烧进 `AgentEngineStateSnapshot`

### 2.5 `pending notifications` 仍不适合作为可靠 wizard thought bus

`AgentState` 里的 pending notification 机制更像 host notification 队列。  
它的当前语义是：

- 下一条 observation-like entry 会把队列内容取出并合并
- 但还没有“只有被模型成功消费才确认移除”的 ack 语义

这意味着它不太适合直接承载 wizard 的临时 thought / phase prompt。

换句话说：

- 它适合普通宿主通知
- 不适合承载需要精细生命周期的 wizard overlay

## 3. 目前代码里已经具备的基础插槽

和更早的审阅稿相比，这一节不再是“准备补什么”，而是“代码里已经有什么”。

### 3.1 新增 `ActionProduced` 事件

位置：

- `prototypes/Agent.Core/AgentEngine.cs`
- `prototypes/Agent.Core/AgentEngine.Events.cs`

作用：

- 在模型输出 `ActionEntry` 被追加到历史后触发
- 若启用了 repo-backed host，还会等本 Step 的稳定边界先持久化成功后再触发
- 宿主可直接看到这次 action 是否带 tool call
- 后续可据此触发 wizard，例如：
  - 某工具 intent 刚被模型调用
  - 某类输出刚刚出现，需要进入 repair / review / summarize 流程

### 3.2 新增 `ToolExecutionCompleted` 事件

位置同上。

作用：

- 在单个工具调用完成后触发
- 若启用了 repo-backed host，同样是在稳定边界持久化成功后才对外触发
- 包含：
  - `RawToolCall`
  - `ToolCallExecutionResult`
  - 本 turn 的真实 `LlmProfile`

这正好可以承接：

- “某工具刚成功执行”
- “某工具失败了，进入 repair phase”
- “某工具执行后要触发某个 micro-wizard”

### 3.3 `InjectActionContent(...)` / `InjectionEntry` primitive 已落地，并接入 turn 级 capability gate

位置：

- `prototypes/Agent.Core/AgentEngine.cs`
- `prototypes/Agent.Core/History/ActionInjection.cs`
- `prototypes/Agent.Core/History/AgentState.cs`

它现在提供的是一套更贴近 micro-wizard user story 的原语：

- `InjectActionContent(ActionInjectionRequest)`
- `HasPendingActionContinuation`
- `InjectionEntry`

核心语义是：

1. 根据最近一条 `ActionEntry` 的尾部形态，决定注入为 `thinking` 还是正文 `text`
2. 注入内容不再偷偷改写上一条 `ActionEntry`，而是显式追加一条独立的 `InjectionEntry`
3. `RecentHistory` 因此不再被迫等同于 provider message log，而是升级为更诚实的事件账本
4. 下一次 completion 前，由 `ProjectInvocationContext(...)` 把相邻的 actor-side entries（`ActionEntry` + `InjectionEntry`）动态拼接成真正发给 provider 的 assistant/action message

这正是“触发器向模型脑海里塞一个念头，然后让它继续想 / 继续行动”的核心能力。

需要诚实说明的一点是：

- 这套机制依赖 provider / dialect 对 trailing assistant prefix continuation 的接受程度
- capability 差异现在已经部分内化为 engine 级门控
- 但跨模型仍应按“能力有差异”的前提来设计回退路径

结合当前代码，更准确的说法应是：

- `InjectionEntry` 作为底层 history primitive 可以继续存在
- 但真正把它当作 Micro-Wizard 主路径原语使用时，应要求当前 turn 已通过 `SupportsAgentCoreFullFeatures` 的准入门
- 这条门现在已经由 `AgentEngine` 自己执行，而不是只停留在文档约定

### 3.4 `PrepareInvocationEventArgs.ToolAccessOverride` 现在就是“请求级 tool gating”

位置：

- `prototypes/Agent.Core/AgentEngine.Events.cs`
- `prototypes/Agent.Core/AgentEngine.cs`

现在职责已经收窄为一件事：

- `PrepareInvocationEventArgs.ToolAccessOverride`

也就是：

- 它负责“这一轮允许模型看到哪些工具”
- 语义上是对 AppHost 默认投影的进一步收紧，而不是任意放宽
- 不再承担“向模型上下文注入一段想法 / 正文”的职责

这样分层会更诚实：

- **History primitive** 负责 assistant prefix / thought injection
- **PrepareInvocation 参数** 负责请求级工具可见性控制

### 3.5 `ToolAccessSnapshot` 现在支持 allow-only

位置：

- `prototypes/Completion.Tools/ToolAccessSnapshot.cs`

新增了：

- `ToolAccessMode`
- `ToolAccessSnapshot.AllowOnly(...)`

这样 phase sandbox 终于可以自然表达成：

```csharp
args.ToolAccessOverride =
    ToolAccessSnapshot.AllowOnly([
        "memo.read_node",
        "memo.split_body_block_by_text"
    ]);
```

### 3.6 测试层面已经有最小护栏

当前测试已经覆盖了几类对 wizard runtime 很重要的边界：

- `Agent.Core` 会拒绝 non-full-feature input profile
- `ResolveProfile` 产出的实际 profile 也必须是 full-feature
- `ToolAccessOverride` 只能进一步收紧，不能放宽 App projection
- turn 边界允许在 full-feature profile 之间切换
- 同一 turn 内 capability shape 仍不能悄悄漂移

这意味着现在开工宿主侧 `WizardOrchestrator`，已经不是“完全裸奔”的状态了。

## 4. 现在还不建议立刻做进引擎核心的东西

### 4.1 不建议第一步就把 `WizardRecipe` / `WizardInstance` 写进 `AgentEngineStateSnapshot`

原因不是做不到，而是现在还太早。

在 recipe / instance 的字段真正稳定前，过早写入 durable schema 会带来：

- schema 迁移负担
- runtime 逻辑和 durable 模型纠缠
- 让“实验性流程建模”过早僵化

更稳的路线是：

1. 先让宿主侧 runtime 通过事件、history injection 和 tool gating 跑起来
2. 用真实场景压一轮 shape
3. 再把稳定下来的那部分沉到 durable state

### 4.2 不建议第一步就追求并发 wizard

当前 `Agent.Core` 的单线程单步推进心智模型非常清楚。  
Micro-Wizard 第一版应该顺着这个纪律来：

- 同时最多一个 active wizard
- wizard 只是主会话旁边的受控局部流程
- 先把生命周期、action injection、工具 gating、结果固化做好

并发调度留到后面再谈。

### 4.3 不建议第一步就把 capability policy 再抽象出第二套并行系统

当前 engine 已经有：

- `LlmProfile.Capabilities`
- `CurrentTurnFullFeatureEnabled`
- `StepAsync(...)` / `ResolveProfile` 的 full-feature 准入门

因此 wizard runtime 第一版不应再额外发明一套：

- 自己的 “can_inject”
- 自己的 “supports_first_system”
- 自己的 “supports_wizard”

更好的做法是尽量复用 engine 已决议出的事实，避免双真源。

## 5. 我推荐的施工顺序

### 阶段 A：先在宿主侧做 `WizardOrchestrator`

不要急着把它塞进 `AgentEngine`。

先让一个宿主侧对象负责：

- 订阅 `ActionProduced`
- 订阅 `ToolExecutionCompleted`
- 在适当时机调用 `engine.InjectActionContent(...)`
- 在 `PrepareInvocationAsync` 中根据当前 wizard phase 填 `ToolAccessOverride`

这样可以先把“运行时机制”跑通，而不污染引擎主循环。

### 阶段 B：只落一个真实场景

推荐首个真实场景继续围绕已经想清楚的那类问题：

- `view_file` 选择性记忆
- MemoTree split 后 gist/summary repair
- collapse / summary maintenance

先挑一个，不要多线并行。

### 阶段 C：等 phase/trigger shape 稳定后，再下沉 durable state

届时可以把这些对象正式建模：

- `WizardRecipeRef`
- `WizardInstance`
- `WizardStatus`
- `WizardPhaseState`
- `WizardScratchpad`

并写入 `AgentEngineStateSnapshot` / 内部 snapshot helper。

### 阶段 D：最后再做 timeline cleanup / telemetry / audit

这是很重要的一层，但不该抢跑。

先有：

- 能触发
- 能进 phase
- 能切换工具范围
- 能固化结果

再谈：

- wizard timeline
- cleanup policy
- telemetry policy
- training trace harvesting

## 6. 一个很自然的下一步骨架

如果下一步你要开始动手，我建议先做一个很薄的宿主侧原型接口，大致像这样：

```csharp
public interface IWizardOrchestrator {
    void OnActionProduced(ActionProducedEventArgs args);
    void OnToolExecutionCompleted(ToolExecutionCompletedEventArgs args);
    ValueTask PrepareInvocationAsync(PrepareInvocationEventArgs args, CancellationToken ct);
}
```

然后把它挂到 `AgentEngine`：

- `engine.ActionProduced += ...`
- `engine.ToolExecutionCompleted += ...`
- `engine.PrepareInvocationAsync = ...`

这样第一版就已经能做出：

- 事件触发
- 向 RecentHistory 注入可续写的 actor-side prefix / thought
- allow-only tool sandbox

而且整个 blast radius 仍然很小。

## 7. 总结判断

当前 `Agent.Core` 不是“已经万事俱备的 wizard runtime”，但它已经从“还缺关键 primitive”走到了“可以真正开工第一版宿主侧 wizard runtime”的阶段。

它现在已经具备成为这套基础设施承载体的关键条件：

- 单步状态机边界清晰
- 请求前裁决门清晰
- tool session 抽象清晰
- durable snapshot 壳子已在
- 事件源已在
- actor-side continuation primitive 已在
- allow-only phase sandbox 已在
- full-feature capability gate 已在

这轮最重要的结论不再是“先补底层插槽”，而是：

**先基于现有内核，在宿主侧做出一个真正运行的 `WizardOrchestrator`，用一个真实场景把 trigger / phase / gating / cleanup 纪律压出来。**

等这一层 shape 稳定后，再决定哪些对象值得正式下沉进引擎核心与 durable snapshot。
