# TextAdv2 Runtime Boundary And Routing Simplification Plan

> 状态：首轮收口已完成，本文改为“基于当前实际代码”的后续精修计划
> 适用范围：`prototypes/TextAdv2/`、`prototypes/TextAdv2.GameServer/`、`prototypes/TextAdv2.E2eCli/`
> 前提：当前没有旧数据、没有已投入使用的稳定 API、没有兼容性包袱

## 0. 文档目的

这份文档不再描述最初那一轮三段式重构的“理想状态”。

截至当前代码，原计划里最关键的三件事已经基本落地：

1. runtime boundary freeze 已完成
2. routing ownership consolidation 已完成
3. machine-facing surface 的第一轮 convergence 已完成

因此，后续计划应该从“大方向提案”收缩成“剩余设计债务与下一拍可执行工作包”。

本文的目标是：

- 先如实记录当前实际状态
- 明确哪些问题已经解决，不要反复重开
- 把剩余重构压缩成更小、更稳、更容易验收的工作包

## 1. 当前实际状态

### 1.1 已完成的收口

当前代码已经具备下面这些结构性事实：

- `WorldSession` 已移除，公共运行入口是 `Runtime/SerialWorldRuntime.cs`
- durable world 与 runtime state 已拆开：
  - `Runtime/WorldHost.cs`
  - `Runtime/WorldRuntime.cs`
  - `Runtime/SerialWorldRuntime.cs`
- `RuntimeEpochId` 已存在于 runtime 层，但目前只作为 internal seam，不进入 machine-facing DTO
- routing 已迁出 `Observation/`，当前主实现位于 `Routing/`
- `AccelerationIndex/` 已被吸收到 `Routing/Heuristics/`
- directed travel cost 的权威入口已收口到 `WorldTruth/Passage.cs` 的 `GetTotalTravelCostFrom(locationId)`
- `PassageView/*View` 已删除，projector / dump / authoring 现在直接消费 `Passage`
- `ActorContextObservation` 已完成第一轮收口：
  - `AvailableMoves` 是 canonical actor-facing action surface
  - `CurrentLocation` 已收窄为不含 `Exits` 的 context payload
- `docs/TextAdv2/canonical-machine-surface.md` 与 cross-host parity tests 已同步到上述 actor-context contract

### 1.2 当前代码的实际分层

按今天的代码看，TextAdv2 已经大致形成下面四层：

- `WorldTruth`
  - durable authoritative truth
  - actor / location / passage / logical time 的最小业务规则
- `Runtime`
  - 进程内 movement trace
  - route acceleration cache
  - `SerialWorldRuntime` façade
- `Routing`
  - navigation graph
  - planner
  - heuristic
  - graph signature
- `Observation`
  - observation DTO 与 projector
  - 以及 route-plan contract 与相邻 helper 的残余 ownership 议题

### 1.3 当前最重要的判断

当前主线已经不需要再做“大拆分”。

后续真正值得继续做的，不是重开前面三包，而是把剩下那几处“命名与职责不完全对齐”的尾巴收干净：

- `Observation` 内仍有少量历史表述与职责描述需要继续收口
- route-plan contract 与相邻 wire/text helper 的 owner 需要补足现状说明
- 宿主侧还残留少量 `session` 命名，但这已经是 host wording debt，不再是核心域边界问题

## 2. 已解决的问题，不再重开

除非后续发现明确 bug 或新需求直接冲突，下面这些设计结论视为已定：

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

### 2.4 `PassageView`

- `PassageView/*View` 已被证明没有必要
- 后续不再恢复任何等价的“伪 view”包装层

## 3. 当前仍存在的设计债务

### 3.1 `Observation` 已成为现行命名，但少量描述仍落后于职责

今天的 `Observation/` 已是现行 observation layer 命名，但少量文档描述还是历史遗留。

更具体地说：

- `LocationObservation*` 与 `NavigationObservation*` 现在确实是 observation
- 但 `LocationRoutePlanObservation` 及其辅助类型仍留在这里
- `Routing/LocationRoutePlanner.cs` 当前直接依赖该 observation contract

这不算结构性错误，但如果缺少 owner 说明，仍会制造“planner contract 究竟归谁”的理解噪音。

### 3.2 route-plan owner 需要补足现状说明

当前 route-plan 相关代码已经拆成三块，结构本身就是现阶段的预期落点；剩下的问题主要是 owner 表述还不够明确：

- machine-facing observation contract
  - `Observation/LocationRoutePlan.cs`
  - `LocationRoutePlanObservation`
  - `LocationRoutePlanStepObservation`
  - `LocationRoutePlanSearchStatsObservation`
  - `RoutePlanStatus`
- planner
  - `Routing/LocationRoutePlanner.cs`
  - 当前有意直接产出上述 observation contract，不再引入中间 routing result / projector
- wire helper
  - `DevSupport/RoutePlanStatusWireToken.cs`
- JSON codec
  - `DevSupport/TextAdv2Json.cs`
- dev-only text helper
  - `DevSupport/LocationRoutePlanTextRenderer.cs`

这不是继续做结构拆分的信号；当前包更适合做的是把这些 owner 用注释、文档和测试措辞写清楚，避免继续制造“planner contract 究竟归谁”的阅读噪音。

### 3.3 host wording 仍有历史残留

`TextAdv2.GameServer` 里仍存在：

- `SessionService`
- `/admin/session-status`

但它们现在表达的已经不是旧式 `WorldSession` 领域对象，而是 host 自己的 runtime/open-status 概念。

这类命名残留会造成理解摩擦，但它已经不再是高优先级设计风险。

### 3.4 少量注释与文档仍指向旧结构

例如：

- `WorldTruth/WorldState.cs` 的顶部注释仍提到过时层名与旧 acceleration 表述
- 一些计划文档与归档文档还会提到已删除的 `PassageView`

这些问题不会影响运行，但会拖慢后续阅读与 review。

## 4. 修订后的设计原则

接下来的重构不再追求“把整个世界再重命名一遍”，而是遵守下面几条：

### 4.1 不重开已经完成的大包

只要当前边界已经清楚、测试也守住了，就不为了“更纯粹”再回头重做：

- 不再重开 runtime boundary freeze
- 不再重开 routing ownership consolidation
- 不再重开 `PassageView` 删除
- 不再重开 actor-context canonical action surface

### 4.2 优先拆职责，再做机械改名

如果某个目录名已经不理想，优先先把职责拆干净。

例如：

- 先把 route-plan contract / planner / codec / text helper 的 owner 写清楚
- 命名已收口到 `Observation`，后续只继续处理残余历史表述

不要反过来先做大面积重命名，再在新名字下面继续保留旧混合职责。

### 4.3 不把“命名整齐”误当成“边界更好”

`SessionService` 等名字确实不完美，但它们的风险等级不同：

- route-plan owner 若缺少明确说明，仍会影响设计理解，值得继续收
- `SessionService` 更像宿主内部措辞问题，可低优先级处理

后续计划要按真实收益排序，而不是按“看起来不顺眼”的程度排序。

### 4.4 保持单世界串行模型优先

TextAdv2 当前仍是：

- 单世界
- 单进程内串行逻辑模型
- 面向小世界模拟的 Agent RL Gym

因此后续设计仍应优先：

- 边界清楚
- DTO 稳定
- 测试易守
- 宿主行为可检视

而不是为未来多世界/并发/分布式预埋复杂抽象。

## 5. 修订后的后续实施计划

后续建议不再按原来的“三期大包”推进，而是改成下面三个更小、更实际的工作包。

## 5.1 Package A：route-plan owner clarity / wording cleanup

这是当前最值得优先做的下一拍。

### 目标

- 把 route-plan contract / planner / codec / text helper 的当前 owner 写清楚
- 去掉计划文档与活跃测试中的残余历史表述
- 保持现有 JSON contract 不变

### 建议做法

1. 保留 route-plan DTO / enum 作为 machine-facing observation contract
2. 在 `LocationRoutePlanner` 注释中明确：当前有意直接产出该 contract，不再引入中间 routing result
3. 在 `RoutePlanStatusWireToken` / `TextAdv2Json` 注释中明确 wire token / JSON codec 的 owner
4. 保持 `LocationRoutePlanTextRenderer` 留在 `DevSupport/`，并标明其 dev-only text helper 身份
5. 同步修正文档和活跃测试中的历史措辞

### 明确不做

- 不改 route-plan JSON shape
- 不改 `RoutePlanStatus` 的语义
- 不移动 DTO / enum
- 不引入新的 internal planner result 或 projector
- 不额外扩大为 host wording cleanup 或 contract shape 变更

### 影响文件

- `prototypes/TextAdv2/Observation/LocationRoutePlan.cs`
- `prototypes/TextAdv2/DevSupport/TextAdv2Json.cs`
- `prototypes/TextAdv2/Routing/LocationRoutePlanner.cs`
- 相关测试与 text rendering tests

### 完成定义

- route-plan contract、planner、codec、text helper 的当前 owner 有清晰说明
- 不再暗示还要继续做一轮更大的 route-plan 结构拆分
- 现有 route-plan tests / host parity tests 保持通过

## 5.2 Package B：observation seam naming cleanup

这是 Package A 之后的自然下一步，但不建议先做。

### 目标

- 保持 `Observation` 作为现行 observation seam 命名
- 同步修正文档、注释和测试描述

### 建议做法

1. 保持 `Observation/` 作为现行目录名
2. 保持对应 namespace 为 `Atelia.TextAdv2.Observation`
3. 同步 `Runtime`、`Routing`、tests 的 observation using
4. 顺手修正 `WorldState.cs` 等注释中的旧层名

### 为什么放在 Package A 之后

因为 Package A 先把 route-plan owner 与历史措辞收干净，后续命名类尾修才不会继续搬运旧叙述噪音。

### 完成定义

- 运行时代码中不再出现 `ReadOnlyView` 作为现行层名
- `WorldState` 等核心注释不再提到 `AccelerationIndex`
- JSON contract 保持不变

## 5.3 Package C：canonical observe contract hardening

> 状态：`C1` 已完成；host wording cleanup 暂缓

Package C 经再次审视后，已经收缩成一个更小的测试硬化包。

当前真正值得立即做的，只有 canonical observe seams 的结构型 guard；宿主层 `session` 措辞虽然不完美，但目前仍属于低收益、较高噪音的 host-only debt。

### 已完成的 `C1`

- 为 `observe actor` 增加显式 JSON shape guard
- 为 `observe location` 增加显式 JSON shape guard
- 保持改动只落在 parity suite，不修改 runtime、DTO、endpoint 或 host status contract

### 暂缓的 host wording cleanup

下面这些动作目前统一 parked，不作为当前主线：

1. 将 `TextAdv2.GameServer/SessionService.cs` 重命名为 `RuntimeService`
2. 把 `/admin/session-status` 改成更贴近 host/runtime 的路径
3. 顺手改 `session` / `sessionOpenMode` 等 payload 字段措辞

### 为什么暂缓

- 它们不属于 canonical machine surface 的核心边界风险
- 当前 `GameServer` 集成测试已经广泛锁住这些 host-only 措辞
- 单独改其中一部分，只会制造新的半收口状态

### 明确不做

- 不把 host wording cleanup 和 domain boundary 重新混为一谈
- 不为了“去掉 session 这个词”而修改核心 runtime 结构
- 不把 host wording debt 和 canonical observe contract hardening 绑成同一包

## 6. 推荐顺序

如果只开一个短周期，建议顺序如下：

1. 先做 Package A：route-plan owner clarity / wording cleanup
2. 继续做 Package B 后续尾修：observation seam naming cleanup
3. 再做 Package C1：canonical observe JSON shape guards
4. host wording cleanup 只有在确有收益时，再作为独立低优先级包整体启动

这个顺序比旧版计划更简单，原因是：

- 最大的结构性工作已经做完了
- 现在最该避免的是“大范围机械改名先行”
- 先把 route-plan owner 与文案写清，后续 rename 才不会继续搬运旧问题

## 7. 当前验收结论

按当前实际代码，可以认为下面这些目标已经达成：

- durable world 与 runtime state 已拆清
- `Routing/` 已成为 routing 主语义归属
- travel cost 已有单一权威入口
- `PassageView/*View` 已删除
- actor-facing canonical action surface 已明确
- canonical machine surface 文档与 parity tests 已同步到 actor context 新 contract
- `observe actor` / `observe location` 已补上显式 JSON shape guard

当前未完成、但值得继续收口的，主要只剩：

- route-plan owner 与残余历史表述清理
- `Observation` 内 route-plan contract 的残余历史表述
- 少量 host wording / 注释文档清理

## 8. 一句总纲

TextAdv2 现在已经跨过“先把边界拆清”的阶段。

后续最合理的路线，不是再做一轮大重构，而是把剩下几处 owner 与措辞不完全对齐的尾巴，用小包、低噪音、可验证的方式逐个收干净。
