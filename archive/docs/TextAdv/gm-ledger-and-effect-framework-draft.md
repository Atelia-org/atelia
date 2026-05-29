# TextAdv - GM Ledger and Effect Framework Draft

> 状态：中层设计草案
> 适用范围：`prototypes/TextAdv/`
> 目的：在现有 GM Agent 世界裁决设计之上，进一步澄清“世界真相账本”“Effect 规则框架”“从 GM 文本到可验证规则”的分层路径

## 一句话结论

如果 TextAdv 要继续沿“GM Agent 驱动动态世界”这条路线走下去，那么下一步最关键的，不是继续加 prompt 技巧，而是把下面三件事明确下来：

- 哪些东西属于世界真相账本，必须由结构化状态托住。
- 哪些东西只是一次性叙事结果，不能偷偷升级成长期规则。
- 哪些东西一旦会被未来反复依赖，就必须脱离自由文本，进入某种可检查、可回放、可逐步编译的 Effect / Rule 表达层。

换句话说，GM Agent 仍然可以像 TRPG 主持人一样进行裁决与叙事，但宿主系统需要逐步具备“世界语义检查器”的能力，而不只是“把 LLM 说的话变成账本”。

## 背景与问题

当前 TextAdv 已经开始形成一个健康的边界：

- `GameActionValidator` 负责检查玩家输入的 groundedness。
- `GameMasterResolver` 负责在分阶段流程中裁决世界后果。
- `GmWorldEditService` 负责通过受控工具修改账本。
- `PerceptionBundle` 负责把世界状态投影给特定 actor。

这条路已经明显优于“纯自然语言续写世界”。

但随着 GM Agent 能创建更多物品、NPC、interaction、visibility 变化乃至更复杂的回合效果，新的问题会迅速冒出来：

1. 某些后果只是“这次发生了什么”，某些后果却暗含“以后都应该这样”。
2. 当前工具可以校验字段合法性，却还很难校验长期因果自洽。
3. GM 文本中创造出来的局部规则，如果不进入某种结构化层，就只能依赖模型自己记住，长期上会漂移。
4. 一旦开始做更写实、更真实世界导向的模拟，玩家和开发者都会更自然地拿“现实常识”与“前文既有规律”来追究系统自洽性。

因此，TextAdv 现在需要的不是一个全能 `RuleContract` 终稿，而是先把更基础的中层语义地基搭起来。

## 设计目标

### 1. 让 GM 文本只负责叙事，不偷偷兼任长期规则存储

如果某个内容只用于描述本回合的结果，它可以停留在 narration 或 resolution summary。

如果某个内容会被未来回合依赖，它就必须进入某种结构化层。

### 2. 让世界演进尽可能由语义完整的操作组成

例如：

- 用 `move_item` 表达物品转移，而不是“先减少一处，再增加另一处”。
- 用“消费并触发效果”的复合语义操作，而不是让 GM 自己拼一串分散 patch。

这类设计可以从工具层就减少大量“不一致中间态”。

### 3. 让新规则先以半结构化语义出现，再决定是否需要升级成代码

TextAdv 不必一开始就让 GM 自由编写 C#。

更稳妥的路线是：

1. 先有世界真相账本。
2. 再有 Effect 语义骨架。
3. 再有可审查的规则声明。
4. 最后才考虑把其中一部分 lower 到 DSL 或受限代码。

### 4. 让验证器分层，而不是把一切问题都塞给单个 LLM

未来的“验证”应逐步拆成：

- groundedness validator
- 工具级 invariant checker
- 规则级 reviewer swarm
- shadow execution
- 受限规则运行时

## 非目标

当前阶段不追求：

- 一次性设计完整的通用世界规则语言
- 一次性支持开放世界级别的真实世界模拟
- 一次性把所有 `effectNote` 都编译成代码
- 让 GM 拥有不受限的通用编程能力

## 核心判断：先分清五类产物

GM Agent 在一次裁决中产出的内容，可以先按下面五类理解。

### 1. Narrative Output

只是给玩家看的文本，不构成未来规则依据。

例子：

- “你拨开藤蔓后，确认前方确实有一条窄路。”
- “你蹲下摸索了一阵，在沙下掏出一块边缘锋利的薄片。”

### 2. One-shot Event

一次性已经发生的事件，可回放，但本身不是长期规则。

例子：

- 玩家掀开布，露出底下的钥匙。
- 玩家点燃了一小堆潮湿树叶，产生一阵烟。

事件可以进入事件日志，但不自动生成“以后凡是潮湿树叶都这样”的规则。

### 3. Persistent Fact

已经成立并将持续影响未来的世界事实。

例子：

- 箱子已被打开。
- 绳子已断裂。
- 某物品当前在玩家身上。
- 某处出口已经建立。

这类内容必须落入世界真相账本。

### 4. Reusable Effect Pattern

已经不只是“这次发生了什么”，而是“以后满足某条件时还会这样发生”。

例子：

- 这个 interaction 每次使用都会消耗自身并暴露一个新 affordance。
- 某类工作型 interaction 会在若干回合后产出结果。
- 某个隐藏机关只有在持有指定物品时才显现。

这类东西是未来 `RuleContract` 的来源，但当前不必过早定死最终形状。

### 5. Compiled Mechanic

已经被认为足够稳定，值得进入受限规则运行时或代码层。

例子：

- 多回合工作推进器
- 标准的物品消耗与产出
- 标准的 reveal / unlock / consume / transfer / completion 机制

## 推荐的账本分层

建议将与世界相关的信息，至少区分为以下几层。

### L0. World Truth Ledger

这是当前系统真正依赖的硬事实层。

推荐进一步明确包含：

- `locations`
- `actors`
- `items`
- `interactions`
- `ownership / placement`
- `visibility`
- `time / turn / slot`
- `active work orders`
- `pending effects`
- `actor-private last resolution`

这一层的原则是：

- 只存“系统以后会继续依赖”的事实。
- 不存只为了这次文案好看而存在的细枝末节。
- 不允许由 narration 单独定义。

### L0.5. Event Ledger

这是世界演进的事件层，不等同于当前状态。

推荐未来逐步显式化：

- 本回合触发了什么 interaction
- 产生了哪些账本 patch
- 哪些信息分发给了哪些 actor
- 某个新实体或新 affordance 的出现是由什么 cause 导致的

这层的价值在于：

- 支持回放
- 支持审计
- 支持后续规则归因
- 支持训练数据沉淀

### L1. Perception Projection

这是从 L0 / L0.5 投影到某个 actor 视角下的证据包。

当前 `PerceptionBundle` 已经在承担这层职责，但后续应更明确：

- 它是“给某 actor 的证据”
- 不是“完整世界快照”
- 它既服务玩家，也服务 validator，也服务 GM 阶段输入

### L2. Effect Semantics Layer

这是本草案最想补清楚的一层。

它不等于完整规则语言，但它应该开始承载：

- 一个 interaction 或事件会如何触发效果
- 效果需要满足什么 guard
- 效果会对哪些对象产生什么语义变化
- 效果会如何影响可见性、affordance、后续事件或持续工作

当前系统里 `turnCost`、`effectScope`、`effectSlots` 已经是这层的雏形，但还不够。

### L3. Rule Notes / Scenario Notes

这里存放软规则、题材边界、风格提示、GM 注记。

这层可以影响裁决，但不能单独改变世界真相。

### L4. Belief / Notebook / Rumor

这层属于角色内认知，不属于世界真相。

它可以影响：

- 玩家或 NPC 的推理
- validator 的 groundedness 边界
- GM 对“谁应知道什么”的裁决

但它不能被 reducer 直接当作 hard truth。

## 当前最缺的不是通用规则语言，而是 Effect 语义骨架

在当前阶段，与其一上来发明完整 `RuleContract`，不如先把 Effect 的“骨架字段”明确出来。

建议先把一个 effect 至少理解为下面几个问题：

1. 它由什么触发？
2. 它依赖什么 guard？
3. 它改动了哪些世界事实？
4. 它向哪些 actor 暴露了哪些结果？
5. 它是一次性的，还是可复用的？
6. 它会不会生成后续 affordance、工作单或待结算效果？

也就是说，未来任何 effect，无论最后是自然语言 note、半结构化 contract，还是受限代码，都应能回答这几个问题。

## 推荐的 Effect 语义切片

为了避免一上来就设计通用规则语言，建议先把常见 effect 切成一组有限的语义原件。

### A. Transfer

对象在地点、actor、容器之间转移。

例子：

- `item -> actor`
- `item -> location`
- actor 移动到新地点

这类 effect 最适合直接由语义完整的工具承担。

### B. Reveal / Hide

某个对象、interaction 或信息从不可见变为可见，或反之。

例子：

- 露出一个隐藏物品
- 某个 interaction 被消耗后隐藏
- 某 NPC 离开当前视野

### C. Create / Destroy-like Completion

新对象、interaction 或关系被创建。

这里建议谨慎对待“destroy”概念。很多时候更安全的做法不是让对象凭空消失，而是：

- 转移到别处
- 标记为 consumed / unavailable / hidden
- 写入 completion state

### D. Transform

对象身份或玩家可见描述发生变化。

例子：

- 未知薄片被识别为“生锈钥匙”
- 清洗后 item description 改变
- 一个临时 affordance 升级成稳定 affordance

### E. Unlock / Grant Affordance

新的可执行动作出现。

例子：

- 打开盒子后新增“检查夹层”
- 捡起钩子后新增“拨开缝隙”

### F. Consume / Spend / Exhaust

某次使用会消耗交互、资源或工作机会。

这类 effect 特别需要结构化，因为它很容易在 prose 中被写成含糊的“似乎不能再用了”。

### G. Schedule / Continue / Complete Work

影响跨回合推进的效果。

例子：

- 本回合开始工作
- 下一回合继续推进
- 若干回合后完成并产生产出

### H. Knowledge Delivery

不一定改变外部世界，但改变“谁现在可以知道什么”。

这在多 actor 视角下尤其重要。

## 从 GM 文本到可验证规则的推荐升级路径

建议未来把 GM 产物的落地路径设计成“逐级升级”，而不是直接从 prose 跳到自由代码。

### Level 0: 纯文本叙事

仅服务本回合反馈，不进入长期规则层。

### Level 1: 文本 + 账本 patch

叙事中提到的实体、位置、visibility、interaction 通过工具落账，但不产生新规则。

这是当前 TextAdv 已经在做的主要部分。

### Level 2: 文本 + Effect 分类

除了 patch 之外，再为关键后果标注“它属于哪类 effect”。

例如：

- reveal
- transfer
- consume
- unlock
- schedule

这一层还不要求完整规则声明，但已经开始为后续验证打地基。

### Level 3: 文本 + 半结构化规则声明

当某个 effect 会在未来反复触发时，要求 GM 或 GM 辅助团队显式写出规则声明。

这里暂不规定 `RuleContract` 的最终字段，只先接受一个事实：

- 这一步已经不再只是 narration；
- 它是对未来可复用机制的声明；
- 因而必须接受额外审查。

### Level 4: 受限 DSL / 受限 C# API

当某类规则已经足够稳定，并且值得被宿主长期依赖时，再把它 lower 成可执行机制。

推荐此时的设计原则是：

- 不让 GM 直接写任意 C#
- 只允许调用有限的 effect-building API
- 只允许访问受控的世界读写接口

## 验证体系建议分成四层

### 1. 玩家输入 validator

继续检查 groundedness。

它负责：

- 玩家有没有越过证据边界
- 玩家是否把猜测写成事实
- 玩家是否拿未完成效果当成已完成事实

它不负责：

- 世界规则长期一致性
- GM patch 是否自洽

### 2. Patch / Invariant Checker

这是未来最应该尽快补的宿主层检查器。

至少应逐步覆盖：

- 引用完整性
- 位置与持有关系一致性
- visibility 目标合法性
- interaction target 合法性
- 效果槽与作用域匹配
- 当前 summary 引用的实体是否已落账

这一层应尽可能少依赖 LLM。

### 3. Rule Reviewer Swarm

当 GM 产出可复用规则声明时，可并行启用多个 LLM reviewer，各自只审一条或一组规则。

建议 reviewer 的职责分离，而不是所有 reviewer 都做同一件事。

例如：

- 一类 reviewer 审触发条件是否充分
- 一类 reviewer 审视角安全与信息泄露
- 一类 reviewer 审是否与现有规则冲突
- 一类 reviewer 审玩家是否会感到“现实常识不通”

这一层很适合吸收“先不考虑 API 成本，只关注可行性”的实验方向。

### 4. Shadow Execution

对于新规则，不必立刻正式接管世界。

可以先让它在若干回合内以 shadow mode 运行：

- 记录它本来会不会触发
- 记录它本来会产生什么 patch
- 与 GM 实际裁决进行比对

这样可以显著降低“新规则刚写出来就污染世界”的风险。

## 关于 `RuleContract` 的当前态度

当前不建议过早追求一个“万能而优雅”的 `RuleContract` 终稿。

更合理的顺序是：

1. 先把世界真相账本边界说清楚。
2. 先把 effect 语义原件说清楚。
3. 先把什么情况下必须升级为“可复用规则声明”说清楚。
4. 在此基础上，再逐渐归纳出最小 `RuleContract`。

因此，当前阶段对 `RuleContract` 只建议保留一个弱承诺：

- 它应该服务于“可审查、可回放、可下沉到运行时”的中层表达。
- 它不应一开始就承担完整的世界建模野心。
- 它首先要能描述 TextAdv 当前最常见的几类 effect，而不是试图覆盖一切。

## 对写实风格路线的额外意义

如果 TextAdv 逐步偏向低奇幻、偏写实、偏真实世界局部模拟，那么这套账本与 effect 框架反而更重要。

原因不是写实会更简单，而是：

- 玩家会更自然地要求常识一致性。
- GM 更难用“世界观特例”掩盖规则空洞。
- 很多裁决都会转化为“现实常识 + 既有局部规则 + 当前证据”的混合判断。

这会进一步抬高对下列能力的要求：

- 世界真相与角色认知的分离
- patch 的语义完整性
- 新规则的审查与灰度启用

## 推荐的近期实现顺序

### Phase A: 澄清 L0 / L0.5 / L1 边界

优先把以下问题写清楚并逐步落实：

- 当前哪些字段属于世界真相
- 当前哪些结果只存在于 summary，未来应升级为 ledger
- 当前事件日志里最少要保留哪些 cause / patch / delivery 信息

### Phase B: 建立 Effect 分类表

先不做通用规则系统，只做一份项目内 effect taxonomy。

至少包含：

- transfer
- reveal / hide
- create
- transform
- unlock / grant-affordance
- consume
- schedule / continue / complete
- knowledge-delivery

### Phase C: 实现最小 `GmInvariantChecker`

让宿主开始真正检查工具产物，而不是主要依赖 prompt 自觉。

### Phase D: 为“会被未来依赖”的效果引入半结构化声明

先从最常见、最容易漂移的 effect 开始：

- 多回合工作
- interaction 消耗
- 新 affordance 解锁
- reveal hidden item / hidden interaction

### Phase E: 实验 reviewer swarm 与 shadow execution

这一步再开始认真评估：

- 并行 LLM reviewer 的效果
- 哪些规则适合保留在半结构化层
- 哪些规则值得编译到受限运行时

## 当前开放问题

下面这些问题是后续最值得继续展开讨论的：

1. 世界真相账本里，哪些“对象状态”还没有被清晰表达出来？
2. 事件日志最小需要记录到什么粒度，才足以支撑规则归因？
3. `interaction` 未来是否要区分“玩家可见描述”和“宿主内部 effect 语义”？
4. 哪些 effect 可以永远保持为宿主内建语义原件，哪些 effect 才需要 GM 声明新规则？
5. reviewer swarm 的任务拆分，最有效的维度是什么？
6. shadow execution 的观测指标应该是什么？
7. 受限 DSL / 受限 C# API 的最小能力集应该长什么样？

## 当前推荐的结论

在现阶段，TextAdv 最应该收敛的不是一个漂亮的万能 `RuleContract`，而是三件更基础的事：

1. 世界真相账本的边界
2. Effect 语义原件的分类
3. GM 文本何时必须升级为“可审查的规则声明”

只要这三件事逐步清楚，后面的 `RuleContract`、reviewer swarm、shadow execution、受限规则代码核心，都会更自然地长出来，而不是靠想象硬拼。
