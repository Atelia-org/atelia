# TextAdv2 - Spatial Foundation Near-Term Plan

> 状态：近期目标与设计草案
> 适用范围：`prototypes/TextAdv2/`
> 目标：先把“角色在世界里稳定溜达”这件事做顺，再逐步叠加更复杂的游戏语义

## 一句话结论

这条近期计划是合理的，而且顺序基本正确。

如果 TextAdv2 当前阶段的目标是先成为一个扎实的“文字版旅游模拟器”，那么最稳妥的路线就是：

1. 先把 `WorldTruth` 层的空间模型做稳，并用程序化测试地图覆盖当前已支持的全部空间语义。
2. 再做非文学化的 `ReadOnlyView` 投影，让“当前地点可见什么”成为稳定、可测试、可复用的数据接口。
3. 最后基于稳定图模型做 A* 寻路，把移动、导航、路线评估建立在同一套世界真相上。

这个顺序的好处是：后面的信息呈现和寻路都不需要反向塑形世界模型，避免为了 UI 或算法临时改坏底层 schema。

## 近期目标

TextAdv2 近期阶段的核心目标不是叙事，不是物品系统，也不是 GM 裁决，而是把“空间”这件事先做成可靠基础设施。

具体来说，近期应达成：

- 世界里存在一张可持久化、可重开、可枚举、可验证的测试地图。
- Actor 可以稳定地占据一个 Location，并沿 Passage 在世界中移动。
- 系统可以在某个 Location 上生成非文学化的“可见信息集合”。
- 系统可以基于空间图做路径搜索、路线比较和导航建议。

达到这一步之后，TextAdv2 就已经拥有一个很好的“旅游模拟器”底盘：角色能观察地点、看见出口、理解路线差异、做出移动决策，并且这些行为都建立在稳定持久化世界之上。

## 近期非目标

以下内容当前 SHOULD 明确延后，避免空间地基还没稳就把关注点打散：

- Item / inventory / ownership 系统
- 叙事性文本润色与文学化渲染
- 动态 GM / narrator / scenario resolution
- 性能导向的缓存、反向索引和预计算图
- 多层级可见性、遮挡、雾区、复杂感知系统

这不是说这些不重要，而是它们都更适合建立在“空间真相 + 空间视图 + 空间导航”已经稳定之后。

## 顶层命名空间分层

TextAdv2 顶层命名空间当前建议固定为三层：

| 命名空间 | 责任 | 当前状态 |
|:---|:---|:---|
| `Atelia.TextAdv2.WorldTruth` | 世界唯一真相；持久化 schema；ID、连接、位置、规则约束 | 当前正在建设 |
| `Atelia.TextAdv2.AccelerationIndex` | 为查询、投影、导航服务的缓存、索引、派生结构 | 已开始最小引入，用于 landmark heuristic snapshot |
| `Atelia.TextAdv2.ReadOnlyView` | 面向调用方的只读投影；把真相组织成“看见什么”“能去哪”这类稳定视图 | 已建立并被观察 / 导航 / 路由组件使用 |

近期阶段的一个重要约束是：

- `WorldTruth` MUST 不依赖 `AccelerationIndex` 或 `ReadOnlyView`。
- `ReadOnlyView` MUST 只读 `WorldTruth`，不能自己持有另一份业务真相。
- `AccelerationIndex` 即使将来存在，也 MUST 只是可丢弃、可重建的派生层，不得成为唯一真相。

## 当前 WorldTruth 空间模型

当前主线已经开始形成下面这套边界：

- [`WorldState`](../../prototypes/TextAdv2/WorldTruth/WorldState.cs)：世界真相层 graph root，持有 `locations` 和 `passages` 两个 ledger。
- [`Location`](../../prototypes/TextAdv2/WorldTruth/Location.cs)：地点本体，只保存地点自身信息。
- [`Passage`](../../prototypes/TextAdv2/WorldTruth/Passage.cs)：跨地点连接的唯一真相。
- `PassageEndpoint`：某一端对这条路的本地命名与提示。
- `PassageDirectionRule`：某一方向独有的规则与额外代价。

这套模型的核心决策是：

- 共享事实只在 `Passage` 上写一份。
- 本地出口视角只在 `PassageEndpoint` 上写。
- 方向差异只在 `PassageDirectionRule` 上写。
- `Location` 不再长期持有 `exitName -> targetLocationId` 这类第二份真相。

这条边界 SHOULD 继续保持稳定，因为它正好避开了旧模型最容易腐烂的地方：双端重复、局部修补、久而久之互相打架。

## 为什么这条路线合理

你提议的三步，其实正好对应三种不同职责：

1. 程序化测试地图：验证 `WorldTruth` 是否足够表达空间。
2. 非文学化信息呈现：验证 `ReadOnlyView` 是否足够稳定清晰。
3. A* 寻路：验证空间图是否足够一致、可计算。

这三步的依赖方向是对的：

- 没有稳定空间图，信息呈现就只能硬编码。
- 没有稳定信息投影，角色移动与调试就很难观察。
- 没有稳定图模型，A* 会把临时字段和表现层偶然细节固化进算法里。

所以这不是“先做个地图 demo 再说”，而是在按正确因果顺序收敛世界结构。

## Phase 1：程序化测试地图

第一阶段建议目标不是“做一张好玩的图”，而是“做一张覆盖当前空间语义的图”。

测试地图 SHOULD 至少覆盖下面这些情况：

| 功能点 | 测试地图中应出现的样例 |
|:---|:---|
| 普通双向陆路 | `village <-> square` |
| 双向但代价不对称 | 上坡 / 下坡山道 |
| 单向通行 | 只能顺流而下的水路，或只能单向开启的门 |
| 不同 travel mode | `land` / `water` / `portal` |
| Passage 级共享说明 | 整条路泥泞、结冰、起雾 |
| Endpoint 本地说明 | 同一 Passage 在两端叫法不同，例如“北门”/“回城路” |
| 方向级别说明 | 某方向逆风、逆流、需要攀爬 |
| 暂时不可逆 | 某方向 `enabled = false` |

第一阶段不必追求自动生成地图，也不必追求文学描述。程序化 builder 即可，只要能稳定生成、稳定 reopen、稳定验证。

### 第一阶段产物建议

- 一个固定测试世界的 bootstrap 入口
- 一个“打印世界空间结构”的调试命令或 demo 程序
- 一组围绕 Passage 建模边界的单元测试
- 一组围绕 reopen 后一致性的持久化测试

### 第一阶段验证问题

完成后应能明确回答：

- 我们能否不依赖任何缓存，就正确枚举某个地点的所有出口？
- 一条路的共享事实是否真的只维护一份？
- 不同方向的差异是否不需要复制整条路？
- reopen 后图结构、方向规则和 travel cost 是否保持一致？

## Phase 2：非文学化可见信息集合

第二阶段建议引入 `ReadOnlyView`，但先只做“结构化观察”，不做文学表达。

这里的目标不是生成漂亮文本，而是定义一个稳定接口：

“当一个 actor 站在某个 location 时，系统能返回哪些可见空间信息？”

建议的首版投影内容：

- 当前 Location 的 `id`、`name`、`description`
- 从当前位置可见的所有出口项
- 每个出口项对应的目标 Location 基本信息
- 每个方向的 travel mode、基础代价、方向修正、共享说明、方向说明

首版 SHOULD 以 record / DTO / struct 化对象作为输出，而不是直接拼自然语言字符串。

原因很简单：

- 结构化视图更适合测试。
- 结构化视图更适合后续喂给 renderer、CLI、LLM、debug dump。
- 一旦先写文学文本，后面几乎一定会为了调试再倒着拆回结构化字段。

### ReadOnlyView 首版建议

可考虑提供类似下面的结构：

```csharp
LocationObservation
  - locationId
  - locationName
  - locationDescription
  - exits: ExitObservation[]

ExitObservation
  - passageId
  - exitName
  - targetLocationId
  - targetLocationName
  - travelMode
  - baseTravelCost
  - travelCostModifier
  - totalTravelCost
  - sharedConditionNote
  - directionalConditionNote
  - localViewNote
  - isEnabled
```

这层输出 SHOULD 保持“非文学化、低歧义、低推断”的气质。它是观察数据，不是 narrator 文案。

## Phase 3：A* 寻路

第三阶段做 A* 是合理的，但我建议首版先做 **Location 节点 / Passage 有向边** 的寻路，而不是一上来就做 `PassageEndpoint` 粒度。

### 为什么首版先用 Location 级图

在当前模型里，绝大多数导航问题都天然是：

- 节点：Location
- 边：Passage 的某个方向
- 边权：`BaseTravelCost + TravelCostModifier`
- 可达性：`PassageDirectionRule.IsEnabled`

这已经足够表达：

- 单向路
- 非对称耗时
- 不同 travel mode
- 路线偏好
- 最短路 / 最省力路

也就是说，在当前阶段，Location 级图已经和世界真相边界天然同构，工程复杂度最低。

### PassageEndpoint 级寻路何时才值得引入

只有当下面这些需求真正出现时，才建议升级到 `PassageEndpoint` 级图：

- 同一 Location 内部存在多个互不等价的进出端口
- 进入同一 Location 后，还需要在内部子空间中移动到某个具体出口
- 某些规则绑定在“从哪个入口进入”而不是“到达哪个 Location”
- 一个大型 Location 需要内部导航，而不仅仅是作为一个抽象节点

换句话说，`PassageEndpoint` 级寻路是为“地点内部也开始空间化”准备的；当前阶段大概率还没到那一步。

### A* 的职责边界

路径搜索 SHOULD 是读取 `WorldTruth` 的算法组件，而不是把路径结果写回真相层的业务对象。

它可以输出：

- 推荐路径
- 总 travel cost
- 经过的 passage 序列
- 每一步的出口名和目标地点

但它不应反向改变世界结构，也不应要求在 `WorldTruth` 中提前存一份导航缓存。

近期主线已先落下一个最小 heuristic seam，并开始在 `AccelerationIndex` 中试行基于 landmark 的 lower-bound snapshot。
更细的近期方案见 [`landmark-heuristic-near-term-plan.md`](./landmark-heuristic-near-term-plan.md)。

## 角色在空间中的最小语义

既然近期目标是“让角色能在世界里溜达”，那么角色首版其实只需要非常少的空间字段。

Actor 的空间相关真相，近期 MAY 只包含：

- `id`
- `name`
- `currentLocationId`

然后围绕它定义两个最小操作：

- 获取当前位置观察：`GetLocationObservation(actor.CurrentLocationId)`
- 沿某个 Passage 方向移动：`MoveActorAlongPassage(actorId, passageId)` 或等价接口

这样可以把“角色移动”明确约束成空间图上的合法状态迁移，而不是未来任何系统都能随手改 `locationId`。

## 推荐的近期实施顺序

### Step 1

补齐 `WorldTruth` 的测试地图 bootstrap，并让它稳定覆盖当前所有 Passage 语义。

### Step 2

补一个最小 Actor ledger，让 actor 可以占据 location 并执行合法移动。

### Step 3

新增 `ReadOnlyView` 的 `LocationObservation` / `ExitObservation` 一类只读 DTO。

### Step 4

让 CLI / demo / debug 输出改用 `ReadOnlyView`，不再直接拼读底层 durable schema。

### Step 5

实现 Location 级 A*，把方向规则和 travel cost 纳入边权。

### Step 6

等 Location 级 A* 真不够了，再评估是否需要 PassageEndpoint 级图。

## 近期阶段的固定约束

为了避免后面“改着改着又回到双端重复”，近期阶段建议锁定下面这些约束：

- `Location` MUST NOT 重新引入长期维护的 `exitName -> targetLocationId` 真相字段。
- `Passage` MUST 继续作为跨地点连接的唯一真相。
- `ReadOnlyView` MUST 是只读投影，不得回写业务真相。
- A* 首版 SHOULD 基于 Location 图实现，而不是直接把 `PassageEndpoint` 提前升格成主导航节点。
- 在没有明确性能瓶颈之前，`AccelerationIndex` SHOULD NOT 提前介入空间建模。

## 一个务实判断

如果 TextAdv2 近期真能把下面这几件事做扎实：

- 一张稳定、可持久化、可验证的空间测试地图
- 一个干净的非文学化当前位置视图
- 一个可靠的 Location 级导航能力

那么它就已经不是“还没开始做游戏”，而是已经拥有了一个很像样的空间模拟底盘。

在那之后，不管你要往旅游模拟器、文字冒险、叙事沙盒还是多 Actor 世界推进，都会轻松很多。
