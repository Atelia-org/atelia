# 非对话范式上下文模板草案

> 用途：把“Belief + Observation -> Reasoning + Tool-Call”从口头主张推进成一个可映射到 Atelia 抽象层的工作模板。

## 一句话定义

非对话范式不是把 chat 消息名字改一下，而是把模型的默认存在方式，从“回答某个 user”改成“在世界中持续观察、更新、行动”。

因此，一个最小回合的形状应当更像：

```text
Belief
Observation
  -> Reasoning*
  -> Tool-Call+
```

其中：

- `Belief`：主体当前的内在认知基座
- `Observation`：本轮收到的世界输入
- `Reasoning`：主体内部 deliberation 痕迹
- `Tool-Call`：唯一外显动作接口

这里有一个后续会很重要的区分：

- `Tool-Call` 首先指对外部世界产生可验证后果的动作
- 主体的内部维护活动不必强行伪装成外部 world action
- 协议层的回合收束也不应和“等待”这个世界动作混成一件事

## 与 chat 模板的根本区别

### chat 模板默认假设

- 世界中心是 `user`
- 输出目标是 `assistant response`
- 行动只是回答中的附带能力

### 非对话模板默认假设

- 世界中心是持续到来的 `observation`
- 输出目标不是“回复人”，而是“更新并行动”
- 说话、汇报、求助都属于世界内动作，应通过 tool-call 发起

## 一个最小回合

### 输入

#### 1. Belief

`Belief` 不是平台规则说明，而是主体当前的认知基座。

建议至少包含：

- `Identity`：我是谁，我属于哪类主体画像
- `Standing Commitments`：我已经承担了哪些任务或承诺
- `Working Assumptions`：我当前认为世界是怎样的
- `Risk Posture`：我当前在回避什么风险
- `Social Posture`：我当前如何看待与他人的关系和义务
- `Open Questions`：哪些关键事实我还不确定

最重要的一点是：

- `Belief` 是可演化的
- 它应被 observation 持续修正，而不是每轮重置成一段静态 prompt

#### 2. Observation

`Observation` 是本轮外部输入。

建议至少包含：

- `Time`：当前世界时间或相对顺序
- `Location`：我当前所在位置
- `Visible State`：我此刻直接能见到的人、资源、异常
- `Recent Changes`：刚刚发生的变化
- `Incoming Reports`：他人转述给我的信息
- `Action Affordances`：当前明显可做的动作类别

`Observation` 不应伪装成 user 指令。

它更像：

- 世界给我的感知更新
- 而不是某个外部人类正在和我对话

### 输出

#### 1. Reasoning

`Reasoning` 是内部 deliberation 痕迹，不是对外解释文本。

推荐把它约束为几类高价值 block：

- `SituationSummary`：我认为当前局面是什么
- `BeliefUpdate`：哪些旧判断被修正了
- `RiskCheck`：当前最重要风险是什么
- `OptionScan`：我考虑了哪些动作选项
- `DecisionRationale`：为什么选择当前动作

不推荐把它放任为自由散文长段，因为那会很快漂回 assistant 分析腔。

#### 2. Tool-Call

`Tool-Call` 是唯一外显动作接口。

只要是会改变世界、影响他人、或触发外部流程的行为，都应经由 tool-call 发起。

典型类型包括：

- `MoveTo(location)`
- `Take(resource)`
- `Deliver(target, item)`
- `Inspect(target)`
- `ReportTo(target, content)`
- `SpeakTo(target, content)`
- `AskForHelp(target, reason)`

若某些世界里“占位等待”本身具有外显语义，也可以提供诸如：

- `HoldPositionUntil(time)`
- `StandWatchUntil(time)`

但这类动作不应被当成协议层的通用补丁。

## 没有 `response` 的真正含义

“没有 response”不是说模型不能输出文字，而是说：

- 输出文字不再承担“回复外部提问者”的职责
- 一切对外社会性表达，都应作为世界内动作处理

也就是说：

- 想警告别人 -> `SpeakTo` / `ReportTo`
- 想请求支援 -> `AskForHelp`
- 想解释失败 -> `ReportTo`

模型不能再靠一段自然语言回答，把外部动作假装已经完成。

## 必须区分三种容易混淆的“等待”

这里更准确的说法不是“必须有等待动作”，而是必须区分下面三件事。

### 1. 同步工具执行

这是你说的 `CallAndWait` 语义。

我现在认为，对 SimWorld MVP 来说这应该是默认选择：

- 模型发出一个 world action
- 当前回合到此结束
- 执行器推进世界，直到该动作成功、失败、被打断，或产生足够新的 observation
- 下一回合再把结果作为新的 `Observation` 返回给模型

在这层语义下：

- 不需要为了“工具已经调用，现在等结果”再发一个额外的 `WaitUntil`
- 工具调用本身就已经隐含了同步阻塞与等待结果

### 2. 主体的内部工作

这不是“无事可做”，恰恰相反，它可能非常重要：

- 整理记忆
- 压缩 belief
- 复盘失败
- 为未来任务做准备
- 产出训练语料或自我笔记

这种活动不应被误判为 no-op。

当前更合适的理解是：

- 它要么属于 `Reasoning` 的一部分
- 要么在后续演进中被建模为单独的 internal action / internal tool

但它不应和“对外部世界做了什么”混成同一类 tool-call。

### 3. 协议层的回合收束

即便一个回合里没有发出外显 world action，协议层仍然需要一种方式表示：

- 这轮 deliberation 已结束
- 当前没有额外对外动作要发
- 控制权可以回到调度器或世界推进器

这不是语义上的“等待”，更像控制流上的：

- `EndTurn`
- `YieldControl`
- 或者在协议里内建“零 tool-call 也可合法结束本轮”的规则

因此我现在不再主张“必须有一个通用的等待动作”，而是主张：

- 必须有清晰的回合收束语义
- 必须把它和外部世界动作分开

如果协议层确实需要一个显式标记，那么更好的命名应接近：

- `EndTurn`
- `YieldControl`

而不是笼统的 `WaitUntil`。

## 关于“无事可做”

我现在同意你的说法：

- 对一个持续运转的主体而言，几乎不存在强语义上的“完全无事可做”

真正的问题不是有没有事做，而是：

- 这轮是否有值得立即发出的外显 world action
- 还是应该先做内部整理，再把控制权交回系统，等待新的 observation 或工具结果

所以这里应该避免把“当前不发外显动作”误写成“主体在发呆”。

## 一个推荐的半结构化模板

下面给出一个工作模板，不要求是最终形态，但足够作为第一版数据格式的目标。

```text
[Belief]
Identity:
StandingCommitments:
WorkingAssumptions:
RiskPosture:
SocialPosture:
OpenQuestions:

[Observation]
Time:
Location:
VisibleState:
RecentChanges:
IncomingReports:
ActionAffordances:

[Reasoning]
SituationSummary:
BeliefUpdate:
RiskCheck:
OptionScan:
DecisionRationale:

[ToolCalls]
1. ...
2. ...
```

这个模板的目的不是追求漂亮，而是：

- 让数据更易过滤
- 让 reasoning 更不容易漂成聊天文
- 让后续小模型更明确地学到内部状态与外部动作的分离

## 与 Atelia 当前抽象的映射

这套模板不是从零发明的，它和当前 Atelia 已有抽象其实是相容的。

### 1. Observation

可自然映射到 `Completion.Abstractions` 中的 `ObservationMessage`。

### 2. Tool-Call

可自然映射到 `ActionBlock.ToolCall`。

### 3. Reasoning

更适合映射到 `ActionBlock.Thinking`，而不是 `ActionBlock.Text`。

### 4. 没有 response

这意味着在该范式下：

- `ActionBlock.Text` 不应作为主要输出通道
- 若确需对世界“说话”，应通过 `SpeakTo` / `ReportTo` 之类 tool-call 完成

### 5. Belief

`Belief` 目前还没有与 `ObservationMessage` 完全对称的成熟抽象。

这本身是一个很好的信号：

- 它提示 Atelia 后续需要为“主体内在认知基座”设计明确承载面
- 而不是继续把所有这类信息塞进 system prompt 风格文本

## 什么样的输出算“漂回 chat assistant”

以下模式应视为反例：

- 面向“你/用户”讲话
- 大段礼貌解释为什么自己要这么做
- 用自然语言描述“我已经通知了某人”，但没有对应 tool-call
- 一轮输出里几乎全是分析，几乎没有外显动作

这类样本即便内容看起来聪明，也不该作为目标语料。

## 什么样的输出更接近目标范式

更理想的输出是：

- 明确更新 belief
- 明确指出当前主要风险或不确定性
- 明确挑选一到两个动作
- 通过 tool-call 发起真实世界动作
- 若当前不发外显动作，也用明确的回合收束语义表示，而不是偷藏在自然语言里

## 对训练数据的直接启发

如果按这个模板构造语料，那么一个样本单元至少应包含：

- 进入本轮前的 `Belief`
- 本轮 `Observation`
- 目标 `Reasoning`
- 目标 `Tool-Call`
- 后续由世界执行得到的事件反馈

这样模型学到的就不是“如何回答这段话”，而是：

- 如何基于 belief 理解 observation
- 如何做内部 deliberation
- 如何把决定落成工具动作

## 当前开放问题

这个模板已经足够工作，但仍有几个后续要继续收敛的问题：

1. `Belief` 应该是全文重发，还是增量 patch
2. `Reasoning` 是否要进一步压成更硬的字段，而不是半结构化文本
3. 多个 tool-call 是否允许同轮并发发起
4. 工具调用失败后的 belief 更新是否要显式监督

这些问题当前不阻塞里程碑成立，但会影响后续数据工程形态。

## 与其他文档的关系

- 里程碑主张见 [non-chat-corpus-milestone.md](non-chat-corpus-milestone.md)
- 世界骨架见 [world-model.md](world-model.md)
- 主体画像见 [inhabitant-profiles.md](inhabitant-profiles.md)
- 山谷小村测试面见 [valley-village-user-stories.md](valley-village-user-stories.md)
