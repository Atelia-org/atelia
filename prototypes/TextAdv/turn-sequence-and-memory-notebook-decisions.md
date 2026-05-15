# TextAdv — Turn Sequence And Memory Notebook Decisions

> 状态：当前方向收敛记录
> 适用范围：`prototypes/TextAdv/` 原型
> 目的：记录回合步骤、`Memory-Notebook` 状态模型、逐步验证与语料产出的当前共识

## 一句话定位

一个回合不是“看一眼世界然后立刻做一个动作”，而是一份有边界的交卷过程：

1. 系统展示 `Perception-Bundle`
2. Player 可以执行零到多个 `Small-Action`
3. Player 最终必须提交一个终结回合的 `Large-Action`
4. 每一步动作都带 `Reason-Trace` 并经过 validator 检查

因此，游戏的核心输入产物不是单个动作，而是一条从可见信息出发、经过多个 `推理-动作` 步骤、最终收束到终结动作的步骤序列。

## 当前已明确的决定

| 决定 | 为什么重要 | 对实现的直接含义 |
|:---|:---|:---|
| 每个 Player 拥有一个私有的 `Memory-Notebook` | 它是世界内合法的外化记忆载体 | `Memory-Notebook` 必须成为 Player 状态的一部分，而不是 UI 附件 |
| `Memory-Notebook` 是持久状态 | 它承担跨回合连续性 | notebook 快照必须跨回合保存，并在后续回合继续可读 |
| 每回合可以先做零到多个 `Small-Action` | 让整理、记录、局部试探能自然发生 | 回合输入要支持步骤序列，而不是单个动作 |
| 每回合必须以一个 `Large-Action` 收束 | 世界推进需要一个明确的终结点 | world tick 应在 `Large-Action` 被接受后才进入统一结算 |
| 当前唯一已明确的 `Small-Action` 是编辑 `Memory-Notebook` | 这是最核心、最稳定、最直接支撑训练目标的小动作 | 首版应优先把 notebook 编辑路径做实，而不是先铺很多其他小动作 |
| 每一步动作都要附带 `Reason-Trace` | 这是训练价值与防作弊约束的一部分 | 动作输入协议必须同时容纳“动作参数”和“推理参数” |
| 每一步动作都先经过 validator | 质量控制应发生在步骤边界，而不是回合结束后补救 | 系统需要 `submit -> validate -> accept/reject -> feedback` 的循环 |
| semantic validation 采用专用纯 LLM 路线 | groundedness 更像语义裁判任务，而不是简单规则匹配 | 需要专门的 validator prompt、输出协议和宿主包装代码 |
| 完整的已接受步骤序列是核心训练语料 | 真正有价值的不是单个 terminal action，而是过程轨迹 | 系统应保留 `Perception-Bundle -> accepted steps -> final resolution` 的完整链条 |

## 回合步骤模型

### 1. 回合起点

每个回合从系统向 Player 展示当前回合的 `Perception-Bundle` 开始。

在这一时刻，Player 合法可用的输入材料至少包括：

- 当前回合直接可感知的世界外显信息
- 自己私有 `Memory-Notebook` 的当前快照

其他信息块，例如小地图 TUI、Recent History、关系面板、配方面板，当前仍属于待实验项。

### 2. Small-Action

`Small-Action` 指不会直接结束回合的动作步骤。

当前最明确的小动作是：

- 编辑 `Memory-Notebook`

候选但尚未正式钉死的小动作包括：

- 主动向其他 Player 问话
- 试验配方
- 其他局部试探或整理行为

首版当前不限制 `Small-Action` 的数量，但这只是为了快速原型，不是长期承诺。后续很可能引入动作点数或其他预算模型。

### 3. Large-Action

`Large-Action` 指会导致当前回合结束的动作。

它承担两个职责：

- 作为本回合最终要落地的高层选择
- 作为 world tick 和多方同步结算的触发点

因此，一个回合可以没有任何 `Small-Action`，但不能没有 `Large-Action`。

### 4. Canonical Turn Trace

从训练和回放角度看，一个回合的 canonical trace 应优先由下列内容构成：

1. `Perception-Bundle`
2. notebook 起始快照
3. 一系列已接受的 `Small-Action` / `Large-Action`
4. 每一步对应的 `Reason-Trace`
5. validator 的通过结果
6. `Large-Action` 触发后的世界结算结果
7. notebook 终态快照或增量

这条链更像“考试交卷的完整答题过程”，而不是传统游戏里的一串按钮日志。

## Memory-Notebook 状态模型

### 1. 身份与可见性

每个 Player 拥有一份私有的 `Memory-Notebook`。

当前默认假设是：

- notebook 属于游戏世界内对象，而不是系统外外挂
- notebook 内容默认只对所属 Player 可见
- 其他 Player 若要接触 notebook，应通过显式世界机制，而不是系统侧直接共享

### 2. 持久性

`Memory-Notebook` 是跨回合、跨日持续存在的持久状态。

它不是最近历史的镜像，也不是系统自动生成的 recap 替代品。

它的价值恰恰在于：Player 必须主动决定什么值得记，如何记，以及如何依赖自己留下的记忆。

### 3. 编辑时机

当前收敛的时机模型是：

- 在 `Perception-Bundle` 展示之后，Player 可以通过 `Small-Action` 编辑 notebook
- 每次被接受的 notebook 编辑，应立即成为同一回合后续步骤可见的当前 notebook 快照
- 一旦 `Large-Action` 被接受，本回合结束，不再继续插入 notebook 编辑

换句话说，notebook 编辑是回合内步骤的一部分，而不是只允许在回合尾声集中结算的附带操作。

### 4. 首版内容模型

首版不必过早把 notebook 做成重结构化数据库。

更稳妥的起点是：

- 以玩家可编辑文本为主
- 允许形成事实、假说、待办、疑点等自然结构
- 暂不强制把这些结构编码成很多硬字段

如果实践表明某些结构长期稳定，再逐步把它们提升为显式 schema。

## Reason-Trace 与逐步验证

### 1. 每一步都要有理由

当前方向是：不是只有 `Large-Action` 需要理由，而是每一步动作都需要推理性说明。

但这不意味着每个 `Small-Action` 都必须写成长篇大论。更合理的目标是：

- `Small-Action` 提供局部、简短、可核验的理由
- `Large-Action` 提供更完整的终局选择理由

### 2. validator 的职责

validator 的核心职责不是判断 Player 是否“猜中了世界真相”，而是判断这一步推理是否 grounded 于合法证据。

至少包括：

- 是否只依赖当前可见信息与 notebook 中已存在的内容
- 是否把猜测误写成确定事实
- 是否存在明显跳步、偷用外部记忆或无依据结论

### 3. 纯 LLM validator

当前更适合首版的路线是：semantic validation 由专用纯 LLM validator 负责。

这里的“纯 LLM”指语义裁判本身由 LLM 完成；宿主代码仍然负责：

- 组织输入材料
- 调用 validator
- 解析 validator 输出
- 把反馈回传给 Player

### 4. 失败反馈

当 validator 不通过时，系统应把不合理之处反馈给 Player，使其可以修正并重试。

这件事不只是防作弊，也是在帮助 LLM Player 学会更稳健地组织推理路径。

## 当前最值得警惕的边界

### 1. 并非所有候选 `Small-Action` 都天然安全

编辑 notebook 显然适合作为 `Small-Action`，因为它主要影响 Player 的私有可用状态。

但“主动向其他 Player 问话”“试验配方”这类候选动作，未必都能直接放进同一层级。

原因是它们可能：

- 引入新的外部信息
- 触发资源消耗
- 需要其他主体响应
- 制造回合内的来回互动循环

因此，首版更稳妥的做法是先把 notebook 编辑做实，再审查哪些候选动作真的适合归入 `Small-Action`。

### 2. 不要让系统 Recent History 取代 notebook

如果系统自动提供太强的 Recent History，Player 对 notebook 的依赖就会被削弱。

因此，若后续要提供历史相关信息，应该优先考虑：

- 只展示当前回合已接受步骤的局部轨迹
- 慎重评估是否要给跨回合自动历史面板

否则会削弱 `Amnesia` 主题的核心约束。

## 当前仍待定的问题

1. validator 的输出协议应多结构化到什么程度
2. validator 失败是否存在次数上限、代价或冷却
3. 哪些候选 `Small-Action` 会被正式纳入首版
4. `Small-Action` 是否会在后续引入动作点或预算系统
5. notebook 是否要保留面向训练的数据级 diff / revision 视图

## 推荐的下一步设计重心

在这份方向基本成立之后，下一阶段最值得澄清的是：

1. `Perception-Bundle` 的 Core vs Optional 边界
2. validator 的输入/输出协议
3. `Small-Action` 的分类规则：哪些只影响本地状态，哪些必须进入世界级结算
