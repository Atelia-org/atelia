# TextAdv2 地点建模与移动逻辑实现草案

> 状态：实现草案
> 前置文档：`docs/TextAdv2/location-movement-refactor-proposal.md`
> 目标：把上一份重构提案翻译成可直接开工的类型/文件调整建议，并给出以 LLM Agent 会话为粒度的推进顺序

## 当前进度

截至当前实现，下面七个会话已经完成：

- 会话 1：引入 `Spatial` 派生层骨架
- 会话 2：让 navigation 与 routing graph 改为消费 spatial seam
- 会话 3：让 route planner、landmark heuristic、graph signature 统一改读 spatial seam
- 会话 4：让 `LocationObservationProjector` 与 exit-name 校验接入同一条空间派生路径
- 会话 5：新增 `ActorContextObservationProjector`，收口 actor context
- 会话 6：重命名 runtime trace，全链路显式暴露 runtime 边界
- 会话 7：补文档与 canonical surface 说明

其中会话 4 的实际落地选择已经明确，不再停留在“二选一讨论”阶段：

- `LocationObservationProjector` 已新增接受 `WorldSpatialSnapshot` 的重载
- `ActorLocationObservation` 投影也已支持复用同一份 `WorldSpatialSnapshot`
- exit-name 校验采用新增的 `Spatial/WorldSpatialValidation.cs`
- `WorldState.CreatePassage(...)` 与 `WorldState.ValidateIntegrity()` 均已切到同一条 spatial-derived 校验路径

其中会话 5 的实际落地选择也已经明确：

- 新增 `Observation/ActorContextObservationProjector.cs`
- `ActorContextObservation` 直接从 `WorldState + WorldSpatialSnapshot` 投影
- `SerialWorldRuntime.ObserveActorContext(...)` 已改为委托 `WorldHost.ObserveActorContext(...)`
- `WorldHost` 再委托 `ActorContextObservationProjector`
- `availableMoves` 的低层投影逻辑收口为 `NavigationObservationProjector.ProjectNavigationEdges(...)`
- actor context 不再通过 `ObserveActor + ObserveActorNavigation` 拼装

其中会话 6 的实际落地选择也已经明确：

- `Runtime/ActorRouteTrace.cs` 已删除，改为新增 `Runtime/ActorRuntimeRouteTrace.cs`
- `Runtime/MovementTraceRuntime.cs` 已删除，runtime 内部轻量历史改收口到 `Runtime/RuntimeRouteTraceState.cs`
- 新增 `ActorRuntimeRouteTraceObservation`、`ActorRuntimeRouteTraceStepObservation` 与 `ActorRuntimeRouteTraceObservationProjector`
- `SerialWorldRuntime.TraceActorRoute(...)` 已更名为 `TraceActorRuntimeRoute(...)`
- `RuntimeEpochId` 继续保持 internal strong type，但 public DTO 显式暴露 `string RuntimeEpochId`
- `DevTextRenderer` 已改为 `RenderRuntimeRouteTrace(...)`
- GameServer endpoint 已改为 `/actors/{actorId}/runtime-route-trace` 与 `/actors/{actorId}/runtime-route-trace/json`
- E2eCli flag 已改为 `--trace-actor-runtime-route` 与 `--trace-actor-runtime-route-json`
- cross-host parity 已调整为：两端都必须显式输出 `runtimeEpochId`，但 parity 比较忽略该顶层 runtime-boundary token

其中会话 7 的实际落地选择也已经明确：

- `docs/TextAdv2/canonical-machine-surface.md` 已把 `runtime-route-trace/json` 收编为 canonical machine seam
- 文档中已明确 `runtimeEpochId` 的定位：它是 runtime-boundary token，而不是 durable ID
- 文档中已明确 `ActorContextObservation` 的 canonical 语义：`availableMoves` 是唯一 actor-facing action surface，`currentLocation.exits` 不属于该 seam
- 文档中已明确三层边界：
  - durable `WorldState`
  - internal canonical spatial seam `WorldSpatialSnapshot`
  - runtime-owned seam（runtime route trace、route acceleration 等）
- `route-acceleration` 仍被明确排除在 canonical machine surface 之外

当前尚未开始的主项是：

- 会话 8：可选的 snapshot/cache 收口

本文回答的问题不是“要不要重构”，而是：

- 如果要做，这次重构建议具体落到哪些类型与文件
- 哪些旧类型应保留，哪些应重命名，哪些应删除
- 如果由多个 LLM Agent 会话逐步推进，怎样切分更稳

由于当前 `TextAdv2` 没有旧数据或旧 API 的兼容负担，本文默认采用**直接重命名、直接收口、避免兼容层**的策略。

---

## 1. 先给出最终希望得到的结构

目标结构建议是下面这样：

- `WorldTruth`
  - `Location`
  - `Passage`
  - `Actor`
  - `WorldState`
  - `ActorMoveReceipt`

- `Spatial` 或 `Derived`
  - `WorldSpatialSnapshot`
  - `LocationAdjacencySnapshot`
  - `LocationAdjacencyEdge`
  - `WorldSpatialSnapshotBuilder`
  - 可选：`WorldSpatialSnapshotCache`

- `Observation`
  - `LocationObservation`
  - `LocationNavigationObservation`
  - `ActorNavigationObservation`
  - `LocationRoutePlanObservation`
  - `LocationObservationProjector`
  - `NavigationObservationProjector`
  - `ActorContextObservationProjector`

- `Routing`
  - `LocationRoutePlanner`
  - `LocationRoutePlanningOptions`
  - `LocationNavigationGraphSignature`
  - `LocationLandmarkHeuristicSnapshot`
  - 不再持有“从 world 临时投影邻接边”的 projector

- `Runtime`
  - `SerialWorldRuntime`
  - `WorldHost`
  - `WorldRuntime`
  - `RouteAccelerationCache`
  - `ActorRuntimeRouteTrace`
  - `ActorRuntimeRouteTraceStep`
  - `ActorRuntimeRouteTraceObservation`
  - `ActorRuntimeRouteTraceProjector`
  - `RuntimeEpochId`

这个结构里，最大的变化只有两条：

1. 引入一个统一的 `Spatial/Derived` 派生层
2. 把 route trace 明确改名为 `RuntimeRouteTrace`

其他地方本质上都是沿着这两条主线整理。

---

## 2. 建议新增的具体类型与文件

下面是我认为现在就可以较清楚定下来的部分。

## 2.1 新增 `Spatial` 派生层

建议新增目录：

- `prototypes/TextAdv2/Spatial/`

建议新增文件：

- `prototypes/TextAdv2/Spatial/WorldSpatialSnapshot.cs`
- `prototypes/TextAdv2/Spatial/WorldSpatialSnapshotBuilder.cs`

如果你更喜欢 `Derived` 命名，也可以是：

- `prototypes/TextAdv2/Derived/WorldSpatialSnapshot.cs`
- `prototypes/TextAdv2/Derived/WorldSpatialSnapshotBuilder.cs`

我个人更偏向 `Spatial`，因为它比 `Derived` 更能说明“这层专门描述空间关系派生结果”。

### `WorldSpatialSnapshot.cs`

建议这个文件承载以下 internal 类型：

- `internal sealed record WorldSpatialSnapshot(...)`
- `internal sealed record LocationAdjacencySnapshot(...)`
- `internal sealed record LocationAdjacencyEdge(...)`

建议字段形状如下：

```csharp
internal sealed record WorldSpatialSnapshot(
    IReadOnlyDictionary<string, LocationAdjacencySnapshot> Locations
);

internal sealed record LocationAdjacencySnapshot(
    string LocationId,
    LocationAdjacencyEdge[] Edges
);

internal sealed record LocationAdjacencyEdge(
    string PassageId,
    string FromLocationId,
    string ToLocationId,
    string ExitName,
    TravelMode TravelMode,
    int BaseTravelCost,
    int TravelCostModifier,
    int TotalTravelCost,
    string SharedConditionNote,
    string DirectionalConditionNote,
    string LocalViewNote,
    bool IsEnabled
);
```

这里有几个设计要点：

- `ExitName` 直接放进 edge，避免上层再回 `Passage` 反查
- 同时保留 `BaseTravelCost` 与 `TravelCostModifier`，因为 rich observation 需要两者
- `TotalTravelCost` 也直接放，避免 planner 和 navigation 各算一遍
- `IsEnabled` 保留在边上，这样同一份 snapshot 既能服务 rich observation，也能服务 enabled-only navigation

### `WorldSpatialSnapshotBuilder.cs`

建议这个文件承载：

- `internal static class WorldSpatialSnapshotBuilder`

公开一个入口：

```csharp
public static WorldSpatialSnapshot Build(WorldState world)
```

职责应非常单一：

- 枚举所有 `Location`
- 枚举所有 `Passage`
- 为每个 location 构造它自己的有向 adjacency edges
- 保证输出排序稳定

它不应该做：

- route planning
- heuristic precompute
- actor context 拼装
- runtime 缓存

它只是 canonical spatial derived seam 的 builder。

## 2.2 可选新增 `WorldSpatialSnapshotCache`

如果在实现中发现“按次 Build”太重，可以再新增：

- `prototypes/TextAdv2/Spatial/WorldSpatialSnapshotCache.cs`

但我建议这是第二阶段再决定的事情。

当前更稳的顺序是：

- 先让所有读路径统一改为 consume `WorldSpatialSnapshot`
- 再观察是否需要 cache

否则容易把“收口派生来源”和“优化生命周期”搅在一起。

截至当前实现，这个 cache 仍未引入，建议继续保持为后置可选项。

---

## 3. 建议改造的现有类型与文件

## 3.1 `LocationNavigationGraphProjector` 建议删除

当前文件：

- `prototypes/TextAdv2/Routing/LocationNavigationGraphProjector.cs`

建议去向：

- 最终删除

原因：

- 它现在承担的是“从 world truth 临时投影最小图边”
- 这个职责会被 `WorldSpatialSnapshotBuilder` 完整吸收
- planner / heuristic / navigation observation 都不应再直接依赖它

如果在中间过渡期想降低 diff，可以短暂保留并让它改成：

- 从 `WorldSpatialSnapshot` 投影 lightweight graph

但从最终设计上看，它不是必须长期存在的类型。

## 3.2 `LocationNavigationGraph.cs` 建议收缩为 routing-only adapter

当前文件：

- `prototypes/TextAdv2/Routing/LocationNavigationGraph.cs`

建议保留，但职责改成非常明确的 routing adapter：

- `LocationNavigationGraph`
- `LocationNavigationGraphEdge`

它们不再从 `WorldState` 直接产生，而是从 `LocationAdjacencySnapshot` 过滤 `IsEnabled` 后得到。

换句话说：

- `WorldSpatialSnapshot` 是 canonical spatial seam
- `LocationNavigationGraph` 是 routing-only view

这样它还能保住 routing 层“只消费极小边”的优点，同时不再承担 primary projection 角色。

如果实现时觉得这层 adapter 也嫌多，可以进一步删除，让 planner 直接 consume enabled adjacency edges。

这点我认为目前还可以留一点设计余地：

- 如果你更看重 routing 层的最小语义面，就保留 adapter
- 如果你更看重层次简化，就直接让 planner 用 `LocationAdjacencyEdge`

## 3.3 `LocationObservationProjector` 改为消费 `WorldSpatialSnapshot`

当前文件：

- `prototypes/TextAdv2/Observation/LocationObservationProjector.cs`

建议改为：

- 保留文件名
- `ObserveLocation(WorldState world, string locationId)` 的内部实现先 `Build(world)` 或接收 snapshot
- 不再自行枚举 `PassagesTouching(locationId)`

建议新增一个更底层 overload：

```csharp
public static LocationObservation ObserveLocation(
    WorldState world,
    WorldSpatialSnapshot spatial,
    string locationId
)
```

然后把现有 world-only overload 作为 convenience wrapper。

好处是：

- `WorldHost` 仍然可以低成本调用旧签名
- 内部高频路径可以在同一个调用链上重用 snapshot

## 3.4 `NavigationObservationProjector` 改为消费 `WorldSpatialSnapshot`

当前文件：

- `prototypes/TextAdv2/Observation/NavigationObservationProjector.cs`

建议改为：

- 直接从 `LocationAdjacencySnapshot` 中取 `IsEnabled == true` 的 edges
- 不再经过 `LocationNavigationGraphProjector`
- 不再为了拿 `ExitName`/`TargetLocationName` 去多次回 world 反查 passage

这里建议的具体实现风格是：

- `WorldSpatialSnapshot` 已含 `ExitName`
- `TargetLocationName` 仍可从 world 读取 location name

也就是说，snapshot 存“边事实”，地点名仍然从 world 取，这样比较平衡。

## 3.5 `ActorContextObservationProjector` 已实现并成为 actor context 的唯一投影入口

当前文件：

- `prototypes/TextAdv2/Observation/ActorContextObservationProjector.cs`

当前已采用的结构：

- `internal static class ActorContextObservationProjector`
- `ObserveActorContext(WorldState world, WorldSpatialSnapshot spatial, string actorId)`
- `ObserveActorContext(WorldState world, string actorId)` convenience wrapper

当前实现遵循的边界是：

- 读取 actor
- 读取当前位置
- 读取当前地点 present actors
- 读取当前地点 adjacency edges
- 直接投影 `ActorContextObservation`
- 只保留 actor context 需要的窄 location 信息
- `availableMoves` 仍来自同一条 spatial seam
- 但不再通过 `ActorNavigationObservation` 包一层再拆出来

当前配套调整还包括：

- `WorldHost.ObserveActorContext(...)` 已新增
- `SerialWorldRuntime.ObserveActorContext(...)` 已变为直接委托
- `SerialWorldRuntime.ProjectActorContextLocation(...)` 已删除
- `NavigationObservationProjector` 新增内部 helper `ProjectNavigationEdges(...)`
  - 这样 actor context 与 navigation observation 共用的是“边投影逻辑”
  - 而不是“整个 ActorNavigationObservation DTO”

这样做的结果是：

- actor context 真正成为第一类 read model
- 它不再是已有 observation 的拼装副产物
- 但又没有把 navigation edge 的投影逻辑复制出第二份

## 3.6 `WorldState` 的 exit-name 校验已改为共用 spatial seam

当前文件：

- `prototypes/TextAdv2/WorldTruth/WorldState.cs`
- `prototypes/TextAdv2/Spatial/WorldSpatialValidation.cs`

当前已采用的实现：

- 新增 `WorldSpatialValidation.EnsureExitNameAvailable(WorldSpatialSnapshot, locationId, exitName)`
- 新增 `WorldSpatialValidation.EnsureUniqueExitNames(WorldSpatialSnapshot)`
- `WorldState.CreatePassage(...)` 先 `Build(this)`，再调用 `EnsureExitNameAvailable(...)`
- `WorldState.ValidateIntegrity()` 在完成 endpoint existence / exit-name legality 校验后，再调用 `EnsureUniqueExitNames(...)`

采用这个方案后的结论是：

- 不要让 `WorldTruth` 引用 `Observation`
- 当前允许 `WorldTruth` 使用 `Spatial`，因为 `Spatial` 已经被明确限定为 `WorldTruth` 的只读派生层
- “如何读取地点邻接关系做一致性校验”现在已经从 `WorldState` 手工扫描逻辑中抽离出来

这样做的收益是：

- `LocationObservation` 与 exit-name 校验真正共用了同一条派生数据来源
- 后续如果还有其他“基于地点邻接关系的 authoring / integrity 校验”，已有自然落点
- `WorldState` 保持“声明需要哪些校验”，而不再自己维护一套独立的邻接读取细节

当前仍需注意的点：

- `WorldSpatialValidation.EnsureUniqueExitNames(...)` 的报错锚点现在是“重复组中 canonical 排序下的首个 passage”
- 这已经与现有测试基线对齐，但后续如果引入更复杂的 authoring 语义，应继续把“报错锚点稳定性”视为 contract 的一部分

---

## 4. Route trace 的具体重命名建议

这部分我认为已经足够清晰，可以直接给出建议。

## 4.1 运行态 trace DTO 与 projector

当前文件：

- `prototypes/TextAdv2/Runtime/ActorRouteTrace.cs`
- `prototypes/TextAdv2/Runtime/MovementTraceRuntime.cs`

建议重命名为：

- `prototypes/TextAdv2/Runtime/ActorRuntimeRouteTrace.cs`
- `prototypes/TextAdv2/Runtime/RuntimeRouteTraceState.cs`

建议的类型映射如下：

- `ActorRouteTrace` -> `ActorRuntimeRouteTrace`
- `ActorRouteTraceStep` -> `ActorRuntimeRouteTraceStep`
- `RuntimeRouteTraceProjector` -> `RuntimeRouteTraceProjector` 或 `ActorRuntimeRouteTraceProjector`
- `ActorRouteTraceObservation` -> `ActorRuntimeRouteTraceObservation`
- `ActorRouteTraceStepObservation` -> `ActorRuntimeRouteTraceStepObservation`
- `ActorRouteTraceProjector` -> `ActorRuntimeRouteTraceObservationProjector`

这里我建议 projector 名称也更具体一点，避免两个“RuntimeRouteTraceProjector”混淆：

- public DTO projector：`ActorRuntimeRouteTraceProjector`
- observation projector：`ActorRuntimeRouteTraceObservationProjector`

## 4.2 public API 的重命名

当前：

- `SerialWorldRuntime.TraceActorRoute(string actorId)`

建议改为：

- `SerialWorldRuntime.TraceActorRuntimeRoute(string actorId)`

如果你觉得太长，可以是：

- `TraceActorSessionRoute`

两者里我更偏向 `RuntimeRoute`，因为当前系统里“session”有时更像 host 调用语义，而这里实际边界是 runtime 生命周期。

## 4.3 结果里建议新增字段

建议给 `ActorRuntimeRouteTrace` 增加：

- `string RuntimeEpochId`

建议形状：

```csharp
public sealed record ActorRuntimeRouteTrace(
    string RuntimeEpochId,
    string ActorId,
    string ActorName,
    string StartLocationId,
    string StartLocationName,
    string EndLocationId,
    string EndLocationName,
    int StepCount,
    int TotalTravelCost,
    ActorRuntimeRouteTraceStep[] Steps
);
```

这样 public DTO 就能把 runtime-lifetime 边界明确编码出来。

## 4.4 `RuntimeEpochId` 本身的建议

当前 `RuntimeEpochId` 是 internal，且只存在于 `WorldRuntime` 中。

建议做法：

- 保留 `RuntimeEpochId` 这个 internal 类型不动
- 在投影 public DTO 时只输出 `RuntimeEpochId.ToString()`

也就是：

- 不把 `RuntimeEpochId` 类型本身公开
- 只把它的字符串值作为 boundary token 暴露给外部

这比公开一个新的 public strong type 更轻。

## 4.5 dev-support 文本渲染器同步改名

当前文件：

- `prototypes/TextAdv2/DevSupport/DevTextRenderer.cs`

建议调整：

- `RenderRouteTrace(ActorRouteTrace trace)` -> `RenderRuntimeRouteTrace(ActorRuntimeRouteTrace trace)`
- 文本头可改成 `RUNTIME ROUTE TRACE`
- 输出里加上 `epoch=...`

这样 human-facing 输出也和类型边界一致。

---

## 5. `SerialWorldRuntime` 与 `WorldHost` 的具体整理建议

## 5.1 `SerialWorldRuntime`

当前文件：

- `prototypes/TextAdv2/Runtime/SerialWorldRuntime.cs`

建议的具体调整：

- `ObserveActorContext` 改为调用 `ActorContextObservationProjector`
- `TraceActorRoute` 改为 `TraceActorRuntimeRoute`
- `ObserveLocation` / `ObserveNavigation` / `PlanRoute` / `PlanActorRoute` 内部逐步改为共享同一次 spatial snapshot

这里建议新增一个 internal helper：

```csharp
private WorldSpatialSnapshot BuildSpatialSnapshot()
```

或者让 `WorldHost` 提供：

```csharp
internal WorldSpatialSnapshot BuildSpatialSnapshot()
```

我更倾向放在 `WorldHost`，因为它更贴近 durable world。

## 5.2 `WorldHost`

当前文件：

- `prototypes/TextAdv2/Runtime/WorldHost.cs`

建议新增 internal helper：

```csharp
internal WorldSpatialSnapshot BuildSpatialSnapshot()
```

注意：

- 它只是 helper，不是 stateful property
- 除非后续明确引入 cache，否则不要把 snapshot 挂成长期字段

然后逐步让下面这些路径从 shared snapshot 投影：

- `ObserveLocation`
- `ObserveNavigation`
- `ObserveActorNavigation`
- `PlanRoute`
- `PlanActorRoute`

对于 `MoveActor`，我不建议强行用 snapshot，因为它天然是“先 mutate，再 commit，再读当前状态”的路径。
它和只读投影路径不是一类问题。

---

## 6. Route acceleration 与 spatial seam 的关系

当前文件：

- `prototypes/TextAdv2/Runtime/RouteAcceleration.cs`

建议的设计关系是：

- `RouteAccelerationCache` 不负责构图
- 它只负责保存 heuristic snapshot 及其 graph signature
- graph signature 改为从 `WorldSpatialSnapshot` 派生，而不是从 `LocationNavigationGraphProjector` 派生

也就是说：

- `LocationNavigationGraphSignature.Build(WorldState world)`
  内部应先取得 `WorldSpatialSnapshot`

或者改签名为：

```csharp
public static string Build(WorldSpatialSnapshot spatial)
```

我更偏向第二种。
原因是它能让 signature 的输入边界更准，也更能提醒调用方“这是一个 derived-graph signature，不是 raw world signature”。

---

## 7. 我建议直接删除或合并的内容

以下类型/文件在最终态下建议不再存在，或者至少不再承担今天的角色：

- `prototypes/TextAdv2/Routing/LocationNavigationGraphProjector.cs`
  - 建议删除

- `SerialWorldRuntime.ProjectActorContextLocation(...)`
  - 建议删除
  - 这个逻辑应移动到 `ActorContextObservationProjector`

- `SerialWorldRuntime.ParseExplicitLandmarkLocationIds(...)`
  - 可以暂留
  - 但长期看更适合移到 route-acceleration 相关 helper，而不是留在 runtime façade 里

- `MovementTraceRuntime.cs` 这个文件名
  - 建议删除并改成更具体的 runtime-route-trace 命名

---

## 8. 文件级改造映射表

下面给一个更直接的映射表。

### 新增

- `prototypes/TextAdv2/Spatial/WorldSpatialSnapshot.cs`
- `prototypes/TextAdv2/Spatial/WorldSpatialSnapshotBuilder.cs`
- `prototypes/TextAdv2/Spatial/WorldSpatialValidation.cs`
- `prototypes/TextAdv2/Observation/ActorContextObservationProjector.cs`

### 重命名

- `prototypes/TextAdv2/Runtime/ActorRouteTrace.cs`
  -> `prototypes/TextAdv2/Runtime/ActorRuntimeRouteTrace.cs`

- `prototypes/TextAdv2/Runtime/MovementTraceRuntime.cs`
  -> `prototypes/TextAdv2/Runtime/RuntimeRouteTraceState.cs`

### 保留但重写内部实现

- `prototypes/TextAdv2/Observation/LocationObservationProjector.cs`
- `prototypes/TextAdv2/Observation/NavigationObservationProjector.cs`
- `prototypes/TextAdv2/Routing/LocationRoutePlanner.cs`
- `prototypes/TextAdv2/Routing/Heuristics/LocationLandmarkHeuristicSnapshot.cs`
- `prototypes/TextAdv2/Routing/LocationNavigationGraphSignature.cs`
- `prototypes/TextAdv2/Runtime/SerialWorldRuntime.cs`
- `prototypes/TextAdv2/Runtime/WorldHost.cs`
- `prototypes/TextAdv2/Runtime/WorldRuntime.cs`
- `prototypes/TextAdv2/DevSupport/DevTextRenderer.cs`

### 最终建议删除

- `prototypes/TextAdv2/Routing/LocationNavigationGraphProjector.cs`

### 可能需要补文档

- `docs/TextAdv2/canonical-machine-surface.md`
  - route trace 命名更新
  - actor context 生成路径更新
  - runtime-session seam 的说明同步

---

## 9. LLM Agent 会话粒度的重构步骤列表

下面这个列表按“每一轮会话最好能独立提交并验证”的粒度来写。
每一步都尽量控制成一个清晰的设计目标，而不是散装改几处文件。

## 会话 1：引入 `Spatial` 派生层骨架

目标：

- 新增 `WorldSpatialSnapshot`
- 新增 `WorldSpatialSnapshotBuilder`
- 能从 `WorldState` 构出稳定的 location adjacency 结果

涉及文件：

- 新增 `Spatial/*.cs`

完成标准：

- build 通过
- 不改 public contract
- 先没有任何调用方切换过去也没关系

这是最重要的起手式，因为它建立了后续所有重构共享的基底。

## 会话 2：让 navigation 与 routing graph 改为消费 spatial seam

目标：

- `NavigationObservationProjector` 改为从 `WorldSpatialSnapshot` 投影
- `LocationNavigationGraph` 若保留，则改为从 adjacency edges 适配
- `LocationNavigationGraphProjector` 进入废弃状态

涉及文件：

- `Observation/NavigationObservationProjector.cs`
- `Routing/LocationNavigationGraph.cs`
- `Routing/LocationNavigationGraphProjector.cs`

完成标准：

- `ObserveNavigation` / `ObserveActorNavigation` 行为不变
- build 通过
- `LocationNavigationGraphProjector` 若尚未删掉，也只剩非常薄的 adapter 角色

## 会话 3：让 route planner、landmark heuristic、graph signature 统一改读 spatial seam

目标：

- `LocationRoutePlanner`
- `LocationLandmarkHeuristicSnapshot`
- `LocationNavigationGraphSignature`

都从同一 spatial seam 获取边语义

涉及文件：

- `Routing/LocationRoutePlanner.cs`
- `Routing/Heuristics/LocationLandmarkHeuristicSnapshot.cs`
- `Routing/LocationNavigationGraphSignature.cs`

完成标准：

- route plan 输出语义不变
- build 通过
- `LocationNavigationGraphProjector` 可以被彻底删除，或只剩临时兼容壳

这一步做完后，三大主要只读热路径就统一了。

## 会话 4：让 `LocationObservationProjector` 与 exit-name 校验接入同一条空间派生路径

目标：

- `LocationObservationProjector` 改用 spatial seam
- 重新评估 `EnsureExitNameAvailable` 的落点

涉及文件：

- `Observation/LocationObservationProjector.cs`
- `WorldTruth/WorldState.cs`
- 可选新增 `Spatial/WorldSpatialValidation.cs`

完成标准：

- `ObserveLocation` 输出语义不变
- exit-name uniqueness 逻辑不再是完全独立的那套邻接访问路径
- build 通过

这是本轮里最可能出现“小设计分歧”的一步。
当前该会话已完成，且实际采用的是：

- `LocationObservationProjector` 新增 `ObserveLocation(world, spatial, locationId)` 与 `ObserveActorLocation(world, spatial, actorId)` 重载
- convenience wrapper 仍保留，但内部先 `Build(world)` 再走 shared-spatial overload
- `WorldState` 不再自己手写 exit-name 可用性扫描
- 新增 `WorldSpatialValidation` 作为 spatial-derived validation helper

因此后续实现可以把这里视为已稳定完成，不必再把“先只改 observation、把校验留到后面”作为保留选项。

## 会话 5：新增 `ActorContextObservationProjector`，收口 actor context

目标：

- 新增专用 projector
- `SerialWorldRuntime.ObserveActorContext` 不再自己拼装

涉及文件：

- 新增 `Observation/ActorContextObservationProjector.cs`
- `Runtime/SerialWorldRuntime.cs`
- 可选 `Runtime/WorldHost.cs`

完成标准：

- `ActorContextObservation` JSON shape 保持不变
- `availableMoves` 仍是唯一 canonical actor-facing action surface
- `SerialWorldRuntime` 删除 `ProjectActorContextLocation(...)`

这一步完成后，actor-facing read model 就从“拼出来的 DTO”升级成真正的一等 contract 了。

当前该会话已完成，且实际采用的是：

- 新增 `Observation/ActorContextObservationProjector.cs`
- `WorldHost` 新增 `ObserveActorContext(...)`
- `SerialWorldRuntime.ObserveActorContext(...)` 只做 façade 转发
- actor context 不再调用 `ObserveActor(...)` 与 `ObserveActorNavigation(...)`
- `availableMoves` 通过 `NavigationObservationProjector.ProjectNavigationEdges(...)` 共用低层边投影逻辑

因此后续可以把 actor context 视为已经完成收口，不必再把“先继续拼 DTO，等 route trace 后再说”作为保留选项。

## 会话 6：重命名 runtime trace，全链路显式暴露 runtime 边界

当前该会话已完成，且实际采用的是：

- public DTO 命名固定为 `ActorRuntimeRouteTrace` / `ActorRuntimeRouteTraceStep`
- runtime 内部 observation 命名固定为 `ActorRuntimeRouteTraceObservation` / `ActorRuntimeRouteTraceStepObservation`
- runtime façade API 固定为 `TraceActorRuntimeRoute(...)`
- `RuntimeEpochId` 不上浮为 public 类型，只在 DTO 上投影成 `string RuntimeEpochId`
- 文本 surface 固定为 `RUNTIME ROUTE TRACE epoch=<id> ...`
- host / CLI machine surface 固定使用 `runtime-route-trace` / `trace-actor-runtime-route`
- 语义上明确：这是 runtime-owned ephemeral trace，不是 durable world history

这一轮之后，runtime 边界已经不再只是注释里的隐含知识，而是成为：

- 类型名的一部分
- JSON payload 的字段
- HTTP route 的路径片段
- CLI operation 的显式名称
- dev text header 的显式前缀

因此后续文档与测试都应以这个新边界为准，不再把 trace 当作“可跨 reopen 持续”的历史接口来描述。

## 会话 7：补文档与 canonical surface 说明

当前该会话已完成，且实际采用的是：

- `canonical-machine-surface.md` 现在明确把 `runtime route trace json` 列为 canonical seam
- 但该 seam 的 parity 规则被特别注明为“忽略顶层 `runtimeEpochId` 的具体值”
- actor context 的文档已明确：
  - `ActorContextObservation` 不是 `LocationObservation` 的删减版
  - `availableMoves` 是唯一 canonical actor-facing action surface
  - `currentLocation.exits` 不是该 contract 的一部分
- 文档额外补出了 internal `WorldSpatialSnapshot` 的定位，防止把它误读成 public contract

因此这一步之后，文档叙事与代码命名已经一致，不再存在“surface 名字像 durable history，但注释里才悄悄说它不是”的漂移。

## 会话 8：可选的 snapshot/cache 收口

当前已经有足够清晰的信息做出一个**阶段性判断**：

- 现在还**不建议实现** `WorldSpatialSnapshotCache`
- 但已经足够清楚地知道，将来如果要做，应该优先做什么、不该做什么

当前判断依据：

- `WorldSpatialSnapshotBuilder.Build(world)` 虽然已经成为统一派生入口，但大多数 public operation 目前都是“单次调用只 build 一次”
- `LocationObservationProjector`、`NavigationObservationProjector`、`ActorContextObservationProjector` 都已经提供接受 `WorldSpatialSnapshot` 的重载，说明“同次调用内共享 snapshot”的低成本路径已经存在
- 目前真正持续持有 runtime state 的地方主要是 `WorldRuntime` 与 `RouteAccelerationCache`
- 其中 `RouteAccelerationCache` 已经缓存了更高层的 planning artifact；若再引入 spatial cache，就会立刻遇到新的 invalidation 责任边界
- 当前代码里还没有独立的 world revision/version token 可供 spatial cache 直接挂靠；若现在贸然加 cache，极容易把“缓存命中判断”和“runtime 状态语义”搅在一起

因此更稳的结论是：

- 现阶段优先保持 `WorldSpatialSnapshot` 为无状态 derived seam
- 先通过“同次调用内显式传递 snapshot”吸收已知重复构建
- 暂不引入跨调用、跨 operation 的 `WorldSpatialSnapshotCache`

如果未来真的出现足够压力，建议顺序应是：

1. 先测量 `Build(world)` 的热点来源，而不是先假定 cache 值得做
2. 若热点主要来自“单次 request 内多次读同一 world”，优先在 `WorldHost` 或更窄的 orchestration 层共享一次 snapshot，而不是做 runtime 级全局 cache
3. 只有当热点明确来自“跨多次 operation 对同一 immutable topology 反复 build”，再考虑 `WorldSpatialSnapshotCache`
4. 若走到这一步，cache key 也应优先挂在明确的 topology/version token 上，而不是临时拼接更多隐式约束

换句话说，会话 8 现在已经足够清楚到可以作出“**暂缓实现，保留明确准入条件**”的决策，但还不够清楚到值得直接写出稳定的 cache 类型。

---

## 10. 哪些部分我认为已经足够清楚，哪些还可以边做边看

### 已经足够清楚，可以直接开工的

- 引入 `WorldSpatialSnapshot`
- 让 navigation / planner / heuristic / graph signature 统一走 spatial seam
- 让 `LocationObservationProjector` 与 exit-name 校验统一走 spatial seam
- 让 actor context 成为独立 projector 驱动的一等 read model
- route trace 更名为 runtime route trace
- public trace DTO 加入 `RuntimeEpochId`

### 仍可边做边看的

- `LocationNavigationGraph` 最终是否保留为 routing-only adapter
- 是否需要 `WorldSpatialSnapshotCache`

我的建议是：

- 第一批先别在这些点上追求“最终完美答案”
- 先把 canonical seam 立住
- cache 目前保持“不做，但把准入条件写清楚”
- 再根据真实热点和代码手感决定 adapter 与 cache 的去留

---

## 11. 一句话开工建议

如果接下来马上要启动下一轮实现，我建议的第一句任务说明就是：

**在当前已经完成前七个会话的基础上，继续保持 `WorldSpatialSnapshot` 为无状态 derived seam；没有实测热点前，不要急着引入 runtime 级 spatial cache。**

原因是：

- spatial seam 是底座
- actor context 已经完成收口
- runtime trace 的命名与边界已经落地
- canonical machine surface 文档也已与现状对齐
- 现阶段更值得保持的是“清楚地不做什么”，而不是为了局部优化过早引入 cache 状态机

当前最稳的顺序已经从“冻结 contract 语义并清理文档叙事”切换成了“在无状态 spatial seam 上继续观察真实热点，再决定是否需要 cache”。
