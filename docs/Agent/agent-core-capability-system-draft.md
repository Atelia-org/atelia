# Agent.Core Capability System Draft

状态：draft v1

配套文档：
- `docs/Agent/agent-core-micro-wizard-readiness-review.md`
- `docs/Agent/micro-wizard-runtime-draft.md`
- `docs/Agent/micro-wizard-dsl-sketch.md`
- `docs/Agent/role-play-agent-first-system-draft.md`
- `docs/Agent/Thinking-Replay-Design.md`

## 0. 一句话结论

`CapabilityProfile` 仍然负责记录不同 LLM surface 的语义事实，但 `Agent.Core` 当前不再尝试承接“更宽的基础执行内核 + 更窄的 full-feature 认知层”这两条并行产品路径。

当前收口是：

- **`Agent.Core` 运行时只接受 `SupportsAgentCoreFullFeatures == true` 的 profile**
- **non-full-feature surface 暂不进入 `Agent.Core` 主链**
- **需要普通 durable chat / tool loop 的更宽路径，交给其他宿主承接**

这是一条刻意的、创新优先的边界，而不是未来永远不变的终局。

---

## 1. 问题陈述

`ActionInjection`、Micro-Wizard、第一系统式触发器，这些能力依赖的不是“能调模型”这么弱的前提，而是更强的会话语义：

- thinking 对 runtime 是透明的
- runtime 可以稳定地回写 actor-side continuation
- assistant prefix continuation 可以被正式依赖
- 同一份上下文在 full-feature profile 之间具有可共享的解释语义

如果这些前提不成立，系统会落入最糟糕的灰区：

- 看起来还能注入 thought，其实 provider 只是把它当普通历史文本
- 看起来还能切模型，其实上一轮 reasoning 在新 surface 上不再可解释
- 看起来像“半兼容”，其实只是偶尔不炸

当前项目阶段并不需要这种灰区。  
我们更需要一个**敢于收窄边界、优先保护强语义**的核心。

---

## 2. 设计目标

本设计要解决的是：

1. 把不同 surface 的关键语义差异显式建模，而不是继续藏在经验判断里。
2. 给 `Agent.Core` 导出一个非常简单、可强执行的兼容性结论：
   - `SupportsAgentCoreFullFeatures == true`
   - `SupportsAgentCoreFullFeatures == false`
3. 让 `Agent.Core` 当前只服务前者，从入口就 fail fast。
4. 保持 turn lock，同时允许 full-feature profile 在 turn 边界切换。

本设计当前**不**追求：

- 让 `Agent.Core` 继续兼容 non-full-feature profile 的普通运行模式
- 给 `IApp` 再发明一套 feature requirement matrix
- 在同一份 runtime 里同时维护“强语义主链”和“弱语义降级主链”
- 用 capability taxonomy 取代真实的产品边界选择

---

## 3. 核心主张

### 3.1 区分“能力事实”与“运行时准入”

底层 capability model 仍然可以稍微 richer 一点。  
但 `Agent.Core` 当前真正消费的不是一套多级 tier，而是一条非常硬的准入线：

- `SupportsAgentCoreFullFeatures == true` → 允许进入 `Agent.Core`
- `SupportsAgentCoreFullFeatures == false` → 当前不允许进入 `Agent.Core`

也就是说：

- 能力事实是描述层
- `Agent.Core` 当前的运行时策略是产品层

### 3.2 `Agent.Core` 当前是 full-feature-only runtime

这次收口最重要的变化是：

- 不再把 `Agent.Core` 描述成“更宽基础执行内核 + 更窄高级认知层”的双路径 runtime
- 而是明确把它收成：**一个只服务 full-feature compatible surface 的实验核心**

这样做的收益很直接：

- Micro-Wizard 不必再背 capability 分叉
- `InjectActionContent(...)` 不必再为弱语义 surface 设计半兼容解释
- 第一系统式注入不必再担心“到底哪些 surface 理论上也许能弱化支持”
- 文档、代码、测试、宿主契约都能共用同一条主叙事

### 3.3 full-feature profile 之间共享同一份上下文

如果某个 profile 已经满足：

- thinking 明文可见
- turn 内 replay 成立
- runtime-authored reasoning replay 成立
- actor-side continuation 可编辑
- assistant prefix continuation 稳定

那么它已经满足 `Agent.Core` 的主语义前提。

因此当前不再引入 `SemanticSurfaceId`。  
更直接的做法是：

- **所有 `SupportsAgentCoreFullFeatures == true` 的 profile，都可共享同一份 `Agent.Core` 上下文**
- **真正的切换边界只由 turn lock 负责**

---

## 4. 建议的数据模型

### 4.1 `CapabilityProfile`

```csharp
public sealed record CapabilityProfile(
    bool ThinkingIsVisibleAsText,
    bool ThinkingReplayWithinTurnIsSupported,
    bool RuntimeAuthoredReasoningReplayIsSupported,
    bool RuntimeMayEditActorContinuation,
    bool AssistantPrefixContinuationIsStable,
    string? Notes = null
) {
    public bool SupportsAgentCoreFullFeatures =>
        ThinkingIsVisibleAsText &&
        ThinkingReplayWithinTurnIsSupported &&
        RuntimeAuthoredReasoningReplayIsSupported &&
        RuntimeMayEditActorContinuation &&
        AssistantPrefixContinuationIsStable;
}
```

这五个字段不是为了在 `Agent.Core` 主链里分五种模式，而是为了：

- 让 full-feature 的定义有清楚根据
- 让 future host 能知道某个 surface 为什么不合格
- 让我们以后真要重新放宽边界时，还有可回溯的语义事实

### 4.2 `LlmProfile`

`LlmProfile` 继续承载：

- `Client`
- `ModelId`
- `Name`
- `SoftContextTokenCap`
- `Capabilities`

当前运行时语义应明确写成：

- `Capabilities == null` 不表示“随便先跑”
- 它等价于“不满足 full-feature 准入”
- 因此当前 `Agent.Core` 会拒绝使用这种 profile

---

## 5. `Agent.Core` 当前的运行时契约

### 5.1 准入契约

当前 `Agent.Core` 的契约很简单：

- `StepAsync(profile)` 的输入 profile 必须满足 `SupportsAgentCoreFullFeatures == true`
- `ResolveProfile` 最终产出的实际 profile 也必须满足 `SupportsAgentCoreFullFeatures == true`
- 任一环节不满足，都应尽早失败

这意味着 capability system 在当前代码里的职责，不是“允许 weak path 继续跑”，而是：

- **在入口把 weak path 拦在外面**

### 5.2 为什么故意不做 `IApp` feature requirement

这是你已经明确判断过的一点，我也认同。

当前阶段不值得继续扩成：

- `IApp.RequiresVisibleThinking`
- `IApp.RequiresStableAssistantPrefix`
- `IApp.RequiresRuntimeAuthoredReasoningReplay`

原因很简单：

- 当前真正要创新和快速演化的，不是 app capability marketplace
- 而是围绕 full-feature surface 把 Micro-Wizard / 第一系统 / ActionInjection 主链跑通

等未来真的出现“一个 runtime 内并存多类 app、且它们各自需要不同能力”的压力时，再开这条线更自然。

### 5.3 `CurrentTurnFullFeatureEnabled` 的地位

在旧口径下，它承担的是“同一个 runtime 内做 capability 分流”的职责。  
在新口径下，它更像一个内部一致性状态：

- turn 尚未 resolve 实际 profile 前，值可能是 `null`
- 一旦本 turn 完成 profile 决议，在当前运行时里它应为 `true`

也就是说：

- 它仍然可以保留
- 但不再是宿主必须围绕其三值语义写大量分支的公开设计中心

---

## 6. 模型切换策略

### 6.1 保留 turn lock

同一 turn 内仍然要求 `CompletionDescriptor` 一致。  
这是 replay 连续性与工具往返一致性的核心约束。

### 6.2 不引入 session-level semantic lock

因为 full-feature 的定义已经足够强，所以当前不再额外引入 session 级 `SemanticSurfaceId`。

### 6.3 允许 full-feature profile 在 turn 边界切换

当前建议是：

- turn 内：仍然锁定
- turn 边界：允许从一个 full-feature profile 切到另一个 full-feature profile

因此“模型切换”的主约束不是 provider 标签，而是：

- 是否 full-feature compatible
- 是否发生在 turn 边界

---

## 7. 对 Micro-Wizard / 第一系统 / Thinking Replay 的含义

### 7.1 Micro-Wizard

Micro-Wizard 可以默认建立在强语义前提上：

- 不再为 non-full-feature profile 设计降级路径
- 不再把 capability branching 作为 v0 运行时设计的一部分

### 7.2 第一系统式注入

第一系统注入不再被视为“所有 surface 都应兼容”的普适技巧。  
它是 full-feature runtime 的一等能力。

### 7.3 Thinking Replay

Thinking replay 的底层 substrate 设计，仍然可以继续保持 provider-neutral 与 lossless。  
但“底层能表达某种 replay”不等于“当前 `Agent.Core` runtime 就应该接受该 surface”。

也就是说：

- `CapabilityProfile` 负责判断能不能进入主链
- `Thinking-Replay-Design` 负责描述主链内部如何保存与投影 thinking

---

## 8. 实施建议

### 8.1 当前应落实到代码的地方

1. 在 `StepAsync(profile)` 入口校验输入 profile
2. 在 `ResolveProfile` 产出最终 profile 后再次校验
3. 对非 full-feature profile 直接报语义不兼容错误
4. 保持 turn lock 不变
5. 继续允许 full-feature profile 在 turn 边界切换

### 8.2 当前应落实到文档的地方

所有相关文档都应改口为：

- `Agent.Core` 当前是 full-feature-only runtime
- Micro-Wizard / 第一系统默认建立在这条前提上
- non-full-feature surface 不属于当前实现阶段的目标范围

---

## 9. 一句话总结

`CapabilityProfile` 继续描述世界的细节；  
但 `Agent.Core` 当前选择的产品边界非常简单：

> **只有 `SupportsAgentCoreFullFeatures == true` 的 profile，才能进入 `Agent.Core`。**

这不是保守，而是为了把最有创新价值的强语义主链先彻底做实。
