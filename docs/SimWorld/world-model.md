# SimWorld 最小世界模型

> 用途：把“山谷小村 + 主体画像”的讨论压缩成一个可实现、可查询、可驱动事件队列的最小世界模型。

## 结论先行

SimWorld 的最小世界模型应明确分为三层：

- World Skeleton：地点、连接、资源、事件、可见性、时间
- Inhabitant Profile：主体的 needs、risk、social grammar 与 affordance
- Narrative Projection：把世界真相和主体状态投影成可供 LLM 使用的观察文本

首版实现时，最容易犯的错误是把这三层揉在一起：

- 把世界约束藏在 prompt 里
- 把主体特性写死在场景文案里
- 把可见性与真相混成一份文本描述

这个文档的目的就是把这些边界先钉住。

## 一张总图

```text
World Skeleton
  ├─ Location / Connection
  ├─ Resource / Ownership / Possession
  ├─ Action Reservation / Event Queue
  └─ Visibility / Observation Delivery

Inhabitant Profile
  ├─ Embodiment
  ├─ Need Channels
  ├─ Appraisal Channels
  ├─ Social Norms
  └─ Affordances

Narrative Projection
  ├─ Perception Packet
  ├─ Narrator Rendering
  └─ Agent-facing Observation Text
```

## 核心设计原则

### 1. 世界骨架先于主体皮肤

地点图、资源占用、事件顺序、观察传播，应当在不依赖“主体是不是人”的情况下成立。

### 2. 主体画像改变的是“压力函数”，不是“世界 API”

Human-Lite 和 Household-Robot-Lite 都应能共享大体一致的世界查询与行动接口。

它们最大的区别不应是“有没有完全不同的引擎”，而应是：

- 什么东西会持续消耗
- 什么东西会触发中断
- 什么东西会被视为高风险
- 什么东西会被优先告知

### 3. 观察是投影，不是真相本身

世界中实际发生的事，与某个主体收到的 observation，不应混同。

这对后续实现以下能力是硬要求：

- 错过
- 传话
- 谣言
- 误判
- 追问
- 隐瞒

## 最小实体集合

首版无需追求“大而全”，但至少要有下列实体。

### 1. Location

表示地点图中的节点。

建议最小字段：

| 字段 | 含义 |
|------|------|
| Id | 稳定地点标识 |
| Name | 人类可读名称 |
| Tags | 如 public / resource / worksite / private |
| Notes | 非规范性说明，供 narrator 使用 |

对山谷小村而言，对应的是：

- 山口哨点
- 村口主路
- 中央空地
- 住屋区
- 水井
- 谷仓
- 工坊
- 农田
- 林地入口
- 后坡小路

### 2. Connection

表示地点之间的边。

建议最小字段：

| 字段 | 含义 |
|------|------|
| Id | 稳定边标识 |
| From / To | 起点与终点 |
| TraversalCost | 基础耗时 |
| VisibilityClass | 公开 / 常规 / 隐蔽 |
| IsPassable | 当前是否可通行 |
| IsVisibleThrough | 当前是否可视通 |
| Conditions | 额外条件，如权限、钥匙、时段 |

这已经足以表达首版的：

- 绕行
- 尾随
- 夜巡
- 封路
- 山口守门

### 3. Inhabitant

表示世界中的一个行动主体。

建议最小字段：

| 字段 | 含义 |
|------|------|
| Id | 稳定主体标识 |
| DisplayName | 对外显示名称 |
| ProfileId | 指向 inhabitant profile |
| CurrentLocationId | 当前地点 |
| TransitState | 是否在移动中，若是则包含目标与到达时间 |
| RoleTags | 如 steward / guard / courier / craft |
| RelationHandles | 与其他主体的关系句柄或引用 |

注意：

- Inhabitant 是世界中的实体
- Human-Lite / Household-Robot-Lite 是 profile

两者不能混成一个类型。

### 4. Inhabitant Profile

表示一类主体的画像模板。

建议最小字段：

| 字段 | 含义 |
|------|------|
| Id | Profile 标识 |
| Embodiment | 移动、携带、感知、脆弱性等 |
| NeedChannels | 持续需求通道 |
| AppraisalChannels | 紧迫/危险/债务等评价通道 |
| Affordances | 可做动作种类 |
| SocialNorms | 承诺、服从、共享、声誉等倾向 |
| NarrativeSkin | narrator 与 prompt 使用的渲染偏好 |

### 5. Need State

主体当前的需求状态，应与 profile 中的 channels 对齐。

建议最小字段：

| 字段 | 含义 |
|------|------|
| OwnerId | 主体 |
| Channel | 如 hunger / rest / charge / maintenance |
| Level | 当前程度 |
| Trend | 上升/下降趋势 |
| ThresholdState | normal / warning / critical |

对于 Human-Lite：

- hunger / thirst / rest 可以存在，但保持粗粒度

对于 Household-Robot-Lite：

- charge / maintenance / damage-risk 更重要

### 6. Resource Handle

表示会影响任务规划的关键资源或物品。

建议最小字段：

| 字段 | 含义 |
|------|------|
| Id | 稳定资源标识 |
| Kind | tool / food / water / material / key-item |
| LocationId | 若在地点中 |
| HolderId | 若被主体持有 |
| QuantityOrState | 数量或状态，如 usable / broken |
| ReservationState | 是否已被某行动预约 |

MVP 不必先做通用容器系统，但必须能表达：

- 某把斧头现在在工坊
- 某个水桶被某个主体拿着
- 粮食目前在谷仓库存中

### 7. Task Commitment

表示主体已接受或被分配的任务承诺。

建议最小字段：

| 字段 | 含义 |
|------|------|
| Id | 任务标识 |
| OwnerId | 责任主体 |
| GoalKind | 任务类型，如 deliver / repair / guard |
| TargetRef | 目标地点、资源或主体 |
| DueTime | 时限 |
| Preconditions | 执行前置条件 |
| Status | planned / active / blocked / done / failed |

### 8. Action Reservation

表示一个已经计划好、但尚未真正生效的未来动作。

这是“动作耗时 + 事件队列”模型的核心对象。

建议最小字段：

| 字段 | 含义 |
|------|------|
| Id | 预约动作标识 |
| ActorId | 执行者 |
| ActionKind | move / take / deliver / inspect / report 等 |
| TargetRef | 目标引用 |
| ScheduledTime | 计划出队时间 |
| QueueOrderKey | 同时刻事件的稳定排序键 |
| Preconditions | 出队时要重验的条件 |
| SuccessTemplate | 成功后的状态变化模板 |
| FailureTemplate | 失败/打断后的结果模板 |

其中 `QueueOrderKey` 的目的不是暴露某种特定实现，而是保证：

- 当多个 reservation 具有相同 `ScheduledTime` 时
- 队列仍然可以以稳定、可重放、可解释的顺序出队

这对“单工具冲突”这类 case 是硬要求。

### 9. World Event

表示已经实际发生过的事件。

建议最小字段：

| 字段 | 含义 |
|------|------|
| Id | 事件标识 |
| Time | 发生时间 |
| Kind | movement / transfer / failure / sighting / rumor 等 |
| Participants | 涉及主体 |
| AffectedRefs | 受影响对象 |
| GroundTruthPayload | 世界真相层记录 |

Action Reservation 是未来式，World Event 是过去式。两者应明确区分。

### 10. Observation Delivery

表示某条事件如何传达到某个主体。

建议最小字段：

| 字段 | 含义 |
|------|------|
| EventId | 来源事件 |
| ReceiverId | 接收者 |
| DeliveryTime | 何时收到 |
| DeliveryMode | direct / overheard / reported / inferred |
| Certainty | certain / partial / rumor |
| ProjectionHint | narrator 渲染提示 |

这个实体是实现“真相与感知分离”的关键。

## 首版查询接口应围绕什么来设计

不是所有信息都该让 LLM 直接碰底层实体。更实用的做法是围绕高价值查询面设计。

### 1. 空间与移动

- 当前主体所在地点是什么
- 从这里能到哪些地点
- 每条路的大致耗时与可见性如何
- 某个目标地点当前是否可达

### 2. 可观察信息

- 当前地点有哪些主体
- 当前地点有哪些关键资源
- 最近有哪些新变化与异常
- 哪些信息是直接所见，哪些是传闻

### 3. 任务与承诺

- 我当前手上有哪些任务
- 哪些任务快要逾期
- 哪些前置条件已失效

### 4. 风险与需求

- 我当前的高优先级风险是什么
- 哪个 need channel 正在进入 warning / critical
- 我现在最不该做什么

## 事件生命周期建议

最小事件生命周期可收敛为：

1. Agent 产生意图
2. 意图被翻译为 Action Reservation
3. Reservation 进入事件队列，等待到时
4. 出队时重验 Preconditions
5. 生成成功或失败的 World Event
6. 根据可见性与传播规则生成 Observation Delivery
7. Narrator 把 delivery 投影成观察文本

这条链如果做稳了，后面很多机制都能自然长出来。

## Human-Lite 与 Household-Robot-Lite 在这个模型里怎么接入

### Human-Lite

更关注：

- 饥饿
- 口渴
- 休息
- 公开失败
- 承诺与互惠
- 受伤或不适风险

### Household-Robot-Lite

更关注：

- 电量
- 维护状态
- 碰撞风险
- 禁区与权限
- 任务切换成本
- 不可自愈损伤

但它们共享的世界接口仍然可以高度一致：

- MoveTo(location)
- Take(resource)
- Deliver(target)
- Report(to, info)

若某些世界里“占位等待”本身具有外显语义，还可以额外提供：

- HoldPositionUntil(time)
- StandWatchUntil(time)

这正是“骨架复用，画像切换”的价值所在。

需要额外强调：

- 对 SimWorld MVP，更推荐默认采用同步的 `CallAndWait` 语义
- 也就是 world action 一旦发出，本轮结束，执行器推进到结果 observation 再返回
- 因此协议层的 `EndTurn` / `YieldControl` 不应与世界动作接口混成一类

## 首版刻意不纳入的东西

为了保护 MVP，以下内容暂不进入最小世界模型：

- 地点内格子与几何遮挡
- 连续物理模拟
- 复杂经济价格系统
- 高保真身体生理学
- 完整心理学人格系统
- 通用容器树与嵌套库存

这些后续都可以加，但当前不应抢占主链。

## 对下一步文档的直接要求

有了这个模型，下一份最自然的文档应当是：

- [valley-village-user-stories.md](valley-village-user-stories.md)

它要做的不是再讲世界观，而是把：

- 单工具冲突
- 送达错位
- 山口来客
- 夜间传闻
- 临时补位

这些情境明确写成：

- 初始世界状态
- 参与主体 profile
- 事件顺序
- 预期 observation
- 通过/失败判定
