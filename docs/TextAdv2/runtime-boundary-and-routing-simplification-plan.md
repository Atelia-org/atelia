# TextAdv2 Runtime Boundary And Routing Simplification Plan

> 状态：主线收口已基本完成，本文改为“基于当前实际代码”的后续低优先级清单
> 适用范围：`prototypes/TextAdv2/`、`prototypes/TextAdv2.GameServer/`、`prototypes/TextAdv2.E2eCli/`
> 前提：当前没有旧数据、没有已投入使用的稳定 API、没有兼容性包袱

## 0. 文档目的

这份文档不再描述最初那一轮大重构提案，也不再把已经完成的小包继续写成“下一拍计划”。

截至当前代码，TextAdv2 的主线结构收口已经基本完成。本文现在只做三件事：

- 如实记录当前实际状态
- 明确哪些设计结论已经定案，不再反复重开
- 把剩余事项收缩成少量可选、低优先级、可独立验收的 backlog

默认建议已经不是“继续开下一轮结构重构”，而是先停在当前边界上。只有当某个低优先级问题真的值得做时，再单独开小包处理。

## 1. 当前实际状态

### 1.1 已完成的主线收口

当前代码已经具备下面这些结构性事实：

- `WorldSession` 已移除，公共运行入口是 `Runtime/SerialWorldRuntime.cs`
- durable world 与 process-local runtime state 已拆开：
  - `Runtime/WorldHost.cs`
  - `Runtime/WorldRuntime.cs`
  - `Runtime/SerialWorldRuntime.cs`
- `RuntimeEpochId` 仍留在 runtime seam，不进入 machine-facing DTO
- routing 主实现已经稳定落在 `Routing/`
- directed travel cost 的权威入口已收口到 `WorldTruth/Passage.cs` 的 `GetTotalTravelCostFrom(locationId)`
- `PassageView/*View` 已删除，projector / dump / authoring 直接消费 `Passage`
- `Observation/` 已成为现行 observation seam 命名，`ReadOnlyView` 已退出活跃代码路径
- `ActorContextObservation` 已收口到：
  - `AvailableMoves` 是唯一 canonical actor-facing action surface
  - `CurrentLocation` 不再重复暴露 `Exits`
- route-plan 相关 helper 的物理落点已经稳定：
  - machine-facing contract 在 `Observation/LocationRoutePlan.cs`
  - planner 在 `Routing/LocationRoutePlanner.cs`
  - wire/token helper 与 JSON codec 在 `DevSupport/`
  - dev-only text renderer 在 `DevSupport/LocationRoutePlanTextRenderer.cs`
- canonical machine surface 的活跃 parity guard 已补到当前需要的基线：
  - `observe actor context` 已守住 canonical action surface
  - `observe actor` / `observe location` 已补显式 JSON shape guard

### 1.2 当前实际分层

按今天的代码看，TextAdv2 可以理解为下面几层：

- `WorldTruth`
  - durable authoritative truth
  - actor / location / passage / logical time 的最小业务规则
- `Runtime`
  - movement trace
  - route acceleration cache
  - `SerialWorldRuntime` façade
- `Routing`
  - navigation graph
  - planner
  - heuristic
  - graph signature
- `Observation`
  - machine-facing observation contract 与 projector
- `DevSupport`
  - host JSON codec
  - dev text renderer
  - 调试 / 文字化辅助逻辑

`DevSupport` 不是核心业务层，但它现在已经是 route-plan token helper、JSON codec、text renderer 的稳定落点。

### 1.3 当前最重要的判断

当前主线已经跨过“先把边界拆清”的阶段。

也就是说：

- 不再存在一个明显值得立即继续推进的结构性大包
- 当前最合理的默认动作是停在这里
- 剩余问题主要只剩低优先级的 contract hardening tail

如果后续继续动，应该按“一个低噪音小包 + 明确完成定义”的方式推进，而不是重开一轮大重构。

## 2. 已定设计结论

除非后续发现明确 bug 或有新需求直接冲突，下面这些结论视为已定，不再重开。

### 2.1 runtime boundary

- `WorldHost` 代表 durable world handle
- `WorldRuntime` 代表 process-local runtime state
- `SerialWorldRuntime` 只是当前单世界串行模型下的 façade
- 不再恢复 `WorldSession` 这类混合主对象

### 2.2 routing ownership

- `Routing/` 是 routing graph / planner / heuristic / signature 的主语义归属
- `WorldTruth/Passage` 是 travel cost 权威源
- 不再把 routing 逻辑迁回 `Observation`

### 2.3 machine-facing action surface

- `ActorContextObservation.AvailableMoves` 是唯一 canonical actor-facing action surface
- `ActorContextObservation.CurrentLocation` 不再重复承担地点级 exits 列举
- 需要完整地点出口观察时，走 `observe location`

### 2.4 route-plan owner baseline

现阶段 route-plan 的 owner 划分已经足够简单，后续不再默认继续拆：

- `Observation/LocationRoutePlan.cs`
  - machine-facing route-plan contract
- `Routing/LocationRoutePlanner.cs`
  - 直接产出该 contract
  - 不额外引入中间 routing result / projector
- `DevSupport/RoutePlanStatusWireToken.cs`
  - canonical token helper
- `DevSupport/TextAdv2Json.cs`
  - host JSON codec registration
- `DevSupport/LocationRoutePlanTextRenderer.cs`
  - dev-only text helper

### 2.5 seam naming

- `Observation/` 是现行 seam 命名
- `PassageView/*View` 不再恢复
- `ReadOnlyView` 不再作为现行层名返回活跃代码路径

## 3. 剩余低优先级债务

### 3.1 optional contract hardening

当前 canonical machine surface 的 guard 已经足够覆盖主线基线，但仍有一些“可以更严、但不是现在必须做”的项，例如：

- `observe actor context` 目前只守住了 `currentLocation` 的字段集合和 `availableMoves` 的存在性，还没显式钉住 `availableMoves[]` item shape
- route-plan seams 当前已有 parity guard，但还没有像 `observe actor` / `observe location` 那样的显式 JSON shape guard
- 一些 canonical seam 仍主要依赖 parity，而不是更细粒度的结构 guard

这类工作有价值，但已经属于 test hardening backlog，不再是主线结构风险。

## 4. 修订后的设计原则

### 4.1 默认停止继续结构重构

只要当前边界已经清楚、测试守住了，就不要为了“更纯粹”再继续拆层或改名。

### 4.2 若继续，优先做低 blast radius 的测试硬化

在今天的代码状态下，最容易产生真实收益的后续动作，通常是补更显式的 contract guard，而不是再继续做结构迁移。

### 4.3 host wording cleanup 只视为 host-only 清理

已完成的 host wording cleanup 也应只被理解为 host-only 术语清理，而不是重新包装成 domain boundary refactor。

### 4.4 保持单世界串行模型优先

TextAdv2 当前仍是：

- 单世界
- 单进程内串行逻辑模型
- 面向小世界模拟的 Agent RL Gym

因此后续设计仍应优先：

- 简单
- 清楚
- 易测
- 可检视

而不是为未来多世界 / 并发 / 分布式预埋复杂抽象。

## 5. 已完成工作包

下面这些工作包已经完成，不再写成未来计划：

1. runtime boundary freeze
2. routing ownership consolidation
3. `PassageView` 删除
4. actor-context canonical action surface 收口
5. route-plan helper 拆到 `DevSupport`
6. `ReadOnlyView -> Observation` 命名收口
7. `observe actor` / `observe location` JSON shape guards
8. route-plan owner / wording cleanup
9. host wording cleanup（`RuntimeService` / `/admin/runtime-status` / host payload `runtime`）
10. active doc / comment hygiene（活跃文档、xml doc 与注释中的旧阶段措辞清理）

## 6. 可选小包与完成记录

下面这些都不是“下一步必须做”的主线事项；其中已完成的小包保留在此，主要用于记录边界，未完成的则只有在确有收益时才建议启动。

### 6.1 Optional P1：canonical contract hardening tail

这是剩余 backlog 里最像“继续做也合理”的一包。

#### 目标

- 为仍缺少显式结构 guard 的 canonical seams 补测试硬化

#### 建议做法

1. 评估是否为 `observe actor context` 增加 `availableMoves[]` item shape guard
2. 评估是否为 actor/location route-plan seams 增加显式 JSON shape guard
3. 保持改动只落在 parity suite 或相关 contract 文档，不改 runtime 代码

#### 明确不做

- 不改 DTO
- 不改 endpoint
- 不改 JSON shape
- 不扩大成 host wording cleanup

#### 验证

- 受影响的 `CrossHostMachineContractParityTests`
- 必要时补跑相关 route-plan / host integration tests

### 6.2 已完成的 Optional P2：active doc / comment hygiene

#### 目标

- 清理活跃文档与活跃 xml doc / 注释中的旧层名 / 旧阶段措辞
- 避免继续用历史包名、阶段标签、旧主对象名来描述当前边界

#### 建议做法

1. 优先清活跃文档
2. 同步清理仍会把读者拉回旧阶段语境的活跃 xml doc / 注释
3. 不清 archive，也不把这包扩大成新的设计收口工程

#### 完成情况

- 已清理 canonical machine surface 活跃参考文档中的历史阶段标签与旧 runtime 归属措辞
- 已清理 `DevTextRenderer`、`NavigationObservation`、`SerialWorldRuntime` 中会误导到旧阶段语境的 xml doc / 注释

### 6.3 已完成的 Optional P3：host wording cleanup

这包已经完成，因此不再属于剩余 backlog；这里保留完成记录，主要用于说明它的边界和为什么不应该被误读成 domain boundary 重构。

#### 目标

- 统一 `TextAdv2.GameServer` 的 host-only 术语

#### 建议做法

1. 已一次性统一：
   - `RuntimeService`
   - `/admin/runtime-status`
   - status payload 中的 `runtime` / `runtimeOpenMode`
2. 同步更新：
   - `GameServerHostPolicy`
   - `Program.cs`
   - `GameServerIntegrationTests`
   - `plannedEndpoints`
3. 未保留兼容 alias

#### 明确不做

- 不把它包装成 domain boundary 重构
- 不只改其中一个名字就停下

## 7. 推荐顺序

如果只是为了让路线更简洁、合理、可执行，推荐顺序其实是：

1. 默认停在当前状态，不再继续结构重构
2. 若还想继续补强，优先做 `Optional P1`
3. `Optional P2` 与 host wording cleanup 都已完成，不再列为后续顺位

## 8. 当前验收结论

按当前实际代码，可以认为下面这些目标已经达成：

- durable world 与 runtime state 已拆清
- `Routing/` 已成为 routing 主语义归属
- travel cost 已有单一权威入口
- `PassageView/*View` 已删除
- `Observation/` 已成为现行 seam 命名
- actor-facing canonical action surface 已明确
- route-plan owner 已写清
- host-only runtime wording 已统一
- canonical machine surface 的主线 parity guard 已补到当前需要的基线

当前未完成、但仍可能值得做的，只剩：

- optional contract hardening tail

## 9. 一句总纲

TextAdv2 现在已经从“结构收口阶段”进入“低优先级硬化与清理阶段”。

后续最合理的路线，不是再继续拆结构，而是默认停手；只有在某个小问题的收益足够明确时，再用低噪音小包单独处理。
