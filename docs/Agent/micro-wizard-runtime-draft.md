# Micro-Wizard Runtime Draft

状态：draft v0
定位：把 Micro-Wizard 从单点实验机制提升为 Agent 运行时基础设施，用于承载受控的短时临时过程。

配套文档：
- 运行时定位与分层：本文
- DSL / IR 草图：`docs/Agent/micro-wizard-dsl-sketch.md`

## 1. 核心判断

长寿命 Agent 不应该只有两种运行模式：

- 主会话正常思考
- 工具直接单步执行

现实中还存在第三类过程：

- 多步
- 有阶段
- 需要临时上下文
- 需要中间清理
- 结果要固化，但过程最好不要长期堆在主上下文里

Micro-Wizard 就是为这第三类过程准备的运行时机制。

它不是“另一个花哨工具层”，而更像：

- Agent 的短时过程调度器
- 主会话外的受控临时执行腔室
- 一组可声明、可触发、可清理的认知微流程

## 2. 直觉类比

可以把 LLM 理解成 AI 系统里的 CPU。

CPU 很强，但如果没有：

- 外设
- 中断
- 驱动
- 任务调度
- 操作系统

就只能做非常原始的串行计算。

今天很多 Agent 系统的上下文使用方式，仍然像没有外围软件栈的早期计算环境：

- 用户输入直接喂给模型
- 工具调用过程原样堆积
- 所有中间步骤都长期残留
- 所有修复与整理都靠主会话显式承担

Micro-Wizard 解决的正是这类“外围软件栈缺失”的问题。

## 3. 这类机制为什么重要

有很多操作都不适合直接在主会话里裸跑：

- context compression
- memory summary maintenance
- risky edit preflight
- node split 后的 gist/summary 重建
- dirty derived memory repair
- dependency invalidation 后的受影响节点扫描

这些动作有共同点：

- 它们有清晰过程
- 过程本身常常不值得长期留在主上下文里
- 但结果必须可靠地固化

如果没有 Micro-Wizard，就容易出现两种极端：

- 要么把所有步骤都堆进主会话，噪音越来越大
- 要么把它们塞成一个超级工具，单次调用过重、不可审计、难失败恢复

Micro-Wizard 走的是中间路线：

- 原语保持小
- 过程由运行时托管
- 结果在退出时固化
- 中间痕迹按规则清理

## 4. 基本定义

### 4.1 Main Session

主会话。

负责用户任务的连续推进，是 Agent 的默认认知线程。

### 4.2 Wizard

一个受控的临时执行过程。

它有：

- 触发条件
- 临时上下文
- 允许工具集
- 阶段目标
- 完成条件
- 退出后的固化与清理规则

### 4.3 Wizard Runtime

负责托管 wizard 生命周期的运行时层。

### 4.4 Wizard Recipe

一个声明式定义，描述某类 wizard 应如何运行。

### 4.5 Trigger

触发器。

用于监听主会话或系统状态中的某个事件，并决定是否进入某个 wizard。

## 5. 核心设计主张

### 5.1 原语和 Wizard 必须分层

底层工具应保持小而明确，例如：

- `SplitBodyBlockByText`
- `SplitNode`
- `SetGist`
- `SetSummary`

Wizard 负责把这些原语编排成受控过程。

不要把整个流程揉进一个超重工具。

### 5.2 Wizard 过程应默认是临时的

Wizard 不是为了制造更多上下文，而是为了减少主上下文噪音。

因此默认预期应是：

- 过程可审计
- 结果固化
- 中间过程尽量不污染主上下文

### 5.3 Wizard 应是可声明的

上层逻辑最好只声明：

- 何时触发
- 允许哪些工具
- 分几步
- 什么算完成
- 如何清理

而不是每次手写一套 ad-hoc 流程控制。

### 5.4 Trigger 数量应受控

触发器是非常强的机制，过多会让系统变成噪音工厂。

因此应该允许：

- 限制总数量
- 限制同时激活数量
- 限制某类触发器在一定时间内的频率

这也与你提到的“先少量练习一种美德，再逐步内化”的思路很一致。

## 6. 推荐的运行时分层

推荐把系统拆成四层：

### 6.1 Primitive Layer

最底层原语：

- 小
- 单步
- 可审计
- 无隐藏流程

### 6.2 Wizard Recipe Layer

声明式描述某种流程：

- 名称
- 目标
- 输入绑定
- 允许工具
- 阶段列表
- 完成条件
- 失败恢复
- 退出清理

### 6.3 Wizard Runtime Layer

负责：

- 启动 wizard
- 构造临时上下文
- 推进阶段
- 记录中间结果
- 判定成功/失败/放弃
- 执行结果固化
- 清理临时痕迹

### 6.4 Trigger Layer

负责：

- 监听事件
- 过滤条件
- 节流
- 决定是否进入 wizard

## 7. 事件来源

触发器不应只盯着“用户发消息”。

更有价值的事件来源包括：

- 某工具刚调用完成
- 某对象进入 dirty state
- 某节点被 split / merge
- 某次编辑被标记为 risky
- token 用量接近阈值
- 某类依赖 invalidation 发生

也就是说，Agent 运行时的很多“经验”本质上都可以抽象成：

`event -> trigger -> wizard`

这与你说的“很多经验本质是触发器形式的”高度一致。

## 8. 一个典型案例：MemoTree Node Split Wizard

这是当前最直接的例子。

### 8.1 触发

用户或主会话表达：

- 希望拆分某个长节点

或：

- 某节点被检测为“正文过长且子节点较多”

### 8.2 Wizard 阶段

阶段 1：确认切分意图

- 目标：明确要把哪个节点拆成什么主题

阶段 2：建立文本边界

- 调用 `SplitBodyBlockByText`
- 若失败则要求更具体的前后文本

阶段 3：执行结构切分

- 调用 `SplitNode`

阶段 4：补全左右节点的 `gist/summary`

- 读取左右节点
- 生成新的派生记忆

阶段 5：固化与清理

- 写回结果
- 退出 wizard
- 主会话只保留结果性痕迹

### 8.3 优点

- 主会话不需要长期背着整个多步过程
- Split 原语本身保持干净
- 失败时可控
- 成功后可把“脏节点”修到干净

## 9. Dirty State 与 Wizard 的关系

Micro-Wizard 和 dirty state 天然是一对。

推荐理解：

- dirty state 负责显式表达“哪里不干净了”
- wizard runtime 负责承载“怎么把它修回来”

比如：

- `SplitNode` 之后左右节点的 `summary_state = Invalidated`
- 触发器监测到存在 invalidated summary
- 启动 `RepairDerivedMemoryWizard`
- wizard 生成新 `gist/summary`
- 退出时固化

这比要求 `SplitNode` 一次性完成所有事情更稳。

## 10. 临时上下文与主上下文的关系

Wizard 最大的价值之一，是允许中间过程在临时上下文里运行。

推荐目标不是完全隐藏所有过程，而是：

- 主会话默认不背中间垃圾
- 运行时仍保留审计能力

所以推荐保留三层痕迹：

1. 主会话痕迹  
   只保留高价值结果

2. Wizard 摘要痕迹  
   用于必要时解释发生过什么

3. 完整审计日志  
   用于调试、复盘、微调数据收集

## 11. 建议的 Recipe 形状

不必拘泥于最终 API，先给一个概念形状：

```text
WizardRecipe
  id
  trigger
  goal
  allowed_tools
  phases[]
  completion_condition
  abort_condition
  commit_strategy
  cleanup_strategy
  telemetry_policy
```

Phase 可进一步有：

```text
WizardPhase
  id
  instruction
  success_condition
  retry_policy
  on_success
  on_failure
```

## 12. 建议的触发器形状

同样先给概念形状：

```text
WizardTrigger
  id
  event_kind
  predicate
  cooldown
  max_concurrent
  priority
  recipe_id
```

这层非常重要，因为它决定了系统是“会形成经验的运行时”，还是“偶尔能跑个临时流程的拼装玩具”。

## 13. 关于“训练内化”的思路

我很认同一个方向：

- 前期先限制触发器数量
- 把某些关键流程反复稳定执行
- 记录轨迹
- 用轨迹做后续模型微调或策略蒸馏

这会形成一种很有意思的演进链：

1. 起初，能力靠外部 wizard runtime 提供
2. 反复执行后，轨迹变成训练数据
3. 模型逐步把这些模式内化成本能
4. 运行时再把更高阶、更罕见的过程留给 wizard

也就是说：

- Wizard 是当前阶段的外围软件栈
- 微调是把高频外围软件栈逐步“烧进芯片”

这非常像计算机发展史里：

- 先有外部工具与操作系统
- 后来某些能力再被更底层地吸收与优化

## 14. 为什么这值得放进 Agent.Core

如果 Micro-Wizard 只是某个 prototype 的一次性技巧，它的价值很有限。

但如果它升格为 `Agent.Core` 基础设施，应用面会一下子变大：

- MemoTree 派生记忆修复
- context compression
- editing preflight
- dependency invalidation repair
- multi-step tool orchestration
- 低频但重要的经验性“反射动作”

这时它不再是“一个特殊功能”，而变成：

Agent 的 runtime middleware

## 15. 推荐的最小落地路线

### 阶段 1

把现有 micro-wizard 实验抽象成：

- recipe
- runtime
- trigger

三块独立概念。

### 阶段 2

支持一个最小的单实例 wizard 生命周期：

- 启动
- 运行
- 成功
- 放弃
- 清理

### 阶段 3

接入第一个真实业务场景：

- MemoTree split 后的派生记忆修复

### 阶段 4

再扩展到：

- dirty node repair
- compression wizard
- risky edit wizard

### 阶段 5

再考虑更丰富的触发器治理、并发策略与训练闭环。

## 16. 一句话结论

Micro-Wizard 不该继续只是“在主会话旁边临时开个小房间”的实验技巧。

它更适合被正式提升为：

一种围绕事件触发、临时过程、结果固化与痕迹清理而设计的 Agent 运行时基础设施。
